using System.Text;
using DevTools.Core.Json;
using DevTools.Core.Text;

namespace DevTools.Core.CodeGen;

/// <summary>
/// Generates C# model types from a JSON sample (FR-J40…FR-J47).
/// </summary>
/// <remarks>
/// The bar this holds itself to is not "it produces C#" but "it produces C# that compiles and
/// round-trips the sample it was given" — which is what the acceptance test actually asserts,
/// by compiling the output with Roslyn and deserialising the original document into it.
/// </remarks>
public static class CSharpFromJson
{
    public static OperationResult<CodeGenResult> Generate(string? json, CSharpGenOptions? options = null)
    {
        var opts = options ?? CSharpGenOptions.Default;

        if (TextUtil.IsBlank(json))
        {
            return OperationResult<CodeGenResult>.Ok(new CodeGenResult(string.Empty, 0, []));
        }

        var parsed = JsonReader.Parse(json, JsonReaderOptions.Tolerant);
        if (!parsed.IsSuccess)
        {
            return OperationResult<CodeGenResult>.Fail(parsed.Error!);
        }

        return Generate(parsed.Value!.Root, opts, parsed.Value.Warnings);
    }

    public static OperationResult<CodeGenResult> Generate(
        JsonNode root,
        CSharpGenOptions? options = null,
        IReadOnlyList<string>? inheritedWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        var opts = options ?? CSharpGenOptions.Default;
        var warnings = new List<string>(inheritedWarnings ?? []);

        var builder = new ShapeBuilder(opts.DetectDateGuidUri);
        var shape = builder.Build(root);
        warnings.AddRange(builder.Warnings);

        // A document whose root is an array still describes one element type; say so rather
        // than inventing a wrapper type nobody asked for.
        var rootShape = shape;
        var rootIsArray = false;

        while (rootShape is ArrayShape array)
        {
            rootShape = array.Element;
            rootIsArray = true;
        }

        var rootName = IdentifierFactory.Pascal(opts.RootTypeName);

        if (rootIsArray)
        {
            warnings.Add($"The document's root is an array, so deserialise it as {Collection(rootName, opts)}.");
        }

        if (rootShape is not ObjectShape rootObject)
        {
            warnings.Add("The sample contains no object, so there is no model to generate.");
            return OperationResult<CodeGenResult>.Ok(new CodeGenResult(string.Empty, 0, warnings));
        }

        var graph = new TypeGraph(opts);
        var emitted = graph.Register(rootObject, rootName);

        if (graph.Types.Count > Limits.MaxGeneratedTypes)
        {
            return OperationResult<CodeGenResult>.Fail(
                $"The sample implies {graph.Types.Count:N0} types, above the {Limits.MaxGeneratedTypes:N0} limit. Generate from a smaller sample.");
        }

        graph.ApplyNesting(emitted);

        var code = new CSharpEmitter(opts, graph).Emit(emitted);

        // Collected after emitting: the emitter is where member-name collisions surface.
        warnings.AddRange(graph.Warnings);

        return OperationResult<CodeGenResult>.Ok(new CodeGenResult(code, graph.Types.Count, warnings));
    }

    internal static string Collection(string element, CSharpGenOptions options) => options.CollectionKind switch
    {
        CollectionKind.Array => $"{element}[]",
        CollectionKind.IReadOnlyList => $"IReadOnlyList<{element}>",
        _ => $"List<{element}>",
    };
}

/// <summary>One generated type: a name plus the object shape it came from.</summary>
internal sealed class GeneratedType(string name, ObjectShape shape)
{
    /// <summary>Settable because nesting can force a rename to avoid CS0102.</summary>
    public string Name { get; set; } = name;

    public ObjectShape Shape { get; } = shape;

    /// <summary>Types declared inside this one when nesting is on.</summary>
    public List<GeneratedType> Nested { get; } = [];

    /// <summary>True once some other type has claimed this one as a nested declaration.</summary>
    public bool IsNested { get; set; }
}

