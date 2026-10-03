using System.Globalization;
using DevTools.Core.Text;

namespace DevTools.Core.Json;

/// <summary>One value a JSONPath selected, with the path that reached it.</summary>
public sealed record JsonPathMatch(string Path, JsonNode Node);

/// <summary>
/// A JSONPath evaluator covering the subset developers actually type (FR-J06):
/// <c>$</c>, <c>.name</c>, <c>['name']</c>, <c>..name</c> (recursive descent), <c>[n]</c>
/// (including negative indices), <c>[start:end:step]</c>, <c>[*]</c>, and filters of the form
/// <c>[?(@.field &lt;op&gt; value)]</c>.
/// </summary>
public static class JsonPathQuery
{
    public static OperationResult<IReadOnlyList<JsonPathMatch>> Select(JsonNode root, string? path)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (TextUtil.IsBlank(path))
        {
            return OperationResult<IReadOnlyList<JsonPathMatch>>.Ok([new JsonPathMatch("$", root)]);
        }

        var parsed = ParseSteps(path!.Trim());
        if (!parsed.IsSuccess)
        {
            return OperationResult<IReadOnlyList<JsonPathMatch>>.Fail(parsed.Error!);
        }

        IReadOnlyList<JsonPathMatch> current = [new JsonPathMatch("$", root)];

        foreach (var step in parsed.Value!)
        {
            var next = new List<JsonPathMatch>();

            foreach (var match in current)
            {
                step.Apply(match, next);
            }

            current = next;

            if (current.Count == 0)
            {
                break;
            }
        }

