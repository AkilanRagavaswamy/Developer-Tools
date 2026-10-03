using System.Globalization;

namespace DevTools.Core.Vector;

/// <summary>
/// Parses the SVG path mini-language into absolute segments (FR-V03).
/// </summary>
/// <remarks>
/// The grammar is more permissive than it looks, and every allowance here exists because real
/// files rely on it: commands repeat implicitly (<c>L 1 1 2 2</c> is two lines), separators are
/// optional around signs and decimal points (<c>M1-2.5.5</c> is three numbers), and arc flags
/// are single characters that may be run together with what follows
/// (<c>a1 1 0 011 1</c> means flags <c>0</c> and <c>1</c>, then <c>1 1</c>).
/// </remarks>
internal static class SvgPathGrammar
{
    public static PathGeometryData Parse(string? data, IList<string>? warnings = null)
    {
        var geometry = new PathGeometryData();

        if (string.IsNullOrWhiteSpace(data))
        {
            return geometry;
        }

        var scanner = new Scanner(data);
        var current = new Point(0, 0);
        var subpathStart = new Point(0, 0);

        // The reflected control point for smooth curves (S and T).
        Point? lastCubicControl = null;
        Point? lastQuadControl = null;

        var command = '\0';

        while (true)
        {
            scanner.SkipSeparators();

            if (scanner.AtEnd)
            {
                break;
            }

            if (char.IsLetter(scanner.Current))
            {
                command = scanner.Current;
                scanner.Advance();
            }
            else if (command == '\0')
            {
                warnings?.Add($"The path data starts with '{scanner.Current}' instead of a command; it was ignored.");
                return geometry;
            }
            else if (command is 'M' or 'm')
            {
                // An implicit repeat of a moveto is a lineto, per the specification.
                command = command == 'M' ? 'L' : 'l';
            }
            else if (command is 'Z' or 'z')
            {
                warnings?.Add("The path data has numbers after a 'Z' with no command; they were ignored.");
                return geometry;
            }

            var relative = char.IsLower(command);

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                {
                    if (!scanner.TryPoint(out var p))
                    {
                        return Truncated(geometry, warnings, command);
                    }

                    current = relative ? Offset(current, p) : p;
                    subpathStart = current;
                    geometry.Add(PathSegment.Move(current));
                    lastCubicControl = lastQuadControl = null;
                    break;
                }

                case 'L':
                {
                    if (!scanner.TryPoint(out var p))
                    {
                        return Truncated(geometry, warnings, command);
                    }

                    current = relative ? Offset(current, p) : p;
                    geometry.Add(PathSegment.Line(current));
                    lastCubicControl = lastQuadControl = null;
                    break;
                }

                case 'H':
                {
                    if (!scanner.TryNumber(out var x))
                    {
                        return Truncated(geometry, warnings, command);
                    }

                    current = new Point(relative ? current.X + x : x, current.Y);
                    geometry.Add(PathSegment.Line(current));
                    lastCubicControl = lastQuadControl = null;
                    break;
                }

                case 'V':
                {
                    if (!scanner.TryNumber(out var y))
                    {
                        return Truncated(geometry, warnings, command);
                    }

                    current = new Point(current.X, relative ? current.Y + y : y);
                    geometry.Add(PathSegment.Line(current));
                    lastCubicControl = lastQuadControl = null;
                    break;
                }

                case 'C':
                {
                    if (!scanner.TryPoint(out var c1) || !scanner.TryPoint(out var c2) || !scanner.TryPoint(out var end))
                    {
                        return Truncated(geometry, warnings, command);
                    }

                    if (relative)
                    {
                        c1 = Offset(current, c1);
                        c2 = Offset(current, c2);
                        end = Offset(current, end);
                    }

                    geometry.Add(PathSegment.Cubic(c1, c2, end));
                    lastCubicControl = c2;
                    lastQuadControl = null;
                    current = end;
                    break;
                }

                case 'S':
                {
                    if (!scanner.TryPoint(out var c2) || !scanner.TryPoint(out var end))
                    {
                        return Truncated(geometry, warnings, command);
                    }

                    if (relative)
                    {
                        c2 = Offset(current, c2);
                        end = Offset(current, end);
                    }

                    // The first control point is the reflection of the previous one; with no
                    // previous curve it coincides with the current point.
                    var c1 = lastCubicControl is { } previous ? Reflect(current, previous) : current;

                    geometry.Add(PathSegment.Cubic(c1, c2, end));
                    lastCubicControl = c2;
                    lastQuadControl = null;
                    current = end;
                    break;
                }

                case 'Q':
                {
                    if (!scanner.TryPoint(out var c) || !scanner.TryPoint(out var end))
                    {
                        return Truncated(geometry, warnings, command);
                    }

                    if (relative)
                    {
                        c = Offset(current, c);
                        end = Offset(current, end);
                    }

                    geometry.Add(PathSegment.Quadratic(c, end));
                    lastQuadControl = c;
                    lastCubicControl = null;
                    current = end;
                    break;
                }

                case 'T':
                {
                    if (!scanner.TryPoint(out var end))
                    {
                        return Truncated(geometry, warnings, command);
                    }

                    if (relative)
                    {
                        end = Offset(current, end);
                    }

                    var c = lastQuadControl is { } previous ? Reflect(current, previous) : current;

                    geometry.Add(PathSegment.Quadratic(c, end));
                    lastQuadControl = c;
                    lastCubicControl = null;
                    current = end;
                    break;
                }

                case 'A':
                {
                    if (!scanner.TryNumber(out var rx) || !scanner.TryNumber(out var ry) ||
                        !scanner.TryNumber(out var rotation) ||
                        !scanner.TryFlag(out var largeArc) || !scanner.TryFlag(out var sweep) ||
                        !scanner.TryPoint(out var end))
                    {
                        return Truncated(geometry, warnings, command);
                    }

                    if (relative)
                    {
                        end = Offset(current, end);
                    }

                    SvgArc.AppendAsCubics(geometry, current, rx, ry, rotation, largeArc, sweep, end);
                    current = end;
                    lastCubicControl = lastQuadControl = null;
                    break;
                }

                case 'Z':
                {
                    geometry.Add(PathSegment.Close());
                    current = subpathStart;
                    lastCubicControl = lastQuadControl = null;
                    break;
                }

                default:
                    warnings?.Add($"'{command}' is not an SVG path command; the rest of the path was ignored.");
                    return geometry;
            }
        }

