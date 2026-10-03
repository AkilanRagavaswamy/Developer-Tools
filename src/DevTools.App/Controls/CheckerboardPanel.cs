using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DevTools.App.Controls;

/// <summary>
/// A content host whose background is the familiar transparency checkerboard.
/// </summary>
/// <remarks>
/// It exists for one reason: an icon drawn on a flat panel gives no way to tell a white fill
/// from a transparent one, and that difference is most of what you are checking when you look
/// at a converted icon. WinUI has no tiling brush, so the pattern is painted into a bitmap the
/// size of the panel and repainted when the panel resizes — one element rather than a grid of
/// several hundred rectangles.
/// </remarks>
public sealed partial class CheckerboardPanel : ContentControl
{
    private const int CellSize = 12;

    public CheckerboardPanel()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        SizeChanged += (_, _) => Repaint();
        ActualThemeChanged += (_, _) => Repaint();
    }

    private void Repaint()
    {
        var width = (int)Math.Ceiling(ActualWidth);
        var height = (int)Math.Ceiling(ActualHeight);

        if (width <= 0 || height <= 0)
        {
            return;
        }

        // A dark theme wants the pattern lighter than the surrounding card and a light theme
        // darker; either way both shades stay close together, so the artwork is what you read.
        var light = ActualTheme == ElementTheme.Dark ? (byte)0x2B : (byte)0xF2;
        var dark = ActualTheme == ElementTheme.Dark ? (byte)0x33 : (byte)0xE2;

        var bitmap = new WriteableBitmap(width, height);
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            var row = (y / CellSize) % 2;

            for (var x = 0; x < width; x++)
            {
                var shade = ((x / CellSize) % 2) == row ? light : dark;
                var offset = ((y * width) + x) * 4;

                // BGRA, premultiplied. Fully opaque, so the three channels carry the shade.
                pixels[offset] = shade;
                pixels[offset + 1] = shade;
                pixels[offset + 2] = shade;
                pixels[offset + 3] = 0xFF;
            }
        }

        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        bitmap.Invalidate();

        Background = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.None };
    }
}
