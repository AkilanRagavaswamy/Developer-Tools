namespace DevTools.Core;

/// <summary>
/// The guard rails every engine shares. Centralised so the UI can describe them
/// consistently and so tests can assert against the same numbers the engines enforce.
/// </summary>
public static class Limits
{
    /// <summary>Above this, the UI asks before running a transform (FR-T07).</summary>
    public const int MaxInputBytes = 5 * 1024 * 1024;

    /// <summary>Nesting we are willing to parse; deeper input is a bug or an attack, not data.</summary>
    public const int MaxJsonDepth = 256;

    /// <summary>Largest pair of documents the JSON differ will walk (FR-J27).</summary>
    public const int MaxDiffNodes = 50_000;

    /// <summary>Largest pair of documents the textual diff engine will compare line by line.</summary>
    public const int MaxDiffLines = 50_000;

    /// <summary>Upper bound on the type graph the C# generator will emit.</summary>
    public const int MaxGeneratedTypes = 500;

    /// <summary>Largest SVG the converter will walk.</summary>
    public const int MaxSvgElements = 50_000;

    /// <summary>Hard cap on a response body we will buffer.</summary>
    public const long MaxResponseBytes = 64L * 1024 * 1024;

    /// <summary>Default per-request timeout.</summary>
    public static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(100);

    /// <summary>Responses retained per request in API Builder history (FR-A28).</summary>
    public const int MaxHistoryPerRequest = 25;

    public static string Describe(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.##} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.##} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
