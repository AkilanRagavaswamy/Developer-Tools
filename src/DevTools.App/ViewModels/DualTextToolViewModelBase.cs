using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using DevTools.Core;
using DevTools.Core.Text;

namespace DevTools.App.ViewModels;

/// <summary>
/// Two input boxes compared against each other. Used by JSON Diff Checker.
/// </summary>
/// <remarks>
/// Not a special case of <see cref="TextToolViewModelBase"/> with an extra property: both
/// sides need their own paste, open, clear and counter, the guard has to weigh the pair rather
/// than one box, and swapping them is a first-class action rather than two edits.
/// </remarks>
public abstract partial class DualTextToolViewModelBase : ToolViewModelBase
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(250);

    protected DualTextToolViewModelBase(ToolServices services)
        : base(services)
    {
        Left = string.Empty;
        Right = string.Empty;
        Output = string.Empty;
    }

    [ObservableProperty]
    public partial string Left { get; set; }

    [ObservableProperty]
    public partial string Right { get; set; }

    /// <summary>The rendered result — a patch, a unified diff, whatever the tool produces.</summary>
    [ObservableProperty]
    public partial string Output { get; set; }

    public virtual string LeftLabel => "Original";

    public virtual string RightLabel => "Changed";

    protected virtual string SuggestedFileName => ToolId;

    protected virtual string[] OutputExtensions => [".txt"];

    protected virtual string[] InputExtensions => [];

    public string LeftStats => Describe(Left);

    public string RightStats => Describe(Right);

    private static string Describe(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "0 characters";
        }

        var characters = TextUtil.GraphemeCount(text);
        var lines = TextUtil.SplitLines(text).Length;
        return $"{characters:N0} characters · {lines:N0} lines";
    }

    partial void OnLeftChanged(string value)
    {
        OnPropertyChanged(nameof(LeftStats));
        ScheduleRun();
        PersistState();
    }

    partial void OnRightChanged(string value)
    {
        OnPropertyChanged(nameof(RightStats));
        ScheduleRun();
        PersistState();
    }

    // ---------------------------------------------------------------- execution

    protected void ScheduleRun()
    {
        if (IsRestoring)
        {
            return;
        }

        var token = BeginRun();
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
            // Superseded.
        }
    }

    public override async Task RunAsync()
    {
        var token = BeginRun();

        try
        {
            await ExecuteAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Superseded.
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

    protected abstract Task RunCoreAsync(CancellationToken token);

    /// <summary>The guard weighs the pair, because the comparison has to hold both.</summary>
    private async Task<bool> ExceedsInputGuardAsync()
    {
        var bytes = Encoding.UTF8.GetByteCount(Left) + Encoding.UTF8.GetByteCount(Right);

        if (bytes <= Limits.MaxInputBytes)
        {
            return false;
        }

        var proceed = await Services.Dialogs.ConfirmAsync(
            "Large input",
            $"The two documents come to {Limits.Describe(bytes)}, above the " +
            $"{Limits.Describe(Limits.MaxInputBytes)} threshold. Comparing them may take a moment. Continue?",
            "Compare anyway");

        if (!proceed)
        {
            SetInfo("Comparison skipped for this large input.");
        }

        return !proceed;
    }

    /// <summary>True when either side is empty, which is a placeholder, not an error.</summary>
    protected bool HandleEmptyInput()
    {
        if (!TextUtil.IsBlank(Left) && !TextUtil.IsBlank(Right))
        {
            return false;
        }

        Output = string.Empty;
        ClearMessage();
        OnEmptyInput();
        return true;
    }

    /// <summary>Hook for clearing whatever else the tool shows beside the output text.</summary>
    protected virtual void OnEmptyInput()
    {
    }

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private async Task PasteLeftAsync() => Left = await PasteAsync() ?? Left;

    [RelayCommand]
    private async Task PasteRightAsync() => Right = await PasteAsync() ?? Right;

    private async Task<string?> PasteAsync()
    {
        var text = await Services.Clipboard.GetTextAsync();

        if (text is null)
        {
            SetInfo("The clipboard does not contain text.");
        }

        return text;
    }

    [RelayCommand]
    private async Task OpenLeftAsync()
    {
        if (await OpenAsync() is { } text)
        {
            Left = text;
        }
    }

    [RelayCommand]
    private async Task OpenRightAsync()
    {
        if (await OpenAsync() is { } text)
        {
            Right = text;
        }
    }

    private async Task<string?> OpenAsync()
    {
        var result = await Services.Files.OpenTextFileAsync(InputExtensions);

        if (result.WasCancelled)
        {
            return null;
        }

        if (!result.Success)
        {
            SetError(result.Error ?? "The file could not be opened.");
            return null;
        }

        SetSuccess($"Loaded {result.FileName}.");
        return result.Text ?? string.Empty;
    }

    [RelayCommand]
    private void ClearLeft() => Left = string.Empty;

    [RelayCommand]
    private void ClearRight() => Right = string.Empty;

    /// <summary>Swaps the two sides — the quickest way to read a diff the other way round.</summary>
    [RelayCommand]
    private void Swap()
    {
        (Left, Right) = (Right, Left);
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

    public override void Clear()
    {
        Left = string.Empty;
        Right = string.Empty;
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
        state.SetData("left", Left);
        state.SetData("right", Right);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        Left = state.GetString("left");
        Right = state.GetString("right");
    }

    protected override void OnOptionChanged()
    {
        base.OnOptionChanged();
        ScheduleRun();
    }

    protected override void AfterReset() => ScheduleRun();

    protected override Task OnActivatedAsync()
    {
        // Two responses handed over from API Builder land as the two sides (FR-A31).
        if (Services.Handoff.Take(ToolId) is ToolPayload.TextPair handed)
        {
            Left = handed.Left;
            Right = handed.Right;
            SetInfo("Loaded two documents from another tool.");
        }

        ScheduleRun();
        return Task.CompletedTask;
    }
}
