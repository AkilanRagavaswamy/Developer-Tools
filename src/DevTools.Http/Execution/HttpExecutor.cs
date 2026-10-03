using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using DevTools.Core;
using DevTools.Http.Model;

namespace DevTools.Http.Execution;

/// <summary>Whether a run reuses connections or establishes a fresh one each time (FR-A03).</summary>
public enum ConnectionMode
{
    /// <summary>Reuse pooled connections — what a real client does, and the default.</summary>
    Pooled,

    /// <summary>A new connection per request, so every handshake is measured.</summary>
    Fresh,
}

/// <summary>
/// The only type in DevTools that opens a socket (NFR-05).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HttpClient"/> will not hand you a DNS/TCP/TLS breakdown, so the handler does the
/// first two steps by hand in <see cref="SocketsHttpHandler.ConnectCallback"/> and brackets the
/// handshake with <see cref="SocketsHttpHandler.PlaintextStreamFilter"/>, which the runtime
/// invokes once TLS has been negotiated. Everything after that is stopwatches around the send,
/// the first byte and the body read.
/// </para>
/// <para>
/// The attribution is exact under <see cref="ConnectionMode.Fresh"/>, because that mode gives
/// each request its own handler. Under <see cref="ConnectionMode.Pooled"/> a request that
/// reuses a connection reports zero for the handshake segments and sets
/// <see cref="RequestTiming.EstablishedConnection"/> to false — which is the honest answer, and
/// better than inventing a number.
/// </para>
/// </remarks>
public sealed class HttpExecutor : IDisposable
{
    private static readonly AsyncLocal<ConnectionTiming?> Current = new();

    private readonly ICredentialStore _credentials;
    private readonly Dictionary<string, HttpClient> _pool = new(StringComparer.Ordinal);
    private bool _disposed;

    public HttpExecutor(ICredentialStore? credentials = null)
    {
        _credentials = credentials ?? new InMemoryCredentialStore();
    }

