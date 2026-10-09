using Microsoft.UI.Xaml.Media;

namespace DevTools.App.Controls;

/// <summary>One cell, carrying the width of its column so every row lines up.</summary>
/// <remarks>In a file of its own, for the same XAML-tooling reason as <see cref="JsonDiffRowView"/>.</remarks>
public sealed class DataTableCellView
{
    public string Text { get; init; } = string.Empty;

    public double Width { get; init; }
}

/// <summary>One row of the table.</summary>
public sealed class DataTableRowView
{
    public string Number { get; init; } = string.Empty;

    public IReadOnlyList<DataTableCellView> Cells { get; init; } = [];

    public Brush? Background { get; init; }
}
