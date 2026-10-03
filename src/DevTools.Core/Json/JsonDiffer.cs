using System.Globalization;
using DevTools.Core.Text;

namespace DevTools.Core.Json;

/// <summary>What happened to a node between the two documents.</summary>
public enum JsonDiffKind
{
    Unchanged,
    Added,
    Removed,
    Changed,
}

/// <summary>How array elements on the two sides are paired up (FR-J21).</summary>
public enum ArrayStrategy
{
    /// <summary>Element <c>i</c> against element <c>i</c>. Cheap, and right for ordered data.</summary>
    Index,

    /// <summary>Elements paired by the value of a chosen id member. Right for record sets that reorder.</summary>
    Key,

    /// <summary>
    /// Longest common subsequence over element hashes, so inserting one element does not
    /// report every element after it as changed. The default, and what a human expects.
    /// </summary>
    BestMatch,
}

/// <summary>Everything the differ lets the caller choose (FR-J22).</summary>
public sealed record JsonDiffOptions
{
    public ArrayStrategy ArrayStrategy { get; init; } = ArrayStrategy.BestMatch;

    /// <summary>The member name used to pair elements under <see cref="ArrayStrategy.Key"/>.</summary>
    public string KeyField { get; init; } = "id";

    /// <summary>Treats arrays as multisets: order carries no meaning.</summary>
    public bool IgnoreArrayOrder { get; init; }

    /// <summary>Two numbers within this absolute difference compare equal. Null means exact.</summary>
    public decimal? NumericTolerance { get; init; }

    /// <summary>String <em>values</em> compare case-insensitively.</summary>
    public bool IgnoreCaseInValues { get; init; }

    /// <summary>Member <em>names</em> compare case-insensitively.</summary>
    public bool IgnoreCaseInKeys { get; init; }

    /// <summary><c>{"a":null}</c> and <c>{}</c> compare equal.</summary>
    public bool NullEqualsMissing { get; init; }

    /// <summary>
    /// Paths excluded from the comparison entirely. Supports <c>*</c> for one segment and
    /// <c>..</c> or <c>**</c> for any depth, e.g. <c>$..timestamp</c>, <c>$.meta.*</c>,
    /// <c>$.items[*].etag</c>. Essential when diffing API responses.
    /// </summary>
    public IReadOnlyList<string> IgnorePaths { get; init; } = [];

    public static JsonDiffOptions Default { get; } = new();

