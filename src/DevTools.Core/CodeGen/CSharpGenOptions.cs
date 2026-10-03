namespace DevTools.Core.CodeGen;

/// <summary>What shape of type each object becomes.</summary>
public enum CSharpTypeKind
{
    Record,
    Class,
    ReadonlyRecordStruct,
}

/// <summary>How members are declared.</summary>
public enum MemberStyle
{
    /// <summary><c>{ get; set; }</c> — mutable, works with every serializer.</summary>
    GetSet,

    /// <summary><c>{ get; init; }</c> — settable only during construction.</summary>
    GetInit,

    /// <summary><c>required … { get; init; }</c> — the compiler enforces that each one is set.</summary>
    Required,
}

/// <summary>Which serializer's property-name attribute is emitted.</summary>
public enum AttributeStyle
{
    SystemTextJson,
    NewtonsoftJson,
    None,
}

/// <summary>How a JSON array becomes a C# member type.</summary>
public enum CollectionKind
{
    List,
    Array,
    IReadOnlyList,
}

/// <summary>How the namespace declaration is written.</summary>
public enum NamespaceStyle
{
    FileScoped,
    Block,
    None,
}

/// <summary>Everything the C# generator lets the caller choose (FR-J44).</summary>
public sealed record CSharpGenOptions
{
    public CSharpTypeKind TypeKind { get; init; } = CSharpTypeKind.Record;

    public MemberStyle MemberStyle { get; init; } = MemberStyle.GetInit;

    public AttributeStyle AttributeStyle { get; init; } = AttributeStyle.SystemTextJson;

    public CollectionKind CollectionKind { get; init; } = CollectionKind.List;

    /// <summary>Annotates nullable reference types with <c>?</c>.</summary>
    public bool NullableAnnotations { get; init; } = true;

    /// <summary>Probes string values for <c>DateTimeOffset</c>, <c>Guid</c> and <c>Uri</c> shapes (FR-J43).</summary>
    public bool DetectDateGuidUri { get; init; } = true;

    public NamespaceStyle NamespaceStyle { get; init; } = NamespaceStyle.FileScoped;

    public string NamespaceName { get; init; } = "Generated";

    /// <summary>Nests child types inside their parent instead of emitting them as siblings.</summary>
    public bool NestTypes { get; init; }

    public string RootTypeName { get; init; } = "Root";

    public static CSharpGenOptions Default { get; } = new();
}

/// <summary>The generated source, plus anything the caller should know about it.</summary>
public sealed record CodeGenResult(string Code, int TypeCount, IReadOnlyList<string> Warnings);
