using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DevTools.App.Services;

public interface IThemeService
{
    event EventHandler? ThemeChanged;

    ElementTheme CurrentTheme { get; }

    /// <summary>The theme actually in effect once <see cref="ElementTheme.Default"/> is resolved.</summary>
    ElementTheme EffectiveTheme { get; }

    bool IsDark { get; }

    void Initialize(Window window);

    void ApplyTheme(ElementTheme theme);

    void ApplyBackdrop(BackdropKind kind);

    void ToggleTheme();
}

/// <summary>
/// Applies theme and window backdrop live, with no restart (FR-S08, FR-S09). Backdrop
/// selection probes OS support and degrades to a solid brush, so the app is correct on
/// Windows 10 and in VMs where transparency is off.
/// </summary>
public sealed class ThemeService(ISettingsService settings) : IThemeService
{
    private Window? _window;

    public event EventHandler? ThemeChanged;

    public ElementTheme CurrentTheme => settings.Theme;

    public ElementTheme EffectiveTheme
    {
        get
        {
            if (settings.Theme != ElementTheme.Default)
            {
                return settings.Theme;
            }

            if (_window?.Content is FrameworkElement element)
            {
                return element.ActualTheme;
            }

            return Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? ElementTheme.Dark
                : ElementTheme.Light;
        }
    }

    public bool IsDark => EffectiveTheme == ElementTheme.Dark;

    public void Initialize(Window window)
    {
        _window = window;

        if (window.Content is FrameworkElement element)
        {
            // Following the system theme means reacting when the system theme changes.
            element.ActualThemeChanged += (_, _) => ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        ApplyTheme(settings.Theme);
        ApplyBackdrop(settings.Backdrop);
    }

    public void ApplyTheme(ElementTheme theme)
    {
        settings.Theme = theme;

        if (_window?.Content is FrameworkElement element)
        {
            element.RequestedTheme = theme;
        }

        UpdateCaptionButtonColors();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleTheme() =>
        ApplyTheme(EffectiveTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark);

    public void ApplyBackdrop(BackdropKind kind)
    {
        settings.Backdrop = kind;

        if (_window is null)
        {
            return;
        }

        try
        {
            _window.SystemBackdrop = kind switch
            {
                BackdropKind.Mica when MicaController.IsSupported() =>
                    new MicaBackdrop { Kind = MicaKind.Base },
                BackdropKind.MicaAlt when MicaController.IsSupported() =>
                    new MicaBackdrop { Kind = MicaKind.BaseAlt },
                BackdropKind.Acrylic when DesktopAcrylicController.IsSupported() =>
                    new DesktopAcrylicBackdrop(),
                _ => null,
            };
        }
        catch (Exception)
        {
            // An unsupported or failed backdrop must not take the window down: the page
            // background brush already paints a correct opaque surface underneath.
            _window.SystemBackdrop = null;
        }
    }

    /// <summary>
    /// With an extended title bar, the caption buttons are drawn by the system and do not
    /// follow the element theme on their own; they have to be told.
    /// </summary>
    private void UpdateCaptionButtonColors()
    {
        if (_window?.AppWindow?.TitleBar is not { } titleBar)
        {
            return;
        }

        try
        {
            var dark = EffectiveTheme == ElementTheme.Dark;
            var foreground = dark ? Colors.White : Colors.Black;
            var hover = dark
                ? Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x20, 0x00, 0x00, 0x00);
            var pressed = dark
                ? Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x40, 0x00, 0x00, 0x00);

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = dark
                ? Windows.UI.Color.FromArgb(0xFF, 0x80, 0x80, 0x80)
                : Windows.UI.Color.FromArgb(0xFF, 0x70, 0x70, 0x70);
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = pressed;
            titleBar.ButtonPressedForegroundColor = foreground;
        }
        catch (Exception)
        {
            // Title-bar customisation is unavailable on some OS builds; ignore.
        }
    }
}
