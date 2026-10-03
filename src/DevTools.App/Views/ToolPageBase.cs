using DevTools.App.Services;
using DevTools.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace DevTools.App.Views;

/// <summary>
/// The base every tool page derives from. It ties the page lifecycle to the view model —
/// activate on arrival, cancel and persist on departure — publishes the view model to the
/// title bar, and installs the per-tool keyboard accelerators (FR-S13).
/// </summary>
public partial class ToolPageBase : Page
{
    private readonly IToolChrome _chrome = App.GetService<IToolChrome>();

    protected ToolPageBase()
    {
        NavigationCacheMode = NavigationCacheMode.Disabled;

        // The accelerators belong to the whole page, so WinUI's automatic "Ctrl+Enter" tooltip
        // would pop up wherever the pointer rests. The buttons carry their own tooltips.
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        AddAccelerator(VirtualKey.Enter, VirtualKeyModifiers.Control, () => Tool?.RunCommand.Execute(null));
        AddAccelerator(VirtualKey.L, VirtualKeyModifiers.Control, () => Tool?.ClearCommand.Execute(null));
        AddAccelerator(VirtualKey.D, VirtualKeyModifiers.Control, () => Tool?.ToggleFavoriteCommand.Execute(null));
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control, () => Tool?.SaveOutputCommand.Execute(null));

        // Escape stops a running job. Only the job tools have one, so it is a no-op elsewhere.
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, () =>
        {
            if (Tool is JobToolViewModelBase { IsRunning: true } job)
            {
                job.StopCommand.Execute(null);
            }
        });
    }

    /// <summary>Set by the derived page once it has resolved its view model.</summary>
    protected ToolViewModelBase? Tool { get; set; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Published before activation so the title bar names the tool while it is still loading.
        _chrome.SetActive(Tool);

        if (Tool is not null)
        {
            await Tool.ActivateAsync();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // Only clear what we put there: the next page may already have claimed the title bar.
        if (ReferenceEquals(_chrome.Active, Tool))
        {
            _chrome.SetActive(null);
        }

        Tool?.Deactivate();
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };

        KeyboardAccelerators.Add(accelerator);
    }
}
