namespace DevTools.App.Services;

public interface IFavoritesService
{
    event EventHandler? Changed;

    IReadOnlyList<string> Ids { get; }

    bool IsFavorite(string toolId);

    Task ToggleAsync(string toolId);

    Task SetAsync(string toolId, bool isFavorite);

    Task ClearAsync();

    Task LoadAsync();
}

/// <summary>Pinned tools, kept in insertion order so the nav pane is stable.</summary>
public sealed class FavoritesService : IFavoritesService
{
    private const string FileName = "favorites.json";

    private List<string> _ids = [];

    public event EventHandler? Changed;

    public IReadOnlyList<string> Ids => _ids;

    public async Task LoadAsync()
    {
        var loaded = await JsonStore.LoadAsync<List<string>>(FileName).ConfigureAwait(false);
        _ids = loaded ?? [];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsFavorite(string toolId) =>
        _ids.Contains(toolId, StringComparer.OrdinalIgnoreCase);

    public Task ToggleAsync(string toolId) => SetAsync(toolId, !IsFavorite(toolId));

    public async Task SetAsync(string toolId, bool isFavorite)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return;
        }

        var changed = false;

        if (isFavorite && !IsFavorite(toolId))
        {
            _ids.Add(toolId);
            changed = true;
        }
        else if (!isFavorite)
        {
            changed = _ids.RemoveAll(id => string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            await JsonStore.SaveAsync(FileName, _ids).ConfigureAwait(false);
        }
    }

    public async Task ClearAsync()
    {
        _ids.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        await JsonStore.SaveAsync(FileName, _ids).ConfigureAwait(false);
    }
}

public interface IRecentToolsService
{
    event EventHandler? Changed;

    IReadOnlyList<string> Ids { get; }

    Task TouchAsync(string toolId);

    Task ClearAsync();

    Task LoadAsync();
}

/// <summary>The most recently opened tools, newest first, de-duplicated and capped.</summary>
public sealed class RecentToolsService : IRecentToolsService
{
    private const string FileName = "recents.json";
    public const int MaxEntries = 12;

    private List<string> _ids = [];

    public event EventHandler? Changed;

    public IReadOnlyList<string> Ids => _ids;

    public async Task LoadAsync()
    {
        var loaded = await JsonStore.LoadAsync<List<string>>(FileName).ConfigureAwait(false);
        _ids = loaded ?? [];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task TouchAsync(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return;
        }

        _ids.RemoveAll(id => string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase));
        _ids.Insert(0, toolId);

        if (_ids.Count > MaxEntries)
        {
            _ids.RemoveRange(MaxEntries, _ids.Count - MaxEntries);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        await JsonStore.SaveAsync(FileName, _ids).ConfigureAwait(false);
    }

    public async Task ClearAsync()
    {
        _ids.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        await JsonStore.SaveAsync(FileName, _ids).ConfigureAwait(false);
    }
}
