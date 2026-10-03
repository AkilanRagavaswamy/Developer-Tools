using System.Text;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DevTools.App.Services;

/// <summary>The outcome of a file read: text, or a reason it could not be read.</summary>
public sealed record FileReadResult(bool Success, string? Text, byte[]? Bytes, string? FileName, string? Error)
{
    public static FileReadResult Cancelled() => new(false, null, null, null, null);

    public static FileReadResult Failed(string error) => new(false, null, null, null, error);

    /// <summary>True when the user simply closed the picker — not an error worth showing.</summary>
    public bool WasCancelled => !Success && Error is null;
}

public interface IFileDialogService
{
    Task<FileReadResult> OpenTextFileAsync(params string[] extensions);

    Task<FileReadResult> OpenBinaryFileAsync(params string[] extensions);

    Task<string?> PickFilePathAsync(params string[] extensions);

    Task<string?> SaveTextFileAsync(string suggestedName, string content, params string[] extensions);

    Task<string?> SaveBytesAsync(string suggestedName, byte[] content, params string[] extensions);
}

/// <summary>
/// WinRT pickers wired to the app window. Every path reports a cancelled picker as a
/// no-op and an OS failure with the reason the OS gave (edge cases 10 and 11).
/// </summary>
public sealed class FileDialogService(IShellContext shell) : IFileDialogService
{
    public async Task<FileReadResult> OpenTextFileAsync(params string[] extensions)
    {
        var file = await PickOpenFileAsync(extensions);
        if (file is null)
        {
            return FileReadResult.Cancelled();
        }

        try
        {
            var bytes = await ReadAllBytesAsync(file);
            if (bytes.Length > DevTools.Core.Limits.MaxInputBytes)
            {
                return FileReadResult.Failed(
                    $"{file.Name} is {DevTools.Core.Limits.Describe(bytes.Length)}, which exceeds the " +
                    $"{DevTools.Core.Limits.Describe(DevTools.Core.Limits.MaxInputBytes)} limit for text input.");
            }

            return new FileReadResult(true, DecodeText(bytes), bytes, file.Name, null);
        }
        catch (Exception ex)
        {
            return FileReadResult.Failed($"Could not read {file.Name}: {ex.Message}");
        }
    }

    public async Task<FileReadResult> OpenBinaryFileAsync(params string[] extensions)
    {
        var file = await PickOpenFileAsync(extensions);
        if (file is null)
        {
            return FileReadResult.Cancelled();
        }

        try
        {
            var bytes = await ReadAllBytesAsync(file);
            return new FileReadResult(true, null, bytes, file.Name, null);
        }
        catch (Exception ex)
        {
            return FileReadResult.Failed($"Could not read {file.Name}: {ex.Message}");
        }
    }

    public async Task<string?> PickFilePathAsync(params string[] extensions)
    {
        var file = await PickOpenFileAsync(extensions);
        return file?.Path;
    }

    public async Task<string?> SaveTextFileAsync(string suggestedName, string content, params string[] extensions)
    {
        var file = await PickSaveFileAsync(suggestedName, extensions);
        if (file is null)
        {
            return null;
        }

        try
        {
            await FileIO.WriteTextAsync(file, content ?? string.Empty, Windows.Storage.Streams.UnicodeEncoding.Utf8);
            return file.Path;
        }
        catch (Exception ex)
        {
            throw new IOException($"Could not write {file.Name}: {ex.Message}", ex);
        }
    }

    public async Task<string?> SaveBytesAsync(string suggestedName, byte[] content, params string[] extensions)
    {
        var file = await PickSaveFileAsync(suggestedName, extensions);
        if (file is null)
        {
            return null;
        }

        try
        {
            await FileIO.WriteBytesAsync(file, content ?? []);
            return file.Path;
        }
        catch (Exception ex)
        {
            throw new IOException($"Could not write {file.Name}: {ex.Message}", ex);
        }
    }

    private async Task<StorageFile?> PickOpenFileAsync(string[] extensions)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, shell.WindowHandle);

            if (extensions.Length == 0)
            {
                picker.FileTypeFilter.Add("*");
            }
            else
            {
                foreach (var extension in extensions)
                {
                    picker.FileTypeFilter.Add(extension);
                }
            }

            return await picker.PickSingleFileAsync();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<StorageFile?> PickSaveFileAsync(string suggestedName, string[] extensions)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = string.IsNullOrWhiteSpace(suggestedName) ? "devtools-output" : suggestedName,
            };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, shell.WindowHandle);

            if (extensions.Length == 0)
            {
                picker.FileTypeChoices.Add("Text file", [".txt"]);
            }
            else
            {
                foreach (var extension in extensions)
                {
                    picker.FileTypeChoices.Add(DescribeExtension(extension), [extension]);
                }
            }

            return await picker.PickSaveFileAsync();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(StorageFile file)
    {
        var buffer = await FileIO.ReadBufferAsync(file);
        var bytes = new byte[buffer.Length];
        using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Sniffs the byte-order mark so a UTF-16 or UTF-32 file opens as readable text rather
    /// than as a field of NUL characters, and falls back to UTF-8 with replacement.
    /// </summary>
    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true).GetString(bytes, 4, bytes.Length - 4);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(bytes);
    }

    private static string DescribeExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" => "Text file",
        ".json" => "JSON file",
        ".xml" => "XML file",
        ".yaml" or ".yml" => "YAML file",
        ".csv" => "CSV file",
        ".sql" => "SQL file",
        ".md" => "Markdown file",
        ".html" or ".htm" => "HTML file",
        ".png" => "PNG image",
        ".jpg" or ".jpeg" => "JPEG image",
        ".bin" => "Binary file",
        ".pem" or ".crt" or ".cer" => "Certificate",
        _ => $"{extension.TrimStart('.').ToUpperInvariant()} file",
    };
}
