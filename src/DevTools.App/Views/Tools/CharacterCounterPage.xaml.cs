using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class CharacterCounterPage : ToolPageBase
{
    public CharacterCounterPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<CharacterCounterViewModel>();
        Tool = ViewModel;
    }

    public CharacterCounterViewModel ViewModel { get; }
}
