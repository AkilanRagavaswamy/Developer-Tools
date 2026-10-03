using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DevTools.App.Services;
using DevTools.Core.Vector;

namespace DevTools.App.ViewModels.Tools;

/// <summary>One line of the SVG conversion report, dressed for the list.</summary>
public sealed record SvgNoteRow(SvgNoteKind Kind, string Headline, string Detail)
{
    /// <summary>
    /// One flag per kind, because the row is coloured by three FontIcons rather than one with a
    /// converted brush: a converter resolves theme resources against the application, which is
    /// the wrong answer as soon as the window is showing a theme the app was not launched with.
    /// </summary>
    public bool IsConverted => Kind == SvgNoteKind.Converted;

    public bool IsDropped => Kind == SvgNoteKind.Dropped;

    public bool IsLimitation => Kind == SvgNoteKind.Limitation;

    public bool HasDetail => Detail.Length > 0;

    public string AutomationName => Kind switch
    {
        SvgNoteKind.Converted => $"Converted. {Headline}. {Detail}",
        SvgNoteKind.Dropped => $"Dropped. {Headline}. {Detail}",
        _ => $"Limitation. {Headline}. {Detail}",
    };
}

/// <summary>SVG to XAML Converter (FR-V01…FR-V11).</summary>
public sealed partial class SvgToXamlViewModel : TextToolViewModelBase
{
    public SvgToXamlViewModel(ToolServices services)
        : base(services)
    {
        Flavor = XamlFlavor.WinUi;
        Shape = XamlOutputShape.Canvas;
        DecimalPlaces = 3;
        ResourceKey = string.Empty;
        StatsText = string.Empty;
        AvailableShapes = SvgConvertOptions.ShapesFor(XamlFlavor.WinUi);
    }

    public override string ToolId => "svg-to-xaml";

    protected override string SuggestedFileName => "converted.xaml";

    protected override string[] OutputExtensions => [".xaml", ".txt"];

    protected override string[] InputExtensions => [".svg", ".xml", ".txt"];

    // ---------------------------------------------------------------- options

    [ObservableProperty]
    public partial XamlFlavor Flavor { get; set; }

    [ObservableProperty]
    public partial XamlOutputShape Shape { get; set; }

    [ObservableProperty]
    public partial int DecimalPlaces { get; set; }

    [ObservableProperty]
    public partial string ResourceKey { get; set; }

    [ObservableProperty]
    public partial string StatsText { get; set; }

    /// <summary>
    /// The shapes the chosen flavour can actually express.
    /// </summary>
    /// <remarks>
    /// WinUI has no <c>DrawingImage</c> and WPF has no <c>PathIcon</c>. Offering the wrong one
    /// and emitting XAML that silently fails to load would be worse than not offering it.
    /// </remarks>
    [ObservableProperty]
    public partial IReadOnlyList<XamlOutputShape> AvailableShapes { get; set; }

    public IReadOnlyList<XamlFlavor> Flavors { get; } = [XamlFlavor.WinUi, XamlFlavor.Wpf];

    public IReadOnlyList<int> DecimalPlaceOptions { get; } = [1, 2, 3, 4, 6];

    /// <summary>The XAML the preview pane should try to render. Empty when there is nothing.</summary>
    [ObservableProperty]
    public partial string PreviewXaml { get; set; } = string.Empty;

    /// <summary>Set by the page when <c>XamlReader.Load</c> refuses the output (FR-V10).</summary>
    [ObservableProperty]
    public partial string? PreviewError { get; set; }

    public bool HasPreviewError => !string.IsNullOrEmpty(PreviewError);

    partial void OnPreviewErrorChanged(string? value) => OnPropertyChanged(nameof(HasPreviewError));

    /// <summary>Combo-box indices. See <see cref="OptionListExtensions"/> for why these exist.</summary>
    public int FlavorIndex
    {
        get => Flavors.IndexOfValue(Flavor);
        set => Flavor = Flavors.ValueAt(value, XamlFlavor.WinUi);
    }

