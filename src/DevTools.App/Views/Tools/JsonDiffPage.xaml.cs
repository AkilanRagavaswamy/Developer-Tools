using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class JsonDiffPage : ToolPageBase
{
    public JsonDiffPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<JsonDiffViewModel>();
        Tool = ViewModel;
    }

    public JsonDiffViewModel ViewModel { get; }
}
