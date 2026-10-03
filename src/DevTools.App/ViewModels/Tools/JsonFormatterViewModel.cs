using CommunityToolkit.Mvvm.ComponentModel;
using DevTools.App.Services;
using DevTools.Core.Json;
using DevTools.Core.Text;

namespace DevTools.App.ViewModels.Tools;

/// <summary>JSON Formatter (FR-J01…FR-J10).</summary>
public sealed partial class JsonFormatterViewModel : TextToolViewModelBase
{
    public JsonFormatterViewModel(ToolServices services)
        : base(services)
    {
        Mode = JsonFormatMode.Pretty;
        IndentStyle = IndentStyle.TwoSpaces;
        JsonPath = string.Empty;
        StatsText = string.Empty;
    }

    public override string ToolId => "json-formatter";

    protected override string SuggestedFileName => "formatted.json";

    protected override string[] OutputExtensions => [".json", ".txt"];

    protected override string[] InputExtensions => [".json", ".txt"];

    // ---------------------------------------------------------------- options

    [ObservableProperty]
    public partial JsonFormatMode Mode { get; set; }

    [ObservableProperty]
    public partial IndentStyle IndentStyle { get; set; }

    [ObservableProperty]
    public partial bool SortKeys { get; set; }

    [ObservableProperty]
    public partial bool AllowTrailingCommas { get; set; }

    [ObservableProperty]
    public partial bool AllowComments { get; set; }

    [ObservableProperty]
    public partial bool EscapeNonAscii { get; set; }

    /// <summary>A JSONPath expression; empty means show the whole document.</summary>
    [ObservableProperty]
    public partial string JsonPath { get; set; }

    /// <summary>The status strip under the output: object, array, key and depth counts.</summary>
    [ObservableProperty]
    public partial string StatsText { get; set; }

    public IReadOnlyList<JsonFormatMode> Modes { get; } =
        [JsonFormatMode.Pretty, JsonFormatMode.Minify, JsonFormatMode.ValidateOnly];

    public IReadOnlyList<IndentStyle> IndentStyles { get; } =
        [IndentStyle.TwoSpaces, IndentStyle.FourSpaces, IndentStyle.Tab];

    /// <summary>Indent only matters when the output is being laid out.</summary>
    public bool IsIndentEnabled => Mode == JsonFormatMode.Pretty;

    partial void OnModeChanged(JsonFormatMode value)
    {
        OnPropertyChanged(nameof(IsIndentEnabled));
        OnPropertyChanged(nameof(ModeIndex));
        OnOptionChanged();
    }

    partial void OnIndentStyleChanged(IndentStyle value)
    {
        OnPropertyChanged(nameof(IndentIndex));
        OnOptionChanged();
    }

    partial void OnSortKeysChanged(bool value) => OnOptionChanged();

    partial void OnAllowTrailingCommasChanged(bool value)
    {
        RaiseMoreOptionCount();
        OnOptionChanged();
    }

    partial void OnAllowCommentsChanged(bool value)
    {
        RaiseMoreOptionCount();
        OnOptionChanged();
    }

    partial void OnEscapeNonAsciiChanged(bool value)
    {
        RaiseMoreOptionCount();
        OnOptionChanged();
    }

    partial void OnJsonPathChanged(string value) => OnOptionChanged();

    // ---------------------------------------------------------------- options bar

    /// <summary>
    /// How many of the three options behind "More" are on. The badge exists so collapsing the
    /// rare settings does not hide the fact that one of them is changing the output.
    /// </summary>
    public int MoreOptionCount =>
        (AllowTrailingCommas ? 1 : 0) + (AllowComments ? 1 : 0) + (EscapeNonAscii ? 1 : 0);

    public bool HasMoreOptions => MoreOptionCount > 0;

    private void RaiseMoreOptionCount()
    {
        OnPropertyChanged(nameof(MoreOptionCount));
        OnPropertyChanged(nameof(HasMoreOptions));
    }


    // ---------------------------------------------------------------- result view

    /// <summary>
    /// Whether the result is shown as text or as a tree you can fold.
    /// </summary>
    /// <remarks>
    /// Text stays the default: it is what you copy, and it is what the formatting options are
    /// about. The tree is for the other question — what shape is this payload — which a
    /// formatted document can only answer by scrolling.
    /// </remarks>
    [ObservableProperty]
    public partial bool ShowTree { get; set; }

