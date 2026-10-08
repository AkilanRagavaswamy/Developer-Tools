using System.Text.RegularExpressions;

namespace DevTools.Core.Scratch;

/// <summary>The one language a scratch note is written in (decision: one per note, no blocks).</summary>
public enum ScratchLanguage
{
    Plain,
    Markdown,
    Json,
    Xml,
    Sql,
    CSharp,
    Html,
    Math,
}

/// <summary>Names, titles and file details for scratch notes. Pure, so it can be tested.</summary>
public static class ScratchNotes
{
    public const int MaxTitleLength = 60;

    public const int MaxPreviewLength = 120;

    /// <summary>Notes above this stay editable but lose live maths (SP-15).</summary>
    public const int LargeNoteChars = 2 * 1024 * 1024;

    /// <summary>The largest text a note will take, from a paste or a file (SP-07, edge case 6).</summary>
    public const int MaxNoteBytes = 10 * 1024 * 1024;

    public static IReadOnlyList<ScratchLanguage> Languages { get; } = Enum.GetValues<ScratchLanguage>();

    public static string DisplayName(ScratchLanguage language) => language switch
    {
        ScratchLanguage.Plain => "Plain text",
        ScratchLanguage.Markdown => "Markdown",
        ScratchLanguage.Json => "JSON",
        ScratchLanguage.Xml => "XML",
        ScratchLanguage.Sql => "SQL",
        ScratchLanguage.CSharp => "C#",
        ScratchLanguage.Html => "HTML",
        ScratchLanguage.Math => "Math",
        _ => language.ToString(),
    };

    /// <summary>The extension a note is exported with.</summary>
    public static string Extension(ScratchLanguage language) => language switch
    {
        ScratchLanguage.Markdown => ".md",
        ScratchLanguage.Json => ".json",
        ScratchLanguage.Xml => ".xml",
        ScratchLanguage.Sql => ".sql",
        ScratchLanguage.CSharp => ".cs",
        ScratchLanguage.Html => ".html",
        _ => ".txt",
    };

    /// <summary>The language a file most likely holds, from its extension.</summary>
    public static ScratchLanguage FromExtension(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".md" or ".markdown" => ScratchLanguage.Markdown,
        ".json" => ScratchLanguage.Json,
        ".xml" or ".xaml" or ".config" or ".csproj" => ScratchLanguage.Xml,
        ".sql" => ScratchLanguage.Sql,
        ".cs" => ScratchLanguage.CSharp,
        ".html" or ".htm" => ScratchLanguage.Html,
        _ => ScratchLanguage.Plain,
    };

    /// <summary>
    /// The title a note shows until it is renamed: its first non-empty line, trimmed (SP-03).
    /// Markdown heading marks and comment markers are dropped so "# Plan" reads as "Plan".
    /// </summary>
    public static string? DeriveTitle(string? text)
    {
        // The first line with a word in it: a formatted JSON note starts with "{", which says nothing.
        var rest = text;
        for (var i = 0; i < 50 && FirstNonEmptyLine(rest, out var end) is { } line; i++)
        {
            line = line.TrimStart('#', '/', '*', '-', '>', ' ', '\t').Trim();
            if (line.Any(char.IsLetterOrDigit))
            {
                return Truncate(line, MaxTitleLength);
            }

            rest = rest![end..];
        }

        return null;
    }

    /// <summary>What the note list shows under the title: the text after the first line, on one line.</summary>
    public static string Preview(string? text)
    {
        if (FirstNonEmptyLine(text, out var end) is null)
        {
            return string.Empty;
        }

        var rest = text!.AsSpan(end);
        var builder = new System.Text.StringBuilder(MaxPreviewLength + 1);
        var lastWasSpace = true;

        foreach (var c in rest)
        {
            if (builder.Length > MaxPreviewLength)
            {
                break;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
        }

        return Truncate(builder.ToString().Trim(), MaxPreviewLength);
    }

    /// <summary>True for a note with nothing but whitespace in it (edge case 9).</summary>
    public static bool IsEmpty(string? text) => string.IsNullOrWhiteSpace(text);

    private static string? FirstNonEmptyLine(string? text, out int end)
    {
        end = 0;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOfAny(['\r', '\n'], start);
            var stop = newline < 0 ? text.Length : newline;
            var line = text.AsSpan(start, stop - start).Trim();

            if (!line.IsEmpty)
            {
                end = newline < 0 ? text.Length : SkipLineBreak(text, newline);
                return line.ToString();
            }

            if (newline < 0)
            {
                break;
            }

            start = SkipLineBreak(text, newline);
        }

        return null;
    }

    /// <summary>The index just past the line break at <paramref name="at"/>, treating CR LF as one break.</summary>
    private static int SkipLineBreak(string text, int at) =>
        text[at] == '\r' && at + 1 < text.Length && text[at + 1] == '\n' ? at + 2 : at + 1;

