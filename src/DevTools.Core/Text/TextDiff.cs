using System.Globalization;
using System.Text;

namespace DevTools.Core.Text;

/// <summary>The unit a comparison is expressed in.</summary>
public enum DiffGranularity
{
    /// <summary>Whole lines — the granularity a side-by-side file diff uses.</summary>
    Line,

    /// <summary>Words, whitespace runs and punctuation — for prose.</summary>
    Word,

    /// <summary>Single user-perceived characters (grapheme clusters, so emoji stay whole).</summary>
    Character,
}

/// <summary>What happened to one unit of text between the left and right documents.</summary>
public enum DiffChangeKind
{
    /// <summary>Present on both sides and equal under the configured options.</summary>
    Unchanged,

    /// <summary>Present only on the right side.</summary>
    Inserted,

    /// <summary>Present only on the left side.</summary>
    Deleted,

    /// <summary>A deletion paired with an insertion, so both sides carry text.</summary>
    Modified,
}

/// <summary>
/// How <see cref="TextDiff.Compare"/> compares two documents. The defaults reproduce what a
/// developer expects from <c>diff -u</c>: exact line comparison with three lines of context.
/// </summary>
public sealed record TextDiffOptions
{
    /// <summary>The unit the segments and inline runs are expressed in.</summary>
    public DiffGranularity Granularity { get; init; } = DiffGranularity.Line;

    /// <summary>Compares text case-insensitively, so <c>Value</c> and <c>value</c> match.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>Removes every whitespace character before comparing, so re-indentation is not a change.</summary>
    public bool IgnoreAllWhitespace { get; init; }

    /// <summary>Trims each unit before comparing, so trailing spaces are not a change. Ignored when <see cref="IgnoreAllWhitespace"/> is set.</summary>
    public bool IgnoreLeadingAndTrailingWhitespace { get; init; }

    /// <summary>Drops blank lines from the comparison and from the result, so added spacing is not a change. Line granularity only.</summary>
    public bool IgnoreBlankLines { get; init; }

    /// <summary>Unchanged lines kept either side of a hunk in <see cref="TextDiffResult.UnifiedDiff"/>.</summary>
    public int ContextLines { get; init; } = 3;

    /// <summary>Pairs a deletion with the insertion that replaced it into one <see cref="DiffChangeKind.Modified"/> segment.</summary>
    public bool PairModifiedLines { get; init; } = true;

    /// <summary>The name written on the <c>---</c> header line of the unified diff.</summary>
    public string LeftLabel { get; init; } = "original";

    /// <summary>The name written on the <c>+++</c> header line of the unified diff.</summary>
    public string RightLabel { get; init; } = "modified";

    /// <summary>The options a caller should use when it has no reason to deviate.</summary>
    public static TextDiffOptions Default { get; } = new();
}

/// <summary>
/// One row of the side-by-side view. A row carries the text of whichever sides take part in it
/// and the one-based line number on each side, which is <c>null</c> where that side has no line.
/// </summary>
public sealed record DiffSegment(
    DiffChangeKind Kind,
    string? LeftText,
    string? RightText,
    int? LeftLineNumber,
    int? RightLineNumber,
    IReadOnlyList<DiffRun> WordDiff);

/// <summary>
/// One run of the inline view: a stretch of text that is wholly unchanged, wholly inserted or
/// wholly deleted, in reading order.
/// </summary>
public sealed record DiffRun(
    DiffChangeKind Kind,
    string Text,
    int? LeftLineNumber,
    int? RightLineNumber);

/// <summary>
/// The headline numbers a diff UI shows above the panes. Counts are always in lines, whatever
/// granularity the segments use, and <c>SimilarityPercent</c> counts an unchanged line in full and
/// a modified line as half.
/// </summary>
public sealed record DiffStatistics(
    int LinesAdded,
    int LinesRemoved,
    int LinesModified,
    int LinesUnchanged,
    double SimilarityPercent)
{
    /// <summary>Everything that is not an unchanged line.</summary>
    public int TotalChanges => LinesAdded + LinesRemoved + LinesModified;
}

/// <summary>Everything the diff UI needs from a single comparison, computed once.</summary>
public sealed record TextDiffResult(
    DiffGranularity Granularity,
    IReadOnlyList<DiffSegment> Segments,
    IReadOnlyList<DiffRun> InlineRuns,
    string UnifiedDiff,
    DiffStatistics Statistics,
    bool AreIdentical,
    bool OnlyLineEndingsDiffer,
    NewlineStyle LeftNewline,
    NewlineStyle RightNewline,
    bool LeftEndsWithNewline,
    bool RightEndsWithNewline,
    bool IsApproximate);

