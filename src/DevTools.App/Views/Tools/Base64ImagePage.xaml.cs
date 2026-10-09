using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using DevTools.App.ViewModels.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DevTools.App.Views.Tools;

public sealed partial class Base64ImagePage : ToolPageBase
{
    public Base64ImagePage()
    {
        InitializeComponent();
        ViewModel = App.GetService<Base64ImageViewModel>();
        Tool = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Loaded += (_, _) => _ = ShowAsync();
    }

    public Base64ImageViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Base64ImageViewModel.ImageBytes))
        {
            _ = ShowAsync();
        }
    }

    /// <summary>
    /// Decodes the bytes with the platform's own codecs, which is also the honest test: if
    /// Windows will not draw it, neither will most of the places the Base64 is headed.
    /// </summary>
    private async Task ShowAsync()
    {
        if (ViewModel.ImageBytes is not { } bytes)
        {
            Preview.Source = null;
            return;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            if (ViewModel.IsSvg)
            {
                Preview.MaxWidth = double.PositiveInfinity;
                Preview.MaxHeight = double.PositiveInfinity;

                var svg = new SvgImageSource();
                await svg.SetSourceAsync(stream);
                Preview.Source = svg;
                ViewModel.Dimensions = svg.RasterizePixelWidth > 0 ? $"{svg.RasterizePixelWidth:0} × {svg.RasterizePixelHeight:0}" : string.Empty;
                return;
            }

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            Preview.Source = bitmap;

            // Shown at its own size when it fits, so a 16 px icon is not blown up into a blur.
            Preview.MaxWidth = bitmap.PixelWidth > 0 ? bitmap.PixelWidth : double.PositiveInfinity;
            Preview.MaxHeight = bitmap.PixelHeight > 0 ? bitmap.PixelHeight : double.PositiveInfinity;

            if (bitmap.PixelWidth > 0)
            {
                ViewModel.Dimensions = $"{bitmap.PixelWidth:N0} × {bitmap.PixelHeight:N0} px";
            }
        }
        catch (Exception)
        {
            Preview.Source = null;
            ViewModel.Dimensions = "Windows cannot draw this image";
        }
    }

    private void OnImageOpened(object sender, RoutedEventArgs e)
    {
        if (Preview.Source is BitmapImage { PixelWidth: > 0 } bitmap)
        {
            ViewModel.Dimensions = $"{bitmap.PixelWidth:N0} × {bitmap.PixelHeight:N0} px";
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Encode as Base64";
            e.DragUIOverride.IsContentVisible = false;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.FirstOrDefault() is StorageFile file)
            {
                var buffer = await FileIO.ReadBufferAsync(file);
                ViewModel.LoadImage(buffer.ToArray(), file.FileType);
            }
        }
        catch (Exception ex)
        {
            App.LogError("Base64 image drop", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
