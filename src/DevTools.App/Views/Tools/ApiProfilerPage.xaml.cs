using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class ApiProfilerPage : ToolPageBase
{
    public ApiProfilerPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<ApiProfilerViewModel>();
        Tool = ViewModel;
    }

    public ApiProfilerViewModel ViewModel { get; }
}
