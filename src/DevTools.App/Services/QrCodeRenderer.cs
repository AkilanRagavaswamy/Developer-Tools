using System.Globalization;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using ZXing;
using ZXing.QrCode.Internal;

namespace DevTools.App.Services;

/// <summary>The four Reed–Solomon recovery levels a QR code can carry.</summary>
public enum QrErrorCorrection
{
    /// <summary>Recovers about 7% damage. The smallest code.</summary>
    Low,

    /// <summary>About 15%. The usual default.</summary>
    Medium,

    /// <summary>About 25%.</summary>
    Quartile,

    /// <summary>About 30% — enough to survive a logo printed over the middle.</summary>
    High,
}

/// <summary>A QR code as its grid of modules: <c>true</c> is dark.</summary>
public sealed record QrMatrix(bool[,] Modules, int Version)
{
    public int Size => Modules.GetLength(0);
}

/// <summary>
/// Encodes text as a QR code and draws it.
/// </summary>
/// <remarks>
/// <para>
/// ZXing.Net (Apache-2.0) does the encoding only — choosing the version and mask and laying out
/// the modules. Drawing is done here, straight from the module grid: PNG through the Windows
/// imaging encoder and SVG as one path. DevToys draws through ZXing's ImageSharp binding, and
/// ImageSharp's split licence is not open source for every user; nothing here needs it.
/// </para>
/// <para>
/// Text is encoded as UTF-8 and flagged as such with an ECI header, so accented letters and
/// emoji read back correctly on phones rather than as Latin-1 guesses.
/// </para>
/// </remarks>
public static class QrCodeRenderer
{
    /// <summary>The most bytes a version 40 code holds at the lowest recovery level.</summary>
    public const int MaxBytes = 2953;

    public static QrMatrix Encode(string text, QrErrorCorrection level)
    {
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
        };

        var ec = level switch
        {
            QrErrorCorrection.Low => ErrorCorrectionLevel.L,
            QrErrorCorrection.Quartile => ErrorCorrectionLevel.Q,
            QrErrorCorrection.High => ErrorCorrectionLevel.H,
            _ => ErrorCorrectionLevel.M,
        };

        var code = ZXing.QrCode.Internal.Encoder.encode(text, ec, hints);
        var matrix = code.Matrix;
        var modules = new bool[matrix.Width, matrix.Height];

        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                modules[x, y] = matrix[x, y] == 1;
            }
        }

        return new QrMatrix(modules, code.Version.VersionNumber);
    }

    /// <summary>
    /// BGRA pixels, premultiplied, with <paramref name="margin"/> modules of quiet zone around
    /// the code. Each module is <paramref name="scale"/> pixels square, so edges stay crisp.
    /// </summary>
    public static (byte[] Pixels, int Width) Rasterize(QrMatrix qr, int scale, int margin, Windows.UI.Color dark, Windows.UI.Color light)
    {
        var modules = qr.Size + (2 * margin);
        var width = modules * scale;
        var pixels = new byte[width * width * 4];

        static (byte B, byte G, byte R, byte A) Premultiply(Windows.UI.Color c) =>
            ((byte)(c.B * c.A / 255), (byte)(c.G * c.A / 255), (byte)(c.R * c.A / 255), c.A);

        var darkPx = Premultiply(dark);
        var lightPx = Premultiply(light);

        for (var my = 0; my < modules; my++)
        {
            for (var mx = 0; mx < modules; mx++)
            {
                var x = mx - margin;
                var y = my - margin;
                var isDark = x >= 0 && y >= 0 && x < qr.Size && y < qr.Size && qr.Modules[x, y];
                var px = isDark ? darkPx : lightPx;

                for (var dy = 0; dy < scale; dy++)
                {
                    var row = ((my * scale) + dy) * width * 4;
                    for (var dx = 0; dx < scale; dx++)
                    {
                        var at = row + (((mx * scale) + dx) * 4);
                        pixels[at] = px.B;
                        pixels[at + 1] = px.G;
                        pixels[at + 2] = px.R;
                        pixels[at + 3] = px.A;
                    }
                }
            }
        }

        return (pixels, width);
    }

    /// <summary>Encodes the code as a PNG at roughly <paramref name="targetSize"/> pixels square.</summary>
    public static async Task<byte[]> ToPngAsync(QrMatrix qr, int targetSize, int margin, Windows.UI.Color dark, Windows.UI.Color light)
    {
        var scale = Math.Max(1, targetSize / (qr.Size + (2 * margin)));
        var (pixels, width) = Rasterize(qr, scale, margin, dark, light);

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)width, 96, 96, pixels);
        await encoder.FlushAsync();

        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// The code as SVG: one path of unit squares on a viewBox measured in modules, so it scales
    /// to any size without blurring and stays small.
    /// </summary>
    public static string ToSvg(QrMatrix qr, int margin, Windows.UI.Color dark, Windows.UI.Color light)
    {
        var size = qr.Size + (2 * margin);
        var path = new StringBuilder();

        for (var y = 0; y < qr.Size; y++)
        {
            var x = 0;
            while (x < qr.Size)
            {
                if (!qr.Modules[x, y])
                {
                    x++;
                    continue;
                }

                // A run of dark modules along the row is one rectangle.
                var start = x;
                while (x < qr.Size && qr.Modules[x, y])
                {
                    x++;
                }

                path.Append(CultureInfo.InvariantCulture, $"M{start + margin} {y + margin}h{x - start}v1h-{x - start}z");
            }
        }

        var background = light.A == 0
            ? string.Empty
            : $"<rect width=\"{size}\" height=\"{size}\" fill=\"{Hex(light)}\"{Opacity(light)}/>";

        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" shape-rendering=\"crispEdges\">" +
               background +
               $"<path fill=\"{Hex(dark)}\"{Opacity(dark)} d=\"{path}\"/></svg>";
    }

    private static string Hex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string Opacity(Windows.UI.Color c) =>
        c.A == 255 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" fill-opacity=\"{c.A / 255.0:0.###}\"");
}
