using System.Runtime.InteropServices;
using System.Security.Principal;
using DevTools.Core;
using Microsoft.Win32;

namespace DevTools.Http.Capture;

/// <summary>What the Windows proxy setting looked like before capture started.</summary>
public sealed record ProxyState(bool Enabled, string? Server, string? Override);

/// <summary>
/// Points the Windows proxy setting at the capture proxy, and puts it back.
/// </summary>
/// <remarks>
/// <para>
/// The setting lives under <c>HKCU</c>, so it is per user and affects every program that reads
/// it — there is no per-process equivalent. That is the single most important thing about this
/// tool and the UI says so in plain words rather than implying per-app capture.
/// </para>
/// <para>
/// Reaching it from inside an MSIX package is the awkward part, and the answer is that you
/// cannot: a packaged process has its registry writes redirected into the package's own hive,
/// through <c>HKEY_CURRENT_USER</c> and <c>HKEY_USERS</c> alike. The write succeeds, a read-back
/// agrees, and nothing else on the machine has changed. <see cref="CanChangeSystemSetting"/>
/// says so up front rather than letting the capture sit there listening to silence.
/// </para>
/// <para>
/// Restoring on a clean stop is easy. The hard case is a crash, so the caller writes the state
/// this returns to disk before changing anything and puts it back at the next launch.
/// </para>
/// </remarks>
public static class SystemProxy
{
    private const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    /// <summary>
    /// Whether this process can actually change the machine's proxy setting.
    /// </summary>
    /// <remarks>
    /// A packaged (MSIX) process cannot. Its registry writes are redirected into the package's
    /// own hive: the write succeeds, a read-back agrees, and nothing else on the machine sees
    /// any change — including reads through <c>HKEY_USERS</c>, which is redirected too. There is
    /// no way to tell the difference by reading, so the honest thing is to ask whether we are
    /// packaged and not attempt a change that cannot work. The capture proxy still captures
    /// everything pointed at it, which is what the UI then explains how to do.
    /// </remarks>
    public static bool CanChangeSystemSetting => !IsPackaged();

    private static bool IsPackaged()
    {
        try
        {
            var length = 0;

            // 15700L is APPMODEL_ERROR_NO_PACKAGE: no package identity, so nothing is redirected.
            return GetCurrentPackageFullName(ref length, null) != 15700;
        }
        catch (Exception)
        {
            // If the API is unavailable the process is certainly not packaged.
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, System.Text.StringBuilder? fullName);

    /// <summary>Reads the current setting, so it can be restored exactly.</summary>
    public static ProxyState Capture()
    {
        using var key = OpenSettings(writable: false);

        if (key is null)
        {
            return new ProxyState(false, null, null);
        }

        try
        {
            var enabled = key.GetValue("ProxyEnable") is int flag && flag != 0;
            return new ProxyState(enabled, key.GetValue("ProxyServer") as string, key.GetValue("ProxyOverride") as string);
        }
        catch (Exception)
        {
            return new ProxyState(false, null, null);
        }
    }

    /// <summary>Points the setting at <c>127.0.0.1:port</c>.</summary>
    public static OperationResult<bool> PointAt(int port)
    {
        var server = $"127.0.0.1:{port}";

        try
        {
            using var key = OpenSettings(writable: true);

            if (key is null)
            {
                return OperationResult<bool>.Fail("The Windows proxy setting could not be opened for writing.");
            }

            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", server, RegistryValueKind.String);

            // The proxy itself must not be proxied, and neither should anything else on the
            // loopback, or a local service would see its own traffic bounce through us.
            key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);

            Notify();
        }
        catch (Exception ex)
        {
            return OperationResult<bool>.Fail($"The Windows proxy setting could not be changed: {ex.Message}");
        }

        // Read it back. This is what tells a change that took from one that went into a private
        // hive nothing else reads, which is otherwise indistinguishable from a quiet network.
        var applied = Capture();

        if (!applied.Enabled || !string.Equals(applied.Server, server, StringComparison.Ordinal))
        {
            return OperationResult<bool>.Fail(
                "Windows still reports no proxy after the change. Point the app you want to watch at " +
                $"{server} itself — through its own settings, or by starting it with HTTP_PROXY and " +
                $"HTTPS_PROXY set to http://{server}.");
        }

        return OperationResult<bool>.Ok(true);
    }

    /// <summary>Puts the setting back exactly as <see cref="Capture"/> found it.</summary>
    public static OperationResult<bool> Restore(ProxyState state)
    {
        try
        {
            using var key = OpenSettings(writable: true);

            if (key is null)
            {
                return OperationResult<bool>.Fail("The Windows proxy setting could not be opened for writing.");
            }

            key.SetValue("ProxyEnable", state.Enabled ? 1 : 0, RegistryValueKind.DWord);

            Put("ProxyServer", state.Server);
            Put("ProxyOverride", state.Override);

            Notify();
            return OperationResult<bool>.Ok(true);

            void Put(string name, string? value)
            {
                if (value is null)
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue(name, value, RegistryValueKind.String);
                }
            }
        }
        catch (Exception ex)
        {
            return OperationResult<bool>.Fail($"The Windows proxy setting could not be restored: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the Internet Settings key, preferring the path that reaches the real one.
    /// </summary>
    /// <remarks>
    /// <c>HKEY_USERS\&lt;sid&gt;</c> is the same key as <c>HKEY_CURRENT_USER</c> for this user.
    /// Either path works for an unpackaged process, and neither escapes the redirection applied
    /// to a packaged one, which is why <see cref="CanChangeSystemSetting"/> is checked first.
    /// </remarks>
    private static RegistryKey? OpenSettings(bool writable)
    {
        try
        {
            if (WindowsIdentity.GetCurrent().User?.Value is { Length: > 0 } sid)
            {
                var key = Registry.Users.OpenSubKey($@"{sid}\{SettingsKey}", writable);

                if (key is not null)
                {
                    return key;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the ordinary path below.
        }

        try
        {
            return Registry.CurrentUser.OpenSubKey(SettingsKey, writable)
                ?? (writable ? Registry.CurrentUser.CreateSubKey(SettingsKey) : null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Tells WinINet the setting changed. Without this, programs that cached it at start-up go
    /// on using the old value, and the capture looks broken for no visible reason.
    /// </summary>
    private static void Notify()
    {
        try
        {
            _ = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            _ = InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        catch (Exception)
        {
            // Best effort: the setting is written either way.
        }
    }

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int bufferLength);
}
