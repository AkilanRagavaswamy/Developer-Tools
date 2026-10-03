using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using DevTools.Core;

namespace DevTools.Http.Capture;

/// <summary>One header, as it arrives from the agent. Mirror of the agent's own type.</summary>
internal sealed class WireHeader
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// One exchange as it arrives from the in-process agent over the pipe.
/// </summary>
/// <remarks>
/// A byte-for-byte mirror of <c>DevTools.Profiler.Agent.WireExchange</c>, kept in step by property
/// name. The two assemblies cannot share a type — the agent targets a plain framework and loads
/// into other people's processes — so the shape is duplicated rather than referenced.
/// </remarks>
internal sealed class WireExchange
{
    public long StartedAtUnixMs { get; set; }

    public string Method { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public string PathAndQuery { get; set; } = string.Empty;

    public string HttpVersion { get; set; } = "1.1";

    public bool IsSecure { get; set; }

    public WireHeader[] RequestHeaders { get; set; } = [];

    public byte[] RequestBody { get; set; } = [];

    public int StatusCode { get; set; }

    public string ReasonPhrase { get; set; } = string.Empty;

    public WireHeader[] ResponseHeaders { get; set; } = [];

    public byte[] ResponseBody { get; set; } = [];

    public string? ResponseMediaType { get; set; }

    public double DurationMs { get; set; }

    public int ProcessId { get; set; }

    public string ProcessName { get; set; } = string.Empty;

    public bool Failed { get; set; }

    public string? Error { get; set; }
}

/// <summary>What a launch produced: the process it started.</summary>
public sealed record LaunchInfo(int ProcessId, string ProcessName);

/// <summary>
/// The launch-and-attach capture backend: start a .NET application with the profiler agent loaded,
/// and read the calls it reports over a private named pipe.
/// </summary>
/// <remarks>
/// This is the half that does not touch the machine at all — no proxy, no certificate, no
/// system-wide setting. The cost is that it only sees a process DevTools itself started, and only
/// a .NET one, since the agent rides in through <c>DOTNET_STARTUP_HOOKS</c>. The launched app is
/// left running when capture stops; it is the user's program, not ours to kill.
/// </remarks>
public sealed class LaunchProfiler : IDisposable
{
    private readonly object _gate = new();
    private int _index;

    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>Raised on a thread-pool thread for each exchange the agent reports.</summary>
    public event EventHandler<CapturedExchange>? Captured;

    /// <summary>The agent assembly shipped next to the app, or <see langword="null"/> when missing.</summary>
    public static string? ResolveAgentPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Profiler", "DevTools.Profiler.Agent.dll");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Launches <paramref name="executablePath"/> with the agent attached and starts reading its
    /// reported calls. The process keeps running after <see cref="Stop"/>; this only stops reading.
    /// </summary>
    public OperationResult<LaunchInfo> Start(string executablePath, string? arguments, string? workingDirectory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return OperationResult<LaunchInfo>.Fail("Choose an application to launch first.");
        }

        var agentPath = ResolveAgentPath();
        if (agentPath is null)
        {
            return OperationResult<LaunchInfo>.Fail(
                "The profiler agent was not found next to DevTools. Rebuild the app so it is copied into the Profiler folder.");
        }

        lock (_gate)
        {
            if (_process is not null)
            {
                return OperationResult<LaunchInfo>.Fail("A launched session is already running.");
            }

            var pipeName = "devtools-profiler-" + Guid.NewGuid().ToString("N");

            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Path.GetDirectoryName(executablePath) ?? string.Empty
                    : workingDirectory,
            };

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                startInfo.Arguments = arguments;
            }

            startInfo.Environment["DOTNET_STARTUP_HOOKS"] = agentPath;
            startInfo.Environment["DEVTOOLS_PROFILER_PIPE"] = pipeName;

            Process process;
            try
            {
                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("The process did not start.");
            }
            catch (Exception ex)
            {
                pipe.Dispose();
                return OperationResult<LaunchInfo>.Fail($"Could not launch the application: {ex.Message}");
            }

            _pipe = pipe;
            _process = process;
            _cts = new CancellationTokenSource();

            var token = _cts.Token;
            _ = Task.Run(() => ReadLoopAsync(pipe, token), token);

            var name = SafeProcessName(process);
            return OperationResult<LaunchInfo>.Ok(new LaunchInfo(process.Id, name));
        }
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);

            var header = new byte[4];

            while (!token.IsCancellationRequested)
            {
                await pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);

                var length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
                if (length is <= 0 or > 128 * 1024 * 1024)
                {
                    break;
                }

                var payload = new byte[length];
                await pipe.ReadExactlyAsync(payload, token).ConfigureAwait(false);

                var wire = JsonSerializer.Deserialize<WireExchange>(payload);
                if (wire is not null)
                {
                    Captured?.Invoke(this, Map(wire, Interlocked.Increment(ref _index)));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop was called; nothing to report.
        }
        catch (EndOfStreamException)
        {
            // The agent's process ended, or it closed the pipe. Reading is simply done.
        }
        catch (IOException)
        {
            // The pipe broke, usually because the launched process exited. Done.
        }
        catch (Exception)
        {
            // Any other read failure ends the loop rather than taking the app down.
        }
    }

    /// <summary>
    /// Parses one pipe payload into an exchange. Internal so the wire contract can be asserted
    /// directly, which is the parity that most easily drifts between the two assemblies.
    /// </summary>
    internal static CapturedExchange Parse(byte[] json, int index) =>
        Map(JsonSerializer.Deserialize<WireExchange>(json) ?? new WireExchange(), index);

    private static CapturedExchange Map(WireExchange wire, int index) => new()
    {
        Index = index,
        StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(wire.StartedAtUnixMs).ToLocalTime(),
        Method = wire.Method,
        Url = wire.Url,
        Host = wire.Host,
        PathAndQuery = wire.PathAndQuery,
        HttpVersion = wire.HttpVersion,
        IsSecure = wire.IsSecure,
        RequestHeaders = [.. wire.RequestHeaders.Select(h => new CapturedHeader(h.Name, h.Value))],
        RequestBody = wire.RequestBody,
        StatusCode = wire.StatusCode,
        ReasonPhrase = wire.ReasonPhrase,
        ResponseHeaders = [.. wire.ResponseHeaders.Select(h => new CapturedHeader(h.Name, h.Value))],
        ResponseBody = wire.ResponseBody,
        ResponseMediaType = wire.ResponseMediaType,
        Duration = TimeSpan.FromMilliseconds(wire.DurationMs),
        ProcessId = wire.ProcessId,
        ProcessName = wire.ProcessName,
        Outcome = wire.Failed ? CaptureOutcome.Failed : CaptureOutcome.Complete,
        Error = wire.Error,
    };

    private static string SafeProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (Exception)
        {
            return Path.GetFileNameWithoutExtension(process.StartInfo.FileName);
        }
    }

    /// <summary>Stops reading. The launched application is left running.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            _pipe?.Dispose();
            _pipe = null;

            _process?.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
