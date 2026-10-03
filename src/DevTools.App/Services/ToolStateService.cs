using System.Collections.Concurrent;
using System.Globalization;

namespace DevTools.App.Services;

/// <summary>
/// A tool's remembered input and option selections. Values are stored as strings so the
/// shape can evolve without breaking a previously saved file — an unknown or unparseable
/// entry simply falls back to the caller's default.
/// </summary>
public sealed class ToolState
{
    public Dictionary<string, string> Values { get; set; } = [];

    public string GetString(string key, string fallback = "") =>
        Values.TryGetValue(key, out var value) ? value : fallback;

    public bool GetBool(string key, bool fallback = false) =>
        Values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    public int GetInt(string key, int fallback = 0) =>
        Values.TryGetValue(key, out var value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    public double GetDouble(string key, double fallback = 0) =>
        Values.TryGetValue(key, out var value) &&
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    public T GetEnum<T>(string key, T fallback)
        where T : struct, Enum =>
        Values.TryGetValue(key, out var value) && Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;

    public void Set(string key, string? value) => Values[key] = value ?? string.Empty;

    public void Set(string key, bool value) => Values[key] = value ? "true" : "false";

    public void Set(string key, int value) => Values[key] = value.ToString(CultureInfo.InvariantCulture);

    public void Set(string key, double value) => Values[key] = value.ToString("R", CultureInfo.InvariantCulture);

    public void Set<T>(string key, T value)
        where T : struct, Enum => Values[key] = value.ToString();
}

public interface IToolStateService
{
    Task<ToolState> GetAsync(string toolId);

    /// <summary>Queues a debounced write; repeated calls within the window coalesce.</summary>
    void Schedule(string toolId, ToolState state);

    Task FlushAsync();

    Task ClearAsync(string toolId);

    Task ClearAllAsync();
}

/// <summary>
/// Per-tool state files under <c>state\&lt;tool-id&gt;.json</c>, written on a 500 ms debounce so
/// typing does not hit the disk on every keystroke (FR-S12).
/// </summary>
public sealed class ToolStateService(ISettingsService settings) : IToolStateService
{
    private const string Directory = "state";
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<string, ToolState> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);

    private static string FileFor(string toolId) => $"{Directory}/{Sanitize(toolId)}.json";

    public async Task<ToolState> GetAsync(string toolId)
    {
        if (!settings.PersistToolState)
        {
            return new ToolState();
        }

        if (_cache.TryGetValue(toolId, out var cached))
        {
            return cached;
        }

        var loaded = await JsonStore.LoadAsync<ToolState>(FileFor(toolId)).ConfigureAwait(false)
                     ?? new ToolState();
        _cache[toolId] = loaded;
        return loaded;
    }

    public void Schedule(string toolId, ToolState state)
    {
        if (!settings.PersistToolState)
        {
            return;
        }

        _cache[toolId] = state;

        var cts = new CancellationTokenSource();
        if (_pending.TryRemove(toolId, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        _pending[toolId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceWindow, cts.Token).ConfigureAwait(false);
                await JsonStore.SaveAsync(FileFor(toolId), state).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer edit; the newer write will land instead.
            }
            finally
            {
                if (_pending.TryGetValue(toolId, out var current) && current == cts)
                {
                    _pending.TryRemove(toolId, out _);
                }

                cts.Dispose();
            }
        });
    }

    /// <summary>Writes every pending change immediately — called as the app closes.</summary>
    public async Task FlushAsync()
    {
        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(key, out var cts))
            {
                await cts.CancelAsync().ConfigureAwait(false);
                cts.Dispose();
            }

            if (_cache.TryGetValue(key, out var state))
            {
                await JsonStore.SaveAsync(FileFor(key), state).ConfigureAwait(false);
            }
        }
    }

    public Task ClearAsync(string toolId)
    {
        _cache.TryRemove(toolId, out _);
        JsonStore.Delete(FileFor(toolId));
        return Task.CompletedTask;
    }

    public Task ClearAllAsync()
    {
        _cache.Clear();
        JsonStore.DeleteDirectory(Directory);
        return Task.CompletedTask;
    }

    /// <summary>Tool ids are ours, but never trust one straight into a path.</summary>
    private static string Sanitize(string toolId)
    {
        Span<char> buffer = stackalloc char[toolId.Length];
        for (var i = 0; i < toolId.Length; i++)
        {
            var c = toolId[i];
            buffer[i] = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }

        return new string(buffer);
    }
}
