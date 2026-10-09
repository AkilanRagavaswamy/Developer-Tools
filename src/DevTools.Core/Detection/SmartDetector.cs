using DevTools.Core.Codecs;
using DevTools.Core.Json;
using DevTools.Core.Text;
using DevTools.Core.Xml;

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

        if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(";base64,", StringComparison.OrdinalIgnoreCase))
        {
            hits.Add(new DetectionHit("base64-image", "an image data URI", 95));
            return hits;
        }

        if (LooksLikeSvg(trimmed))
        {
            hits.Add(new DetectionHit("svg-to-xaml", "an SVG image", 95));
            hits.Add(new DetectionHit("xml-formatter", "XML", 40));
        }
        else if (trimmed.StartsWith('<') && XmlFormatter.IsWellFormed(trimmed))
        {
            hits.Add(new DetectionHit("xml-formatter", "XML", 85));
        }

        if (LooksLikeJson(trimmed))
        {
            hits.Add(new DetectionHit("json-formatter", "JSON", 90));
            hits.Add(new DetectionHit("json-to-csharp", "JSON", 70));
            hits.Add(new DetectionHit("json-diff", "JSON", 60));

            if (LooksLikeArrayOfObjects(trimmed))
            {
                hits.Add(new DetectionHit("json-to-table", "a JSON array", 55));
            }
        }
        else if (LooksLikeUrl(trimmed))
        {
            hits.Add(new DetectionHit("api-builder", "a URL", 70));

            if (trimmed.Contains('%', StringComparison.Ordinal))
            {
                hits.Add(new DetectionHit("url-encoder", "an encoded URL", 50));
            }
        }
        else if (LooksLikeSql(trimmed))
        {
            hits.Add(new DetectionHit("sql-formatter", "SQL", 80));
        }
        else if (LooksLikeTimestamp(trimmed))
        {
            hits.Add(new DetectionHit("date-converter", "a Unix timestamp", 75));
        }
        else if (Guid.TryParse(trimmed, out _) && trimmed.Length >= 32)
        {
            hits.Add(new DetectionHit("uuid-generator", "a UUID", 60));
        }
        else if (LooksLikeBase64Text(trimmed))
        {
            hits.Add(new DetectionHit("base64-text", "Base64", 65));
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

    private static bool LooksLikeArrayOfObjects(string text)
    {
        if (text[0] != '[')
        {
            return false;
        }

        var parsed = JsonReader.Parse(text, JsonReaderOptions.Tolerant);
        return parsed.IsSuccess && parsed.Value!.Root is JsonArray { Items.Count: > 1 } array && array.Items.All(static i => i is JsonObject);
    }

    /// <summary>
    /// A statement that opens with a clause keyword and contains the keyword that clause needs —
    /// "SELECT … FROM", "UPDATE … SET" — which ordinary prose beginning "Select the…" rarely does.
    /// </summary>
    private static bool LooksLikeSql(string text)
    {
        (string Start, string Needs)[] shapes =
        [
            ("SELECT ", " FROM "), ("INSERT INTO ", " VALUES"), ("INSERT INTO ", " SELECT "), ("UPDATE ", " SET "),
            ("DELETE FROM ", ""), ("WITH ", " AS ("), ("CREATE TABLE ", "("), ("ALTER TABLE ", ""), ("MERGE INTO ", " USING "),
        ];

        var flat = string.Join(' ', text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

        // Prose can say "select the best option from the list"; SQL nearly always has punctuation
        // or a second clause somewhere.
        var hasSyntax = flat.AsSpan().IndexOfAny(",*()=;") >= 0 ||
                        flat.Contains(" WHERE ", StringComparison.OrdinalIgnoreCase) ||
                        flat.Contains(" JOIN ", StringComparison.OrdinalIgnoreCase) ||
                        flat.Contains(" GROUP BY ", StringComparison.OrdinalIgnoreCase) ||
                        flat.Contains(" ORDER BY ", StringComparison.OrdinalIgnoreCase);

        if (!hasSyntax)
        {
            return false;
        }

        foreach (var (start, needs) in shapes)
        {
            if (flat.StartsWith(start, StringComparison.OrdinalIgnoreCase) &&
                (needs.Length == 0 || flat.Contains(needs, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ten digits (seconds) or thirteen (milliseconds) that land between 2001 and 2100.</summary>
    private static bool LooksLikeTimestamp(string text)
    {
        if (text.Length is not (10 or 13) || !text.All(char.IsAsciiDigit) || !long.TryParse(text, out var value))
        {
            return false;
        }

        var seconds = text.Length == 13 ? value / 1000 : value;
        return seconds is >= 978_307_200 and <= 4_102_444_800;
    }

    /// <summary>
    /// Base64 that decodes to readable text. Long, padded or mixed-case-and-digit runs only, so a
    /// word or an identifier is never mistaken for it.
    /// </summary>
    private static bool LooksLikeBase64Text(string text)
    {
        if (text.Length < 16 || text.Length % 4 != 0 || text.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!text.Any(char.IsAsciiDigit) || !text.Any(char.IsAsciiLetterUpper) || !text.Any(char.IsAsciiLetterLower))
        {
            return false;
        }

        return Base64Codec.Run(text, CodecDirection.Decode).IsSuccess;
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
