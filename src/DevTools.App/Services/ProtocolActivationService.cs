using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace DevTools.App.Services;

public interface IProtocolActivationService
{
    /// <summary>Raised when a <c>forgekit:</c> URI names a tool to open.</summary>
    event EventHandler<string>? ToolRequested;

    /// <summary>The tool id from the URI that launched the app, if it was launched by one.</summary>
    string? PendingToolId { get; }

    /// <summary>Starts listening for activations that arrive while the app is already running.</summary>
    void Initialize();

    /// <summary>Consumes and clears <see cref="PendingToolId"/>.</summary>
    string? TakePendingToolId();
}

/// <summary>
/// Handles <c>forgekit://tool/&lt;tool-id&gt;</c> activation (FR-S18), so a shortcut, a script or
/// another app can open a specific tool directly.
/// <para>
/// Both entry points are covered: the URI that launched the process, and a URI that arrives
/// while the app is already running.
/// </para>
/// </summary>
public sealed class ProtocolActivationService(ToolCatalog catalog, IShellContext shell)
    : IProtocolActivationService
{
    public const string Scheme = "forgekit";

    private string? _pendingToolId;

    public event EventHandler<string>? ToolRequested;

    public string? PendingToolId => _pendingToolId;

    public void Initialize()
    {
        try
        {
            var current = AppInstance.GetCurrent();
            _pendingToolId = ResolveToolId(current.GetActivatedEventArgs());
            current.Activated += OnActivated;
        }
        catch (Exception)
        {
            // Activation plumbing is unavailable when running unpackaged; the app still works,
            // it simply cannot be driven by a URI.
        }
    }

    public string? TakePendingToolId()
    {
        var id = _pendingToolId;
        _pendingToolId = null;
        return id;
    }

    private void OnActivated(object? sender, AppActivationArguments args)
    {
        if (ResolveToolId(args) is not { } toolId)
        {
            return;
        }

        // Activation arrives on a background thread; navigation must happen on the UI thread.
        shell.Post(() => ToolRequested?.Invoke(this, toolId));
    }

    private string? ResolveToolId(AppActivationArguments? args)
    {
        if (args?.Kind != ExtendedActivationKind.Protocol ||
            args.Data is not IProtocolActivatedEventArgs protocolArgs)
        {
            return null;
        }

        return ParseToolId(protocolArgs.Uri, catalog);
    }

    /// <summary>
    /// Accepts <c>forgekit://tool/&lt;id&gt;</c> and the shorter <c>forgekit://&lt;id&gt;</c>, and
    /// returns the id only when it names a tool that actually exists.
    /// </summary>
    internal static string? ParseToolId(Uri? uri, ToolCatalog catalog)
    {
        if (uri is null || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = $"{uri.Host}/{uri.AbsolutePath}"
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return null;
        }

        // "tool" is an optional path prefix, so both forms above resolve the same way.
        var candidate = segments.Length > 1 && segments[0].Equals("tool", StringComparison.OrdinalIgnoreCase)
            ? segments[1]
            : segments[0];

        return catalog.ById(candidate)?.Id;
    }
}
