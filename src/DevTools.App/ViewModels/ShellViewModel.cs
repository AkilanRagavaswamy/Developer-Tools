using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Models;
using DevTools.App.Services;
using DevTools.Core.Detection;
using Microsoft.UI.Xaml;

namespace DevTools.App.ViewModels;

/// <summary>A tool offered by the clipboard suggestion banner.</summary>
public sealed record SuggestionItem(string ToolId, string Name, string Glyph, Microsoft.UI.Xaml.Media.FontFamily GlyphFont);

/// <summary>
/// Drives the shell: the search index behind both the title-bar box and the command palette,
/// the favorites group, and the clipboard suggestion banner.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ToolCatalog _catalog;
    private readonly IFavoritesService _favorites;
    private readonly IRecentToolsService _recents;
    private readonly IClipboardService _clipboard;
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IToolChrome _chrome;

    private string? _lastDetectedClipboard;

    public ShellViewModel(ToolCatalog catalog, IFavoritesService favorites,
        IRecentToolsService recents, IClipboardService clipboard, ISettingsService settings,
        IThemeService theme, IToolChrome chrome)
    {
        _catalog = catalog;
        _favorites = favorites;
        _recents = recents;
        _clipboard = clipboard;
        _settings = settings;
        _theme = theme;
        _chrome = chrome;

        PaletteQuery = string.Empty;

        // Through the dispatcher: these fire from whichever thread finished reading the file,
        // and a binding update off the UI thread brings the shell down (see UiDispatcher).
        _favorites.Changed += (_, _) => UiDispatcher.Run(() =>
        {
            RefreshFavorites();
            RaiseActiveToolChanged();
        });

        _chrome.ActiveChanged += (_, _) => UiDispatcher.Run(() =>
        {
            RaiseActiveToolChanged();
            PruneActiveToolSuggestion();
        });
    }

    // ---------------------------------------------------------------- title bar

    /// <summary>The tool the title bar is showing, or <see langword="null"/> on Home and Settings.</summary>
    public ToolViewModelBase? ActiveTool => _chrome.Active;

    public bool HasActiveTool => _chrome.Active is not null;

    public string ActiveToolName => _chrome.Active?.Title ?? string.Empty;

    /// <summary>The one-line description, shown beside the name when the window is wide enough.</summary>
    public string ActiveToolSubtitle => _chrome.Active?.Subtitle ?? string.Empty;

    /// <summary>
    /// Read from the favourites list, not from the tool. The tool is published to the title bar
    /// before it activates, and it only learns its own favourite state during activation — so
    /// asking the tool made a pinned tool open with an unpinned star.
    /// </summary>
    public bool ActiveToolIsFavorite => _chrome.Active is { } tool && _favorites.IsFavorite(tool.ToolId);

    private void RaiseActiveToolChanged()
    {
        OnPropertyChanged(nameof(ActiveTool));
        OnPropertyChanged(nameof(HasActiveTool));
        OnPropertyChanged(nameof(ActiveToolName));
        OnPropertyChanged(nameof(ActiveToolSubtitle));
        OnPropertyChanged(nameof(ActiveToolIsFavorite));
    }

    [RelayCommand]
    private async Task ToggleActiveFavoriteAsync()
    {
        if (_chrome.Active is { } tool)
        {
            await tool.ToggleFavoriteCommand.ExecuteAsync(null);
            RaiseActiveToolChanged();
        }
    }

    [RelayCommand]
    private async Task ResetActiveToolAsync()
    {
        if (_chrome.Active is { } tool)
        {
            await tool.ResetCommand.ExecuteAsync(null);
        }
    }

    public ToolCatalog Catalog => _catalog;

    public ObservableCollection<ToolDescriptor> Favorites { get; } = [];

    public ObservableCollection<SuggestionItem> Suggestions { get; } = [];

    [ObservableProperty]
    public partial bool IsPaletteOpen { get; set; }

    [ObservableProperty]
    public partial string PaletteQuery { get; set; }

    [ObservableProperty]
    public partial string? SuggestionMessage { get; set; }

    [ObservableProperty]
    public partial bool IsSuggestionOpen { get; set; }

    public ObservableCollection<ToolSearchResult> PaletteResults { get; } = [];

    public bool HasFavorites => Favorites.Count > 0;

    partial void OnPaletteQueryChanged(string value)
    {
        PaletteResults.Clear();

        // An empty palette leads with recents, newest first — usually the tool you want — and
        // then lists everything else, so every tool is still one keystroke away.
        var items = string.IsNullOrWhiteSpace(value)
            ? RecentTools().Concat(_catalog.All.Where(t => !_recents.Ids.Contains(t.Id, StringComparer.OrdinalIgnoreCase)))
                .Select(t => new ToolSearchResult(t, 0))
            : _catalog.Search(value, limit: 30);

        foreach (var item in items)
        {
            PaletteResults.Add(item);
        }
    }

    public async Task InitializeAsync()
    {
        await _favorites.LoadAsync();
        await _recents.LoadAsync();
        RefreshFavorites();
    }

    private void RefreshFavorites()
    {
        Favorites.Clear();

        foreach (var id in _favorites.Ids)
        {
            if (_catalog.ById(id) is { } tool)
            {
                Favorites.Add(tool);
            }
        }

        OnPropertyChanged(nameof(HasFavorites));
    }

    public IReadOnlyList<ToolDescriptor> RecentTools() =>
        _recents.Ids
            .Select(_catalog.ById)
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();

    [RelayCommand]
    public void OpenPalette()
    {
        PaletteQuery = string.Empty;
        OnPaletteQueryChanged(string.Empty);
        IsPaletteOpen = true;
    }

    [RelayCommand]
    public void ClosePalette() => IsPaletteOpen = false;

    [RelayCommand]
    public void ToggleTheme() => _theme.ToggleTheme();

    [RelayCommand]
    public void DismissSuggestion()
    {
        IsSuggestionOpen = false;
        Suggestions.Clear();
    }

    /// <summary>
    /// Looks at the clipboard and offers matching tools (FR-S07). The same clipboard content
    /// is only offered once, so returning to the app does not nag repeatedly.
    /// </summary>
    public async Task DetectClipboardAsync()
    {
        if (!_settings.SmartDetectEnabled)
        {
            IsSuggestionOpen = false;
            return;
        }

        var text = await _clipboard.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (string.Equals(text, _lastDetectedClipboard, StringComparison.Ordinal))
        {
            return;
        }

        _lastDetectedClipboard = text;

        var hits = SmartDetector.Detect(text);
        if (hits.Count == 0)
        {
            IsSuggestionOpen = false;
            Suggestions.Clear();
            return;
        }

        // Never suggest the tool you are already in: if you copied a URL while the API Builder is
        // open, you are plainly already using it, and a banner pointing back at it is just noise.
        var activeToolId = _chrome.Active?.ToolId;

        Suggestions.Clear();
        foreach (var hit in hits)
        {
            if (string.Equals(hit.ToolId, activeToolId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_catalog.ById(hit.ToolId) is { } tool)
            {
                Suggestions.Add(new SuggestionItem(tool.Id, tool.Name, tool.Glyph, tool.GlyphFont));
            }
        }

        if (Suggestions.Count == 0)
        {
            IsSuggestionOpen = false;
            return;
        }

        SuggestionMessage = $"The clipboard looks like {hits[0].Label}.";
        IsSuggestionOpen = true;
    }

    /// <summary>
    /// Drops the open suggestion once you navigate into the tool it points at, and closes the
    /// banner if that leaves nothing — the suggestion has served its purpose by then.
    /// </summary>
    private void PruneActiveToolSuggestion()
    {
        if (!IsSuggestionOpen || _chrome.Active?.ToolId is not { } activeToolId)
        {
            return;
        }

        for (var i = Suggestions.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Suggestions[i].ToolId, activeToolId, StringComparison.OrdinalIgnoreCase))
            {
                Suggestions.RemoveAt(i);
            }
        }

        if (Suggestions.Count == 0)
        {
            IsSuggestionOpen = false;
        }
    }

    /// <summary>Human-readable summary of the app and its runtime, shown in Settings and About.</summary>
    public static string AboutText =>
        $"ForgeKitRk 1.0.0\n" +
        $".NET {Environment.Version}\n" +
        $"Windows App SDK 2.4.0\n" +
        $"{Environment.OSVersion.VersionString}";

    public ElementTheme CurrentTheme => _theme.CurrentTheme;
}