    internal StringComparer KeyComparer =>
        IgnoreCaseInKeys ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal StringComparison ValueComparison =>
        IgnoreCaseInValues ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

/// <summary>One node of the diff tree. <see cref="Left"/> and <see cref="Right"/> are rendered for display.</summary>
public sealed record JsonDiffNode(
    string Path,
    string Name,
    JsonDiffKind Kind,
    string? Left,
    string? Right,
    IReadOnlyList<JsonDiffNode> Children)
{
    /// <summary>
    /// What each side actually was, which is how a changed value is told from a changed type.
    /// </summary>
    /// <remarks>
    /// <c>Left</c> and <c>Right</c> are rendered for reading; <c>"1"</c> and <c>1</c> print
    /// almost the same and mean something quite different. A side-by-side view has to say which
    /// of the two it is looking at, so the kinds are carried rather than re-derived from text.
    /// </remarks>
    public JsonKind? LeftKind { get; init; }

    public JsonKind? RightKind { get; init; }

    /// <summary>The marker shown beside the row. Status is never signalled by colour alone.</summary>
    public string Marker => Kind switch
    {
        JsonDiffKind.Added => "+",
        JsonDiffKind.Removed => "−",
        JsonDiffKind.Changed => "~",
        _ => " ",
    };

    public bool HasChildren => Children.Count > 0;
}

/// <summary>The outcome of comparing two documents.</summary>
public sealed record JsonDiffResult(
    JsonDiffNode Root,
    int Added,
    int Removed,
    int Changed,
    int Unchanged,
    bool AreEqual,
    string JsonPatch,
    IReadOnlyList<string> Warnings)
{
    public int TotalDifferences => Added + Removed + Changed;

    public string Summary => AreEqual
        ? "The two documents are semantically equal."
        : $"{Added} added, {Removed} removed, {Changed} changed, {Unchanged} unchanged.";
}

/// <summary>
/// Compares two JSON documents by structure rather than by text (FR-J20…FR-J27).
/// </summary>
/// <remarks>
/// Member order is never a difference — that is what "semantic" means here. Everything that
/// <em>is</em> a difference is reported once, with a path that can be clicked, and the whole
/// comparison is also emitted as an RFC 6902 patch so it can be applied by anything else.
/// </remarks>
public static class JsonDiffer
{
    public static OperationResult<JsonDiffResult> Compare(
        string? leftJson,
        string? rightJson,
        JsonDiffOptions? options = null,
        JsonReaderOptions? readerOptions = null)
    {
        var opts = options ?? JsonDiffOptions.Default;
        var reader = readerOptions ?? JsonReaderOptions.Tolerant;

        var leftBlank = TextUtil.IsBlank(leftJson);
        var rightBlank = TextUtil.IsBlank(rightJson);

        if (leftBlank && rightBlank)
        {
            return OperationResult<JsonDiffResult>.Ok(EmptyResult());
        }

        if (leftBlank)
        {
            return OperationResult<JsonDiffResult>.Fail("The left-hand document is empty.");
        }

        if (rightBlank)
        {
            return OperationResult<JsonDiffResult>.Fail("The right-hand document is empty.");
        }

        var left = JsonReader.Parse(leftJson, reader);
        if (!left.IsSuccess)
        {
            return OperationResult<JsonDiffResult>.Fail(Prefix(left.Error!, "Left"));
        }

        var right = JsonReader.Parse(rightJson, reader);
        if (!right.IsSuccess)
        {
            return OperationResult<JsonDiffResult>.Fail(Prefix(right.Error!, "Right"));
        }

        return Compare(left.Value!.Root, right.Value!.Root, opts,
            [.. left.Value.Warnings.Select(static w => "Left: " + w),
             .. right.Value.Warnings.Select(static w => "Right: " + w)]);
    }

    public static OperationResult<JsonDiffResult> Compare(
        JsonNode left,
        JsonNode right,
        JsonDiffOptions? options = null,
        IReadOnlyList<string>? inheritedWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var opts = options ?? JsonDiffOptions.Default;

        // Ignored paths are pruned from both trees before anything looks at them, rather than
        // being skipped during the walk. Skipping during the walk is not enough: array
        // elements are paired by hashing them, so two elements differing only in an ignored
        // member would hash differently and fail to pair at all — reporting an add and a
        // remove for a change the user explicitly asked to ignore.
        var ignore = new PathMatcher(opts.IgnorePaths);
        if (!ignore.IsEmpty)
        {
            left = Prune(left, "$", ignore);
            right = Prune(right, "$", ignore);
        }

        var context = new DiffContext(opts);

        var root = context.Compare("$", "$", left, right);

        var warnings = new List<string>(inheritedWarnings ?? []);
        warnings.AddRange(context.Warnings);

        var patch = JsonPatch.Build(left, right, opts);

        return OperationResult<JsonDiffResult>.Ok(new JsonDiffResult(
            root,
            context.Added,
            context.Removed,
            context.Changed,
            context.Unchanged,
            context is { Added: 0, Removed: 0, Changed: 0 },
            patch,
            warnings));
    }

    /// <summary>The textual line-by-line fallback mode (FR-J24).</summary>
    public static OperationResult<TextDiffResult> CompareAsText(
        string? leftJson,
        string? rightJson,
        TextDiffOptions? options = null) =>
        TextDiff.Compare(leftJson, rightJson, options);

