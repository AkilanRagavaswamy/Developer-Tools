using Microsoft.UI.Dispatching;

namespace DevTools.App.Services;

/// <summary>
/// Puts work back on the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// The services that load state from disk await the file with <c>ConfigureAwait(false)</c> and
/// then raise their <c>Changed</c> event, so a handler runs on whichever thread-pool thread
/// finished the read. When that handler is a view model raising a property change, the compiled
/// bindings update on that thread — and the first binding that needs a converter asks
/// <c>Application.Current.Resources</c> for it, a call marshalled to the UI thread. Off-thread
/// it fails with <c>RPC_E_WRONGTHREAD</c>, which during start-up takes the shell down before a
/// single tool has loaded.
/// </para>
/// <para>
/// The alternative — dropping <c>ConfigureAwait(false)</c> in the services — would make a file
/// reader's correctness depend on who happens to be listening. This way the rule lives with the
/// code that has the UI thread affinity, which is the code that actually needs it.
/// </para>
/// </remarks>
public static class UiDispatcher
{
    private static DispatcherQueue? _queue;

    /// <summary>Called once, from the UI thread, as the app starts.</summary>
    public static void Initialize() => _queue ??= DispatcherQueue.GetForCurrentThread();

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread, immediately if already there.
    /// </summary>
    /// <remarks>
    /// Running inline when the caller is already on the UI thread matters: queueing instead
    /// would defer every ordinary notification by a frame, and the order in which a view model
    /// raises its changes is sometimes the order the UI depends on.
    /// </remarks>
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var queue = _queue;

        if (queue is null || queue.HasThreadAccess)
        {
            action();
            return;
        }

        queue.TryEnqueue(() => action());
    }
}
