namespace DevTools.Core.Json;

/// <summary>What kind of difference a row carries, in the words a reader thinks in.</summary>
/// <remarks>
/// The differ's own vocabulary is added / removed / changed, which describes an edit. Reading
/// two documents side by side, the question is a different one — is this member missing, is it
/// the wrong type, or is it simply a different value — so the three categories are named for
/// that, and each can be turned off on its own.
/// </remarks>
public enum JsonDiffCategory
{
    Same,

    /// <summary>Present on one side only.</summary>
    MissingProperty,

    /// <summary>On both sides, but one is a string and the other a number, and so on.</summary>
    IncorrectType,

    /// <summary>Same type on both sides, different value.</summary>
    UnequalValue,
}

/// <summary>
/// One row of the side-by-side view: the same place in both documents.
/// </summary>
/// <remarks>
/// A row can be blank on one side — that is what a missing member looks like — and the line
/// numbers are each document's own, so a blank does not advance the number beside it. Rows stay
/// level with each other while the numbers stay true to the file.
/// </remarks>
public sealed record JsonDiffRow(
    int? LeftLine,
    string LeftText,
    int? RightLine,
    string RightText,
    JsonDiffCategory Category,
    string Path)
{
    /// <summary>Which difference this row belongs to, or -1 for a row that matches.</summary>
    public int DifferenceIndex { get; init; } = -1;

    public bool IsDifference => Category != JsonDiffCategory.Same;
}

/// <summary>One difference, as the navigator steps through them.</summary>
public sealed record JsonDifference(
    int Index,
    int Row,
    JsonDiffCategory Category,
    string Path,
    string Note);

/// <summary>Both documents laid out against each other, plus what differs.</summary>
public sealed record JsonDiffLayout(
    IReadOnlyList<JsonDiffRow> Rows,
    IReadOnlyList<JsonDifference> Differences,
    int MissingProperties,
    int IncorrectTypes,
    int UnequalValues)
{
    public int Total => MissingProperties + IncorrectTypes + UnequalValues;

    public string Summary => Total switch
    {
        0 => "No differences",
        1 => "Found 1 difference",
        _ => $"Found {Total} differences",
    };

    public static JsonDiffLayout Empty { get; } = new([], [], 0, 0, 0);
}

/// <summary>
/// Lays the two documents out side by side, row for row (FR-J28).
/// </summary>
/// <remarks>
/// <para>
/// Built from the diff tree rather than by pretty-printing each document and matching the two
/// texts up afterwards. The tree has already decided which member on the left is which member
/// on the right — including across a reordered or key-matched array, where the same element has
/// different indices on the two sides — so walking it emits both sides in step, and a row is
/// aligned by construction rather than by a second guess at the same question.
/// </para>
/// <para>
/// A member that exists on one side only leaves the other side of its row blank. Line numbers
/// count only real lines, so each column's numbering is still that document's own.
/// </para>
/// </remarks>
public static class JsonDiffLayoutBuilder
{
    /// <summary>
    /// How far a subtree is expanded when the two sides are not the same kind of thing.
    /// </summary>
    /// <remarks>
    /// When a member is an object on the left and a string on the right there is nothing to
    /// walk in step, so each side is rendered on its own. Doing that indefinitely would print a
    /// whole document into one row, so beyond this depth the rest is written inline.
    /// </remarks>
    private const int MismatchExpandDepth = 32;

    public static JsonDiffLayout Build(JsonDiffResult? diff, int indentWidth = 2)
    {
        if (diff is null)
        {
            return JsonDiffLayout.Empty;
        }

        var builder = new Builder(Math.Clamp(indentWidth, 1, 8));
        builder.WriteRoot(diff.Root);
        return builder.ToLayout();
    }

    private sealed class Builder(int indentWidth)
    {
        private readonly List<JsonDiffRow> _rows = [];
        private readonly List<JsonDifference> _differences = [];

        private int _leftLine;
        private int _rightLine;

        private int _missing;
        private int _incorrectTypes;
        private int _unequal;

        public JsonDiffLayout ToLayout() =>
            new(_rows, _differences, _missing, _incorrectTypes, _unequal);

