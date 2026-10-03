namespace DevTools.App.Models;

/// <summary>
/// The three groups the dashboard is organised by.
/// </summary>
/// <remarks>
/// The navigation pane lists the six tools flat (FR-S01) — six items do not need collapsible
/// groups, and hiding half of them behind a expander would make the app slower to use, not
/// tidier. The categories exist for the dashboard, where they give the cards a reading order.
/// </remarks>
public enum ToolCategory
{
    Json,
    Vector,
    Api,
}

public static class ToolCategoryInfo
{
    /// <summary>Declaration order is display order.</summary>
    public static IReadOnlyList<ToolCategory> All { get; } =
    [
        ToolCategory.Json,
        ToolCategory.Vector,
        ToolCategory.Api,
    ];

    public static string DisplayName(ToolCategory category) => category switch
    {
        ToolCategory.Json => "JSON",
        ToolCategory.Vector => "Vector",
        ToolCategory.Api => "API",
        _ => category.ToString(),
    };

    /// <summary>Segoe Fluent Icons glyph for the category header.</summary>
    public static string Glyph(ToolCategory category) => category switch
    {
        ToolCategory.Json => "",
        ToolCategory.Vector => "",
        ToolCategory.Api => "",
        _ => "",
    };
}
