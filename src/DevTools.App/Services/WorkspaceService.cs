using DevTools.Core;
using DevTools.Http.Workspace;
using Windows.Storage;

namespace DevTools.App.Services;

/// <summary>Owns the API Builder workspace and its debounced, atomic persistence (FR-A20).</summary>
public interface IWorkspaceService
{
    ApiWorkspace Current { get; }

    event EventHandler? Changed;

    Task LoadAsync();

    /// <summary>Replaces the workspace and queues a write.</summary>
    void Update(ApiWorkspace workspace);

    Task FlushAsync();

    string WorkspaceFolder { get; }
}

/// <summary>
/// The workspace, held in memory and written back on a debounce.
/// </summary>
/// <remarks>
/// Editing a request should not hit the disk on every keystroke, but a crash should not lose
/// a collection either. A 750 ms debounce plus a flush on deactivate and on app close is the
/// balance DevKit's per-tool state used, and it holds here for the same reason.
/// </remarks>
public sealed class WorkspaceService : IWorkspaceService, IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(750);

    private readonly WorkspaceStore _store;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _pending;
    private bool _dirty;

    public WorkspaceService()
    {
        WorkspaceFolder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "workspace");
        _store = new WorkspaceStore(WorkspaceFolder);
        Current = ApiWorkspace.Empty;
    }

    public ApiWorkspace Current { get; private set; }

    public string WorkspaceFolder { get; }

    public event EventHandler? Changed;

    /// <summary>Set when the stored file could not be read, so the UI can say so once.</summary>
    public string? LoadWarning { get; private set; }

    public Task LoadAsync()
    {
        var result = _store.Load();

        if (result.IsSuccess)
        {
            Current = result.Value!;
            LoadWarning = result.Warning;
        }
        else
        {
            // A refusal (a file from a newer build) must not be overwritten by an empty one.
            Current = ApiWorkspace.Empty;
            LoadWarning = result.ErrorMessage;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void Update(ApiWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        Current = workspace;
        Changed?.Invoke(this, EventArgs.Empty);
        Schedule();
    }

    private void Schedule()
    {
        CancellationTokenSource cts;

        lock (_gate)
        {
            _dirty = true;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = cts = new CancellationTokenSource();
        }

        _ = WriteAfterDelayAsync(cts.Token);
    }

    private async Task WriteAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceWindow, token);
            Write();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later edit; that write will cover this one too.
        }
    }

    public Task FlushAsync()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }

        Write();
        return Task.CompletedTask;
    }

    private void Write()
    {
        ApiWorkspace snapshot;

        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            snapshot = Current;
        }

        _ = _store.Save(snapshot);
    }

    public string DescribeSize()
    {
        var path = _store.WorkspacePath;
        return File.Exists(path) ? Limits.Describe(new FileInfo(path).Length) : "empty";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }
}
