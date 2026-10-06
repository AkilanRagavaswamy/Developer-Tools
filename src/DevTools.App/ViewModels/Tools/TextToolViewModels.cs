using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using DevTools.Core.Text;

namespace DevTools.App.ViewModels.Tools;

/// <summary>Text Compare.</summary>
public sealed partial class TextCompareViewModel : DualTextToolViewModelBase
{
    public TextCompareViewModel(ToolServices services)
        : base(services)
    {
        SummaryText = string.Empty;
    }

    public override string ToolId => "text-compare";

    protected override string SuggestedFileName => "changes.diff";

    protected override string[] OutputExtensions => [".diff", ".patch", ".txt"];

    [ObservableProperty]
    public partial bool IgnoreCase { get; set; }

    /// <summary>0 = compare whitespace, 1 = ignore leading and trailing, 2 = ignore all.</summary>
    [ObservableProperty]
    public partial int WhitespaceIndex { get; set; }

    [ObservableProperty]
    public partial bool IgnoreBlankLines { get; set; }

    /// <summary>One column with + and − markers, rather than two side by side.</summary>
    [ObservableProperty]
    public partial bool IsInline { get; set; }

    public int ViewIndex
    {
        get => IsInline ? 1 : 0;
        set => IsInline = value == 1;
    }

    [ObservableProperty]
    public partial TextDiffResult? Result { get; set; }

    [ObservableProperty]
    public partial string SummaryText { get; set; }

    public int ComparisonCount => (IgnoreCase ? 1 : 0) + (WhitespaceIndex > 0 ? 1 : 0) + (IgnoreBlankLines ? 1 : 0);

    public bool HasComparisonOptions => ComparisonCount > 0;

    partial void OnIgnoreCaseChanged(bool value) => ComparisonChanged();

    partial void OnWhitespaceIndexChanged(int value) => ComparisonChanged();

    partial void OnIgnoreBlankLinesChanged(bool value) => ComparisonChanged();

    partial void OnIsInlineChanged(bool value)
    {
        OnPropertyChanged(nameof(ViewIndex));
        PersistState();
    }

    private void ComparisonChanged()
    {
        OnPropertyChanged(nameof(ComparisonCount));
        OnPropertyChanged(nameof(HasComparisonOptions));
        OnOptionChanged();
    }

    protected override void OnEmptyInput()
    {
        Result = null;
        SummaryText = string.Empty;
    }

    protected override async Task RunCoreAsync(CancellationToken token)
    {
        if (HandleEmptyInput())
        {
            return;
        }

        var left = Left;
        var right = Right;
        var options = new TextDiffOptions
        {
            IgnoreCase = IgnoreCase,
            IgnoreLeadingAndTrailingWhitespace = WhitespaceIndex == 1,
            IgnoreAllWhitespace = WhitespaceIndex == 2,
            IgnoreBlankLines = IgnoreBlankLines,
        };

        var result = await ComputeAsync(() => TextDiff.Compare(left, right, options), token);

        if (!result.IsSuccess)
        {
            SetError(result.Error!);
            return;
        }

        var diff = result.Value!;
        Result = diff;
        Output = diff.UnifiedDiff;

        var s = diff.Statistics;
        SummaryText = diff.AreIdentical
            ? "The two texts are identical."
            : diff.OnlyLineEndingsDiffer
                ? "Only the line endings differ."
                : $"{s.LinesAdded:N0} added · {s.LinesRemoved:N0} removed · {s.LinesModified:N0} changed · {s.SimilarityPercent:0.#}% similar";

        if (diff.IsApproximate)
        {
            SetWarning("These texts are large and very different, so parts of the comparison are approximate.");
        }
        else if (result.HasWarning)
        {
            SetWarning(result.Warning!);
        }
        else
        {
            ClearMessage();
        }
    }

    /// <summary>Saves the side-by-side comparison as a self-contained HTML page.</summary>
    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        if (Result is not { } diff)
        {
            SetInfo("Compare two texts first.");
            return;
        }

