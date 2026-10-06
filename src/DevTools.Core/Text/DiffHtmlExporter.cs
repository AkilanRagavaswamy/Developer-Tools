using System.Globalization;

using System.Text;
using DevTools.Core.Json;

namespace DevTools.Core.Text;

/// <summary>
/// Writes a side-by-side diff as one self-contained HTML page — inline styles, no script, no
/// external resources — so it can be mailed, attached to a ticket or opened in any browser.
/// </summary>
public static class DiffHtmlExporter
{
    private enum Side
    {
        Same,
        Removed,
        Added,
        Changed,
        Missing,
    }

    /// <summary>A text comparison, with the words that changed in a changed line marked.</summary>
    public static string FromText(TextDiffResult diff, string leftTitle, string rightTitle, string summary)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var rows = new StringBuilder();

        foreach (var segment in diff.Segments)
        {
            var (leftHtml, rightHtml) = segment.Kind == DiffChangeKind.Modified
                ? WordHtml(segment)
                : (Encode(segment.LeftText), Encode(segment.RightText));

            var (left, right) = segment.Kind switch
            {
                DiffChangeKind.Deleted => (Side.Removed, Side.Missing),
                DiffChangeKind.Inserted => (Side.Missing, Side.Added),
                DiffChangeKind.Modified => (Side.Removed, Side.Added),
                _ => (Side.Same, Side.Same),
            };

            AppendRow(rows, segment.LeftLineNumber, leftHtml, left, segment.RightLineNumber, rightHtml, right);
        }

        return Page(leftTitle, rightTitle, summary, rows.ToString());
    }

    /// <summary>A JSON comparison laid out by <see cref="JsonDiffLayoutBuilder"/>.</summary>
    public static string FromJson(JsonDiffLayout layout, string leftTitle, string rightTitle)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var rows = new StringBuilder();

        foreach (var row in layout.Rows)
        {
            var leftMissing = row.LeftLine is null && row.IsDifference;
            var rightMissing = row.RightLine is null && row.IsDifference;

            var kind = row.Category switch
            {
                JsonDiffCategory.MissingProperty => leftMissing ? Side.Added : Side.Removed,
                JsonDiffCategory.IncorrectType or JsonDiffCategory.UnequalValue => Side.Changed,
                _ => Side.Same,
            };

            AppendRow(
                rows,
                row.LeftLine, Encode(row.LeftText), leftMissing ? Side.Missing : kind,
                row.RightLine, Encode(row.RightText), rightMissing ? Side.Missing : kind);
        }

        return Page(leftTitle, rightTitle, layout.Summary, rows.ToString());
    }

    private static void AppendRow(StringBuilder rows, int? leftLine, string left, Side leftSide, int? rightLine, string right, Side rightSide)
    {
        rows.Append("<tr>")
            .Append(CultureInfo.InvariantCulture, $"<td class=\"n\">{leftLine}</td><td class=\"{Css(leftSide)}\">{left}</td>")
            .Append(CultureInfo.InvariantCulture, $"<td class=\"n\">{rightLine}</td><td class=\"{Css(rightSide)}\">{right}</td>")
            .Append("</tr>\n");
    }

    private static string Css(Side side) => side switch
    {
        Side.Removed => "c del",
        Side.Added => "c add",
        Side.Changed => "c chg",
        Side.Missing => "c gap",
        _ => "c",
    };

    private static (string Left, string Right) WordHtml(DiffSegment segment)
    {
        var left = new StringBuilder();
        var right = new StringBuilder();
        var leftText = new StringBuilder();
        var rightText = new StringBuilder();

        foreach (var run in segment.WordDiff)
        {
            var html = Encode(run.Text);
            switch (run.Kind)
            {
                case DiffChangeKind.Deleted:
                    left.Append("<mark class=\"wd\">").Append(html).Append("</mark>");
                    leftText.Append(run.Text);
                    break;
                case DiffChangeKind.Inserted:
                    right.Append("<mark class=\"wa\">").Append(html).Append("</mark>");
                    rightText.Append(run.Text);
                    break;
                default:
                    left.Append(html);
                    right.Append(html);
                    leftText.Append(run.Text);
                    rightText.Append(run.Text);
                    break;
            }
        }

        // If the runs do not rebuild the lines exactly, fall back to the plain lines.
        return leftText.ToString() == segment.LeftText && rightText.ToString() == segment.RightText
            ? (left.ToString(), right.ToString())
            : (Encode(segment.LeftText), Encode(segment.RightText));
    }

    private static string Encode(string? text) => Codecs.HtmlCodec.Encode(text ?? string.Empty, encodeNonAscii: false);

    private static string Page(string leftTitle, string rightTitle, string summary, string rows) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8">
        <title>{{Encode(leftTitle)}} ↔ {{Encode(rightTitle)}}</title>
        <style>
        :root { color-scheme: light dark; --bg:#fff; --fg:#1f2328; --mute:#6e7781; --line:#d0d7de;
          --del:rgba(239,68,68,.16); --add:rgba(34,197,94,.18); --chg:rgba(245,158,11,.20); --gap:rgba(127,127,127,.08);
          --wd:rgba(239,68,68,.45); --wa:rgba(34,197,94,.50); }
        @media (prefers-color-scheme: dark) { :root { --bg:#0d1117; --fg:#e6edf3; --mute:#8d96a0; --line:#30363d; } }
        body { margin: 0; padding: 24px; background: var(--bg); color: var(--fg); font: 14px/1.5 "Segoe UI", system-ui, sans-serif; }
        h1 { font-size: 18px; margin: 0 0 4px; } p { margin: 0 0 16px; color: var(--mute); }
        table { width: 100%; border-collapse: collapse; table-layout: fixed; font: 12.5px/1.45 "Cascadia Mono", Consolas, monospace; border: 1px solid var(--line); }
        th { text-align: left; padding: 6px 10px; border-bottom: 1px solid var(--line); font: 600 13px "Segoe UI", sans-serif; }
        td { vertical-align: top; padding: 1px 8px; white-space: pre-wrap; word-break: break-all; }
        td.n { width: 48px; text-align: right; color: var(--mute); user-select: none; }
        td.n + td.c + td.n { border-left: 1px solid var(--line); }
        .del { background: var(--del); } .add { background: var(--add); } .chg { background: var(--chg); } .gap { background: var(--gap); }
        mark { color: inherit; border-radius: 2px; } mark.wd { background: var(--wd); } mark.wa { background: var(--wa); }
        footer { margin-top: 14px; color: var(--mute); font-size: 12px; }
        </style></head><body>
        <h1>{{Encode(leftTitle)}} ↔ {{Encode(rightTitle)}}</h1>
        <p>{{Encode(summary)}}</p>
        <table>
        <colgroup><col style="width:48px"><col><col style="width:48px"><col></colgroup>
        <thead><tr><th colspan="2">{{Encode(leftTitle)}}</th><th colspan="2">{{Encode(rightTitle)}}</th></tr></thead>
        <tbody>
        {{rows}}</tbody>
        </table>
        <footer>Exported from ForgeKitRk.</footer>
        </body></html>
        """;
}