    public int ShapeIndex
    {
        get => AvailableShapes.IndexOfValue(Shape);
        set => Shape = AvailableShapes.ValueAt(value, XamlOutputShape.Canvas);
    }

    public int DecimalIndex
    {
        get => DecimalPlaceOptions.IndexOfValue(DecimalPlaces);
        set => DecimalPlaces = DecimalPlaceOptions.ValueAt(value, 3);
    }

    /// <summary>The shape names shown in the combo box, which change with the flavour.</summary>
    public IReadOnlyList<string> ShapeNames =>
        [.. AvailableShapes.Select(static s => s switch
        {
            XamlOutputShape.Canvas => "Canvas of paths",
            XamlOutputShape.MergedPath => "Single merged Path",
            XamlOutputShape.DrawingImage => "DrawingImage resource",
            XamlOutputShape.PathIcon => "PathIcon",
            _ => s.ToString(),
        })];

    partial void OnAvailableShapesChanged(IReadOnlyList<XamlOutputShape> value)
    {
        OnPropertyChanged(nameof(ShapeNames));
        OnPropertyChanged(nameof(ShapeIndex));
    }

    partial void OnFlavorChanged(XamlFlavor value)
    {
        AvailableShapes = SvgConvertOptions.ShapesFor(value);

        // Switching flavour can invalidate the chosen shape; fall back rather than fail.
        if (!AvailableShapes.Contains(Shape))
        {
            Shape = AvailableShapes[0];
        }

        OnPropertyChanged(nameof(FlavorIndex));
        OnOptionChanged();
    }

    partial void OnShapeChanged(XamlOutputShape value)
    {
        OnPropertyChanged(nameof(ShapeIndex));
        OnOptionChanged();
    }

    partial void OnDecimalPlacesChanged(int value)
    {
        OnPropertyChanged(nameof(DecimalIndex));
        OnOptionChanged();
    }

    partial void OnResourceKeyChanged(string value) => OnOptionChanged();

    // ---------------------------------------------------------------- report

    /// <summary>
    /// The conversion report (FR-V11): one row per feature, what happened and why.
    /// </summary>
    /// <remarks>
    /// The converter's promise is that nothing disappears quietly. That promise is only kept if
    /// the report is on screen next to the two previews, not buried in a banner that the next
    /// keystroke replaces — which is why this is a list the user can read at leisure.
    /// </remarks>
    public ObservableCollection<SvgNoteRow> Notes { get; } = [];

    /// <summary>The green pill: what came across.</summary>
    [ObservableProperty]
    public partial string ConvertedSummary { get; set; } = string.Empty;

    /// <summary>The amber pill: how many features did not survive intact.</summary>
    [ObservableProperty]
    public partial int IssueCount { get; set; }

    public bool HasIssues => IssueCount > 0;

    /// <summary>The amber pill's wording. "3" alone does not say three of what.</summary>
    public string IssueSummary => IssueCount == 1 ? "1 not represented" : $"{IssueCount:N0} not represented";

