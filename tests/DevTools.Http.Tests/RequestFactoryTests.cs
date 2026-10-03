using System.Text;
using DevTools.Http.Execution;
using DevTools.Http.Model;
using Xunit;

namespace DevTools.Http.Tests;

public sealed class RequestFactoryTests
{
    private static async Task<HttpRequestMessage> BuildAsync(RequestDefinition request, ICredentialStore? store = null)
    {
        var result = await RequestFactory.BuildAsync(request, store ?? new InMemoryCredentialStore());
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return result.Value!;
    }

    // ---- url and query (FR-A22) --------------------------------------------------------

    [Fact]
    public void A_bare_host_is_assumed_to_be_https()
    {
        var url = RequestFactory.BuildUrl(RequestDefinition.Get("example.com/api"));
        Assert.True(url.IsSuccess);
        Assert.Equal("https://example.com/api", url.Value!.AbsoluteUri);
    }

    [Fact]
    public void Query_rows_are_appended_to_the_url()
    {
        var url = RequestFactory.BuildUrl(RequestDefinition.Get("https://example.com/s") with
        {
            Query = [new KeyValueItem("q", "a b"), new KeyValueItem("page", "2")],
        });

        Assert.Equal("https://example.com/s?q=a%20b&page=2", url.Value!.AbsoluteUri);
    }

