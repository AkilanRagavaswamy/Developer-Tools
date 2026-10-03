using System.Globalization;
using DevTools.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace DevTools.App.Converters;

/// <summary>true becomes Visible. Pass "Invert" as the parameter to flip it.</summary>
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (IsInverted(parameter))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        var visible = value is Visibility v && v == Visibility.Visible;
        return IsInverted(parameter) ? !visible : visible;
    }

    internal static bool IsInverted(object parameter) =>
        parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A non-empty string becomes Visible. Pass "Invert" to flip it.</summary>
public sealed partial class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var hasText = value is string s && !string.IsNullOrEmpty(s);
        if (BoolToVisibilityConverter.IsInverted(parameter))
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Inverts a boolean, for IsEnabled bindings driven by a busy flag.</summary>
public sealed partial class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is not bool b || !b;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is not bool b || !b;
}

/// <summary>A non-null value becomes Visible. Pass "Invert" to flip it.</summary>
public sealed partial class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var hasValue = value is not null;
        if (BoolToVisibilityConverter.IsInverted(parameter))
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>A count greater than zero becomes Visible. Pass "Invert" to flip it.</summary>
public sealed partial class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var count = value switch
        {
            int i => i,
            System.Collections.ICollection c => c.Count,
            _ => 0,
        };

        var any = count > 0;
        if (BoolToVisibilityConverter.IsInverted(parameter))
        {
            any = !any;
        }

        return any ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Maps the view-model severity enum onto the InfoBar severity, keeping UI types out of view models.</summary>
public sealed partial class SeverityToInfoBarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is ToolMessageSeverity severity
            ? severity switch
            {
                ToolMessageSeverity.Success => InfoBarSeverity.Success,
                ToolMessageSeverity.Warning => InfoBarSeverity.Warning,
                ToolMessageSeverity.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational,
            }
            : InfoBarSeverity.Informational;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Filled star when pinned, outline star when not.</summary>
public sealed partial class FavoriteGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b && b ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>"Remove from favorites" / "Add to favorites", for the tooltip and automation name.</summary>
public sealed partial class FavoriteLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b && b ? "Remove from favorites" : "Add to favorites";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// True when the bound enum equals the parameter — lets a group of RadioButtons bind
/// two-way to a single enum property.
/// </summary>
public sealed partial class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is null || parameter is not string name)
        {
            return false;
        }

        return string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b && b && parameter is string name && targetType.IsEnum)
        {
            return Enum.Parse(targetType, name, ignoreCase: true);
        }

        return DependencyProperty.UnsetValue;
    }
}

/// <summary>Formats a double 0..1 as a whole-number percentage.</summary>
public sealed partial class RatioToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is double d
            ? (d * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%"
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}


/// <summary>
/// Colours an HTTP verb the way every API tool does: green reads, amber creates, blue
/// replaces, red deletes.
/// </summary>
/// <remarks>
/// The colours are literals rather than theme resources on purpose. A converter has no element
/// to resolve a <c>ThemeResource</c> against, so it would have to ask the application — which
/// gives the wrong answer the moment the window is showing a theme the app did not start in.
/// These four are chosen to read on both.
/// </remarks>
public sealed partial class MethodBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var method = value as string ?? string.Empty;

        var color = method.ToUpperInvariant() switch
        {
            "GET" or "HEAD" => Windows.UI.Color.FromArgb(0xFF, 0x34, 0xA8, 0x53),
            "POST" => Windows.UI.Color.FromArgb(0xFF, 0xD9, 0x87, 0x0A),
            "PUT" or "PATCH" => Windows.UI.Color.FromArgb(0xFF, 0x33, 0x8E, 0xDA),
            "DELETE" => Windows.UI.Color.FromArgb(0xFF, 0xE0, 0x52, 0x52),
            _ => Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A),
        };

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Shortens a verb to fit the 32 px badge: DELETE reads as DEL, OPTIONS as OPT.</summary>
public sealed partial class MethodBadgeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        (value as string ?? string.Empty).ToUpperInvariant() switch
        {
            "DELETE" => "DEL",
            "OPTIONS" => "OPT",
            var other => other,
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
