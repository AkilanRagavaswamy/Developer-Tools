using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using DevTools.App.Services;
using DevTools.App.ViewModels.Tools;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DevTools.App.Views.Tools;

public sealed partial class QrCodePage : ToolPageBase
{
    public QrCodePage()
    {
        InitializeComponent();
        ViewModel = App.GetService<QrCodeViewModel>();
        Tool = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Loaded += (_, _) => Draw();
    }

    public QrCodeViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QrCodeViewModel.Matrix) or nameof(QrCodeViewModel.DarkColor) or
            nameof(QrCodeViewModel.LightColor) or nameof(QrCodeViewModel.PreviewMargin))
        {
            Draw();
        }
    }

    /// <summary>
    /// Draws the code at a few pixels per module and lets the Image scale it up. A whole number
    /// of pixels per module, drawn once, is what keeps the preview crisp at any window size.
    /// </summary>
    private void Draw()
    {
        if (ViewModel.Matrix is not { } matrix)
        {
            Preview.Source = null;
            return;
        }

        var margin = ViewModel.PreviewMargin;
        var scale = Math.Max(1, 600 / (matrix.Size + (2 * margin)));
        var (pixels, width) = QrCodeRenderer.Rasterize(matrix, scale, margin, ViewModel.DarkColor, ViewModel.LightColor);

        var bitmap = new WriteableBitmap(width, width);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        bitmap.Invalidate();
        Preview.Source = bitmap;
    }
}