    [Fact]
    public void Query_rows_merge_with_a_query_already_in_the_url()
    {
        var url = RequestFactory.BuildUrl(RequestDefinition.Get("https://example.com/s?existing=1") with
        {
            Query = [new KeyValueItem("added", "2")],
        });

        Assert.Contains("existing=1", url.Value!.ToString(), StringComparison.Ordinal);
        Assert.Contains("added=2", url.Value.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_disabled_query_row_is_left_out()
    {
        var url = RequestFactory.BuildUrl(RequestDefinition.Get("https://example.com/") with
        {
            Query = [new KeyValueItem("on", "1"), new KeyValueItem("off", "2", Enabled: false)],
        });

        Assert.DoesNotContain("off", url.Value!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Splitting_and_stripping_a_query_is_the_inverse_of_building_one()
    {
        const string url = "https://example.com/s?q=a%20b&page=2";

        Assert.Equal("https://example.com/s", RequestFactory.StripQuery(url));

        var rows = RequestFactory.SplitQuery(url);
        Assert.Equal(2, rows.Count);
        Assert.Equal("a b", rows[0].Value);
        Assert.Equal("page", rows[1].Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_url_is_reported(string url)
    {
        Assert.False(RequestFactory.BuildUrl(RequestDefinition.Get(url)).IsSuccess);
    }

    [Fact]
    public void A_non_http_scheme_is_refused()
    {
        var result = RequestFactory.BuildUrl(RequestDefinition.Get("ftp://example.com/x"));
        Assert.False(result.IsSuccess);
        Assert.Contains("ftp", result.ErrorMessage, StringComparison.Ordinal);
    }

    // ---- headers -------------------------------------------------------------------------

    [Fact]
    public async Task Headers_are_applied_and_disabled_rows_skipped()
    {
        using var message = await BuildAsync(RequestDefinition.Get("https://example.com/") with
        {
            Headers =
            [
                new KeyValueItem("X-On", "yes"),
                new KeyValueItem("X-Off", "no", Enabled: false),
            ],
        });

        Assert.True(message.Headers.Contains("X-On"));
        Assert.False(message.Headers.Contains("X-Off"));
    }

    // Content headers live on the content in .NET, and adding them to the request throws.
    [Fact]
    public async Task A_content_type_header_lands_on_the_content()
    {
        using var message = await BuildAsync(new RequestDefinition
        {
            Method = "POST",
            Url = "https://example.com/",
            Headers = [new KeyValueItem("Content-Type", "application/vnd.custom+json")],
            Body = new BodySpec { Kind = BodyKind.Raw, Text = "x" },
        });

        Assert.Equal("application/vnd.custom+json", message.Content!.Headers.ContentType!.MediaType);
    }

    // ---- bodies (FR-A24) -------------------------------------------------------------------

    [Fact]
    public async Task A_json_body_declares_application_json()
    {
        using var message = await BuildAsync(new RequestDefinition
        {
            Method = "POST",
            Url = "https://example.com/",
            Body = BodySpec.FromJson("""{"a":1}"""),
        });

        Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("""{"a":1}""", await message.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_form_body_is_url_encoded()
    {
        using var message = await BuildAsync(new RequestDefinition
        {
            Method = "POST",
            Url = "https://example.com/",
            Body = new BodySpec
            {
                Kind = BodyKind.FormUrlEncoded,
                Form = [new FormField("a", "1 2"), new FormField("skip", "x", Enabled: false)],
            },
        });

        var text = await message.Content!.ReadAsStringAsync();
        Assert.Equal("a=1+2", text);
    }

    [Fact]
    public async Task A_multipart_body_carries_its_value_parts()
    {
        using var message = await BuildAsync(new RequestDefinition
        {
            Method = "POST",
            Url = "https://example.com/",
            Body = new BodySpec { Kind = BodyKind.Multipart, Parts = [new MultipartPart("field", Value: "value")] },
        });

        Assert.StartsWith("multipart/form-data", message.Content!.Headers.ContentType!.MediaType, StringComparison.Ordinal);
        Assert.Contains("value", await message.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_multipart_part_pointing_at_a_missing_file_is_reported()
    {
        var result = await RequestFactory.BuildAsync(
            new RequestDefinition
            {
                Method = "POST",
                Url = "https://example.com/",
                Body = new BodySpec
                {
                    Kind = BodyKind.Multipart,
                    Parts = [new MultipartPart("f", FilePath: Path.Combine(Path.GetTempPath(), "does-not-exist-9e1c.bin"))],
                },
            },
            new InMemoryCredentialStore());

        Assert.False(result.IsSuccess);
        Assert.Contains("does not exist", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_binary_body_reads_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"devtools-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);

        try
        {
            using var message = await BuildAsync(new RequestDefinition
            {
                Method = "PUT",
                Url = "https://example.com/",
                Body = new BodySpec { Kind = BodyKind.BinaryFile, FilePath = path },
            });

            Assert.Equal([1, 2, 3, 4], await message.Content!.ReadAsByteArrayAsync());
            Assert.Equal("application/octet-stream", message.Content.Headers.ContentType!.MediaType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- authentication (FR-A25) -------------------------------------------------------------

    [Fact]
    public async Task Basic_auth_encodes_the_user_and_the_stored_secret()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("secret-1", "s3cret");

        using var message = await BuildAsync(
            RequestDefinition.Get("https://example.com/") with
            {
                Auth = new AuthSpec { Kind = AuthKind.Basic, Username = "ada", SecretRef = "secret-1" },
            },
            store);

        Assert.Equal("Basic", message.Headers.Authorization!.Scheme);
        Assert.Equal("ada:s3cret",
            Encoding.UTF8.GetString(Convert.FromBase64String(message.Headers.Authorization.Parameter!)));
    }

    [Fact]
    public async Task Bearer_auth_uses_the_stored_token()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("tok", "abc123");

        using var message = await BuildAsync(
            RequestDefinition.Get("https://example.com/") with
            {
                Auth = new AuthSpec { Kind = AuthKind.Bearer, SecretRef = "tok" },
            },
            store);

        Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
        Assert.Equal("abc123", message.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Bearer_auth_without_a_token_is_reported_rather_than_sent_empty()
    {
        var result = await RequestFactory.BuildAsync(
            RequestDefinition.Get("https://example.com/") with { Auth = new AuthSpec { Kind = AuthKind.Bearer } },
            new InMemoryCredentialStore());

        Assert.False(result.IsSuccess);
        Assert.Contains("no token", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_api_key_can_go_in_a_header_or_the_query_string()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("k", "KEY");

        using var header = await BuildAsync(
            RequestDefinition.Get("https://example.com/") with
            {
                Auth = new AuthSpec { Kind = AuthKind.ApiKey, ApiKeyName = "X-Api-Key", SecretRef = "k" },
            },
            store);

        Assert.Equal("KEY", header.Headers.GetValues("X-Api-Key").Single());

        using var query = await BuildAsync(
            RequestDefinition.Get("https://example.com/path?a=1") with
            {
                Auth = new AuthSpec
                {
                    Kind = AuthKind.ApiKey,
                    ApiKeyName = "api_key",
                    ApiKeyIn = ApiKeyLocation.Query,
                    SecretRef = "k",
                },
            },
            store);

        Assert.Contains("a=1", query.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("api_key=KEY", query.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_api_key_without_a_name_is_reported()
    {
        var result = await RequestFactory.BuildAsync(
            RequestDefinition.Get("https://example.com/") with { Auth = new AuthSpec { Kind = AuthKind.ApiKey } },
            new InMemoryCredentialStore());

        Assert.False(result.IsSuccess);
        Assert.Contains("name", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_oauth_token_request_is_a_client_credentials_form_post()
    {
        var result = RequestFactory.BuildTokenRequest(
            new AuthSpec
            {
                Kind = AuthKind.OAuth2ClientCredentials,
                TokenUrl = "https://id.example.com/token",
                ClientId = "client",
                Scope = "read write",
            },
            "shhh");

        Assert.True(result.IsSuccess, result.ErrorMessage);

        var request = result.Value!;
        Assert.Equal("POST", request.Method);
        Assert.Equal(BodyKind.FormUrlEncoded, request.Body.Kind);

        var fields = request.Body.Form.ToDictionary(static f => f.Name, static f => f.Value, StringComparer.Ordinal);
        Assert.Equal("client_credentials", fields["grant_type"]);
        Assert.Equal("client", fields["client_id"]);
        Assert.Equal("shhh", fields["client_secret"]);
        Assert.Equal("read write", fields["scope"]);
    }

    [Fact]
    public void An_oauth_token_request_without_a_url_or_client_is_reported()
    {
        Assert.False(RequestFactory.BuildTokenRequest(new AuthSpec { ClientId = "c" }, "s").IsSuccess);
        Assert.False(RequestFactory.BuildTokenRequest(new AuthSpec { TokenUrl = "https://x/token" }, "s").IsSuccess);
    }

    [Fact]
    public void An_access_token_is_read_out_of_the_token_reply()
    {
        var response = Reply("""{"access_token":"tok-123","token_type":"Bearer","expires_in":3600}""");

        var result = RequestFactory.ReadAccessToken(response);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("tok-123", result.Value);
    }

    [Fact]
    public void A_refused_token_request_reports_the_servers_own_reason()
    {
        var response = Reply("""{"error":"invalid_client","error_description":"Bad secret"}""");

        var result = RequestFactory.ReadAccessToken(response);
        Assert.False(result.IsSuccess);
        Assert.Contains("invalid_client", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Bad secret", result.ErrorMessage, StringComparison.Ordinal);
    }

    private static ResponseRecord Reply(string json) => new(
        200, "OK", "1.1", [], [], Encoding.UTF8.GetBytes(json),
        "application/json", "utf-8", json.Length, RequestTiming.Zero,
        DateTimeOffset.Now, "https://id.example.com/token", "POST");
}
