using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Models;
using DevTools.App.Services;

namespace DevTools.App.ViewModels;

/// <summary>One category section on the dashboard.</summary>
public sealed class ToolGroup(ToolCategory category, IEnumerable<ToolDescriptor> tools)
{
    public ToolCategory Category { get; } = category;

    public string Name { get; } = ToolCategoryInfo.DisplayName(category);

    public string Glyph { get; } = ToolCategoryInfo.Glyph(category);

    public IReadOnlyList<ToolDescriptor> Tools { get; } = [.. tools];

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

        Groups = [.. ToolCategoryInfo.All
            .Where(c => _catalog.ByCategory.ContainsKey(c))
            .Select(c => new ToolGroup(c, _catalog.ByCategory[c]))];

        // Through the dispatcher: the favourites load finishes on a thread-pool thread, and
        // Refresh touches collections the UI is bound to (see UiDispatcher).
        _favorites.Changed += (_, _) => UiDispatcher.Run(Refresh);

        Refresh();
    }

    public IReadOnlyList<ToolGroup> Groups { get; }

    public ObservableCollection<ToolDescriptor> FavoriteTools { get; } = [];

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
        FavoriteTools.Clear();
        foreach (var id in _favorites.Ids)
        {
            if (_catalog.ById(id) is { } tool)
            {
                FavoriteTools.Add(tool);
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
