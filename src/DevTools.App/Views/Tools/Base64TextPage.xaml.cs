using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class Base64TextPage : ToolPageBase
{
    public Base64TextPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<Base64TextViewModel>();
        Tool = ViewModel;
    }

    public Base64TextViewModel ViewModel { get; }
}