        return geometry;
    }

    private static PathGeometryData Truncated(PathGeometryData geometry, IList<string>? warnings, char command)
    {
        warnings?.Add($"The path data ends in the middle of a '{command}' command; the incomplete part was dropped.");
        return geometry;
    }

    private static Point Offset(Point origin, Point delta) => new(origin.X + delta.X, origin.Y + delta.Y);

    private static Point Reflect(Point about, Point p) => new((2 * about.X) - p.X, (2 * about.Y) - p.Y);

    private sealed class Scanner(string text)
    {
        private int _index;

        public bool AtEnd => _index >= text.Length;

        public char Current => text[_index];

        public void Advance() => _index++;

        public void SkipSeparators()
        {
            while (_index < text.Length && (char.IsWhiteSpace(text[_index]) || text[_index] == ','))
            {
                _index++;
            }
        }

        public bool TryPoint(out Point point)
        {
            point = default;

            if (!TryNumber(out var x) || !TryNumber(out var y))
            {
                return false;
            }

            point = new Point(x, y);
            return true;
        }

        /// <summary>
        /// Reads an arc flag, which is exactly one character. It cannot go through
        /// <see cref="TryNumber"/>: in <c>a1 1 0 011 1</c> the run "011" is the two flags
        /// <c>0</c> and <c>1</c> followed by the number <c>1</c>.
        /// </summary>
        public bool TryFlag(out bool value)
        {
            value = false;
            SkipSeparators();

            if (AtEnd || (Current != '0' && Current != '1'))
            {
                return false;
            }

            value = Current == '1';
            _index++;
            return true;
        }

        public bool TryNumber(out double value)
        {
            value = 0;
            SkipSeparators();

            if (AtEnd)
            {
                return false;
            }

            var start = _index;

            if (text[_index] is '+' or '-')
            {
                _index++;
            }

            var sawDigit = false;

            while (_index < text.Length && char.IsAsciiDigit(text[_index]))
            {
                _index++;
                sawDigit = true;
            }

            if (_index < text.Length && text[_index] == '.')
            {
                _index++;
                while (_index < text.Length && char.IsAsciiDigit(text[_index]))
                {
                    _index++;
                    sawDigit = true;
                }
            }

            if (!sawDigit)
            {
                _index = start;
                return false;
            }

            if (_index < text.Length && (text[_index] is 'e' or 'E'))
            {
                var exponentStart = _index;
                _index++;

                if (_index < text.Length && (text[_index] is '+' or '-'))
                {
                    _index++;
                }

                if (_index < text.Length && char.IsAsciiDigit(text[_index]))
                {
                    while (_index < text.Length && char.IsAsciiDigit(text[_index]))
                    {
                        _index++;
                    }
                }
                else
                {
                    // "1e" with no exponent digits: the 'e' belongs to whatever follows.
                    _index = exponentStart;
                }
            }

            return double.TryParse(
                text.AsSpan(start, _index - start),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }
    }
}

/// <summary>
/// Converts an SVG elliptical arc into cubic Béziers.
/// </summary>
/// <remarks>
/// Implements the endpoint-to-centre parameterisation from SVG 1.1 appendix F.6, including
/// the out-of-range corrections that appendix mandates: zero radii degrade to a straight line,
/// negative radii are made absolute, and radii too small to span the endpoints are scaled up
/// until they fit. Files produced by drawing tools rely on that last one constantly.
/// </remarks>
internal static class SvgArc
{
    public static void AppendAsCubics(
        PathGeometryData geometry,
        Point start,
        double rx,
        double ry,
        double xAxisRotationDegrees,
        bool largeArc,
        bool sweep,
        Point end)
    {
        if (Math.Abs(start.X - end.X) < double.Epsilon && Math.Abs(start.Y - end.Y) < double.Epsilon)
        {
            // Identical endpoints: the arc is omitted entirely, per the specification.
            return;
        }

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);

