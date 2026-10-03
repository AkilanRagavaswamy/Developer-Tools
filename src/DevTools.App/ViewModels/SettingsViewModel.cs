using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using Microsoft.UI.Xaml;

namespace DevTools.App.ViewModels;

/// <summary>One row in the keyboard-shortcut reference shown in Settings (FR-S14).</summary>
public sealed record ShortcutEntry(string Keys, string Action);

/// <summary>Backs the Settings page (FR-S13). Every change applies immediately.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IToolStateService _state;
    private readonly IRecentToolsService _recents;
    private readonly IFavoritesService _favorites;
    private readonly IDialogService _dialogs;

    public SettingsViewModel(
        ISettingsService settings,
        IThemeService theme,
        IToolStateService state,
        IRecentToolsService recents,
        IFavoritesService favorites,
        IDialogService dialogs)
    {
        _settings = settings;
        _theme = theme;
        _state = state;
        _recents = recents;
        _favorites = favorites;
        _dialogs = dialogs;
    }

    // ---------------------------------------------------------------- appearance

    public int ThemeIndex
    {
        get => _settings.Theme switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0,
        };
        set
        {
            var theme = value switch
            {
                1 => ElementTheme.Light,
                2 => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };

            _theme.ApplyTheme(theme);
            OnPropertyChanged();
        }
    }

    public int BackdropIndex
    {
        get => (int)_settings.Backdrop;
        set
        {
            _theme.ApplyBackdrop((BackdropKind)value);
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- editor

    public double EditorFontSize
    {
        get => _settings.EditorFontSize;
        set
        {
            _settings.EditorFontSize = value;
            OnPropertyChanged();
        }
    }

    public double MinimumFontSize => SettingsService.MinFontSize;

    public double MaximumFontSize => SettingsService.MaxFontSize;

    public bool WordWrap
    {
        get => _settings.EditorWordWrap;
        set
        {
            _settings.EditorWordWrap = value;
            OnPropertyChanged();
        }
    }

    public bool SyntaxColouring
    {
        get => _settings.EditorSyntaxColouring;
        set
        {
            _settings.EditorSyntaxColouring = value;
            OnPropertyChanged();
        }
    }

    public bool LineNumbers
    {
        get => _settings.EditorLineNumbers;
        set
        {
            _settings.EditorLineNumbers = value;
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- behaviour

    public bool SmartDetect
    {
        get => _settings.SmartDetectEnabled;
        set
        {
            _settings.SmartDetectEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool PersistToolState
    {
        get => _settings.PersistToolState;
        set
        {
            _settings.PersistToolState = value;
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- tools

    public bool JsonDiffTextViews
    {
        get => _settings.JsonDiffTextViews;
        set
        {
            _settings.JsonDiffTextViews = value;
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private async Task ClearToolStateAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                "Clear saved tool state",
                "Every tool will forget its saved input and options. This cannot be undone.",
                "Clear"))
        {
            return;
        }

        await _state.ClearAllAsync();
        StatusMessage = "Saved tool state cleared.";
    }

    [RelayCommand]
    private async Task ClearRecentsAsync()
    {
        await _recents.ClearAsync();
        StatusMessage = "Recent tools cleared.";
    }

    [RelayCommand]
    private async Task ClearFavoritesAsync()
    {
        if (!await _dialogs.ConfirmAsync("Clear favorites", "All pinned tools will be unpinned.", "Clear"))
        {
            return;
        }

        await _favorites.ClearAsync();
        StatusMessage = "Favorites cleared.";
    }

    [RelayCommand]
    private async Task ResetAllSettingsAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                "Reset all settings",
                "Theme, backdrop, editor options and window placement return to their defaults.",
                "Reset"))
        {
            return;
        }

        _settings.Reset();
        _theme.ApplyTheme(_settings.Theme);
        _theme.ApplyBackdrop(_settings.Backdrop);

        OnPropertyChanged(nameof(ThemeIndex));
        OnPropertyChanged(nameof(BackdropIndex));
        OnPropertyChanged(nameof(EditorFontSize));
        OnPropertyChanged(nameof(WordWrap));
        OnPropertyChanged(nameof(LineNumbers));
        OnPropertyChanged(nameof(SmartDetect));
        OnPropertyChanged(nameof(SyntaxColouring));
        OnPropertyChanged(nameof(PersistToolState));
        OnPropertyChanged(nameof(JsonDiffTextViews));

        StatusMessage = "Settings reset.";
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = JsonStore.RootPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the folder: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- about

    public string AppVersion => "1.0.0";

    public string RuntimeVersion => $".NET {Environment.Version}";

    public string WindowsAppSdkVersion => "Windows App SDK 2.4.0";

    public string OperatingSystem => Environment.OSVersion.VersionString;

    public string DataFolder => JsonStore.RootPath;

    public string Developer => "R K Akilan";

    public Uri RepositoryUri { get; } = new("https://github.com/AkilanRagavaswamy/Developer-Tools");

    public string RepositoryText => "Navigate to repository";

    public string LicenseName => "MIT License";

    public Uri LicenseUri { get; } = new("https://github.com/AkilanRagavaswamy/Developer-Tools?tab=MIT-1-ov-file");

    public string Copyright => "© 2026 R K Akilan. All rights reserved.";

    /// <summary>The licence DevTools itself is released under, as it appears in the repository.</summary>
    public string LicenseText =>
        "Copyright (c) 2026 R K Akilan\n\n" +
        "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and " +
        "associated documentation files (the \"Software\"), to deal in the Software without restriction, " +
        "including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, " +
        "and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, " +
        "subject to the following conditions:\n\n" +
        "The above copyright notice and this permission notice shall be included in all copies or substantial " +
        "portions of the Software.\n\n" +
        "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT " +
        "LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO " +
        "EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER " +
        "IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR " +
        "THE USE OR OTHER DEALINGS IN THE SOFTWARE.";

    public IReadOnlyList<ShortcutEntry> Shortcuts { get; } =
    [
        new("Ctrl + K", "Open the command palette"),
        new("Ctrl + ,", "Open Settings"),
        new("Ctrl + D", "Pin or unpin the current tool"),
        new("Ctrl + Enter", "Run the current tool"),
        new("Ctrl + L", "Clear the current tool input"),
        new("Ctrl + S", "Save the current tool output"),
        new("Alt + Left", "Back"),
        new("Alt + Right", "Forward"),
        new("F1", "About DevTools"),
        new("Esc", "Close the palette or an overlay"),
    ];

    public IReadOnlyList<string> Licenses { get; } =
    [
        "Windows App SDK — MIT License, © Microsoft Corporation",
        "CommunityToolkit.Mvvm — MIT License, © .NET Foundation",
        "CommunityToolkit.WinUI Controls — MIT License, © .NET Foundation",
    ];

    /// <summary>
    /// The privacy statement, shown in Settings because a claim like this has to be checkable
    /// rather than buried in a licence (NFR-05, NFR-06).
    /// </summary>
    public IReadOnlyList<string> PrivacyNotes { get; } =
    [
        "The JSON, SVG and code-generation tools run entirely on this machine. They have no network code at all.",
        "API Builder and API Profiler send exactly the requests you compose, to the addresses you give them, and nowhere else.",
        "No telemetry, no analytics, no crash reporting and no update checks — ever.",
        "Passwords, tokens and client secrets are held in the Windows credential vault, never written into a collection file.",
    ];
}