/// <summary>
/// Assigns a name to every object shape, and emits one type per <em>distinct</em> shape
/// rather than one per occurrence (FR-J46).
/// </summary>
internal sealed class TypeGraph(CSharpGenOptions options)
{
    private readonly Dictionary<string, GeneratedType> _bySignature = new(StringComparer.Ordinal);
    private readonly HashSet<string> _takenNames = new(StringComparer.Ordinal);
    private readonly Dictionary<GeneratedType, HashSet<GeneratedType>> _referencedBy = [];
    private readonly List<string> _warnings = [];

    public List<GeneratedType> Types { get; } = [];

    public IReadOnlyList<string> Warnings => _warnings;

    public GeneratedType Register(ObjectShape shape, string preferredName)
    {
        var signature = Signature(shape);

        if (_bySignature.TryGetValue(signature, out var existing))
        {
            return existing;
        }

        var name = IdentifierFactory.Unique(preferredName, _takenNames);
        var type = new GeneratedType(name, shape);

        // Recorded before descending, so a self-referencing shape terminates.
        _bySignature[signature] = type;
        Types.Add(type);

        foreach (var property in shape.Properties)
        {
            RegisterChildren(property.Shape, property.Name, type);
        }

        return type;
    }

    private void RegisterChildren(JsonShape shape, string memberName, GeneratedType parent)
    {
        switch (shape)
        {
            case ObjectShape obj:
            {
                var child = Register(obj, IdentifierFactory.Pascal(memberName));

                if (!ReferenceEquals(child, parent))
                {
                    if (!_referencedBy.TryGetValue(child, out var parents))
                    {
                        _referencedBy[child] = parents = [];
                    }

                    parents.Add(parent);
                }

                break;
            }

            case ArrayShape array:
                // An array member names its element type in the singular: "addresses" → Address.
                RegisterChildren(array.Element, IdentifierFactory.Singular(memberName), parent);
                break;
        }
    }

    /// <summary>
    /// Decides which types are declared inside which, once the whole graph is known.
    /// </summary>
    /// <remarks>
    /// Two things make this more than a formatting choice, and both are silent compile errors
    /// if they are got wrong:
    /// <list type="bullet">
    /// <item>A type used by more than one parent cannot be nested inside either of them — the
    /// other parent would have no way to name it. Those stay top-level.</item>
    /// <item>A nested type may not share its name with a member of the type that declares it
    /// (CS0102) — and by construction it always would, because both are named after the same
    /// JSON member. The nested type is renamed, and the caller is told.</item>
    /// </list>
    /// </remarks>
    public void ApplyNesting(GeneratedType root)
    {
        if (!options.NestTypes)
        {
            return;
        }

        foreach (var type in Types)
        {
            if (ReferenceEquals(type, root) ||
                !_referencedBy.TryGetValue(type, out var parents) ||
                parents.Count != 1)
            {
                continue;
            }

            var parent = parents.First();

            var memberNames = parent.Shape.Properties
                .Select(static p => IdentifierFactory.Pascal(p.Name))
                .ToHashSet(StringComparer.Ordinal);

            if (memberNames.Contains(type.Name))
            {
                var renamed = IdentifierFactory.Unique(type.Name + "Data", _takenNames);
                Warn($"Nested type {type.Name} was renamed to {renamed}: a nested type cannot share its name with a member of {parent.Name}.");
                type.Name = renamed;
            }

            parent.Nested.Add(type);
            type.IsNested = true;
        }
    }

    /// <summary>
    /// A structural fingerprint. Two shapes with the same fingerprint are the same type, which
    /// is what stops a payload with ten identical address objects producing ten Address types.
    /// </summary>
    private static string Signature(JsonShape shape)
    {
        var builder = new StringBuilder();
        Write(shape, builder, 0);
        return builder.ToString();

        static void Write(JsonShape node, StringBuilder sink, int depth)
        {
            if (depth > Limits.MaxJsonDepth)
            {
                sink.Append('…');
                return;
            }

            switch (node)
            {
                case ScalarShape scalar:
                    sink.Append((int)scalar.Kind).Append(';');
                    break;

                case ArrayShape array:
                    sink.Append('[');
                    Write(array.Element, sink, depth + 1);
                    sink.Append(']');
                    break;

                case ObjectShape obj:
                    sink.Append('{');
                    foreach (var property in obj.Properties.OrderBy(static p => p.Name, StringComparer.Ordinal))
                    {
                        sink.Append(property.Name).Append(obj.IsNullable(property) ? "?:" : ":");
                        Write(property.Shape, sink, depth + 1);
                        sink.Append(',');
                    }

                    sink.Append('}');
                    break;
            }
        }
    }

