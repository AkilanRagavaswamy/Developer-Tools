using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using Microsoft.UI.Xaml;

namespace DevTools.App.ViewModels;

/// <summary>One row in the keyboard-shortcut reference shown in Settings (FR-S14).</summary>
public sealed record ShortcutEntry(string Keys, string Action)
{
    /// <summary>Every shortcut the app responds to — shown in Settings and from the title bar.</summary>
    public static IReadOnlyList<ShortcutEntry> All { get; } =
    [
        new("Ctrl + K", "Jump to a tool"),
        new("Ctrl + N", "Scratchpad: new note"),
        new("Ctrl + Shift + F", "Scratchpad: search notes"),
        new("Ctrl + Shift + K", "Scratchpad: send to a tool"),
        new("Shift + Alt + F", "Scratchpad: format the note"),
        new("Ctrl + Shift + ;", "Scratchpad: insert the date and time"),
        new("Ctrl + ,", "Open Settings"),
        new("Ctrl + Enter", "Run the current tool"),
        new("Ctrl + S", "Save the current tool's output"),
        new("Ctrl + L", "Clear the current tool"),
        new("Ctrl + D", "Pin or unpin the current tool"),
        new("Ctrl + F", "Find in the text pane that has focus"),
        new("Enter / Shift+Enter", "Next / previous match while finding"),
        new("Esc", "Close the tool list or the find bar; stop a running request"),
        new("Alt + Left", "Back"),
        new("Alt + Right", "Forward"),
        new("F1", "About ForgeKitRk"),
    ];
}

/// <summary>Backs the Settings page (FR-S13). Every change applies immediately.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IToolStateService _state;
    private readonly IRecentToolsService _recents;
    private readonly IFavoritesService _favorites;
    private readonly IDialogService _dialogs;
    private readonly IScratchpadStore _scratchpad;

    public SettingsViewModel(
        ISettingsService settings,
        IThemeService theme,
        IToolStateService state,
        IRecentToolsService recents,
        IFavoritesService favorites,
        IDialogService dialogs,
        IScratchpadStore scratchpad)
    {
        _scratchpad = scratchpad;
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

    // ---------------------------------------------------------------- scratchpad

    public double ScratchpadRetentionDays
    {
        get => _settings.ScratchpadRetentionDays;
        set
        {
            // A cleared NumberBox reports NaN; keep the current value rather than jump to 1.
            if (double.IsNaN(value))
            {
                OnPropertyChanged();
                return;
            }

            _settings.ScratchpadRetentionDays = (int)Math.Round(value);
            OnPropertyChanged();
        }
    }

    public double MinimumRetentionDays => DevTools.Core.Scratch.ScratchRetention.MinDays;

    public double MaximumRetentionDays => DevTools.Core.Scratch.ScratchRetention.MaxDays;

    public int ScratchpadStartIndex
    {
        get => _settings.ScratchpadStartWithNewNote ? 1 : 0;
        set
        {
            _settings.ScratchpadStartWithNewNote = value == 1;
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
    private async Task ClearScratchpadHistoryAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                "Clear Scratchpad history",
                "Every earlier version of every note will be removed. The notes themselves are kept. This cannot be undone.",
                "Clear"))
        {
            return;
        }

        await _scratchpad.InitializeAsync();
        _scratchpad.ClearHistory();
        StatusMessage = "Scratchpad history cleared.";
    }

    [RelayCommand]
    private async Task TrashAllScratchNotesAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                "Move all notes to Trash",
                $"Every Scratchpad note will move to Trash. They can be restored from there for {_settings.ScratchpadRetentionDays} days.",
                "Move to Trash"))
        {
            return;
        }

        await _scratchpad.InitializeAsync();
        var count = _scratchpad.TrashAll();
        StatusMessage = count == 1 ? "1 note moved to Trash." : $"{count:N0} notes moved to Trash.";
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
        OnPropertyChanged(nameof(ScratchpadRetentionDays));
        OnPropertyChanged(nameof(ScratchpadStartIndex));

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

    /// <summary>Read from the package, so About can never disagree with what the Store installed.</summary>
    public string AppVersion
    {
        get
        {
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch (InvalidOperationException)
            {
                return typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.1.0";
            }
        }
    }

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

    public IReadOnlyList<ShortcutEntry> Shortcuts => ShortcutEntry.All;

    public IReadOnlyList<string> Licenses { get; } =
    [
        "Windows App SDK — MIT License, © Microsoft Corporation",
        "CommunityToolkit.Mvvm — MIT License, © .NET Foundation",
        "CommunityToolkit.WinUI Controls — MIT License, © .NET Foundation",
        "Microsoft.Extensions.DependencyInjection — MIT License, © .NET Foundation",
        "ZXing.Net (QR encoding) — Apache License 2.0, © the ZXing.Net and ZXing authors",
        "Markdig (Markdown) — BSD 2-Clause License, © 2016–2026 Alexandre Mutel",
        "SQL formatter keyword tables and layout rules — MIT License, © 2021 DevToys, derived from sql-formatter © ZeroTurnaround LLC and contributors",
        "Full licence texts ship with the app in THIRD-PARTY-NOTICES.md.",
    ];

    /// <summary>
    /// The privacy statement, shown in Settings because a claim like this has to be checkable
    /// rather than buried in a licence (NFR-05, NFR-06).
    /// </summary>
    public IReadOnlyList<string> PrivacyNotes { get; } =
    [
        "Every tool except API Builder runs entirely on this machine and has no network code at all.",
        "What you enter in a tool is forgotten when the app closes, with one exception: Scratchpad notes and their earlier versions are saved as plain, unencrypted text in the app's folder on this PC, and nowhere else.",
        "Markdown Preview and HTML Viewer refuse every request the page makes, so a document cannot make them load an image, a script or anything else from the internet.",
        "API Builder sends exactly the requests you compose, to the addresses you give it, and nowhere else.",
        "No telemetry, no analytics, no crash reporting and no update checks — ever.",
        "Passwords, tokens and client secrets are held in the Windows credential vault, never written into a collection file.",
    ];
}
