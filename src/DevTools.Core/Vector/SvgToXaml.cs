using System.Text;
using DevTools.Core.Text;

namespace DevTools.Core.Vector;

/// <summary>Which XAML dialect the output targets (FR-V08).</summary>
public enum XamlFlavor
{
    /// <summary>WPF — has <c>DrawingImage</c>, <c>DrawingGroup</c> and arbitrary <c>Clip</c> geometry.</summary>
    Wpf,

    /// <summary>WinUI 3 / UWP — has none of those, so the emitter offers fewer shapes.</summary>
    WinUi,
}

/// <summary>What the converter produces.</summary>
public enum XamlOutputShape
{
    /// <summary>A <c>Canvas</c> holding one <c>Path</c> per shape. Highest fidelity.</summary>
    Canvas,

    /// <summary>One <c>Path</c> whose <c>Data</c> merges every geometry. Best for single-colour icons.</summary>
    MergedPath,

    /// <summary>A <c>DrawingImage</c> resource. <b>WPF only.</b></summary>
    DrawingImage,

    /// <summary>A <c>PathIcon</c> for use in WinUI controls. <b>WinUI only.</b></summary>
    PathIcon,
}

/// <summary>Everything the SVG converter lets the caller choose.</summary>
public sealed record SvgConvertOptions
{
    public XamlFlavor Flavor { get; init; } = XamlFlavor.WinUi;

    public XamlOutputShape Shape { get; init; } = XamlOutputShape.Canvas;

    /// <summary>Digits kept on every emitted coordinate.</summary>
    public int DecimalPlaces { get; init; } = 3;

    /// <summary>Emits <c>x:Key</c> on the root so the output drops into a ResourceDictionary.</summary>
    public string? ResourceKey { get; init; }

    /// <summary>Includes the <c>xmlns</c> declarations on the root element.</summary>
    public bool IncludeNamespaces { get; init; } = true;

    public static SvgConvertOptions Default { get; } = new();

    /// <summary>The output shapes this flavour can actually express.</summary>
    public static IReadOnlyList<XamlOutputShape> ShapesFor(XamlFlavor flavor) => flavor switch
    {
        XamlFlavor.Wpf => [XamlOutputShape.Canvas, XamlOutputShape.MergedPath, XamlOutputShape.DrawingImage],
        _ => [XamlOutputShape.Canvas, XamlOutputShape.MergedPath, XamlOutputShape.PathIcon],
    };
}

/// <summary>The emitted XAML, plus what could not be represented.</summary>
public sealed record SvgConvertResult(
    string Xaml,
    int ElementCount,
    double Width,
    double Height,
    IReadOnlyList<SvgNote> Notes)
{
    /// <summary>The report as plain sentences, for callers that only want the text.</summary>
    public IReadOnlyList<string> Warnings => [.. Notes.Where(n => n.Kind != SvgNoteKind.Converted).Select(n => n.Text)];
}

/// <summary>
/// Converts SVG to WPF or WinUI XAML (FR-V01…FR-V11).
/// </summary>
/// <remarks>
/// Two decisions shape the output. First, every transform is baked into the emitted
/// coordinates, so the result is plain path data that needs no transform elements and behaves
/// the same in both dialects. Second, the supported subset is written down and anything
/// outside it produces a named warning — a converter that silently drops a feature is worse
/// than one that refuses it, because the user finds out from the rendered picture.
/// </remarks>
public static class SvgToXaml
{
    public static OperationResult<SvgConvertResult> Convert(string? svg, SvgConvertOptions? options = null)
    {
        var opts = options ?? SvgConvertOptions.Default;

        if (TextUtil.IsBlank(svg))
        {
            return OperationResult<SvgConvertResult>.Ok(new SvgConvertResult(string.Empty, 0, 0, 0, []));
        }

        if (!SvgConvertOptions.ShapesFor(opts.Flavor).Contains(opts.Shape))
        {
            return OperationResult<SvgConvertResult>.Fail(
                $"{opts.Shape} is not available for {Describe(opts.Flavor)}. " +
                $"{Describe(opts.Flavor)} supports {string.Join(", ", SvgConvertOptions.ShapesFor(opts.Flavor))}.");
        }

        var parser = new SvgParser();
        var parsed = parser.Parse(TextUtil.StripBom(svg)!);

        if (!parsed.IsSuccess)
        {
            return OperationResult<SvgConvertResult>.Fail(parsed.Error!);
        }

        var document = parsed.Value!;
        var notes = new List<SvgNote>(parser.Notes);

        if (document.Items.Count == 0)
        {
            notes.Add(new SvgNote(
                SvgNoteKind.Dropped,
                "The document contains no shapes that could be converted",
                "Nothing in it maps to a XAML geometry."));
        }

        var emitter = new XamlEmitter(opts, document, notes);
        var xaml = emitter.Emit();

        return OperationResult<SvgConvertResult>.Ok(
            new SvgConvertResult(xaml, document.Items.Count, document.Width, document.Height, [Summarise(document), .. notes]));
    }


