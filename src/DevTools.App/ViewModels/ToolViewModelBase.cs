using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Models;
using DevTools.App.Services;
using DevTools.Core;

namespace DevTools.App.ViewModels;

/// <summary>Severity of the banner a tool shows, kept free of UI types.</summary>
public enum ToolMessageSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>
/// What every tool shares, whatever shape it has: the message banner, the busy flag, run
/// cancellation that a stale result cannot survive, favourites, reset, and per-tool state.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately smaller than DevKit's equivalent, which also owned a single input
/// string, a single output string and a 250 ms live debounce. That shape fits a one-box
/// utility and fits almost nothing here: JSON Diff has two inputs, SVG to XAML has a rendered
/// preview, and both API tools run an explicit, long-lived, cancellable job against the
/// network rather than a transform of a text box.
/// </para>
/// <para>
/// The three shapes live in <see cref="TextToolViewModelBase"/>,
/// <see cref="DualTextToolViewModelBase"/> and <see cref="JobToolViewModelBase"/>.
/// </para>
/// </remarks>
public abstract partial class ToolViewModelBase : ObservableObject
{
    private CancellationTokenSource? _runCts;
    private bool _stateLoaded;

    protected ToolViewModelBase(ToolServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;

        MessageSeverity = ToolMessageSeverity.Informational;
    }

    protected ToolServices Services { get; }

    /// <summary>The catalog id of this tool. Used for state, favourites and recents.</summary>
    public abstract string ToolId { get; }

    public ToolDescriptor? Descriptor => Services.Catalog.ById(ToolId);

    public string Title => Descriptor?.Name ?? ToolId;

    public string Subtitle => Descriptor?.Description ?? string.Empty;

    public string Glyph => Descriptor?.Glyph ?? "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial ToolMessageSeverity MessageSeverity { get; set; }

    [ObservableProperty]
    public partial bool IsFavorite { get; set; }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Whether the page shows an explicit action button instead of running live.</summary>
    public virtual bool ShowRunButton => false;

    /// <summary>Label for that button — "Run", "Send", "Start".</summary>
    public virtual string RunLabel => "Run";

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    /// <summary>Set while state is being restored, so a restore does not look like an edit.</summary>
    protected bool IsRestoring { get; private set; }

    protected bool IsStateLoaded => _stateLoaded;

    // ---------------------------------------------------------------- execution

    /// <summary>The token of the run currently in flight, if any.</summary>
    protected CancellationToken CurrentToken => _runCts?.Token ?? CancellationToken.None;

    /// <summary>Starts a new run, cancelling any already in flight, and returns its token.</summary>
    protected CancellationToken BeginRun()
    {
        CancelRun();
        var cts = new CancellationTokenSource();
        _runCts = cts;
        return cts.Token;
    }

    protected void CancelRun()
    {
        var cts = _runCts;
        _runCts = null;

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }

        cts.Dispose();
    }

    /// <summary>True when a run is in flight, which is what a Stop button binds to.</summary>
    public bool CanCancel => _runCts is not null;

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool and resumes on the UI thread, throwing
    /// if this run has been superseded — which is what stops a slow, stale result from
    /// overwriting a newer one (edge case 12).
    /// </summary>
    protected static async Task<T> ComputeAsync<T>(Func<T> work, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(work);

        var result = await Task.Run(work, token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>The tool's primary action: Run, Send or Start. Bound to Ctrl+Enter.</summary>
    [RelayCommand]
    public virtual Task RunAsync() => Task.CompletedTask;

    /// <summary>Clears the tool's inputs and outputs. Bound to Ctrl+L.</summary>
    [RelayCommand]
    public virtual void Clear() => ClearMessage();

    /// <summary>Saves the tool's output. Bound to Ctrl+S.</summary>
    [RelayCommand]
    public virtual Task SaveOutputAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------- messages

    protected void ClearMessage()
    {
        Message = null;
        MessageSeverity = ToolMessageSeverity.Informational;
    }

    protected void SetMessage(string text, ToolMessageSeverity severity)
    {
        Message = text;
        MessageSeverity = severity;
    }

    protected void SetInfo(string text) => SetMessage(text, ToolMessageSeverity.Informational);

    protected void SetSuccess(string text) => SetMessage(text, ToolMessageSeverity.Success);

    protected void SetWarning(string text) => SetMessage(text, ToolMessageSeverity.Warning);

    protected void SetError(string text) => SetMessage(text, ToolMessageSeverity.Error);

    protected void SetError(ToolError error) => SetError(error.ToDisplayString());

    /// <summary>Reports a list of warnings as one banner line, or clears it when there are none.</summary>
    protected void ReportWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            ClearMessage();
            return;
        }

        SetWarning(warnings.Count == 1
            ? warnings[0]
            : $"{warnings[0]} (and {warnings.Count - 1} more)");
    }

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        await Services.Favorites.ToggleAsync(ToolId);
        IsFavorite = Services.Favorites.IsFavorite(ToolId);
    }

    /// <summary>Returns the tool to its defaults and forgets its saved state (FR-T01).</summary>
    [RelayCommand]
    private async Task ResetAsync()
    {
        IsRestoring = true;
        try
        {
            Clear();
            ResetOptions();
        }
        finally
        {
            IsRestoring = false;
        }

        await Services.State.ClearAsync(ToolId);
        AfterReset();
    }

    /// <summary>Overridden by tools that have options to restore to their defaults.</summary>
    protected virtual void ResetOptions()
    {
    }

    /// <summary>Hook for re-running after a reset, where that makes sense.</summary>
    protected virtual void AfterReset()
    {
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Called by the page when it is navigated to.</summary>
    public async Task ActivateAsync()
    {
        IsFavorite = Services.Favorites.IsFavorite(ToolId);

        if (!_stateLoaded)
        {
            var state = await Services.State.GetAsync(ToolId);

            IsRestoring = true;
            try
            {
                RestoreState(state);
            }
            finally
            {
                IsRestoring = false;
                // Set after restoring, so a restore can never be mistaken for a user edit and
                // write an empty state back over what was just read.
                _stateLoaded = true;
            }
        }

        await OnActivatedAsync();
    }

    /// <summary>Hook for tools that need to do work when shown.</summary>
    protected virtual Task OnActivatedAsync() => Task.CompletedTask;

    /// <summary>Called by the page when it is navigated away from.</summary>
    public virtual void Deactivate()
    {
        CancelRun();
        PersistState();
    }

    /// <summary>Saves the tool's state. Safe to call often — the write is debounced.</summary>
    protected void PersistState()
    {
        if (IsRestoring || !_stateLoaded)
        {
            return;
        }

        var state = new ToolState();
        CaptureState(state);
        Services.State.Schedule(ToolId, state);
    }

    /// <summary>Overridden by tools to add their values to the saved state.</summary>
    protected virtual void CaptureState(ToolState state)
    {
    }

    /// <summary>Overridden by tools to read their values back from saved state.</summary>
    protected virtual void RestoreState(ToolState state)
    {
    }

    /// <summary>Call from an option setter: re-runs the tool and saves the new option value.</summary>
    protected virtual void OnOptionChanged() => PersistState();
}
