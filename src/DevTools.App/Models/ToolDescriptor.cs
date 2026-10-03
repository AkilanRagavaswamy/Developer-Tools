namespace DevTools.App.Models;

/// <summary>
/// One entry in the tool catalog. <see cref="Id"/> is the stable key used by persisted
/// state, favorites, recents and <c>devtools://tool/&lt;id&gt;</c> activation, so it must never
/// change once shipped.
/// </summary>
public sealed record ToolDescriptor(
    string Id,
    string Name,
    string Description,
    ToolCategory Category,
    string Glyph,
    Type PageType,
    IReadOnlyList<string> Keywords)
{
    /// <summary>The font every glyph is drawn in unless a tool says otherwise.</summary>
    public static Microsoft.UI.Xaml.Media.FontFamily SymbolFont { get; } = new("Segoe Fluent Icons,Segoe MDL2 Assets");

    /// <summary>
    /// The font <see cref="Glyph"/> is drawn in. A tool whose job no symbol says — JSON to C#
    /// produces C#, and the icon font has no mark for that — can draw its glyph as text instead.
    /// </summary>
    public Microsoft.UI.Xaml.Media.FontFamily GlyphFont { get; init; } = SymbolFont;

    /// <summary>
    /// The initials used for acronym search — "JSON Formatter" becomes "jf", so typing
    /// <c>jf</c> in the palette finds it.
    /// </summary>
    public string Acronym { get; } = BuildAcronym(Name);

    private static string BuildAcronym(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        var length = 0;
        var atBoundary = true;

        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (atBoundary)
                {
                    buffer[length++] = char.ToLowerInvariant(c);
                    atBoundary = false;
                }
            }
            else
            {
                atBoundary = true;
            }
        }

        return new string(buffer[..length]);
    }
}
