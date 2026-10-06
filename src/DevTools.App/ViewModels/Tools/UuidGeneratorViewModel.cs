using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using DevTools.Core.Generators;

namespace DevTools.App.ViewModels.Tools;

/// <summary>UUID Generator.</summary>
public sealed partial class UuidGeneratorViewModel : ToolViewModelBase
{
    private static readonly UuidVersion[] VersionList = [UuidVersion.V4, UuidVersion.V7, UuidVersion.V1, UuidVersion.Nil];

    private static readonly UuidFormat[] FormatList = [UuidFormat.Hyphenated, UuidFormat.Compact, UuidFormat.Braces, UuidFormat.Urn];

    public UuidGeneratorViewModel(ToolServices services)
        : base(services)
    {
        Count = 1;
        Output = string.Empty;
        InspectText = string.Empty;
        InspectResult = string.Empty;
    }

    public override string ToolId => "uuid-generator";

    public override bool ShowRunButton => true;

    public override string RunLabel => "Generate";

    [ObservableProperty]
    public partial int VersionIndex { get; set; }

    [ObservableProperty]
    public partial int FormatIndex { get; set; }

    [ObservableProperty]
    public partial bool Uppercase { get; set; }

    [ObservableProperty]
    public partial double Count { get; set; }

    [ObservableProperty]
    public partial string Output { get; set; }

    /// <summary>A UUID pasted to find out which version it is.</summary>
    [ObservableProperty]
    public partial string InspectText { get; set; }

    [ObservableProperty]
    public partial string InspectResult { get; set; }

    public string OutputStats
    {
        get
        {
            var lines = string.IsNullOrEmpty(Output) ? 0 : Output.Count(static c => c == '\n') + 1;
            return lines == 1 ? "1 UUID" : $"{lines:N0} UUIDs";
        }
    }

    public string VersionHint => VersionList[Math.Clamp(VersionIndex, 0, VersionList.Length - 1)] switch
    {
        UuidVersion.V7 => "Time-ordered: sorts by creation time, which keeps database indexes compact.",
        UuidVersion.V1 => "Time-based, with a random node in place of a network card's address.",
        UuidVersion.Nil => "All zeros — a placeholder for \"no value\".",
        _ => "Random. The usual choice when nothing needs to sort by time.",
    };

    partial void OnVersionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(VersionHint));
        Changed();
    }

    partial void OnFormatIndexChanged(int value) => Changed();

    partial void OnUppercaseChanged(bool value) => Changed();

    partial void OnCountChanged(double value) => Changed();

    partial void OnOutputChanged(string value) => OnPropertyChanged(nameof(OutputStats));

    partial void OnInspectTextChanged(string value) =>
        InspectResult = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : UuidGenerator.Describe(value) ?? "That is not a UUID.";

    private void Changed()
    {
        // Before the saved options are in, a change is only a control settling its initial value
        // (the NumberBox does this) and generating then would use the wrong count.
        if (IsRestoring || !IsStateLoaded)
        {
            return;
        }

        PersistState();
        Generate();
    }

    public override Task RunAsync()
    {
        Generate();
        return Task.CompletedTask;
    }

    private void Generate()
    {
        var count = double.IsNaN(Count) ? 1 : (int)Math.Clamp(Count, 1, UuidGenerator.MaxCount);

        Output = UuidGenerator.Generate(new UuidOptions
        {
            Version = VersionList[Math.Clamp(VersionIndex, 0, VersionList.Length - 1)],
            Format = FormatList[Math.Clamp(FormatIndex, 0, FormatList.Length - 1)],
            Uppercase = Uppercase,
            Count = count,
        });
    }

    [RelayCommand]
    private void CopyOutput()
    {
        if (!string.IsNullOrEmpty(Output))
        {
            Services.Clipboard.SetText(Output);
            SetSuccess(Output.Contains('\n') ? "Copied to clipboard." : $"Copied {Output}.");
        }
    }

    public override async Task SaveOutputAsync()
    {
        if (string.IsNullOrEmpty(Output))
        {
            return;
        }

        var path = await Services.Files.SaveTextFileAsync("uuids.txt", Output, [".txt"]);
        if (path is not null)
        {
            SetSuccess($"Saved to {path}.");
        }
    }

    public override void Clear()
    {
        base.Clear();
        Output = string.Empty;
        InspectText = string.Empty;
    }

    protected override Task OnActivatedAsync()
    {
        // A fresh batch every time the tool opens, made with the options just restored.
        Generate();
        return Task.CompletedTask;
    }

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("version", VersionIndex);
        state.Set("format", FormatIndex);
        state.Set("upper", Uppercase);
        state.Set("count", (int)Math.Clamp(double.IsNaN(Count) ? 1 : Count, 1, UuidGenerator.MaxCount));
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        VersionIndex = Math.Clamp(state.GetInt("version"), 0, VersionList.Length - 1);
        FormatIndex = Math.Clamp(state.GetInt("format"), 0, FormatList.Length - 1);
        Uppercase = state.GetBool("upper");
        Count = Math.Clamp(state.GetInt("count", 1), 1, UuidGenerator.MaxCount);
    }

    protected override void ResetOptions()
    {
        VersionIndex = 0;
        FormatIndex = 0;
        Uppercase = false;
        Count = 1;
    }

    protected override void AfterReset() => Generate();
}
