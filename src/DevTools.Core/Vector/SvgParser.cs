using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace DevTools.Core.Vector;

internal sealed record SvgGradientStop(double Offset, SvgColor Color);

internal abstract record SvgGradient(
    string Id,
    IReadOnlyList<SvgGradientStop> Stops,
    bool UserSpace,
    string? GradientTransform)
{
    public sealed record Linear(
        string Id, IReadOnlyList<SvgGradientStop> Stops, bool UserSpace, string? GradientTransform,
        double X1, double Y1, double X2, double Y2)
        : SvgGradient(Id, Stops, UserSpace, GradientTransform);

    public sealed record Radial(
        string Id, IReadOnlyList<SvgGradientStop> Stops, bool UserSpace, string? GradientTransform,
        double Cx, double Cy, double R, double Fx, double Fy)
        : SvgGradient(Id, Stops, UserSpace, GradientTransform);
}

/// <summary>One drawable produced by flattening the document: geometry plus how to paint it.</summary>
internal sealed record SvgDrawItem(
    PathGeometryData Geometry,
    SvgPaint Fill,
    double FillOpacity,
    SvgPaint Stroke,
    double StrokeOpacity,
    double StrokeWidth,
    string? LineCap,
    string? LineJoin,
    double? MiterLimit,
    IReadOnlyList<double>? DashArray,
    double? DashOffset,
    double Opacity,
    PathGeometryData? Clip);

/// <summary>The flattened document.</summary>
internal sealed record SvgDocument(
    IReadOnlyList<SvgDrawItem> Items,
    IReadOnlyDictionary<string, SvgGradient> Gradients,
    double Width,
    double Height,
    SvgColor CurrentColor);

/// <summary>
/// Reads an SVG document and flattens it to a list of drawables in root coordinates
/// (FR-V01, FR-V02, FR-V04…FR-V07).
/// </summary>
/// <remarks>
/// Flattening — rather than preserving the group tree — is deliberate. Every transform,
/// inherited paint and <c>use</c> instantiation is resolved here, so the emitter only has to
/// know how to write a path. It also means the WPF and WinUI emitters share one input, which
/// is what keeps the two flavours from drifting apart.
/// </remarks>
internal sealed class SvgParser
{
    private readonly List<SvgNote> _notes = [];
    private readonly Dictionary<string, XElement> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SvgGradient> _gradients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PathGeometryData> _clipPaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unsupported = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SvgDrawItem> _items = [];

    private int _elementCount;
    private int _useDepth;

    /// <summary>What the report says: one line per feature that did not survive intact.</summary>
    public IReadOnlyList<SvgNote> Notes => _notes;

