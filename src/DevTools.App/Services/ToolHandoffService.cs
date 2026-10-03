using DevTools.Http.Model;

namespace DevTools.App.Services;

/// <summary>What one tool is handing to another.</summary>
public abstract record ToolPayload
{
    /// <summary>Text to drop into the receiving tool's input.</summary>
    public sealed record Text(string Value) : ToolPayload;

    /// <summary>Two documents to compare.</summary>
    public sealed record TextPair(string Left, string Right) : ToolPayload;

    /// <summary>A request to open or profile.</summary>
    public sealed record Request(RequestDefinition Value) : ToolPayload;
}

/// <summary>
/// Carries a typed payload from one tool to another across a navigation (FR-S18, FR-A31).
/// </summary>
/// <remarks>
/// The point of routing this through a service rather than passing an object to the page is
/// that no tool ends up referencing another tool's view model. API Builder knows it wants "the
/// JSON formatter, with this text"; it does not know what a <c>JsonFormatterViewModel</c> is.
/// </remarks>
public interface IToolHandoffService
{
    /// <summary>Stashes a payload and navigates to the receiving tool.</summary>
    void Send(string toolId, ToolPayload payload);

    /// <summary>Takes the pending payload for a tool, if there is one. Consumes it.</summary>
    ToolPayload? Take(string toolId);
}

public sealed class ToolHandoffService(INavigationService navigation) : IToolHandoffService
{
    private readonly Dictionary<string, ToolPayload> _pending = new(StringComparer.OrdinalIgnoreCase);

    public void Send(string toolId, ToolPayload payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(payload);

        lock (_pending)
        {
            _pending[toolId] = payload;
        }

        navigation.NavigateToTool(toolId);
    }

    public ToolPayload? Take(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return null;
        }

        lock (_pending)
        {
            if (!_pending.Remove(toolId, out var payload))
            {
                return null;
            }

            return payload;
        }
    }
}