/// <summary>
/// Compares two documents with a minimal edit script (Myers' O(ND) algorithm in linear space)
/// and shapes the result for a side-by-side view, an inline view and a unified-diff export.
/// </summary>
public static class TextDiff
{
    /// <summary>
    /// Work budget for one middle-snake search, expressed as edit-distance steps multiplied by
    /// sequence length. Beyond it the search gives up on minimality for that region rather than
    /// letting a pathological pair of 50,000-line files run for minutes.
    /// </summary>
    private const long BisectBudget = 50_000_000L;

    /// <summary>Similarity of two documents that are both empty.</summary>
    private const double EmptyPairSimilarity = 100d;

    /// <summary>
    /// Compares <paramref name="left"/> with <paramref name="right"/> and returns the segments,
    /// inline runs, unified diff and counts in one pass.
    /// </summary>
    public static OperationResult<TextDiffResult> Compare(
        string? left,
        string? right,
        TextDiffOptions? options = null)
    {
        var opts = options ?? TextDiffOptions.Default;

        if (opts.ContextLines < 0)
        {
            return OperationResult<TextDiffResult>.Fail(
                $"Context lines must be zero or greater; received {opts.ContextLines.ToString(CultureInfo.InvariantCulture)}.");
        }

        var leftBytes = Encoding.UTF8.GetByteCount(left ?? string.Empty);
        if (leftBytes > Limits.MaxInputBytes)
        {
            return OperationResult<TextDiffResult>.Fail(
                $"The left input is {Limits.Describe(leftBytes)}, which exceeds the {Limits.Describe(Limits.MaxInputBytes)} input limit (Limits.MaxInputBytes).");
        }

        var rightBytes = Encoding.UTF8.GetByteCount(right ?? string.Empty);
        if (rightBytes > Limits.MaxInputBytes)
        {
            return OperationResult<TextDiffResult>.Fail(
                $"The right input is {Limits.Describe(rightBytes)}, which exceeds the {Limits.Describe(Limits.MaxInputBytes)} input limit (Limits.MaxInputBytes).");
        }

        var leftStripped = TextUtil.StripBom(left);
        var rightStripped = TextUtil.StripBom(right);
        var leftNewline = TextUtil.DetectNewline(leftStripped);
        var rightNewline = TextUtil.DetectNewline(rightStripped);
        var leftNormalized = TextUtil.NormalizeNewlines(leftStripped);
        var rightNormalized = TextUtil.NormalizeNewlines(rightStripped);

        var onlyLineEndingsDiffer =
            !string.Equals(leftStripped, rightStripped, StringComparison.Ordinal) &&
            string.Equals(leftNormalized, rightNormalized, StringComparison.Ordinal);

        var leftLines = SplitIntoLines(leftNormalized, out var leftEndsWithNewline);
        var rightLines = SplitIntoLines(rightNormalized, out var rightEndsWithNewline);

        if (leftLines.Length > Limits.MaxDiffLines)
        {
            return OperationResult<TextDiffResult>.Fail(
                $"The left input has {leftLines.Length.ToString(CultureInfo.InvariantCulture)} lines, which exceeds the diff limit of {Limits.MaxDiffLines.ToString(CultureInfo.InvariantCulture)} lines (Limits.MaxDiffLines).");
        }

        if (rightLines.Length > Limits.MaxDiffLines)
        {
            return OperationResult<TextDiffResult>.Fail(
                $"The right input has {rightLines.Length.ToString(CultureInfo.InvariantCulture)} lines, which exceeds the diff limit of {Limits.MaxDiffLines.ToString(CultureInfo.InvariantCulture)} lines (Limits.MaxDiffLines).");
        }

        var context = new DiffContext();

        // The unified diff and the statistics are always line based, whatever granularity the
        // segments use, because a hunk header only means something in lines.
        var leftLineTokens = BuildLineTokens(leftLines, opts);
        var rightLineTokens = BuildLineTokens(rightLines, opts);
        var lineOps = Diff(leftLineTokens, rightLineTokens, opts, context);
        var lineSegments = BuildSegments(lineOps, leftLineTokens, rightLineTokens, opts, opts.Granularity == DiffGranularity.Line, context);
        var statistics = ComputeStatistics(lineSegments);
        var unified = BuildUnifiedDiff(lineOps, leftLineTokens, rightLineTokens, opts, leftLines.Length, rightLines.Length, leftEndsWithNewline, rightEndsWithNewline);

        IReadOnlyList<DiffSegment> segments;
        IReadOnlyList<DiffRun> inlineRuns;

        if (opts.Granularity == DiffGranularity.Line)
        {
            segments = lineSegments;
            inlineRuns = BuildLineRuns(lineOps, leftLineTokens, rightLineTokens);
        }
        else
        {
            var leftTokens = Tokenize(leftNormalized, opts.Granularity, opts);
            var rightTokens = Tokenize(rightNormalized, opts.Granularity, opts);
            var tokenOps = Diff(leftTokens, rightTokens, opts, context);
            segments = BuildSegments(tokenOps, leftTokens, rightTokens, opts, computeWordDiff: false, context);
            inlineRuns = BuildCoalescedRuns(tokenOps, leftTokens, rightTokens);
        }

        var areIdentical = statistics.TotalChanges == 0;

        var result = new TextDiffResult(
            opts.Granularity,
            segments,
            inlineRuns,
            unified,
            statistics,
            areIdentical,
            onlyLineEndingsDiffer,
            leftNewline,
            rightNewline,
            leftEndsWithNewline,
            rightEndsWithNewline,
            context.Approximate);

        string? warning = null;
        if (onlyLineEndingsDiffer)
        {
            warning =
                $"The two inputs are identical apart from their line endings (left uses {DescribeNewline(leftNewline)}, right uses {DescribeNewline(rightNewline)}).";
        }
        else if (context.Approximate)
        {
            warning =
                "The inputs were too large and too dissimilar for a minimal diff, so an approximate edit script is shown.";
        }

        return OperationResult<TextDiffResult>.Ok(result, warning);
    }

