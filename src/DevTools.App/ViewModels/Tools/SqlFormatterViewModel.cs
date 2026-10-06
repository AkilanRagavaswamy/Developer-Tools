using CommunityToolkit.Mvvm.ComponentModel;
using DevTools.App.Services;
using DevTools.Core.Sql;
using DevTools.Core.Text;

namespace DevTools.App.ViewModels.Tools;

/// <summary>SQL Formatter.</summary>
public sealed partial class SqlFormatterViewModel : TextToolViewModelBase
{
    public SqlFormatterViewModel(ToolServices services)
        : base(services)
    {
        Dialect = SqlDialect.StandardSql;
        IndentStyle = IndentStyle.TwoSpaces;
        KeywordCase = SqlKeywordCase.Upper;
        StatsText = string.Empty;
    }

    public override string ToolId => "sql-formatter";

    protected override string SuggestedFileName => "formatted.sql";

    protected override string[] OutputExtensions => [".sql", ".txt"];

    protected override string[] InputExtensions => [".sql", ".txt"];

    [ObservableProperty]
    public partial SqlDialect Dialect { get; set; }

    [ObservableProperty]
    public partial IndentStyle IndentStyle { get; set; }

    [ObservableProperty]
    public partial SqlKeywordCase KeywordCase { get; set; }

    [ObservableProperty]
    public partial bool LeadingCommas { get; set; }

    [ObservableProperty]
    public partial string StatsText { get; set; }

    public IReadOnlyList<SqlDialect> Dialects { get; } = Enum.GetValues<SqlDialect>();

    public IReadOnlyList<string> DialectNames { get; } = [.. Enum.GetValues<SqlDialect>().Select(SqlFormatter.DisplayName)];

    public IReadOnlyList<IndentStyle> IndentStyles { get; } = [IndentStyle.TwoSpaces, IndentStyle.FourSpaces, IndentStyle.Tab];

    public IReadOnlyList<SqlKeywordCase> KeywordCases { get; } = [SqlKeywordCase.Upper, SqlKeywordCase.Lower, SqlKeywordCase.Preserve];

    public int DialectIndex
    {
        get => Dialects.IndexOfValue(Dialect);
        set => Dialect = Dialects.ValueAt(value, SqlDialect.StandardSql);
    }

    public int IndentIndex
    {
        get => IndentStyles.IndexOfValue(IndentStyle);
        set => IndentStyle = IndentStyles.ValueAt(value, IndentStyle.TwoSpaces);
    }

    public int KeywordCaseIndex
    {
        get => KeywordCases.IndexOfValue(KeywordCase);
        set => KeywordCase = KeywordCases.ValueAt(value, SqlKeywordCase.Upper);
    }

    partial void OnDialectChanged(SqlDialect value)
    {
        OnPropertyChanged(nameof(DialectIndex));
        OnOptionChanged();
    }

    partial void OnIndentStyleChanged(IndentStyle value)
    {
        OnPropertyChanged(nameof(IndentIndex));
        OnOptionChanged();
    }

    partial void OnKeywordCaseChanged(SqlKeywordCase value)
    {
        OnPropertyChanged(nameof(KeywordCaseIndex));
        OnOptionChanged();
    }

    partial void OnLeadingCommasChanged(bool value) => OnOptionChanged();

    protected override async Task RunCoreAsync(CancellationToken token)
    {
        if (HandleEmptyInput())
        {
            StatsText = string.Empty;
            return;
        }

        var input = Input;
        var options = new SqlFormatOptions
        {
            Dialect = Dialect,
            IndentStyle = IndentStyle,
            KeywordCase = KeywordCase,
            LeadingCommas = LeadingCommas,
        };

        var result = await ComputeAsync(() => SqlFormatter.Format(input, options), token);
        Apply(result);

        if (result.IsSuccess)
        {
            StatsText = $"{SqlFormatter.DisplayName(Dialect)} · {TextUtil.CountLines(result.Value):N0} lines";
        }
    }

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("dialect", Dialect);
        state.Set("indent", IndentStyle);
        state.Set("case", KeywordCase);
        state.Set("leadingCommas", LeadingCommas);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        Dialect = state.GetEnum("dialect", SqlDialect.StandardSql);
        IndentStyle = state.GetEnum("indent", IndentStyle.TwoSpaces);
        KeywordCase = state.GetEnum("case", SqlKeywordCase.Upper);
        LeadingCommas = state.GetBool("leadingCommas");
    }

    protected override void ResetOptions()
    {
        Dialect = SqlDialect.StandardSql;
        IndentStyle = IndentStyle.TwoSpaces;
        KeywordCase = SqlKeywordCase.Upper;
        LeadingCommas = false;
        StatsText = string.Empty;
    }
}
