using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using DevTools.Core;
using DevTools.Core.Text;

namespace DevTools.App.ViewModels;

/// <summary>
/// One input box, one output box, and a transform between them that re-runs as you type.
/// </summary>
/// <remarks>
/// Used by JSON Formatter, JSON to C# and SVG to XAML. The debounce and the generation guard
/// are what make live transforms usable: typing coalesces into one run, and a slow run that
/// has been superseded discards its own result rather than overwriting a newer one.
/// </remarks>
public abstract partial class TextToolViewModelBase : ToolViewModelBase
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(250);

    protected TextToolViewModelBase(ToolServices services)
        : base(services)
    {
        Input = string.Empty;
        Output = string.Empty;
    }

    [ObservableProperty]
    public partial string Input { get; set; }

    [ObservableProperty]
    public partial string Output { get; set; }

    /// <summary>Default file name offered when saving this tool's output.</summary>
    protected virtual string SuggestedFileName => ToolId;

    protected virtual string[] OutputExtensions => [".txt"];

    protected virtual string[] InputExtensions => [];

    /// <summary>False for tools too expensive to run on every keystroke (FR-T04).</summary>
    protected virtual bool RunsLive => true;

    public override bool ShowRunButton => !RunsLive;

    partial void OnInputChanged(string value)
    {
        OnPropertyChanged(nameof(InputStats));
        ScheduleRun();
        PersistState();
    }

    /// <summary>Live character, line and size counter shown under the input box (FR-T02).</summary>
    public string InputStats => Describe(Input);

    protected static string Describe(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "0 characters";
        }

        var characters = TextUtil.GraphemeCount(text);
        var lines = TextUtil.CountLines(text);
        var bytes = Encoding.UTF8.GetByteCount(text);

        return $"{characters:N0} characters · {lines:N0} lines · {Limits.Describe(bytes)}";
    }

    // ---------------------------------------------------------------- execution

    /// <summary>
    /// Queues a run after the debounce window, cancelling any run already in flight. Calling
    /// it repeatedly while typing coalesces into a single execution (edge case 12).
    /// </summary>
    protected void ScheduleRun()
    {
        if (IsRestoring)
        {
            return;
        }

        var token = BeginRun();

        if (!RunsLive)
        {
            return;
        }

        _ = RunAfterDelayAsync(token);
    }

    private async Task RunAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceDelay, token);
            await ExecuteAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke, or the user navigated away.
        }
    }

    /// <summary>Runs immediately, bypassing the debounce — the Run button and Ctrl+Enter.</summary>
    public override async Task RunAsync()
    {
        var token = BeginRun();

        try
        {
            await ExecuteAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by a newer run.
        }
    }

    private async Task ExecuteAsync(CancellationToken token)
    {
        if (await ExceedsInputGuardAsync())
        {
            return;
        }

        IsBusy = true;

        try
        {
            await RunCoreAsync(token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An engine should never throw; if one does, show it rather than losing the app.
            SetError($"Unexpected error: {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Implemented per tool. Called on the UI thread; use <see cref="ToolViewModelBase.ComputeAsync"/>
    /// for the work itself so the UI stays responsive.
    /// </summary>
    protected abstract Task RunCoreAsync(CancellationToken token);

    /// <summary>Asks before processing an input large enough to be slow (FR-T07).</summary>
    private async Task<bool> ExceedsInputGuardAsync()
    {
        var bytes = Encoding.UTF8.GetByteCount(Input);

        if (bytes <= Limits.MaxInputBytes)
        {
            return false;
        }

        var proceed = await Services.Dialogs.ConfirmAsync(
            "Large input",
            $"This input is {Limits.Describe(bytes)}, above the {Limits.Describe(Limits.MaxInputBytes)} " +
            "threshold. Processing it may take a moment. Continue?",
            "Process anyway");

        if (!proceed)
        {
            SetInfo("Processing skipped for this large input.");
        }

        return !proceed;
    }

    /// <summary>
    /// Applies a result to <see cref="Output"/> and the banner in one step. On failure the
    /// previous good output is deliberately left in place, so the pane does not flicker empty
    /// while the user is mid-edit (FR-T05).
    /// </summary>
    protected void Apply(OperationResult<string> result)
    {
        if (!result.IsSuccess)
        {
            SetError(result.Error!);
            return;
        }

        Output = result.Value ?? string.Empty;

        if (result.HasWarning)
        {
            SetWarning(result.Warning!);
        }
        else
        {
            ClearMessage();
        }
    }

    /// <summary>True when there is nothing to process, which is a placeholder, not an error.</summary>
    protected bool HandleEmptyInput()
    {
        if (!TextUtil.IsBlank(Input))
        {
            return false;
        }

        Output = string.Empty;
        ClearMessage();
        return true;
    }

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private async Task PasteAsync()
    {
        var text = await Services.Clipboard.GetTextAsync();

        if (text is null)
        {
            SetInfo("The clipboard does not contain text.");
            return;
        }

        Input = text;
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var result = await Services.Files.OpenTextFileAsync(InputExtensions);

        if (result.WasCancelled)
        {
            return;
        }

        if (!result.Success)
        {
            SetError(result.Error ?? "The file could not be opened.");
            return;
        }

        Input = result.Text ?? string.Empty;
        SetSuccess($"Loaded {result.FileName}.");
    }

    [RelayCommand]
    private void CopyOutput()
    {
        if (string.IsNullOrEmpty(Output))
        {
            return;
        }

        Services.Clipboard.SetText(Output);
        SetSuccess("Copied to clipboard.");
    }

    [RelayCommand]
    private void CopyInput()
    {
        if (!string.IsNullOrEmpty(Input))
        {
            Services.Clipboard.SetText(Input);
        }
    }

    public override void Clear()
    {
        Input = string.Empty;
        Output = string.Empty;
        ClearMessage();
    }

    public override async Task SaveOutputAsync()
    {
        if (string.IsNullOrEmpty(Output))
        {
            SetInfo("There is nothing to save yet.");
            return;
        }

        try
        {
            var path = await Services.Files.SaveTextFileAsync(SuggestedFileName, Output, OutputExtensions);

            if (path is not null)
            {
                SetSuccess($"Saved to {path}.");
            }
        }
        catch (IOException ex)
        {
            SetError(ex.Message);
        }
    }

    // ---------------------------------------------------------------- state

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.SetData("input", Input);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        Input = state.GetString("input");
    }

    protected override void OnOptionChanged()
    {
        base.OnOptionChanged();
        ScheduleRun();
    }

    protected override void AfterReset() => ScheduleRun();

    protected override Task OnActivatedAsync()
    {
        // A payload handed over from another tool replaces the input (FR-S18). Taking it is a
        // consuming read, so returning to the page later does not re-apply it.
        if (Services.Handoff.Take(ToolId) is ToolPayload.Text handed)
        {
            Input = handed.Value;
            SetInfo("Loaded from another tool.");
        }

        ScheduleRun();
        return Task.CompletedTask;
    }
}
