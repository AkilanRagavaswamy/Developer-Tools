using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace DevTools.Http.Capture;

/// <summary>
/// A loopback HTTP proxy that records everything that passes through it.
/// </summary>
/// <remarks>
/// Plain HTTP arrives as a request with an absolute URI and is simply forwarded. <c>CONNECT</c>
/// is the interesting case: with a trusted certificate the tunnel is terminated here, the
/// request inside it is read, and a second TLS connection is made to the real server — which is
/// the only way to see an https body. Without a certificate the bytes are relayed untouched and
/// the exchange is recorded as a tunnel, with host, size and timing but no content. A client
/// that pins its certificate refuses the handshake; that is recorded too, rather than being
/// left to look like a failure.
///
/// Forwarding goes through <see cref="HttpClient"/> rather than re-framing HTTP by hand. It
/// re-encodes the request, which a strict recording proxy would not do, but it gets chunked
/// bodies, compression and connection reuse right — and getting those wrong shows up as a
/// capture that quietly corrupts the traffic it was meant to observe.
/// </remarks>
public sealed class CaptureProxy : IDisposable
{
    private static readonly string[] HopByHopHeaders =
    [
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Proxy-Connection",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Host",
        "Content-Length",
    ];

    private readonly CaptureCertificates? _certificates;
    private readonly HttpClient _client;
    private readonly HttpClientHandler _handler;
    private readonly CancellationTokenSource _stopping = new();

    private TcpListener? _listener;
    private int _counter;
    private bool _disposed;

