using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DevTools.App.Services;
using DevTools.Core.Text;

namespace DevTools.App.ViewModels.Tools;

/// <summary>One match, ready for the list.</summary>
public sealed record RegexMatchRow(string Header, string Value, string Groups)
{
    public bool HasGroups => Groups.Length > 0;
}

/// <summary>Regex Validator.</summary>
public sealed partial class RegexValidatorViewModel : TextToolViewModelBase
{
    public RegexValidatorViewModel(ToolServices services)
        : base(services)
    {
        Pattern = string.Empty;
        Replacement = string.Empty;
        Summary = string.Empty;
        Multiline = true;
    }

    public override string ToolId => "regex-validator";

    protected override string SuggestedFileName => "replaced.txt";

    [ObservableProperty]
    public partial string Pattern { get; set; }

    /// <summary>Optional. When set, the replaced text is shown under the matches.</summary>
    [ObservableProperty]
    public partial string Replacement { get; set; }

    [ObservableProperty]
    public partial bool IgnoreCase { get; set; }

    [ObservableProperty]
    public partial bool Multiline { get; set; }

    [ObservableProperty]
    public partial bool Singleline { get; set; }

    [ObservableProperty]
    public partial bool IgnorePatternWhitespace { get; set; }

    [ObservableProperty]
    public partial bool ExplicitCapture { get; set; }

    [ObservableProperty]
    public partial bool EcmaScript { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    /// <summary>True when the pattern compiles and matched at least once.</summary>
    [ObservableProperty]
    public partial bool IsMatch { get; set; }

    public ObservableCollection<RegexMatchRow> Matches { get; } = [];

    public bool HasReplacement => !string.IsNullOrEmpty(Replacement);

    public int MoreOptionCount => (IgnorePatternWhitespace ? 1 : 0) + (ExplicitCapture ? 1 : 0) + (EcmaScript ? 1 : 0);

    public bool HasMoreOptions => MoreOptionCount > 0;

    partial void OnPatternChanged(string value) => OnOptionChanged();

    partial void OnReplacementChanged(string value)
    {
        OnPropertyChanged(nameof(HasReplacement));
        OnOptionChanged();
    }

    partial void OnIgnoreCaseChanged(bool value) => OnOptionChanged();

    partial void OnMultilineChanged(bool value) => OnOptionChanged();

    partial void OnSinglelineChanged(bool value) => OnOptionChanged();

    partial void OnIgnorePatternWhitespaceChanged(bool value) => MoreChanged();

    partial void OnExplicitCaptureChanged(bool value) => MoreChanged();

    partial void OnEcmaScriptChanged(bool value) => MoreChanged();

    private void MoreChanged()
    {
        OnPropertyChanged(nameof(MoreOptionCount));
        OnPropertyChanged(nameof(HasMoreOptions));
        OnOptionChanged();
    }

    protected override async Task RunCoreAsync(CancellationToken token)
    {
        Matches.Clear();
        IsMatch = false;

        if (string.IsNullOrEmpty(Pattern))
        {
            Output = string.Empty;
            Summary = "Type a pattern above to test it against the text.";
            ClearMessage();
            return;
        }

        var pattern = Pattern;
        var input = Input ?? string.Empty;
        var options = new RegexTestOptions
        {
            IgnoreCase = IgnoreCase,
            Multiline = Multiline,
            Singleline = Singleline,
            IgnorePatternWhitespace = IgnorePatternWhitespace,
            ExplicitCapture = ExplicitCapture,
            EcmaScript = EcmaScript,
            Replacement = HasReplacement ? Replacement : null,
        };

        var result = await ComputeAsync(() => RegexTester.Test(pattern, input, options), token);

        if (!result.IsSuccess)
        {
            Summary = "No result";
            Output = string.Empty;
            SetError(result.Error!);
            return;
        }

        var value = result.Value!;
        foreach (var match in value.Matches)
        {
            var groups = string.Join('\n', match.Groups.Select(static g =>
                g.Success ? $"{g.Name}: {Show(g.Value)}" : $"{g.Name}: (no match)"));

            Matches.Add(new RegexMatchRow(
                $"Match {match.Number:N0} · Ln {match.Line:N0}, Col {match.Column:N0} · {match.Length:N0} chars",
                Show(match.Value),
                groups));
        }

        IsMatch = value.TotalMatches > 0;
        Output = value.Replaced ?? string.Empty;
        Summary = value.TotalMatches switch
        {
            0 => "No match",
            1 => "1 match",
            _ => $"{value.TotalMatches:N0} matches",
        } + (value.Truncated ? $" (first {RegexTester.MaxListedMatches:N0} listed)" : string.Empty)
          + $" · {value.Elapsed.TotalMilliseconds:0.#} ms";

        ClearMessage();
    }

    /// <summary>An empty match, or one of only whitespace, is still visible in the list.</summary>
    private static string Show(string value) => value.Length == 0
        ? "(empty)"
        : value.Replace("\r\n", "↵", StringComparison.Ordinal).Replace('\n', '↵').Replace('\r', '↵').Replace('\t', '⇥');

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.SetData("pattern", Pattern);
        state.SetData("replacement", Replacement);
        state.Set("i", IgnoreCase);
        state.Set("m", Multiline);
        state.Set("s", Singleline);
        state.Set("x", IgnorePatternWhitespace);
        state.Set("n", ExplicitCapture);
        state.Set("ecma", EcmaScript);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        Pattern = state.GetString("pattern");
        Replacement = state.GetString("replacement");
        IgnoreCase = state.GetBool("i");
        Multiline = state.GetBool("m", true);
        Singleline = state.GetBool("s");
        IgnorePatternWhitespace = state.GetBool("x");
        ExplicitCapture = state.GetBool("n");
        EcmaScript = state.GetBool("ecma");
    }

    public override void Clear()
    {
        base.Clear();
        Matches.Clear();
        Summary = string.Empty;
    }

    protected override void ResetOptions()
    {
        Pattern = string.Empty;
        Replacement = string.Empty;
        IgnoreCase = false;
        Multiline = true;
        Singleline = false;
        IgnorePatternWhitespace = false;
        ExplicitCapture = false;
        EcmaScript = false;
    }
}
