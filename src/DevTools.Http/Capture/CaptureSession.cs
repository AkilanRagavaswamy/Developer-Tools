using DevTools.Core;

namespace DevTools.Http.Capture;

/// <summary>What a capture needs to know before it starts, remembered between sessions.</summary>
public sealed record CaptureSettings
{
    /// <summary>The port to try first. Zero lets Windows choose.</summary>
    public int Port { get; init; } = 8899;

    /// <summary>The thumbprint of the root installed by an earlier run, if there was one.</summary>
    public string? CertificateThumbprint { get; init; }
}

/// <summary>Where a capture is in its lifecycle.</summary>
public enum CaptureState
{
    Stopped,
    Running,
}

/// <summary>How a capture gets at an application's calls.</summary>
public enum CaptureBackend
{
    /// <summary>A local proxy the machine is pointed at. Sees any app, needs a certificate for https.</summary>
    Proxy,

    /// <summary>The profiler agent, loaded into a .NET app DevTools launches. Sees https in the clear.</summary>
    Launch,
}

/// <summary>
/// Runs a capture: the proxy, the Windows proxy setting, and putting the setting back.
/// </summary>
/// <remarks>
/// Everything this touches outside the process is reversible and is reversed on
/// <see cref="StopAsync"/>. The one thing it deliberately does not undo is the trusted root —
/// installing that is a decision the user made once, and silently removing it would mean asking
/// again every session. <see cref="RemoveCertificate"/> is there for when they want it gone.
/// </remarks>
public sealed class CaptureSession : IDisposable
{
    private readonly object _gate = new();

    private CaptureProxy? _proxy;
    private LaunchProfiler? _launch;
    private CaptureCertificates? _certificates;
    private ProxyState? _previousProxy;
    private bool _disposed;

    /// <summary>Raised on a thread-pool thread for each completed exchange.</summary>
    public event EventHandler<CapturedExchange>? Captured;

    public CaptureState State { get; private set; } = CaptureState.Stopped;

    /// <summary>Which backend is running, meaningful only while <see cref="State"/> is Running.</summary>
    public CaptureBackend Backend { get; private set; } = CaptureBackend.Proxy;

    public int Port { get; private set; }

    /// <summary>
    /// Whether the capture is routing traffic by itself, or waiting for an app to be pointed at
    /// it. False inside an MSIX package, which cannot change the machine's proxy setting.
    /// </summary>
    public bool RoutesAutomatically { get; private set; }

    /// <summary>True when https bodies can be read, rather than only tunnelled.</summary>
    public bool CanDecryptTls => _certificates is not null && _certificates.IsTrusted();

    public string? CertificateThumbprint => _certificates?.Thumbprint;

    /// <summary>
    /// Loads the root recorded by an earlier session. Returns false when there is none, or when
    /// the one recorded has since been removed from Windows.
    /// </summary>
    public bool LoadCertificate(string? thumbprint)
    {
        var loaded = CaptureCertificates.Load(thumbprint);

        if (loaded is null)
        {
            return false;
        }

        _certificates?.Dispose();
        _certificates = loaded;
        return true;
    }

    /// <summary>
    /// Creates a root and asks Windows to trust it for this user. Windows shows its own
    /// confirmation; this returns the thumbprint to record so the next session reuses it.
    /// </summary>
    public OperationResult<string> InstallCertificate()
    {
        var created = CaptureCertificates.Create();

        if (!created.IsSuccess)
        {
            return OperationResult<string>.Fail(created.Error!);
        }

        var certificates = created.Value!;
        var trusted = certificates.Trust();

        if (!trusted.IsSuccess)
        {
            certificates.Remove();
            certificates.Dispose();
            return OperationResult<string>.Fail(trusted.Error!);
        }

        _certificates?.Dispose();
        _certificates = certificates;

        return OperationResult<string>.Ok(certificates.Thumbprint);
    }

