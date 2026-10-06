using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using DevTools.Core.Json;
using DevTools.Core.Text;

namespace DevTools.App.ViewModels.Tools;

/// <summary>One row of the diff tree, flattened for display with its indent depth.</summary>
public sealed record DiffRow(
    string Path,
    string Name,
    string Marker,
    JsonDiffKind Kind,
    string? Left,
    string? Right,
    int Depth)
{
    public double Indent => Depth * 16.0;

    /// <summary>Read out by a screen reader instead of the marker glyph.</summary>
    public string AutomationName => Kind switch
    {
        JsonDiffKind.Added => $"Added: {Path} is {Right}",
        JsonDiffKind.Removed => $"Removed: {Path} was {Left}",
        JsonDiffKind.Changed => $"Changed: {Path} was {Left}, now {Right}",
        _ => $"Unchanged: {Path}",
    };
}

/// <summary>What the result pane is showing.</summary>
public enum DiffView
{
    /// <summary>The two documents against each other, row for row — what a diff mostly is.</summary>
    SideBySide,

    Tree,
    JsonPatch,
    UnifiedText,
}

/// <summary>JSON Diff Checker (FR-J20…FR-J27).</summary>
public sealed partial class JsonDiffViewModel : DualTextToolViewModelBase
{
    public JsonDiffViewModel(ToolServices services)
        : base(services)
    {
        Views = services.Settings.JsonDiffTextViews
            ? [DiffView.SideBySide, DiffView.Tree, DiffView.JsonPatch, DiffView.UnifiedText]
            : [DiffView.SideBySide, DiffView.Tree];

        ArrayStrategy = ArrayStrategy.BestMatch;
        KeyField = "id";
        IgnorePathsText = string.Empty;
        NumericToleranceText = string.Empty;
        View = DiffView.SideBySide;
        SummaryText = string.Empty;
    }

    public override string ToolId => "json-diff";

    public override string LeftLabel => "Original JSON";

    public override string RightLabel => "Changed JSON";

    protected override string SuggestedFileName => "diff";

    protected override string[] OutputExtensions => [".json", ".txt"];

    protected override string[] InputExtensions => [".json", ".txt"];

    // ---------------------------------------------------------------- options

    [ObservableProperty]
    public partial ArrayStrategy ArrayStrategy { get; set; }

    [ObservableProperty]
    public partial string KeyField { get; set; }

    [ObservableProperty]
    public partial bool IgnoreArrayOrder { get; set; }

    [ObservableProperty]
    public partial bool IgnoreCaseInValues { get; set; }

    [ObservableProperty]
    public partial bool IgnoreCaseInKeys { get; set; }

    [ObservableProperty]
    public partial bool NullEqualsMissing { get; set; }

    /// <summary>One glob per line, e.g. <c>$..timestamp</c>.</summary>
    [ObservableProperty]
    public partial string IgnorePathsText { get; set; }

    [ObservableProperty]
    public partial string NumericToleranceText { get; set; }

    [ObservableProperty]
    public partial bool ShowUnchanged { get; set; }

    [ObservableProperty]
    public partial DiffView View { get; set; }

    // ---------------------------------------------------------------- results

    public ObservableCollection<DiffRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string SummaryText { get; set; }

    [ObservableProperty]
    public partial bool AreEqual { get; set; }

    [ObservableProperty]
    public partial int AddedCount { get; set; }

    [ObservableProperty]
    public partial int RemovedCount { get; set; }

    [ObservableProperty]
    public partial int ChangedCount { get; set; }

    /// <summary>
    /// The two documents laid out against each other, for the side-by-side view (FR-J28).
    /// </summary>
    /// <remarks>
    /// Built from the same comparison as the tree, not a second one: the array pairing is where
    /// a diff makes its real decisions, and two views disagreeing about which element is which
    /// would be worse than either of them being wrong on its own.
    /// </remarks>
    [ObservableProperty]
    public partial JsonDiffLayout? Layout { get; set; }

    public IReadOnlyList<ArrayStrategy> ArrayStrategies { get; } =
        [ArrayStrategy.BestMatch, ArrayStrategy.Index, ArrayStrategy.Key];

    /// <summary>
    /// The result views on offer. JSON Patch and Unified text are only listed when Settings
    /// asks for them; read once, because a tool view model lives no longer than its page.
    /// </summary>
    public IReadOnlyList<DiffView> Views { get; }

    /// <summary>What the result-view combo box shows, in the same order as <see cref="Views"/>.</summary>
    public IReadOnlyList<string> ViewLabels => [.. Views.Select(static view => view switch
    {
        DiffView.SideBySide => "Side by side",
        DiffView.Tree => "Tree",
        DiffView.JsonPatch => "JSON Patch",
        _ => "Unified text",
    })];

    /// <summary>Falls back to side by side when a remembered view is no longer offered.</summary>
    private DiffView Normalize(DiffView view) => Views.Contains(view) ? view : DiffView.SideBySide;

    /// <summary>The key field only means anything under the Key strategy.</summary>
    public bool IsKeyFieldEnabled => ArrayStrategy == ArrayStrategy.Key;

