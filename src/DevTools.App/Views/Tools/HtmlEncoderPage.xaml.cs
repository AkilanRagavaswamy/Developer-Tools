using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class HtmlEncoderPage : ToolPageBase
{
    public HtmlEncoderPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<HtmlEncoderViewModel>();
        Tool = ViewModel;
    }

    public HtmlEncoderViewModel ViewModel { get; }
}
