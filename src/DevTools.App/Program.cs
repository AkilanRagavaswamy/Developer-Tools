using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace DevTools.App;

/// <summary>
/// The entry point, written by hand so DevTools can be single-instanced.
/// </summary>
/// <remarks>
/// <para>
/// Without this, every <c>devtools://tool/&lt;id&gt;</c> URI starts another copy of the app:
/// <see cref="AppInstance.GetCurrent"/>'s <c>Activated</c> event only fires for activations
/// that were <em>redirected</em> to this process, and nothing redirects them by default.
/// </para>
/// <para>
/// A second copy is not merely untidy. Each one would own its own settings, favourites, tool
/// state and API workspace, and the last one to write would silently win — so a URI shortcut
/// could quietly discard a collection the user had just edited.
/// </para>
/// <para>
/// Enabled by <c>DISABLE_XAML_GENERATED_MAIN</c> in the project file, which suppresses the
/// <c>Main</c> the XAML compiler would otherwise generate.
/// </para>
/// </remarks>
public static class Program
{
    private const string InstanceKey = "DevTools.ApiWorkbench.Main";

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (TryRedirectToRunningInstance())
        {
            return 0;
        }

        Application.Start(parameters =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
            _ = new App();
        });

        return 0;
    }

    /// <summary>
    /// Hands this activation to the instance that is already running, if there is one.
    /// </summary>
    /// <returns><see langword="true"/> when this process should exit immediately.</returns>
    private static bool TryRedirectToRunningInstance()
    {
        try
        {
            var main = AppInstance.FindOrRegisterForKey(InstanceKey);

            if (main.IsCurrent)
            {
                return false;
            }

            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();

            // RedirectActivationToAsync must not be awaited on this thread: it needs the COM
            // apartment to keep pumping, and blocking the STA here would deadlock it.
            using var redirected = new SemaphoreSlim(0, 1);

            _ = Task.Run(async () =>
            {
                try
                {
                    await main.RedirectActivationToAsync(activation);
                }
                finally
                {
                    redirected.Release();
                }
            });

            redirected.Wait(TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception)
        {
            // Unpackaged, or the app-lifecycle API is unavailable. Running as a second instance
            // is a far better outcome than failing to start at all.
            return false;
        }
    }
}