    /// <summary>
    /// The text with every line break as <paramref name="newline"/>. WinUI's text box uses a bare
    /// CR and files on Windows use CR LF; comparing or storing one against the other must not
    /// look like an edit.
    /// </summary>
    public static string NormalizeLineEndings(string? text, string newline = "\r\n") =>
        string.IsNullOrEmpty(text) ? string.Empty : text.ReplaceLineEndings(newline);

    /// <summary>True when two texts differ only in how their lines end.</summary>
    public static bool SameText(string? a, string? b) =>
        string.Equals(NormalizeLineEndings(a, "\n"), NormalizeLineEndings(b, "\n"), StringComparison.Ordinal);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";
}

/// <summary>
/// Spots text in a note that looks like a credential, so the user can be told that notes are
/// stored unencrypted (SP-60). It only warns; it never changes or hides the text.
/// </summary>
public static class ScratchSecrets
{
    private static readonly (string Label, Regex Pattern)[] Patterns =
    [
        ("a private key", Make(@"-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----")),
        ("a JSON Web Token", Make(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")),
        ("an AWS access key", Make(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")),
        ("a GitHub token", Make(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,})\b")),
        ("a Slack token", Make(@"\bxox[abprs]-[A-Za-z0-9-]{10,}")),
        ("a bearer token", Make(@"\bBearer\s+[A-Za-z0-9\-._~+/]{20,}=*")),
        ("a connection-string password", Make(@"(?i)\b(?:password|pwd)\s*=\s*[^;\s]{4,}")),
        ("a password", Make(@"(?i)""?(?:password|passwd|secret|api[_-]?key|client[_-]?secret)""?\s*[:=]\s*""?[^\s""]{6,}")),
    ];

    /// <summary>Text above this is not scanned; the warning is a courtesy, not a scanner.</summary>
    private const int MaxScanChars = 1024 * 1024;

    /// <summary>What the first secret-looking text in the note appears to be, or <see langword="null"/>.</summary>
    public static string? Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var scan = text.Length > MaxScanChars ? text[..MaxScanChars] : text;

        foreach (var (label, pattern) in Patterns)
        {
            try
            {
                if (pattern.IsMatch(scan))
                {
                    return label;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological note is not worth stalling on.
            }
        }

        return null;
    }

    private static Regex Make(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
}

/// <summary>One stored snapshot as the retention policy sees it.</summary>
public readonly record struct ScratchSnapshotInfo(string Key, DateTime CreatedUtc, long Bytes);

/// <summary>
/// How long Scratchpad keeps what it no longer shows (SP-08, SP-35). Kept apart from the file
/// handling so the rules can be tested without a disk.
/// </summary>
public static class ScratchRetention
{
    public const int MinDays = 1;

    public const int MaxDays = 60;

    public const int DefaultDays = 20;

    /// <summary>Snapshots kept per note, however recent.</summary>
    public const int MaxSnapshotsPerNote = 100;

    /// <summary>The most the Scratchpad folder may hold before the oldest history goes.</summary>
    public const long MaxTotalBytes = 500L * 1024 * 1024;

    public static int ClampDays(int days) => Math.Clamp(days, MinDays, MaxDays);

    /// <summary>True when a note deleted at <paramref name="deletedUtc"/> has been in Trash long enough.</summary>
    public static bool IsTrashExpired(DateTime deletedUtc, DateTime nowUtc, int days) =>
        nowUtc - deletedUtc >= TimeSpan.FromDays(ClampDays(days));

    /// <summary>
    /// The snapshots of one note that should go: those older than the retention period, and
    /// any beyond the newest <see cref="MaxSnapshotsPerNote"/>.
    /// </summary>
    public static IReadOnlyList<ScratchSnapshotInfo> SelectExpired(
        IEnumerable<ScratchSnapshotInfo> snapshots, DateTime nowUtc, int days)
    {
        var cutoff = nowUtc - TimeSpan.FromDays(ClampDays(days));
        var expired = new List<ScratchSnapshotInfo>();
        var kept = 0;

        foreach (var snapshot in snapshots.OrderByDescending(s => s.CreatedUtc))
        {
            if (snapshot.CreatedUtc < cutoff || kept >= MaxSnapshotsPerNote)
            {
                expired.Add(snapshot);
            }
            else
            {
                kept++;
            }
        }

        return expired;
    }

    /// <summary>
    /// When the folder is over its size cap, the oldest snapshots across every note, in the
    /// order to delete them, until it fits. Notes themselves are never on this list.
    /// </summary>
    public static IReadOnlyList<ScratchSnapshotInfo> SelectOverCap(
        IEnumerable<ScratchSnapshotInfo> snapshots, long notesBytes, long capBytes = MaxTotalBytes)
    {
        var all = snapshots.OrderBy(s => s.CreatedUtc).ToList();
        var total = notesBytes + all.Sum(s => s.Bytes);
        var remove = new List<ScratchSnapshotInfo>();

        foreach (var snapshot in all)
        {
            if (total <= capBytes)
            {
                break;
            }

            remove.Add(snapshot);
            total -= snapshot.Bytes;
        }

        return remove;
    }
}