    public bool IsSideBySideVisible => View == DiffView.SideBySide;

    public bool IsTreeVisible => View == DiffView.Tree;

    public bool IsTextVisible => View is DiffView.JsonPatch or DiffView.UnifiedText;

    /// <summary>Combo-box indices. See <see cref="OptionListExtensions"/> for why these exist.</summary>
    public int StrategyIndex
    {
        get => ArrayStrategies.IndexOfValue(ArrayStrategy);
        set => ArrayStrategy = ArrayStrategies.ValueAt(value, ArrayStrategy.BestMatch);
    }

    public int ViewIndex
    {
        get => Views.IndexOfValue(View);
        set => View = Views.ValueAt(value, DiffView.SideBySide);
    }

    partial void OnArrayStrategyChanged(ArrayStrategy value)
    {
        OnPropertyChanged(nameof(IsKeyFieldEnabled));
        OnPropertyChanged(nameof(StrategyIndex));
        RaiseComparisonCount();
        OnOptionChanged();
    }

    partial void OnKeyFieldChanged(string value)
    {
        RaiseComparisonCount();
        OnOptionChanged();
    }

    partial void OnIgnoreArrayOrderChanged(bool value)
    {
        RaiseComparisonCount();
        OnOptionChanged();
    }

    partial void OnIgnoreCaseInValuesChanged(bool value)
    {
        RaiseComparisonCount();
        OnOptionChanged();
    }

    partial void OnIgnoreCaseInKeysChanged(bool value)
    {
        RaiseComparisonCount();
        OnOptionChanged();
    }

    partial void OnNullEqualsMissingChanged(bool value)
    {
        RaiseComparisonCount();
        OnOptionChanged();
    }

    partial void OnIgnorePathsTextChanged(string value)
    {
        RaiseIgnoredPathCount();
        OnOptionChanged();
    }

    partial void OnNumericToleranceTextChanged(string value)
    {
        RaiseComparisonCount();
        OnOptionChanged();
    }

    partial void OnShowUnchangedChanged(bool value) => OnOptionChanged();

    // ---------------------------------------------------------------- options bar

    /// <summary>
    /// How many comparison settings are away from their defaults. The badge is what makes it
    /// safe to fold six of them behind one button: a diff that looks wrong is usually a
    /// comparison setting left on, and this says so without opening anything.
    /// </summary>
    public int ComparisonCount =>
        (IgnoreArrayOrder ? 1 : 0) +
        (IgnoreCaseInValues ? 1 : 0) +
        (IgnoreCaseInKeys ? 1 : 0) +
        (NullEqualsMissing ? 1 : 0) +
        (TextUtil.IsBlank(NumericToleranceText) ? 0 : 1) +
        (ArrayStrategy == ArrayStrategy.Key && !TextUtil.IsBlank(KeyField) ? 1 : 0);

    public bool HasComparisonOptions => ComparisonCount > 0;

    /// <summary>The number of paths currently being ignored, blank lines not counted.</summary>
    public int IgnoredPathCount =>
        TextUtil.SplitLines(IgnorePathsText, keepTrailingEmpty: false)
            .Count(line => !TextUtil.IsBlank(line));

    public bool HasIgnoredPaths => IgnoredPathCount > 0;

    private void RaiseComparisonCount()
    {
        OnPropertyChanged(nameof(ComparisonCount));
        OnPropertyChanged(nameof(HasComparisonOptions));
    }

    private void RaiseIgnoredPathCount()
    {
        OnPropertyChanged(nameof(IgnoredPathCount));
        OnPropertyChanged(nameof(HasIgnoredPaths));
    }

    partial void OnViewChanged(DiffView value)
    {
        OnPropertyChanged(nameof(IsSideBySideVisible));
        OnPropertyChanged(nameof(IsTreeVisible));
        OnPropertyChanged(nameof(IsTextVisible));
        OnPropertyChanged(nameof(ViewIndex));
        OnOptionChanged();
    }

    // ---------------------------------------------------------------- run

    protected override void OnEmptyInput()
    {
        Rows.Clear();
        Layout = null;
        SummaryText = string.Empty;
        AddedCount = RemovedCount = ChangedCount = 0;
        AreEqual = false;
    }

    protected override async Task RunCoreAsync(CancellationToken token)
    {
        if (HandleEmptyInput())
        {
            return;
        }

        var left = Left;
        var right = Right;
        var options = BuildOptions();
        var view = View;

        var result = await ComputeAsync(() => JsonDiffer.Compare(left, right, options), token);

        if (!result.IsSuccess)
        {
            SetError(result.Error!);
            return;
        }

        var diff = result.Value!;

        AreEqual = diff.AreEqual;
        AddedCount = diff.Added;
        RemovedCount = diff.Removed;
        ChangedCount = diff.Changed;
        SummaryText = diff.Summary;

        Output = view switch
        {
            DiffView.JsonPatch => diff.JsonPatch,
            DiffView.UnifiedText => await BuildUnifiedAsync(left, right, token),
            _ => diff.JsonPatch,
        };

        Layout = await ComputeAsync(() => JsonDiffLayoutBuilder.Build(diff), token);

        var showUnchanged = ShowUnchanged;
        var rows = await ComputeAsync(() => Flatten(diff.Root, showUnchanged), token);

        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        if (diff.Warnings.Count > 0)
        {
            ReportWarnings(diff.Warnings);
        }
        else if (diff.AreEqual)
        {
            SetSuccess("The two documents are semantically equal.");
        }
        else
        {
            ClearMessage();
        }
    }