    /// <summary>
    /// The one line that says what did come across. A report of nothing but failures reads as
    /// if the whole conversion failed, so this is always first, even on a clean run.
    /// </summary>
    private static SvgNote Summarise(SvgDocument document)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in document.Items)
        {
            Reference(item.Fill);
            Reference(item.Stroke);
        }

        var shapes = document.Items.Count;
        var text = shapes == 1 ? "1 shape" : $"{shapes:N0} shapes";

        if (used.Count > 0)
        {
            text += used.Count == 1 ? " and 1 gradient" : $" and {used.Count:N0} gradients";
        }

        return new SvgNote(SvgNoteKind.Converted, $"{text} converted");

        void Reference(SvgPaint paint)
        {
            if (paint is SvgPaint.Reference r && document.Gradients.ContainsKey(r.Id))
            {
                used.Add(r.Id);
            }
        }
    }

    private static string Describe(XamlFlavor flavor) => flavor == XamlFlavor.Wpf ? "WPF" : "WinUI";
}

/// <summary>Writes the flattened document as XAML in the requested dialect.</summary>
internal sealed class XamlEmitter(SvgConvertOptions options, SvgDocument document, List<SvgNote> notes)
{
    private const string WpfNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private readonly StringBuilder _builder = new();

    public string Emit()
    {
        switch (options.Shape)
        {
            case XamlOutputShape.MergedPath:
                EmitMergedPath();
                break;

            case XamlOutputShape.DrawingImage:
                EmitDrawingImage();
                break;

            case XamlOutputShape.PathIcon:
                EmitPathIcon();
                break;

            default:
                EmitCanvas();
                break;
        }

        return _builder.ToString().TrimEnd() + "\n";
    }

    // ---- output shapes -----------------------------------------------------------------

    private void EmitCanvas()
    {
        _builder.Append("<Canvas");
        AppendRootAttributes();

        if (document.Width > 0)
        {
            _builder.Append(" Width=\"").Append(Number(document.Width)).Append('"');
        }

        if (document.Height > 0)
        {
            _builder.Append(" Height=\"").Append(Number(document.Height)).Append('"');
        }

        _builder.AppendLine(">");

        foreach (var item in document.Items)
        {
            EmitPath(item, "    ");
        }

        _builder.AppendLine("</Canvas>");
    }

    private void EmitMergedPath()
    {
        var merged = MergeGeometry(out var fill);

        _builder.Append("<Path");
        AppendRootAttributes();

        if (fill is not null)
        {
            _builder.Append(" Fill=\"").Append(fill).Append('"');
        }

        _builder.Append(" Data=\"").Append(Escape(merged)).AppendLine("\" />");
    }

    private void EmitPathIcon()
    {
        var merged = MergeGeometry(out _);

        notes.Add(new SvgNote(
            SvgNoteKind.Limitation,
            "PathIcon takes its colour from the control it sits in",
            "Fills and strokes from the SVG are not carried over. Choose the Canvas output to keep them."));

        _builder.Append("<PathIcon");
        AppendRootAttributes();
        _builder.Append(" Data=\"").Append(Escape(merged)).AppendLine("\" />");
    }

    private void EmitDrawingImage()
    {
        _builder.Append("<DrawingImage");
        AppendRootAttributes();
        _builder.AppendLine(">");
        _builder.AppendLine("    <DrawingImage.Drawing>");
        _builder.AppendLine("        <DrawingGroup>");

        foreach (var item in document.Items)
        {
            var geometry = item.Geometry.ToPathData(options.DecimalPlaces);

            if (geometry.Length == 0)
            {
                continue;
            }

            _builder.Append("            <GeometryDrawing Geometry=\"").Append(Escape(geometry)).Append('"');

            if (SolidBrushValue(item.Fill, item.FillOpacity * item.Opacity) is { } brush)
            {
                _builder.Append(" Brush=\"").Append(brush).Append('"');
            }

            if (item.Stroke is not SvgPaint.None)
            {
                _builder.AppendLine(">");
                _builder.AppendLine("                <GeometryDrawing.Pen>");
                _builder.Append("                    <Pen Thickness=\"").Append(Number(item.StrokeWidth)).Append('"');

                if (SolidBrushValue(item.Stroke, item.StrokeOpacity * item.Opacity) is { } strokeBrush)
                {
                    _builder.Append(" Brush=\"").Append(strokeBrush).Append('"');
                }

                if (item.LineCap is { } cap)
                {
                    _builder.Append(" StartLineCap=\"").Append(cap).Append("\" EndLineCap=\"").Append(cap).Append('"');
                }

                if (item.LineJoin is { } join)
                {
                    _builder.Append(" LineJoin=\"").Append(join).Append('"');
                }

                _builder.AppendLine(" />");
                _builder.AppendLine("                </GeometryDrawing.Pen>");
                _builder.AppendLine("            </GeometryDrawing>");
            }
            else
            {
                _builder.AppendLine(" />");
            }
        }

        _builder.AppendLine("        </DrawingGroup>");
        _builder.AppendLine("    </DrawingImage.Drawing>");
        _builder.AppendLine("</DrawingImage>");
    }

