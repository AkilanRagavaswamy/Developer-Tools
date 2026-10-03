using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DevTools.App.Controls;

/// <summary>Small visual-tree walkers used where WinUI exposes no direct accessor.</summary>
public static class VisualTreeHelperExtensions
{
    /// <summary>
    /// First descendant of type <typeparamref name="T"/>, optionally matching a template name.
    /// Used to reach the <c>ScrollViewer</c> inside a <c>TextBox</c> template, which is the only
    /// way to keep a line-number gutter in sync with the text as it scrolls.
    /// </summary>
    public static T? FindDescendant<T>(this DependencyObject? root, string? name = null)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T typed &&
                (name is null || (child as FrameworkElement)?.Name == name))
            {
                return typed;
            }

            var nested = child.FindDescendant<T>(name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>First ancestor of type <typeparamref name="T"/>, or null.</summary>
    public static T? FindAscendant<T>(this DependencyObject? element)
        where T : DependencyObject
    {
        var current = element;
        while (current is not null)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is T typed)
            {
                return typed;
            }
        }

        return null;
    }
}
