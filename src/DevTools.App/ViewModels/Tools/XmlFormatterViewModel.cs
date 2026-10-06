using CommunityToolkit.Mvvm.ComponentModel;
using DevTools.App.Services;
using DevTools.Core;
using DevTools.Core.Text;
using DevTools.Core.Xml;

namespace DevTools.App.ViewModels.Tools;

/// <summary>XML Formatter.</summary>
public sealed partial class XmlFormatterViewModel : TextToolViewModelBase
{
    public XmlFormatterViewModel(ToolServices services)
        : base(services)
    {
        IndentStyle = IndentStyle.TwoSpaces;
        StatsText = string.Empty;
    }

    public override string ToolId => "xml-formatter";

    protected override string SuggestedFileName => "formatted.xml";

    protected override string[] OutputExtensions => [".xml", ".txt"];

    protected override string[] InputExtensions => [".xml", ".config", ".csproj", ".xaml", ".svg", ".txt"];

    /// <summary>0 = format, 1 = minify.</summary>
    [ObservableProperty]
    public partial int ModeIndex { get; set; }

    [ObservableProperty]
    public partial IndentStyle IndentStyle { get; set; }

    [ObservableProperty]
    public partial bool NewLineOnAttributes { get; set; }

    [ObservableProperty]
    public partial bool RemoveComments { get; set; }

    [ObservableProperty]
    public partial string StatsText { get; set; }

    public IReadOnlyList<IndentStyle> IndentStyles { get; } = [IndentStyle.TwoSpaces, IndentStyle.FourSpaces, IndentStyle.Tab];

    public bool IsMinify => ModeIndex == 1;

    public bool IsIndentEnabled => !IsMinify;

    public int IndentIndex
    {
        get => IndentStyles.IndexOfValue(IndentStyle);
        set => IndentStyle = IndentStyles.ValueAt(value, IndentStyle.TwoSpaces);
    }

    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsMinify));
        OnPropertyChanged(nameof(IsIndentEnabled));
        OnOptionChanged();
    }

    partial void OnIndentStyleChanged(IndentStyle value)
    {
        OnPropertyChanged(nameof(IndentIndex));
        OnOptionChanged();
    }

    partial void OnNewLineOnAttributesChanged(bool value) => OnOptionChanged();

    partial void OnRemoveCommentsChanged(bool value) => OnOptionChanged();

    protected override async Task RunCoreAsync(CancellationToken token)
    {
        if (HandleEmptyInput())
        {
            StatsText = string.Empty;
            return;
        }

        var input = Input;
        var options = new XmlFormatOptions
        {
            IndentStyle = IndentStyle,
            Minify = IsMinify,
            NewLineOnAttributes = NewLineOnAttributes,
            RemoveComments = RemoveComments,
        };

        var result = await ComputeAsync(() => XmlFormatter.Format(input, options), token);

        if (!result.IsSuccess)
        {
            SetError(result.Error!);
            return;
        }

        var value = result.Value!;
        Output = value.Output;
        StatsText = $"{value.ElementCount:N0} elements · {value.AttributeCount:N0} attributes · depth {value.MaxDepth:N0} · {Limits.Describe(System.Text.Encoding.UTF8.GetByteCount(value.Output))}";
        ClearMessage();
    }

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("mode", ModeIndex);
        state.Set("indent", IndentStyle);
        state.Set("attributes", NewLineOnAttributes);
        state.Set("comments", RemoveComments);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        ModeIndex = Math.Clamp(state.GetInt("mode"), 0, 1);
        IndentStyle = state.GetEnum("indent", IndentStyle.TwoSpaces);
        NewLineOnAttributes = state.GetBool("attributes");
        RemoveComments = state.GetBool("comments");
    }

    protected override void ResetOptions()
    {
        ModeIndex = 0;
        IndentStyle = IndentStyle.TwoSpaces;
        NewLineOnAttributes = false;
        RemoveComments = false;
        StatsText = string.Empty;
    }
}
