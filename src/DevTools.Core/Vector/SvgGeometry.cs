using System.Globalization;
using System.Text;

namespace DevTools.Core.Vector;

/// <summary>A 2-D affine transform, stored the way XAML writes one.</summary>
internal readonly record struct Matrix2D(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
{
    public static Matrix2D Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public bool IsIdentity =>
        M11 == 1 && M12 == 0 && M21 == 0 && M22 == 1 && OffsetX == 0 && OffsetY == 0;

    /// <summary>Applies this transform, then <paramref name="outer"/>.</summary>
    public Matrix2D Then(Matrix2D outer) => new(
        (M11 * outer.M11) + (M12 * outer.M21),
        (M11 * outer.M12) + (M12 * outer.M22),
        (M21 * outer.M11) + (M22 * outer.M21),
        (M21 * outer.M12) + (M22 * outer.M22),
        (OffsetX * outer.M11) + (OffsetY * outer.M21) + outer.OffsetX,
        (OffsetX * outer.M12) + (OffsetY * outer.M22) + outer.OffsetY);

    public Point Apply(Point p) => new(
        (p.X * M11) + (p.Y * M21) + OffsetX,
        (p.X * M12) + (p.Y * M22) + OffsetY);

    public static Matrix2D Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    public static Matrix2D Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    public static Matrix2D Rotate(double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new Matrix2D(cos, sin, -sin, cos, 0, 0);
    }

    public static Matrix2D Rotate(double degrees, double cx, double cy) =>
        Translate(-cx, -cy).Then(Rotate(degrees)).Then(Translate(cx, cy));

    public static Matrix2D SkewX(double degrees) =>
        new(1, 0, Math.Tan(degrees * Math.PI / 180.0), 1, 0, 0);

    public static Matrix2D SkewY(double degrees) =>
        new(1, Math.Tan(degrees * Math.PI / 180.0), 0, 1, 0, 0);
}

internal readonly record struct Point(double X, double Y);

internal enum SegmentKind
{
    Move,
    Line,
    Cubic,
    Quadratic,
    Close,
}

/// <summary>
/// One path segment in absolute coordinates.
/// </summary>
/// <remarks>
/// There is deliberately no arc segment. SVG arcs are converted to cubic Béziers while the
/// path is parsed, because an arc's parameters (radii and x-axis rotation) do not survive an
/// arbitrary affine transform, whereas control points do. Converting first means transforms
/// can simply be applied to every point, and the emitted path data needs no transform at all.
/// </remarks>
internal readonly record struct PathSegment(SegmentKind Kind, Point A, Point B, Point C)
{
    public static PathSegment Move(Point p) => new(SegmentKind.Move, p, default, default);

    public static PathSegment Line(Point p) => new(SegmentKind.Line, p, default, default);

    public static PathSegment Cubic(Point c1, Point c2, Point end) => new(SegmentKind.Cubic, c1, c2, end);

    public static PathSegment Quadratic(Point c, Point end) => new(SegmentKind.Quadratic, c, end, default);

    public static PathSegment Close() => new(SegmentKind.Close, default, default, default);

    public PathSegment Transformed(Matrix2D m) => Kind switch
    {
        SegmentKind.Close => this,
        SegmentKind.Cubic => new PathSegment(Kind, m.Apply(A), m.Apply(B), m.Apply(C)),
        SegmentKind.Quadratic => new PathSegment(Kind, m.Apply(A), m.Apply(B), default),
        _ => new PathSegment(Kind, m.Apply(A), default, default),
    };
}

/// <summary>Non-zero or even-odd, mapping to the <c>F1</c> / <c>F0</c> prefix in XAML path data.</summary>
internal enum SvgFillRule
{
    NonZero,
    EvenOdd,
}

/// <summary>A complete geometry: a segment list plus its fill rule.</summary>
internal sealed class PathGeometryData
{
    public List<PathSegment> Segments { get; } = [];

    public SvgFillRule FillRule { get; set; } = SvgFillRule.NonZero;

    public bool IsEmpty => Segments.Count == 0;

    public void Add(PathSegment segment) => Segments.Add(segment);

    public void AddRange(IEnumerable<PathSegment> segments) => Segments.AddRange(segments);

    public PathGeometryData Transformed(Matrix2D m)
    {
        if (m.IsIdentity)
        {
            return this;
        }

        var result = new PathGeometryData { FillRule = FillRule };

        foreach (var segment in Segments)
        {
            result.Add(segment.Transformed(m));
        }

        return result;
    }

    /// <summary>
    /// Renders the geometry as the XAML path mini-language, which both WPF and WinUI parse
    /// identically — so the output needs no per-platform geometry element tree.
    /// </summary>
    public string ToPathData(int decimals)
    {
        var builder = new StringBuilder();

        if (FillRule == SvgFillRule.EvenOdd)
        {
            builder.Append("F0 ");
        }

        for (var i = 0; i < Segments.Count; i++)
        {
            var segment = Segments[i];

            if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }

            switch (segment.Kind)
            {
                case SegmentKind.Move:
                    builder.Append('M').Append(' ').Append(Pair(segment.A, decimals));
                    break;

                case SegmentKind.Line:
                    builder.Append('L').Append(' ').Append(Pair(segment.A, decimals));
                    break;

                case SegmentKind.Cubic:
                    builder.Append('C').Append(' ')
                        .Append(Pair(segment.A, decimals)).Append(' ')
                        .Append(Pair(segment.B, decimals)).Append(' ')
                        .Append(Pair(segment.C, decimals));
                    break;

                case SegmentKind.Quadratic:
                    builder.Append('Q').Append(' ')
                        .Append(Pair(segment.A, decimals)).Append(' ')
                        .Append(Pair(segment.B, decimals));
                    break;

                case SegmentKind.Close:
                    builder.Append('Z');
                    break;
            }
        }

        return builder.ToString().Trim();
    }

    private static string Pair(Point p, int decimals) =>
        $"{Number(p.X, decimals)},{Number(p.Y, decimals)}";

    internal static string Number(double value, int decimals)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }

        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);

        // -0 renders as "-0", which is legal but looks like a defect in generated output.
        if (rounded == 0)
        {
            return "0";
        }

        return rounded.ToString("0.###########", CultureInfo.InvariantCulture);
    }
}

