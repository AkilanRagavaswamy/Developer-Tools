using System.Runtime.InteropServices.WindowsRuntime;
using DevTools.App.ViewModels.Tools;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace DevTools.App.Views.Tools;

public sealed partial class ApiBuilderPage : ToolPageBase
{
    public ApiBuilderPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<ApiBuilderViewModel>();
        Tool = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public ApiBuilderViewModel ViewModel { get; }

    /// <summary>Opening a request is a click, not a selection: clicking the open one reloads it.</summary>
    private void OnTreeItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TreeEntry entry)
        {
            ViewModel.OpenTreeEntryCommand.Execute(entry.Id);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApiBuilderViewModel.IsImageResponse))
        {
            _ = ShowImagePreviewAsync();
        }
    }

    /// <summary>
    /// Decodes an image response for the Preview tab.
    /// </summary>
    /// <remarks>
    /// The bytes go through <c>BitmapImage</c>, which is a decoder and nothing more — unlike a
    /// web view, it cannot be talked into running anything the response contains.
    /// </remarks>
    private async Task ShowImagePreviewAsync()
    {
        if (!ViewModel.IsImageResponse || ViewModel.ResponseBytes is not { Length: > 0 } bytes)
        {
            ImagePreview.Source = null;
            return;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            ImagePreview.Source = image;
        }
        catch (Exception ex)
        {
            ImagePreview.Source = null;
            ViewModel.PreviewNotice = $"The image could not be decoded: {ex.Message}";
            App.LogError("API Builder image preview", ex);
        }
    }
}
