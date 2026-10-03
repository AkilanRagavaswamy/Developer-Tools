namespace DevTools.Http.Model;

/// <summary>A header or query-string row that the user can switch off without deleting.</summary>
public sealed record KeyValueItem(string Name, string Value, bool Enabled = true, string? Description = null);

/// <summary>What kind of body a request carries (FR-A24).</summary>
public enum BodyKind
{
    None,
    Raw,
    Json,
    FormUrlEncoded,
    Multipart,
    BinaryFile,
}

public sealed record FormField(string Name, string Value, bool Enabled = true);

/// <summary>One part of a multipart body: either a value or a file.</summary>
public sealed record MultipartPart(
    string Name,
    string? Value = null,
    string? FilePath = null,
    string? ContentType = null,
    bool Enabled = true);

public sealed record BodySpec
{
    public BodyKind Kind { get; init; } = BodyKind.None;

    /// <summary>The literal body for <see cref="BodyKind.Raw"/> and <see cref="BodyKind.Json"/>.</summary>
    public string? Text { get; init; }

    /// <summary>Overrides the content type this body would otherwise declare.</summary>
    public string? ContentType { get; init; }

    public IReadOnlyList<FormField> Form { get; init; } = [];

    public IReadOnlyList<MultipartPart> Parts { get; init; } = [];

    /// <summary>The file sent for <see cref="BodyKind.BinaryFile"/>.</summary>
    public string? FilePath { get; init; }

    public static BodySpec None { get; } = new();

    public static BodySpec FromJson(string json) => new() { Kind = BodyKind.Json, Text = json };
}

/// <summary>The authentication schemes API Builder can apply (FR-A25).</summary>
public enum AuthKind
{
    None,
    Basic,
    Bearer,
    ApiKey,
    OAuth2ClientCredentials,
}

public enum ApiKeyLocation
{
    Header,
    Query,
}

/// <summary>
/// How a request authenticates.
/// </summary>
/// <remarks>
/// No field here holds a secret. Passwords, tokens and client secrets live in
/// <see cref="ICredentialStore"/> and are referenced by <see cref="SecretRef"/>, so a
/// collection file on disk never contains a credential (FR-A30).
/// </remarks>
public sealed record AuthSpec
{
    public AuthKind Kind { get; init; } = AuthKind.None;

    /// <summary>Basic auth user name. Not a secret.</summary>
    public string? Username { get; init; }

    /// <summary>The header or query parameter an API key is sent in.</summary>
    public string? ApiKeyName { get; init; }

    public ApiKeyLocation ApiKeyIn { get; init; } = ApiKeyLocation.Header;

    /// <summary>OAuth 2.0 token endpoint.</summary>
    public string? TokenUrl { get; init; }

    public string? ClientId { get; init; }

    public string? Scope { get; init; }

    /// <summary>The key this request's secret is stored under. Never the secret itself.</summary>
    public string? SecretRef { get; init; }

    public static AuthSpec None { get; } = new();
}

/// <summary>Per-request transport settings.</summary>
public sealed record RequestOptions
{
    public int TimeoutSeconds { get; init; } = 100;

    public bool FollowRedirects { get; init; } = true;

    public int MaxRedirects { get; init; } = 10;

    /// <summary>
    /// Disables certificate validation for this request only (FR-A08). There is deliberately
    /// no global setting for this: it is a per-request, explicitly-chosen hazard.
    /// </summary>
    public bool IgnoreCertificateErrors { get; init; }

    /// <summary>"1.1", "2.0" or "3.0"; anything else falls back to the platform default.</summary>
    public string? HttpVersion { get; init; }

    public string? ProxyUrl { get; init; }

    public static RequestOptions Default { get; } = new();
}

/// <summary>Everything needed to send one request (FR-A21…FR-A25).</summary>
public sealed record RequestDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "New request";

    public string Method { get; init; } = "GET";

    public string Url { get; init; } = string.Empty;

    public IReadOnlyList<KeyValueItem> Headers { get; init; } = [];

    /// <summary>Query rows, kept in step with the URL in both directions (FR-A22).</summary>
    public IReadOnlyList<KeyValueItem> Query { get; init; } = [];

    public BodySpec Body { get; init; } = BodySpec.None;

    public AuthSpec Auth { get; init; } = AuthSpec.None;

    public RequestOptions Options { get; init; } = RequestOptions.Default;

    public static RequestDefinition Get(string url) => new() { Method = "GET", Url = url };
}

/// <summary>
/// Where secrets live. The implementation in the app is backed by the Windows credential
/// vault; the in-memory one exists so tests never need a vault.
/// </summary>
public interface ICredentialStore
{
    ValueTask<string?> GetAsync(string reference, CancellationToken cancellationToken = default);

    ValueTask SetAsync(string reference, string secret, CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(string reference, CancellationToken cancellationToken = default);
}

/// <summary>A credential store that keeps nothing beyond the life of the process.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public ValueTask<string?> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        lock (_secrets)
        {
            return ValueTask.FromResult(_secrets.GetValueOrDefault(reference));
        }
    }

    public ValueTask SetAsync(string reference, string secret, CancellationToken cancellationToken = default)
    {
        lock (_secrets)
        {
            _secrets[reference] = secret;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string reference, CancellationToken cancellationToken = default)
    {
        lock (_secrets)
        {
            _secrets.Remove(reference);
        }

        return ValueTask.CompletedTask;
    }
}