        public void WriteRoot(JsonDiffNode root)
        {
            ArgumentNullException.ThrowIfNull(root);
            Write(root, prefix: null, depth: 0, lastOnLeft: true, lastOnRight: true);
        }

        /// <summary>
        /// Writes one node to both sides.
        /// </summary>
        /// <param name="prefix">The quoted member name and colon, or null inside an array.</param>
        /// <param name="lastOnLeft">False when a comma is still to come on that side.</param>
        private void Write(JsonDiffNode node, string? prefix, int depth, bool lastOnLeft, bool lastOnRight)
        {
            var onLeft = node.Kind != JsonDiffKind.Added;
            var onRight = node.Kind != JsonDiffKind.Removed;

            // Both sides a container of the same kind: walk the children in step.
            if (onLeft && onRight && node.HasChildren &&
                node.LeftKind == node.RightKind &&
                node.LeftKind is JsonKind.Object or JsonKind.Array)
            {
                WriteContainer(node, prefix, depth, lastOnLeft, lastOnRight);
                return;
            }

            var category = Categorise(node);

            // Anything else — a leaf, or two sides that are not the same shape — is written as
            // its own block on each side, which is the only honest thing to do when there is
            // no correspondence below this point.
            var left = onLeft ? Render(node, side: true, depth, lastOnLeft, prefix) : [];
            var right = onRight ? Render(node, side: false, depth, lastOnRight, prefix) : [];

            EmitBlock(left, right, category, node.Path);
        }

        private void WriteContainer(JsonDiffNode node, string? prefix, int depth, bool lastOnLeft, bool lastOnRight)
        {
            var isObject = node.LeftKind == JsonKind.Object;
            var open = isObject ? "{" : "[";
            var close = isObject ? "}" : "]";
            var pad = Indent(depth);

            AddRow(
                $"{pad}{prefix}{open}",
                $"{pad}{prefix}{open}",
                JsonDiffCategory.Same,
                node.Path,
                hasLeft: true,
                hasRight: true);

            // A child is last on a side when no later child appears on that side; the commas
            // have to follow each document's own membership, not the merged list.
            var children = node.Children;
            var lastLeft = LastIndexOn(children, left: true);
            var lastRight = LastIndexOn(children, left: false);

            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                var childPrefix = isObject ? $"{JsonWriter.Quote(child.Name)}: " : null;

                Write(child, childPrefix, depth + 1, i >= lastLeft, i >= lastRight);
            }

            var tailLeft = $"{pad}{close}{(lastOnLeft ? string.Empty : ",")}";
            var tailRight = $"{pad}{close}{(lastOnRight ? string.Empty : ",")}";

            AddRow(tailLeft, tailRight, JsonDiffCategory.Same, node.Path, hasLeft: true, hasRight: true);
        }

