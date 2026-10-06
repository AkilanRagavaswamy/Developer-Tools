using System.Text;
using DevTools.Core.Text;

namespace DevTools.Core.Json;

/// <summary>The text forms a table can be copied or saved as.</summary>
public enum TableTextFormat
{
    Csv,
    Tsv,
    Markdown,
}

/// <summary>A JSON document laid out as rows and columns.</summary>
public sealed record JsonTableResult(IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows, int SkippedElements)
{
    public static JsonTableResult Empty { get; } = new([], [], 0);
}

/// <summary>Options for <see cref="JsonTable.Convert"/>.</summary>
public sealed record JsonTableOptions
{
    /// <summary>Joins the names of nested members into one column name: <c>address.city</c>.</summary>
    public string Separator { get; init; } = ".";

    /// <summary>Spreads nested objects into columns of their own; off, a nested object is one JSON cell.</summary>
    public bool FlattenObjects { get; init; } = true;

    public JsonReaderOptions ReaderOptions { get; init; } = JsonReaderOptions.Tolerant;

    public static JsonTableOptions Default { get; } = new();
}

/// <summary>
/// Turns a JSON array of objects into a table: one row per element, one column per member.
/// </summary>
/// <remarks>
/// <para>
/// Columns appear in the order their member is first seen, across all rows, so a member that
/// only the tenth element has still gets a column. Nested objects become dotted columns; arrays
/// stay as compact JSON in their cell rather than being dropped, because a table that silently
/// loses the "tags" column is worse than one with a JSON cell in it.
/// </para>
/// <para>
/// Built on <see cref="JsonReader"/>, so numbers keep their literal text and a date string is
/// shown exactly as it was written — DevToys' version re-formats both.
/// </para>
/// </remarks>
public static class JsonTable
{
    public static OperationResult<JsonTableResult> Convert(string? json, JsonTableOptions? options = null)
    {
        var opts = options ?? JsonTableOptions.Default;

        if (TextUtil.IsBlank(json))
        {
            return OperationResult<JsonTableResult>.Ok(JsonTableResult.Empty);
        }

        var parsed = JsonReader.Parse(json, opts.ReaderOptions);
        if (!parsed.IsSuccess)
        {
            return OperationResult<JsonTableResult>.Fail(parsed.Error!);
        }

        var root = parsed.Value!.Root;

        // A single object is a table of one row; an array of scalars is a single column.
        IReadOnlyList<JsonNode> elements = root switch
        {
            JsonArray array => array.Items,
            JsonObject obj => [obj],
            _ => [],
        };

        if (elements.Count == 0)
        {
            return root is JsonArray
                ? OperationResult<JsonTableResult>.Ok(JsonTableResult.Empty, "The array is empty.")
                : OperationResult<JsonTableResult>.Fail("A table needs an array of objects, or a single object, at the top level.");
        }

        var columns = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var records = new List<Dictionary<int, string>>(elements.Count);
        var skipped = 0;

        int Column(string name)
        {
            if (!index.TryGetValue(name, out var at))
            {
                at = columns.Count;
                columns.Add(name);
                index[name] = at;
            }

            return at;
        }

        var hasObjects = elements.Any(static e => e is JsonObject);

        foreach (var element in elements)
        {
            var record = new Dictionary<int, string>();

            if (element is JsonObject obj)
            {
                Flatten(obj, string.Empty, record, Column, opts);
            }
            else if (hasObjects)
            {
                // A stray scalar among objects has no columns to go in.
                skipped++;
                continue;
            }
            else
            {
                record[Column("value")] = Cell(element);
            }

            records.Add(record);
        }

        if (columns.Count == 0)
        {
            return OperationResult<JsonTableResult>.Fail("The objects have no members, so there are no columns to show.");
        }

        var rows = new List<string[]>(records.Count);
        foreach (var record in records)
        {
            var row = new string[columns.Count];
            for (var c = 0; c < row.Length; c++)
            {
                row[c] = record.TryGetValue(c, out var value) ? value : string.Empty;
            }

            rows.Add(row);
        }

        var result = new JsonTableResult(columns, rows, skipped);
        return OperationResult<JsonTableResult>.Ok(
            result,
            skipped > 0 ? $"{skipped:N0} element(s) were not objects and were left out." : null);
    }

    private static void Flatten(
        JsonObject obj, string prefix, Dictionary<int, string> record, Func<string, int> column, JsonTableOptions options)
    {
        foreach (var member in obj.Members)
        {
            var name = prefix.Length == 0 ? member.Name : prefix + options.Separator + member.Name;

            if (options.FlattenObjects && member.Value is JsonObject { Members.Count: > 0 } child)
            {
                Flatten(child, name, record, column, options);
                continue;
            }

            // A duplicate member keeps the last value, as every JSON consumer would.
            record[column(name)] = Cell(member.Value);
        }
    }

    private static string Cell(JsonNode node) => node switch
    {
        JsonNull => string.Empty,
        JsonString s => s.Value,
        JsonNumber n => n.Raw,
        JsonBool b => b.Value ? "true" : "false",
        _ => JsonWriter.Write(node, JsonWriterOptions.Compact),
    };

    /// <summary>Renders the table as text. CSV follows RFC 4180, quoting any cell that needs it.</summary>
    public static string ToText(JsonTableResult table, TableTextFormat format)
    {
        var output = new StringBuilder();

        switch (format)
        {
            case TableTextFormat.Markdown:
                AppendMarkdownRow(output, table.Columns);
                output.Append('|');
                foreach (var _ in table.Columns)
                {
                    output.Append(" --- |");
                }

                output.Append('\n');
                foreach (var row in table.Rows)
                {
                    AppendMarkdownRow(output, row);
                }

                break;

            default:
                var separator = format == TableTextFormat.Tsv ? '\t' : ',';
                AppendDelimitedRow(output, table.Columns, separator);
                foreach (var row in table.Rows)
                {
                    AppendDelimitedRow(output, row, separator);
                }

                break;
        }

        return output.ToString();
    }

    private static void AppendDelimitedRow(StringBuilder output, IReadOnlyList<string> cells, char separator)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                output.Append(separator);
            }

            var cell = cells[i];
            if (cell.AsSpan().IndexOfAny(['"', separator, '\n', '\r']) >= 0)
            {
                output.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                output.Append(cell);
            }
        }

        output.Append("\r\n");
    }

    private static void AppendMarkdownRow(StringBuilder output, IReadOnlyList<string> cells)
    {
        output.Append('|');
        foreach (var cell in cells)
        {
            output.Append(' ')
                .Append(cell.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r\n", "<br>", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal))
                .Append(" |");
        }

        output.Append('\n');
    }
}
