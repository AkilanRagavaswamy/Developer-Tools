using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;

namespace DevTools.App.ViewModels;

/// <summary>
/// A tool that runs an explicit, long-lived, cancellable job rather than a live transform.
/// </summary>
/// <remarks>
/// <para>
/// Used by API Builder. Two things separate these from the text tools, and
/// both are the reason they do not share a base class with them.
/// </para>
/// <para>
/// First, there is no debounce and there never should be: a tool that sends network requests
/// must not fire because someone typed a character. The job starts when the user says so.
/// Second, the job reports progress and can be stopped part-way, and what it produced up to
/// that point is kept rather than discarded (FR-A09).
/// </para>
/// </remarks>
public abstract partial class JobToolViewModelBase : ToolViewModelBase
{
    protected JobToolViewModelBase(ToolServices services)
        : base(services)
    {
        StatusText = string.Empty;
    }

    /// <summary>0–100 while a job runs. Negative means indeterminate.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>True once a job has produced something worth looking at.</summary>
    [ObservableProperty]
    public partial bool HasResult { get; set; }

    public override bool ShowRunButton => true;

    public override string RunLabel => "Run";

    partial void OnIsRunningChanged(bool value)
    {
        IsBusy = value;
        OnPropertyChanged(nameof(CanCancel));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Starts the job. Bound to the primary button and to Ctrl+Enter.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var token = BeginRun();

        IsRunning = true;
        Progress = 0;

        try
        {
            await RunJobAsync(token);
        }
        catch (OperationCanceledException)
        {
            SetInfo("Stopped. Whatever finished before you stopped is still shown.");
        }
        catch (Exception ex)
        {
            SetError($"Unexpected error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            Progress = 0;
        }
    }

    /// <summary>Overridden per tool. Must honour the token and keep partial results.</summary>
    protected abstract Task RunJobAsync(CancellationToken token);

    protected virtual bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    public void Stop()
    {
        CancelRun();
        StatusText = "Stopping…";
    }

    public override Task RunAsync() => StartAsync();

    public override void Clear()
    {
        HasResult = false;
        StatusText = string.Empty;
        ClearMessage();
    }

    protected void ReportProgress(int completed, int total, string? status = null)
    {
        Progress = total > 0 ? Math.Clamp(completed * 100.0 / total, 0, 100) : 0;
        StatusText = status ?? $"{completed:N0} of {total:N0}";
    }

    public override void Deactivate()
    {
        // A job in flight is deliberately not cancelled by navigating away: a profiling run is
        // something the user set going and will come back to. Only the state write happens here.
        PersistState();
    }
}
