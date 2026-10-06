using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using DevTools.Core.Json;

namespace DevTools.App.ViewModels.Tools;

/// <summary>JSON to Table.</summary>
public sealed partial class JsonToTableViewModel : TextToolViewModelBase
{
    public JsonToTableViewModel(ToolServices services)
        : base(services)
    {
        FlattenObjects = true;
        StatsText = string.Empty;
    }

    public override string ToolId => "json-to-table";

    protected override string SuggestedFileName => "table.csv";

    protected override string[] OutputExtensions => [".csv", ".tsv", ".md", ".txt"];

    protected override string[] InputExtensions => [".json", ".txt"];

    [ObservableProperty]
    public partial bool FlattenObjects { get; set; }

    /// <summary>The table the grid shows; empty until a document converts.</summary>
    [ObservableProperty]
    public partial JsonTableResult Table { get; set; } = JsonTableResult.Empty;

    [ObservableProperty]
    public partial string StatsText { get; set; }

    public bool HasTable => Table.Columns.Count > 0;

    partial void OnTableChanged(JsonTableResult value) => OnPropertyChanged(nameof(HasTable));

    partial void OnFlattenObjectsChanged(bool value) => OnOptionChanged();

    protected override async Task RunCoreAsync(CancellationToken token)
    {
        if (HandleEmptyInput())
        {
            Table = JsonTableResult.Empty;
            StatsText = string.Empty;
            return;
        }

        var input = Input;
        var options = new JsonTableOptions { FlattenObjects = FlattenObjects };
        var result = await ComputeAsync(() => JsonTable.Convert(input, options), token);

        if (!result.IsSuccess)
        {
            SetError(result.Error!);
            return;
        }

        Table = result.Value!;
        Output = await ComputeAsync(() => JsonTable.ToText(result.Value!, TableTextFormat.Csv), token);
        StatsText = $"{Table.Rows.Count:N0} rows · {Table.Columns.Count:N0} columns";

        if (result.HasWarning)
        {
            SetWarning(result.Warning!);
        }
        else
        {
            ClearMessage();
        }
    }

    /// <summary>Copies the table as CSV, TSV or Markdown. TSV pastes straight into a spreadsheet.</summary>
    [RelayCommand]
    private void CopyAs(string? format)
    {
        if (!HasTable)
        {
            return;
        }

        var kind = format switch
        {
            "tsv" => TableTextFormat.Tsv,
            "md" => TableTextFormat.Markdown,
            _ => TableTextFormat.Csv,
        };

        Services.Clipboard.SetText(JsonTable.ToText(Table, kind));
        SetSuccess(kind switch
        {
            TableTextFormat.Tsv => "Copied as tab-separated values — paste it into a spreadsheet.",
            TableTextFormat.Markdown => "Copied as a Markdown table.",
            _ => "Copied as CSV.",
        });
    }

    public override void Clear()
    {
        base.Clear();
        Table = JsonTableResult.Empty;
        StatsText = string.Empty;
    }

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("flatten", FlattenObjects);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        FlattenObjects = state.GetBool("flatten", true);
    }

    protected override void ResetOptions()
    {
        FlattenObjects = true;
        Table = JsonTableResult.Empty;
        StatsText = string.Empty;
    }
}