    /// <summary>Removes the root from Windows, leaving the machine as it was found.</summary>
    public OperationResult<bool> RemoveCertificate()
    {
        if (_certificates is null)
        {
            return OperationResult<bool>.Ok(true);
        }

        var result = _certificates.Remove();
        _certificates.Dispose();
        _certificates = null;
        return result;
    }

    /// <summary>
    /// Starts the proxy and points Windows at it. If pointing Windows at it fails, the proxy is
    /// stopped again rather than left listening to nothing.
    /// </summary>
    public OperationResult<int> Start(CaptureSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (State == CaptureState.Running)
            {
                return OperationResult<int>.Ok(Port);
            }

            var proxy = new CaptureProxy(_certificates);
            proxy.Captured += OnCaptured;

            try
            {
                Port = proxy.Start(settings.Port);
            }
            catch (Exception ex)
            {
                proxy.Captured -= OnCaptured;
                proxy.Dispose();
                return OperationResult<int>.Fail($"The capture proxy could not start: {ex.Message}");
            }

            // A packaged process cannot change the machine's proxy setting, so it does not try:
            // writing into its own private hive would look like success and capture nothing. The
            // proxy still listens, and the UI says what to point at it.
            if (SystemProxy.CanChangeSystemSetting)
            {
                _previousProxy = SystemProxy.Capture();
                var pointed = SystemProxy.PointAt(Port);

                if (!pointed.IsSuccess)
                {
                    proxy.Captured -= OnCaptured;
                    proxy.Dispose();
                    _previousProxy = null;
                    return OperationResult<int>.Fail(pointed.Error!);
                }

                RoutesAutomatically = true;
            }
            else
            {
                RoutesAutomatically = false;
            }

            _proxy = proxy;
            Backend = CaptureBackend.Proxy;
            State = CaptureState.Running;

            return OperationResult<int>.Ok(Port);
        }
    }

    /// <summary>
    /// Starts a launch-and-attach capture: runs the given application with the profiler agent
    /// loaded and reports the calls it makes. Nothing on the machine is changed, and https bodies
    /// are readable without a certificate because the agent reads them inside the process.
    /// </summary>
    public OperationResult<LaunchInfo> StartLaunch(string executablePath, string? arguments, string? workingDirectory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (State == CaptureState.Running)
            {
                return OperationResult<LaunchInfo>.Fail("Stop the current capture before launching an application.");
            }

            var launch = new LaunchProfiler();
            launch.Captured += OnCaptured;

            var started = launch.Start(executablePath, arguments, workingDirectory);
            if (!started.IsSuccess)
            {
                launch.Captured -= OnCaptured;
                launch.Dispose();
                return started;
            }

            _launch = launch;
            Backend = CaptureBackend.Launch;
            RoutesAutomatically = true;
            State = CaptureState.Running;

            return started;
        }
    }

    /// <summary>
    /// Stops the proxy and puts the Windows setting back. Safe to call when already stopped,
    /// which is what makes it usable from a shutdown path that cannot know the state.
    /// </summary>
    public OperationResult<bool> Stop()
    {
        lock (_gate)
        {
            if (_launch is { } launch)
            {
                launch.Captured -= OnCaptured;
                launch.Dispose();
                _launch = null;
                State = CaptureState.Stopped;
                return OperationResult<bool>.Ok(true);
            }

            if (State == CaptureState.Stopped && _previousProxy is null)
            {
                return OperationResult<bool>.Ok(true);
            }

            // The setting goes back first. If stopping the proxy were to throw, the machine
            // would otherwise be left pointing at a port that is no longer listening.
            var restored = _previousProxy is null
                ? OperationResult<bool>.Ok(true)
                : SystemProxy.Restore(_previousProxy);

            _previousProxy = null;

            if (_proxy is { } proxy)
            {
                proxy.Captured -= OnCaptured;
                proxy.Dispose();
                _proxy = null;
            }

            State = CaptureState.Stopped;

            return restored;
        }
    }

    private void OnCaptured(object? sender, CapturedExchange exchange) => Captured?.Invoke(this, exchange);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Stop();
        _certificates?.Dispose();
        _certificates = null;
    }
}