    public CaptureProxy(CaptureCertificates? certificates)
    {
        _certificates = certificates;

        _handler = new HttpClientHandler
        {
            // The proxy is the thing being configured; it must not route through itself.
            UseProxy = false,

            // A redirect is part of what the captured app sees, so it is passed back rather
            // than followed here.
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            UseCookies = false,
        };

        _client = new HttpClient(_handler) { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>Raised on a thread-pool thread once an exchange is complete.</summary>
    public event EventHandler<CapturedExchange>? Captured;

    public int Port { get; private set; }

    public bool CanDecryptTls => _certificates is not null;

    /// <summary>Binds the listener and starts accepting. Returns the port actually bound.</summary>
    public int Start(int preferredPort)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _listener = new TcpListener(IPAddress.Loopback, preferredPort);

        try
        {
            _listener.Start();
        }
        catch (SocketException)
        {
            // The remembered port is taken by something else; let Windows choose one and carry
            // on rather than refusing to start.
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(() => AcceptLoopAsync(_stopping.Token));

        return Port;
    }

    public void Stop()
    {
        if (!_stopping.IsCancellationRequested)
        {
            _stopping.Cancel();
        }

        try
        {
            _listener?.Stop();
        }
        catch (Exception)
        {
            // Stopping a listener that is already down is not a problem.
        }

        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is { } listener)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (Exception)
            {
                // Cancelled, or the listener went away. Either way the loop is done.
                return;
            }

            // One misbehaving connection must not take the capture down with it.
            _ = Task.Run(() => HandleClientSafelyAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            await HandleClientAsync(client, token);
        }
        catch (Exception)
        {
            // A client that hangs up mid-request is ordinary, not an error worth reporting.
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        client.NoDelay = true;

        // The owning process has to be read now, while the connection is open: the TCP table is
        // a snapshot, and by the time the exchange finishes the row may be gone.
        var owner = client.Client.RemoteEndPoint is IPEndPoint remote ? ProcessResolver.OwnerOf(remote) : 0;
        var ownerName = ProcessResolver.NameOf(owner);

        await using var stream = client.GetStream();

        var request = await HttpWire.ReadRequestAsync(stream, token);

        if (request is null)
        {
            return;
        }

        if (string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            await HandleConnectAsync(stream, request, owner, ownerName, token);
            return;
        }

        var body = await HttpWire.ReadBodyAsync(stream, request.Headers, token);
        await ForwardAsync(stream, request, body, secure: false, host: null, owner, ownerName, token);
    }

    // ---------------------------------------------------------------- https

    private async Task HandleConnectAsync(
        Stream clientStream,
        WireRequest request,
        int owner,
        string ownerName,
        CancellationToken token)
    {
        var (host, port) = SplitAuthority(request.Target);
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();

        await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n", token);

        if (_certificates is null)
        {
            var bytes = await RelayAsync(clientStream, host, port, token);
            Publish(Tunnel(request, host, port, started, clock.Elapsed, bytes, CaptureOutcome.Tunnelled, owner, ownerName));
            return;
        }

        var tls = new SslStream(clientStream, leaveInnerStreamOpen: false);

        try
        {
            await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificates.ForHost(host),
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
                token);
        }
        catch (Exception)
        {
            // The client checked our certificate and said no. That is a pinned client, and
            // saying so is more useful than letting it look like a network failure.
            await tls.DisposeAsync();
            Publish(Tunnel(request, host, port, started, clock.Elapsed, 0, CaptureOutcome.Pinned, owner, ownerName));
            return;
        }

        await using (tls)
        {
            // A client may send several requests down one tunnel before closing it.
            while (!token.IsCancellationRequested)
            {
                var inner = await HttpWire.ReadRequestAsync(tls, token);

                if (inner is null)
                {
                    break;
                }

                var body = await HttpWire.ReadBodyAsync(tls, inner.Headers, token);

                if (!await ForwardAsync(tls, inner, body, secure: true, host, owner, ownerName, token))
                {
                    break;
                }
            }
        }
    }

    /// <summary>Copies bytes both ways without looking at them, and counts them.</summary>
    private async Task<long> RelayAsync(Stream clientStream, string host, int port, CancellationToken token)
    {
        using var server = new TcpClient();
        await server.ConnectAsync(host, port, token);
        await using var serverStream = server.GetStream();

        var counters = new long[2];

        var up = CopyAsync(clientStream, serverStream, 0);
        var down = CopyAsync(serverStream, clientStream, 1);

        await Task.WhenAny(up, down);

        return counters[0] + counters[1];

        async Task CopyAsync(Stream from, Stream to, int slot)
        {
            var buffer = new byte[16 * 1024];

            try
            {
                while (true)
                {
                    var read = await from.ReadAsync(buffer, token);

                    if (read == 0)
                    {
                        return;
                    }

                    counters[slot] += read;
                    await to.WriteAsync(buffer.AsMemory(0, read), token);
                    await to.FlushAsync(token);
                }
            }
            catch (Exception)
            {
                // Either end closing is how a tunnel normally finishes.
            }
        }
    }

    // ---------------------------------------------------------------- forwarding

    /// <summary>
    /// Sends the request on to the real server and writes the response back to the client.
    /// Returns false when the connection should not be reused.
    /// </summary>
    private async Task<bool> ForwardAsync(
        Stream clientStream,
        WireRequest request,
        byte[] body,
        bool secure,
        string? host,
        int owner,
        string ownerName,
        CancellationToken token)
    {
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();

        var url = BuildUrl(request, secure, host);

        if (url is null)
        {
            await WriteAsciiAsync(clientStream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token);
            return false;
        }

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), url);

        if (body.Length > 0)
        {
            message.Content = new ByteArrayContent(body);
        }

        foreach (var header in request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!message.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                message.Content?.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        try
        {
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseContentRead, token);

            var responseBody = await response.Content.ReadAsByteArrayAsync(token);
            clock.Stop();

            var responseHeaders = new List<CapturedHeader>();

            foreach (var header in response.Headers)
            {
                foreach (var value in header.Value)
                {
                    responseHeaders.Add(new CapturedHeader(header.Key, value));
                }
            }

            foreach (var header in response.Content.Headers)
            {
                foreach (var value in header.Value)
                {
                    responseHeaders.Add(new CapturedHeader(header.Key, value));
                }
            }

            await WriteResponseAsync(clientStream, response, responseBody, responseHeaders, token);

            Publish(new CapturedExchange
            {
                Index = Interlocked.Increment(ref _counter),
                StartedAt = started,
                Method = request.Method,
                Url = url.AbsoluteUri,
                Host = url.Host,
                PathAndQuery = url.PathAndQuery,
                HttpVersion = request.Version.Replace("HTTP/", string.Empty, StringComparison.OrdinalIgnoreCase),
                IsSecure = secure,
                RequestHeaders = request.Headers,
                RequestBody = body,
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase ?? string.Empty,
                ResponseHeaders = responseHeaders,
                ResponseBody = responseBody,
                ResponseMediaType = response.Content.Headers.ContentType?.MediaType,
                Duration = clock.Elapsed,
                Outcome = CaptureOutcome.Complete,
                ProcessId = owner,
                ProcessName = ownerName,
            });
        }
        catch (Exception ex)
        {
            clock.Stop();

            await WriteAsciiAsync(
                clientStream,
                "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                token);

            Publish(new CapturedExchange
            {
                Index = Interlocked.Increment(ref _counter),
                StartedAt = started,
                Method = request.Method,
                Url = url.AbsoluteUri,
                Host = url.Host,
                PathAndQuery = url.PathAndQuery,
                IsSecure = secure,
                RequestHeaders = request.Headers,
                RequestBody = body,
                Duration = clock.Elapsed,
                Outcome = CaptureOutcome.Failed,
                Error = ex.Message,
                ProcessId = owner,
                ProcessName = ownerName,
            });

            return false;
        }

        return true;
    }

