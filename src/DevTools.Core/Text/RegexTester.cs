using System.Text.RegularExpressions;

namespace DevTools.Core.Text;

/// <summary>The .NET regex flags the validator offers.</summary>
public sealed record RegexTestOptions
{
    public bool IgnoreCase { get; init; }

    /// <summary><c>^</c> and <c>$</c> match at every line, not only at the ends of the text.</summary>
    public bool Multiline { get; init; } = true;

    /// <summary><c>.</c> matches a line break too.</summary>
    public bool Singleline { get; init; }

    /// <summary>Whitespace in the pattern is ignored and <c>#</c> starts a comment.</summary>
    public bool IgnorePatternWhitespace { get; init; }

    /// <summary>Only named groups capture.</summary>
    public bool ExplicitCapture { get; init; }

    /// <summary>JavaScript-compatible behaviour, for a pattern headed for a browser.</summary>
    public bool EcmaScript { get; init; }

    /// <summary>When set, the replacement is run over the text as well.</summary>
    public string? Replacement { get; init; }

    /// <summary>The most time one evaluation may take before it is stopped.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    public static RegexTestOptions Default { get; } = new();

    internal RegexOptions ToRegexOptions()
    {
        var options = RegexOptions.None;

        if (IgnoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        if (Multiline)
        {
            options |= RegexOptions.Multiline;
        }

        // ECMAScript allows only IgnoreCase and Multiline beside it.
        if (EcmaScript)
        {
            return options | RegexOptions.ECMAScript;
        }

        if (Singleline)
        {
            options |= RegexOptions.Singleline;
        }

        if (IgnorePatternWhitespace)
        {
            options |= RegexOptions.IgnorePatternWhitespace;
        }

        if (ExplicitCapture)
        {
            options |= RegexOptions.ExplicitCapture;
        }

        return options;
    }
}

/// <summary>One group of one match. <see cref="Success"/> is false for a group that did not take part.</summary>
public sealed record RegexGroupResult(string Name, int Index, int Length, string Value, bool Success);

/// <summary>One match, with the line and column it starts at.</summary>
public sealed record RegexMatchResult(int Number, int Index, int Length, int Line, int Column, string Value, IReadOnlyList<RegexGroupResult> Groups);

public sealed record RegexTestResult(
    IReadOnlyList<RegexMatchResult> Matches,
    int TotalMatches,
    bool Truncated,
    IReadOnlyList<string> GroupNames,
    string? Replaced,
    TimeSpan Elapsed);

/// <summary>
/// Runs a regular expression over a text and reports what it matched.
/// </summary>
/// <remarks>
/// Every evaluation carries a timeout, so a pattern with catastrophic backtracking —
/// <c>(a+)+$</c> against a long run of <c>a</c>s — is stopped and reported instead of freezing
/// the app. A pattern that does not compile is reported with the offset .NET gives for it.
/// </remarks>
public static class RegexTester
{
    /// <summary>Matches beyond this are counted but not listed.</summary>
    public const int MaxListedMatches = 1_000;

    public static OperationResult<RegexTestResult> Test(string? pattern, string? input, RegexTestOptions? options = null)
    {
        var opts = options ?? RegexTestOptions.Default;
        input ??= string.Empty;

        if (string.IsNullOrEmpty(pattern))
        {
            return OperationResult<RegexTestResult>.Ok(new RegexTestResult([], 0, false, [], null, TimeSpan.Zero));
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, opts.ToRegexOptions(), opts.Timeout);
        }
        catch (RegexParseException ex)
        {
            return OperationResult<RegexTestResult>.Fail(new ToolError(
                $"The pattern is not valid: {Describe(ex.Error)}.", Offset: ex.Offset));
        }
        catch (ArgumentException ex)
        {
            return OperationResult<RegexTestResult>.Fail(ex.Message);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var names = regex.GetGroupNames().Where(static n => n != "0").ToList();
            var matches = new List<RegexMatchResult>();
            var total = 0;

            // Lines are counted forward from the last match rather than from the start each time.
            var line = 1;
            var lineStart = 0;
            var scanned = 0;

            for (var match = regex.Match(input); match.Success; match = match.NextMatch())
            {
                total++;

                if (matches.Count >= MaxListedMatches)
                {
                    continue;
                }

                for (; scanned < match.Index; scanned++)
                {
                    if (input[scanned] == '\n' || (input[scanned] == '\r' && (scanned + 1 >= input.Length || input[scanned + 1] != '\n')))
                    {
                        line++;
                        lineStart = scanned + 1;
                    }
                }

                var groups = new List<RegexGroupResult>(names.Count);
                foreach (var name in names)
                {
                    var group = match.Groups[name];
                    groups.Add(new RegexGroupResult(name, group.Index, group.Length, group.Value, group.Success));
                }

                matches.Add(new RegexMatchResult(
                    total, match.Index, match.Length, line, match.Index - lineStart + 1, match.Value, groups));
            }

            var replaced = opts.Replacement is null ? null : regex.Replace(input, opts.Replacement);

            return OperationResult<RegexTestResult>.Ok(new RegexTestResult(
                matches, total, total > matches.Count, names, replaced, watch.Elapsed));
        }
        catch (RegexMatchTimeoutException)
        {
            return OperationResult<RegexTestResult>.Fail(
                $"The pattern ran for more than {opts.Timeout.TotalSeconds:0} seconds and was stopped. " +
                "It probably backtracks catastrophically — look for nested quantifiers such as (a+)+.");
        }
    }

    private static string Describe(RegexParseError error)
    {
        // "UnterminatedBracket" reads better as "unterminated bracket".
        var name = error.ToString();
        var words = new System.Text.StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                words.Append(' ');
            }

            words.Append(char.ToLowerInvariant(name[i]));
        }

        return words.ToString();
    }
}
