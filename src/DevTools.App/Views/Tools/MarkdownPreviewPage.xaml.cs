using System.ComponentModel;
using DevTools.App.ViewModels.Tools;
using Microsoft.UI.Xaml;

namespace DevTools.App.Views.Tools;

/// <summary>
/// The Markdown page. The preview is a <see cref="Controls.SafeWebPreview"/>: no script, and
/// every request the page makes refused, so a Markdown file cannot make the app reach the network.
/// </summary>
public sealed partial class MarkdownPreviewPage : ToolPageBase
{
    public MarkdownPreviewPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<MarkdownPreviewViewModel>();
        Tool = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ActualThemeChanged += (_, _) => Render();
        Loaded += (_, _) => Render();
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public MarkdownPreviewViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MarkdownPreviewViewModel.Html) or nameof(MarkdownPreviewViewModel.ThemeIndex))
        {
            Render();
        }
    }

    private void Render()
    {
        var dark = ViewModel.ThemeIndex switch
        {
            1 => false,
            2 => true,
            _ => ActualTheme == ElementTheme.Dark,
        };

        Preview.Show(MarkdownPreviewViewModel.Document(ViewModel.Html, dark, forPreview: true));
    }
}
