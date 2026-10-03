using System.Text;
using DevTools.Core;

namespace DevTools.Http.Model;

/// <summary>
/// Where the time went on one request (FR-A01).
/// </summary>
/// <remarks>
/// <see cref="Dns"/>, <see cref="Connect"/> and <see cref="Tls"/> are zero when the request
/// reused a pooled connection — which is not a measurement failure but the most important
/// thing about that request. <see cref="EstablishedConnection"/> says which case it was, so
/// the UI never presents a reused connection as an implausibly fast handshake.
/// </remarks>
public sealed record RequestTiming(
    TimeSpan Dns,
    TimeSpan Connect,
    TimeSpan Tls,
    TimeSpan TimeToFirstByte,
    TimeSpan Download,
    TimeSpan Total,
    bool EstablishedConnection)
{
    public static RequestTiming Zero { get; } = new(
        TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, false);

    /// <summary>Time spent before the first response byte, however it was spent.</summary>
    public TimeSpan Latency => Total - Download;
}

public sealed record CookieItem(string Name, string Value, string? Domain = null, string? Path = null);

/// <summary>One completed exchange: what came back, and how long it took.</summary>
public sealed record ResponseRecord(
    int StatusCode,
    string ReasonPhrase,
    string HttpVersion,
    IReadOnlyList<KeyValueItem> Headers,
    IReadOnlyList<CookieItem> Cookies,
    byte[] Body,
    string? MediaType,
    string? CharSet,
    long ContentLength,
    RequestTiming Timing,
    DateTimeOffset StartedAt,
    string RequestUrl,
    string RequestMethod,
    bool Truncated = false)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public string StatusLine => $"{StatusCode} {ReasonPhrase}".Trim();

    /// <summary>
    /// The body as text, or <see langword="null"/> when it is not text at all.
    /// </summary>
    /// <remarks>
    /// A response with no usable charset is decoded as UTF-8 only if it actually is UTF-8.
    /// Returning null instead of mojibake is what lets the viewer fall back to a hex dump
    /// rather than showing a screen of replacement characters (edge case 20).
    /// </remarks>
    public string? TryGetText()
    {
        if (Body.Length == 0)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(CharSet))
        {
            try
            {
                return Encoding.GetEncoding(CharSet).GetString(Body);
            }
            catch (ArgumentException)
            {
                // An unknown charset label: fall through to the UTF-8 probe.
            }
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(Body);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    public string DescribeSize() => Limits.Describe(Body.Length);
}

/// <summary>A request that never produced a response, and why.</summary>
public enum TransportFailure
{
    None,
    DnsFailure,
    ConnectionRefused,
    TlsFailure,
    Timeout,
    Cancelled,
    TooLarge,
    Other,
}
