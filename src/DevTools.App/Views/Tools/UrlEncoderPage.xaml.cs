using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class UrlEncoderPage : ToolPageBase
{
    public UrlEncoderPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<UrlEncoderViewModel>();
        Tool = ViewModel;
    }

    public UrlEncoderViewModel ViewModel { get; }
}
