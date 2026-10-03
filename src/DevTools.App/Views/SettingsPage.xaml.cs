using DevTools.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DevTools.App.Views;

/// <summary>Appearance, editor, behaviour, shortcuts and About (FR-S13).</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<SettingsViewModel>();
    }

    public SettingsViewModel ViewModel { get; }
}