        if (rx == 0 || ry == 0)
        {
            geometry.Add(PathSegment.Line(end));
            return;
        }

        var phi = xAxisRotationDegrees * Math.PI / 180.0;
        var cosPhi = Math.Cos(phi);
        var sinPhi = Math.Sin(phi);

        var dx2 = (start.X - end.X) / 2.0;
        var dy2 = (start.Y - end.Y) / 2.0;

        var x1p = (cosPhi * dx2) + (sinPhi * dy2);
        var y1p = (-sinPhi * dx2) + (cosPhi * dy2);

        // F.6.6.2 — grow radii that cannot span the chord.
        var lambda = ((x1p * x1p) / (rx * rx)) + ((y1p * y1p) / (ry * ry));
        if (lambda > 1)
        {
            var scale = Math.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        var rxSq = rx * rx;
        var rySq = ry * ry;
        var x1pSq = x1p * x1p;
        var y1pSq = y1p * y1p;

        var numerator = (rxSq * rySq) - (rxSq * y1pSq) - (rySq * x1pSq);
        var denominator = (rxSq * y1pSq) + (rySq * x1pSq);

        var factor = denominator == 0 ? 0 : Math.Sqrt(Math.Max(0, numerator / denominator));

        if (largeArc == sweep)
        {
            factor = -factor;
        }

        var cxp = factor * rx * y1p / ry;
        var cyp = -factor * ry * x1p / rx;

        var cx = (cosPhi * cxp) - (sinPhi * cyp) + ((start.X + end.X) / 2.0);
        var cy = (sinPhi * cxp) + (cosPhi * cyp) + ((start.Y + end.Y) / 2.0);

        var startAngle = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        var deltaAngle = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);

        if (!sweep && deltaAngle > 0)
        {
            deltaAngle -= 2 * Math.PI;
        }
        else if (sweep && deltaAngle < 0)
        {
            deltaAngle += 2 * Math.PI;
        }

        // A cubic approximates a circular arc well up to about 90°; beyond that the error is
        // visible, so the sweep is split.
        var count = Math.Max(1, (int)Math.Ceiling(Math.Abs(deltaAngle) / (Math.PI / 2)));
        var step = deltaAngle / count;
        var alpha = 4.0 / 3.0 * Math.Tan(step / 4.0);

        var theta = startAngle;
        var from = start;

        for (var i = 0; i < count; i++)
        {
            var theta2 = theta + step;

            var cosTheta1 = Math.Cos(theta);
            var sinTheta1 = Math.Sin(theta);
            var cosTheta2 = Math.Cos(theta2);
            var sinTheta2 = Math.Sin(theta2);

            var to = OnEllipse(cx, cy, rx, ry, cosPhi, sinPhi, cosTheta2, sinTheta2);

            var d1 = Derivative(rx, ry, cosPhi, sinPhi, cosTheta1, sinTheta1);
            var d2 = Derivative(rx, ry, cosPhi, sinPhi, cosTheta2, sinTheta2);

            var c1 = new Point(from.X + (alpha * d1.X), from.Y + (alpha * d1.Y));
            var c2 = new Point(to.X - (alpha * d2.X), to.Y - (alpha * d2.Y));

            geometry.Add(PathSegment.Cubic(c1, c2, to));

            from = to;
            theta = theta2;
        }
    }

    private static Point OnEllipse(
        double cx, double cy, double rx, double ry,
        double cosPhi, double sinPhi, double cosTheta, double sinTheta) =>
        new(
            cx + (rx * cosPhi * cosTheta) - (ry * sinPhi * sinTheta),
            cy + (rx * sinPhi * cosTheta) + (ry * cosPhi * sinTheta));

    private static Point Derivative(
        double rx, double ry,
        double cosPhi, double sinPhi, double cosTheta, double sinTheta) =>
        new(
            (-rx * cosPhi * sinTheta) - (ry * sinPhi * cosTheta),
            (-rx * sinPhi * sinTheta) + (ry * cosPhi * cosTheta));

    private static double Angle(double ux, double uy, double vx, double vy)
    {
        var dot = (ux * vx) + (uy * vy);
        var length = Math.Sqrt(((ux * ux) + (uy * uy)) * ((vx * vx) + (vy * vy)));

        if (length == 0)
        {
            return 0;
        }

        var cosine = Math.Clamp(dot / length, -1.0, 1.0);
        var angle = Math.Acos(cosine);

        return (ux * vy) - (uy * vx) < 0 ? -angle : angle;
    }
}