    // ---- one path ------------------------------------------------------------------------

    private void EmitPath(SvgDrawItem item, string indent)
    {
        var data = item.Geometry.ToPathData(options.DecimalPlaces);

        if (data.Length == 0)
        {
            return;
        }

        var fillBrush = SolidBrushValue(item.Fill, item.FillOpacity * item.Opacity);
        var strokeBrush = SolidBrushValue(item.Stroke, item.StrokeOpacity * item.Opacity);

        var fillGradient = GradientFor(item.Fill);
        var strokeGradient = GradientFor(item.Stroke);

        var needsChildren = fillGradient is not null || strokeGradient is not null || item.Clip is not null;

        _builder.Append(indent).Append("<Path");

        if (fillBrush is not null)
        {
            _builder.Append(" Fill=\"").Append(fillBrush).Append('"');
        }

        if (strokeBrush is not null)
        {
            _builder.Append(" Stroke=\"").Append(strokeBrush).Append('"');
        }

        if (item.Stroke is not SvgPaint.None)
        {
            _builder.Append(" StrokeThickness=\"").Append(Number(item.StrokeWidth)).Append('"');

            if (item.LineCap is { } cap)
            {
                _builder.Append(" StrokeStartLineCap=\"").Append(cap)
                    .Append("\" StrokeEndLineCap=\"").Append(cap).Append('"');

                if (options.Flavor == XamlFlavor.Wpf)
                {
                    _builder.Append(" StrokeDashCap=\"").Append(cap).Append('"');
                }
            }

            if (item.LineJoin is { } join)
            {
                _builder.Append(" StrokeLineJoin=\"").Append(join).Append('"');
            }

            if (item.MiterLimit is { } miter)
            {
                _builder.Append(" StrokeMiterLimit=\"").Append(Number(miter)).Append('"');
            }

            if (item.DashArray is { Count: > 0 } dashes)
            {
                _builder.Append(" StrokeDashArray=\"")
                    .Append(string.Join(",", dashes.Select(Number))).Append('"');
            }

            if (item.DashOffset is { } dashOffset && dashOffset != 0)
            {
                _builder.Append(" StrokeDashOffset=\"").Append(Number(dashOffset)).Append('"');
            }
        }

        _builder.Append(" Data=\"").Append(Escape(data)).Append('"');

        if (!needsChildren)
        {
            _builder.AppendLine(" />");
            return;
        }

        _builder.AppendLine(">");

        if (fillGradient is not null)
        {
            EmitGradient("Path.Fill", fillGradient, item.FillOpacity * item.Opacity, indent + "    ");
        }

        if (strokeGradient is not null)
        {
            EmitGradient("Path.Stroke", strokeGradient, item.StrokeOpacity * item.Opacity, indent + "    ");
        }

        if (item.Clip is not null)
        {
            EmitClip(item.Clip, indent + "    ");
        }

        _builder.Append(indent).AppendLine("</Path>");
    }

    /// <summary>
    /// WinUI's <c>UIElement.Clip</c> is a <c>RectangleGeometry</c> and nothing else, so an
    /// arbitrary clip path cannot be expressed there. Saying so beats emitting XAML that
    /// silently fails to load.
    /// </summary>
    private void EmitClip(PathGeometryData clip, string indent)
    {
        if (options.Flavor == XamlFlavor.Wpf)
        {
            _builder.Append(indent).AppendLine("<Path.Clip>");
            _builder.Append(indent).Append("    <PathGeometry Figures=\"")
                .Append(Escape(clip.ToPathData(options.DecimalPlaces))).AppendLine("\" />");
            _builder.Append(indent).AppendLine("</Path.Clip>");
            return;
        }

        notes.Add(new SvgNote(
            SvgNoteKind.Limitation,
            "A clip-path was dropped",
            "In WinUI an element's Clip must be a RectangleGeometry, so an arbitrary clipping shape cannot be expressed. Switch the target to WPF, or apply the clip in your editor."));
    }

