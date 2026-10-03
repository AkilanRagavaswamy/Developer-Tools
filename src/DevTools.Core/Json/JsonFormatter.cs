using System.Text;
using DevTools.Core.Text;

namespace DevTools.Core.Json;

/// <summary>What <see cref="JsonFormatter.Format"/> should do with the document it parsed.</summary>
public enum JsonFormatMode
{
    /// <summary>Re-emit the document indented, one member per line.</summary>
    Pretty,

    /// <summary>Re-emit the document with no insignificant whitespace at all.</summary>
    Minify,

    /// <summary>Parse only, and report a one-line verdict instead of a document.</summary>
    ValidateOnly,
}

/// <summary>Everything the JSON formatter lets the caller choose (FR-J01…FR-J10).</summary>
public sealed record JsonFormatOptions
{
    public JsonFormatMode Mode { get; init; } = JsonFormatMode.Pretty;

    public IndentStyle IndentStyle { get; init; } = IndentStyle.TwoSpaces;

    /// <summary>Sorts the members of every object by ordinal name, recursively.</summary>
    public bool SortKeys { get; init; }

    /// <summary>Accepts a comma before <c>}</c> or <c>]</c> on input.</summary>
    public bool AllowTrailingCommas { get; init; }

    /// <summary>Accepts <c>//</c> and <c>/* */</c> comments on input; they are dropped.</summary>
    public bool AllowComments { get; init; }

    /// <summary>Escapes every non-ASCII character as <c>\uXXXX</c> in the output.</summary>
    public bool EscapeNonAscii { get; init; }

    public static JsonFormatOptions Default { get; } = new();

    internal JsonReaderOptions ToReaderOptions() => new()
    {
        AllowTrailingCommas = AllowTrailingCommas,
        AllowComments = AllowComments,
    };

    internal JsonWriterOptions ToWriterOptions() => new()
    {
        Minify = Mode == JsonFormatMode.Minify,
        IndentStyle = IndentStyle,
        SortKeys = SortKeys,
        EscapeNonAscii = EscapeNonAscii,
    };
}

/// <summary>A shape summary of a JSON document, for the status strip (FR-J07).</summary>
public sealed record JsonStats(int ObjectCount, int ArrayCount, int MaxDepth, int KeyCount, int ByteSize)
{
    public static JsonStats Empty { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>The formatted document, the shape of what was parsed, and anything worth saying.</summary>
public sealed record JsonFormatResult(
    string Output,
    JsonStats Stats,
    JsonFormatMode Mode,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Pretty-prints, minifies, validates and queries JSON on top of <see cref="JsonReader"/> and
/// <see cref="JsonWriter"/>, so numbers never pass through <see cref="double"/> and
/// <c>1.0</c>, <c>1e10</c> and 30-digit integers survive a round trip (FR-J05).
/// </summary>
public static class JsonFormatter
{
    public static OperationResult<JsonFormatResult> Format(string? json, JsonFormatOptions? options = null)
    {
        var opts = options ?? JsonFormatOptions.Default;

        if (TextUtil.IsBlank(json))
        {
            return OperationResult<JsonFormatResult>.Ok(
                new JsonFormatResult(string.Empty, JsonStats.Empty, opts.Mode, []));
        }

        var parsed = JsonReader.Parse(json, opts.ToReaderOptions());
        if (!parsed.IsSuccess)
        {
            return OperationResult<JsonFormatResult>.Fail(parsed.Error!);
        }

        var document = parsed.Value!;
        var stats = Analyze(document.Root, json!);

        var output = opts.Mode == JsonFormatMode.ValidateOnly
            ? Describe(stats)
            : JsonWriter.Write(document.Root, opts.ToWriterOptions());

        var warnings = new List<string>(document.Warnings);
        if (stats.ByteSize > Limits.MaxInputBytes)
        {
            warnings.Add($"The document is {Limits.Describe(stats.ByteSize)}, above the {Limits.Describe(Limits.MaxInputBytes)} comfort limit.");
        }

        return OperationResult<JsonFormatResult>.Ok(
            new JsonFormatResult(output, stats, opts.Mode, warnings),
            warnings.Count > 0 ? warnings[0] : null);
    }

    /// <summary>Parses and reports the document's shape without rendering it.</summary>
    public static OperationResult<JsonStats> Inspect(string? json, JsonFormatOptions? options = null)
    {
        var opts = options ?? JsonFormatOptions.Default;

        if (TextUtil.IsBlank(json))
        {
            return OperationResult<JsonStats>.Ok(JsonStats.Empty);
        }

        var parsed = JsonReader.Parse(json, opts.ToReaderOptions());
        return parsed.IsSuccess
            ? OperationResult<JsonStats>.Ok(Analyze(parsed.Value!.Root, json!))
            : OperationResult<JsonStats>.Fail(parsed.Error!);
    }

    /// <summary>Runs a JSONPath over the document (FR-J06).</summary>
    public static OperationResult<JsonQueryResult> Query(string? json, string? path, JsonFormatOptions? options = null)
    {
        var opts = options ?? JsonFormatOptions.Default;

        if (TextUtil.IsBlank(json))
        {
            return OperationResult<JsonQueryResult>.Ok(new JsonQueryResult(string.Empty, 0, path ?? "$", []));
        }

        var parsed = JsonReader.Parse(json, opts.ToReaderOptions());
        if (!parsed.IsSuccess)
        {
            return OperationResult<JsonQueryResult>.Fail(parsed.Error!);
        }

        return JsonPathQuery.Query(parsed.Value!.Root, path, opts.ToWriterOptions());
    }

    /// <summary>True when the text parses. Used by the API Builder body editor.</summary>
    public static bool IsValid(string? json, JsonFormatOptions? options = null) =>
        !TextUtil.IsBlank(json) && JsonReader.Parse(json, (options ?? JsonFormatOptions.Default).ToReaderOptions()).IsSuccess;

    /// <summary>Pretty-prints if the text is JSON, and returns it untouched if it is not.</summary>
    public static string PrettyOrOriginal(string? text)
    {
        if (TextUtil.IsBlank(text))
        {
            return text ?? string.Empty;
        }

        var result = Format(text, new JsonFormatOptions { Mode = JsonFormatMode.Pretty });
        return result.IsSuccess ? result.Value!.Output : text!;
    }

    private static JsonStats Analyze(JsonNode root, string source)
    {
        var objects = 0;
        var arrays = 0;
        var keys = 0;
        var maxDepth = 0;

        Walk(root, 1);

        return new JsonStats(objects, arrays, maxDepth, keys, Encoding.UTF8.GetByteCount(source));

        void Walk(JsonNode node, int depth)
        {
            if (depth > maxDepth)
            {
                maxDepth = depth;
            }

            switch (node)
            {
                case JsonObject obj:
                    objects++;
                    keys += obj.Members.Count;
                    foreach (var member in obj.Members)
                    {
                        Walk(member.Value, depth + 1);
                    }

                    break;

                case JsonArray array:
                    arrays++;
                    foreach (var item in array.Items)
                    {
                        Walk(item, depth + 1);
                    }

                    break;
            }
        }
    }

    private static string Describe(JsonStats stats) =>
        $"Valid JSON — {stats.ObjectCount} object(s), {stats.ArrayCount} array(s), " +
        $"{stats.KeyCount} key(s), max depth {stats.MaxDepth}, {Limits.Describe(stats.ByteSize)}.";
}
