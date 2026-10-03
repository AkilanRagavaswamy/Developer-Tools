using Windows.ApplicationModel.DataTransfer;

namespace DevTools.App.Services;

public interface IClipboardService
{
    /// <summary>Clipboard text, or null when the clipboard is empty or holds something else.</summary>
    Task<string?> GetTextAsync();

    /// <summary>Clipboard image bytes, or null when the clipboard holds no bitmap.</summary>
    Task<byte[]?> GetImageBytesAsync();

    void SetText(string? text);
}

/// <summary>
/// Clipboard access that never throws at the caller. The Windows clipboard is a shared,
/// contended resource — another process can hold it open — so every operation is guarded
/// and simply reports "nothing available" on failure (edge case 9).
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    public async Task<string?> GetTextAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view is null || !view.Contains(StandardDataFormats.Text))
            {
                return null;
            }

            return await view.GetTextAsync();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<byte[]?> GetImageBytesAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view is null || !view.Contains(StandardDataFormats.Bitmap))
            {
                return null;
            }

            var reference = await view.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();
            var bytes = new byte[stream.Size];
            using var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void SetText(string? text)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text ?? string.Empty);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception)
        {
            // Another process is holding the clipboard; the user can retry.
        }
    }
}
