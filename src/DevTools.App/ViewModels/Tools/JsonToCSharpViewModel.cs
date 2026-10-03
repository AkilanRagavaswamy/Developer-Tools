using CommunityToolkit.Mvvm.ComponentModel;
using DevTools.App.Services;
using DevTools.Core.CodeGen;

namespace DevTools.App.ViewModels.Tools;

/// <summary>JSON to C# Generator (FR-J40…FR-J47).</summary>
public sealed partial class JsonToCSharpViewModel : TextToolViewModelBase
{
    public JsonToCSharpViewModel(ToolServices services)
        : base(services)
    {
        TypeKind = CSharpTypeKind.Class;
        MemberStyle = MemberStyle.GetSet;
        AttributeStyle = AttributeStyle.NewtonsoftJson;
        CollectionKind = CollectionKind.List;
        FractionalNumberType = FractionalNumberType.Double;
        NamespaceStyle = NamespaceStyle.FileScoped;
        NamespaceName = DefaultNamespace;
        RootTypeName = "Root";
        NullableAnnotations = true;
        DetectDateGuidUri = true;
        StatsText = string.Empty;
    }

    /// <summary>What the namespace box starts at, and what the Generation badge compares to.</summary>
    private const string DefaultNamespace = "Generated";

    public override string ToolId => "json-to-csharp";

    protected override string SuggestedFileName => RootTypeName;

    protected override string[] OutputExtensions => [".cs", ".txt"];

    protected override string[] InputExtensions => [".json", ".txt"];

    // ---------------------------------------------------------------- options

    [ObservableProperty]
    public partial CSharpTypeKind TypeKind { get; set; }

    [ObservableProperty]
    public partial MemberStyle MemberStyle { get; set; }

    [ObservableProperty]
    public partial AttributeStyle AttributeStyle { get; set; }

    [ObservableProperty]
    public partial CollectionKind CollectionKind { get; set; }

    [ObservableProperty]
    public partial FractionalNumberType FractionalNumberType { get; set; }

    [ObservableProperty]
    public partial NamespaceStyle NamespaceStyle { get; set; }

    [ObservableProperty]
    public partial string NamespaceName { get; set; }

    [ObservableProperty]
    public partial string RootTypeName { get; set; }

    [ObservableProperty]
    public partial bool NullableAnnotations { get; set; }

    [ObservableProperty]
    public partial bool DetectDateGuidUri { get; set; }

    [ObservableProperty]
    public partial bool NestTypes { get; set; }

    [ObservableProperty]
    public partial string StatsText { get; set; }

    public IReadOnlyList<CSharpTypeKind> TypeKinds { get; } =
        [CSharpTypeKind.Class, CSharpTypeKind.ReadonlyRecordStruct, CSharpTypeKind.Record];

    public IReadOnlyList<MemberStyle> MemberStyles { get; } =
        [MemberStyle.GetSet, MemberStyle.GetInit, MemberStyle.Required];

    public IReadOnlyList<AttributeStyle> AttributeStyles { get; } =
        [AttributeStyle.NewtonsoftJson, AttributeStyle.SystemTextJson, AttributeStyle.None];

    public IReadOnlyList<CollectionKind> CollectionKinds { get; } =
        [CollectionKind.List, CollectionKind.Array, CollectionKind.IReadOnlyList];

    public IReadOnlyList<FractionalNumberType> FractionalNumberTypes { get; } =
        [FractionalNumberType.Double, FractionalNumberType.Decimal];

    public IReadOnlyList<NamespaceStyle> NamespaceStyles { get; } =
        [NamespaceStyle.FileScoped, NamespaceStyle.Block, NamespaceStyle.None];

    public bool IsNamespaceNameEnabled => NamespaceStyle != NamespaceStyle.None;

    /// <summary>Combo-box indices. See <see cref="OptionListExtensions"/> for why these exist.</summary>
    public int TypeKindIndex
    {
        get => TypeKinds.IndexOfValue(TypeKind);
        set => TypeKind = TypeKinds.ValueAt(value, CSharpTypeKind.Class);
    }

    public int MemberStyleIndex
    {
        get => MemberStyles.IndexOfValue(MemberStyle);
        set => MemberStyle = MemberStyles.ValueAt(value, MemberStyle.GetSet);
    }

    public int AttributeStyleIndex
    {
        get => AttributeStyles.IndexOfValue(AttributeStyle);
        set => AttributeStyle = AttributeStyles.ValueAt(value, AttributeStyle.NewtonsoftJson);
    }

    public int CollectionKindIndex
    {
        get => CollectionKinds.IndexOfValue(CollectionKind);
        set => CollectionKind = CollectionKinds.ValueAt(value, CollectionKind.List);
    }

    public int FractionalNumberTypeIndex
    {
        get => FractionalNumberTypes.IndexOfValue(FractionalNumberType);
        set => FractionalNumberType = FractionalNumberTypes.ValueAt(value, FractionalNumberType.Double);
    }

    public int NamespaceStyleIndex
    {
        get => NamespaceStyles.IndexOfValue(NamespaceStyle);
        set => NamespaceStyle = NamespaceStyles.ValueAt(value, NamespaceStyle.FileScoped);
    }

    partial void OnTypeKindChanged(CSharpTypeKind value)
    {
        OnPropertyChanged(nameof(TypeKindIndex));
        OnOptionChanged();
    }

    partial void OnMemberStyleChanged(MemberStyle value)
    {
        OnPropertyChanged(nameof(MemberStyleIndex));
        OnOptionChanged();
    }

    partial void OnAttributeStyleChanged(AttributeStyle value)
    {
        OnPropertyChanged(nameof(AttributeStyleIndex));
        OnOptionChanged();
    }

