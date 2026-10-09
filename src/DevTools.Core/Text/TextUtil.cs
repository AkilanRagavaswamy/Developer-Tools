using System.Globalization;
using System.Text;

namespace DevTools.Core.Text;

/// <summary>The newline convention a document uses.</summary>
public enum NewlineStyle
{
    /// <summary>No newline was found.</summary>
    None,
    Lf,
    CrLf,
    Cr,
    Mixed,
}

/// <summary>
/// Text normalisation shared by every engine. These handle the global edge cases in
/// <c>Docs/01 §6</c> — BOMs, mixed line endings, grapheme-correct counting — once, so no
/// individual tool has to re-derive them.
/// </summary>
public static class TextUtil
{
    public const char Bom = '﻿';

    /// <summary>True when the text is null, empty or only white space (edge cases 1 and 2).</summary>
    public static bool IsBlank(string? text) => string.IsNullOrWhiteSpace(text);

    /// <summary>Removes a leading byte-order mark if present.</summary>
    public static string StripBom(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text[0] == Bom ? text[1..] : text;
    }

    public static bool HasBom(string? text) => !string.IsNullOrEmpty(text) && text[0] == Bom;

    /// <summary>Rewrites every CR, LF and CRLF to a single LF so parsers see one convention.</summary>
    public static string NormalizeNewlines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                builder.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Rewrites every newline in <paramref name="text"/> to <paramref name="style"/>.</summary>
    public static string ApplyNewlines(string? text, NewlineStyle style)
    {
        var normalized = NormalizeNewlines(text);
        return style switch
        {
            NewlineStyle.CrLf => normalized.Replace("\n", "\r\n", StringComparison.Ordinal),
            NewlineStyle.Cr => normalized.Replace("\n", "\r", StringComparison.Ordinal),
            _ => normalized,
        };
    }

    /// <summary>Reports which newline convention the text uses, so round trips can preserve it.</summary>
    public static NewlineStyle DetectNewline(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return NewlineStyle.None;
        }

        var crlf = false;
        var lf = false;
        var cr = false;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf = true;
                    i++;
                }
                else
                {
                    cr = true;
                }
            }
            else if (text[i] == '\n')
            {
                lf = true;
            }
        }

        var kinds = (crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0);
        if (kinds == 0)
        {
            return NewlineStyle.None;
        }

        if (kinds > 1)
        {
            return NewlineStyle.Mixed;
        }

        return crlf ? NewlineStyle.CrLf : lf ? NewlineStyle.Lf : NewlineStyle.Cr;
    }

    /// <summary>Splits into lines after normalising, without allocating a trailing empty entry for a final newline.</summary>
    public static string[] SplitLines(string? text, bool keepTrailingEmpty = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var normalized = NormalizeNewlines(StripBom(text));
        var lines = normalized.Split('\n');

        if (!keepTrailingEmpty && lines.Length > 1 && lines[^1].Length == 0)
        {
            return lines[..^1];
        }

        return lines;
    }

    /// <summary>
    /// The number of entries <see cref="SplitLines"/> would return, without building them.
    /// </summary>
    /// <remarks>
    /// Editors count lines on every keystroke; splitting a large document into strings just to
    /// read the array's length costs two full copies of it each time.
    /// </remarks>
    public static int CountLines(string? text, bool keepTrailingEmpty = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var span = text.AsSpan();
        if (span[0] == Bom)
        {
            span = span[1..];
        }

        if (span.IsEmpty)
        {
            return 1;
        }

        var lines = 1;
        var at = 0;
        while (true)
        {
            var next = span[at..].IndexOfAny('\r', '\n');
            if (next < 0)
            {
                break;
            }

            at += next;
            at += span[at] == '\r' && at + 1 < span.Length && span[at + 1] == '\n' ? 2 : 1;
            lines++;

            if (at >= span.Length)
            {
                // The text ends in a newline, which leaves one empty entry after it.
                return keepTrailingEmpty || lines == 1 ? lines : lines - 1;
            }
        }

        return lines;
    }

    /// <summary>
    /// Counts user-perceived characters. An emoji with a skin-tone modifier is one grapheme
    /// but several UTF-16 units, and users count the former (edge case 7).
    /// </summary>
    public static int GraphemeCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        // Plain ASCII has one grapheme per character, except that CR LF is a single one. The
        // counter runs on every keystroke, and the general enumerator is far slower than this.
        var span = text.AsSpan();
        if (!span.ContainsAnyExceptInRange('\0', '\u007f'))
        {
            var pairs = 0;
            var at = 0;
            int found;
            while ((found = span[at..].IndexOf("\r\n")) >= 0)
            {
                pairs++;
                at += found + 2;
            }

            return text.Length - pairs;
        }

        var count = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            count++;
        }

        return count;
    }

    /// <summary>Converts a zero-based character offset into a one-based line and column.</summary>
    public static (int Line, int Column) OffsetToLineColumn(string? text, int offset)
    {
        if (string.IsNullOrEmpty(text) || offset <= 0)
        {
            return (1, 1);
        }

        var limit = Math.Min(offset, text.Length);
        var line = 1;
        var column = 1;

        for (var i = 0; i < limit; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else if (text[i] == '\r')
            {
                if (i + 1 < limit && text[i + 1] == '\n')
                {
                    continue;
                }

                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
    }

    /// <summary>Repeats a single indent unit, used by every formatter.</summary>
    public static string Indent(IndentStyle style, int level)
    {
        if (level <= 0)
        {
            return string.Empty;
        }

        // Formatters ask for an indent on every line, so the common depths are built once.
        var cache = IndentCache[style switch { IndentStyle.Tab => 2, IndentStyle.FourSpaces => 1, _ => 0 }];
        if (level < cache.Length)
        {
            return cache[level] ??= BuildIndent(style, level);
        }

        return BuildIndent(style, level);
    }

    private static readonly string?[][] IndentCache = [new string?[64], new string?[64], new string?[64]];

    private static string BuildIndent(IndentStyle style, int level) => style switch
    {
        IndentStyle.Tab => new string('\t', level),
        IndentStyle.FourSpaces => new string(' ', level * 4),
        _ => new string(' ', level * 2),
    };

    /// <summary>The literal text of one indent unit.</summary>
    public static string IndentUnit(IndentStyle style) => style switch
    {
        IndentStyle.Tab => "\t",
        IndentStyle.FourSpaces => "    ",
        _ => "  ",
    };

    /// <summary>Truncates for display in an error message, with an ellipsis.</summary>
    public static string Ellipsize(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text ?? string.Empty;
        }

        return string.Concat(text.AsSpan(0, Math.Max(0, maxLength - 1)), "…");
    }
}

/// <summary>Indent width offered by every formatter.</summary>
public enum IndentStyle
{
    TwoSpaces,
    FourSpaces,
    Tab,
}
