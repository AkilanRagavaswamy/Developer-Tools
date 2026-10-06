using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class TextComparePage : ToolPageBase
{
    public TextComparePage()
    {
        InitializeComponent();
        ViewModel = App.GetService<TextCompareViewModel>();
        Tool = ViewModel;
    }

    public TextCompareViewModel ViewModel { get; }
}