    public OperationResult<SvgDocument> Parse(string svg)
    {
        XDocument document;

        try
        {
            // DTD processing stays off: an SVG from an untrusted source must not be able to
            // pull in an external entity.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };

            using var reader = XmlReader.Create(new StringReader(svg), settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            return OperationResult<SvgDocument>.Fail(
                new ToolError($"The file is not well-formed XML: {ex.Message}", ex.LineNumber, ex.LinePosition));
        }

        var root = document.Root;

        if (root is null || !IsName(root, "svg"))
        {
            return OperationResult<SvgDocument>.Fail(
                "The document's root element is not <svg>, so there is nothing to convert.");
        }

        IndexIds(root);
        CollectDefinitions(root);

        var (viewTransform, width, height) = ResolveViewBox(root);

        var currentColor = SvgColorParser.TryParseColor(Attr(root, "color"), out var color) ? color : SvgColor.Black;

        var initial = SvgStyle.Root;
        Walk(root, viewTransform, initial);

        foreach (var name in _unsupported.OrderBy(static n => n, StringComparer.Ordinal))
        {
            _notes.Add(new SvgNote(
                SvgNoteKind.Dropped,
                $"<{name}> is not supported and was left out",
                UnsupportedDetail(name)));
        }

        return OperationResult<SvgDocument>.Ok(
            new SvgDocument(_items, _gradients, width, height, currentColor));
    }

    // ---- indexing and definitions ----------------------------------------------------

    private void IndexIds(XElement element)
    {
        var id = Attr(element, "id");
        if (!string.IsNullOrEmpty(id))
        {
            _byId.TryAdd(id, element);
        }

        foreach (var child in element.Elements())
        {
            IndexIds(child);
        }
    }

    private void CollectDefinitions(XElement element)
    {
        foreach (var child in element.Descendants())
        {
            if (IsName(child, "linearGradient") || IsName(child, "radialGradient"))
            {
                var gradient = ReadGradient(child);
                if (gradient is not null)
                {
                    _gradients[gradient.Id] = gradient;
                }
            }
            else if (IsName(child, "clipPath"))
            {
                var id = Attr(child, "id");
                if (!string.IsNullOrEmpty(id))
                {
                    var combined = new PathGeometryData();

                    foreach (var shape in child.Elements())
                    {
                        var geometry = ShapeGeometry(shape);
                        if (geometry is not null)
                        {
                            combined.AddRange(geometry.Transformed(ReadTransform(Attr(shape, "transform"))).Segments);
                        }
                    }

                    _clipPaths[id] = combined;
                }
            }
        }
    }

    private SvgGradient? ReadGradient(XElement element)
    {
        var id = Attr(element, "id");
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var stops = ReadStops(element);

        // xlink:href / href inheritance: a gradient may take its stops from another one.
        var href = Attr(element, "href") ?? AttrNs(element, "http://www.w3.org/1999/xlink", "href");
        if (stops.Count == 0 && !string.IsNullOrEmpty(href))
        {
            var target = href.TrimStart('#');
            if (_byId.TryGetValue(target, out var inherited))
            {
                stops = ReadStops(inherited);
            }
        }

        if (stops.Count == 0)
        {
            return null;
        }

        var userSpace = string.Equals(Attr(element, "gradientUnits"), "userSpaceOnUse", StringComparison.Ordinal);
        var transform = Attr(element, "gradientTransform");

        if (!string.IsNullOrWhiteSpace(transform))
        {
            _notes.Add(new SvgNote(
                SvgNoteKind.Limitation,
                $"gradientTransform on \"{id}\" was dropped",
                "A XAML brush has no transform, so the gradient runs between its start and end points instead. Compare the two previews."));
        }

        if (IsName(element, "linearGradient"))
        {
            return new SvgGradient.Linear(
                id, stops, userSpace, transform,
                Length(Attr(element, "x1"), 0),
                Length(Attr(element, "y1"), 0),
                Length(Attr(element, "x2"), userSpace ? 0 : 1),
                Length(Attr(element, "y2"), 0));
        }

        var cx = Length(Attr(element, "cx"), userSpace ? 0 : 0.5);
        var cy = Length(Attr(element, "cy"), userSpace ? 0 : 0.5);
        var r = Length(Attr(element, "r"), userSpace ? 0 : 0.5);

        return new SvgGradient.Radial(
            id, stops, userSpace, transform,
            cx, cy, r,
            Length(Attr(element, "fx"), cx),
            Length(Attr(element, "fy"), cy));
    }

    private List<SvgGradientStop> ReadStops(XElement gradient)
    {
        var stops = new List<SvgGradientStop>();

        foreach (var stop in gradient.Elements().Where(static e => IsName(e, "stop")))
        {
            var style = ParseInlineStyle(Attr(stop, "style"));

            var colorText = Pick(style, "stop-color") ?? Attr(stop, "stop-color");
            var opacityText = Pick(style, "stop-opacity") ?? Attr(stop, "stop-opacity");
            var offsetText = Attr(stop, "offset");

            var color = SvgColorParser.TryParseColor(colorText, out var parsed) ? parsed : SvgColor.Black;

            if (!string.IsNullOrEmpty(opacityText) && SvgColorParser.TryNumber(opacityText.TrimEnd('%'), out var opacity))
            {
                if (opacityText.EndsWith('%'))
                {
                    opacity /= 100.0;
                }

                color = color.WithOpacity(Math.Clamp(opacity, 0, 1));
            }

            var offset = 0.0;
            if (!string.IsNullOrEmpty(offsetText))
            {
                var trimmed = offsetText.Trim();
                if (trimmed.EndsWith('%'))
                {
                    SvgColorParser.TryNumber(trimmed[..^1], out offset);
                    offset /= 100.0;
                }
                else
                {
                    SvgColorParser.TryNumber(trimmed, out offset);
                }
            }

            stops.Add(new SvgGradientStop(Math.Clamp(offset, 0, 1), color));
        }

        return stops;
    }

    // ---- viewBox ---------------------------------------------------------------------

    private (Matrix2D Transform, double Width, double Height) ResolveViewBox(XElement root)
    {
        var viewBox = Attr(root, "viewBox");
        var widthText = Attr(root, "width");
        var heightText = Attr(root, "height");

        double[]? box = null;

        if (!string.IsNullOrWhiteSpace(viewBox))
        {
            var parts = viewBox.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 4 &&
                parts.All(p => SvgColorParser.TryNumber(p, out _)))
            {
                box = [.. parts.Select(p => { SvgColorParser.TryNumber(p, out var v); return v; })];
            }
            else
            {
                _notes.Add(new SvgNote(
                    SvgNoteKind.Limitation,
                    $"The viewBox \"{viewBox}\" is not four numbers and was ignored",
                    "The size falls back to the width and height attributes, so the output may be the wrong size."));
            }
        }

        var width = Length(widthText, box?[2] ?? 0);
        var height = Length(heightText, box?[3] ?? 0);

        if (box is null)
        {
            // No viewBox: user units are already the output units.
            return (Matrix2D.Identity, width, height);
        }

        if (width <= 0)
        {
            width = box[2];
        }

        if (height <= 0)
        {
            height = box[3];
        }

        if (box[2] <= 0 || box[3] <= 0)
        {
            return (Matrix2D.Identity, width, height);
        }

        var scaleX = width / box[2];
        var scaleY = height / box[3];

        var preserve = (Attr(root, "preserveAspectRatio") ?? "xMidYMid meet").Trim();

        if (!preserve.StartsWith("none", StringComparison.OrdinalIgnoreCase))
        {
            var slice = preserve.Contains("slice", StringComparison.OrdinalIgnoreCase);
            var uniform = slice ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);

            var offsetX = AlignOffset(preserve, "xMin", "xMid", "xMax", width - (box[2] * uniform));
            var offsetY = AlignOffset(preserve, "YMin", "YMid", "YMax", height - (box[3] * uniform));

            return (
                Matrix2D.Translate(-box[0], -box[1])
                    .Then(Matrix2D.Scale(uniform, uniform))
                    .Then(Matrix2D.Translate(offsetX, offsetY)),
                width,
                height);
        }

