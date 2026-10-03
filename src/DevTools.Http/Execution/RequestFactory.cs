using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using DevTools.Core;
using DevTools.Http.Model;

namespace DevTools.Http.Execution;

/// <summary>
/// Turns a <see cref="RequestDefinition"/> into an <see cref="HttpRequestMessage"/>:
/// URL and query merging, headers, body, and authentication (FR-A21…FR-A25).
/// </summary>
/// <remarks>
/// Everything here is pure apart from reading secrets, which is why it is separate from
/// <see cref="HttpExecutor"/> — it can be unit-tested end to end without a socket, and every
/// auth scheme is asserted that way.
/// </remarks>
public static class RequestFactory
{
    /// <summary>Content types that a body sets automatically when the user has not chosen one.</summary>
    private const string JsonContentType = "application/json";

    public static async Task<OperationResult<HttpRequestMessage>> BuildAsync(
        RequestDefinition request,
        ICredentialStore credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credentials);

        var url = BuildUrl(request);
        if (!url.IsSuccess)
        {
            return OperationResult<HttpRequestMessage>.Fail(url.Error!);
        }

        var uri = url.Value!;
        var method = NormalizeMethod(request.Method);

        var message = new HttpRequestMessage(method, uri);

        if (!string.IsNullOrWhiteSpace(request.Options.HttpVersion) &&
            Version.TryParse(request.Options.HttpVersion, out var version))
        {
            message.Version = version;
            message.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }

        var body = BuildBody(request.Body);
        if (!body.IsSuccess)
        {
            message.Dispose();
            return OperationResult<HttpRequestMessage>.Fail(body.Error!);
        }

        message.Content = body.Value;

        var applied = await ApplyAuthAsync(message, request, credentials, cancellationToken).ConfigureAwait(false);
        if (!applied.IsSuccess)
        {
            message.Dispose();
            return OperationResult<HttpRequestMessage>.Fail(applied.Error!);
        }

        // Applied last so an explicit header always wins over one a body or scheme implied.
        var headers = ApplyHeaders(message, request);
        if (!headers.IsSuccess)
        {
            message.Dispose();
            return OperationResult<HttpRequestMessage>.Fail(headers.Error!);
        }

