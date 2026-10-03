using DevTools.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DevTools.App.Views;

/// <summary>The dashboard: favorites, recents and every tool as a card (FR-S02).</summary>
public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        ViewModel = App.GetService<HomeViewModel>();
    }

    public HomeViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }

    private void OnToolCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string toolId })
        {
            ViewModel.OpenToolCommand.Execute(toolId);
        }
    }

    private void OnToggleFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string toolId })
        {
            ViewModel.ToggleFavoriteCommand.Execute(toolId);
        }
    }
}
