using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using Windows.UI;

namespace DevTools.App.ViewModels.Tools;

/// <summary>QR Code Generator.</summary>
public sealed partial class QrCodeViewModel : TextToolViewModelBase
{
    private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);

    public QrCodeViewModel(ToolServices services)
        : base(services)
    {
        ErrorCorrectionIndex = 1;
        Margin = 4;
        DarkColor = Black;
        LightColor = White;
        StatsText = string.Empty;
    }

    public override string ToolId => "qr-code";

    /// <summary>0–3: low, medium, quartile, high.</summary>
    [ObservableProperty]
    public partial int ErrorCorrectionIndex { get; set; }

    /// <summary>The quiet zone, in modules. The standard asks for four.</summary>
    [ObservableProperty]
    public partial double Margin { get; set; }

    [ObservableProperty]
    public partial Color DarkColor { get; set; }

    [ObservableProperty]
    public partial Color LightColor { get; set; }

    /// <summary>Index into <see cref="ExportSizes"/>.</summary>
    [ObservableProperty]
    public partial int ExportSizeIndex { get; set; } = 1;

    public IReadOnlyList<int> ExportSizes { get; } = [512, 1024, 2048, 4096];

    /// <summary>The encoded code the preview draws; null when there is nothing to draw.</summary>
    [ObservableProperty]
    public partial QrMatrix? Matrix { get; set; }

    [ObservableProperty]
    public partial string StatsText { get; set; }

    public bool HasCode => Matrix is not null;

    private int MarginModules => double.IsNaN(Margin) ? 4 : (int)Math.Clamp(Margin, 0, 16);

    private QrErrorCorrection Level => (QrErrorCorrection)Math.Clamp(ErrorCorrectionIndex, 0, 3);

    partial void OnMatrixChanged(QrMatrix? value) => OnPropertyChanged(nameof(HasCode));

    partial void OnErrorCorrectionIndexChanged(int value) => OnOptionChanged();

    partial void OnMarginChanged(double value) => OnOptionChanged();

    partial void OnDarkColorChanged(Color value) => OnOptionChanged();

    partial void OnLightColorChanged(Color value) => OnOptionChanged();

    partial void OnExportSizeIndexChanged(int value) => PersistState();

    /// <summary>Swaps in one of the common colour schemes.</summary>
    [RelayCommand]
    private void UsePreset(string? preset)
    {
        (DarkColor, LightColor) = preset switch
        {
            "inverse" => (White, Black),
            "transparent" => (Black, Color.FromArgb(0, 255, 255, 255)),
            _ => (Black, White),
        };
    }

    protected override async Task RunCoreAsync(CancellationToken token)
    {
        if (string.IsNullOrEmpty(Input))
        {
            Matrix = null;
            StatsText = string.Empty;
            ClearMessage();
            return;
        }

        var text = Input;
        var level = Level;
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);

        try
        {
            var matrix = await ComputeAsync(() => QrCodeRenderer.Encode(text, level), token);
            Matrix = matrix;
            StatsText = $"Version {matrix.Version} · {matrix.Size} × {matrix.Size} modules · {bytes:N0} bytes";
            ClearMessage();

            if (Contrast(DarkColor, LightColor) < 3)
            {
                SetWarning("The two colours are close in brightness, so many scanners will not read this code.");
            }
        }
        catch (ZXing.WriterException)
        {
            Matrix = null;
            SetError(level == QrErrorCorrection.Low
                ? $"That is {bytes:N0} bytes, more than a QR code can hold ({QrCodeRenderer.MaxBytes:N0} at most)."
                : $"That is {bytes:N0} bytes, too much for this recovery level. A lower level holds more.");
        }
    }

    /// <summary>The WCAG contrast ratio between the two colours, ignoring transparency.</summary>
    private static double Contrast(Color a, Color b)
    {
        if (b.A == 0)
        {
            return 21;
        }

        static double Luminance(Color c)
        {
            static double Channel(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
        }

        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private int ExportSize => ExportSizes[Math.Clamp(ExportSizeIndex, 0, ExportSizes.Count - 1)];

    [RelayCommand]
    private async Task SavePngAsync()
    {
        if (Matrix is not { } matrix)
        {
            return;
        }

        var png = await QrCodeRenderer.ToPngAsync(matrix, ExportSize, MarginModules, DarkColor, LightColor);
        var path = await Services.Files.SaveBytesAsync("qr-code.png", png, ".png");
        if (path is not null)
        {
            SetSuccess($"Saved to {path}.");
        }
    }

    [RelayCommand]
    private async Task SaveSvgAsync()
    {
        if (Matrix is not { } matrix)
        {
            return;
        }

        var path = await Services.Files.SaveTextFileAsync("qr-code.svg", QrCodeRenderer.ToSvg(matrix, MarginModules, DarkColor, LightColor), ".svg");
        if (path is not null)
        {
            SetSuccess($"Saved to {path}.");
        }
    }

    [RelayCommand]
    private async Task CopyImageAsync()
    {
        if (Matrix is not { } matrix)
        {
            return;
        }

        var png = await QrCodeRenderer.ToPngAsync(matrix, ExportSize, MarginModules, DarkColor, LightColor);
        if (await Services.Clipboard.SetImageAsync(png))
        {
            SetSuccess("Copied the image to the clipboard.");
        }
        else
        {
            SetError("The clipboard is in use by another app. Try again.");
        }
    }

    [RelayCommand]
    private void CopySvg()
    {
        if (Matrix is { } matrix)
        {
            Services.Clipboard.SetText(QrCodeRenderer.ToSvg(matrix, MarginModules, DarkColor, LightColor));
            SetSuccess("Copied the SVG markup.");
        }
    }

    public override Task SaveOutputAsync() => SavePngAsync();

    public int PreviewMargin => MarginModules;

    protected override void OnOptionChanged()
    {
        OnPropertyChanged(nameof(PreviewMargin));
        base.OnOptionChanged();
    }

    public override void Clear()
    {
        base.Clear();
        Matrix = null;
        StatsText = string.Empty;
    }

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("ec", ErrorCorrectionIndex);
        state.Set("margin", MarginModules);
        state.Set("dark", ToHex(DarkColor));
        state.Set("light", ToHex(LightColor));
        state.Set("size", ExportSizeIndex);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        ErrorCorrectionIndex = Math.Clamp(state.GetInt("ec", 1), 0, 3);
        Margin = Math.Clamp(state.GetInt("margin", 4), 0, 16);
        DarkColor = FromHex(state.GetString("dark"), Black);
        LightColor = FromHex(state.GetString("light"), White);
        ExportSizeIndex = Math.Clamp(state.GetInt("size", 1), 0, ExportSizes.Count - 1);
    }

    protected override void ResetOptions()
    {
        ErrorCorrectionIndex = 1;
        Margin = 4;
        DarkColor = Black;
        LightColor = White;
        ExportSizeIndex = 1;
    }

    private static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color FromHex(string text, Color fallback) =>
        text.Length == 9 && text[0] == '#' && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : fallback;
}