        return OperationResult<HttpRequestMessage>.Ok(message);
    }

    // ---- url -------------------------------------------------------------------------

    /// <summary>
    /// Merges the URL with the query table. Rows are appended to whatever the URL already
    /// carries, so the two stay consistent in both directions (FR-A22).
    /// </summary>
    public static OperationResult<Uri> BuildUrl(RequestDefinition request)
    {
        var text = (request.Url ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return OperationResult<Uri>.Fail("The request has no URL.");
        }

        // A bare host is what people type; assume https rather than refusing it.
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return OperationResult<Uri>.Fail($"\"{request.Url}\" is not a valid URL.");
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return OperationResult<Uri>.Fail($"\"{uri.Scheme}\" is not a scheme this tool sends; use http or https.");
        }

        var enabled = request.Query.Where(static q => q.Enabled && !string.IsNullOrEmpty(q.Name)).ToList();

        if (enabled.Count == 0)
        {
            return OperationResult<Uri>.Ok(uri);
        }

        var builder = new UriBuilder(uri);
        var existing = builder.Query.TrimStart('?');

        var appended = string.Join('&', enabled.Select(static q =>
            $"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(q.Value ?? string.Empty)}"));

        builder.Query = existing.Length > 0 ? $"{existing}&{appended}" : appended;

        return OperationResult<Uri>.Ok(builder.Uri);
    }

    /// <summary>Splits a URL's query string into table rows — the other half of FR-A22.</summary>
    public static IReadOnlyList<KeyValueItem> SplitQuery(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return [];
        }

        var question = url.IndexOf('?', StringComparison.Ordinal);
        if (question < 0 || question == url.Length - 1)
        {
            return [];
        }

        var query = url[(question + 1)..];
        var hash = query.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            query = query[..hash];
        }

        var rows = new List<KeyValueItem>();

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);

            rows.Add(equals < 0
                ? new KeyValueItem(Uri.UnescapeDataString(pair), string.Empty)
                : new KeyValueItem(
                    Uri.UnescapeDataString(pair[..equals]),
                    Uri.UnescapeDataString(pair[(equals + 1)..])));
        }

        return rows;
    }

    /// <summary>The URL with its query string removed, for the URL box when the table owns it.</summary>
    public static string StripQuery(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var question = url.IndexOf('?', StringComparison.Ordinal);
        return question < 0 ? url : url[..question];
    }

    private static HttpMethod NormalizeMethod(string? method) =>
        string.IsNullOrWhiteSpace(method) ? HttpMethod.Get : HttpMethod.Parse(method.Trim().ToUpperInvariant());

    // ---- headers ----------------------------------------------------------------------

    private static OperationResult<bool> ApplyHeaders(HttpRequestMessage message, RequestDefinition request)
    {
        foreach (var header in request.Headers)
        {
            if (!header.Enabled || string.IsNullOrWhiteSpace(header.Name))
            {
                continue;
            }

            var name = header.Name.Trim();
            var value = header.Value ?? string.Empty;

            // Content headers live on the content, not the request, and .NET enforces that.
            if (message.Content is not null && IsContentHeader(name))
            {
                message.Content.Headers.Remove(name);

                if (!message.Content.Headers.TryAddWithoutValidation(name, value))
                {
                    return OperationResult<bool>.Fail($"\"{name}: {value}\" is not a header value the runtime accepts.");
                }

                continue;
            }

            message.Headers.Remove(name);

            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                return OperationResult<bool>.Fail($"\"{name}\" is not a header this request can carry.");
            }
        }

        return OperationResult<bool>.Ok(true);
    }

    private static bool IsContentHeader(string name) => name.ToLowerInvariant() switch
    {
        "content-type" or "content-length" or "content-encoding" or "content-language" or
        "content-location" or "content-disposition" or "content-range" or "content-md5" or
        "expires" or "last-modified" or "allow" => true,
        _ => false,
    };

    // ---- body -------------------------------------------------------------------------

    private static OperationResult<HttpContent?> BuildBody(BodySpec body)
    {
        switch (body.Kind)
        {
            case BodyKind.None:
                return OperationResult<HttpContent?>.Ok(null);

            case BodyKind.Raw:
            case BodyKind.Json:
            {
                var text = body.Text ?? string.Empty;
                var contentType = body.ContentType
                    ?? (body.Kind == BodyKind.Json ? JsonContentType : "text/plain");

                var content = new StringContent(text, Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue(contentType) { CharSet = "utf-8" };

                return OperationResult<HttpContent?>.Ok(content);
            }

            case BodyKind.FormUrlEncoded:
            {
                var pairs = body.Form
                    .Where(static f => f.Enabled && !string.IsNullOrEmpty(f.Name))
                    .Select(static f => new KeyValuePair<string, string>(f.Name, f.Value ?? string.Empty));

                return OperationResult<HttpContent?>.Ok(new FormUrlEncodedContent(pairs));
            }

            case BodyKind.Multipart:
            {
                var multipart = new MultipartFormDataContent();

                foreach (var part in body.Parts.Where(static p => p.Enabled && !string.IsNullOrEmpty(p.Name)))
                {
                    if (!string.IsNullOrWhiteSpace(part.FilePath))
                    {
                        if (!File.Exists(part.FilePath))
                        {
                            multipart.Dispose();
                            return OperationResult<HttpContent?>.Fail(
                                $"The multipart part \"{part.Name}\" points at \"{part.FilePath}\", which does not exist.");
                        }

                        var bytes = new ByteArrayContent(File.ReadAllBytes(part.FilePath));

                        if (!string.IsNullOrWhiteSpace(part.ContentType))
                        {
                            bytes.Headers.ContentType = new MediaTypeHeaderValue(part.ContentType);
                        }

                        multipart.Add(bytes, part.Name, Path.GetFileName(part.FilePath));
                    }
                    else
                    {
                        multipart.Add(new StringContent(part.Value ?? string.Empty, Encoding.UTF8), part.Name);
                    }
                }

                return OperationResult<HttpContent?>.Ok(multipart);
            }

            case BodyKind.BinaryFile:
            {
                if (string.IsNullOrWhiteSpace(body.FilePath) || !File.Exists(body.FilePath))
                {
                    return OperationResult<HttpContent?>.Fail(
                        $"The request body file \"{body.FilePath}\" does not exist.");
                }

                var content = new ByteArrayContent(File.ReadAllBytes(body.FilePath));
                content.Headers.ContentType =
                    new MediaTypeHeaderValue(body.ContentType ?? "application/octet-stream");

                return OperationResult<HttpContent?>.Ok(content);
            }

            default:
                return OperationResult<HttpContent?>.Ok(null);
        }
    }

    // ---- authentication ----------------------------------------------------------------

    private static async Task<OperationResult<bool>> ApplyAuthAsync(
        HttpRequestMessage message,
        RequestDefinition request,
        ICredentialStore credentials,
        CancellationToken cancellationToken)
    {
        var auth = request.Auth;

        if (auth.Kind == AuthKind.None)
        {
            return OperationResult<bool>.Ok(true);
        }

        var secret = string.IsNullOrEmpty(auth.SecretRef)
            ? null
            : await credentials.GetAsync(auth.SecretRef, cancellationToken).ConfigureAwait(false);

        switch (auth.Kind)
        {
            case AuthKind.Basic:
            {
                var raw = $"{auth.Username}:{secret ?? string.Empty}";
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                message.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
                return OperationResult<bool>.Ok(true);
            }

            case AuthKind.Bearer:
            {
                if (string.IsNullOrEmpty(secret))
                {
                    return OperationResult<bool>.Fail("Bearer authentication is selected but no token has been set for this request.");
                }

                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                return OperationResult<bool>.Ok(true);
            }

            case AuthKind.ApiKey:
            {
                if (string.IsNullOrWhiteSpace(auth.ApiKeyName))
                {
                    return OperationResult<bool>.Fail("API-key authentication is selected but the key's name has not been set.");
                }

                if (auth.ApiKeyIn == ApiKeyLocation.Header)
                {
                    message.Headers.TryAddWithoutValidation(auth.ApiKeyName, secret ?? string.Empty);
                    return OperationResult<bool>.Ok(true);
                }

                var builder = new UriBuilder(message.RequestUri!);
                var existing = builder.Query.TrimStart('?');
                var pair = $"{Uri.EscapeDataString(auth.ApiKeyName)}={Uri.EscapeDataString(secret ?? string.Empty)}";

                builder.Query = existing.Length > 0 ? $"{existing}&{pair}" : pair;
                message.RequestUri = builder.Uri;

                return OperationResult<bool>.Ok(true);
            }

            case AuthKind.OAuth2ClientCredentials:
                // The token exchange is a request of its own and belongs to the executor, which
                // owns every outbound call. The caller fetches the token and stores it as the
                // request's bearer secret before sending.
                if (string.IsNullOrEmpty(secret))
                {
                    return OperationResult<bool>.Fail(
                        "No OAuth 2.0 access token has been obtained yet. Use \"Get token\" before sending.");
                }

                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                return OperationResult<bool>.Ok(true);

            default:
                return OperationResult<bool>.Ok(true);
        }
    }

    /// <summary>
    /// Builds the client-credentials token request. Kept here so the whole auth surface is in
    /// one file and testable without sending anything.
    /// </summary>
    public static OperationResult<RequestDefinition> BuildTokenRequest(AuthSpec auth, string? clientSecret)
    {
        if (string.IsNullOrWhiteSpace(auth.TokenUrl))
        {
            return OperationResult<RequestDefinition>.Fail("The OAuth 2.0 token URL has not been set.");
        }

        if (string.IsNullOrWhiteSpace(auth.ClientId))
        {
            return OperationResult<RequestDefinition>.Fail("The OAuth 2.0 client id has not been set.");
        }

        var fields = new List<FormField>
        {
            new("grant_type", "client_credentials"),
            new("client_id", auth.ClientId),
            new("client_secret", clientSecret ?? string.Empty),
        };

        if (!string.IsNullOrWhiteSpace(auth.Scope))
        {
            fields.Add(new FormField("scope", auth.Scope));
        }

        return OperationResult<RequestDefinition>.Ok(new RequestDefinition
        {
            Name = "OAuth 2.0 token",
            Method = "POST",
            Url = auth.TokenUrl,
            Body = new BodySpec { Kind = BodyKind.FormUrlEncoded, Form = fields },
        });
    }

    /// <summary>Pulls <c>access_token</c> out of a token endpoint's reply.</summary>
    public static OperationResult<string> ReadAccessToken(ResponseRecord response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = response.TryGetText();

        if (string.IsNullOrWhiteSpace(text))
        {
            return OperationResult<string>.Fail("The token endpoint returned an empty body.");
        }

        var parsed = Core.Json.JsonReader.Parse(text);

        if (!parsed.IsSuccess || parsed.Value!.Root is not Core.Json.JsonObject obj)
        {
            return OperationResult<string>.Fail("The token endpoint did not return a JSON object.");
        }

        if (obj.Find("access_token") is Core.Json.JsonString token && token.Value.Length > 0)
        {
            return OperationResult<string>.Ok(token.Value);
        }

        var error = obj.Find("error") is Core.Json.JsonString e ? e.Value : null;
        var description = obj.Find("error_description") is Core.Json.JsonString d ? d.Value : null;

        return OperationResult<string>.Fail(error is null
            ? "The token endpoint's reply contained no \"access_token\"."
            : $"The token endpoint refused the request: {error}{(description is null ? string.Empty : $" — {description}")}.");
    }

    internal static string FormatDuration(TimeSpan value) => value.TotalMilliseconds switch
    {
        < 1 => $"{value.TotalMilliseconds:0.##} ms",
        < 1000 => $"{value.TotalMilliseconds:0.#} ms",
        _ => $"{value.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s",
    };
}
