using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using DevTools.Core;
using DevTools.Core.Codecs;
using DevTools.Core.Text;

namespace DevTools.App.ViewModels.Tools;

/// <summary>
/// What the encoders and decoders share: a direction, and a swap that feeds the result back in
/// the other way — the quickest check that an encoding round-trips.
/// </summary>
public abstract partial class CodecViewModelBase : TextToolViewModelBase
{
    protected CodecViewModelBase(ToolServices services)
        : base(services)
    {
    }

    /// <summary>0 = encode, 1 = decode.</summary>
    [ObservableProperty]
    public partial int DirectionIndex { get; set; }

    public CodecDirection Direction => DirectionIndex == 1 ? CodecDirection.Decode : CodecDirection.Encode;

    public string InputHeader => Direction == CodecDirection.Encode ? PlainLabel : EncodedLabel;

    public string OutputHeader => Direction == CodecDirection.Encode ? EncodedLabel : PlainLabel;

    protected virtual string PlainLabel => "Text";

    protected abstract string EncodedLabel { get; }

    partial void OnDirectionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Direction));
        OnPropertyChanged(nameof(InputHeader));
        OnPropertyChanged(nameof(OutputHeader));
        OnOptionChanged();
    }

    /// <summary>Moves the result into the input and turns the direction round.</summary>
    [RelayCommand]
    private void Swap()
    {
        var output = Output;
        DirectionIndex = DirectionIndex == 0 ? 1 : 0;
        Input = output;
    }

    protected async Task RunCodecAsync(Func<string, OperationResult<string>> run, CancellationToken token)
    {
        if (string.IsNullOrEmpty(Input))
        {
            Output = string.Empty;
            ClearMessage();
            OutputStats = string.Empty;
            return;
        }

        var input = Input;
        var result = await ComputeAsync(() => run(input), token);
        Apply(result);

        if (result.IsSuccess)
        {
            OutputStats = Describe(Output);
        }
    }

    [ObservableProperty]
    public partial string OutputStats { get; set; } = string.Empty;

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("direction", DirectionIndex);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        DirectionIndex = Math.Clamp(state.GetInt("direction"), 0, 1);
    }

    protected override void ResetOptions() => DirectionIndex = 0;
}

/// <summary>Base64 Text.</summary>
public sealed partial class Base64TextViewModel : CodecViewModelBase
{
    public Base64TextViewModel(ToolServices services)
        : base(services)
    {
        TextEncoding = TextEncodingKind.Utf8;
    }

    public override string ToolId => "base64-text";

    protected override string EncodedLabel => "Base64";

    protected override string SuggestedFileName => DirectionIndex == 0 ? "encoded.txt" : "decoded.txt";

    [ObservableProperty]
    public partial TextEncodingKind TextEncoding { get; set; }

    [ObservableProperty]
    public partial bool UrlSafe { get; set; }

    [ObservableProperty]
    public partial bool WrapLines { get; set; }

    [ObservableProperty]
    public partial bool EachLine { get; set; }

    public IReadOnlyList<TextEncodingKind> Encodings { get; } = EncodingCatalog.All;

    public IReadOnlyList<string> EncodingNames { get; } = [.. EncodingCatalog.All.Select(EncodingCatalog.DisplayName)];

    public int EncodingIndex
    {
        get => Encodings.IndexOfValue(TextEncoding);
        set => TextEncoding = Encodings.ValueAt(value, TextEncodingKind.Utf8);
    }

    public int MoreOptionCount => (UrlSafe ? 1 : 0) + (WrapLines ? 1 : 0) + (EachLine ? 1 : 0);

    public bool HasMoreOptions => MoreOptionCount > 0;

    partial void OnTextEncodingChanged(TextEncodingKind value)
    {
        OnPropertyChanged(nameof(EncodingIndex));
        OnOptionChanged();
    }

    partial void OnUrlSafeChanged(bool value) => MoreChanged();

    partial void OnWrapLinesChanged(bool value) => MoreChanged();

    partial void OnEachLineChanged(bool value) => MoreChanged();

    private void MoreChanged()
    {
        OnPropertyChanged(nameof(MoreOptionCount));
        OnPropertyChanged(nameof(HasMoreOptions));
        OnOptionChanged();
    }

    protected override Task RunCoreAsync(CancellationToken token)
    {
        var options = new Base64Options { TextEncoding = TextEncoding, UrlSafe = UrlSafe, WrapLines = WrapLines, EachLine = EachLine };
        var direction = Direction;
        return RunCodecAsync(input => Base64Codec.Run(input, direction, options), token);
    }

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("encoding", TextEncoding);
        state.Set("urlSafe", UrlSafe);
        state.Set("wrap", WrapLines);
        state.Set("eachLine", EachLine);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        TextEncoding = state.GetEnum("encoding", TextEncodingKind.Utf8);
        UrlSafe = state.GetBool("urlSafe");
        WrapLines = state.GetBool("wrap");
        EachLine = state.GetBool("eachLine");
    }

    protected override void ResetOptions()
    {
        base.ResetOptions();
        TextEncoding = TextEncodingKind.Utf8;
        UrlSafe = false;
        WrapLines = false;
        EachLine = false;
    }
}

/// <summary>URL Encoder.</summary>
public sealed partial class UrlEncoderViewModel : CodecViewModelBase
{
    public UrlEncoderViewModel(ToolServices services)
        : base(services)
    {
    }

    public override string ToolId => "url-encoder";

    protected override string EncodedLabel => "Encoded";

    protected override string PlainLabel => "Decoded";

    /// <summary>0 = component, 1 = full URL, 2 = form.</summary>
    [ObservableProperty]
    public partial int ModeIndex { get; set; }

    [ObservableProperty]
    public partial bool EachLine { get; set; }

    private UrlEncodeMode Mode => ModeIndex switch
    {
        1 => UrlEncodeMode.FullUrl,
        2 => UrlEncodeMode.Form,
        _ => UrlEncodeMode.Component,
    };

    partial void OnModeIndexChanged(int value) => OnOptionChanged();

    partial void OnEachLineChanged(bool value) => OnOptionChanged();

    protected override Task RunCoreAsync(CancellationToken token)
    {
        var direction = Direction;
        var mode = Mode;
        var eachLine = EachLine;
        return RunCodecAsync(input => UrlCodec.Run(input, direction, mode, eachLine), token);
    }

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("mode", ModeIndex);
        state.Set("eachLine", EachLine);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        ModeIndex = Math.Clamp(state.GetInt("mode"), 0, 2);
        EachLine = state.GetBool("eachLine");
    }

    protected override void ResetOptions()
    {
        base.ResetOptions();
        ModeIndex = 0;
        EachLine = false;
    }
}

/// <summary>HTML Encoder.</summary>
public sealed partial class HtmlEncoderViewModel : CodecViewModelBase
{
    public HtmlEncoderViewModel(ToolServices services)
        : base(services)
    {
    }

    public override string ToolId => "html-encoder";

    protected override string EncodedLabel => "HTML";

    [ObservableProperty]
    public partial bool EncodeNonAscii { get; set; }

    partial void OnEncodeNonAsciiChanged(bool value) => OnOptionChanged();

    protected override Task RunCoreAsync(CancellationToken token)
    {
        var direction = Direction;
        var nonAscii = EncodeNonAscii;
        return RunCodecAsync(input => HtmlCodec.Run(input, direction, nonAscii), token);
    }

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("nonAscii", EncodeNonAscii);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        EncodeNonAscii = state.GetBool("nonAscii");
    }

    protected override void ResetOptions()
    {
        base.ResetOptions();
        EncodeNonAscii = false;
    }
}
