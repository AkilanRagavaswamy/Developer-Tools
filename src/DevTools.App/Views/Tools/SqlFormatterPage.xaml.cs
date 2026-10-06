using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class SqlFormatterPage : ToolPageBase
{
    public SqlFormatterPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<SqlFormatterViewModel>();
        Tool = ViewModel;
    }

    public SqlFormatterViewModel ViewModel { get; }
}