    /// <summary>Sends one request and reads the whole response.</summary>
    public async Task<OperationResult<ResponseRecord>> SendAsync(
        RequestDefinition request,
        ConnectionMode mode = ConnectionMode.Pooled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var built = await RequestFactory.BuildAsync(request, _credentials, cancellationToken).ConfigureAwait(false);
        if (!built.IsSuccess)
        {
            return OperationResult<ResponseRecord>.Fail(built.Error!);
        }

        using var message = built.Value!;

        var timing = new ConnectionTiming();
        Current.Value = timing;

        HttpClient client;
        HttpClient? disposable = null;

        if (mode == ConnectionMode.Fresh)
        {
            client = disposable = CreateClient(request.Options);
        }
        else
        {
            client = GetPooled(request.Options);
        }

        var startedAt = DateTimeOffset.Now;
        var total = Stopwatch.StartNew();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.Options.TimeoutSeconds, 1, 3600)));

            using var response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            var ttfb = total.Elapsed;

            var download = Stopwatch.StartNew();
            var (body, truncated) = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
            download.Stop();
            total.Stop();

            return OperationResult<ResponseRecord>.Ok(new ResponseRecord(
                (int)response.StatusCode,
                response.ReasonPhrase ?? string.Empty,
                response.Version.ToString(),
                ReadHeaders(response),
                ReadCookies(response),
                body,
                response.Content.Headers.ContentType?.MediaType,
                response.Content.Headers.ContentType?.CharSet,
                response.Content.Headers.ContentLength ?? body.Length,
                new RequestTiming(
                    timing.Dns,
                    timing.Connect,
                    timing.Tls,
                    ttfb,
                    download.Elapsed,
                    total.Elapsed,
                    timing.Established),
                startedAt,
                message.RequestUri?.ToString() ?? request.Url,
                request.Method,
                truncated));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OperationResult<ResponseRecord>.Fail("The request was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return OperationResult<ResponseRecord>.Fail(
                $"The request timed out after {request.Options.TimeoutSeconds} s.");
        }
        catch (HttpRequestException ex)
        {
            return OperationResult<ResponseRecord>.Fail(TransportDiagnostics.Describe(ex, request.Url));
        }
        catch (IOException ex)
        {
            return OperationResult<ResponseRecord>.Fail($"The connection failed while transferring data: {ex.Message}");
        }
        finally
        {
            Current.Value = null;
            disposable?.Dispose();
        }
    }

    private async Task<(byte[] Body, bool Truncated)> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        var truncated = false;

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > Limits.MaxResponseBytes)
            {
                // Better a capped body with a flag than an out-of-memory crash on a stream
                // that turns out to be a 40 GB file.
                buffer.Write(chunk, 0, (int)Math.Max(0, Limits.MaxResponseBytes - buffer.Length));
                truncated = true;
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), truncated);
    }

    private static List<KeyValueItem> ReadHeaders(HttpResponseMessage response)
    {
        var headers = new List<KeyValueItem>();

        foreach (var header in response.Headers)
        {
            headers.Add(new KeyValueItem(header.Key, string.Join(", ", header.Value)));
        }

        foreach (var header in response.Content.Headers)
        {
            headers.Add(new KeyValueItem(header.Key, string.Join(", ", header.Value)));
        }

        return headers;
    }

    private static List<CookieItem> ReadCookies(HttpResponseMessage response)
    {
        var cookies = new List<CookieItem>();

        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return cookies;
        }

        foreach (var value in values)
        {
            var parts = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                continue;
            }

            var equals = parts[0].IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            string? domain = null;
            string? path = null;

            foreach (var attribute in parts.Skip(1))
            {
                if (attribute.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase))
                {
                    domain = attribute[7..];
                }
                else if (attribute.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                {
                    path = attribute[5..];
                }
            }

            cookies.Add(new CookieItem(parts[0][..equals], parts[0][(equals + 1)..], domain, path));
        }

        return cookies;
    }

    // ---- clients ------------------------------------------------------------------------

    private HttpClient GetPooled(RequestOptions options)
    {
        var key = PoolKey(options);

        lock (_pool)
        {
            if (!_pool.TryGetValue(key, out var client))
            {
                _pool[key] = client = CreateClient(options);
            }

            return client;
        }
    }

    private static string PoolKey(RequestOptions options) =>
        $"{options.FollowRedirects}|{options.MaxRedirects}|{options.IgnoreCertificateErrors}|{options.ProxyUrl}|{options.HttpVersion}";

    private static HttpClient CreateClient(RequestOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = options.FollowRedirects,
            MaxAutomaticRedirections = Math.Max(1, options.MaxRedirects),
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            ConnectCallback = ConnectAsync,
            PlaintextStreamFilter = FilterAsync,
        };

        if (options.IgnoreCertificateErrors)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            };
        }

        if (!string.IsNullOrWhiteSpace(options.ProxyUrl) &&
            Uri.TryCreate(options.ProxyUrl, UriKind.Absolute, out var proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            // The per-request linked token owns the deadline; the client-level one would
            // report a generic TaskCanceledException that cannot be told from a user cancel.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>Resolves and connects by hand, which is the only way to time the two separately.</summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var timing = Current.Value;
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        var dns = Stopwatch.StartNew();
        IPAddress[] addresses;

        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }

        dns.Stop();

        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        var connect = Stopwatch.StartNew();

        try
        {
            await socket.ConnectAsync(addresses, port, cancellationToken).ConfigureAwait(false);
            connect.Stop();
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        if (timing is not null)
        {
            timing.Dns = dns.Elapsed;
            timing.Connect = connect.Elapsed;
            timing.Established = true;

            // The handshake clock only starts for https. Over plain http the filter still runs,
            // and timing the few microseconds between connect and filter would report a TLS
            // handshake on a connection that never had one.
            timing.HandshakeStarted =
                string.Equals(context.InitialRequestMessage.RequestUri?.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? Stopwatch.GetTimestamp()
                    : 0;
        }

        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>
    /// Invoked once the plaintext stream is available — after TLS has been negotiated — so the
    /// interval since the connect completed is the handshake.
    /// </summary>
    private static ValueTask<Stream> FilterAsync(SocketsHttpPlaintextStreamFilterContext context, CancellationToken cancellationToken)
    {
        var timing = Current.Value;

        if (timing is { HandshakeStarted: > 0 })
        {
            timing.Tls = Stopwatch.GetElapsedTime(timing.HandshakeStarted);
            timing.HandshakeStarted = 0;
        }

        _ = cancellationToken;
        return ValueTask.FromResult(context.PlaintextStream);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_pool)
        {
            foreach (var client in _pool.Values)
            {
                client.Dispose();
            }

            _pool.Clear();
        }
    }

    private sealed class ConnectionTiming
    {
        public TimeSpan Dns;
        public TimeSpan Connect;
        public TimeSpan Tls;
        public bool Established;
        public long HandshakeStarted;
    }
}
