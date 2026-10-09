using System.Text.Json;
using Windows.Storage;

namespace DevTools.App.Services;

/// <summary>
/// Atomic JSON persistence under the packaged app's local folder. Writes go to a temp file
/// and are then moved over the target, so a crash mid-write cannot leave a half-written
/// file behind; reads treat any failure as "no data yet" rather than propagating.
/// </summary>
public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>The app's writable data root, falling back to LocalAppData when unpackaged.</summary>
    public static string RootPath
    {
        get
        {
            try
            {
                return ApplicationData.Current.LocalFolder.Path;
            }
            catch (Exception)
            {
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ForgeKitRk");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }
    }

    public static string PathFor(string relativePath) => Path.Combine(RootPath, relativePath);

    public static async Task<T?> LoadAsync<T>(string relativePath)
    {
        var path = PathFor(relativePath);

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return default;
            }

            // ConfigureAwait on the disposal too: a small file is read synchronously, so without it
            // the close of the stream would resume on the caller's thread — the UI thread — and a
            // caller blocking that thread on this task (the app closing) would wait on itself.
            var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<T>(stream, Options).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A corrupt or unreadable state file must never stop the app: start fresh.
            return default;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task SaveAsync<T>(string relativePath, T value)
    {
        var path = PathFor(relativePath);

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            // The disposal flushes the file and is awaited too, so it needs ConfigureAwait as well:
            // see LoadAsync. This one was the close-time deadlock — a small index serialises
            // without yielding, and the flush then tried to resume on the blocked UI thread.
            var stream = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Persistence is a convenience, never a reason to interrupt the user.
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Delete(string relativePath)
    {
        try
        {
            var path = PathFor(relativePath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    public static void DeleteDirectory(string relativeDirectory)
    {
        try
        {
            var path = PathFor(relativeDirectory);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
