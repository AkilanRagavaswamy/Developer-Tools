using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using DevTools.Core;
using DevTools.Core.Codecs;

namespace DevTools.App.ViewModels.Tools;

/// <summary>Base64 Image encoder and decoder.</summary>
/// <remarks>
/// One page, both directions: paste Base64 or a data URI and the image appears; open, drop or
/// paste an image and its Base64 appears. The text is always the source of truth, so what is in
/// the box is exactly what will be copied.
/// </remarks>
public sealed partial class Base64ImageViewModel : TextToolViewModelBase
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".svg", ".tif", ".tiff"];

    private bool _encoding;

    public Base64ImageViewModel(ToolServices services)
        : base(services)
    {
        AsDataUri = true;
        ImageInfo = string.Empty;
    }

    public override string ToolId => "base64-image";

    protected override string[] InputExtensions => [".txt", ".b64", ".base64"];

    /// <summary>Writes <c>data:image/png;base64,…</c> rather than bare Base64.</summary>
    [ObservableProperty]
    public partial bool AsDataUri { get; set; }

    /// <summary>The decoded image, or null when the text does not decode to one.</summary>
    [ObservableProperty]
    public partial byte[]? ImageBytes { get; set; }

    [ObservableProperty]
    public partial ImageFormatInfo? ImageFormat { get; set; }

    [ObservableProperty]
    public partial string ImageInfo { get; set; }

    /// <summary>Set by the page once the image has loaded and its pixel size is known.</summary>
    [ObservableProperty]
    public partial string Dimensions { get; set; } = string.Empty;

    public bool HasImage => ImageBytes is not null;

    public bool IsSvg => ImageFormat?.Name == "SVG";

    partial void OnImageBytesChanged(byte[]? value) => OnPropertyChanged(nameof(HasImage));

    partial void OnImageFormatChanged(ImageFormatInfo? value) => OnPropertyChanged(nameof(IsSvg));

    partial void OnDimensionsChanged(string value) => RefreshInfo();

    partial void OnAsDataUriChanged(bool value)
    {
        PersistState();

        // Re-label the text already there rather than leaving the toggle with nothing to do.
        if (ImageBytes is { } bytes && !IsRestoring)
        {
            SetEncoded(bytes, ImageFormat);
        }
    }

    protected override Task RunCoreAsync(CancellationToken token)
    {
        if (_encoding)
        {
            _encoding = false;
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(Input))
        {
            ClearImage();
            ClearMessage();
            return Task.CompletedTask;
        }

        var decoded = Base64Image.Decode(Input);
        if (!decoded.IsSuccess)
        {
            ClearImage();
            SetError(decoded.Error!);
            return Task.CompletedTask;
        }

        Dimensions = string.Empty;
        ImageFormat = decoded.Value.Format;
        ImageBytes = decoded.Value.Data;
        RefreshInfo();
        ClearMessage();
        return Task.CompletedTask;
    }

    private void RefreshInfo() =>
        ImageInfo = ImageBytes is null || ImageFormat is null
            ? string.Empty
            : string.Join(" · ", new[] { ImageFormat.Name, Dimensions, Limits.Describe(ImageBytes.Length) }.Where(static s => s.Length > 0));

    private void ClearImage()
    {
        ImageBytes = null;
        ImageFormat = null;
        Dimensions = string.Empty;
        ImageInfo = string.Empty;
    }

    private void SetEncoded(byte[] bytes, ImageFormatInfo? format)
    {
        format ??= Base64Image.Detect(bytes);
        ImageFormat = format;
        ImageBytes = bytes;
        RefreshInfo();

        // The text is set from the image here, so the decode the text change triggers is skipped.
        _encoding = true;
        Input = Base64Image.Encode(bytes, AsDataUri, format);
    }

    /// <summary>Loads image bytes from anywhere — the file picker, a drop, the clipboard.</summary>
    public void LoadImage(byte[] bytes, string? extension = null)
    {
        var format = Base64Image.Detect(bytes) ?? Base64Image.FromExtension(extension);
        if (format is null)
        {
            SetError("That file is not an image this tool recognises (PNG, JPEG, GIF, BMP, WebP, ICO, TIFF or SVG).");
            return;
        }

        Dimensions = string.Empty;
        SetEncoded(bytes, format);
        SetSuccess($"Encoded a {Limits.Describe(bytes.Length)} {format.Name} image.");
    }

    [RelayCommand]
    private async Task OpenImageAsync()
    {
        var result = await Services.Files.OpenBinaryFileAsync(ImageExtensions);
        if (result.WasCancelled)
        {
            return;
        }

        if (!result.Success || result.Bytes is null)
        {
            SetError(result.Error ?? "The image could not be opened.");
            return;
        }

        LoadImage(result.Bytes, Path.GetExtension(result.FileName));
    }

    [RelayCommand]
    private async Task PasteImageAsync()
    {
        var bytes = await Services.Clipboard.GetImageBytesAsync();
        if (bytes is null)
        {
            SetInfo("The clipboard does not hold an image. Copy one first, or paste Base64 text into the box.");
            return;
        }

        LoadImage(bytes);
    }

    [RelayCommand]
    private async Task SaveImageAsync()
    {
        if (ImageBytes is not { } bytes || ImageFormat is not { } format)
        {
            return;
        }

        var path = await Services.Files.SaveBytesAsync("image" + format.Extension, bytes, format.Extension);
        if (path is not null)
        {
            SetSuccess($"Saved to {path}.");
        }
    }

    [RelayCommand]
    private async Task CopyImageAsync()
    {
        if (ImageBytes is not { } bytes)
        {
            return;
        }

        if (IsSvg)
        {
            Services.Clipboard.SetText(System.Text.Encoding.UTF8.GetString(bytes));
            SetSuccess("Copied the SVG markup.");
            return;
        }

        SetMessage(await Services.Clipboard.SetImageAsync(bytes) ? "Copied the image." : "The clipboard is busy. Try again.",
            ToolMessageSeverity.Informational);
    }

    public override Task SaveOutputAsync() => SaveImageAsync();

    public override void Clear()
    {
        base.Clear();
        ClearImage();
    }

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("dataUri", AsDataUri);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        AsDataUri = state.GetBool("dataUri", true);
    }

    protected override void ResetOptions() => AsDataUri = true;
}
