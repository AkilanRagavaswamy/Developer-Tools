using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DevTools.Http.Tests;

/// <summary>What the server saw.</summary>
public sealed record CapturedRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);

    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>What the server should send back.</summary>
public sealed record CannedResponse(
    int StatusCode = 200,
    string ReasonPhrase = "OK",
    string Body = "",
    string ContentType = "text/plain; charset=utf-8",
    IReadOnlyDictionary<string, string>? Headers = null,
    TimeSpan Delay = default)
{
    public static CannedResponse Json(string json, int statusCode = 200) =>
        new(statusCode, "OK", json, "application/json; charset=utf-8");
}

/// <summary>
/// A minimal HTTP/1.1 server on the loopback interface, for tests only.
/// </summary>
/// <remarks>
/// Built on <see cref="TcpListener"/> rather than <see cref="HttpListener"/> on purpose: it
/// needs no URL reservation and no elevation, so the suite runs identically on a developer's
/// machine and on a build agent. It also gives delay control — injecting a known latency is the
/// only way to assert that measured timings mean anything.
/// </remarks>
public sealed class LoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private readonly List<CapturedRequest> _requests = [];
    private readonly Func<CapturedRequest, CannedResponse> _handler;

    private LoopbackServer(Func<CapturedRequest, CannedResponse> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public string Url(string path) => $"{BaseUrl}/{path.TrimStart('/')}";

    /// <summary>Every request the server has handled, in arrival order.</summary>
    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public CapturedRequest LastRequest
    {
        get
        {
            lock (_requests)
            {
                return _requests[^1];
            }
        }
    }

    public static LoopbackServer Start(Func<CapturedRequest, CannedResponse> handler) => new(handler);

    public static LoopbackServer Start(CannedResponse response) => new(_ => response);

    public static LoopbackServer Echo() => new(static request => CannedResponse.Json(
        $$"""{"method":"{{request.Method}}","path":"{{request.Path}}","body":{{Quote(request.BodyText)}}}"""));

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal)
                    .Replace("\r", "\\r", StringComparison.Ordinal) + "\"";

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();

                // One connection may carry several requests; keep-alive is what makes the
                // pooled-versus-fresh distinction observable at all.
                while (!_shutdown.IsCancellationRequested)
                {
                    var request = await ReadRequestAsync(stream).ConfigureAwait(false);

                    if (request is null)
                    {
                        return;
                    }

                    lock (_requests)
                    {
                        _requests.Add(request);
                    }

                    var response = _handler(request);

                    if (response.Delay > TimeSpan.Zero)
                    {
                        await Task.Delay(response.Delay, _shutdown.Token).ConfigureAwait(false);
                    }

                    await WriteResponseAsync(stream, response).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // A client that hangs up mid-exchange is normal here, especially for the
                // cancellation tests.
            }
        }
    }

    private static async Task<CapturedRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var header = new MemoryStream();
        var one = new byte[1];
        var matched = 0;

        // Read byte by byte up to the blank line: simple, and correct for a test server.
        while (matched < 4)
        {
            var read = await stream.ReadAsync(one).ConfigureAwait(false);

            if (read == 0)
            {
                return null;
            }

            header.WriteByte(one[0]);

            matched = one[0] switch
            {
                (byte)'\r' when matched is 0 or 2 => matched + 1,
                (byte)'\n' when matched is 1 or 3 => matched + 1,
                _ => 0,
            };
        }

        var text = Encoding.ASCII.GetString(header.ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0)
        {
            return null;
        }

        var requestLine = lines[0].Split(' ');

        if (requestLine.Length < 2)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);

            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var body = Array.Empty<byte>();

        if (headers.TryGetValue("Content-Length", out var lengthText) &&
            int.TryParse(lengthText, out var length) && length > 0)
        {
            var buffer = new byte[length];
            var offset = 0;

            while (offset < length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset)).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            body = buffer;
        }

        return new CapturedRequest(requestLine[0], requestLine[1], headers, body);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, CannedResponse response)
    {
        var body = Encoding.UTF8.GetBytes(response.Body);
        var builder = new StringBuilder();

        builder.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(response.ReasonPhrase).Append("\r\n");
        builder.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        builder.Append("Content-Length: ").Append(body.Length).Append("\r\n");

        if (response.Headers is not null)
        {
            foreach (var header in response.Headers)
            {
                builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
        }

        builder.Append("Connection: keep-alive\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString())).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _shutdown.Dispose();
    }
}