        /// <summary>The index of the last child that appears on the given side, or -1.</summary>
        private static int LastIndexOn(IReadOnlyList<JsonDiffNode> children, bool left)
        {
            for (var i = children.Count - 1; i >= 0; i--)
            {
                var missing = left ? JsonDiffKind.Added : JsonDiffKind.Removed;
                if (children[i].Kind != missing)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Renders one side of a node that has no counterpart to walk alongside.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A node like this — a member only one document has, or one that is an object here and
        /// a string there — has no paired children in the diff tree, because there was nothing
        /// to pair it with. Its value is carried as exact JSON text, so it is read back and
        /// printed properly rather than left on one long line: a whole object appearing on one
        /// side should read as an object.
        /// </para>
        /// <para>
        /// Re-reading costs a parse of that subtree alone, and only for nodes that differ. If
        /// the text somehow will not parse, the one-line form is still correct.
        /// </para>
        /// </remarks>
        private List<string> Render(JsonDiffNode node, bool side, int depth, bool last, string? prefix)
        {
            var text = side ? node.Left : node.Right;
            var kind = side ? node.LeftKind : node.RightKind;
            var pad = Indent(depth);
            var comma = last ? string.Empty : ",";
            var oneLine = new List<string> { $"{pad}{prefix}{text}{comma}" };

            if (kind is not (JsonKind.Object or JsonKind.Array) || text is null || depth >= MismatchExpandDepth)
            {
                return oneLine;
            }

            var parsed = JsonReader.Parse(text, JsonReaderOptions.Tolerant);

            if (!parsed.IsSuccess || parsed.Value?.Root is null)
            {
                return oneLine;
            }

            var lines = new List<string>();
            Expand(parsed.Value.Root, depth, prefix, comma, lines);
            return lines;
        }

        /// <summary>Pretty-prints a value that belongs to one side only.</summary>
        private void Expand(JsonNode node, int depth, string? prefix, string comma, List<string> lines)
        {
            var pad = Indent(depth);

            switch (node)
            {
                case JsonObject { Members.Count: > 0 } o:
                    lines.Add($"{pad}{prefix}{{");
                    for (var i = 0; i < o.Members.Count; i++)
                    {
                        var member = o.Members[i];
                        var tail = i == o.Members.Count - 1 ? string.Empty : ",";
                        Expand(member.Value, depth + 1, $"{JsonWriter.Quote(member.Name)}: ", tail, lines);
                    }

                    lines.Add($"{pad}}}{comma}");
                    break;

                case JsonArray { Items.Count: > 0 } a:
                    lines.Add($"{pad}{prefix}[");
                    for (var i = 0; i < a.Items.Count; i++)
                    {
                        var tail = i == a.Items.Count - 1 ? string.Empty : ",";
                        Expand(a.Items[i], depth + 1, null, tail, lines);
                    }

                    lines.Add($"{pad}]{comma}");
                    break;

                default:
                    lines.Add($"{pad}{prefix}{JsonWriter.WriteInline(node, int.MaxValue)}{comma}");
                    break;
            }
        }

        /// <summary>
        /// Puts two blocks of lines on the page, level at the top and padded to the taller.
        /// </summary>
        /// <remarks>
        /// Padding at the bottom rather than distributing it: the first line of each side is the
        /// one being compared — the member name and its value — and that is the line the eye
        /// goes to and the navigator scrolls to.
        /// </remarks>
        private void EmitBlock(List<string> left, List<string> right, JsonDiffCategory category, string path)
        {
            var difference = -1;

            if (category != JsonDiffCategory.Same)
            {
                difference = _differences.Count;
                _differences.Add(new JsonDifference(difference, _rows.Count, category, path, NoteFor(category, path)));
                Tally(category);
            }

            for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
            {
                var hasLeft = i < left.Count;
                var hasRight = i < right.Count;

                AddRow(
                    hasLeft ? left[i] : string.Empty,
                    hasRight ? right[i] : string.Empty,
                    category,
                    path,
                    hasLeft,
                    hasRight,
                    difference);
            }
        }

        private void AddRow(
            string left,
            string right,
            JsonDiffCategory category,
            string path,
            bool hasLeft,
            bool hasRight,
            int difference = -1)
        {
            _rows.Add(new JsonDiffRow(
                hasLeft ? ++_leftLine : null,
                hasLeft ? left : string.Empty,
                hasRight ? ++_rightLine : null,
                hasRight ? right : string.Empty,
                category,
                path)
            {
                DifferenceIndex = difference,
            });
        }

        private void Tally(JsonDiffCategory category)
        {
            switch (category)
            {
                case JsonDiffCategory.MissingProperty: _missing++; break;
                case JsonDiffCategory.IncorrectType: _incorrectTypes++; break;
                case JsonDiffCategory.UnequalValue: _unequal++; break;
                default: break;
            }
        }

        private string Indent(int depth) => new(' ', depth * indentWidth);

        private static JsonDiffCategory Categorise(JsonDiffNode node) => node.Kind switch
        {
            JsonDiffKind.Unchanged => JsonDiffCategory.Same,
            JsonDiffKind.Added or JsonDiffKind.Removed => JsonDiffCategory.MissingProperty,
            _ when node.LeftKind != node.RightKind => JsonDiffCategory.IncorrectType,
            _ => JsonDiffCategory.UnequalValue,
        };

        private static string NoteFor(JsonDiffCategory category, string path) => category switch
        {
            JsonDiffCategory.MissingProperty => $"{path} is on one side only.",
            JsonDiffCategory.IncorrectType => $"{path} is a different type on each side.",
            JsonDiffCategory.UnequalValue => $"{path} should be the same on both sides.",
            _ => string.Empty,
        };
    }
}
