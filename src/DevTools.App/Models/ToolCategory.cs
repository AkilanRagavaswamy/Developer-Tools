namespace DevTools.App.Models;

/// <summary>
/// The three groups the dashboard and the navigation pane are organised by.
/// </summary>
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
        ToolCategory.Json => "JSON Tools",
        ToolCategory.Vector => "Media Tools",
        ToolCategory.Api => "API Tools",
        _ => category.ToString(),
    };

    /// <summary>The group heading in the navigation pane.</summary>
    public static string NavigationName(ToolCategory category) => category switch
    {
        ToolCategory.Json => "JSON Tools",
        ToolCategory.Vector => "Media Tools",
        ToolCategory.Api => "API Tools",
        _ => category.ToString(),
    };

    /// <summary>Segoe Fluent Icons glyph for the category header.</summary>
    public static string Glyph(ToolCategory category) => category switch
    {
        // Not the glyph of any tool inside the group, so a group and its first tool never
        // look the same in the compact pane.
        ToolCategory.Json => "\uE943",
        ToolCategory.Vector => "",
        ToolCategory.Api => "",
        _ => "",
    };
}
