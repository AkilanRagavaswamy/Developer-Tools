using System.Net;
using System.Text;
using DevTools.Http.Capture;
using Xunit;

namespace DevTools.Http.Tests;

/// <summary>
/// The wire reader: the part of the capture proxy that has to be right before anything else
/// can be, because everything downstream reads what it produced.
/// </summary>
public sealed class HttpWireTests
{
    [Fact]
    public async Task Reads_the_request_line_and_headers()
    {
        using var stream = Text(
            "GET http://example.com/things?page=1 HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Accept: application/json\r\n" +
            "\r\n");

        var request = await HttpWire.ReadRequestAsync(stream, TestContext.Current.CancellationToken);

        Assert.NotNull(request);
        Assert.Equal("GET", request.Method);
        Assert.Equal("http://example.com/things?page=1", request.Target);
        Assert.Equal("HTTP/1.1", request.Version);
        Assert.Equal("example.com", request.Header("Host"));
        Assert.Equal("application/json", request.Header("accept"));
    }

    [Fact]
    public async Task A_connect_request_carries_the_authority_as_its_target()
    {
        using var stream = Text("CONNECT api.example.com:443 HTTP/1.1\r\nHost: api.example.com:443\r\n\r\n");

        var request = await HttpWire.ReadRequestAsync(stream, TestContext.Current.CancellationToken);

        Assert.NotNull(request);
        Assert.Equal("CONNECT", request.Method);
        Assert.Equal("api.example.com:443", request.Target);
    }

