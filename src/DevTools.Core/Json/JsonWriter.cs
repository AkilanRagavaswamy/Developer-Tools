using System.Text;
using DevTools.Core.Text;

namespace DevTools.Core.Json;

/// <summary>How a <see cref="JsonNode"/> tree is rendered back to text.</summary>
public sealed record JsonWriterOptions
{
    /// <summary>Emit with no insignificant whitespace at all.</summary>
    public bool Minify { get; init; }

    /// <summary>Indent unit used when not minifying.</summary>
    public IndentStyle IndentStyle { get; init; } = IndentStyle.TwoSpaces;

    /// <summary>Sorts the members of every object by ordinal name, recursively.</summary>
    public bool SortKeys { get; init; }

    /// <summary>Escapes every non-ASCII character as <c>\uXXXX</c>.</summary>
    public bool EscapeNonAscii { get; init; }

    public static JsonWriterOptions Pretty { get; } = new();

    public static JsonWriterOptions Compact { get; } = new() { Minify = true };
}

/// <summary>Renders a <see cref="JsonNode"/> tree back to JSON text.</summary>
public static class JsonWriter
{
    public static string Write(JsonNode node, JsonWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(node);

        var opts = options ?? JsonWriterOptions.Pretty;
        var builder = new StringBuilder();
        WriteNode(builder, node, opts, 0);
        return builder.ToString();
    }

    private static void WriteNode(StringBuilder builder, JsonNode node, JsonWriterOptions options, int depth)
    {
        switch (node)
        {
            case JsonNull:
                builder.Append("null");
                break;

            case JsonBool b:
                builder.Append(b.Value ? "true" : "false");
                break;

            // The number is re-emitted from its captured source text, never reformatted. This
            // is what keeps 1.0, 1e10 and 30-digit integers intact (FR-J05).
            case JsonNumber n:
                builder.Append(n.Raw);
                break;

            case JsonString s:
                WriteString(builder, s.Value, options);
                break;

            case JsonArray a:
                WriteArray(builder, a, options, depth);
                break;

            case JsonObject o:
                WriteObject(builder, o, options, depth);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(node), node.Kind, "Unknown JSON node kind.");
        }
    }

    private static void WriteArray(StringBuilder builder, JsonArray array, JsonWriterOptions options, int depth)
    {
        if (array.Items.Count == 0)
        {
            builder.Append("[]");
            return;
        }

        builder.Append('[');

        for (var i = 0; i < array.Items.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            NewlineAndIndent(builder, options, depth + 1);
            WriteNode(builder, array.Items[i], options, depth + 1);
        }

        NewlineAndIndent(builder, options, depth);
        builder.Append(']');
    }

    private static void WriteObject(StringBuilder builder, JsonObject obj, JsonWriterOptions options, int depth)
    {
        if (obj.Members.Count == 0)
        {
            builder.Append("{}");
            return;
        }

        var members = options.SortKeys
            ? obj.Members.OrderBy(static m => m.Name, StringComparer.Ordinal).ToList()
            : obj.Members;

        builder.Append('{');

        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            NewlineAndIndent(builder, options, depth + 1);
            WriteString(builder, members[i].Name, options);
            builder.Append(':');

            if (!options.Minify)
            {
                builder.Append(' ');
            }

            WriteNode(builder, members[i].Value, options, depth + 1);
        }

        NewlineAndIndent(builder, options, depth);
        builder.Append('}');
    }

    private static void NewlineAndIndent(StringBuilder builder, JsonWriterOptions options, int depth)
    {
        if (options.Minify)
        {
            return;
        }

        builder.Append('\n').Append(TextUtil.Indent(options.IndentStyle, depth));
    }

    internal static void WriteString(StringBuilder builder, string value, JsonWriterOptions options)
    {
        builder.Append('"');

        // The usual string needs no escaping at all, and appending it whole is far cheaper
        // than appending it a character at a time.
        if (!NeedsEscaping(value, options.EscapeNonAscii))
        {
            builder.Append(value).Append('"');
            return;
        }

        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(c) || (options.EscapeNonAscii && c > 0x7F))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    private static bool NeedsEscaping(string value, bool escapeNonAscii)
    {
        foreach (var c in value)
        {
            if (c < ' ' || c is '"' or '\\' || (c >= '\u007f' && (escapeNonAscii || c <= '\u009f')))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Renders a node as a single-line string, for diff cells and error messages.</summary>
    public static string WriteInline(JsonNode node, int maxLength = 120)
    {
        var text = Write(node, JsonWriterOptions.Compact);
        return TextUtil.Ellipsize(text, maxLength);
    }

    /// <summary>A string as JSON would write it: quoted, with the escapes it needs.</summary>
    /// <remarks>
    /// Public so that anything rendering JSON by hand — a member name in the side-by-side diff,
    /// for one — escapes it the same way the writer does, rather than adding quotes and hoping
    /// the name has no backslash in it.
    /// </remarks>
    public static string Quote(string value)
    {
        var builder = new StringBuilder();
        WriteString(builder, value ?? string.Empty, JsonWriterOptions.Pretty);
        return builder.ToString();
    }
}