    private async Task<string> BuildUnifiedAsync(string left, string right, CancellationToken token)
    {
        var result = await ComputeAsync(() => JsonDiffer.CompareAsText(left, right), token);
        return result.IsSuccess ? result.Value!.UnifiedDiff : string.Empty;
    }

    private JsonDiffOptions BuildOptions()
    {
        decimal? tolerance = null;

        if (!string.IsNullOrWhiteSpace(NumericToleranceText) &&
            decimal.TryParse(
                NumericToleranceText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) &&
            parsed > 0)
        {
            tolerance = parsed;
        }

        var ignorePaths = IgnorePathsText
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new JsonDiffOptions
        {
            ArrayStrategy = ArrayStrategy,
            KeyField = string.IsNullOrWhiteSpace(KeyField) ? "id" : KeyField.Trim(),
            IgnoreArrayOrder = IgnoreArrayOrder,
            IgnoreCaseInValues = IgnoreCaseInValues,
            IgnoreCaseInKeys = IgnoreCaseInKeys,
            NullEqualsMissing = NullEqualsMissing,
            NumericTolerance = tolerance,
            IgnorePaths = ignorePaths,
        };
    }

    /// <summary>
    /// Flattens the tree for a virtualised list. Unchanged branches are pruned unless the user
    /// asked for them, because a diff of a large document is mostly unchanged and scrolling
    /// through it to find the three rows that matter is not reading a diff.
    /// </summary>
    private static List<DiffRow> Flatten(JsonDiffNode root, bool showUnchanged)
    {
        var rows = new List<DiffRow>();
        Walk(root, 0);
        return rows;

        void Walk(JsonDiffNode node, int depth)
        {
            var include = showUnchanged || node.Kind != JsonDiffKind.Unchanged;

            if (include && depth > 0)
            {
                rows.Add(new DiffRow(node.Path, node.Name, node.Marker, node.Kind, node.Left, node.Right, depth - 1));
            }

            if (!include)
            {
                return;
            }

            foreach (var child in node.Children)
            {
                Walk(child, depth + 1);
            }
        }
    }

    [RelayCommand]
    private void CopyPatch()
    {
        if (string.IsNullOrEmpty(Output))
        {
            return;
        }

        Services.Clipboard.SetText(Output);
        SetSuccess("Copied to clipboard.");
    }

    /// <summary>Saves the side-by-side view as a self-contained HTML page.</summary>
    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        if (Layout is not { Rows.Count: > 0 } layout)
        {
            SetInfo("Compare two documents first.");
            return;
        }

        var html = Core.Text.DiffHtmlExporter.FromJson(layout, LeftLabel, RightLabel);
        var path = await Services.Files.SaveTextFileAsync("json-diff.html", html, ".html");
        if (path is not null)
        {
            SetSuccess($"Saved to {path}.");
        }
    }

    // ---------------------------------------------------------------- state

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("strategy", ArrayStrategy);
        state.Set("keyField", KeyField);
        state.Set("ignoreOrder", IgnoreArrayOrder);
        state.Set("ignoreValueCase", IgnoreCaseInValues);
        state.Set("ignoreKeyCase", IgnoreCaseInKeys);
        state.Set("nullMissing", NullEqualsMissing);
        state.Set("ignorePaths", IgnorePathsText);
        state.Set("tolerance", NumericToleranceText);
        state.Set("showUnchanged", ShowUnchanged);
        state.Set("view", View);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        ArrayStrategy = state.GetEnum("strategy", ArrayStrategy.BestMatch);
        KeyField = state.GetString("keyField", "id");
        IgnoreArrayOrder = state.GetBool("ignoreOrder");
        IgnoreCaseInValues = state.GetBool("ignoreValueCase");
        IgnoreCaseInKeys = state.GetBool("ignoreKeyCase");
        NullEqualsMissing = state.GetBool("nullMissing");
        IgnorePathsText = state.GetString("ignorePaths");
        NumericToleranceText = state.GetString("tolerance");
        ShowUnchanged = state.GetBool("showUnchanged");
        View = Normalize(state.GetEnum("view", DiffView.SideBySide));
    }

    protected override void ResetOptions()
    {
        ArrayStrategy = ArrayStrategy.BestMatch;
        KeyField = "id";
        IgnoreArrayOrder = false;
        IgnoreCaseInValues = false;
        IgnoreCaseInKeys = false;
        NullEqualsMissing = false;
        IgnorePathsText = string.Empty;
        NumericToleranceText = string.Empty;
        ShowUnchanged = false;
        View = DiffView.SideBySide;
        OnEmptyInput();
    }
}
