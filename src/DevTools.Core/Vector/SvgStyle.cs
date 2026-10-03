using System.Xml.Linq;

namespace DevTools.Core.Vector;

/// <summary>
/// The resolved presentation properties of one element.
/// </summary>
/// <remarks>
/// Inheritance follows the SVG specification, which is not the same as "copy everything from
/// the parent": <c>fill</c>, <c>stroke</c> and the stroke properties inherit, while
/// <c>opacity</c>, <c>transform</c> and <c>clip-path</c> do not. Getting <c>opacity</c> wrong
/// in particular compounds visibly — a group at 50% inside a group at 50% must be 25%, applied
/// once per level, not inherited as a value.
/// </remarks>
internal sealed record SvgStyle
{
    public SvgPaint? Fill { get; init; }

    public SvgPaint? Stroke { get; init; }

    public double? FillOpacity { get; init; }

    public double? StrokeOpacity { get; init; }

    public double? StrokeWidth { get; init; }

    public string? LineCap { get; init; }

    public string? LineJoin { get; init; }

    public double? MiterLimit { get; init; }

    public IReadOnlyList<double>? DashArray { get; init; }

    public double? DashOffset { get; init; }

    public SvgFillRule? FillRule { get; init; }

    /// <summary>Not inherited — it multiplies down the tree instead.</summary>
    public double? Opacity { get; init; }

    public SvgColor CurrentColor { get; init; } = SvgColor.Black;

    /// <summary>The SVG initial values: black fill, no stroke, stroke width 1, fully opaque.</summary>
    public static SvgStyle Root { get; } = new()
    {
        Fill = new SvgPaint.Solid(SvgColor.Black),
        Stroke = new SvgPaint.None(),
        StrokeWidth = 1,
        FillOpacity = 1,
        StrokeOpacity = 1,
        FillRule = SvgFillRule.NonZero,
        Opacity = 1,
    };

    public static SvgStyle From(XElement element, SvgStyle parent)
    {
        var inline = SvgParser.ParseInlineStyle(SvgParser.Attr(element, "style"));

        // An inline style declaration wins over the matching presentation attribute.
        string? Read(string name) => SvgParser.Pick(inline, name) ?? SvgParser.Attr(element, name);

        var currentColor = parent.CurrentColor;
        if (Read("color") is { } colorText && SvgColorParser.TryParseColor(colorText, out var parsedColor))
        {
            currentColor = parsedColor;
        }

        var style = parent with
        {
            CurrentColor = currentColor,

            // Opacity is per-element and multiplies: a 50% group inside a 50% group is 25%.
            Opacity = (parent.Opacity ?? 1) * ReadNumber(Read("opacity"), 1),
        };

        if (Read("fill") is { } fillText)
        {
            style = style with { Fill = Resolve(SvgColorParser.ParsePaint(fillText), currentColor) };
        }

        if (Read("stroke") is { } strokeText)
        {
            style = style with { Stroke = Resolve(SvgColorParser.ParsePaint(strokeText), currentColor) };
        }

        if (Read("fill-opacity") is { } fillOpacity)
        {
            style = style with { FillOpacity = ReadNumber(fillOpacity, 1) };
        }

        if (Read("stroke-opacity") is { } strokeOpacity)
        {
            style = style with { StrokeOpacity = ReadNumber(strokeOpacity, 1) };
        }

        if (Read("stroke-width") is { } strokeWidth)
        {
            style = style with { StrokeWidth = Math.Max(0, SvgParser.Length(strokeWidth, 1)) };
        }

        if (Read("stroke-linecap") is { } lineCap)
        {
            style = style with { LineCap = MapCap(lineCap) };
        }

        if (Read("stroke-linejoin") is { } lineJoin)
        {
            style = style with { LineJoin = MapJoin(lineJoin) };
        }

        if (Read("stroke-miterlimit") is { } miterLimit)
        {
            style = style with { MiterLimit = SvgParser.Length(miterLimit, 4) };
        }

        if (Read("stroke-dasharray") is { } dashArray)
        {
            style = style with { DashArray = ReadDashes(dashArray, style.StrokeWidth ?? 1) };
        }

        if (Read("stroke-dashoffset") is { } dashOffset)
        {
            style = style with { DashOffset = SvgParser.Length(dashOffset, 0) };
        }

        if (Read("fill-rule") is { } fillRule)
        {
            style = style with
            {
                FillRule = fillRule.Trim().Equals("evenodd", StringComparison.OrdinalIgnoreCase)
                    ? SvgFillRule.EvenOdd
                    : SvgFillRule.NonZero,
            };
        }

        if (Read("display") is { } display && display.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            style = style with { Fill = new SvgPaint.None(), Stroke = new SvgPaint.None() };
        }

        if (Read("visibility") is { } visibility && visibility.Trim().Equals("hidden", StringComparison.OrdinalIgnoreCase))
        {
            style = style with { Fill = new SvgPaint.None(), Stroke = new SvgPaint.None() };
        }

        return style;
    }

    private static SvgPaint Resolve(SvgPaint paint, SvgColor currentColor) =>
        paint is SvgPaint.Current ? new SvgPaint.Solid(currentColor) : paint;

    private static double ReadNumber(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var trimmed = text.Trim();

        if (trimmed.EndsWith('%'))
        {
            return SvgColorParser.TryNumber(trimmed[..^1], out var percent)
                ? Math.Clamp(percent / 100.0, 0, 1)
                : fallback;
        }

        return SvgColorParser.TryNumber(trimmed, out var value) ? Math.Clamp(value, 0, 1) : fallback;
    }

    /// <summary>
    /// XAML dash lengths are multiples of the stroke width; SVG's are absolute. Dividing here
    /// is what keeps a dashed line looking the same after conversion.
    /// </summary>
    private static IReadOnlyList<double>? ReadDashes(string text, double strokeWidth)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var width = strokeWidth > 0 ? strokeWidth : 1;

        var dashes = text
            .Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => SvgColorParser.TryNumber(p, out var v) ? v / width : (double?)null)
            .Where(static v => v.HasValue)
            .Select(static v => v!.Value)
            .ToList();

        if (dashes.Count == 0 || dashes.All(static d => d <= 0))
        {
            return null;
        }

        // An odd-length pattern repeats to become even, per the SVG specification.
        if (dashes.Count % 2 == 1)
        {
            dashes.AddRange(dashes);
        }

        return dashes;
    }

    private static string? MapCap(string value) => value.Trim().ToLowerInvariant() switch
    {
        "round" => "Round",
        "square" => "Square",
        "butt" => "Flat",
        _ => null,
    };

    private static string? MapJoin(string value) => value.Trim().ToLowerInvariant() switch
    {
        "round" => "Round",
        "bevel" => "Bevel",
        "miter" => "Miter",
        _ => null,
    };
}