    private void EmitGradient(string property, SvgGradient gradient, double opacity, string indent)
    {
        _builder.Append(indent).Append('<').Append(property).AppendLine(">");

        var mapping = gradient.UserSpace ? " MappingMode=\"Absolute\"" : string.Empty;

        switch (gradient)
        {
            case SvgGradient.Linear linear:
                _builder.Append(indent).Append("    <LinearGradientBrush")
                    .Append(" StartPoint=\"").Append(Number(linear.X1)).Append(',').Append(Number(linear.Y1)).Append('"')
                    .Append(" EndPoint=\"").Append(Number(linear.X2)).Append(',').Append(Number(linear.Y2)).Append('"')
                    .Append(mapping)
                    .AppendLine(">");
                EmitStops(gradient, opacity, indent + "        ");
                _builder.Append(indent).AppendLine("    </LinearGradientBrush>");
                break;

            case SvgGradient.Radial radial:
                _builder.Append(indent).Append("    <RadialGradientBrush")
                    .Append(" Center=\"").Append(Number(radial.Cx)).Append(',').Append(Number(radial.Cy)).Append('"')
                    .Append(" GradientOrigin=\"").Append(Number(radial.Fx)).Append(',').Append(Number(radial.Fy)).Append('"')
                    .Append(" RadiusX=\"").Append(Number(radial.R)).Append('"')
                    .Append(" RadiusY=\"").Append(Number(radial.R)).Append('"')
                    .Append(mapping)
                    .AppendLine(">");
                EmitStops(gradient, opacity, indent + "        ");
                _builder.Append(indent).AppendLine("    </RadialGradientBrush>");
                break;
        }

        _builder.Append(indent).Append("</").Append(property).AppendLine(">");
    }

    private void EmitStops(SvgGradient gradient, double opacity, string indent)
    {
        foreach (var stop in gradient.Stops)
        {
            _builder.Append(indent).Append("<GradientStop Offset=\"").Append(Number(stop.Offset))
                .Append("\" Color=\"").Append(stop.Color.WithOpacity(opacity).ToHex()).AppendLine("\" />");
        }
    }

    // ---- helpers --------------------------------------------------------------------------

    private void AppendRootAttributes()
    {
        if (options.IncludeNamespaces)
        {
            _builder.Append(" xmlns=\"").Append(WpfNamespace).Append('"');

            if (!string.IsNullOrWhiteSpace(options.ResourceKey))
            {
                _builder.Append(" xmlns:x=\"").Append(XamlNamespace).Append('"');
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ResourceKey))
        {
            _builder.Append(" x:Key=\"").Append(Escape(options.ResourceKey!)).Append('"');
        }
    }

    private string MergeGeometry(out string? fill)
    {
        var merged = new PathGeometryData();
        var fills = new HashSet<string>(StringComparer.Ordinal);
        string? first = null;

        foreach (var item in document.Items)
        {
            if (item.Geometry.IsEmpty)
            {
                continue;
            }

            merged.AddRange(item.Geometry.Segments);

            // Even-odd anywhere wins: merging non-zero shapes under even-odd would fill holes.
            if (item.Geometry.FillRule == SvgFillRule.EvenOdd)
            {
                merged.FillRule = SvgFillRule.EvenOdd;
            }

            if (SolidBrushValue(item.Fill, item.FillOpacity * item.Opacity) is { } brush)
            {
                fills.Add(brush);
                first ??= brush;
            }
        }

        if (fills.Count > 1)
        {
            notes.Add(new SvgNote(
                SvgNoteKind.Limitation,
                $"The shapes use {fills.Count} different fills, but a single merged path can only have one",
                $"\"{first}\" was used. Choose the Canvas output to keep every colour."));
        }

        if (document.Items.Any(static i => i.Stroke is not SvgPaint.None))
        {
            notes.Add(new SvgNote(
                SvgNoteKind.Limitation,
                "Strokes cannot be carried into a single merged path",
                "Choose the Canvas output to keep them."));
        }

        fill = first;
        return merged.ToPathData(options.DecimalPlaces);
    }

    private SvgGradient? GradientFor(SvgPaint paint) =>
        paint is SvgPaint.Reference reference && document.Gradients.TryGetValue(reference.Id, out var gradient)
            ? gradient
            : null;

    /// <summary>The brush value for a paint, or <see langword="null"/> when it needs an element.</summary>
    private string? SolidBrushValue(SvgPaint paint, double opacity)
    {
        switch (paint)
        {
            case SvgPaint.Solid solid:
                return solid.Color.WithOpacity(Math.Clamp(opacity, 0, 1)).ToHex();

            case SvgPaint.Reference reference when !document.Gradients.ContainsKey(reference.Id):
                notes.Add(new SvgNote(
                    SvgNoteKind.Dropped,
                    $"The paint server \"#{reference.Id}\" is not defined in this file",
                    "That shape was left unpainted."));
                return null;

            default:
                return null;
        }
    }

    private string Number(double value) => PathGeometryData.Number(value, options.DecimalPlaces);

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

}
