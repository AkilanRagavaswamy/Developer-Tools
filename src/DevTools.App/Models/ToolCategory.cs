namespace DevTools.App.Models;

/// <summary>
/// The groups the dashboard and the navigation pane are organised by.
/// </summary>
public enum ToolCategory
{
    Formatters,
    Validators,
    Converters,
    Encoders,
    Generators,
    Text,
    Vector,
    Api,
}

public static class ToolCategoryInfo
{
    /// <summary>Declaration order is display order.</summary>
    public static IReadOnlyList<ToolCategory> All { get; } =
    [
        ToolCategory.Formatters,
        ToolCategory.Validators,
        ToolCategory.Converters,
        ToolCategory.Encoders,
        ToolCategory.Generators,
        ToolCategory.Text,
        ToolCategory.Vector,
        ToolCategory.Api,
    ];

    public static string DisplayName(ToolCategory category) => category switch
    {
        ToolCategory.Formatters => "Formatters",
        ToolCategory.Validators => "Validators",
        ToolCategory.Converters => "Converters",
        ToolCategory.Encoders => "Encoders & Decoders",
        ToolCategory.Generators => "Generators",
        ToolCategory.Text => "Text Tools",
        ToolCategory.Vector => "Media Tools",
        ToolCategory.Api => "API Tools",
        _ => category.ToString(),
    };

    /// <summary>The group heading in the navigation pane.</summary>
    public static string NavigationName(ToolCategory category) => DisplayName(category);

    /// <summary>Segoe Fluent Icons glyph for the category header.</summary>
    public static string Glyph(ToolCategory category) => category switch
    {
        // Not the glyph of any tool inside the group, so a group and its first tool never
        // look the same in the compact pane.
        ToolCategory.Formatters => "",
        ToolCategory.Validators => "",
        ToolCategory.Converters => "",
        ToolCategory.Encoders => "",
        ToolCategory.Generators => "",
        ToolCategory.Text => "",
        ToolCategory.Vector => "",
        ToolCategory.Api => "",
        _ => "",
    };
}
