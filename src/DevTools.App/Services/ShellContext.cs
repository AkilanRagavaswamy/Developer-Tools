using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DevTools.App.Services;

/// <summary>
/// The pieces of the live window that services need but must not own: the HWND for
/// WinRT picker interop, the <see cref="XamlRoot"/> for dialogs, and the UI dispatcher.
/// Populated once the main window exists.
/// </summary>
public interface IShellContext
{
    nint WindowHandle { get; }

    XamlRoot? XamlRoot { get; }

    DispatcherQueue? DispatcherQueue { get; }

    Window? Window { get; }

    void Attach(Window window, nint handle);

    void SetXamlRoot(XamlRoot? xamlRoot);

    /// <summary>Runs <paramref name="action"/> on the UI thread, whichever thread calls it.</summary>
    void Post(Action action);
}

public sealed class ShellContext : IShellContext
{
    public nint WindowHandle { get; private set; }

    public XamlRoot? XamlRoot { get; private set; }

    public DispatcherQueue? DispatcherQueue { get; private set; }

    public Window? Window { get; private set; }

    public void Attach(Window window, nint handle)
    {
        Window = window;
        WindowHandle = handle;
        DispatcherQueue = window.DispatcherQueue;
    }

    public void SetXamlRoot(XamlRoot? xamlRoot) => XamlRoot = xamlRoot;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var queue = DispatcherQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            action();
            return;
        }

        queue.TryEnqueue(() => action());
    }
}
