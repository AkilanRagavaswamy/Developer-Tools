using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class RegexValidatorPage : ToolPageBase
{
    public RegexValidatorPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<RegexValidatorViewModel>();
        Tool = ViewModel;
    }

    public RegexValidatorViewModel ViewModel { get; }
}