/// <summary>Builds the standard primitive shapes as path geometry.</summary>
internal static class ShapeBuilder2D
{
    /// <summary>The circular-arc Bézier constant: 4/3 · tan(π/8).</summary>
    private const double Kappa = 0.5522847498307933;

    public static PathGeometryData Rectangle(double x, double y, double width, double height, double rx, double ry)
    {
        var geometry = new PathGeometryData();

        if (width <= 0 || height <= 0)
        {
            return geometry;
        }

        // SVG: a missing radius mirrors the other one, and each is clamped to half the side.
        if (rx < 0 && ry < 0)
        {
            rx = ry = 0;
        }
        else if (rx < 0)
        {
            rx = ry;
        }
        else if (ry < 0)
        {
            ry = rx;
        }

        rx = Math.Min(rx, width / 2);
        ry = Math.Min(ry, height / 2);

        if (rx <= 0 || ry <= 0)
        {
            geometry.Add(PathSegment.Move(new Point(x, y)));
            geometry.Add(PathSegment.Line(new Point(x + width, y)));
            geometry.Add(PathSegment.Line(new Point(x + width, y + height)));
            geometry.Add(PathSegment.Line(new Point(x, y + height)));
            geometry.Add(PathSegment.Close());
            return geometry;
        }

        var cx = rx * Kappa;
        var cy = ry * Kappa;
        var right = x + width;
        var bottom = y + height;

        geometry.Add(PathSegment.Move(new Point(x + rx, y)));
        geometry.Add(PathSegment.Line(new Point(right - rx, y)));
        geometry.Add(PathSegment.Cubic(
            new Point(right - rx + cx, y), new Point(right, y + ry - cy), new Point(right, y + ry)));
        geometry.Add(PathSegment.Line(new Point(right, bottom - ry)));
        geometry.Add(PathSegment.Cubic(
            new Point(right, bottom - ry + cy), new Point(right - rx + cx, bottom), new Point(right - rx, bottom)));
        geometry.Add(PathSegment.Line(new Point(x + rx, bottom)));
        geometry.Add(PathSegment.Cubic(
            new Point(x + rx - cx, bottom), new Point(x, bottom - ry + cy), new Point(x, bottom - ry)));
        geometry.Add(PathSegment.Line(new Point(x, y + ry)));
        geometry.Add(PathSegment.Cubic(
            new Point(x, y + ry - cy), new Point(x + rx - cx, y), new Point(x + rx, y)));
        geometry.Add(PathSegment.Close());

        return geometry;
    }

    public static PathGeometryData Ellipse(double cx, double cy, double rx, double ry)
    {
        var geometry = new PathGeometryData();

        if (rx <= 0 || ry <= 0)
        {
            return geometry;
        }

        var ox = rx * Kappa;
        var oy = ry * Kappa;

        geometry.Add(PathSegment.Move(new Point(cx + rx, cy)));
        geometry.Add(PathSegment.Cubic(
            new Point(cx + rx, cy + oy), new Point(cx + ox, cy + ry), new Point(cx, cy + ry)));
        geometry.Add(PathSegment.Cubic(
            new Point(cx - ox, cy + ry), new Point(cx - rx, cy + oy), new Point(cx - rx, cy)));
        geometry.Add(PathSegment.Cubic(
            new Point(cx - rx, cy - oy), new Point(cx - ox, cy - ry), new Point(cx, cy - ry)));
        geometry.Add(PathSegment.Cubic(
            new Point(cx + ox, cy - ry), new Point(cx + rx, cy - oy), new Point(cx + rx, cy)));
        geometry.Add(PathSegment.Close());

        return geometry;
    }

    public static PathGeometryData Line(double x1, double y1, double x2, double y2)
    {
        var geometry = new PathGeometryData();
        geometry.Add(PathSegment.Move(new Point(x1, y1)));
        geometry.Add(PathSegment.Line(new Point(x2, y2)));
        return geometry;
    }

    public static PathGeometryData Polygon(IReadOnlyList<Point> points, bool close)
    {
        var geometry = new PathGeometryData();

        if (points.Count == 0)
        {
            return geometry;
        }

        geometry.Add(PathSegment.Move(points[0]));

        for (var i = 1; i < points.Count; i++)
        {
            geometry.Add(PathSegment.Line(points[i]));
        }

        if (close)
        {
            geometry.Add(PathSegment.Close());
        }

        return geometry;
    }
}