    /// <summary>
    /// Removes every node whose path matches an ignore pattern, so the comparison, the array
    /// pairing and the emitted patch all see exactly the same documents.
    /// </summary>
    /// <remarks>
    /// Object members are dropped outright, which also makes "present on one side only"
    /// disappear as a difference. An array <em>element</em> cannot be dropped without
    /// renumbering everything after it, so it is replaced with a placeholder that both sides
    /// share.
    /// </remarks>
    private static JsonNode Prune(JsonNode node, string path, PathMatcher ignore)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var kept = new List<JsonMember>(obj.Members.Count);

                foreach (var member in obj.Members)
                {
                    var childPath = IsSimpleName(member.Name)
                        ? $"{path}.{member.Name}"
                        : $"{path}['{member.Name}']";

                    if (ignore.Matches(childPath))
                    {
                        continue;
                    }

                    kept.Add(member with { Value = Prune(member.Value, childPath, ignore) });
                }

                return obj with { Members = kept };
            }

            case JsonArray array:
            {
                var items = new List<JsonNode>(array.Items.Count);

                for (var i = 0; i < array.Items.Count; i++)
                {
                    var childPath = $"{path}[{i}]";
                    items.Add(ignore.Matches(childPath)
                        ? new JsonNull(array.Items[i].Position)
                        : Prune(array.Items[i], childPath, ignore));
                }

                return array with { Items = items };
            }

