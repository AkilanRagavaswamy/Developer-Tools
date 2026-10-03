using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Models;
using DevTools.App.Services;

namespace DevTools.App.ViewModels;

/// <summary>
/// A tool as a dashboard card: the tool itself plus whether it is pinned, which the card's
/// star follows as favourites change.
/// </summary>
public sealed partial class ToolCard : ObservableObject
{
    public ToolCard(ToolDescriptor tool, bool isFavorite)
    {
        Tool = tool;
        IsFavorite = isFavorite;
    }

    public ToolDescriptor Tool { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteGlyph), nameof(FavoriteLabel))]
    public partial bool IsFavorite { get; set; }

    public string FavoriteGlyph => IsFavorite ? "" : "";

    public string FavoriteLabel => IsFavorite ? "Remove from favorites" : "Add to favorites";
}

/// <summary>One category section on the dashboard.</summary>
public sealed class ToolGroup(ToolCategory category, IEnumerable<ToolCard> tools)
{
    public ToolCategory Category { get; } = category;

    public string Name { get; } = ToolCategoryInfo.DisplayName(category);

    public string Glyph { get; } = ToolCategoryInfo.Glyph(category);

    public IReadOnlyList<ToolCard> Tools { get; } = [.. tools];

    public string CountLabel => Tools.Count == 1 ? "1 tool" : $"{Tools.Count} tools";
}

/// <summary>Backs the dashboard: every tool as a card, plus the pinned ones (FR-S02).</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly ToolCatalog _catalog;
    private readonly IFavoritesService _favorites;
    private readonly INavigationService _navigation;

    public HomeViewModel(
        ToolCatalog catalog,
        IFavoritesService favorites,
        INavigationService navigation)
    {
        _catalog = catalog;
        _favorites = favorites;
        _navigation = navigation;

        // One card per tool, shared by its category and the Favorites row, so pinning from
        // either place updates the star in both.
        _cards = _catalog.All.ToDictionary(
            t => t.Id,
            t => new ToolCard(t, _favorites.IsFavorite(t.Id)),
            StringComparer.OrdinalIgnoreCase);

        Groups = [.. ToolCategoryInfo.All
            .Where(c => _catalog.ByCategory.ContainsKey(c))
            .Select(c => new ToolGroup(c, _catalog.ByCategory[c].Select(t => _cards[t.Id])))];

        // Through the dispatcher: the favourites load finishes on a thread-pool thread, and
        // Refresh touches collections the UI is bound to (see UiDispatcher).
        _favorites.Changed += (_, _) => UiDispatcher.Run(Refresh);

        Refresh();
    }

    private readonly Dictionary<string, ToolCard> _cards;

    public IReadOnlyList<ToolGroup> Groups { get; }

    public ObservableCollection<ToolCard> FavoriteTools { get; } = [];

    public int TotalTools => _catalog.All.Count;

    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => "Working late",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };

    public bool HasFavorites => FavoriteTools.Count > 0;

    public void Refresh()
    {
        foreach (var card in _cards.Values)
        {
            card.IsFavorite = _favorites.IsFavorite(card.Tool.Id);
        }

        FavoriteTools.Clear();
        foreach (var id in _favorites.Ids)
        {
            if (_cards.TryGetValue(id, out var card))
            {
                FavoriteTools.Add(card);
            }
        }

        OnPropertyChanged(nameof(HasFavorites));
    }

    [RelayCommand]
    private void OpenTool(string? toolId)
    {
        if (!string.IsNullOrEmpty(toolId))
        {
            _navigation.NavigateToTool(toolId);
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(string? toolId)
    {
        if (!string.IsNullOrEmpty(toolId))
        {
            await _favorites.ToggleAsync(toolId);
        }
    }

    public bool IsFavorite(string toolId) => _favorites.IsFavorite(toolId);
}
