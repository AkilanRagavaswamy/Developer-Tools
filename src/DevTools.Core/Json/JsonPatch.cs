using System.Text;
using DevTools.Core.Text;

namespace DevTools.Core.Json;

/// <summary>
/// Builds and applies RFC 6902 JSON Patch documents (FR-J26).
/// </summary>
/// <remarks>
/// The differ's tree view is for reading; the patch is for doing. Emitting one means the
/// comparison can be handed to anything else that speaks JSON Patch, and it also gives the
/// test suite a way to prove the diff is <em>right</em> rather than merely plausible:
/// applying the patch to the left document must produce the right one.
/// </remarks>
public static class JsonPatch
{
    /// <summary>Computes the patch that turns <paramref name="left"/> into <paramref name="right"/>.</summary>
    public static string Build(JsonNode left, JsonNode right, JsonDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var opts = options ?? JsonDiffOptions.Default;
        var builder = new PatchBuilder(opts);
        builder.Walk(string.Empty, "$", left, right);
        return builder.Render();
    }

    /// <summary>Applies a patch document to a parsed document.</summary>
    public static OperationResult<JsonNode> Apply(JsonNode document, string? patchJson)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (TextUtil.IsBlank(patchJson))
        {
            return OperationResult<JsonNode>.Ok(document);
        }

        var parsed = JsonReader.Parse(patchJson);
        if (!parsed.IsSuccess)
        {
            return OperationResult<JsonNode>.Fail($"The patch is not valid JSON: {parsed.Error!.ToDisplayString()}");
        }

        if (parsed.Value!.Root is not JsonArray operations)
        {
            return OperationResult<JsonNode>.Fail("A JSON Patch document must be an array of operations.");
        }

        var current = document;

        for (var i = 0; i < operations.Items.Count; i++)
        {
            if (operations.Items[i] is not JsonObject operation)
            {
                return OperationResult<JsonNode>.Fail($"Operation {i} is not an object.");
            }

            var applied = ApplyOne(current, operation, i);
            if (!applied.IsSuccess)
            {
                return applied;
            }

            current = applied.Value!;
        }

