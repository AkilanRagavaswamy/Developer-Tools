using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class DateConverterPage : ToolPageBase
{
    public DateConverterPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<DateConverterViewModel>();
        Tool = ViewModel;
    }

    public DateConverterViewModel ViewModel { get; }

    private void OnCopyClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        ViewModel.CopyValueCommand.Execute((sender as Microsoft.UI.Xaml.FrameworkElement)?.Tag as string);
}