    partial void OnCollectionKindChanged(CollectionKind value)
    {
        OnPropertyChanged(nameof(CollectionKindIndex));
        RaiseGenerationCount();
        OnOptionChanged();
    }

    partial void OnFractionalNumberTypeChanged(FractionalNumberType value)
    {
        OnPropertyChanged(nameof(FractionalNumberTypeIndex));
        RaiseGenerationCount();
        OnOptionChanged();
    }

    partial void OnNamespaceStyleChanged(NamespaceStyle value)
    {
        OnPropertyChanged(nameof(IsNamespaceNameEnabled));
        OnPropertyChanged(nameof(NamespaceStyleIndex));
        RaiseGenerationCount();
        OnOptionChanged();
    }

    partial void OnNamespaceNameChanged(string value)
    {
        RaiseGenerationCount();
        OnOptionChanged();
    }

    partial void OnRootTypeNameChanged(string value) => OnOptionChanged();

    // ---------------------------------------------------------------- options bar

    /// <summary>
    /// How many of the settings behind Generation are away from their default. Those settings
    /// are the ones you choose once for a codebase and then forget, which is exactly why the
    /// button has to say when one of them is no longer what you would expect.
    /// </summary>
    public int GenerationCount =>
        (CollectionKind == CollectionKind.List ? 0 : 1) +
        (FractionalNumberType == FractionalNumberType.Double ? 0 : 1) +
        (NamespaceStyle == NamespaceStyle.FileScoped ? 0 : 1) +
        (NamespaceName == DefaultNamespace ? 0 : 1) +
        (NullableAnnotations ? 0 : 1) +
        (DetectDateGuidUri ? 0 : 1) +
        (NestTypes ? 1 : 0);

    public bool HasGenerationChanges => GenerationCount > 0;

    private void RaiseGenerationCount()
    {
        OnPropertyChanged(nameof(GenerationCount));
        OnPropertyChanged(nameof(HasGenerationChanges));
    }

    partial void OnNullableAnnotationsChanged(bool value)
    {
        RaiseGenerationCount();
        OnOptionChanged();
    }

    partial void OnDetectDateGuidUriChanged(bool value)
    {
        RaiseGenerationCount();
        OnOptionChanged();
    }

    partial void OnNestTypesChanged(bool value)
    {
        RaiseGenerationCount();
        OnOptionChanged();
    }

    // ---------------------------------------------------------------- run

    protected override async Task RunCoreAsync(CancellationToken token)
    {
        if (HandleEmptyInput())
        {
            StatsText = string.Empty;
            return;
        }

        var input = Input;

        var options = new CSharpGenOptions
        {
            TypeKind = TypeKind,
            MemberStyle = MemberStyle,
            AttributeStyle = AttributeStyle,
            CollectionKind = CollectionKind,
            FractionalNumberType = FractionalNumberType,
            NamespaceStyle = NamespaceStyle,
            NamespaceName = string.IsNullOrWhiteSpace(NamespaceName) ? "Generated" : NamespaceName.Trim(),
            RootTypeName = string.IsNullOrWhiteSpace(RootTypeName) ? "Root" : RootTypeName.Trim(),
            NullableAnnotations = NullableAnnotations,
            DetectDateGuidUri = DetectDateGuidUri,
            NestTypes = NestTypes,
        };

        var result = await ComputeAsync(() => CSharpFromJson.Generate(input, options), token);

        if (!result.IsSuccess)
        {
            SetError(result.Error!);
            return;
        }

        var value = result.Value!;
        Output = value.Code;

        StatsText = value.TypeCount switch
        {
            0 => string.Empty,
            1 => "1 type",
            _ => $"{value.TypeCount:N0} types",
        };

        ReportWarnings(value.Warnings);
    }

    // ---------------------------------------------------------------- state

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("typeKind", TypeKind);
        state.Set("memberStyle", MemberStyle);
        state.Set("attributes", AttributeStyle);
        state.Set("collections", CollectionKind);
        state.Set("fractional", FractionalNumberType);
        state.Set("namespaceStyle", NamespaceStyle);
        state.Set("namespaceName", NamespaceName);
        state.Set("rootTypeName", RootTypeName);
        state.Set("nullable", NullableAnnotations);
        state.Set("detect", DetectDateGuidUri);
        state.Set("nest", NestTypes);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        TypeKind = state.GetEnum("typeKind", CSharpTypeKind.Class);
        MemberStyle = state.GetEnum("memberStyle", MemberStyle.GetSet);
        AttributeStyle = state.GetEnum("attributes", AttributeStyle.NewtonsoftJson);
        CollectionKind = state.GetEnum("collections", CollectionKind.List);
        FractionalNumberType = state.GetEnum("fractional", FractionalNumberType.Double);
        NamespaceStyle = state.GetEnum("namespaceStyle", NamespaceStyle.FileScoped);
        NamespaceName = state.GetString("namespaceName", "Generated");
        RootTypeName = state.GetString("rootTypeName", "Root");
        NullableAnnotations = state.GetBool("nullable", true);
        DetectDateGuidUri = state.GetBool("detect", true);
        NestTypes = state.GetBool("nest");
    }

    protected override void ResetOptions()
    {
        TypeKind = CSharpTypeKind.Class;
        MemberStyle = MemberStyle.GetSet;
        AttributeStyle = AttributeStyle.NewtonsoftJson;
        CollectionKind = CollectionKind.List;
        FractionalNumberType = FractionalNumberType.Double;
        NamespaceStyle = NamespaceStyle.FileScoped;
        NamespaceName = DefaultNamespace;
        RootTypeName = "Root";
        NullableAnnotations = true;
        DetectDateGuidUri = true;
        NestTypes = false;
        StatsText = string.Empty;
    }
}
