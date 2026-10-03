using System.Diagnostics;
using HarmonyLib;

namespace DevTools.Profiler.Agent;

/// <summary>
/// Sets the agent going once assembly resolution is in place: connect the pipe, then patch
/// <see cref="System.Net.Http.HttpClient"/> so every call made by the host is reported.
/// </summary>
/// <remarks>
/// Kept separate from the startup hook so that the first time any Harmony type is referenced is
/// inside this method, after the resolver that can load <c>0Harmony.dll</c> has been registered.
/// </remarks>
internal static class AgentBootstrap
{
    /// <summary>The environment variable DevTools sets to the name of the pipe to report on.</summary>
    public const string PipeVariable = "DEVTOOLS_PROFILER_PIPE";

    private static ExchangeChannel? _channel;
    private static int _processId;
    private static string _processName = string.Empty;

    public static void Start()
    {
        var pipeName = Environment.GetEnvironmentVariable(PipeVariable);
        if (string.IsNullOrEmpty(pipeName))
        {
            // Not launched by the profiler. Do nothing at all.
            return;
        }

        // Cleared immediately so child processes this app launches do not inherit the hook and
        // report themselves as the target too.
        Environment.SetEnvironmentVariable(PipeVariable, null);
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);

        _channel = ExchangeChannel.TryConnect(pipeName);
        if (_channel is null)
        {
            return;
        }

        using (var self = Process.GetCurrentProcess())
        {
            _processId = self.Id;
            _processName = self.ProcessName;
        }

        var harmony = new Harmony("com.devtools.profiler.agent");
        HttpClientPatch.Apply(harmony);
    }

    /// <summary>Reports one completed exchange. Called from the patch, on the app's own threads.</summary>
    public static void Report(WireExchange exchange)
    {
        exchange.ProcessId = _processId;
        exchange.ProcessName = _processName;
        _channel?.Send(exchange);
    }
}
