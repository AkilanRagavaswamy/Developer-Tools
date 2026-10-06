using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class UuidGeneratorPage : ToolPageBase
{
    public UuidGeneratorPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<UuidGeneratorViewModel>();
        Tool = ViewModel;
    }

    public UuidGeneratorViewModel ViewModel { get; }
}