        return (
            Matrix2D.Translate(-box[0], -box[1]).Then(Matrix2D.Scale(scaleX, scaleY)),
            width,
            height);
    }

    private static double AlignOffset(string preserve, string min, string mid, string max, double slack)
    {
        if (preserve.Contains(min, StringComparison.Ordinal))
        {
            return 0;
        }

        if (preserve.Contains(max, StringComparison.Ordinal))
        {
            return slack;
        }

        _ = mid;
        return slack / 2;
    }

    // ---- the walk ---------------------------------------------------------------------

    private void Walk(XElement element, Matrix2D transform, SvgStyle inherited)
    {
        if (_elementCount++ > Limits.MaxSvgElements)
        {
            return;
        }

        foreach (var child in element.Elements())
        {
            var name = LocalName(child);

            switch (name)
            {
                // Definitions were collected up front; they draw nothing on their own.
                case "defs" or "linearGradient" or "radialGradient" or "clipPath" or "symbol"
                     or "title" or "desc" or "metadata" or "style":
                    if (name == "style")
                    {
                        _notes.Add(new SvgNote(
                            SvgNoteKind.Dropped,
                            "A <style> block with CSS selectors is not supported",
                            "Only presentation attributes and inline style=\"\" were applied. Re-save with the styles inlined."));
                    }

                    continue;

                case "text" or "tspan" or "textPath" or "filter" or "mask" or "pattern"
                     or "image" or "foreignObject" or "animate" or "animateTransform"
                     or "animateMotion" or "set" or "marker":
                    _unsupported.Add(name);
                    continue;
            }

            var style = SvgStyle.From(child, inherited);
            var local = ReadTransform(Attr(child, "transform")).Then(transform);

            switch (name)
            {
                case "g" or "a" or "svg":
                    Walk(child, local, style);
                    continue;

                case "use":
                    Instantiate(child, local, style);
                    continue;
            }

            var geometry = ShapeGeometry(child);

            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            Emit(geometry.Transformed(local), style, child, local);
        }

    }

    private void Instantiate(XElement use, Matrix2D transform, SvgStyle style)
    {
        // A <use> that reaches itself would recurse forever; 8 levels is far past anything real.
        if (_useDepth > 8)
        {
            _notes.Add(new SvgNote(
                SvgNoteKind.Dropped,
                "A <use> element references itself",
                "The chain was cut off after eight levels, so part of the drawing is missing."));
            return;
        }

        var href = Attr(use, "href") ?? AttrNs(use, "http://www.w3.org/1999/xlink", "href");

        if (string.IsNullOrWhiteSpace(href) || !href.StartsWith('#'))
        {
            if (!string.IsNullOrWhiteSpace(href))
            {
                _notes.Add(new SvgNote(
                    SvgNoteKind.Dropped,
                    $"<use> points at \"{href}\", which is outside this file",
                    "External references are not followed. Paste the referenced shape into this document."));
            }

            return;
        }

        if (!_byId.TryGetValue(href[1..], out var target))
        {
            _notes.Add(new SvgNote(
                SvgNoteKind.Dropped,
                $"<use> points at \"{href}\", which no element in this file defines",
                "Nothing was drawn in its place."));
            return;
        }

        var offset = Matrix2D.Translate(Length(Attr(use, "x"), 0), Length(Attr(use, "y"), 0)).Then(transform);

        _useDepth++;

        try
        {
            if (IsName(target, "symbol") || IsName(target, "g") || IsName(target, "svg"))
            {
                Walk(target, offset, style);
                return;
            }

            var targetStyle = SvgStyle.From(target, style);
            var local = ReadTransform(Attr(target, "transform")).Then(offset);
            var geometry = ShapeGeometry(target);

            if (geometry is not null && !geometry.IsEmpty)
            {
                Emit(geometry.Transformed(local), targetStyle, target, local);
            }
        }
        finally
        {
            _useDepth--;
        }
    }

    private void Emit(PathGeometryData geometry, SvgStyle style, XElement element, Matrix2D transform)
    {
        var fill = style.Fill ?? new SvgPaint.Solid(SvgColor.Black);
        var stroke = style.Stroke ?? new SvgPaint.None();

        if (fill is SvgPaint.None && stroke is SvgPaint.None)
        {
            // Nothing would be painted; emitting it would only add noise to the output.
            return;
        }

        geometry.FillRule = style.FillRule ?? SvgFillRule.NonZero;

        PathGeometryData? clip = null;
        var clipRef = Attr(element, "clip-path");

        if (!string.IsNullOrWhiteSpace(clipRef))
        {
            var id = clipRef.Trim();

            if (id.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                var close = id.IndexOf(')', StringComparison.Ordinal);
                id = close > 4 ? id[4..close].Trim().Trim('\'', '"') : string.Empty;
            }

            id = id.TrimStart('#');

            if (_clipPaths.TryGetValue(id, out var clipGeometry))
            {
                clip = clipGeometry.Transformed(transform);
            }
        }

        _items.Add(new SvgDrawItem(
            geometry,
            fill,
            style.FillOpacity ?? 1,
            stroke,
            style.StrokeOpacity ?? 1,
            ScaleStrokeWidth(style.StrokeWidth ?? 1, transform),
            style.LineCap,
            style.LineJoin,
            style.MiterLimit,
            style.DashArray,
            style.DashOffset,
            style.Opacity ?? 1,
            clip));
    }

    /// <summary>
    /// Scales the stroke width by the transform, because the geometry has been baked into root
    /// coordinates and a stroke must scale with the shape it outlines.
    /// </summary>
    private static double ScaleStrokeWidth(double width, Matrix2D m)
    {
        // The geometric mean of the axis scales — the standard approximation, and exact for
        // any uniform scale, which is what a viewBox produces.
        var scale = Math.Sqrt(Math.Abs((m.M11 * m.M22) - (m.M12 * m.M21)));
        return scale > 0 ? width * scale : width;
    }

    /// <summary>
    /// The path grammar reports in plain sentences, so its findings arrive as notes with no
    /// separate detail — the sentence already carries the reason.
    /// </summary>
    private PathGeometryData? ParsePathData(string? data)
    {
        var messages = new List<string>();
        var geometry = SvgPathGrammar.Parse(data, messages);

        foreach (var message in messages)
        {
            _notes.Add(new SvgNote(SvgNoteKind.Dropped, message));
        }

        return geometry;
    }

    /// <summary>
    /// Why a particular element could not come across, and what to do about it. "Not supported"
    /// on its own sends the user away to guess; these are the sentences that unblock them.
    /// </summary>
    private static string UnsupportedDetail(string name) => name switch
    {
        "text" or "tspan" or "textPath" =>
            "Turning glyphs into geometry needs font metrics. Convert the text to outlines in your editor first.",

        "image" =>
            "An embedded raster is a picture, not a shape. Save it beside the XAML and reference it from an ImageBrush.",

        "filter" or "feGaussianBlur" or "feDropShadow" or "feColorMatrix" =>
            "XAML has no filter pipeline. Bake the effect into the artwork, or use a platform shadow instead.",

        "mask" =>
            "A mask needs per-pixel alpha, which a Path cannot express. Flatten it in your editor.",

        "pattern" =>
            "A tiled paint server has no XAML brush equivalent. Flatten it, or draw one tile yourself.",

        "animate" or "animateTransform" or "animateMotion" or "set" =>
            "Animation is not part of a static drawing. Add a Storyboard in XAML if you need it.",

        "foreignObject" =>
            "Its content is not SVG, so there is nothing to convert.",

        "switch" =>
            "Conditional rendering depends on the viewer's features, which a static conversion cannot decide.",

        _ =>
            "It is outside the documented supported subset.",
    };

    private PathGeometryData? ShapeGeometry(XElement element)
    {
        switch (LocalName(element))
        {
            case "path":
                return ParsePathData(Attr(element, "d"));

            case "rect":
                return ShapeBuilder2D.Rectangle(
                    Length(Attr(element, "x"), 0),
                    Length(Attr(element, "y"), 0),
                    Length(Attr(element, "width"), 0),
                    Length(Attr(element, "height"), 0),
                    Length(Attr(element, "rx"), -1),
                    Length(Attr(element, "ry"), -1));

            case "circle":
            {
                var r = Length(Attr(element, "r"), 0);
                return ShapeBuilder2D.Ellipse(
                    Length(Attr(element, "cx"), 0), Length(Attr(element, "cy"), 0), r, r);
            }

            case "ellipse":
                return ShapeBuilder2D.Ellipse(
                    Length(Attr(element, "cx"), 0),
                    Length(Attr(element, "cy"), 0),
                    Length(Attr(element, "rx"), 0),
                    Length(Attr(element, "ry"), 0));

            case "line":
                return ShapeBuilder2D.Line(
                    Length(Attr(element, "x1"), 0),
                    Length(Attr(element, "y1"), 0),
                    Length(Attr(element, "x2"), 0),
                    Length(Attr(element, "y2"), 0));

            case "polyline":
                return ShapeBuilder2D.Polygon(ReadPoints(Attr(element, "points")), close: false);

            case "polygon":
                return ShapeBuilder2D.Polygon(ReadPoints(Attr(element, "points")), close: true);

            default:
                return null;
        }
    }

    private static List<Point> ReadPoints(string? text)
    {
        var points = new List<Point>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return points;
        }

        var numbers = text
            .Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static p => SvgColorParser.TryNumber(p, out var v) ? (double?)v : null)
            .Where(static v => v.HasValue)
            .Select(static v => v!.Value)
            .ToList();

        for (var i = 0; i + 1 < numbers.Count; i += 2)
        {
            points.Add(new Point(numbers[i], numbers[i + 1]));
        }

        return points;
    }

    // ---- transforms --------------------------------------------------------------------

    internal static Matrix2D ReadTransform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Matrix2D.Identity;
        }

        var result = Matrix2D.Identity;
        var index = 0;

        while (index < text.Length)
        {
            while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == ','))
            {
                index++;
            }

            var nameStart = index;
            while (index < text.Length && (char.IsLetter(text[index])))
            {
                index++;
            }

            if (index == nameStart)
            {
                break;
            }

            var name = text[nameStart..index];

            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= text.Length || text[index] != '(')
            {
                break;
            }

            var close = text.IndexOf(')', index);
            if (close < 0)
            {
                break;
            }

            var args = text[(index + 1)..close]
                .Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static p => SvgColorParser.TryNumber(p, out var v) ? v : 0.0)
                .ToArray();

            index = close + 1;

            // SVG composes left to right: the first listed transform is applied last.
            var step = name.ToLowerInvariant() switch
            {
                "matrix" when args.Length >= 6 => new Matrix2D(args[0], args[1], args[2], args[3], args[4], args[5]),
                "translate" when args.Length >= 2 => Matrix2D.Translate(args[0], args[1]),
                "translate" when args.Length == 1 => Matrix2D.Translate(args[0], 0),
                "scale" when args.Length >= 2 => Matrix2D.Scale(args[0], args[1]),
                "scale" when args.Length == 1 => Matrix2D.Scale(args[0], args[0]),
                "rotate" when args.Length >= 3 => Matrix2D.Rotate(args[0], args[1], args[2]),
                "rotate" when args.Length == 1 => Matrix2D.Rotate(args[0]),
                "skewx" when args.Length >= 1 => Matrix2D.SkewX(args[0]),
                "skewy" when args.Length >= 1 => Matrix2D.SkewY(args[0]),
                _ => Matrix2D.Identity,
            };

            result = step.Then(result);
        }

        return result;
    }

    // ---- attribute helpers ---------------------------------------------------------------

    internal static string LocalName(XElement element) => element.Name.LocalName;

    internal static bool IsName(XElement element, string name) =>
        string.Equals(element.Name.LocalName, name, StringComparison.Ordinal);

    internal static string? Attr(XElement element, string name) =>
        element.Attribute(name)?.Value;

    internal static string? AttrNs(XElement element, string ns, string name) =>
        element.Attribute(XName.Get(name, ns))?.Value;

    /// <summary>
    /// Reads a length, tolerating the CSS units SVG permits. Absolute units are converted at
    /// the CSS reference of 96 dpi; percentages cannot be resolved without a viewport, so they
    /// fall back to the default rather than being silently read as a bare number.
    /// </summary>
    internal static double Length(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var value = text.Trim();

        if (value.EndsWith('%'))
        {
            return SvgColorParser.TryNumber(value[..^1], out var percent) ? percent / 100.0 : fallback;
        }

        (string Suffix, double Factor)[] units =
        [
            ("px", 1), ("pt", 96.0 / 72.0), ("pc", 16), ("mm", 96.0 / 25.4),
            ("cm", 96.0 / 2.54), ("in", 96), ("em", 16), ("ex", 8),
        ];

        foreach (var (suffix, factor) in units)
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return SvgColorParser.TryNumber(value[..^suffix.Length], out var scaled)
                    ? scaled * factor
                    : fallback;
            }
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain)
            ? plain
            : fallback;
    }

    internal static Dictionary<string, string> ParseInlineStyle(string? style)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(style))
        {
            return map;
        }

        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            map[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
        }

        return map;
    }

    internal static string? Pick(Dictionary<string, string> style, string name) =>
        style.TryGetValue(name, out var value) ? value : null;
}
