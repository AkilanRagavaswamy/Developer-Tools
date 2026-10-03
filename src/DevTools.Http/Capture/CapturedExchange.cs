using DevTools.Core;

namespace DevTools.Http.Capture;

/// <summary>Why an exchange could not be read in full, if it could not.</summary>
public enum CaptureOutcome
{
    /// <summary>Request and response were both read.</summary>
    Complete,

    /// <summary>An https tunnel that was passed through untouched — no certificate to sign with.</summary>
    Tunnelled,

    /// <summary>
    /// An https tunnel we tried to open and the client refused: the client checks the server's
    /// certificate against a pinned one, and ours is not it.
    /// </summary>
    Pinned,

    /// <summary>The exchange failed before a response arrived.</summary>
    Failed,
}

/// <summary>One header of a captured exchange.</summary>
public sealed record CapturedHeader(string Name, string Value);

/// <summary>
/// One HTTP exchange seen by the capture proxy.
/// </summary>
/// <remarks>
/// Bodies are kept whole and in memory, which is what makes this useful — a truncated body is
/// exactly the one you wanted to read — and also what makes a long capture expensive. The UI
/// shows the running total so that cost is visible rather than a surprise.
/// </remarks>
public sealed record CapturedExchange
{
    public required int Index { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required string Method { get; init; }

    /// <summary>The absolute URL, or just the host and port for a tunnel.</summary>
    public required string Url { get; init; }

    public required string Host { get; init; }

    /// <summary>Path and query, empty for a tunnel.</summary>
    public string PathAndQuery { get; init; } = string.Empty;

    public string HttpVersion { get; init; } = "1.1";

    public bool IsSecure { get; init; }

    public IReadOnlyList<CapturedHeader> RequestHeaders { get; init; } = [];

    public byte[] RequestBody { get; init; } = [];

    public int StatusCode { get; init; }

    public string ReasonPhrase { get; init; } = string.Empty;

    public IReadOnlyList<CapturedHeader> ResponseHeaders { get; init; } = [];

    public byte[] ResponseBody { get; init; } = [];

    public string? ResponseMediaType { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Bytes relayed through an https tunnel we could not read. Counted rather than kept: the
    /// content is encrypted, so storing it would cost memory for something nobody can read.
    /// </summary>
    public long TunnelBytes { get; init; }

    public CaptureOutcome Outcome { get; init; } = CaptureOutcome.Complete;

    /// <summary>Set when <see cref="Outcome"/> is <see cref="CaptureOutcome.Failed"/>.</summary>
    public string? Error { get; init; }

    /// <summary>The process that opened the connection, from the TCP table. 0 when unknown.</summary>
    public int ProcessId { get; init; }

    public string ProcessName { get; init; } = string.Empty;

    /// <summary>Bytes on the wire, for the running total the capture bar shows.</summary>
    public long TotalBytes => RequestBody.LongLength + ResponseBody.LongLength + TunnelBytes;

    public bool IsSuccess => StatusCode is >= 200 and < 400;

    public string StatusText => Outcome switch
    {
        CaptureOutcome.Tunnelled or CaptureOutcome.Pinned => "—",
        CaptureOutcome.Failed => "failed",
        _ => StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public string SizeText => Outcome == CaptureOutcome.Failed && ResponseBody.Length == 0
        ? "—"
        : Limits.Describe(TotalBytes);

    /// <summary>The one-line reason a row is greyed out, or empty when it is a normal exchange.</summary>
    public string OutcomeNote => Outcome switch
    {
        CaptureOutcome.Tunnelled => "not decrypted · no certificate installed",
        CaptureOutcome.Pinned => "certificate pinned · not decrypted",
        CaptureOutcome.Failed => Error ?? "failed",
        _ => string.Empty,
    };

    public string? HeaderValue(string name) =>
        ResponseHeaders.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
}