    /// <summary>
    /// Writes the response back with an explicit length and no keep-alive.
    /// </summary>
    /// <remarks>
    /// The body has already been read in full, so the length is known exactly and chunked
    /// framing would only be a way to get it wrong. Closing after each response costs a
    /// connection per request and removes a whole class of framing bug from a tool whose job is
    /// to report faithfully.
    /// </remarks>
    private static async Task WriteResponseAsync(
        Stream clientStream,
        HttpResponseMessage response,
        byte[] body,
        IReadOnlyList<CapturedHeader> headers,
        CancellationToken token)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ")
            .Append((int)response.StatusCode)
            .Append(' ')
            .Append(response.ReasonPhrase ?? string.Empty)
            .Append("\r\n");

        foreach (var header in headers)
        {
            if (HopByHopHeaders.Contains(header.Name, StringComparer.OrdinalIgnoreCase) ||
                string.Equals(header.Name, "Content-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                // Content-Encoding goes because the body was decompressed on the way through;
                // leaving the header on would describe a body that no longer exists.
                continue;
            }

            head.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }

        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");

        await WriteAsciiAsync(clientStream, head.ToString(), token);

        if (body.Length > 0)
        {
            await clientStream.WriteAsync(body, token);
        }

        await clientStream.FlushAsync(token);
    }

    // ---------------------------------------------------------------- helpers

    private static Uri? BuildUrl(WireRequest request, bool secure, string? host)
    {
        // A proxied plain request carries the whole URL; a request inside a tunnel carries only
        // the path, and the host comes from the CONNECT that opened it.
        if (Uri.TryCreate(request.Target, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        var authority = host ?? request.Header("Host");

        if (string.IsNullOrWhiteSpace(authority))
        {
            return null;
        }

        var scheme = secure ? "https" : "http";

        return Uri.TryCreate($"{scheme}://{authority}{request.Target}", UriKind.Absolute, out var built)
            ? built
            : null;
    }

    private static (string Host, int Port) SplitAuthority(string authority)
    {
        var colon = authority.LastIndexOf(':');

        return colon > 0 && int.TryParse(authority[(colon + 1)..], out var port)
            ? (authority[..colon], port)
            : (authority, 443);
    }

    private static CapturedExchange Tunnel(
        WireRequest request,
        string host,
        int port,
        DateTimeOffset started,
        TimeSpan duration,
        long bytes,
        CaptureOutcome outcome,
        int owner,
        string ownerName) =>
        new()
        {
            Index = 0,
            StartedAt = started,
            Method = "CONNECT",
            Url = $"https://{host}:{port}",
            Host = host,
            IsSecure = true,
            RequestHeaders = request.Headers,
            TunnelBytes = bytes,
            Duration = duration,
            Outcome = outcome,
            ProcessId = owner,
            ProcessName = ownerName,
        };

    private void Publish(CapturedExchange exchange)
    {
        var numbered = exchange.Index == 0
            ? exchange with { Index = Interlocked.Increment(ref _counter) }
            : exchange;

        Captured?.Invoke(this, numbered);
    }

    private static async Task WriteAsciiAsync(Stream stream, string text, CancellationToken token)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), token);
        await stream.FlushAsync(token);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Stop();
        _stopping.Dispose();
        _client.Dispose();
        _handler.Dispose();
    }
}
