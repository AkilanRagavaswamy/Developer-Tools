using System.Text;

namespace DevTools.App.Controls;

/// <summary>
/// Builds the line-number gutter text for an editor.
/// <para>
/// The editors use a monospace font, which makes it possible to work out exactly how many
/// visual rows each logical line occupies when word wrap is on — greedy word wrapping over a
/// known column count — so the numbers stay aligned with their lines instead of drifting.
/// </para>
/// </summary>
public static class GutterBuilder
{
    /// <summary>
    /// Produces the gutter contents for <paramref name="text"/>. When <paramref name="wrap"/>
    /// is true, each logical line is followed by as many blank rows as it wraps onto.
    /// </summary>
    public static string Build(string? text, bool wrap, int columns)
    {
        var lines = Core.Text.TextUtil.SplitLines(text, keepTrailingEmpty: true);
        if (lines.Length == 0)
        {
            return "1";
        }

        var builder = new StringBuilder(lines.Length * 4);

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(i + 1);

            if (!wrap || columns <= 0)
            {
                continue;
            }

            var rows = CountWrappedRows(lines[i], columns);
            for (var r = 1; r < rows; r++)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// How many visual rows a single logical line occupies at <paramref name="columns"/> wide,
    /// wrapping greedily at spaces and breaking a word that is itself longer than the line.
    /// </summary>
    internal static int CountWrappedRows(string line, int columns)
    {
        if (columns <= 0 || line.Length <= columns)
        {
            return 1;
        }

        var rows = 1;
        var used = 0;
        var index = 0;

        while (index < line.Length)
        {
            // Take the next chunk: a run of spaces, or a word.
            var start = index;
            if (char.IsWhiteSpace(line[index]))
            {
                while (index < line.Length && char.IsWhiteSpace(line[index]))
                {
                    index++;
                }
            }
            else
            {
                while (index < line.Length && !char.IsWhiteSpace(line[index]))
                {
                    index++;
                }
            }

            var length = index - start;

            if (length > columns)
            {
                // A word longer than the line: it fills the current row and then breaks.
                var remaining = length;
                if (used > 0)
                {
                    remaining -= columns - used;
                    rows++;
                    used = 0;
                }

                while (remaining > columns)
                {
                    remaining -= columns;
                    rows++;
                }

                used = remaining;
            }
            else if (used + length > columns)
            {
                rows++;
                used = length;
            }
            else
            {
                used += length;
            }
        }

        return rows;
    }
}