            default:
                return node;
        }
    }

    private static bool IsSimpleName(string name)
    {
        if (name.Length == 0 || (!char.IsLetter(name[0]) && name[0] != '_'))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static ToolError Prefix(ToolError error, string side) =>
        error with { Message = $"{side}-hand document: {error.Message}" };

    private static JsonDiffResult EmptyResult() => new(
        new JsonDiffNode("$", "$", JsonDiffKind.Unchanged, null, null, []),
        0, 0, 0, 0, true, "[]", []);

    // ---- the walk ------------------------------------------------------------------

    private sealed class DiffContext(JsonDiffOptions options)
    {
        private readonly List<string> _warnings = [];
        private readonly PathMatcher _ignore = new(options.IgnorePaths);
        private int _nodes;

        public int Added { get; private set; }

        public int Removed { get; private set; }

        public int Changed { get; private set; }

        public int Unchanged { get; private set; }

        public IReadOnlyList<string> Warnings => _warnings;

        private bool Budget()
        {
            if (_nodes++ < Limits.MaxDiffNodes)
            {
                return true;
            }

            if (_warnings.Count == 0 || !_warnings[^1].StartsWith("The comparison stopped", StringComparison.Ordinal))
            {
                _warnings.Add($"The comparison stopped after {Limits.MaxDiffNodes:N0} nodes. Narrow the documents, or ignore paths you do not care about.");
            }

            return false;
        }

        public JsonDiffNode Compare(string path, string name, JsonNode? left, JsonNode? right)
        {
            if (!Budget())
            {
                return Leaf(path, name, JsonDiffKind.Unchanged, left, right, count: false);
            }

            if (_ignore.Matches(path))
            {
                return Leaf(path, name, JsonDiffKind.Unchanged, left, right);
            }

            // A member that is absent on one side, where null and missing are the same thing.
            if (options.NullEqualsMissing)
            {
                if (left is null or JsonNull && right is null or JsonNull)
                {
                    return Leaf(path, name, JsonDiffKind.Unchanged, left, right);
                }
            }

            if (left is null)
            {
                return Leaf(path, name, JsonDiffKind.Added, null, right);
            }

            if (right is null)
            {
                return Leaf(path, name, JsonDiffKind.Removed, left, null);
            }

            if (left.Kind != right.Kind)
            {
                return Leaf(path, name, JsonDiffKind.Changed, left, right);
            }

            return left switch
            {
                JsonObject leftObject => CompareObjects(path, name, leftObject, (JsonObject)right),
                JsonArray leftArray => CompareArrays(path, name, leftArray, (JsonArray)right),
                _ => Leaf(path, name, ScalarsEqual(left, right) ? JsonDiffKind.Unchanged : JsonDiffKind.Changed, left, right),
            };
        }

        private JsonDiffNode CompareObjects(string path, string name, JsonObject left, JsonObject right)
        {
            var children = new List<JsonDiffNode>();

            var leftMembers = Index(left);
            var rightMembers = Index(right);

            // Left order first, then anything only the right side has — so the view reads in
            // the order the original document did.
            var names = new List<string>();
            var seen = new HashSet<string>(options.KeyComparer);

            foreach (var member in left.Members)
            {
                if (seen.Add(member.Name))
                {
                    names.Add(member.Name);
                }
            }

            foreach (var member in right.Members)
            {
                if (seen.Add(member.Name))
                {
                    names.Add(member.Name);
                }
            }

            foreach (var memberName in names)
            {
                leftMembers.TryGetValue(memberName, out var leftValue);
                rightMembers.TryGetValue(memberName, out var rightValue);

                children.Add(Compare(ChildPath(path, memberName), memberName, leftValue, rightValue));
            }

            return Branch(path, name, children, left, right);
        }

        private Dictionary<string, JsonNode> Index(JsonObject obj)
        {
            var map = new Dictionary<string, JsonNode>(options.KeyComparer);

            foreach (var member in obj.Members)
            {
                // A duplicate name keeps the last value, matching how every JSON consumer
                // behaves; the reader has already warned that duplicates were present.
                map[member.Name] = member.Value;
            }

            return map;
        }

        private JsonDiffNode CompareArrays(string path, string name, JsonArray left, JsonArray right)
        {
            var children = options.IgnoreArrayOrder
                ? PairByHash(path, left, right)
                : options.ArrayStrategy switch
                {
                    ArrayStrategy.Index => PairByIndex(path, left, right),
                    ArrayStrategy.Key => PairByKey(path, left, right),
                    _ => PairByBestMatch(path, left, right),
                };

            return Branch(path, name, children, left, right);
        }

        private List<JsonDiffNode> PairByIndex(string path, JsonArray left, JsonArray right)
        {
            var children = new List<JsonDiffNode>();
            var max = Math.Max(left.Items.Count, right.Items.Count);

            for (var i = 0; i < max; i++)
            {
                var l = i < left.Items.Count ? left.Items[i] : null;
                var r = i < right.Items.Count ? right.Items[i] : null;
                children.Add(Compare($"{path}[{i}]", $"[{i}]", l, r));
            }

            return children;
        }

        private List<JsonDiffNode> PairByKey(string path, JsonArray left, JsonArray right)
        {
            var children = new List<JsonDiffNode>();
            var rightByKey = new Dictionary<string, (int Index, JsonNode Node)>(StringComparer.Ordinal);
            var unkeyed = false;

            for (var i = 0; i < right.Items.Count; i++)
            {
                var key = KeyOf(right.Items[i]);
                if (key is null)
                {
                    unkeyed = true;
                    continue;
                }

                rightByKey[key] = (i, right.Items[i]);
            }

            var matched = new HashSet<int>();

            for (var i = 0; i < left.Items.Count; i++)
            {
                var key = KeyOf(left.Items[i]);

                if (key is not null && rightByKey.TryGetValue(key, out var hit))
                {
                    matched.Add(hit.Index);
                    children.Add(Compare($"{path}[{i}]", $"[{key}]", left.Items[i], hit.Node));
                }
                else
                {
                    if (key is null)
                    {
                        unkeyed = true;
                    }

                    children.Add(Compare($"{path}[{i}]", $"[{i}]", left.Items[i], null));
                }
            }

            for (var i = 0; i < right.Items.Count; i++)
            {
                if (!matched.Contains(i))
                {
                    children.Add(Compare($"{path}[{i}]", $"[{i}]", null, right.Items[i]));
                }
            }

            if (unkeyed)
            {
                var message = $"Some elements of {path} have no \"{options.KeyField}\" member, so they were paired by position instead.";
                if (!_warnings.Contains(message, StringComparer.Ordinal))
                {
                    _warnings.Add(message);
                }
            }

            return children;
        }

        private string? KeyOf(JsonNode node) =>
            node is JsonObject obj && obj.Find(options.KeyField) is { } key and not JsonNull
                ? JsonWriter.Write(key, JsonWriterOptions.Compact)
                : null;

        /// <summary>Multiset pairing: equal elements cancel out, whatever order they arrived in.</summary>
        private List<JsonDiffNode> PairByHash(string path, JsonArray left, JsonArray right)
        {
            var children = new List<JsonDiffNode>();
            var pool = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);

            for (var i = 0; i < right.Items.Count; i++)
            {
                var hash = Hash(right.Items[i]);
                if (!pool.TryGetValue(hash, out var queue))
                {
                    pool[hash] = queue = new Queue<int>();
                }

                queue.Enqueue(i);
            }

            var consumed = new HashSet<int>();

            for (var i = 0; i < left.Items.Count; i++)
            {
                var hash = Hash(left.Items[i]);

                if (pool.TryGetValue(hash, out var queue) && queue.Count > 0)
                {
                    var index = queue.Dequeue();
                    consumed.Add(index);
                    children.Add(Compare($"{path}[{i}]", $"[{i}]", left.Items[i], right.Items[index]));
                }
                else
                {
                    children.Add(Compare($"{path}[{i}]", $"[{i}]", left.Items[i], null));
                }
            }

            for (var i = 0; i < right.Items.Count; i++)
            {
                if (!consumed.Contains(i))
                {
                    children.Add(Compare($"{path}[{i}]", $"[{i}]", null, right.Items[i]));
                }
            }

            return children;
        }

        /// <summary>
        /// LCS over element hashes. An element inserted in the middle costs one "added" row
        /// rather than renumbering — and mis-reporting — everything after it.
        /// </summary>
        private List<JsonDiffNode> PairByBestMatch(string path, JsonArray left, JsonArray right)
        {
            var a = left.Items;
            var b = right.Items;
            var n = a.Count;
            var m = b.Count;

            var leftHashes = new string[n];
            for (var i = 0; i < n; i++)
            {
                leftHashes[i] = Hash(a[i]);
            }

            var rightHashes = new string[m];
            for (var j = 0; j < m; j++)
            {
                rightHashes[j] = Hash(b[j]);
            }

            // Table-based LCS. Bounded by MaxDiffNodes so a pathological pair cannot allocate
            // an enormous table; beyond that we fall back to index pairing.
            if ((long)(n + 1) * (m + 1) > Limits.MaxDiffNodes)
            {
                var message = $"{path} is too large for best-match pairing ({n:N0} × {m:N0}); elements were paired by position instead.";
                if (!_warnings.Contains(message, StringComparer.Ordinal))
                {
                    _warnings.Add(message);
                }

                return PairByIndex(path, left, right);
            }

            var table = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
            {
                for (var j = m - 1; j >= 0; j--)
                {
                    table[i, j] = string.Equals(leftHashes[i], rightHashes[j], StringComparison.Ordinal)
                        ? table[i + 1, j + 1] + 1
                        : Math.Max(table[i + 1, j], table[i, j + 1]);
                }
            }

            var children = new List<JsonDiffNode>();
            var gapLeft = new List<int>();
            var gapRight = new List<int>();
            var x = 0;
            var y = 0;

            while (x < n && y < m)
            {
                if (string.Equals(leftHashes[x], rightHashes[y], StringComparison.Ordinal))
                {
                    FlushGap(path, a, b, gapLeft, gapRight, children);
                    children.Add(Compare($"{path}[{x}]", $"[{x}]", a[x], b[y]));
                    x++;
                    y++;
                }
                else if (table[x + 1, y] >= table[x, y + 1])
                {
                    gapLeft.Add(x++);
                }
                else
                {
                    gapRight.Add(y++);
                }
            }

            while (x < n)
            {
                gapLeft.Add(x++);
            }

            while (y < m)
            {
                gapRight.Add(y++);
            }

            FlushGap(path, a, b, gapLeft, gapRight, children);

            return children;
        }

        /// <summary>The least similarity at which two unequal elements are treated as one that changed.</summary>
        private const double PairingThreshold = 0.25;

        /// <summary>Gaps larger than this (left × right) are paired by position rather than by similarity.</summary>
        private const int MaxGapCells = 250_000;

        /// <summary>
        /// Pairs up the elements that fell between two exact LCS matches.
        /// </summary>
        /// <remarks>
        /// Exact hashing only pairs elements that are identical, so a record with one edited
        /// field would otherwise surface as a whole-element remove plus a whole-element add —
        /// hiding the one field that actually changed. Within each gap the elements are aligned
        /// again, in order, by how similar they are; pairs recurse into a field-level diff and
        /// only what is left over is reported as added or removed.
        /// </remarks>
        private void FlushGap(
            string path,
            IReadOnlyList<JsonNode> a,
            IReadOnlyList<JsonNode> b,
            List<int> gapLeft,
            List<int> gapRight,
            List<JsonDiffNode> children)
        {
            var p = gapLeft.Count;
            var q = gapRight.Count;

            if (p == 0 || q == 0 || (long)p * q > MaxGapCells)
            {
                var paired = p == 0 || q == 0 ? 0 : Math.Min(p, q);

                for (var k = 0; k < paired; k++)
                {
                    children.Add(Compare($"{path}[{gapLeft[k]}]", $"[{gapLeft[k]}]", a[gapLeft[k]], b[gapRight[k]]));
                }

                EmitUnpaired(path, a, b, gapLeft, gapRight, paired, children);
                gapLeft.Clear();
                gapRight.Clear();
                return;
            }

            var similarity = new double[p, q];
            for (var i = 0; i < p; i++)
            {
                for (var j = 0; j < q; j++)
                {
                    similarity[i, j] = Similarity(a[gapLeft[i]], b[gapRight[j]]);
                }
            }

            // Order-preserving alignment that maximises total similarity.
            var score = new double[p + 1, q + 1];
            for (var i = p - 1; i >= 0; i--)
            {
                for (var j = q - 1; j >= 0; j--)
                {
                    var best = Math.Max(score[i + 1, j], score[i, j + 1]);
                    if (similarity[i, j] >= PairingThreshold)
                    {
                        best = Math.Max(best, similarity[i, j] + score[i + 1, j + 1]);
                    }

                    score[i, j] = best;
                }
            }

            var li = 0;
            var rj = 0;
            var pendingRemoved = new List<int>();
            var pendingAdded = new List<int>();

            void FlushPending()
            {
                foreach (var index in pendingRemoved)
                {
                    children.Add(Compare($"{path}[{index}]", $"[{index}]", a[index], null));
                }

                foreach (var index in pendingAdded)
                {
                    children.Add(Compare($"{path}[{index}]", $"[{index}]", null, b[index]));
                }

                pendingRemoved.Clear();
                pendingAdded.Clear();
            }

            while (li < p && rj < q)
            {
                if (similarity[li, rj] >= PairingThreshold &&
                    score[li, rj] == similarity[li, rj] + score[li + 1, rj + 1])
                {
                    FlushPending();
                    var l = gapLeft[li];
                    children.Add(Compare($"{path}[{l}]", $"[{l}]", a[l], b[gapRight[rj]]));
                    li++;
                    rj++;
                }
                else if (score[li, rj] == score[li + 1, rj])
                {
                    pendingRemoved.Add(gapLeft[li++]);
                }
                else
                {
                    pendingAdded.Add(gapRight[rj++]);
                }
            }

            while (li < p)
            {
                pendingRemoved.Add(gapLeft[li++]);
            }

            while (rj < q)
            {
                pendingAdded.Add(gapRight[rj++]);
            }

            FlushPending();
            gapLeft.Clear();
            gapRight.Clear();
        }

        private void EmitUnpaired(
            string path,
            IReadOnlyList<JsonNode> a,
            IReadOnlyList<JsonNode> b,
            List<int> gapLeft,
            List<int> gapRight,
            int skip,
            List<JsonDiffNode> children)
        {
            for (var k = skip; k < gapLeft.Count; k++)
            {
                children.Add(Compare($"{path}[{gapLeft[k]}]", $"[{gapLeft[k]}]", a[gapLeft[k]], null));
            }

            for (var k = skip; k < gapRight.Count; k++)
            {
                children.Add(Compare($"{path}[{gapRight[k]}]", $"[{gapRight[k]}]", null, b[gapRight[k]]));
            }
        }

        /// <summary>
        /// How alike two unequal elements are, from 0 (nothing in common) to 1.
        /// </summary>
        /// <remarks>
        /// Objects score by their members: a member present on both sides with an equal value
        /// counts fully, one present on both sides with a different value counts a little (the
        /// shape still matches), and one present on only one side counts nothing. Scalars of
        /// the same kind score just above the threshold, so a lone edited value in a gap reads
        /// as a change rather than a remove and an add.
        /// </remarks>
        private double Similarity(JsonNode left, JsonNode right)
        {
            if (left.Kind != right.Kind)
            {
                return 0;
            }

            switch (left, right)
            {
                case (JsonObject l, JsonObject r):
                {
                    var rightMembers = new Dictionary<string, JsonNode>(options.KeyComparer);
                    foreach (var member in r.Members)
                    {
                        rightMembers[member.Name] = member.Value;
                    }

                    var union = rightMembers.Count;
                    var total = 0.0;
                    var seen = new HashSet<string>(options.KeyComparer);

                    foreach (var member in l.Members)
                    {
                        if (!seen.Add(member.Name))
                        {
                            continue;
                        }

                        if (!rightMembers.TryGetValue(member.Name, out var other))
                        {
                            union++;
                            continue;
                        }

                        total += string.Equals(Hash(member.Value), Hash(other), StringComparison.Ordinal)
                            ? 1.0
                            : 0.3;
                    }

                    return union == 0 ? 1 : total / union;
                }

                case (JsonArray l, JsonArray r):
                {
                    if (l.Items.Count == 0 && r.Items.Count == 0)
                    {
                        return 1;
                    }

                    var pool = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var item in r.Items)
                    {
                        var hash = Hash(item);
                        pool[hash] = pool.GetValueOrDefault(hash) + 1;
                    }

                    var common = 0;
                    foreach (var item in l.Items)
                    {
                        var hash = Hash(item);
                        if (pool.GetValueOrDefault(hash) > 0)
                        {
                            pool[hash]--;
                            common++;
                        }
                    }

                    return 0.3 + (0.7 * 2.0 * common / (l.Items.Count + r.Items.Count));
                }

                default:
                    return PairingThreshold;
            }
        }

        private string Hash(JsonNode node)
        {
            // Under the loosening options, two nodes that compare equal must hash equal, or
            // the pairing would contradict the comparison that follows it.
            if (options is { IgnoreCaseInValues: false, NumericTolerance: null, IgnoreCaseInKeys: false })
            {
                return node.StructuralHash();
            }

            var sink = new System.Text.StringBuilder();
            HashLoose(node, sink);
            return sink.ToString();
        }

        private void HashLoose(JsonNode node, System.Text.StringBuilder sink)
        {
            switch (node)
            {
                case JsonString s:
                    sink.Append('s').Append(options.IgnoreCaseInValues ? s.Value.ToLowerInvariant() : s.Value).Append(';');
                    break;

                case JsonNumber n when options.NumericTolerance is { } tolerance && tolerance > 0:
                    // Quantise to the tolerance so nearby numbers land in the same bucket.
                    if (decimal.TryParse(n.Canonical, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        sink.Append('#').Append(Math.Round(value / tolerance, MidpointRounding.AwayFromZero)).Append(';');
                    }
                    else
                    {
                        sink.Append('#').Append(n.Canonical).Append(';');
                    }

                    break;

                case JsonObject o:
                    sink.Append('{');
                    foreach (var member in o.Members
                                 .OrderBy(m => options.IgnoreCaseInKeys ? m.Name.ToLowerInvariant() : m.Name, StringComparer.Ordinal))
                    {
                        sink.Append(options.IgnoreCaseInKeys ? member.Name.ToLowerInvariant() : member.Name).Append('=');
                        HashLoose(member.Value, sink);
                    }

                    sink.Append("};");
                    break;

                case JsonArray arr:
                    sink.Append('[');
                    foreach (var item in arr.Items)
                    {
                        HashLoose(item, sink);
                    }

                    sink.Append("];");
                    break;

                default:
                    node.HashInto(sink);
                    break;
            }
        }

        private bool ScalarsEqual(JsonNode left, JsonNode right) => (left, right) switch
        {
            (JsonNull, JsonNull) => true,
            (JsonBool a, JsonBool b) => a.Value == b.Value,
            (JsonString a, JsonString b) => string.Equals(a.Value, b.Value, options.ValueComparison),
            (JsonNumber a, JsonNumber b) => NumbersEqual(a, b),
            _ => false,
        };

        private bool NumbersEqual(JsonNumber a, JsonNumber b)
        {
            // Canonical form already makes 1, 1.0 and 1e0 equal without touching a double.
            if (string.Equals(a.Canonical, b.Canonical, StringComparison.Ordinal))
            {
                return true;
            }

            if (options.NumericTolerance is not { } tolerance || tolerance <= 0)
            {
                return false;
            }

            return decimal.TryParse(a.Canonical, NumberStyles.Float, CultureInfo.InvariantCulture, out var left) &&
                   decimal.TryParse(b.Canonical, NumberStyles.Float, CultureInfo.InvariantCulture, out var right) &&
                   Math.Abs(left - right) <= tolerance;
        }

        private JsonDiffNode Leaf(string path, string name, JsonDiffKind kind, JsonNode? left, JsonNode? right, bool count = true)
        {
            if (count)
            {
                Tally(kind);
            }

            return new JsonDiffNode(
                path,
                name,
                kind,
                // Whole, not ellipsized: this is the value being compared, and a diff that
                // trims the part that differs is worse than no diff at all. The tree view trims
                // its own rows for display; the side-by-side view wants the text.
                left is null ? null : JsonWriter.WriteInline(left, int.MaxValue),
                right is null ? null : JsonWriter.WriteInline(right, int.MaxValue),
                [])
            {
                LeftKind = left?.Kind,
                RightKind = right?.Kind,
            };
        }

        private JsonDiffNode Branch(string path, string name, List<JsonDiffNode> children, JsonNode left, JsonNode right)
        {
            // A container's own verdict is whatever its children say. It is not tallied
            // itself, or one changed leaf would be counted once per level above it.
            var kind = children.Any(static c => c.Kind != JsonDiffKind.Unchanged)
                ? JsonDiffKind.Changed
                : JsonDiffKind.Unchanged;

            if (kind == JsonDiffKind.Unchanged && children.Count == 0)
            {
                Unchanged++;
            }

            return new JsonDiffNode(
                path,
                name,
                kind,
                Describe(left),
                Describe(right),
                children)
            {
                LeftKind = left.Kind,
                RightKind = right.Kind,
            };
        }

        private static string Describe(JsonNode node) => node switch
        {
            JsonObject o => $"{{{o.Members.Count}}}",
            JsonArray a => $"[{a.Items.Count}]",
            _ => JsonWriter.WriteInline(node),
        };

        private void Tally(JsonDiffKind kind)
        {
            switch (kind)
            {
                case JsonDiffKind.Added: Added++; break;
                case JsonDiffKind.Removed: Removed++; break;
                case JsonDiffKind.Changed: Changed++; break;
                default: Unchanged++; break;
            }
        }

        private static string ChildPath(string parent, string name) =>
            IsSimple(name) ? $"{parent}.{name}" : $"{parent}['{name}']";

        private static bool IsSimple(string name)
        {
            if (name.Length == 0 || (!char.IsLetter(name[0]) && name[0] != '_'))
            {
                return false;
            }

            foreach (var c in name)
            {
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