    private static string DescribeNewline(NewlineStyle style) => style switch
    {
        NewlineStyle.Lf => "LF",
        NewlineStyle.CrLf => "CRLF",
        NewlineStyle.Cr => "CR",
        NewlineStyle.Mixed => "mixed line endings",
        _ => "no line ending",
    };

    // ---------------------------------------------------------------- tokenising

    private sealed record DiffToken(string Text, int LineNumber);

    private sealed class DiffContext
    {
        public bool Approximate { get; set; }
    }

    private static string[] SplitIntoLines(string normalized, out bool endsWithNewline)
    {
        if (normalized.Length == 0)
        {
            endsWithNewline = false;
            return [];
        }

        endsWithNewline = normalized[^1] == '\n';
        var parts = normalized.Split('\n');
        return endsWithNewline ? parts[..^1] : parts;
    }

    private static List<DiffToken> BuildLineTokens(string[] lines, TextDiffOptions options)
    {
        var tokens = new List<DiffToken>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            if (options.IgnoreBlankLines && TextUtil.IsBlank(lines[i]))
            {
                continue;
            }

            tokens.Add(new DiffToken(lines[i], i + 1));
        }

        return tokens;
    }

    private static List<DiffToken> Tokenize(string text, DiffGranularity granularity, TextDiffOptions options)
    {
        if (granularity == DiffGranularity.Line)
        {
            return BuildLineTokens(SplitIntoLines(text, out _), options);
        }

        var tokens = new List<DiffToken>();
        var line = 1;
        var i = 0;

        while (i < text.Length)
        {
            var start = i;
            var c = text[i];

            if (c == '\n')
            {
                tokens.Add(new DiffToken("\n", line));
                line++;
                i++;
                continue;
            }

            if (granularity == DiffGranularity.Character)
            {
                i += StringInfo.GetNextTextElementLength(text.AsSpan(i));
            }
            else if (char.IsWhiteSpace(c))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]) && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (IsWordChar(c))
            {
                while (i < text.Length && IsWordChar(text[i]))
                {
                    i++;
                }
            }
            else
            {
                i += StringInfo.GetNextTextElementLength(text.AsSpan(i));
            }

            tokens.Add(new DiffToken(text[start..i], line));
        }

        return tokens;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string ComparisonKey(string text, TextDiffOptions options)
    {
        var key = text;

        if (options.IgnoreAllWhitespace)
        {
            var builder = new StringBuilder(key.Length);
            foreach (var c in key)
            {
                if (!char.IsWhiteSpace(c))
                {
                    builder.Append(c);
                }
            }

            key = builder.ToString();
        }
        else if (options.IgnoreLeadingAndTrailingWhitespace)
        {
            key = key.Trim();
        }

        if (options.IgnoreCase)
        {
            key = key.ToLowerInvariant();
        }

        return key;
    }

    // ---------------------------------------------------------------- edit script

    private enum EditKind
    {
        Equal,
        Delete,
        Insert,
    }

    private readonly record struct EditOp(EditKind Kind, int LeftIndex, int RightIndex);

    private static List<EditOp> Diff(
        List<DiffToken> leftTokens,
        List<DiffToken> rightTokens,
        TextDiffOptions options,
        DiffContext context)
    {
        var interner = new Dictionary<string, int>(StringComparer.Ordinal);
        var leftKeys = new int[leftTokens.Count];
        var rightKeys = new int[rightTokens.Count];

        for (var i = 0; i < leftTokens.Count; i++)
        {
            leftKeys[i] = Intern(interner, ComparisonKey(leftTokens[i].Text, options));
        }

        for (var j = 0; j < rightTokens.Count; j++)
        {
            rightKeys[j] = Intern(interner, ComparisonKey(rightTokens[j].Text, options));
        }

        var ops = new List<EditOp>(leftKeys.Length + rightKeys.Length);
        DiffWithUniqueElimination(leftKeys, rightKeys, ops, context);
        return ops;
    }

    private static int Intern(Dictionary<string, int> interner, string key)
    {
        if (interner.TryGetValue(key, out var id))
        {
            return id;
        }

        id = interner.Count;
        interner[key] = id;
        return id;
    }

    /// <summary>
    /// Drops every token that cannot possibly take part in a common subsequence — one whose key
    /// never occurs on the other side — before running Myers, then splices those tokens back in as
    /// plain deletions and insertions. This is exact, not a heuristic, and it collapses the worst
    /// case (two entirely unrelated 50,000-line files) to almost no work.
    /// </summary>
    private static void DiffWithUniqueElimination(int[] a, int[] b, List<EditOp> ops, DiffContext context)
    {
        if (a.Length == 0)
        {
            for (var j = 0; j < b.Length; j++)
            {
                ops.Add(new EditOp(EditKind.Insert, -1, j));
            }

            return;
        }

        if (b.Length == 0)
        {
            for (var i = 0; i < a.Length; i++)
            {
                ops.Add(new EditOp(EditKind.Delete, i, -1));
            }

            return;
        }

        var aSet = new HashSet<int>(a);
        var bSet = new HashSet<int>(b);

        var aKept = new List<int>(a.Length);
        var aIndex = new List<int>(a.Length);
        for (var i = 0; i < a.Length; i++)
        {
            if (bSet.Contains(a[i]))
            {
                aKept.Add(a[i]);
                aIndex.Add(i);
            }
        }

        var bKept = new List<int>(b.Length);
        var bIndex = new List<int>(b.Length);
        for (var j = 0; j < b.Length; j++)
        {
            if (aSet.Contains(b[j]))
            {
                bKept.Add(b[j]);
                bIndex.Add(j);
            }
        }

        var filteredOps = new List<EditOp>(aKept.Count + bKept.Count);
        var fa = aKept.ToArray();
        var fb = bKept.ToArray();
        DiffCore(fa, 0, fa.Length, fb, 0, fb.Length, filteredOps, context);

        var nextA = 0;
        var nextB = 0;

        foreach (var op in filteredOps)
        {
            switch (op.Kind)
            {
                case EditKind.Equal:
                {
                    var ai = aIndex[op.LeftIndex];
                    var bi = bIndex[op.RightIndex];
                    FlushDeletes(ops, ref nextA, ai);
                    FlushInserts(ops, ref nextB, bi);
                    ops.Add(new EditOp(EditKind.Equal, ai, bi));
                    nextA = ai + 1;
                    nextB = bi + 1;
                    break;
                }

                case EditKind.Delete:
                {
                    var ai = aIndex[op.LeftIndex];
                    FlushDeletes(ops, ref nextA, ai);
                    ops.Add(new EditOp(EditKind.Delete, ai, -1));
                    nextA = ai + 1;
                    break;
                }

                default:
                {
                    var bi = bIndex[op.RightIndex];
                    FlushInserts(ops, ref nextB, bi);
                    ops.Add(new EditOp(EditKind.Insert, -1, bi));
                    nextB = bi + 1;
                    break;
                }
            }
        }

        FlushDeletes(ops, ref nextA, a.Length);
        FlushInserts(ops, ref nextB, b.Length);
    }

    private static void FlushDeletes(List<EditOp> ops, ref int next, int until)
    {
        while (next < until)
        {
            ops.Add(new EditOp(EditKind.Delete, next, -1));
            next++;
        }
    }

    private static void FlushInserts(List<EditOp> ops, ref int next, int until)
    {
        while (next < until)
        {
            ops.Add(new EditOp(EditKind.Insert, -1, next));
            next++;
        }
    }

    /// <summary>
    /// Myers' divide-and-conquer diff: trim the common affixes, find the middle snake in linear
    /// space, then recurse on the two halves.
    /// </summary>
    private static void DiffCore(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, List<EditOp> ops, DiffContext context)
    {
        while (aLo < aHi && bLo < bHi && a[aLo] == b[bLo])
        {
            ops.Add(new EditOp(EditKind.Equal, aLo, bLo));
            aLo++;
            bLo++;
        }

        var suffix = 0;
        while (aLo < aHi && bLo < bHi && a[aHi - 1] == b[bHi - 1])
        {
            aHi--;
            bHi--;
            suffix++;
        }

        if (aLo == aHi)
        {
            for (var j = bLo; j < bHi; j++)
            {
                ops.Add(new EditOp(EditKind.Insert, -1, j));
            }
        }
        else if (bLo == bHi)
        {
            for (var i = aLo; i < aHi; i++)
            {
                ops.Add(new EditOp(EditKind.Delete, i, -1));
            }
        }
        else if (Bisect(a, aLo, aHi, b, bLo, bHi, out var splitA, out var splitB) &&
                 !(splitA == aLo && splitB == bLo) &&
                 !(splitA == aHi && splitB == bHi))
        {
            DiffCore(a, aLo, splitA, b, bLo, splitB, ops, context);
            DiffCore(a, splitA, aHi, b, splitB, bHi, ops, context);
        }
        else
        {
            context.Approximate = true;
            for (var i = aLo; i < aHi; i++)
            {
                ops.Add(new EditOp(EditKind.Delete, i, -1));
            }

            for (var j = bLo; j < bHi; j++)
            {
                ops.Add(new EditOp(EditKind.Insert, -1, j));
            }
        }

        for (var k = 0; k < suffix; k++)
        {
            ops.Add(new EditOp(EditKind.Equal, aHi + k, bHi + k));
        }
    }

    /// <summary>
    /// Runs the forward and reverse D-path searches until they overlap and reports where the two
    /// sequences should be split. Returns false when the work budget is exhausted first.
    /// </summary>
    private static bool Bisect(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, out int splitA, out int splitB)
    {
        splitA = aLo;
        splitB = bLo;

        var n = aHi - aLo;
        var m = bHi - bLo;
        var maxD = (n + m + 1) / 2;
        var cap = (int)Math.Clamp(BisectBudget / (n + m + 1), 64, int.MaxValue);
        if (maxD > cap)
        {
            maxD = cap;
        }

        if (maxD < 1)
        {
            maxD = 1;
        }

        var offset = maxD + 1;
        var length = (2 * maxD) + 3;
        var forward = new int[length];
        var reverse = new int[length];
        Array.Fill(forward, -1);
        Array.Fill(reverse, -1);
        forward[offset + 1] = 0;
        reverse[offset + 1] = 0;

        var delta = n - m;
        var checkForward = (delta & 1) != 0;
        var k1Start = 0;
        var k1End = 0;
        var k2Start = 0;
        var k2End = 0;

        for (var d = 0; d < maxD; d++)
        {
            for (var k1 = -d + k1Start; k1 <= d - k1End; k1 += 2)
            {
                var k1Offset = offset + k1;
                int x1;
                if (k1 == -d || (k1 != d && forward[k1Offset - 1] < forward[k1Offset + 1]))
                {
                    x1 = forward[k1Offset + 1];
                }
                else
                {
                    x1 = forward[k1Offset - 1] + 1;
                }

                var y1 = x1 - k1;
                while (x1 < n && y1 < m && a[aLo + x1] == b[bLo + y1])
                {
                    x1++;
                    y1++;
                }

                forward[k1Offset] = x1;

                if (x1 > n)
                {
                    k1End += 2;
                }
                else if (y1 > m)
                {
                    k1Start += 2;
                }
                else if (checkForward)
                {
                    var k2Offset = offset + delta - k1;
                    if (k2Offset >= 0 && k2Offset < length && reverse[k2Offset] != -1)
                    {
                        var x2 = n - reverse[k2Offset];
                        if (x1 >= x2)
                        {
                            splitA = aLo + x1;
                            splitB = bLo + y1;
                            return true;
                        }
                    }
                }
            }

            for (var k2 = -d + k2Start; k2 <= d - k2End; k2 += 2)
            {
                var k2Offset = offset + k2;
                int x2;
                if (k2 == -d || (k2 != d && reverse[k2Offset - 1] < reverse[k2Offset + 1]))
                {
                    x2 = reverse[k2Offset + 1];
                }
                else
                {
                    x2 = reverse[k2Offset - 1] + 1;
                }

                var y2 = x2 - k2;
                while (x2 < n && y2 < m && a[aHi - x2 - 1] == b[bHi - y2 - 1])
                {
                    x2++;
                    y2++;
                }

                reverse[k2Offset] = x2;

                if (x2 > n)
                {
                    k2End += 2;
                }
                else if (y2 > m)
                {
                    k2Start += 2;
                }
                else if (!checkForward)
                {
                    var k1Offset = offset + delta - k2;
                    if (k1Offset >= 0 && k1Offset < length && forward[k1Offset] != -1)
                    {
                        var x1 = forward[k1Offset];
                        var y1 = offset + x1 - k1Offset;
                        if (x1 >= n - x2)
                        {
                            splitA = aLo + x1;
                            splitB = bLo + y1;
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- shaping

    private static List<DiffSegment> BuildSegments(
        List<EditOp> ops,
        List<DiffToken> leftTokens,
        List<DiffToken> rightTokens,
        TextDiffOptions options,
        bool computeWordDiff,
        DiffContext context)
    {
        var segments = new List<DiffSegment>(ops.Count);
        var index = 0;

        while (index < ops.Count)
        {
            var op = ops[index];
            if (op.Kind == EditKind.Equal)
            {
                var leftToken = leftTokens[op.LeftIndex];
                var rightToken = rightTokens[op.RightIndex];
                segments.Add(new DiffSegment(
                    DiffChangeKind.Unchanged,
                    leftToken.Text,
                    rightToken.Text,
                    leftToken.LineNumber,
                    rightToken.LineNumber,
                    []));
                index++;
                continue;
            }

            var deletes = new List<DiffToken>();
            var inserts = new List<DiffToken>();
            while (index < ops.Count && ops[index].Kind != EditKind.Equal)
            {
                if (ops[index].Kind == EditKind.Delete)
                {
                    deletes.Add(leftTokens[ops[index].LeftIndex]);
                }
                else
                {
                    inserts.Add(rightTokens[ops[index].RightIndex]);
                }

                index++;
            }

            var paired = options.PairModifiedLines ? Math.Min(deletes.Count, inserts.Count) : 0;

            for (var i = 0; i < paired; i++)
            {
                var wordDiff = computeWordDiff
                    ? ComputeWordDiff(deletes[i], inserts[i], options, context)
                    : [];

                segments.Add(new DiffSegment(
                    DiffChangeKind.Modified,
                    deletes[i].Text,
                    inserts[i].Text,
                    deletes[i].LineNumber,
                    inserts[i].LineNumber,
                    wordDiff));
            }

            for (var i = paired; i < deletes.Count; i++)
            {
                segments.Add(new DiffSegment(
                    DiffChangeKind.Deleted,
                    deletes[i].Text,
                    null,
                    deletes[i].LineNumber,
                    null,
                    []));
            }

            for (var i = paired; i < inserts.Count; i++)
            {
                segments.Add(new DiffSegment(
                    DiffChangeKind.Inserted,
                    null,
                    inserts[i].Text,
                    null,
                    inserts[i].LineNumber,
                    []));
            }
        }

        return segments;
    }

    private static IReadOnlyList<DiffRun> ComputeWordDiff(
        DiffToken left,
        DiffToken right,
        TextDiffOptions options,
        DiffContext context)
    {
        var leftWords = Tokenize(left.Text, DiffGranularity.Word, options);
        var rightWords = Tokenize(right.Text, DiffGranularity.Word, options);
        if (leftWords.Count == 0 && rightWords.Count == 0)
        {
            return [];
        }

        var ops = Diff(leftWords, rightWords, options, context);
        return BuildCoalescedRuns(ops, leftWords, rightWords, left.LineNumber, right.LineNumber);
    }

    /// <summary>One run per line, with deletions listed before the insertions that replace them.</summary>
    private static List<DiffRun> BuildLineRuns(List<EditOp> ops, List<DiffToken> leftTokens, List<DiffToken> rightTokens)
    {
        var runs = new List<DiffRun>(ops.Count);
        var index = 0;

        while (index < ops.Count)
        {
            if (ops[index].Kind == EditKind.Equal)
            {
                var leftToken = leftTokens[ops[index].LeftIndex];
                var rightToken = rightTokens[ops[index].RightIndex];
                runs.Add(new DiffRun(DiffChangeKind.Unchanged, leftToken.Text, leftToken.LineNumber, rightToken.LineNumber));
                index++;
                continue;
            }

            var deletes = new List<DiffToken>();
            var inserts = new List<DiffToken>();
            while (index < ops.Count && ops[index].Kind != EditKind.Equal)
            {
                if (ops[index].Kind == EditKind.Delete)
                {
                    deletes.Add(leftTokens[ops[index].LeftIndex]);
                }
                else
                {
                    inserts.Add(rightTokens[ops[index].RightIndex]);
                }

                index++;
            }

            foreach (var token in deletes)
            {
                runs.Add(new DiffRun(DiffChangeKind.Deleted, token.Text, token.LineNumber, null));
            }

            foreach (var token in inserts)
            {
                runs.Add(new DiffRun(DiffChangeKind.Inserted, token.Text, null, token.LineNumber));
            }
        }

        return runs;
    }

    /// <summary>Adjacent tokens of the same kind merge into one run, deletions before insertions.</summary>
    private static List<DiffRun> BuildCoalescedRuns(
        List<EditOp> ops,
        List<DiffToken> leftTokens,
        List<DiffToken> rightTokens,
        int? forcedLeftLine = null,
        int? forcedRightLine = null)
    {
        var runs = new List<DiffRun>();
        var index = 0;
        var builder = new StringBuilder();

        while (index < ops.Count)
        {
            if (ops[index].Kind == EditKind.Equal)
            {
                builder.Clear();
                var leftLine = forcedLeftLine ?? leftTokens[ops[index].LeftIndex].LineNumber;
                var rightLine = forcedRightLine ?? rightTokens[ops[index].RightIndex].LineNumber;
                while (index < ops.Count && ops[index].Kind == EditKind.Equal)
                {
                    builder.Append(leftTokens[ops[index].LeftIndex].Text);
                    index++;
                }

                runs.Add(new DiffRun(DiffChangeKind.Unchanged, builder.ToString(), leftLine, rightLine));
                continue;
            }

            var deleteText = new StringBuilder();
            var insertText = new StringBuilder();
            int? deleteLine = null;
            int? insertLine = null;

            while (index < ops.Count && ops[index].Kind != EditKind.Equal)
            {
                if (ops[index].Kind == EditKind.Delete)
                {
                    var token = leftTokens[ops[index].LeftIndex];
                    deleteLine ??= forcedLeftLine ?? token.LineNumber;
                    deleteText.Append(token.Text);
                }
                else
                {
                    var token = rightTokens[ops[index].RightIndex];
                    insertLine ??= forcedRightLine ?? token.LineNumber;
                    insertText.Append(token.Text);
                }

                index++;
            }

            if (deleteText.Length > 0)
            {
                runs.Add(new DiffRun(DiffChangeKind.Deleted, deleteText.ToString(), deleteLine, null));
            }

            if (insertText.Length > 0)
            {
                runs.Add(new DiffRun(DiffChangeKind.Inserted, insertText.ToString(), null, insertLine));
            }
        }

        return runs;
    }

    private static DiffStatistics ComputeStatistics(List<DiffSegment> segments)
    {
        var added = 0;
        var removed = 0;
        var modified = 0;
        var unchanged = 0;

        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case DiffChangeKind.Inserted:
                    added++;
                    break;
                case DiffChangeKind.Deleted:
                    removed++;
                    break;
                case DiffChangeKind.Modified:
                    modified++;
                    break;
                default:
                    unchanged++;
                    break;
            }
        }

        // Similarity is the share of lines the two sides agree on: an unchanged line counts for
        // both sides, a modified line counts for half because one side of it survived.
        var leftTotal = unchanged + removed + modified;
        var rightTotal = unchanged + added + modified;
        var total = leftTotal + rightTotal;
        var similarity = total == 0
            ? EmptyPairSimilarity
            : Math.Round(100d * ((2d * unchanged) + modified) / total, 2, MidpointRounding.AwayFromZero);

        return new DiffStatistics(added, removed, modified, unchanged, similarity);
    }

    // ---------------------------------------------------------------- unified diff

    private readonly record struct UnifiedEntry(char Prefix, string Text, int? LeftLine, int? RightLine);

    private static string BuildUnifiedDiff(
        List<EditOp> ops,
        List<DiffToken> leftTokens,
        List<DiffToken> rightTokens,
        TextDiffOptions options,
        int leftLineCount,
        int rightLineCount,
        bool leftEndsWithNewline,
        bool rightEndsWithNewline)
    {
        var entries = new List<UnifiedEntry>(ops.Count);
        var index = 0;

        while (index < ops.Count)
        {
            if (ops[index].Kind == EditKind.Equal)
            {
                var leftToken = leftTokens[ops[index].LeftIndex];
                var rightToken = rightTokens[ops[index].RightIndex];
                entries.Add(new UnifiedEntry(' ', leftToken.Text, leftToken.LineNumber, rightToken.LineNumber));
                index++;
                continue;
            }

            var start = index;
            while (index < ops.Count && ops[index].Kind != EditKind.Equal)
            {
                index++;
            }

            for (var i = start; i < index; i++)
            {
                if (ops[i].Kind == EditKind.Delete)
                {
                    var token = leftTokens[ops[i].LeftIndex];
                    entries.Add(new UnifiedEntry('-', token.Text, token.LineNumber, null));
                }
            }

            for (var i = start; i < index; i++)
            {
                if (ops[i].Kind == EditKind.Insert)
                {
                    var token = rightTokens[ops[i].RightIndex];
                    entries.Add(new UnifiedEntry('+', token.Text, null, token.LineNumber));
                }
            }
        }

        var changed = new List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Prefix != ' ')
            {
                changed.Add(i);
            }
        }

        if (changed.Count == 0)
        {
            return string.Empty;
        }

        var hunks = new List<(int Start, int End)>();
        var hunkStart = Math.Max(0, changed[0] - options.ContextLines);
        var hunkEnd = Math.Min(entries.Count - 1, changed[0] + options.ContextLines);

        for (var i = 1; i < changed.Count; i++)
        {
            var nextStart = Math.Max(0, changed[i] - options.ContextLines);
            var nextEnd = Math.Min(entries.Count - 1, changed[i] + options.ContextLines);
            if (nextStart <= hunkEnd + 1)
            {
                hunkEnd = Math.Max(hunkEnd, nextEnd);
            }
            else
            {
                hunks.Add((hunkStart, hunkEnd));
                hunkStart = nextStart;
                hunkEnd = nextEnd;
            }
        }

        hunks.Add((hunkStart, hunkEnd));

        var output = new StringBuilder();
        output.Append("--- ").Append(options.LeftLabel).Append('\n');
        output.Append("+++ ").Append(options.RightLabel).Append('\n');

        foreach (var (start, end) in hunks)
        {
            var leftStart = 0;
            var rightStart = 0;
            var leftCount = 0;
            var rightCount = 0;

            for (var i = start; i <= end; i++)
            {
                if (entries[i].LeftLine is { } leftLine)
                {
                    if (leftCount == 0)
                    {
                        leftStart = leftLine;
                    }

                    leftCount++;
                }

                if (entries[i].RightLine is { } rightLine)
                {
                    if (rightCount == 0)
                    {
                        rightStart = rightLine;
                    }

                    rightCount++;
                }
            }

            output.Append("@@ -")
                .Append(leftStart.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(leftCount.ToString(CultureInfo.InvariantCulture))
                .Append(" +")
                .Append(rightStart.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(rightCount.ToString(CultureInfo.InvariantCulture))
                .Append(" @@\n");

            for (var i = start; i <= end; i++)
            {
                var entry = entries[i];
                output.Append(entry.Prefix).Append(entry.Text).Append('\n');

                var lastLeft = entry.LeftLine == leftLineCount && !leftEndsWithNewline;
                var lastRight = entry.RightLine == rightLineCount && !rightEndsWithNewline;

                var needsMarker = entry.Prefix switch
                {
                    '-' => lastLeft,
                    '+' => lastRight,
                    _ => lastLeft && lastRight,
                };

                if (needsMarker)
                {
                    output.Append("\\ No newline at end of file\n");
                }
            }
        }

        return output.ToString();
    }
}
