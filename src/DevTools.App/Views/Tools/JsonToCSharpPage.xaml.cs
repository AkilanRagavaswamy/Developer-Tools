using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class JsonToCSharpPage : ToolPageBase
{
    public JsonToCSharpPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<JsonToCSharpViewModel>();
        Tool = ViewModel;
    }

    public JsonToCSharpViewModel ViewModel { get; }
}
