using System.Globalization;
using DevTools.App.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DevTools.App;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 820;
    private const int MinWidth = 640;
    private const int MinHeight = 480;

    private readonly IShellContext _shell;
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IToolStateService _state;
    private readonly IScratchpadStore _scratchpad;

    public MainWindow()
    {
        InitializeComponent();

        _shell = App.GetService<IShellContext>();
        _settings = App.GetService<ISettingsService>();
        _theme = App.GetService<IThemeService>();
        _state = App.GetService<IToolStateService>();
        _scratchpad = App.GetService<IScratchpadStore>();

        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _shell.Attach(this, handle);

        Title = "ForgeKitRk";
        AppWindow.SetIcon("Assets/ForgeKitRk.ico");

        // The shell draws its own title bar row, so the system one is extended away.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Shell.TitleBarElement);

        // Tall caption buttons, because the shell's row is 48 px and the standard ones are 32:
        // minimise, maximise and close sat in the top two thirds of the row while our buttons
        // were centred in all of it, which is what made the right-hand end look misaligned.
        // Tall makes the system band the same 48 px, so every button in the row shares a centre.
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }

        RestorePlacement();

        _theme.Initialize(this);

        Shell.Loaded += OnShellLoaded;
        Activated += OnActivated;
        Closed += OnClosed;
        AppWindow.Changed += OnAppWindowChanged;
    }

    private bool _initialized;

    /// <summary>
    /// Start-up is driven by the shell loading, not by the window being activated.
    /// </summary>
    /// <remarks>
    /// A window can appear without ever being activated — launched behind whatever had focus,
    /// or opened by a protocol activation while another app is in front. Waiting for activation
    /// meant the navigation pane and the content frame stayed empty until the user happened to
    /// click on the window, which looks exactly like a hung app. Loaded always fires.
    /// </remarks>
    private void OnShellLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _shell.SetXamlRoot(Shell.XamlRoot);
        _ = InitializeShellAsync();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated || !_initialized)
        {
            return;
        }

        // Coming back to the app is the moment to re-check the clipboard (FR-S07).
        _ = Shell.RunSmartDetectAsync();
    }

    /// <summary>
    /// Start-up runs on a fire-and-forget task, whose exceptions never reach the app's
    /// unhandled-exception handler. Without this catch a failure here would leave an empty
    /// window and no explanation anywhere.
    /// </summary>
    private async Task InitializeShellAsync()
    {
        try
        {
            await Shell.InitializeAsync();
        }
        catch (Exception ex)
        {
            App.LogError("Shell initialization failed", ex);

            await App.GetService<IDialogService>().ShowMessageAsync(
                "DevTools could not finish starting",
                $"{ex.Message}\n\nThe details were written to the logs folder.");
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // The caption-button inset changes with the display, so the title bar is re-measured
        // whenever the window moves between monitors or is resized.
        Shell.UpdateCaptionInset();

        if (args.DidSizeChange || args.DidPositionChange)
        {
            SavePlacement();
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        SavePlacement();

        // Scratch notes are the user's work, so they are written before the window is allowed to go:
        // an async void handler gets no guarantee the process waits for it. The store never
        // resumes on the UI thread, so blocking here cannot deadlock.
        try
        {
            _scratchpad.FlushAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            App.LogError("Saving scratch notes on close", ex);
        }

        await _state.FlushAsync();
    }

    // ------------------------------------------------------------- placement

    /// <summary>
    /// Restores the saved size, position and maximised state, clamping a window that would
    /// land off-screen back onto a visible monitor — for instance after a second display was
    /// unplugged (FR-S11).
    /// </summary>
    private void RestorePlacement()
    {
        var placement = ParsePlacement(_settings.WindowPlacement);

        if (placement is null)
        {
            CenterOnScreen(DefaultWidth, DefaultHeight);
            return;
        }

        var (x, y, width, height, maximized) = placement.Value;

        width = Math.Max(width, MinWidth);
        height = Math.Max(height, MinHeight);

        var area = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest);
        var work = area.WorkArea;

        // If the saved rectangle does not intersect any monitor, recentre rather than
        // restoring a window the user cannot see.
        var intersects = x < work.X + work.Width &&
                         x + width > work.X &&
                         y < work.Y + work.Height &&
                         y + height > work.Y;

        if (!intersects)
        {
            CenterOnScreen(Math.Max(width, DefaultWidth), Math.Max(height, DefaultHeight));
        }
        else
        {
            x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - width));
            y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }

        if (maximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private void CenterOnScreen(int width, int height)
    {
        var area = DisplayArea.Primary;
        var work = area.WorkArea;
        var x = work.X + Math.Max(0, (work.Width - width) / 2);
        var y = work.Y + Math.Max(0, (work.Height - height) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, Math.Min(width, work.Width), Math.Min(height, work.Height)));
    }

    private void SavePlacement()
    {
        try
        {
            // A minimised window reports the off-screen placeholder rectangle Windows parks it
            // in, not where it will reappear. Saving that would restore the app somewhere nobody
            // can see it, so the last real placement is kept instead.
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            {
                return;
            }

            var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };

            // A maximised window reports the maximised rectangle; keep the last restored
            // rectangle so unmaximising returns somewhere sensible.
            if (maximized)
            {
                var existing = ParsePlacement(_settings.WindowPlacement);
                if (existing is { } previous)
                {
                    _settings.WindowPlacement = FormatPlacement(
                        previous.X, previous.Y, previous.Width, previous.Height, maximized: true);
                    return;
                }
            }

            _settings.WindowPlacement = FormatPlacement(
                AppWindow.Position.X,
                AppWindow.Position.Y,
                AppWindow.Size.Width,
                AppWindow.Size.Height,
                maximized);
        }
        catch (Exception)
        {
            // Placement is a nicety; never let it interfere with closing.
        }
    }

    private static string FormatPlacement(int x, int y, int width, int height, bool maximized) =>
        string.Join(',', [
            x.ToString(CultureInfo.InvariantCulture),
            y.ToString(CultureInfo.InvariantCulture),
            width.ToString(CultureInfo.InvariantCulture),
            height.ToString(CultureInfo.InvariantCulture),
            maximized ? "1" : "0",
        ]);

    private static (int X, int Y, int Width, int Height, bool Maximized)? ParsePlacement(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',');
        if (parts.Length != 5)
        {
            return null;
        }

        if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) &&
            int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) &&
            width > 0 && height > 0)
        {
            return (x, y, width, height, parts[4] == "1");
        }

        return null;
    }
}
