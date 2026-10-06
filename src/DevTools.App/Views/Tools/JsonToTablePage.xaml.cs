using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class JsonToTablePage : ToolPageBase
{
    public JsonToTablePage()
    {
        InitializeComponent();
        ViewModel = App.GetService<JsonToTableViewModel>();
        Tool = ViewModel;
    }

    public JsonToTableViewModel ViewModel { get; }
}
