using Microsoft.UI.Xaml;
using Windows.Storage;

namespace DevTools.App.Services;

/// <summary>The window material behind the app content.</summary>
public enum BackdropKind
{
    Mica,
    MicaAlt,
    Acrylic,
    None,
}

/// <summary>Raised when a setting changes so open pages can react without a restart.</summary>
public sealed class SettingChangedEventArgs(string key) : EventArgs
{
    public string Key { get; } = key;
}

public interface ISettingsService
{
    event EventHandler<SettingChangedEventArgs>? SettingChanged;

    ElementTheme Theme { get; set; }

    BackdropKind Backdrop { get; set; }

    double EditorFontSize { get; set; }

    bool EditorWordWrap { get; set; }

    bool EditorLineNumbers { get; set; }
    /// <summary>Colour the read-only output panes (FR-T11).</summary>
    bool EditorSyntaxColouring { get; set; }

    bool SmartDetectEnabled { get; set; }

    bool PersistToolState { get; set; }

    /// <summary>Whether JSON Diff offers the JSON Patch and Unified text result views.</summary>
    bool JsonDiffTextViews { get; set; }

    string WindowPlacement { get; set; }

    double SplitterRatio { get; set; }

    /// <summary>Whether the navigation pane shows tool names. It starts compact (FR-S02).</summary>
    bool NavigationPaneOpen { get; set; }

    /// <summary>Days a note stays in Scratchpad's Trash, and how long its history is kept (1–60).</summary>
    int ScratchpadRetentionDays { get; set; }

    /// <summary>Whether Scratchpad opens on a fresh note instead of the last one.</summary>
    bool ScratchpadStartWithNewNote { get; set; }

    void Reset();
}

/// <summary>
/// Settings live in the packaged app's local settings container, which is per-user and
/// survives updates. Every read tolerates a missing or wrongly typed value by falling
/// back to the default, so a corrupt container can never stop the app starting.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private const string KeyTheme = "Theme";
    private const string KeyBackdrop = "Backdrop";
    private const string KeyFontSize = "EditorFontSize";
    private const string KeyWordWrap = "EditorWordWrap";
    private const string KeyLineNumbers = "EditorLineNumbers";
    private const string KeySyntaxColouring = "EditorSyntaxColouring";
    private const string KeySmartDetect = "SmartDetectEnabled";
    private const string KeyPersistState = "PersistToolState";
    private const string KeyDiffTextViews = "JsonDiffTextViews";
    private const string KeyWindowPlacement = "WindowPlacement";
    private const string KeySplitterRatio = "SplitterRatio";
    private const string KeyPaneOpen = "NavigationPaneOpen";
    private const string KeyScratchDays = "ScratchpadRetentionDays";
    private const string KeyScratchStartNew = "ScratchpadStartWithNewNote";

    public const double MinFontSize = 10;
    public const double MaxFontSize = 28;
    public const double DefaultFontSize = 14;

    private readonly ApplicationDataContainer? _container;

    public SettingsService()
    {
        try
        {
            _container = ApplicationData.Current.LocalSettings;
        }
        catch (Exception)
        {
            // Unpackaged or sandboxed run: fall back to in-memory defaults rather than dying.
            _container = null;
        }
    }

    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    private readonly Dictionary<string, object> _fallback = [];

    public ElementTheme Theme
    {
        get => Read(KeyTheme, ElementTheme.Default);
        set => Write(KeyTheme, value);
    }

    public BackdropKind Backdrop
    {
        get => Read(KeyBackdrop, BackdropKind.Mica);
        set => Write(KeyBackdrop, value);
    }

    public double EditorFontSize
    {
        get => Math.Clamp(Read(KeyFontSize, DefaultFontSize), MinFontSize, MaxFontSize);
        set => Write(KeyFontSize, Math.Clamp(value, MinFontSize, MaxFontSize));
    }

    public bool EditorWordWrap
    {
        get => Read(KeyWordWrap, true);
        set => Write(KeyWordWrap, value);
    }

    public bool EditorLineNumbers
    {
        get => Read(KeyLineNumbers, true);
        set => Write(KeyLineNumbers, value);
    }

    public bool EditorSyntaxColouring
    {
        get => Read(KeySyntaxColouring, true);
        set => Write(KeySyntaxColouring, value);
    }

    public bool SmartDetectEnabled
    {
        get => Read(KeySmartDetect, true);
        set => Write(KeySmartDetect, value);
    }

    public bool PersistToolState
    {
        get => Read(KeyPersistState, true);
        set => Write(KeyPersistState, value);
    }

    public bool JsonDiffTextViews
    {
        get => Read(KeyDiffTextViews, false);
        set => Write(KeyDiffTextViews, value);
    }

    public string WindowPlacement
    {
        get => Read(KeyWindowPlacement, string.Empty);
        set => Write(KeyWindowPlacement, value);
    }

    public double SplitterRatio
    {
        get => Math.Clamp(Read(KeySplitterRatio, 0.5), 0.15, 0.85);
        set => Write(KeySplitterRatio, Math.Clamp(value, 0.15, 0.85));
    }

    /// <summary>
    /// Defaults to false: the pane opens compact so the six glyphs are there without the
    /// 272 px of names, and the width goes to the tool instead. Reopening it sticks.
    /// </summary>
    public bool NavigationPaneOpen
    {
        get => Read(KeyPaneOpen, false);
        set => Write(KeyPaneOpen, value);
    }

    public int ScratchpadRetentionDays
    {
        get => DevTools.Core.Scratch.ScratchRetention.ClampDays(Read(KeyScratchDays, DevTools.Core.Scratch.ScratchRetention.DefaultDays));
        set => Write(KeyScratchDays, DevTools.Core.Scratch.ScratchRetention.ClampDays(value));
    }

    public bool ScratchpadStartWithNewNote
    {
        get => Read(KeyScratchStartNew, false);
        set => Write(KeyScratchStartNew, value);
    }

    public void Reset()
    {
        try
        {
            _container?.Values.Clear();
        }
        catch (Exception)
        {
            // Nothing useful to do; the in-memory fallback below still resets.
        }

        _fallback.Clear();
        SettingChanged?.Invoke(this, new SettingChangedEventArgs(string.Empty));
    }

    private T Read<T>(string key, T fallback)
    {
        try
        {
            object? raw = null;
            if (_container is not null && _container.Values.TryGetValue(key, out var stored))
            {
                raw = stored;
            }
            else if (_fallback.TryGetValue(key, out var memory))
            {
                raw = memory;
            }

            if (raw is null)
            {
                return fallback;
            }

            if (typeof(T).IsEnum)
            {
                return raw is int i && Enum.IsDefined(typeof(T), i)
                    ? (T)Enum.ToObject(typeof(T), i)
                    : fallback;
            }

            return raw is T typed ? typed : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private void Write<T>(string key, T value)
    {
        object boxed = typeof(T).IsEnum ? Convert.ToInt32(value, provider: null) : value!;

        try
        {
            if (_container is not null)
            {
                _container.Values[key] = boxed;
            }
            else
            {
                _fallback[key] = boxed;
            }
        }
        catch (Exception)
        {
            _fallback[key] = boxed;
        }

        SettingChanged?.Invoke(this, new SettingChangedEventArgs(key));
    }
}