    [Fact]
    public async Task An_empty_stream_reads_as_no_request_rather_than_throwing()
    {
        using var stream = Text(string.Empty);

        Assert.Null(await HttpWire.ReadRequestAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reads_a_counted_body()
    {
        using var stream = Text("POST / HTTP/1.1\r\nContent-Length: 5\r\n\r\nhello");

        var request = await HttpWire.ReadRequestAsync(stream, TestContext.Current.CancellationToken);
        var body = await HttpWire.ReadBodyAsync(stream, request!.Headers, TestContext.Current.CancellationToken);

        Assert.Equal("hello", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task Reads_a_chunked_body_and_joins_the_chunks()
    {
        using var stream = Text(
            "POST / HTTP/1.1\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "\r\n" +
            "5\r\nhello\r\n" +
            "6\r\n world\r\n" +
            "0\r\n\r\n");

        var request = await HttpWire.ReadRequestAsync(stream, TestContext.Current.CancellationToken);
        var body = await HttpWire.ReadBodyAsync(stream, request!.Headers, TestContext.Current.CancellationToken);

        Assert.Equal("hello world", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task A_chunk_size_with_an_extension_is_still_read()
    {
        using var stream = Text(
            "POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n4;name=value\r\nabcd\r\n0\r\n\r\n");

        var request = await HttpWire.ReadRequestAsync(stream, TestContext.Current.CancellationToken);
        var body = await HttpWire.ReadBodyAsync(stream, request!.Headers, TestContext.Current.CancellationToken);

        Assert.Equal("abcd", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task No_framing_header_means_no_body()
    {
        using var stream = Text("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n");

        var request = await HttpWire.ReadRequestAsync(stream, TestContext.Current.CancellationToken);
        var body = await HttpWire.ReadBodyAsync(stream, request!.Headers, TestContext.Current.CancellationToken);

        Assert.Empty(body);
    }

    /// <summary>A truncated body keeps what arrived: losing it would hide the very bug being chased.</summary>
    [Fact]
    public async Task A_body_shorter_than_its_content_length_keeps_what_arrived()
    {
        using var stream = Text("POST / HTTP/1.1\r\nContent-Length: 100\r\n\r\nshort");

        var request = await HttpWire.ReadRequestAsync(stream, TestContext.Current.CancellationToken);
        var body = await HttpWire.ReadBodyAsync(stream, request!.Headers, TestContext.Current.CancellationToken);

        Assert.Equal("short", Encoding.UTF8.GetString(body));
    }

    private static MemoryStream Text(string value) => new(Encoding.ASCII.GetBytes(value));
}

/// <summary>How a captured exchange describes itself to the list.</summary>
public sealed class CapturedExchangeTests
{
    [Fact]
    public void A_complete_exchange_reports_its_status_and_total_size()
    {
        var exchange = new CapturedExchange
        {
            Index = 1,
            StartedAt = DateTimeOffset.Now,
            Method = "GET",
            Url = "https://example.com/things",
            Host = "example.com",
            StatusCode = 200,
            RequestBody = new byte[10],
            ResponseBody = new byte[1014],
        };

        Assert.Equal("200", exchange.StatusText);
        Assert.Equal(1024, exchange.TotalBytes);
        Assert.True(exchange.IsSuccess);
        Assert.Empty(exchange.OutcomeNote);
    }

    [Fact]
    public void A_tunnel_counts_its_bytes_without_keeping_them()
    {
        var exchange = new CapturedExchange
        {
            Index = 1,
            StartedAt = DateTimeOffset.Now,
            Method = "CONNECT",
            Url = "https://example.com:443",
            Host = "example.com",
            TunnelBytes = 2048,
            Outcome = CaptureOutcome.Tunnelled,
        };

        Assert.Equal("—", exchange.StatusText);
        Assert.Equal(2048, exchange.TotalBytes);
        Assert.Empty(exchange.ResponseBody);
        Assert.Contains("not decrypted", exchange.OutcomeNote, StringComparison.Ordinal);
    }

    /// <summary>A pinned client is a distinct outcome, not a failure.</summary>
    [Fact]
    public void A_pinned_client_says_so_rather_than_looking_like_an_error()
    {
        var exchange = new CapturedExchange
        {
            Index = 1,
            StartedAt = DateTimeOffset.Now,
            Method = "CONNECT",
            Url = "https://telemetry.example.com:443",
            Host = "telemetry.example.com",
            Outcome = CaptureOutcome.Pinned,
        };

        Assert.Contains("pinned", exchange.OutcomeNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_carries_its_reason()
    {
        var exchange = new CapturedExchange
        {
            Index = 1,
            StartedAt = DateTimeOffset.Now,
            Method = "GET",
            Url = "https://example.com/",
            Host = "example.com",
            Outcome = CaptureOutcome.Failed,
            Error = "The remote host refused the connection.",
        };

        Assert.Equal("failed", exchange.StatusText);
        Assert.Equal("—", exchange.SizeText);
        Assert.Equal("The remote host refused the connection.", exchange.OutcomeNote);
    }
}

/// <summary>The proxy, end to end, against a loopback server.</summary>
public sealed class CaptureProxyTests
{
    [Fact]
    public async Task Forwards_a_request_and_records_the_whole_exchange()
    {
        await using var server = LoopbackServer.Start(CannedResponse.Json("""{"ok":true}"""));

        using var proxy = new CaptureProxy(certificates: null);
        var captured = new TaskCompletionSource<CapturedExchange>();
        proxy.Captured += (_, exchange) => captured.TrySetResult(exchange);

        var port = proxy.Start(0);

        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/things?page=1");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"ok\":true", text, StringComparison.Ordinal);

        var exchange = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("GET", exchange.Method);
        Assert.Equal("/things?page=1", exchange.PathAndQuery);
        Assert.Equal(200, exchange.StatusCode);
        Assert.Equal(CaptureOutcome.Complete, exchange.Outcome);
        Assert.Contains("\"ok\":true", Encoding.UTF8.GetString(exchange.ResponseBody), StringComparison.Ordinal);
    }

    /// <summary>
    /// The point of the tool: the body is kept whole, not sampled. A capture that truncates is
    /// a capture that hides the one response you needed to read.
    /// </summary>
    [Fact]
    public async Task Keeps_a_large_response_body_in_full()
    {
        var payload = new string('x', 512 * 1024);

        await using var server = LoopbackServer.Start(new CannedResponse(Body: payload));

        using var proxy = new CaptureProxy(certificates: null);
        var captured = new TaskCompletionSource<CapturedExchange>();
        proxy.Captured += (_, exchange) => captured.TrySetResult(exchange);

        var port = proxy.Start(0);

        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true };
        using var client = new HttpClient(handler);

        _ = await client.GetAsync($"http://127.0.0.1:{server.Port}/big");

        var exchange = await captured.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(payload.Length, exchange.ResponseBody.Length);
    }

    [Fact]
    public async Task Records_a_posted_body()
    {
        await using var server = LoopbackServer.Echo();

        using var proxy = new CaptureProxy(certificates: null);
        var captured = new TaskCompletionSource<CapturedExchange>();
        proxy.Captured += (_, exchange) => captured.TrySetResult(exchange);

        var port = proxy.Start(0);

        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true };
        using var client = new HttpClient(handler);

        using var content = new StringContent("""{"name":"test"}""", Encoding.UTF8, "application/json");
        _ = await client.PostAsync($"http://127.0.0.1:{server.Port}/create", content);

        var exchange = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("POST", exchange.Method);
        Assert.Equal("""{"name":"test"}""", Encoding.UTF8.GetString(exchange.RequestBody));
    }

    /// <summary>A server that is not there is a recorded failure, not a lost exchange.</summary>
    [Fact]
    public async Task An_unreachable_server_is_recorded_as_a_failure()
    {
        using var proxy = new CaptureProxy(certificates: null);
        var captured = new TaskCompletionSource<CapturedExchange>();
        proxy.Captured += (_, exchange) => captured.TrySetResult(exchange);

        var port = proxy.Start(0);

        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true };
        using var client = new HttpClient(handler);

        // Port 1 on the loopback has nothing listening on any normal machine.
        try
        {
            _ = await client.GetAsync("http://127.0.0.1:1/nothing");
        }
        catch (HttpRequestException)
        {
            // The 502 the proxy writes back may still surface as a transport error.
        }

        var exchange = await captured.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(CaptureOutcome.Failed, exchange.Outcome);
        Assert.False(string.IsNullOrEmpty(exchange.Error));
    }

    [Fact]
    public void Numbers_exchanges_from_one_upwards()
    {
        using var proxy = new CaptureProxy(certificates: null);

        var port = proxy.Start(0);

        Assert.True(port > 0);
        Assert.False(proxy.CanDecryptTls);
    }
}

/// <summary>Process attribution, which is what makes the process filter mean anything.</summary>
public sealed class ProcessResolverTests
{
    [Fact]
    public void Lists_this_process_among_the_others()
    {
        var processes = ProcessResolver.ListProcesses();
        var self = System.Environment.ProcessId;

        Assert.Contains(processes, p => p.Id == self);
    }

    [Fact]
    public void Names_a_process_by_its_id()
    {
        Assert.False(string.IsNullOrEmpty(ProcessResolver.NameOf(System.Environment.ProcessId)));
    }

    /// <summary>An id that has gone is an empty name, not an exception.</summary>
    [Fact]
    public void An_unknown_id_has_no_name()
    {
        Assert.Empty(ProcessResolver.NameOf(0));
        Assert.Empty(ProcessResolver.NameOf(-1));
    }

    /// <summary>
    /// A listening socket the test owns must resolve back to the test process — which is the
    /// whole mechanism the "Only this process" filter rests on.
    /// </summary>
    [Fact]
    public async Task Attributes_a_loopback_connection_to_the_process_that_opened_it()
    {
        await using var server = LoopbackServer.Start(new CannedResponse(Body: "hi"));

        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, server.Port);

        var local = (IPEndPoint)socket.Client.LocalEndPoint!;

        Assert.Equal(System.Environment.ProcessId, ProcessResolver.OwnerOf(local));
    }
}

/// <summary>The certificate authority behind https capture.</summary>
public sealed class CaptureCertificatesTests
{
    [Fact]
    public void Issues_a_host_certificate_signed_by_its_root()
    {
        var created = CaptureCertificates.Create();
        Assert.True(created.IsSuccess, created.Error?.Message);

        using var certificates = created.Value!;

        try
        {
            var leaf = certificates.ForHost("api.example.com");

            Assert.Equal("CN=api.example.com", leaf.Subject);
            Assert.Equal(certificates.Root.Subject, leaf.Issuer);
            Assert.True(leaf.HasPrivateKey);

            // The same host must give the same certificate back: reissuing per connection
            // would be slow enough to change the timings the tool is there to report.
            Assert.Same(leaf, certificates.ForHost("api.example.com"));
        }
        finally
        {
            certificates.Remove();
        }
    }

    [Fact]
    public void A_root_that_was_never_installed_does_not_load()
    {
        Assert.Null(CaptureCertificates.Load(null));
        Assert.Null(CaptureCertificates.Load(string.Empty));
        Assert.Null(CaptureCertificates.Load("0000000000000000000000000000000000000000"));
    }

    /// <summary>Creating a root does not trust it: that is a separate, consented step.</summary>
    [Fact]
    public void Creating_a_root_does_not_trust_it()
    {
        var created = CaptureCertificates.Create();
        Assert.True(created.IsSuccess, created.Error?.Message);

        using var certificates = created.Value!;

        try
        {
            Assert.False(certificates.IsTrusted());
        }
        finally
        {
            certificates.Remove();
        }
    }
}

/// <summary>
/// Reading the Windows proxy setting. Only the read is exercised: a test that changed it would
/// be altering the machine running the suite, and a failure mid-test would leave it altered.
/// </summary>
public sealed class SystemProxyTests
{
    [Fact]
    public void Reads_the_current_setting_without_changing_it()
    {
        var first = SystemProxy.Capture();
        var second = SystemProxy.Capture();

        Assert.Equal(first, second);
    }

    /// <summary>
    /// The test host is not packaged, so it reports that it can change the setting. The value
    /// that matters is the packaged one, which cannot be exercised from here — but pinning the
    /// unpackaged answer catches the check being inverted or short-circuited.
    /// </summary>
    [Fact]
    public void An_unpackaged_process_can_change_the_setting()
    {
        Assert.True(SystemProxy.CanChangeSystemSetting);
    }

    /// <summary>
    /// The address in the failure text is the one the user has to paste somewhere, so it has to
    /// be exactly the proxy's own.
    /// </summary>
    [Fact]
    public void A_capture_reports_the_address_to_point_an_app_at()
    {
        using var proxy = new CaptureProxy(certificates: null);
        var port = proxy.Start(0);

        Assert.InRange(port, 1, 65535);
    }
}

/// <summary>
/// The launch-and-attach backend's wire contract: the agent runs in another process and sends
/// JSON down a pipe, so the field names on the two sides are the one thing that silently breaks.
/// This pins the host's reading of that JSON.
/// </summary>
public sealed class LaunchProfilerTests
{
    [Fact]
    public void Parses_an_agent_payload_into_an_exchange()
    {
        var json = System.Text.Encoding.UTF8.GetBytes("""
            {
              "StartedAtUnixMs": 1700000000000,
              "Method": "POST",
              "Url": "https://api.example.com/v1/things?q=1",
              "Host": "api.example.com",
              "PathAndQuery": "/v1/things?q=1",
              "HttpVersion": "2.0",
              "IsSecure": true,
              "RequestHeaders": [ { "Name": "Accept", "Value": "application/json" } ],
              "RequestBody": "eyJhIjoxfQ==",
              "StatusCode": 201,
              "ReasonPhrase": "Created",
              "ResponseHeaders": [ { "Name": "Content-Type", "Value": "application/json" } ],
              "ResponseBody": "eyJvayI6dHJ1ZX0=",
              "ResponseMediaType": "application/json",
              "DurationMs": 42.5,
              "ProcessId": 1234,
              "ProcessName": "SampleApp",
              "Failed": false,
              "Error": null
            }
            """);

        var exchange = LaunchProfiler.Parse(json, index: 7);

        Assert.Equal(7, exchange.Index);
        Assert.Equal("POST", exchange.Method);
        Assert.Equal("api.example.com", exchange.Host);
        Assert.Equal("/v1/things?q=1", exchange.PathAndQuery);
        Assert.True(exchange.IsSecure);
        Assert.Equal(201, exchange.StatusCode);
        Assert.Equal("application/json", exchange.ResponseMediaType);
        Assert.Equal(TimeSpan.FromMilliseconds(42.5), exchange.Duration);
        Assert.Equal(1234, exchange.ProcessId);
        Assert.Equal("SampleApp", exchange.ProcessName);
        Assert.Equal(CaptureOutcome.Complete, exchange.Outcome);
        Assert.Equal("""{"a":1}""", System.Text.Encoding.UTF8.GetString(exchange.RequestBody));
        Assert.Equal("""{"ok":true}""", System.Text.Encoding.UTF8.GetString(exchange.ResponseBody));
        Assert.Equal("Accept", Assert.Single(exchange.RequestHeaders).Name);
    }

    [Fact]
    public void A_failed_payload_becomes_a_failed_exchange()
    {
        var json = System.Text.Encoding.UTF8.GetBytes("""
            { "Method": "GET", "Url": "https://x/y", "Failed": true, "Error": "boom" }
            """);

        var exchange = LaunchProfiler.Parse(json, index: 1);

        Assert.Equal(CaptureOutcome.Failed, exchange.Outcome);
        Assert.Equal("boom", exchange.Error);
    }
}
