namespace DevTools.Core.Json;

/// <summary>
/// One row of a browsable JSON document: a name, a summary of its value, and its children.
/// </summary>
/// <remarks>
/// The model is laid out for a view that expands and collapses, so every row carries what the
/// collapsed form has to say — <see cref="Summary"/> for a leaf, <see cref="ChildSummary"/> for
/// a branch — and the children are only walked when something asks for them. A 10 MB document
/// has to open instantly and stay responsive, which it cannot do if opening it means building a
/// view object per node first.
/// </remarks>
public sealed class JsonOutlineNode
{
    private readonly JsonNode _node;
    private IReadOnlyList<JsonOutlineNode>? _children;

    internal JsonOutlineNode(string name, JsonNode node, string path, int index)
    {
        Name = name;
        Path = path;
        Index = index;
        _node = node;
    }

    /// <summary>The member name, or the index in brackets for an array element.</summary>
    public string Name { get; }

    /// <summary>The JSONPath to this node, so a row can be copied as a query.</summary>
    public string Path { get; }

    /// <summary>Position among its siblings. Zero for the root.</summary>
    public int Index { get; }

    public JsonKind Kind => _node.Kind;

    public bool IsBranch => _node.Kind is JsonKind.Object or JsonKind.Array;

    /// <summary>The line the node starts on, for taking the text view to the same place.</summary>
    public int Line => _node.Position.Line;

    /// <summary>How many members or elements a branch has. Zero for a leaf.</summary>
    public int ChildCount => _node switch
    {
        JsonObject o => o.Members.Count,
        JsonArray a => a.Items.Count,
        _ => 0,
    };

    /// <summary>
    /// What a leaf shows after its name, in JSON's own notation.
    /// </summary>
    /// <remarks>
    /// Numbers keep their source text rather than being re-rendered, for the same reason the
    /// formatter does: <c>1.0</c> is not <c>1</c>, and a 30-digit integer survives no round trip
    /// through a double.
    /// </remarks>
    public string Summary => _node switch
    {
        JsonNull => "null",
        JsonBool b => b.Value ? "true" : "false",
        JsonNumber n => n.Raw,
        JsonString s => $"\"{s.Value}\"",
        _ => string.Empty,
    };

    /// <summary>
    /// Kind as three flags, so a row can be coloured in markup with <c>{ThemeResource}</c>.
    /// </summary>
    /// <remarks>
    /// A value converter would be the obvious way, and the wrong one: a converter has no
    /// element to resolve a theme resource against and has to ask the application, which gives
    /// the wrong answer the moment the window is showing a theme the app did not start in.
    /// </remarks>
    public bool IsString => Kind == JsonKind.String;

    public bool IsNumber => Kind == JsonKind.Number;

    /// <summary>true, false and null — JSON's keywords.</summary>
    public bool IsKeyword => Kind is JsonKind.Bool or JsonKind.Null;

    /// <summary>What a branch shows instead of a value: how much is folded away.</summary>
    public string ChildSummary => _node switch
    {
        JsonObject o => o.Members.Count == 1 ? "{ 1 key }" : $"{{ {o.Members.Count:N0} keys }}",
        JsonArray a => a.Items.Count == 1 ? "[ 1 item ]" : $"[ {a.Items.Count:N0} items ]",
        _ => string.Empty,
    };

    /// <summary>
    /// The children, built on first ask and kept. Empty for a leaf.
    /// </summary>
    public IReadOnlyList<JsonOutlineNode> Children => _children ??= BuildChildren();

    private IReadOnlyList<JsonOutlineNode> BuildChildren()
    {
        switch (_node)
        {
            case JsonObject o:
            {
                var children = new List<JsonOutlineNode>(o.Members.Count);

                for (var i = 0; i < o.Members.Count; i++)
                {
                    var member = o.Members[i];
                    children.Add(new JsonOutlineNode(member.Name, member.Value, JoinMember(Path, member.Name), i));
                }

                return children;
            }

            case JsonArray a:
            {
                var children = new List<JsonOutlineNode>(a.Items.Count);

                for (var i = 0; i < a.Items.Count; i++)
                {
                    children.Add(new JsonOutlineNode($"[{i}]", a.Items[i], $"{Path}[{i}]", i));
                }

                return children;
            }

            default:
                return [];
        }
    }

    /// <summary>
    /// Appends a member to a JSONPath, bracketing the name when dot notation cannot carry it.
    /// </summary>
    /// <remarks>
    /// A name with a dot, a space or a bracket in it is legal JSON and illegal in dot notation,
    /// so a path built by naive concatenation would be a query that selects the wrong thing.
    /// </remarks>
    private static string JoinMember(string parent, string name) =>
        name.Length > 0 && name.All(static c => char.IsLetterOrDigit(c) || c == '_')
            ? $"{parent}.{name}"
            : $"{parent}['{name.Replace("'", "\\'", StringComparison.Ordinal)}']";
}

/// <summary>Builds a browsable outline of a JSON document (FR-J11).</summary>
public static class JsonOutline
{
    /// <summary>
    /// Parses and projects, returning the root row. The same reader as the formatter, so the
    /// tree and the text can never disagree about what the document says.
    /// </summary>
    public static OperationResult<JsonOutlineNode> Build(string? json, JsonFormatOptions? options = null)
    {
        var opts = options ?? JsonFormatOptions.Default;
        var parsed = JsonReader.Parse(json, opts.ToReaderOptions());

        if (!parsed.IsSuccess)
        {
            return OperationResult<JsonOutlineNode>.Fail(parsed.Error!);
        }

        return OperationResult<JsonOutlineNode>.Ok(
            new JsonOutlineNode("$", parsed.Value!.Root, "$", 0));
    }
}