        return OperationResult<IReadOnlyList<JsonPathMatch>>.Ok(current);
    }

    /// <summary>Selects and renders the result as a JSON array, which is what the UI shows.</summary>
    public static OperationResult<JsonQueryResult> Query(JsonNode root, string? path, JsonWriterOptions? options = null)
    {
        var selected = Select(root, path);
        if (!selected.IsSuccess)
        {
            return OperationResult<JsonQueryResult>.Fail(selected.Error!);
        }

        var matches = selected.Value!;
        var array = new JsonArray([.. matches.Select(static m => m.Node)], JsonPosition.None);
        var text = JsonWriter.Write(array, options ?? JsonWriterOptions.Pretty);

        return OperationResult<JsonQueryResult>.Ok(
            new JsonQueryResult(text, matches.Count, path ?? "$", [.. matches.Select(static m => m.Path)]));
    }

    // ---- parsing -------------------------------------------------------------------

    private static OperationResult<List<Step>> ParseSteps(string path)
    {
        var steps = new List<Step>();
        var i = 0;

        if (path.StartsWith('$'))
        {
            i = 1;
        }
        else if (path.StartsWith('@'))
        {
            i = 1;
        }

        while (i < path.Length)
        {
            var c = path[i];

            if (c == '.')
            {
                if (i + 1 < path.Length && path[i + 1] == '.')
                {
                    i += 2;
                    if (i < path.Length && path[i] == '[')
                    {
                        // `..[0]` — descend, then apply the bracket to everything found.
                        steps.Add(new RecursiveStep(null));
                        continue;
                    }

                    var name = ReadName(path, ref i);
                    if (name.Length == 0)
                    {
                        return OperationResult<List<Step>>.Fail("'..' must be followed by a member name or a bracket.");
                    }

                    steps.Add(new RecursiveStep(name));
                    continue;
                }

                i++;
                if (i < path.Length && path[i] == '*')
                {
                    i++;
                    steps.Add(new WildcardStep());
                    continue;
                }

                var member = ReadName(path, ref i);
                if (member.Length == 0)
                {
                    return OperationResult<List<Step>>.Fail("'.' must be followed by a member name or '*'.");
                }

                steps.Add(new NameStep(member));
                continue;
            }

            if (c == '[')
            {
                var close = FindClosingBracket(path, i);
                if (close < 0)
                {
                    return OperationResult<List<Step>>.Fail($"The '[' at position {i} is never closed.");
                }

                var inner = path[(i + 1)..close].Trim();
                i = close + 1;

                var step = ParseBracket(inner);
                if (!step.IsSuccess)
                {
                    return OperationResult<List<Step>>.Fail(step.Error!);
                }

                steps.Add(step.Value!);
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // A bare name at the start, e.g. `store.book`.
            var bare = ReadName(path, ref i);
            if (bare.Length == 0)
            {
                return OperationResult<List<Step>>.Fail($"'{c}' is not valid here in a JSONPath expression.");
            }

            steps.Add(new NameStep(bare));
        }

        return OperationResult<List<Step>>.Ok(steps);
    }

    private static OperationResult<Step> ParseBracket(string inner)
    {
        if (inner.Length == 0)
        {
            return OperationResult<Step>.Fail("'[]' selects nothing. Use '[*]', an index, a slice or a filter.");
        }

        if (inner == "*")
        {
            return OperationResult<Step>.Ok(new WildcardStep());
        }

        if (inner.StartsWith('?'))
        {
            var expression = inner[1..].Trim();
            if (expression.StartsWith('(') && expression.EndsWith(')'))
            {
                expression = expression[1..^1].Trim();
            }

            var filter = FilterStep.Parse(expression);
            return filter.IsSuccess
                ? OperationResult<Step>.Ok(filter.Value!)
                : OperationResult<Step>.Fail(filter.Error!);
        }

        // A union is split first, and only on commas that are not inside quotes — otherwise
        // ['color','price'] looks like one quoted name that happens to contain a comma.
        var parts = SplitTopLevel(inner, ',');
        if (parts.Count > 1)
        {
            var members = parts.Select(Unquote).ToList();

            return members.All(static n => int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                ? OperationResult<Step>.Ok(new UnionIndexStep(
                    [.. members.Select(static n => int.Parse(n, CultureInfo.InvariantCulture))]))
                : OperationResult<Step>.Ok(new UnionNameStep(members));
        }

        if (IsQuoted(inner))
        {
            return OperationResult<Step>.Ok(new NameStep(inner[1..^1]));
        }

        var sliceParts = SplitTopLevel(inner, ':');
        if (sliceParts.Count > 1)
        {
            if (sliceParts.Count > 3)
            {
                return OperationResult<Step>.Fail($"'[{inner}]' has too many ':' separators; a slice is [start:end:step].");
            }

            int? start = ParseOptionalInt(sliceParts[0]);
            int? end = ParseOptionalInt(sliceParts[1]);
            var stride = sliceParts.Count > 2 ? ParseOptionalInt(sliceParts[2]) ?? 1 : 1;

            if (stride == 0)
            {
                return OperationResult<Step>.Fail("A slice step of 0 would never advance.");
            }

            return OperationResult<Step>.Ok(new SliceStep(start, end, stride));
        }

        if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return OperationResult<Step>.Ok(new IndexStep(index));
        }

        // An unquoted bare word inside brackets is a common shorthand; accept it.
        return OperationResult<Step>.Ok(new NameStep(inner));
    }

    private static bool IsQuoted(string text) =>
        text.Length >= 2 && ((text[0] == '\'' && text[^1] == '\'') || (text[0] == '"' && text[^1] == '"'));

    private static string Unquote(string text)
    {
        var trimmed = text.Trim();
        return IsQuoted(trimmed) ? trimmed[1..^1] : trimmed;
    }

    /// <summary>Splits on a separator that is not inside a quoted section.</summary>
    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        char? quote = null;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quote is { } q)
            {
                if (c == q)
                {
                    quote = null;
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                continue;
            }

            if (c == separator)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(text[start..].Trim());
        return parts;
    }

    private static int? ParseOptionalInt(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0
            ? null
            : int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string ReadName(string path, ref int i)
    {
        var start = i;
        while (i < path.Length && path[i] is not ('.' or '[' or ']'))
        {
            i++;
        }

        return path[start..i].Trim();
    }

    private static int FindClosingBracket(string path, int open)
    {
        var depth = 0;
        char? quote = null;

        for (var i = open; i < path.Length; i++)
        {
            var c = path[i];

            if (quote is { } q)
            {
                if (c == q)
                {
                    quote = null;
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    break;
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    // ---- steps ---------------------------------------------------------------------

    private abstract class Step
    {
        public abstract void Apply(JsonPathMatch match, List<JsonPathMatch> into);

        protected static string Child(string parent, string name) =>
            IsSimpleName(name) ? $"{parent}.{name}" : $"{parent}['{name}']";

        protected static string Element(string parent, int index) => $"{parent}[{index}]";

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
    }

    private sealed class NameStep(string name) : Step
    {
        public override void Apply(JsonPathMatch match, List<JsonPathMatch> into)
        {
            if (match.Node is not JsonObject obj)
            {
                return;
            }

            foreach (var member in obj.Members)
            {
                if (string.Equals(member.Name, name, StringComparison.Ordinal))
                {
                    into.Add(new JsonPathMatch(Child(match.Path, name), member.Value));
                }
            }
        }
    }

    private sealed class UnionNameStep(IReadOnlyList<string> names) : Step
    {
        public override void Apply(JsonPathMatch match, List<JsonPathMatch> into)
        {
            if (match.Node is not JsonObject obj)
            {
                return;
            }

            foreach (var name in names)
            {
                foreach (var member in obj.Members)
                {
                    if (string.Equals(member.Name, name, StringComparison.Ordinal))
                    {
                        into.Add(new JsonPathMatch(Child(match.Path, name), member.Value));
                    }
                }
            }
        }
    }

    private sealed class IndexStep(int index) : Step
    {
        public override void Apply(JsonPathMatch match, List<JsonPathMatch> into)
        {
            if (match.Node is not JsonArray array)
            {
                return;
            }

            var actual = index < 0 ? array.Items.Count + index : index;
            if (actual >= 0 && actual < array.Items.Count)
            {
                into.Add(new JsonPathMatch(Element(match.Path, actual), array.Items[actual]));
            }
        }
    }

    private sealed class UnionIndexStep(IReadOnlyList<int> indices) : Step
    {
        public override void Apply(JsonPathMatch match, List<JsonPathMatch> into)
        {
            if (match.Node is not JsonArray array)
            {
                return;
            }

            foreach (var index in indices)
            {
                var actual = index < 0 ? array.Items.Count + index : index;
                if (actual >= 0 && actual < array.Items.Count)
                {
                    into.Add(new JsonPathMatch(Element(match.Path, actual), array.Items[actual]));
                }
            }
        }
    }

    private sealed class SliceStep(int? start, int? end, int step) : Step
    {
        public override void Apply(JsonPathMatch match, List<JsonPathMatch> into)
        {
            if (match.Node is not JsonArray array)
            {
                return;
            }

            var count = array.Items.Count;

            if (step > 0)
            {
                var from = Clamp(start ?? 0, count);
                var to = Clamp(end ?? count, count);

                for (var i = from; i < to; i += step)
                {
                    into.Add(new JsonPathMatch(Element(match.Path, i), array.Items[i]));
                }
            }
            else
            {
                var from = Clamp(start ?? count - 1, count, inclusiveEnd: true);
                var to = end is { } e ? Clamp(e, count, inclusiveEnd: true) : -1;

                for (var i = from; i > to; i += step)
                {
                    if (i >= 0 && i < count)
                    {
                        into.Add(new JsonPathMatch(Element(match.Path, i), array.Items[i]));
                    }
                }
            }
        }

        private static int Clamp(int value, int count, bool inclusiveEnd = false)
        {
            if (value < 0)
            {
                value += count;
            }

            var max = inclusiveEnd ? count - 1 : count;
            return Math.Max(inclusiveEnd ? -1 : 0, Math.Min(value, max));
        }
    }

    private sealed class WildcardStep : Step
    {
        public override void Apply(JsonPathMatch match, List<JsonPathMatch> into)
        {
            switch (match.Node)
            {
                case JsonArray array:
                    for (var i = 0; i < array.Items.Count; i++)
                    {
                        into.Add(new JsonPathMatch(Element(match.Path, i), array.Items[i]));
                    }

                    break;

                case JsonObject obj:
                    foreach (var member in obj.Members)
                    {
                        into.Add(new JsonPathMatch(Child(match.Path, member.Name), member.Value));
                    }

                    break;
            }
        }
    }

    private sealed class RecursiveStep(string? name) : Step
    {
        public override void Apply(JsonPathMatch match, List<JsonPathMatch> into)
        {
            Walk(match, into);
        }

        private void Walk(JsonPathMatch match, List<JsonPathMatch> into)
        {
            if (name is null)
            {
                into.Add(match);
            }

            switch (match.Node)
            {
                case JsonObject obj:
                    foreach (var member in obj.Members)
                    {
                        var child = new JsonPathMatch(Child(match.Path, member.Name), member.Value);

                        if (name is not null && string.Equals(member.Name, name, StringComparison.Ordinal))
                        {
                            into.Add(child);
                        }

                        Walk(child, into);
                    }

                    break;

                case JsonArray array:
                    for (var i = 0; i < array.Items.Count; i++)
                    {
                        Walk(new JsonPathMatch(Element(match.Path, i), array.Items[i]), into);
                    }

                    break;
            }
        }
    }

    private sealed class FilterStep(string field, string op, string? literal) : Step
    {
        public static OperationResult<Step> Parse(string expression)
        {
            if (expression.Length == 0)
            {
                return OperationResult<Step>.Fail("A filter needs an expression, for example [?(@.price > 10)].");
            }

            string[] operators = ["==", "!=", ">=", "<=", "=~", ">", "<"];

            foreach (var op in operators)
            {
                var at = expression.IndexOf(op, StringComparison.Ordinal);
                if (at <= 0)
                {
                    continue;
                }

                var left = expression[..at].Trim();
                var right = expression[(at + op.Length)..].Trim();

                var field = NormalizeField(left);
                if (field is null)
                {
                    return OperationResult<Step>.Fail($"'{left}' is not a field reference; a filter reads like [?(@.price > 10)].");
                }

                return OperationResult<Step>.Ok(new FilterStep(field, op, Unquote(right)));
            }

            // A bare `@.field` filter tests for existence.
            var existence = NormalizeField(expression);
            return existence is null
                ? OperationResult<Step>.Fail($"'{expression}' is not a filter this understands.")
                : OperationResult<Step>.Ok(new FilterStep(existence, "exists", null));
        }

        private static string? NormalizeField(string text)
        {
            var t = text.Trim();

            if (t.StartsWith("@.", StringComparison.Ordinal))
            {
                return t[2..].Trim();
            }

            if (t.StartsWith("@[", StringComparison.Ordinal) && t.EndsWith(']'))
            {
                return t[2..^1].Trim().Trim('\'', '"');
            }

            return t == "@" ? string.Empty : null;
        }

        private static string Unquote(string text)
        {
            var t = text.Trim();
            return t.Length >= 2 && ((t[0] == '\'' && t[^1] == '\'') || (t[0] == '"' && t[^1] == '"'))
                ? t[1..^1]
                : t;
        }

        public override void Apply(JsonPathMatch match, List<JsonPathMatch> into)
        {
            switch (match.Node)
            {
                case JsonArray array:
                    for (var i = 0; i < array.Items.Count; i++)
                    {
                        if (Test(array.Items[i]))
                        {
                            into.Add(new JsonPathMatch(Element(match.Path, i), array.Items[i]));
                        }
                    }

                    break;

                case JsonObject obj:
                    foreach (var member in obj.Members)
                    {
                        if (Test(member.Value))
                        {
                            into.Add(new JsonPathMatch(Child(match.Path, member.Name), member.Value));
                        }
                    }

                    break;
            }
        }

        private bool Test(JsonNode candidate)
        {
            var target = field.Length == 0
                ? candidate
                : candidate is JsonObject obj ? obj.Find(field) : null;

            if (op == "exists")
            {
                return target is not null and not JsonNull;
            }

            if (target is null)
            {
                return false;
            }

            var actual = Scalar(target);

            if (op is "==" or "!=")
            {
                var equal = ScalarEquals(target, literal);
                return op == "==" ? equal : !equal;
            }

            if (op == "=~")
            {
                return literal is not null && actual.Contains(literal, StringComparison.OrdinalIgnoreCase);
            }

            // Ordering compares numerically when both sides are numbers, textually otherwise.
            if (target is JsonNumber number &&
                decimal.TryParse(number.Canonical, NumberStyles.Float, CultureInfo.InvariantCulture, out var left) &&
                decimal.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var right))
            {
                return op switch
                {
                    ">" => left > right,
                    "<" => left < right,
                    ">=" => left >= right,
                    "<=" => left <= right,
                    _ => false,
                };
            }

            var comparison = string.CompareOrdinal(actual, literal ?? string.Empty);
            return op switch
            {
                ">" => comparison > 0,
                "<" => comparison < 0,
                ">=" => comparison >= 0,
                "<=" => comparison <= 0,
                _ => false,
            };
        }

        private static bool ScalarEquals(JsonNode node, string? literal)
        {
            if (node is JsonNumber number &&
                decimal.TryParse(number.Canonical, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
                decimal.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            {
                return a == b;
            }

            return string.Equals(Scalar(node), literal, StringComparison.Ordinal);
        }

        private static string Scalar(JsonNode node) => node switch
        {
            JsonString s => s.Value,
            JsonNumber n => n.Canonical,
            JsonBool b => b.Value ? "true" : "false",
            JsonNull => "null",
            _ => JsonWriter.Write(node, JsonWriterOptions.Compact),
        };
    }
}

/// <summary>The values a JSONPath selected, already rendered.</summary>
public sealed record JsonQueryResult(string Output, int MatchCount, string Path, IReadOnlyList<string> Paths);
