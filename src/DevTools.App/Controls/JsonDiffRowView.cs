using Microsoft.UI.Xaml.Media;

namespace DevTools.App.Controls;

/// <summary>One row of the side-by-side diff, as the list shows it.</summary>
/// <remarks>
/// <para>
/// A plain class with a parameterless constructor and settable properties, in a file of its
/// own. A compiled binding's <c>x:DataType</c> has to be a type the XAML tooling can describe,
/// and a primary-constructor class declared beside the control it is used by is not reliably
/// one — the page it appeared on stopped parsing altogether.
/// </para>
/// <para>
/// The brush is on the row rather than resolved from a category by a converter: a converter
/// reads <c>Application.Current.Resources</c>, which answers for the application's theme and
/// not the window's, so it returns the wrong colour as soon as the two differ.
/// </para>
/// </remarks>
public sealed class JsonDiffRowView
{
    public string LeftLine { get; init; } = string.Empty;

    public string LeftText { get; init; } = string.Empty;

    public string RightLine { get; init; } = string.Empty;

    public string RightText { get; init; } = string.Empty;

    public Brush? RowBackground { get; init; }

    /// <summary>Which difference this row belongs to, or -1 when the two sides agree.</summary>
    public int DifferenceIndex { get; init; } = -1;
}
