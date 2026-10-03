using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class JsonFormatterPage : ToolPageBase
{
    public JsonFormatterPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<JsonFormatterViewModel>();
        Tool = ViewModel;
    }

    public JsonFormatterViewModel ViewModel { get; }
}