    partial void OnIssueCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(IssueSummary));
    }

    /// <summary>True once the output has been through <c>XamlReader.Load</c> without complaint.</summary>
    [ObservableProperty]
    public partial bool OutputLoads { get; set; }

    /// <summary>The SVG the source preview should render. Empty when there is nothing to show.</summary>
    [ObservableProperty]
    public partial string PreviewSvg { get; set; } = string.Empty;

    /// <summary>Set by the page when the platform's own SVG renderer refuses the input.</summary>
    [ObservableProperty]
    public partial string? SourceError { get; set; }

    public bool HasSourceError => !string.IsNullOrEmpty(SourceError);

    partial void OnSourceErrorChanged(string? value) => OnPropertyChanged(nameof(HasSourceError));

    private void PublishNotes(IReadOnlyList<SvgNote> notes)
    {
        Notes.Clear();

        foreach (var note in notes)
        {
            Notes.Add(new SvgNoteRow(note.Kind, note.Headline, note.Detail));
        }

        ConvertedSummary = notes.FirstOrDefault(n => n.Kind == SvgNoteKind.Converted)?.Headline ?? string.Empty;
        IssueCount = notes.Count(n => n.Kind != SvgNoteKind.Converted);
    }

    private void ClearNotes()
    {
        Notes.Clear();
        ConvertedSummary = string.Empty;
        IssueCount = 0;
        OutputLoads = false;
    }

    // ---------------------------------------------------------------- run

    protected override async Task RunCoreAsync(CancellationToken token)
    {
        if (HandleEmptyInput())
        {
            StatsText = string.Empty;
            PreviewXaml = string.Empty;
            PreviewSvg = string.Empty;
            PreviewError = null;
            SourceError = null;
            ClearNotes();
            return;
        }

        var input = Input;

        var options = new SvgConvertOptions
        {
            Flavor = Flavor,
            Shape = Shape,
            DecimalPlaces = DecimalPlaces,
            ResourceKey = string.IsNullOrWhiteSpace(ResourceKey) ? null : ResourceKey.Trim(),
        };

        var result = await ComputeAsync(() => SvgToXaml.Convert(input, options), token);

        if (!result.IsSuccess)
        {
            SetError(result.Error!);
            PreviewXaml = string.Empty;
            ClearNotes();
            return;
        }

        var value = result.Value!;
        Output = value.Xaml;

        StatsText = value.ElementCount == 1
            ? $"1 shape · {Format(value.Width)} × {Format(value.Height)}"
            : $"{value.ElementCount:N0} shapes · {Format(value.Width)} × {Format(value.Height)}";

        // The preview always renders the WinUI flavour, because that is the dialect the host
        // app itself speaks — a WPF-flavoured string would not load here even when correct.
        if (Flavor == XamlFlavor.WinUi)
        {
            PreviewXaml = value.Xaml;
        }
        else
        {
            var winui = await ComputeAsync(
                () => SvgToXaml.Convert(input, options with { Flavor = XamlFlavor.WinUi, Shape = XamlOutputShape.Canvas }),
                token);

            PreviewXaml = winui.IsSuccess ? winui.Value!.Xaml : string.Empty;
        }

        // The source preview renders the input as the platform sees it, so the two pictures
        // side by side are the real answer to "did the conversion change anything".
        PreviewSvg = input;
        PreviewError = null;
        SourceError = null;
        PublishNotes(value.Notes);
    }

    private static string Format(double value) =>
        value <= 0 ? "auto" : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- state

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("flavor", Flavor);
        state.Set("shape", Shape);
        state.Set("decimals", DecimalPlaces);
        state.Set("resourceKey", ResourceKey);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        Flavor = state.GetEnum("flavor", XamlFlavor.WinUi);
        AvailableShapes = SvgConvertOptions.ShapesFor(Flavor);

        var shape = state.GetEnum("shape", XamlOutputShape.Canvas);
        Shape = AvailableShapes.Contains(shape) ? shape : AvailableShapes[0];

        DecimalPlaces = Math.Clamp(state.GetInt("decimals", 3), 0, 8);
        ResourceKey = state.GetString("resourceKey");
    }

    protected override void ResetOptions()
    {
        Flavor = XamlFlavor.WinUi;
        AvailableShapes = SvgConvertOptions.ShapesFor(XamlFlavor.WinUi);
        Shape = XamlOutputShape.Canvas;
        DecimalPlaces = 3;
        ResourceKey = string.Empty;
        StatsText = string.Empty;
        PreviewXaml = string.Empty;
        PreviewSvg = string.Empty;
        PreviewError = null;
        SourceError = null;
        ClearNotes();
    }
}
