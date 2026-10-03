namespace DevTools.Profiler.Agent;

/// <summary>One header, on the wire.</summary>
public sealed class WireHeader
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// One captured HTTP exchange as it travels from the agent to DevTools.
/// </summary>
/// <remarks>
/// A plain DTO, serialised with System.Text.Json and length-prefixed on the pipe. DevTools has a
/// byte-for-byte mirror of this shape (<c>DevTools.Http.Capture.WireExchange</c>); the two are
/// kept in step by property name. Bodies ride as <see cref="byte"/> arrays, which System.Text.Json
/// writes as base64 — the exchange is captured whole, binary bodies included.
/// </remarks>
public sealed class WireExchange
{
    public long StartedAtUnixMs { get; set; }

    public string Method { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public string PathAndQuery { get; set; } = string.Empty;

    public string HttpVersion { get; set; } = "1.1";

    public bool IsSecure { get; set; }

    public WireHeader[] RequestHeaders { get; set; } = [];

    public byte[] RequestBody { get; set; } = [];

    public int StatusCode { get; set; }

    public string ReasonPhrase { get; set; } = string.Empty;

    public WireHeader[] ResponseHeaders { get; set; } = [];

    public byte[] ResponseBody { get; set; } = [];

    public string? ResponseMediaType { get; set; }

    public double DurationMs { get; set; }

    public int ProcessId { get; set; }

    public string ProcessName { get; set; } = string.Empty;

    public bool Failed { get; set; }

    public string? Error { get; set; }
}
