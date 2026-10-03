using System.Reflection;
using System.Runtime.Loader;

/// <summary>
/// The entry point the .NET runtime calls when a process is started with
/// <c>DOTNET_STARTUP_HOOKS</c> pointing at this assembly. The type name and signature are fixed
/// by the runtime: a parameterless, non-namespaced <c>StartupHook.Initialize()</c>.
/// </summary>
/// <remarks>
/// This runs before the target application's <c>Main</c>. It does the least it can here — wire up
/// assembly resolution, then hand off — because the one rule of a startup hook is that it must
/// not stop the host from starting. Every path is wrapped so a failure in the agent leaves the
/// application exactly as it would have run without it.
/// </remarks>
internal static class StartupHook
{
    public static void Initialize()
    {
        try
        {
            // Registered before any Harmony type is touched. The host app has its own dependency
            // set and knows nothing of 0Harmony.dll, so the agent resolves its own dependencies
            // from its own folder; the JIT only needs them once Start's body runs, by which point
            // this handler is in place.
            AssemblyLoadContext.Default.Resolving += ResolveFromAgentFolder;

            DevTools.Profiler.Agent.AgentBootstrap.Start();
        }
        catch (Exception)
        {
            // A profiler that cannot start is not a reason for the application not to.
        }
    }

    private static Assembly? ResolveFromAgentFolder(AssemblyLoadContext context, AssemblyName name)
    {
        try
        {
            var folder = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);
            if (string.IsNullOrEmpty(folder))
            {
                return null;
            }

            var candidate = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
