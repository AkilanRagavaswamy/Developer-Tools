using System.Globalization;
using DevTools.Core.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevTools.App.Controls;

/// <summary>
/// A read-only grid for <see cref="JsonTableResult"/>: a fixed header, virtualised rows, and
/// columns sized to what they hold.
/// </summary>
/// <remarks>
/// WinUI 3 ships no data grid, and the toolkit's has been retired; a list of rows whose cells
/// share widths worked out up front is all a read-only table needs, and it virtualises.
/// </remarks>
public sealed partial class DataTableView : UserControl
{
    private const double CharacterWidth = 7.6;
    private const double CellPadding = 18;
    private const double MinColumnWidth = 64;
    private const double MaxColumnWidth = 420;

    /// <summary>Widths are judged on this many rows; past that, a long cell is trimmed with an ellipsis.</summary>
    private const int SampleRows = 500;

    public DataTableView()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Rebuild();
    }

    public static readonly DependencyProperty TableProperty = DependencyProperty.Register(
        nameof(Table), typeof(JsonTableResult), typeof(DataTableView), new PropertyMetadata(null, (d, _) => ((DataTableView)d).Rebuild()));

    public JsonTableResult? Table
    {
        get => (JsonTableResult?)GetValue(TableProperty);
        set => SetValue(TableProperty, value);
    }

    private void Rebuild()
    {
        if (Table is not { Columns.Count: > 0 } table)
        {
            HeaderCells.ItemsSource = null;
            RowList.ItemsSource = null;
            return;
        }

        var widths = new double[table.Columns.Count];
        for (var c = 0; c < widths.Length; c++)
        {
            var longest = table.Columns[c].Length;
            for (var r = 0; r < Math.Min(SampleRows, table.Rows.Count); r++)
            {
                longest = Math.Max(longest, table.Rows[r][c].Length);
            }

            widths[c] = Math.Clamp((longest * CharacterWidth) + CellPadding, MinColumnWidth, MaxColumnWidth);
        }

        HeaderCells.ItemsSource = table.Columns
            .Select((name, c) => new DataTableCellView { Text = name, Width = widths[c] })
            .ToList();

        // Every other row is shaded, which is what keeps a wide row readable across the screen.
        var stripe = StripeSwatch.Background;
        var rows = new List<DataTableRowView>(table.Rows.Count);

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var source = table.Rows[r];
            var cells = new DataTableCellView[source.Length];

            for (var c = 0; c < source.Length; c++)
            {
                // A cell shows one line; a value with line breaks keeps them as visible markers.
                var text = source[c];
                cells[c] = new DataTableCellView
                {
                    Text = text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal)
                        ? text.Replace("\r\n", " ↵ ", StringComparison.Ordinal).Replace('\n', '↵').Replace('\r', '↵')
                        : text,
                    Width = widths[c],
                };
            }

            rows.Add(new DataTableRowView
            {
                Number = (r + 1).ToString("N0", CultureInfo.CurrentCulture),
                Cells = cells,
                Background = r % 2 == 1 ? stripe : null,
            });
        }

        RowList.ItemsSource = rows;
    }
}