        return OperationResult<JsonNode>.Ok(current);
    }

    /// <summary>Convenience overload that parses, applies and re-renders in one call.</summary>
    public static OperationResult<string> Apply(string? documentJson, string? patchJson, JsonWriterOptions? writerOptions = null)
    {
        var parsed = JsonReader.Parse(documentJson, JsonReaderOptions.Tolerant);
        if (!parsed.IsSuccess)
        {
            return OperationResult<string>.Fail(parsed.Error!);
        }

        var applied = Apply(parsed.Value!.Root, patchJson);
        return applied.IsSuccess
            ? OperationResult<string>.Ok(JsonWriter.Write(applied.Value!, writerOptions ?? JsonWriterOptions.Pretty))
            : OperationResult<string>.Fail(applied.Error!);
    }

    // ---- applying ------------------------------------------------------------------

    private static OperationResult<JsonNode> ApplyOne(JsonNode root, JsonObject operation, int index)
    {
        if (operation.Find("op") is not JsonString op)
        {
            return OperationResult<JsonNode>.Fail($"Operation {index} has no \"op\" member.");
        }

        if (operation.Find("path") is not JsonString pathNode)
        {
            return OperationResult<JsonNode>.Fail($"Operation {index} (\"{op.Value}\") has no \"path\" member.");
        }

        var path = JsonPointer.Parse(pathNode.Value);

        switch (op.Value)
        {
            case "add":
            {
                var value = operation.Find("value");
                return value is null
                    ? OperationResult<JsonNode>.Fail($"Operation {index} (\"add\") has no \"value\" member.")
                    : Insert(root, path, value, replace: false, index);
            }

            case "replace":
            {
                var value = operation.Find("value");
                return value is null
                    ? OperationResult<JsonNode>.Fail($"Operation {index} (\"replace\") has no \"value\" member.")
                    : Insert(root, path, value, replace: true, index);
            }

            case "remove":
                return Remove(root, path, index);

            case "test":
            {
                var value = operation.Find("value");
                var actual = Resolve(root, path);
                return actual is not null && value is not null &&
                       string.Equals(actual.StructuralHash(), value.StructuralHash(), StringComparison.Ordinal)
                    ? OperationResult<JsonNode>.Ok(root)
                    : OperationResult<JsonNode>.Fail($"Operation {index} (\"test\") failed at \"{pathNode.Value}\".");
            }

            case "copy" or "move":
            {
                if (operation.Find("from") is not JsonString from)
                {
                    return OperationResult<JsonNode>.Fail($"Operation {index} (\"{op.Value}\") has no \"from\" member.");
                }

                var fromPath = JsonPointer.Parse(from.Value);
                var source = Resolve(root, fromPath);
                if (source is null)
                {
                    return OperationResult<JsonNode>.Fail($"Operation {index} (\"{op.Value}\") cannot read \"{from.Value}\".");
                }

                var working = root;
                if (op.Value == "move")
                {
                    var removed = Remove(working, fromPath, index);
                    if (!removed.IsSuccess)
                    {
                        return removed;
                    }

                    working = removed.Value!;
                }

                return Insert(working, path, source, replace: false, index);
            }

            default:
                return OperationResult<JsonNode>.Fail($"Operation {index} uses \"{op.Value}\", which is not an RFC 6902 operation.");
        }
    }

    private static JsonNode? Resolve(JsonNode node, IReadOnlyList<string> path)
    {
        var current = node;

        foreach (var token in path)
        {
            switch (current)
            {
                case JsonObject obj:
                    current = obj.Find(token);
                    break;

                case JsonArray array when int.TryParse(token, out var i) && i >= 0 && i < array.Items.Count:
                    current = array.Items[i];
                    break;

                default:
                    return null;
            }

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static OperationResult<JsonNode> Insert(JsonNode node, IReadOnlyList<string> path, JsonNode value, bool replace, int index)
    {
        if (path.Count == 0)
        {
            return OperationResult<JsonNode>.Ok(value);
        }

        return Rewrite(node, path, 0, index, (container, token) =>
        {
            switch (container)
            {
                case JsonObject obj:
                {
                    var members = new List<JsonMember>(obj.Members);
                    var at = members.FindIndex(m => string.Equals(m.Name, token, StringComparison.Ordinal));

                    if (at >= 0)
                    {
                        members[at] = members[at] with { Value = value };
                    }
                    else if (replace)
                    {
                        return OperationResult<JsonNode>.Fail($"Operation {index} (\"replace\") cannot find \"{token}\".");
                    }
                    else
                    {
                        members.Add(new JsonMember(token, value, JsonPosition.None));
                    }

                    return OperationResult<JsonNode>.Ok(obj with { Members = members });
                }

                case JsonArray array:
                {
                    var items = new List<JsonNode>(array.Items);

                    if (token == "-")
                    {
                        if (replace)
                        {
                            return OperationResult<JsonNode>.Fail($"Operation {index} (\"replace\") cannot target '-'.");
                        }

                        items.Add(value);
                        return OperationResult<JsonNode>.Ok(array with { Items = items });
                    }

                    if (!int.TryParse(token, out var at) || at < 0 || at > items.Count || (replace && at == items.Count))
                    {
                        return OperationResult<JsonNode>.Fail($"Operation {index} targets index {token}, which is outside the array.");
                    }

                    if (replace)
                    {
                        items[at] = value;
                    }
                    else
                    {
                        items.Insert(at, value);
                    }

                    return OperationResult<JsonNode>.Ok(array with { Items = items });
                }

                default:
                    return OperationResult<JsonNode>.Fail($"Operation {index} cannot address \"{token}\" inside a {container.Kind}.");
            }
        });
    }

    private static OperationResult<JsonNode> Remove(JsonNode node, IReadOnlyList<string> path, int index)
    {
        if (path.Count == 0)
        {
            return OperationResult<JsonNode>.Fail($"Operation {index} (\"remove\") cannot remove the whole document.");
        }

        return Rewrite(node, path, 0, index, (container, token) =>
        {
            switch (container)
            {
                case JsonObject obj:
                {
                    var members = new List<JsonMember>(obj.Members);
                    var at = members.FindIndex(m => string.Equals(m.Name, token, StringComparison.Ordinal));

                    if (at < 0)
                    {
                        return OperationResult<JsonNode>.Fail($"Operation {index} (\"remove\") cannot find \"{token}\".");
                    }

                    members.RemoveAt(at);
                    return OperationResult<JsonNode>.Ok(obj with { Members = members });
                }

                case JsonArray array:
                {
                    if (!int.TryParse(token, out var at) || at < 0 || at >= array.Items.Count)
                    {
                        return OperationResult<JsonNode>.Fail($"Operation {index} (\"remove\") targets index {token}, which is outside the array.");
                    }

                    var items = new List<JsonNode>(array.Items);
                    items.RemoveAt(at);
                    return OperationResult<JsonNode>.Ok(array with { Items = items });
                }

                default:
                    return OperationResult<JsonNode>.Fail($"Operation {index} cannot address \"{token}\" inside a {container.Kind}.");
            }
        });
    }

    /// <summary>
    /// Walks down to the parent of the addressed node, applies <paramref name="edit"/> there,
    /// and rebuilds the spine on the way back up. The model is immutable, so every change is
    /// a new tree rather than a mutation.
    /// </summary>
    private static OperationResult<JsonNode> Rewrite(
        JsonNode node,
        IReadOnlyList<string> path,
        int depth,
        int index,
        Func<JsonNode, string, OperationResult<JsonNode>> edit)
    {
        var token = path[depth];

        if (depth == path.Count - 1)
        {
            return edit(node, token);
        }

        switch (node)
        {
            case JsonObject obj:
            {
                var members = new List<JsonMember>(obj.Members);
                var at = members.FindIndex(m => string.Equals(m.Name, token, StringComparison.Ordinal));

                if (at < 0)
                {
                    return OperationResult<JsonNode>.Fail($"Operation {index} cannot find \"{token}\".");
                }

                var child = Rewrite(members[at].Value, path, depth + 1, index, edit);
                if (!child.IsSuccess)
                {
                    return child;
                }

                members[at] = members[at] with { Value = child.Value! };
                return OperationResult<JsonNode>.Ok(obj with { Members = members });
            }

            case JsonArray array:
            {
                if (!int.TryParse(token, out var at) || at < 0 || at >= array.Items.Count)
                {
                    return OperationResult<JsonNode>.Fail($"Operation {index} targets index {token}, which is outside the array.");
                }

                var child = Rewrite(array.Items[at], path, depth + 1, index, edit);
                if (!child.IsSuccess)
                {
                    return child;
                }

                var items = new List<JsonNode>(array.Items) { [at] = child.Value! };
                return OperationResult<JsonNode>.Ok(array with { Items = items });
            }

            default:
                return OperationResult<JsonNode>.Fail($"Operation {index} cannot descend into a {node.Kind} at \"{token}\".");
        }
    }

    // ---- building ------------------------------------------------------------------

    private sealed class PatchBuilder(JsonDiffOptions options)
    {
        private readonly List<string> _operations = [];
        private readonly PathMatcher _ignore = new(options.IgnorePaths);

        public string Render()
        {
            if (_operations.Count == 0)
            {
                return "[]";
            }

            var builder = new StringBuilder("[\n");
            for (var i = 0; i < _operations.Count; i++)
            {
                builder.Append("  ").Append(_operations[i]);
                if (i < _operations.Count - 1)
                {
                    builder.Append(',');
                }

                builder.Append('\n');
            }

            return builder.Append(']').ToString();
        }

        public void Walk(string pointer, string jsonPath, JsonNode left, JsonNode right)
        {
            if (_ignore.Matches(jsonPath))
            {
                return;
            }

            if (left.Kind != right.Kind)
            {
                Replace(pointer, right);
                return;
            }

            switch (left)
            {
                case JsonObject leftObject:
                    WalkObject(pointer, jsonPath, leftObject, (JsonObject)right);
                    break;

                case JsonArray leftArray:
                    WalkArray(pointer, jsonPath, leftArray, (JsonArray)right);
                    break;

                default:
                    if (!ScalarsEqual(left, right))
                    {
                        Replace(pointer, right);
                    }

                    break;
            }
        }

        private void WalkObject(string pointer, string jsonPath, JsonObject left, JsonObject right)
        {
            var leftMembers = Index(left);
            var rightMembers = Index(right);

            foreach (var member in left.Members)
            {
                if (!rightMembers.ContainsKey(member.Name))
                {
                    var childPath = $"{jsonPath}.{member.Name}";
                    if (!_ignore.Matches(childPath))
                    {
                        _operations.Add($"{{ \"op\": \"remove\", \"path\": \"{JsonPointer.Escape(pointer, member.Name)}\" }}");
                    }
                }
            }

            foreach (var member in right.Members)
            {
                var childPointer = JsonPointer.Escape(pointer, member.Name);
                var childPath = $"{jsonPath}.{member.Name}";

                if (_ignore.Matches(childPath))
                {
                    continue;
                }

                if (leftMembers.TryGetValue(member.Name, out var leftValue))
                {
                    Walk(childPointer, childPath, leftValue, member.Value);
                }
                else
                {
                    Add(childPointer, member.Value);
                }
            }
        }

        private Dictionary<string, JsonNode> Index(JsonObject obj)
        {
            var map = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            foreach (var member in obj.Members)
            {
                map[member.Name] = member.Value;
            }

            return map;
        }

        /// <summary>
        /// Array edits are emitted while tracking a cursor into the array <em>as it evolves</em>,
        /// so every index in the patch is valid at the moment that operation is applied. This
        /// is the part that a naive differ gets wrong: emitting indices from the original
        /// array makes the patch apply to the wrong elements.
        /// </summary>
        private void WalkArray(string pointer, string jsonPath, JsonArray left, JsonArray right)
        {
            var a = left.Items;
            var b = right.Items;
            var n = a.Count;
            var m = b.Count;

            var lcs = BuildLcsTable(a, b);

            var i = 0;
            var j = 0;
            var cursor = 0;

            while (i < n && j < m)
            {
                if (string.Equals(a[i].StructuralHash(), b[j].StructuralHash(), StringComparison.Ordinal))
                {
                    i++;
                    j++;
                    cursor++;
                    continue;
                }

                if (lcs[i + 1, j] >= lcs[i, j + 1])
                {
                    // The left element has no partner: remove it. The cursor stays put,
                    // because the next element shifts into this position.
                    _operations.Add($"{{ \"op\": \"remove\", \"path\": \"{pointer}/{cursor}\" }}");
                    i++;
                }
                else
                {
                    Add($"{pointer}/{cursor}", b[j]);
                    j++;
                    cursor++;
                }
            }

            while (i < n)
            {
                _operations.Add($"{{ \"op\": \"remove\", \"path\": \"{pointer}/{cursor}\" }}");
                i++;
            }

            while (j < m)
            {
                Add($"{pointer}/{cursor}", b[j]);
                j++;
                cursor++;
            }

            _ = jsonPath;
        }

        private static int[,] BuildLcsTable(IReadOnlyList<JsonNode> a, IReadOnlyList<JsonNode> b)
        {
            var n = a.Count;
            var m = b.Count;
            var table = new int[n + 1, m + 1];

            var leftHashes = new string[n];
            for (var i = 0; i < n; i++)
            {
                leftHashes[i] = a[i].StructuralHash();
            }

            var rightHashes = new string[m];
            for (var j = 0; j < m; j++)
            {
                rightHashes[j] = b[j].StructuralHash();
            }

            for (var i = n - 1; i >= 0; i--)
            {
                for (var j = m - 1; j >= 0; j--)
                {
                    table[i, j] = string.Equals(leftHashes[i], rightHashes[j], StringComparison.Ordinal)
                        ? table[i + 1, j + 1] + 1
                        : Math.Max(table[i + 1, j], table[i, j + 1]);
                }
            }

            return table;
        }

        private bool ScalarsEqual(JsonNode left, JsonNode right) => (left, right) switch
        {
            (JsonNull, JsonNull) => true,
            (JsonBool x, JsonBool y) => x.Value == y.Value,
            (JsonString x, JsonString y) => string.Equals(x.Value, y.Value, options.ValueComparison),
            (JsonNumber x, JsonNumber y) => string.Equals(x.Canonical, y.Canonical, StringComparison.Ordinal),
            _ => false,
        };

        private void Add(string pointer, JsonNode value) =>
            _operations.Add($"{{ \"op\": \"add\", \"path\": \"{pointer}\", \"value\": {JsonWriter.Write(value, JsonWriterOptions.Compact)} }}");

        private void Replace(string pointer, JsonNode value) =>
            _operations.Add(pointer.Length == 0
                ? $"{{ \"op\": \"replace\", \"path\": \"\", \"value\": {JsonWriter.Write(value, JsonWriterOptions.Compact)} }}"
                : $"{{ \"op\": \"replace\", \"path\": \"{pointer}\", \"value\": {JsonWriter.Write(value, JsonWriterOptions.Compact)} }}");
    }
}

/// <summary>RFC 6901 JSON Pointer escaping and parsing.</summary>
public static class JsonPointer
{
    /// <summary>Appends one token to a pointer, escaping <c>~</c> and <c>/</c> per RFC 6901.</summary>
    public static string Escape(string parent, string token) =>
        $"{parent}/{token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";

    /// <summary>Splits a pointer into its unescaped tokens. An empty pointer is the whole document.</summary>
    public static IReadOnlyList<string> Parse(string? pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer == "/")
        {
            return pointer == "/" ? [string.Empty] : [];
        }

        var raw = pointer.StartsWith('/') ? pointer[1..] : pointer;

        return [.. raw.Split('/').Select(static t =>
            t.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))];
    }
}
