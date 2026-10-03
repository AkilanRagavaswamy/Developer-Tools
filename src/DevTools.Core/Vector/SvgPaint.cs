using System.Globalization;

namespace DevTools.Core.Vector;

/// <summary>An sRGB colour with an alpha channel, in the byte form XAML writes.</summary>
internal readonly record struct SvgColor(byte A, byte R, byte G, byte B)
{
    public static SvgColor Black { get; } = new(255, 0, 0, 0);

    public string ToHex() => A == 255
        ? $"#{R:X2}{G:X2}{B:X2}"
        : $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    public SvgColor WithOpacity(double opacity)
    {
        var alpha = Math.Clamp(A / 255.0 * opacity, 0, 1);
        return this with { A = (byte)Math.Round(alpha * 255) };
    }
}

/// <summary>What a <c>fill</c> or <c>stroke</c> resolved to.</summary>
internal abstract record SvgPaint
{
    /// <summary>The paint is explicitly <c>none</c>: nothing is drawn, which is not the same as black.</summary>
    public sealed record None : SvgPaint;

    public sealed record Solid(SvgColor Color) : SvgPaint;

    /// <summary>A <c>url(#id)</c> reference, resolved against the document's gradients.</summary>
    public sealed record Reference(string Id) : SvgPaint;

    /// <summary>The value was <c>currentColor</c>, resolved from the inherited <c>color</c>.</summary>
    public sealed record Current : SvgPaint;
}

/// <summary>Parses SVG colour and paint syntax (FR-V05).</summary>
internal static class SvgColorParser
{
    public static SvgPaint ParsePaint(string? value)
    {
        var text = value?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            return new SvgPaint.None();
        }

        if (text.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            return new SvgPaint.None();
        }

        if (text.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            return new SvgPaint.Current();
        }

        if (text.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            var close = text.IndexOf(')', StringComparison.Ordinal);
            var inner = close > 4 ? text[4..close].Trim() : string.Empty;
            inner = inner.Trim('\'', '"');

            if (inner.StartsWith('#'))
            {
                inner = inner[1..];
            }

            return inner.Length > 0 ? new SvgPaint.Reference(inner) : new SvgPaint.None();
        }

        return TryParseColor(text, out var color) ? new SvgPaint.Solid(color) : new SvgPaint.None();
    }

    public static bool TryParseColor(string? value, out SvgColor color)
    {
        color = SvgColor.Black;

        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.StartsWith('#'))
        {
            return TryParseHex(text[1..], out color);
        }

        if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRgb(text, out color);
        }

        if (text.StartsWith("hsl", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseHsl(text, out color);
        }

        return SvgNamedColors.TryGet(text, out color);
    }

    private static bool TryParseHex(string hex, out SvgColor color)
    {
        color = SvgColor.Black;

        static bool Nibble(char c, out int value)
        {
            value = 0;

            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }

            value = Convert.ToInt32(c.ToString(), 16);
            return true;
        }

        switch (hex.Length)
        {
            // #rgb and #rgba: each digit is doubled, so 'f' means 0xFF.
            case 3 or 4:
            {
                Span<int> parts = stackalloc int[4] { 0, 0, 0, 15 };

                for (var i = 0; i < hex.Length; i++)
                {
                    if (!Nibble(hex[i], out parts[i]))
                    {
                        return false;
                    }
                }

                color = new SvgColor(
                    (byte)(parts[3] * 17), (byte)(parts[0] * 17), (byte)(parts[1] * 17), (byte)(parts[2] * 17));
                return true;
            }

            case 6 or 8:
            {
                if (!byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
                    !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
                    !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    return false;
                }

                byte a = 255;
                if (hex.Length == 8 &&
                    !byte.TryParse(hex.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a))
                {
                    return false;
                }

                color = new SvgColor(a, r, g, b);
                return true;
            }

            default:
                return false;
        }
    }

    private static bool TryParseRgb(string text, out SvgColor color)
    {
        color = SvgColor.Black;

        var open = text.IndexOf('(', StringComparison.Ordinal);
        var close = text.LastIndexOf(')');

        if (open < 0 || close <= open)
        {
            return false;
        }

        var parts = Split(text[(open + 1)..close]);

        if (parts.Count is < 3 or > 4)
        {
            return false;
        }

        if (!TryChannel(parts[0], out var r) || !TryChannel(parts[1], out var g) || !TryChannel(parts[2], out var b))
        {
            return false;
        }

        byte a = 255;
        if (parts.Count == 4)
        {
            if (!TryAlpha(parts[3], out var alpha))
            {
                return false;
            }

            a = alpha;
        }

        color = new SvgColor(a, r, g, b);
        return true;
    }

    private static bool TryParseHsl(string text, out SvgColor color)
    {
        color = SvgColor.Black;

        var open = text.IndexOf('(', StringComparison.Ordinal);
        var close = text.LastIndexOf(')');

        if (open < 0 || close <= open)
        {
            return false;
        }

        var parts = Split(text[(open + 1)..close]);

        if (parts.Count is < 3 or > 4)
        {
            return false;
        }

        if (!TryNumber(parts[0], out var h) ||
            !TryPercent(parts[1], out var s) ||
            !TryPercent(parts[2], out var l))
        {
            return false;
        }

        byte a = 255;
        if (parts.Count == 4)
        {
            if (!TryAlpha(parts[3], out var alpha))
            {
                return false;
            }

            a = alpha;
        }

        // Normalise the hue onto [0, 360) so hsl(-90, …) and hsl(450, …) behave.
        h = ((h % 360) + 360) % 360;

        var c = (1 - Math.Abs((2 * l) - 1)) * s;
        var x = c * (1 - Math.Abs((h / 60 % 2) - 1));
        var m = l - (c / 2);

        var (r1, g1, b1) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        color = new SvgColor(
            a,
            (byte)Math.Round(Math.Clamp(r1 + m, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(g1 + m, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(b1 + m, 0, 1) * 255));

        return true;
    }

    private static List<string> Split(string inner) =>
        [.. inner.Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static bool TryChannel(string text, out byte value)
    {
        value = 0;

        if (text.EndsWith('%'))
        {
            if (!TryNumber(text[..^1], out var percent))
            {
                return false;
            }

            value = (byte)Math.Round(Math.Clamp(percent / 100.0, 0, 1) * 255);
            return true;
        }

        if (!TryNumber(text, out var number))
        {
            return false;
        }

        value = (byte)Math.Round(Math.Clamp(number, 0, 255));
        return true;
    }

    private static bool TryAlpha(string text, out byte value)
    {
        value = 255;

        if (text.EndsWith('%'))
        {
            if (!TryNumber(text[..^1], out var percent))
            {
                return false;
            }

            value = (byte)Math.Round(Math.Clamp(percent / 100.0, 0, 1) * 255);
            return true;
        }

        if (!TryNumber(text, out var number))
        {
            return false;
        }

        value = (byte)Math.Round(Math.Clamp(number, 0, 1) * 255);
        return true;
    }

    private static bool TryPercent(string text, out double value)
    {
        var trimmed = text.EndsWith('%') ? text[..^1] : text;

        if (!TryNumber(trimmed, out value))
        {
            return false;
        }

        value = Math.Clamp(value / 100.0, 0, 1);
        return true;
    }

    internal static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
