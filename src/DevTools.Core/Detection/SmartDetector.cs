using DevTools.Core.Json;
using DevTools.Core.Text;

namespace DevTools.Core.Detection;

/// <summary>A tool the clipboard's contents might be meant for, and why.</summary>
public sealed record DetectionHit(string ToolId, string Label, int Confidence);

/// <summary>
/// Classifies clipboard text and suggests the tools it suits (FR-S19).
/// </summary>
/// <remarks>
/// Deliberately conservative: a wrong suggestion is worse than none, because it trains people
/// to ignore the banner. JSON must actually parse, SVG must have an <c>&lt;svg&gt;</c> root
/// element, and a cURL command must start with the word <c>curl</c> — no heuristic here fires
/// on ordinary prose.
/// </remarks>
public static class SmartDetector
{
    public static IReadOnlyList<DetectionHit> Detect(string? text)
    {
        if (TextUtil.IsBlank(text))
        {
            return [];
        }

        var trimmed = TextUtil.StripBom(text).Trim();

        // Anything this long is a document someone is working on, not something they just
        // copied to convert; classifying it would also mean parsing megabytes on activation.
        if (trimmed.Length > 512 * 1024)
        {
            return [];
        }

        var hits = new List<DetectionHit>();

        if (LooksLikeCurl(trimmed))
        {
            hits.Add(new DetectionHit("api-builder", "a cURL command", 95));
        }

        if (LooksLikeSvg(trimmed))
        {
            hits.Add(new DetectionHit("svg-to-xaml", "an SVG image", 95));
        }

        if (LooksLikeJson(trimmed))
        {
            hits.Add(new DetectionHit("json-formatter", "JSON", 90));
            hits.Add(new DetectionHit("json-to-csharp", "JSON", 70));
            hits.Add(new DetectionHit("json-diff", "JSON", 60));
        }
        else if (LooksLikeUrl(trimmed))
        {
            hits.Add(new DetectionHit("api-builder", "a URL", 70));
        }

        return hits;
    }

    private static bool LooksLikeCurl(string text) =>
        text.StartsWith("curl ", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("curl\t", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("curl\n", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSvg(string text)
    {
        if (!text.StartsWith('<'))
        {
            return false;
        }

        // Tolerate an XML declaration or a doctype before the root element.
        var index = text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);

        if (index < 0)
        {
            return false;
        }

        // The root must be <svg>, not an <svg> buried inside some larger HTML document.
        var before = text[..index];
        return !before.Contains("<body", StringComparison.OrdinalIgnoreCase) &&
               !before.Contains("<html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeJson(string text)
    {
        // A cheap gate before the parser: JSON worth offering a tool for is an object or array.
        if (text.Length < 2 || text[0] is not ('{' or '['))
        {
            return false;
        }

        return JsonReader.Parse(text, JsonReaderOptions.Tolerant).IsSuccess;
    }

    private static bool LooksLikeUrl(string text)
    {
        if (text.Contains('\n', StringComparison.Ordinal) || text.Length > 2048)
        {
            return false;
        }

        return (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
               Uri.TryCreate(text, UriKind.Absolute, out _);
    }
}
