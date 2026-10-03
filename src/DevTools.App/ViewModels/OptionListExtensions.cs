namespace DevTools.App.ViewModels;

/// <summary>
/// Helpers for the <c>…Index</c> properties the option combo boxes bind to.
/// </summary>
/// <remarks>
/// WinUI's <c>x:Bind</c> cannot two-way bind a <c>ComboBox.SelectedItem</c> to an enum without
/// a converter per enum, so each option exposes an <c>int</c> index over its own value list and
/// the enum stays the single source of truth. This keeps that translation to two lines per
/// option instead of a converter and a resource entry.
/// </remarks>
internal static class OptionListExtensions
{
    public static int IndexOfValue<T>(this IReadOnlyList<T> list, T value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(list[i], value))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Reads back a selected index, clamped so a mid-update -1 cannot throw.</summary>
    public static T ValueAt<T>(this IReadOnlyList<T> list, int index, T fallback) =>
        index >= 0 && index < list.Count ? list[index] : fallback;
}
