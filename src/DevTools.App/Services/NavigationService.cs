using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace DevTools.App.Services;

public sealed class NavigatedEventArgs(string? toolId, Type pageType) : EventArgs
{
    public string? ToolId { get; } = toolId;

    public Type PageType { get; } = pageType;
}

public interface INavigationService
{
    event EventHandler<NavigatedEventArgs>? Navigated;

    bool CanGoBack { get; }

    bool CanGoForward { get; }

    string? CurrentToolId { get; }

    Type? CurrentPageType { get; }

    void Initialize(Frame frame);

    bool NavigateToTool(string toolId);

    bool NavigateTo(Type pageType, object? parameter = null);

    bool GoBack();

    bool GoForward();
}

/// <summary>
/// A thin wrapper over the shell <see cref="Frame"/> that owns history, records recents and
/// keeps the current tool id available to the shell (FR-S15).
/// </summary>
public sealed class NavigationService(ToolCatalog catalog, IRecentToolsService recents) : INavigationService
{
    private Frame? _frame;

    public event EventHandler<NavigatedEventArgs>? Navigated;

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public bool CanGoForward => _frame?.CanGoForward ?? false;

    public string? CurrentToolId { get; private set; }

    public Type? CurrentPageType => _frame?.CurrentSourcePageType;

    public void Initialize(Frame frame)
    {
        _frame = frame;
        _frame.Navigated += OnFrameNavigated;
    }

    public bool NavigateToTool(string toolId)
    {
        var tool = catalog.ById(toolId);
        return tool is not null && NavigateTo(tool.PageType, tool.Id);
    }

    public bool NavigateTo(Type pageType, object? parameter = null)
    {
        ArgumentNullException.ThrowIfNull(pageType);

        if (_frame is null)
        {
            return false;
        }

        // Re-navigating to the page you are already on would push a pointless history entry.
        if (_frame.CurrentSourcePageType == pageType && Equals(CurrentToolId, parameter as string))
        {
            return false;
        }

        try
        {
            return _frame.Navigate(pageType, parameter, new DrillInNavigationTransitionInfo());
        }
        catch (Exception ex)
        {
            // A page that will not load should say why. Without this the failure surfaces as
            // nothing happening when a tool is clicked, with the reason — usually a resource a
            // template could not resolve — thrown away.
            App.LogError($"Navigating to {pageType.Name}", ex);
            return false;
        }
    }

    public bool GoBack()
    {
        if (_frame?.CanGoBack != true)
        {
            return false;
        }

        _frame.GoBack();
        return true;
    }

    public bool GoForward()
    {
        if (_frame?.CanGoForward != true)
        {
            return false;
        }

        _frame.GoForward();
        return true;
    }

    private void OnFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        var toolId = e.Parameter as string ?? catalog.ByPageType(e.SourcePageType)?.Id;
        CurrentToolId = toolId;

        if (toolId is not null)
        {
            _ = recents.TouchAsync(toolId);
        }

        Navigated?.Invoke(this, new NavigatedEventArgs(toolId, e.SourcePageType));
    }
}