        var html = DiffHtmlExporter.FromText(diff, LeftLabel, RightLabel, SummaryText);
        var path = await Services.Files.SaveTextFileAsync("text-compare.html", html, ".html");
        if (path is not null)
        {
            SetSuccess($"Saved to {path}.");
        }
    }

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("ignoreCase", IgnoreCase);
        state.Set("whitespace", WhitespaceIndex);
        state.Set("blankLines", IgnoreBlankLines);
        state.Set("inline", IsInline);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        IgnoreCase = state.GetBool("ignoreCase");
        WhitespaceIndex = Math.Clamp(state.GetInt("whitespace"), 0, 2);
        IgnoreBlankLines = state.GetBool("blankLines");
        IsInline = state.GetBool("inline");
    }

    protected override void ResetOptions()
    {
        IgnoreCase = false;
        WhitespaceIndex = 0;
        IgnoreBlankLines = false;
        IsInline = false;
        Result = null;
        SummaryText = string.Empty;
    }
}

/// <summary>One figure on the counter's dashboard.</summary>
public sealed record StatTile(string Label, string Value, string? Detail = null)
{
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>A length limit the text is measured against.</summary>
public sealed record LimitRow(string Name, int Limit, int Used, string Unit)
{
    public double Ratio => Limit == 0 ? 0 : Math.Min(1, (double)Used / Limit);

    public bool IsOver => Used > Limit;

    public string Remaining => IsOver ? $"{Used - Limit:N0} {Unit} over" : $"{Limit - Used:N0} {Unit} left";

    public string Summary => $"{Used:N0} / {Limit:N0}";
}

/// <summary>A row in a frequency table, ready to show.</summary>
public sealed record FrequencyRow(string Text, int Count, string Percent);

/// <summary>Character Counter.</summary>
public sealed partial class CharacterCounterViewModel : TextToolViewModelBase
{
    public CharacterCounterViewModel(ToolServices services)
        : base(services)
    {
    }

    public override string ToolId => "character-counter";

    public ObservableCollection<StatTile> Tiles { get; } = [];

    public ObservableCollection<LimitRow> Limits { get; } = [];

    public ObservableCollection<FrequencyRow> Characters { get; } = [];

    protected override async Task RunCoreAsync(CancellationToken token)
    {
        var input = Input;
        var stats = await ComputeAsync(() => TextAnalysis.Analyze(input, topWords: 0, topCharacters: 60), token);

        Replace(Tiles,
        [
            new StatTile("Characters", $"{stats.Characters:N0}", stats.Utf16Units != stats.Characters ? $"{stats.Utf16Units:N0} UTF-16 units" : null),
            new StatTile("Without spaces", $"{stats.CharactersWithoutWhitespace:N0}"),
            new StatTile("Words", $"{stats.Words:N0}", stats.Words > 0 ? $"{stats.UniqueWords:N0} unique" : null),
            new StatTile("Sentences", $"{stats.Sentences:N0}"),
            new StatTile("Paragraphs", $"{stats.Paragraphs:N0}"),
            new StatTile("Lines", $"{stats.Lines:N0}", stats.LongestLine > 0 ? $"longest {stats.LongestLine:N0}" : null),
            new StatTile("Bytes (UTF-8)", $"{stats.Utf8Bytes:N0}", $"{stats.Utf16Units * 2:N0} as UTF-16"),
            new StatTile("Reading time", TextAnalysis.DescribeDuration(stats.ReadingTime), $"speaking {TextAnalysis.DescribeDuration(stats.SpeakingTime)}"),
        ]);

        Replace(Limits,
        [
            new LimitRow("Post on X", 280, stats.Characters, "characters"),
            new LimitRow("SMS message", 160, stats.Characters, "characters"),
            new LimitRow("Page title (SEO)", 60, stats.Characters, "characters"),
            new LimitRow("Meta description (SEO)", 160, stats.Characters, "characters"),
            new LimitRow("Git commit subject", 72, FirstLineLength(input), "characters"),
            new LimitRow("App Store / Store short description", 100, stats.Characters, "characters"),
        ]);

        Replace(Characters,
        [.. stats.CharacterFrequency.Select(static f => new FrequencyRow(TextAnalysis.Visible(f.Text), f.Count, $"{f.Percent:0.0}%"))]);

        ClearMessage();
    }

    private static int FirstLineLength(string text)
    {
        var end = text.AsSpan().IndexOfAny('\r', '\n');
        return TextUtil.GraphemeCount(end < 0 ? text : text[..end]);
    }

    internal static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}

