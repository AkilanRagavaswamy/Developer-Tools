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
    /// <summary>
    /// Keys earlier builds wrote with the user's data in them. Stripped from anything read off
    /// disk, so data saved before inputs stopped being persisted does not come back.
    /// </summary>
    private static readonly HashSet<string> LegacyDataKeys = new(StringComparer.Ordinal)
    {
        "input", "left", "right", "path",
        "name", "method", "url", "bodyKind", "body", "authKind", "username", "apiKeyName",
    };

    public Dictionary<string, string> Values { get; set; } = [];

    /// <summary>
    /// The keys holding what the user typed or pasted, as opposed to option choices. They are
    /// kept for the session, so leaving a tool and coming back finds it as it was, but they
    /// are never written to disk: tool data is gone when DevTools closes.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public HashSet<string> DataKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>Records a value that is the user's data rather than an option.</summary>
    public void SetData(string key, string? value)
    {
        Set(key, value);
        DataKeys.Add(key);
    }

    /// <summary>A copy holding only the option values — what is safe to write to disk.</summary>
    public ToolState OptionsOnly()
    {
        var options = new ToolState();

        foreach (var (key, value) in Values)
        {
            if (!DataKeys.Contains(key) && !LegacyDataKeys.Contains(key))
            {
                options.Values[key] = value;
            }
        }

        return options;
    }

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

        var stored = await JsonStore.LoadAsync<ToolState>(FileFor(toolId)).ConfigureAwait(false);
        var loaded = stored?.OptionsOnly() ?? new ToolState();
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
                await JsonStore.SaveAsync(FileFor(toolId), state.OptionsOnly()).ConfigureAwait(false);
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

    /// <summary>
    /// Called as the app closes: writes every tool's options and drops its data.
    /// </summary>
    /// <remarks>
    /// Every cached tool is written, not only those with a write pending, so a tool whose last
    /// save predates this build — and so still holds data on disk — is scrubbed too. The
    /// in-memory data is cleared as well; nothing the user entered outlives the session.
    /// </remarks>
    public async Task FlushAsync()
    {
        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(key, out var cts))
            {
                await cts.CancelAsync().ConfigureAwait(false);
                cts.Dispose();
            }
        }

        foreach (var (key, state) in _cache.ToList())
        {
            var options = state.OptionsOnly();
            _cache[key] = options;

            if (settings.PersistToolState)
            {
                await JsonStore.SaveAsync(FileFor(key), options).ConfigureAwait(false);
            }
        }

        await ScrubUnopenedAsync().ConfigureAwait(false);
    }

    /// <summary>Strips data from the state files of tools that were not opened this session.</summary>
    private async Task ScrubUnopenedAsync()
    {
        string[] files;

        try
        {
            var folder = JsonStore.PathFor(Directory);
            files = System.IO.Directory.Exists(folder)
                ? System.IO.Directory.GetFiles(folder, "*.json")
                : [];
        }
        catch (Exception)
        {
            return;
        }

        var opened = _cache.Keys.Select(Sanitize).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (opened.Contains(name))
            {
                continue;
            }

            var relative = $"{Directory}/{name}.json";
            var stored = await JsonStore.LoadAsync<ToolState>(relative).ConfigureAwait(false);
            var options = stored?.OptionsOnly();

            if (stored is not null && options!.Values.Count != stored.Values.Count)
            {
                await JsonStore.SaveAsync(relative, options).ConfigureAwait(false);
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