    public bool ShowText => !ShowTree;

    public int ResultViewIndex
    {
        get => ShowTree ? 1 : 0;
        set => ShowTree = value == 1;
    }

    /// <summary>The parsed document behind the tree, or null when there is nothing to show.</summary>
    [ObservableProperty]
    public partial JsonOutlineNode? Outline { get; set; }

    partial void OnShowTreeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowText));
        OnPropertyChanged(nameof(ResultViewIndex));

        // Building the outline is a second parse, so it waits until the tree is actually asked
        // for rather than happening on every keystroke in a mode nobody is looking at.
        if (value)
        {
            RebuildOutline();
        }
    }

    private void RebuildOutline()
    {
        if (!ShowTree || TextUtil.IsBlank(Input))
        {
            Outline = null;
            return;
        }

        var built = JsonOutline.Build(Input, CurrentOptions());
        Outline = built.IsSuccess ? built.Value : null;
    }

    private JsonFormatOptions CurrentOptions() => new()
    {
        Mode = Mode,
        IndentStyle = IndentStyle,
        SortKeys = SortKeys,
        AllowTrailingCommas = AllowTrailingCommas,
        AllowComments = AllowComments,
        EscapeNonAscii = EscapeNonAscii,
    };

    // ---------------------------------------------------------------- run

    protected override async Task RunCoreAsync(CancellationToken token)
    {
        if (HandleEmptyInput())
        {
            StatsText = string.Empty;
            Outline = null;
            return;
        }

        var input = Input;

        var options = CurrentOptions();

        // The tree is rebuilt alongside the text so the two always describe the same document.
        RebuildOutline();

        var path = JsonPath;

        // A JSONPath narrows the document first; the formatting options then apply to whatever
        // it selected, which is what makes "query, then read the result" a single step.
        if (!TextUtil.IsBlank(path))
        {
            var query = await ComputeAsync(() => JsonFormatter.Query(input, path, options), token);

            if (!query.IsSuccess)
            {
                SetError(query.Error!);
                return;
            }

            Output = query.Value!.Output;
            StatsText = query.Value.MatchCount == 1
                ? "1 match"
                : $"{query.Value.MatchCount:N0} matches";

            ClearMessage();
            return;
        }

        var result = await ComputeAsync(() => JsonFormatter.Format(input, options), token);

        if (!result.IsSuccess)
        {
            SetError(result.Error!);
            return;
        }

        var value = result.Value!;
        Output = value.Output;

        var stats = value.Stats;
        StatsText =
            $"{stats.ObjectCount:N0} objects · {stats.ArrayCount:N0} arrays · " +
            $"{stats.KeyCount:N0} keys · depth {stats.MaxDepth:N0} · {Core.Limits.Describe(stats.ByteSize)}";

        ReportWarnings(value.Warnings);
    }

    // ---------------------------------------------------------------- state

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("mode", Mode);
        state.Set("indent", IndentStyle);
        state.Set("sort", SortKeys);
        state.Set("commas", AllowTrailingCommas);
        state.Set("comments", AllowComments);
        state.Set("escape", EscapeNonAscii);
        state.Set("path", JsonPath);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        Mode = state.GetEnum("mode", JsonFormatMode.Pretty);
        IndentStyle = state.GetEnum("indent", IndentStyle.TwoSpaces);
        SortKeys = state.GetBool("sort");
        AllowTrailingCommas = state.GetBool("commas");
        AllowComments = state.GetBool("comments");
        EscapeNonAscii = state.GetBool("escape");
        JsonPath = state.GetString("path");
    }

    protected override void ResetOptions()
    {
        Mode = JsonFormatMode.Pretty;
        IndentStyle = IndentStyle.TwoSpaces;
        SortKeys = false;
        AllowTrailingCommas = false;
        AllowComments = false;
        EscapeNonAscii = false;
        JsonPath = string.Empty;
        StatsText = string.Empty;
    }

    // ---------------------------------------------------------------- combo indices

    public int ModeIndex
    {
        get => Modes.IndexOfValue(Mode);
        set => Mode = Modes.ValueAt(value, JsonFormatMode.Pretty);
    }

    public int IndentIndex
    {
        get => IndentStyles.IndexOfValue(IndentStyle);
        set => IndentStyle = IndentStyles.ValueAt(value, IndentStyle.TwoSpaces);
    }
}
