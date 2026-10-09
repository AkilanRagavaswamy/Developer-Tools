using System.ComponentModel;
using DevTools.App.ViewModels.Tools;

namespace DevTools.App.Views.Tools;

public sealed partial class HtmlViewerPage : ToolPageBase
{
    public HtmlViewerPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<HtmlViewerViewModel>();
        Tool = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => Preview.Show(ViewModel.Document);
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public HtmlViewerViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HtmlViewerViewModel.Document))
        {
            Preview.Show(ViewModel.Document);
        }
    }
}