    public void Warn(string message)
    {
        if (!_warnings.Contains(message, StringComparer.Ordinal))
        {
            _warnings.Add(message);
        }
    }
}

/// <summary>Renders the type graph as C# source.</summary>
internal sealed class CSharpEmitter(CSharpGenOptions options, TypeGraph graph)
{
    private readonly StringBuilder _builder = new();

    public string Emit(GeneratedType root)
    {
        EmitUsings();
        var indent = EmitNamespace();

        var order = Order(root);

        for (var i = 0; i < order.Count; i++)
        {
            if (i > 0)
            {
                _builder.AppendLine();
            }

            EmitType(order[i], indent);
        }

        if (options.NamespaceStyle == NamespaceStyle.Block)
        {
            _builder.AppendLine("}");
        }

        return _builder.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// Root first, then every type that was not claimed as a nested declaration — the order a
    /// reader wants, and the set that must be declared at the top level to compile.
    /// </summary>
    private List<GeneratedType> Order(GeneratedType root)
    {
        var order = new List<GeneratedType> { root };
        order.AddRange(graph.Types.Where(t => !ReferenceEquals(t, root) && !t.IsNested));
        return order;
    }

    private void EmitUsings()
    {
        var usings = new SortedSet<string>(StringComparer.Ordinal);

        if (NeedsSystem())
        {
            usings.Add("System");
        }

        if (options.CollectionKind != CollectionKind.Array && UsesCollections())
        {
            usings.Add("System.Collections.Generic");
        }

        switch (options.AttributeStyle)
        {
            case AttributeStyle.SystemTextJson:
                usings.Add("System.Text.Json.Serialization");
                break;
            case AttributeStyle.NewtonsoftJson:
                usings.Add("Newtonsoft.Json");
                break;
        }

        if (usings.Count == 0)
        {
            return;
        }

        foreach (var name in usings)
        {
            _builder.Append("using ").Append(name).AppendLine(";");
        }

        _builder.AppendLine();
    }

    private bool NeedsSystem() => graph.Types
        .SelectMany(static t => t.Shape.Properties)
        .Any(static p => Mentions(p.Shape, static k => k is ScalarKind.DateTimeOffset or ScalarKind.Guid or ScalarKind.Uri));

    private bool UsesCollections() => graph.Types
        .SelectMany(static t => t.Shape.Properties)
        .Any(static p => p.Shape is ArrayShape);

    private static bool Mentions(JsonShape shape, Func<ScalarKind, bool> predicate) => shape switch
    {
        ScalarShape scalar => predicate(scalar.Kind),
        ArrayShape array => Mentions(array.Element, predicate),
        _ => false,
    };

    private string EmitNamespace()
    {
        switch (options.NamespaceStyle)
        {
            case NamespaceStyle.FileScoped:
                _builder.Append("namespace ").Append(SafeNamespace()).AppendLine(";").AppendLine();
                return string.Empty;

            case NamespaceStyle.Block:
                _builder.Append("namespace ").AppendLine(SafeNamespace()).AppendLine("{");
                return "    ";

            default:
                return string.Empty;
        }
    }

    private string SafeNamespace()
    {
        var parts = (options.NamespaceName ?? string.Empty)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(IdentifierFactory.Pascal)
            .ToList();

        return parts.Count == 0 ? "Generated" : string.Join('.', parts);
    }

    private void EmitType(GeneratedType type, string indent)
    {
        _builder.Append(indent).Append("public ").Append(Declaration()).Append(' ').AppendLine(type.Name);
        _builder.Append(indent).AppendLine("{");

        var inner = indent + "    ";
        var taken = new HashSet<string>(StringComparer.Ordinal) { type.Name };
        var first = true;

        foreach (var property in type.Shape.Properties)
        {
            if (!first)
            {
                _builder.AppendLine();
            }

            first = false;
            EmitProperty(type, property, inner, taken);
        }

        if (options.NestTypes)
        {
            foreach (var nested in type.Nested)
            {
                _builder.AppendLine();
                EmitType(nested, inner);
            }
        }

        _builder.Append(indent).AppendLine("}");
    }

    private string Declaration() => options.TypeKind switch
    {
        CSharpTypeKind.Class => "class",
        CSharpTypeKind.ReadonlyRecordStruct => "readonly record struct",
        _ => "record",
    };

    private void EmitProperty(GeneratedType owner, PropertyShape property, string indent, HashSet<string> taken)
    {
        var nullable = owner.Shape.IsNullable(property);
        var typeName = TypeName(property.Shape, property.Name, nullable);
        var memberName = MemberName(owner, property, taken);

        if (options.AttributeStyle != AttributeStyle.None)
        {
            var attribute = options.AttributeStyle == AttributeStyle.SystemTextJson
                ? "JsonPropertyName"
                : "JsonProperty";

            _builder.Append(indent).Append('[').Append(attribute)
                .Append("(\"").Append(EscapeLiteral(property.Name)).AppendLine("\")]");
        }

        _builder.Append(indent).Append("public ");

        // 'required' and a nullable member contradict each other in intent; a member that may
        // be absent cannot be one the compiler insists on.
        if (options.MemberStyle == MemberStyle.Required && !nullable)
        {
            _builder.Append("required ");
        }

        _builder.Append(typeName).Append(' ').Append(memberName).Append(' ');

        _builder.AppendLine(options.MemberStyle == MemberStyle.GetSet
            ? "{ get; set; }"
            : "{ get; init; }");
    }

    /// <summary>
    /// A member may not share its name with the type that declares it (CS0542), and two JSON
    /// names can PascalCase to the same identifier.
    /// </summary>
    private string MemberName(GeneratedType owner, PropertyShape property, HashSet<string> taken)
    {
        var candidate = IdentifierFactory.Pascal(property.Name);

        if (string.Equals(candidate, owner.Name, StringComparison.Ordinal))
        {
            candidate += "Value";
        }

        var unique = IdentifierFactory.Unique(candidate, taken);

        if (!string.Equals(unique, candidate, StringComparison.Ordinal))
        {
            graph.Warn($"\"{property.Name}\" collides with another member of {owner.Name} once converted to PascalCase; it was emitted as {unique}.");
        }

        return unique;
    }

    private string TypeName(JsonShape shape, string memberName, bool nullable)
    {
        switch (shape)
        {
            case ArrayShape array:
            {
                var element = TypeName(array.Element, IdentifierFactory.Singular(memberName), nullable: false);
                var collection = CSharpFromJson.Collection(element, options);
                return Annotate(collection, nullable, isValueType: false);
            }

            case ObjectShape obj:
            {
                var type = graph.Register(obj, IdentifierFactory.Pascal(memberName));
                return Annotate(type.Name, nullable, isValueType: options.TypeKind == CSharpTypeKind.ReadonlyRecordStruct);
            }

            case ScalarShape scalar:
            {
                var (name, isValueType) = Scalar(scalar.Kind);
                return Annotate(name, nullable, isValueType);
            }

            default:
                return Annotate("object", nullable, isValueType: false);
        }
    }

    private (string Name, bool IsValueType) Scalar(ScalarKind kind) => kind switch
    {
        ScalarKind.Bool => ("bool", true),
        ScalarKind.Integer => ("int", true),
        ScalarKind.Long => ("long", true),
        ScalarKind.Decimal => options.FractionalNumberType == FractionalNumberType.Double
            ? ("double", true)
            : ("decimal", true),
        ScalarKind.Double => ("double", true),
        ScalarKind.Guid => ("Guid", true),
        ScalarKind.DateTimeOffset => ("DateTimeOffset", true),
        ScalarKind.Uri => ("Uri", false),
        ScalarKind.String => ("string", false),
        _ => ("object", false),
    };

    private string Annotate(string name, bool nullable, bool isValueType)
    {
        if (!nullable)
        {
            return name;
        }

        // A nullable value type is always annotated: 'int?' is a different type, not a hint.
        // A nullable reference type is annotated only when the caller wants the annotations.
        return isValueType || options.NullableAnnotations ? name + "?" : name;
    }

    private static string EscapeLiteral(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
