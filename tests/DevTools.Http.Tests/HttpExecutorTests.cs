using System.Text;
using DevTools.Http.Execution;
using DevTools.Http.Model;
using Xunit;

namespace DevTools.Http.Tests;

public sealed class HttpExecutorTests
{
    [Fact]
    public async Task A_get_request_round_trips_status_headers_and_body()
    {
        await using var server = LoopbackServer.Start(new CannedResponse(
            200, "OK", """{"ok":true}""", "application/json; charset=utf-8",
            new Dictionary<string, string> { ["X-Custom"] = "here" }));

        using var executor = new HttpExecutor();

        var result = await executor.SendAsync(RequestDefinition.Get(server.Url("/things")));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        var response = result.Value!;

        Assert.Equal(200, response.StatusCode);
        Assert.True(response.IsSuccess);
        Assert.Equal("""{"ok":true}""", response.TryGetText());
        Assert.Equal("application/json", response.MediaType);
        Assert.Contains(response.Headers, h => h.Name == "X-Custom" && h.Value == "here");
        Assert.Equal("/things", server.LastRequest.Path);
    }

    [Fact]
    public async Task Every_verb_reaches_the_server()
    {
        await using var server = LoopbackServer.Start(new CannedResponse());
        using var executor = new HttpExecutor();

        foreach (var method in new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" })
        {
            var result = await executor.SendAsync(new RequestDefinition { Method = method, Url = server.Url("/") });

            Assert.True(result.IsSuccess, $"{method}: {result.ErrorMessage}");
            Assert.Equal(method, server.LastRequest.Method);
        }
    }

    [Fact]
    public async Task A_json_body_arrives_intact_with_its_content_type()
    {
        await using var server = LoopbackServer.Start(new CannedResponse());
        using var executor = new HttpExecutor();

        const string payload = """{"name":"Ada","tags":["x","y"]}""";

        var result = await executor.SendAsync(new RequestDefinition
        {
            Method = "POST",
            Url = server.Url("/submit"),
            Body = BodySpec.FromJson(payload),
        });

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(payload, server.LastRequest.BodyText);
        Assert.StartsWith("application/json", server.LastRequest.Header("Content-Type")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Headers_and_auth_reach_the_server()
    {
        await using var server = LoopbackServer.Start(new CannedResponse());

        var store = new InMemoryCredentialStore();
        await store.SetAsync("tok", "abc");

        using var executor = new HttpExecutor(store);

        var result = await executor.SendAsync(RequestDefinition.Get(server.Url("/")) with
        {
            Headers = [new KeyValueItem("X-Trace", "t-1")],
            Auth = new AuthSpec { Kind = AuthKind.Bearer, SecretRef = "tok" },
        });

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("t-1", server.LastRequest.Header("X-Trace"));
        Assert.Equal("Bearer abc", server.LastRequest.Header("Authorization"));
    }

    [Fact]
    public async Task Status_codes_other_than_200_are_returned_not_thrown()
    {
        await using var server = LoopbackServer.Start(new CannedResponse(404, "Not Found", "nope"));
        using var executor = new HttpExecutor();

        var result = await executor.SendAsync(RequestDefinition.Get(server.Url("/missing")));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(404, result.Value!.StatusCode);
        Assert.False(result.Value.IsSuccess);
        Assert.Equal("404 Not Found", result.Value.StatusLine);
    }

    [Fact]
    public async Task Set_cookie_headers_are_parsed()
    {
        await using var server = LoopbackServer.Start(new CannedResponse(
            Headers: new Dictionary<string, string> { ["Set-Cookie"] = "session=abc; Path=/; Domain=example.com" }));

        using var executor = new HttpExecutor();
        var result = await executor.SendAsync(RequestDefinition.Get(server.Url("/")));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        var cookie = Assert.Single(result.Value!.Cookies);
        Assert.Equal("session", cookie.Name);
        Assert.Equal("abc", cookie.Value);
        Assert.Equal("/", cookie.Path);
    }

    // ---- timings (FR-A01) ----------------------------------------------------------------

    [Fact]
    public async Task A_fresh_connection_reports_a_dns_connect_and_total_breakdown()
    {
        await using var server = LoopbackServer.Start(new CannedResponse(Delay: TimeSpan.FromMilliseconds(60)));
        using var executor = new HttpExecutor();

        var result = await executor.SendAsync(
            RequestDefinition.Get(server.Url("/")), ConnectionMode.Fresh);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        var timing = result.Value!.Timing;

        Assert.True(timing.EstablishedConnection);
        Assert.True(timing.Total >= TimeSpan.FromMilliseconds(50), $"total was {timing.Total}");
        Assert.True(timing.TimeToFirstByte > TimeSpan.Zero);
        Assert.True(timing.Connect > TimeSpan.Zero);

        // Plain HTTP has no handshake to measure.
        Assert.Equal(TimeSpan.Zero, timing.Tls);
    }

    /// <summary>
    /// The one measurement the profiler's honesty rests on: a reused connection must say so,
    /// rather than reporting an implausibly fast handshake.
    /// </summary>
    [Fact]
    public async Task A_pooled_request_reports_that_it_did_not_establish_the_connection()
    {
        await using var server = LoopbackServer.Start(new CannedResponse());
        using var executor = new HttpExecutor();

        var first = await executor.SendAsync(RequestDefinition.Get(server.Url("/")), ConnectionMode.Pooled);
        var second = await executor.SendAsync(RequestDefinition.Get(server.Url("/")), ConnectionMode.Pooled);

        Assert.True(first.IsSuccess, first.ErrorMessage);
        Assert.True(second.IsSuccess, second.ErrorMessage);

        Assert.True(first.Value!.Timing.EstablishedConnection);
        Assert.False(second.Value!.Timing.EstablishedConnection);
        Assert.Equal(TimeSpan.Zero, second.Value.Timing.Connect);
    }

    [Fact]
    public async Task The_server_delay_shows_up_in_time_to_first_byte()
    {
        await using var fast = LoopbackServer.Start(new CannedResponse());
        await using var slow = LoopbackServer.Start(new CannedResponse(Delay: TimeSpan.FromMilliseconds(120)));

        using var executor = new HttpExecutor();

        var quick = await executor.SendAsync(RequestDefinition.Get(fast.Url("/")), ConnectionMode.Fresh);
        var slower = await executor.SendAsync(RequestDefinition.Get(slow.Url("/")), ConnectionMode.Fresh);

        Assert.True(quick.IsSuccess && slower.IsSuccess);
        Assert.True(
            slower.Value!.Timing.TimeToFirstByte > quick.Value!.Timing.TimeToFirstByte,
            $"slow {slower.Value.Timing.TimeToFirstByte} should exceed fast {quick.Value.Timing.TimeToFirstByte}");
    }

    // ---- failure modes (edge case 19) --------------------------------------------------------

    /// <summary>
    /// The mapping is asserted directly rather than by failing to reach a real host: plenty of
    /// networks hijack a non-existent name instead of returning NXDOMAIN, which would make such
    /// a test pass or fail for reasons that have nothing to do with this code.
    /// </summary>
    [Theory]
    [InlineData(System.Net.Sockets.SocketError.HostNotFound, "could not be resolved")]
    [InlineData(System.Net.Sockets.SocketError.ConnectionRefused, "refused the connection")]
    [InlineData(System.Net.Sockets.SocketError.TimedOut, "timed out")]
    [InlineData(System.Net.Sockets.SocketError.HostUnreachable, "unreachable")]
    [InlineData(System.Net.Sockets.SocketError.ConnectionReset, "closed the connection")]
    public void Each_transport_failure_gets_its_own_message(System.Net.Sockets.SocketError error, string expected)
    {
        var exception = new HttpRequestException("generic", new System.Net.Sockets.SocketException((int)error));
        var message = TransportDiagnostics.Describe(exception, "https://api.example.com/v1/things");

        Assert.Contains(expected, message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("api.example.com", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tls_failure_suggests_the_setting_that_would_get_past_it()
    {
        var exception = new HttpRequestException(
            "generic",
            new System.Security.Authentication.AuthenticationException("remote certificate is invalid"));

        var message = TransportDiagnostics.Describe(exception, "https://localhost:5001/");

        Assert.Contains("TLS handshake", message, StringComparison.Ordinal);
        Assert.Contains("Ignore certificate errors", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Transport_failures_are_classified()
    {
        Assert.Equal(TransportFailure.DnsFailure, TransportDiagnostics.Classify(
            new HttpRequestException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound))));

        Assert.Equal(TransportFailure.ConnectionRefused, TransportDiagnostics.Classify(
            new HttpRequestException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused))));

        Assert.Equal(TransportFailure.TlsFailure, TransportDiagnostics.Classify(
            new HttpRequestException("x", new System.Security.Authentication.AuthenticationException("bad"))));
    }

    [Fact]
    public async Task A_refused_connection_says_so()
    {
        // Bind and immediately release a port, so nothing is listening on it.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var executor = new HttpExecutor();

        var result = await executor.SendAsync(
            RequestDefinition.Get($"http://127.0.0.1:{port}/") with
            {
                Options = RequestOptions.Default with { TimeoutSeconds = 5 },
            });

        Assert.False(result.IsSuccess);
        Assert.Contains("refused", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_timeout_names_the_limit_it_exceeded()
    {
        await using var server = LoopbackServer.Start(new CannedResponse(Delay: TimeSpan.FromSeconds(5)));
        using var executor = new HttpExecutor();

        var result = await executor.SendAsync(RequestDefinition.Get(server.Url("/")) with
        {
            Options = RequestOptions.Default with { TimeoutSeconds = 1 },
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_is_distinguished_from_a_timeout()
    {
        await using var server = LoopbackServer.Start(new CannedResponse(Delay: TimeSpan.FromSeconds(5)));
        using var executor = new HttpExecutor();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var result = await executor.SendAsync(
            RequestDefinition.Get(server.Url("/")), ConnectionMode.Pooled, cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // Edge case 20 — a body that is not text must not become a screen of replacement characters.
    [Fact]
    public async Task A_body_that_is_not_valid_text_reports_itself_as_binary()
    {
        var response = new ResponseRecord(
            200, "OK", "1.1", [], [], [0xFF, 0xFE, 0x00, 0x01], "application/octet-stream", null,
            4, RequestTiming.Zero, DateTimeOffset.Now, "http://x/", "GET");

        Assert.Null(response.TryGetText());
    }

    [Fact]
    public void A_body_with_a_declared_charset_is_decoded_with_it()
    {
        var bytes = Encoding.UTF8.GetBytes("héllo");

        var response = new ResponseRecord(
            200, "OK", "1.1", [], [], bytes, "text/plain", "utf-8",
            bytes.Length, RequestTiming.Zero, DateTimeOffset.Now, "http://x/", "GET");

        Assert.Equal("héllo", response.TryGetText());
    }

    [Fact]
    public void An_unknown_charset_falls_back_to_a_utf8_probe_rather_than_throwing()
    {
        var bytes = Encoding.UTF8.GetBytes("ok");

        var response = new ResponseRecord(
            200, "OK", "1.1", [], [], bytes, "text/plain", "x-not-a-charset",
            bytes.Length, RequestTiming.Zero, DateTimeOffset.Now, "http://x/", "GET");

        Assert.Equal("ok", response.TryGetText());
    }
}
