using DevTools.Http.Capture;

namespace DevTools.App.Services;

/// <summary>What the profiler remembers about capture between sessions.</summary>
public sealed record CaptureConfig
{
    /// <summary>The port the capture proxy asks for. Kept so the same one is reused.</summary>
    public int Port { get; init; } = 8899;

    /// <summary>The root certificate installed once, found again by thumbprint.</summary>
    public string? CertificateThumbprint { get; init; }

    /// <summary>
    /// The Windows proxy setting as it was before a capture started, written while a capture is
    /// running and cleared when it stops cleanly.
    /// </summary>
    /// <remarks>
    /// This is the crash watchdog. Restoring the setting on a clean stop is easy; the case that
    /// matters is the process dying with the setting still pointed at a port that no longer
    /// listens, which leaves the whole machine unable to reach the internet. Writing the old
    /// value down before changing it means the next launch can put it back.
    /// </remarks>
    public PendingRestore? Restore { get; init; }
}

/// <summary>The proxy setting to put back if DevTools did not get the chance.</summary>
public sealed record PendingRestore(bool Enabled, string? Server, string? Override);

public interface ICaptureConfigService
{
    CaptureConfig Current { get; }

    Task LoadAsync();

    /// <summary>Records the port and thumbprint so the next session reuses them.</summary>
    Task RememberAsync(int port, string? thumbprint);

    /// <summary>Notes the setting to restore, before a capture changes it.</summary>
    Task ArmRestoreAsync(ProxyState state);

    /// <summary>Clears the note after a clean stop.</summary>
    Task DisarmRestoreAsync();

    /// <summary>
    /// Puts the Windows proxy setting back if a previous run left it changed. Returns what was
    /// restored, or <see langword="null"/> when there was nothing to do.
    /// </summary>
    Task<ProxyState?> RecoverAsync();
}

public sealed class CaptureConfigService : ICaptureConfigService
{
    private const string FileName = "capture.json";

    public CaptureConfig Current { get; private set; } = new();

    public async Task LoadAsync() => Current = await JsonStore.LoadAsync<CaptureConfig>(FileName) ?? new CaptureConfig();

    public Task RememberAsync(int port, string? thumbprint)
    {
        Current = Current with { Port = port, CertificateThumbprint = thumbprint };
        return JsonStore.SaveAsync(FileName, Current);
    }

    public Task ArmRestoreAsync(ProxyState state)
    {
        Current = Current with { Restore = new PendingRestore(state.Enabled, state.Server, state.Override) };
        return JsonStore.SaveAsync(FileName, Current);
    }

    public Task DisarmRestoreAsync()
    {
        if (Current.Restore is null)
        {
            return Task.CompletedTask;
        }

        Current = Current with { Restore = null };
        return JsonStore.SaveAsync(FileName, Current);
    }

    public async Task<ProxyState?> RecoverAsync()
    {
        if (Current.Restore is not { } pending)
        {
            return null;
        }

        var state = new ProxyState(pending.Enabled, pending.Server, pending.Override);
        SystemProxy.Restore(state);

        await DisarmRestoreAsync();
        return state;
    }
}
