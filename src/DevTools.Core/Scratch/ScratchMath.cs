using System.Globalization;
using System.Text.RegularExpressions;

namespace DevTools.Core.Scratch;

/// <summary>One line of a Math note that produced something: a result to show, or why not.</summary>
/// <param name="LineNumber">1-based line in the note.</param>
/// <param name="Source">The line as written, trimmed, for the results list.</param>
/// <param name="Result">The value as displayed — grouped digits, unit, percent sign.</param>
/// <param name="CopyText">The value as copied — no grouping, so it pastes as a number.</param>
/// <param name="Error">Why a line that looks like a calculation could not be worked out.</param>
public sealed record ScratchMathLine(int LineNumber, string Source, string? Result, string? CopyText, string? Error)
{
    public bool IsError => Error is not null;
}

/// <summary>
/// The notepad calculator behind Scratchpad's Math notes (SP-20…27).
/// </summary>
/// <remarks>
/// <para>
/// Every line is worked out on its own, top to bottom, so a value defined on one line
/// (<c>rate = 1500</c>) can be used on any line below, and changing it recalculates them all.
/// Arithmetic is <see cref="decimal"/>, so <c>0.1 + 0.2</c> is <c>0.3</c>.
/// </para>
/// <para>
/// Lines that read like prose produce nothing rather than an error, because a scratch note is
/// mostly words with sums among them. Only a line that starts like a calculation — a number,
/// a known name, an operator or a bracket — reports why it failed.
/// </para>
/// <para>
/// Unit conversion uses fixed factors only. There is deliberately no currency: rates would
/// have to come from the network, and nothing in DevTools.Core can reach one (P1).
/// </para>
/// </remarks>
public static class ScratchMath
{
    /// <summary>Lines beyond this are not evaluated; a note this long is not a calculation.</summary>
    public const int MaxLines = 5_000;

    private static readonly Regex AssignmentPattern = new(
        @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=(?!=)\s*(?<expr>.+)$", RegexOptions.CultureInvariant);

    private static readonly Regex LabelPattern = new(
        @"^(?<label>[A-Za-z][A-Za-z0-9 _\-]{0,40}):\s*(?<rest>.*)$", RegexOptions.CultureInvariant);

    private static readonly Regex DateOrTimePattern = new(
        @"^(\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}|\d{1,2}:\d{2}(:\d{2})?)\b", RegexOptions.CultureInvariant);

    /// <summary>Evaluates every line and returns the ones with a result or an error.</summary>
    public static IReadOnlyList<ScratchMathLine> Evaluate(string? text)
    {
        var results = new List<ScratchMathLine>();
        if (string.IsNullOrEmpty(text))
        {
            return results;
        }

        var scope = new Scope();
        // The editor ends lines with a bare \r; files use \r\n. Either is a line.
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var count = Math.Min(lines.Length, MaxLines);

        for (var i = 0; i < count; i++)
        {
            var line = EvaluateLine(lines[i], i + 1, scope);
            if (line is not null)
            {
                results.Add(line);
            }
        }

        return results;
    }

    /// <summary>Evaluates a single expression with no surrounding note, for tests and quick checks.</summary>
    public static ScratchMathLine? EvaluateExpression(string expression) =>
        EvaluateLine(expression, 1, new Scope());

    private static ScratchMathLine? EvaluateLine(string raw, int lineNumber, Scope scope)
    {
        var source = raw.Trim();
        var body = StripComment(raw).Trim();

        if (body.Length == 0)
        {
            // A blank line closes the block that sum and avg add up.
            if (source.Length == 0)
            {
                scope.Block.Clear();
            }

            return null;
        }

        // Dates and clock times are notes, not subtractions: 2026-10-07 is not 2009.
        if (DateOrTimePattern.IsMatch(body))
        {
            return null;
        }

        if (LabelPattern.Match(body) is { Success: true } label)
        {
            body = label.Groups["rest"].Value.Trim();
            if (body.Length == 0)
            {
                return null;
            }
        }

        string? assignTo = null;
        if (AssignmentPattern.Match(body) is { Success: true } assignment)
        {
            assignTo = assignment.Groups["name"].Value;
            body = assignment.Groups["expr"].Value.Trim();

            if (Names.IsReserved(assignTo))
            {
                return new ScratchMathLine(lineNumber, source, null, null, $"'{assignTo}' is a built-in name and can't be used for a value.");
            }
        }

        List<Token> tokens = [];
        try
        {
            tokens = Lexer.Tokenize(body);
        }
        catch (MathException ex)
        {
            return Fail(ex.Message);
        }

        if (tokens.Count == 0)
        {
            return null;
        }

        try
        {
            var parser = new Parser(tokens, scope);
            var value = parser.ParseLine();

            // Formatted inside the try: a value that can't be shown as asked (1.5 in hex) is an error too.
            var display = Formatter.Display(value);
            var copy = Formatter.Copy(value);

            if (assignTo is not null)
            {
                scope.Variables[assignTo] = value;
            }

            scope.Previous = value;
            scope.Block.Add(value);

            return new ScratchMathLine(lineNumber, source, display, copy, null);
        }
        catch (MathException ex)
        {
            return Fail(ex.Message);
        }
        catch (OverflowException)
        {
            return Fail("The result is too large.");
        }
        catch (DivideByZeroException)
        {
            return Fail("Division by zero.");
        }

        ScratchMathLine? Fail(string message)
        {
            // An assignment is always meant as maths, so it always explains itself.
            if (assignTo is null && LooksLikeProse(body, tokens, scope))
            {
                return null;
            }

            return new ScratchMathLine(lineNumber, source, null, null, message);
        }
    }

    /// <summary>
    /// True for a line that is words with perhaps a number in them, rather than a calculation
    /// that went wrong. Such lines get no result and no error.
    /// </summary>
    private static bool LooksLikeProse(string body, List<Token> tokens, Scope scope)
    {
        if (tokens.Count == 0)
        {
            // Nothing could be read at all: maths only if it at least starts like maths.
            return !(char.IsAsciiDigit(body[0]) || body[0] is '(' or '-' or '.');
        }

        var first = tokens[0];
        if (first.Kind == TokenKind.Identifier && !Names.IsKnown(first.Text, scope))
        {
            return true;
        }

        if (!tokens.Any(t => t.Kind == TokenKind.Number))
        {
            return true;
        }

        var unknown = tokens.Count(t => t.Kind == TokenKind.Identifier && !Names.IsKnown(t.Text, scope));
        return unknown >= 2;
    }

    /// <summary>Drops a <c>//</c> comment, or a <c>#</c> one at the start or after a space.</summary>
    private static string StripComment(string line)
    {
        var slash = line.IndexOf("//", StringComparison.Ordinal);
        if (slash >= 0)
        {
            line = line[..slash];
        }

        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                return line[..i];
            }
        }

        return line;
    }

    // ================================================================ values

    private enum NumberBase
    {
        Decimal,
        Hex,
        Binary,
        Octal,
    }

    private readonly record struct Quantity(decimal Value, Unit? Unit = null, bool IsPercent = false, NumberBase Base = NumberBase.Decimal)
    {
        public static Quantity Of(decimal value) => new(value);
    }

    private sealed class Scope
    {
        public Dictionary<string, Quantity> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<Quantity> Block { get; } = [];

        public Quantity? Previous { get; set; }
    }

    private sealed class MathException(string message) : Exception(message);

    // ================================================================ units

    private enum Dimension
    {
        Data,
        Time,
        Length,
        Mass,
        Temperature,
    }

    private sealed class Unit
    {
        private readonly decimal _factor;
        private readonly decimal _offset;

        public Unit(string symbol, Dimension dimension, decimal factor, decimal offset = 0)
        {
            Symbol = symbol;
            Dimension = dimension;
            _factor = factor;
            _offset = offset;
        }

        public string Symbol { get; }

        public Dimension Dimension { get; }

        public decimal ToBase(decimal value) => value * _factor + _offset;

        public decimal FromBase(decimal value) => (value - _offset) / _factor;
    }

    private static class Units
    {
        // Matched exactly first, because case carries meaning: b is a bit, B a byte, m a metre.
        private static readonly Dictionary<string, Unit> Exact = new(StringComparer.Ordinal);

        // Then by word, ignoring case: "Bytes", "Minutes", "Celsius".
        private static readonly Dictionary<string, Unit> Words = new(StringComparer.OrdinalIgnoreCase);

        static Units()
        {
            // Data, in bytes.
            var bit = new Unit("b", Dimension.Data, 0.125m);
            var b = new Unit("B", Dimension.Data, 1m);
            Add(bit, ["b"], ["bit", "bits"]);
            Add(b, ["B"], ["byte", "bytes"]);
            AddData("KB", "KiB", 1, ["kB"], "kilobyte", "kibibyte");
            AddData("MB", "MiB", 2, [], "megabyte", "mebibyte");
            AddData("GB", "GiB", 3, [], "gigabyte", "gibibyte");
            AddData("TB", "TiB", 4, [], "terabyte", "tebibyte");
            AddData("PB", "PiB", 5, [], "petabyte", "pebibyte");
            Add(new Unit("Kb", Dimension.Data, 125m), ["Kb", "kb", "kbit"], ["kilobit", "kilobits"]);
            Add(new Unit("Mb", Dimension.Data, 125_000m), ["Mb", "Mbit"], ["megabit", "megabits"]);
            Add(new Unit("Gb", Dimension.Data, 125_000_000m), ["Gb", "Gbit"], ["gigabit", "gigabits"]);

            // Time, in seconds.
            Add(new Unit("ns", Dimension.Time, 0.000000001m), ["ns"], ["nanosecond", "nanoseconds"]);
            Add(new Unit("µs", Dimension.Time, 0.000001m), ["µs", "us"], ["microsecond", "microseconds"]);
            Add(new Unit("ms", Dimension.Time, 0.001m), ["ms"], ["millisecond", "milliseconds"]);
            Add(new Unit("s", Dimension.Time, 1m), ["s"], ["sec", "secs", "second", "seconds"]);
            Add(new Unit("min", Dimension.Time, 60m), ["min"], ["mins", "minute", "minutes"]);
            Add(new Unit("h", Dimension.Time, 3600m), ["h"], ["hr", "hrs", "hour", "hours"]);
            Add(new Unit("d", Dimension.Time, 86_400m), ["d"], ["day", "days"]);
            Add(new Unit("wk", Dimension.Time, 604_800m), ["wk"], ["week", "weeks"]);

            // Length, in metres.
            Add(new Unit("mm", Dimension.Length, 0.001m), ["mm"], ["millimetre", "millimetres", "millimeter", "millimeters"]);
            Add(new Unit("cm", Dimension.Length, 0.01m), ["cm"], ["centimetre", "centimetres", "centimeter", "centimeters"]);
            Add(new Unit("m", Dimension.Length, 1m), ["m"], ["metre", "metres", "meter", "meters"]);
            Add(new Unit("km", Dimension.Length, 1000m), ["km"], ["kilometre", "kilometres", "kilometer", "kilometers"]);
            Add(new Unit("inch", Dimension.Length, 0.0254m), [], ["inch", "inches"]);
            Add(new Unit("ft", Dimension.Length, 0.3048m), ["ft"], ["foot", "feet"]);
            Add(new Unit("yd", Dimension.Length, 0.9144m), ["yd"], ["yard", "yards"]);
            Add(new Unit("mi", Dimension.Length, 1609.344m), ["mi"], ["mile", "miles"]);

            // Mass, in kilograms.
            Add(new Unit("mg", Dimension.Mass, 0.000001m), ["mg"], ["milligram", "milligrams"]);
            Add(new Unit("g", Dimension.Mass, 0.001m), ["g"], ["gram", "grams"]);
            Add(new Unit("kg", Dimension.Mass, 1m), ["kg"], ["kilogram", "kilograms", "kilo", "kilos"]);
            Add(new Unit("lb", Dimension.Mass, 0.45359237m), ["lb", "lbs"], ["pound", "pounds"]);
            Add(new Unit("oz", Dimension.Mass, 0.028349523125m), ["oz"], ["ounce", "ounces"]);

            // Temperature, in kelvin. The offset is why this is not a plain factor.
            Add(new Unit("°C", Dimension.Temperature, 1m, 273.15m), ["°C", "degC"], ["celsius"]);
            Add(new Unit("°F", Dimension.Temperature, 5m / 9m, 273.15m - (32m * 5m / 9m)), ["°F", "degF"], ["fahrenheit"]);
            Add(new Unit("K", Dimension.Temperature, 1m), [], ["kelvin"]);
        }

        private static void AddData(string si, string iec, int power, string[] extra, string siWord, string iecWord)
        {
            decimal siFactor = 1, iecFactor = 1;
            for (var i = 0; i < power; i++)
            {
                siFactor *= 1000;
                iecFactor *= 1024;
            }

            Add(new Unit(si, Dimension.Data, siFactor), [si, .. extra], [siWord, siWord + "s"]);
            Add(new Unit(iec, Dimension.Data, iecFactor), [iec], [iecWord, iecWord + "s"]);
        }

        private static void Add(Unit unit, string[] symbols, string[] words)
        {
            foreach (var symbol in symbols)
            {
                Exact[symbol] = unit;
            }

            foreach (var word in words)
            {
                Words[word] = unit;
            }
        }

        public static Unit? Find(string name) =>
            Exact.TryGetValue(name, out var unit) ? unit
            : Words.TryGetValue(name, out unit) ? unit
            : null;
    }

    // ================================================================ names

    private static class Names
    {
        public static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
        {
            "sqrt", "round", "floor", "ceil", "abs", "min", "max", "log", "ln", "sin", "cos", "tan",
        };

        public static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "of", "in", "to", "as", "mod", "prev", "sum", "total", "avg", "average",
            "hex", "bin", "oct", "dec", "binary", "hexadecimal", "octal", "decimal",
        };

        public static bool IsReserved(string name) =>
            Functions.Contains(name) || Keywords.Contains(name);

        public static bool IsKnown(string name, Scope scope) =>
            IsReserved(name) ||
            scope.Variables.ContainsKey(name) ||
            name.Equals("pi", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("e", StringComparison.Ordinal) ||
            Units.Find(name) is not null;
    }

    // ================================================================ lexer

    private enum TokenKind
    {
        Number,
        Identifier,
        Operator,
        LeftParen,
        RightParen,
        Comma,
        Percent,
    }

    private readonly record struct Token(TokenKind Kind, string Text, decimal Number = 0, NumberBase Base = NumberBase.Decimal);

    private static class Lexer
    {
        public static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            var depth = 0;
            var i = 0;

            while (i < text.Length)
            {
                var c = text[i];

                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
                {
                    tokens.Add(ReadNumber(text, ref i, depth));
                    continue;
                }

                if (char.IsLetter(c) || c == '_' || c == '°' || c == 'µ')
                {
                    var start = i;
                    i++;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Identifier, text[start..i]));
                    continue;
                }

                switch (c)
                {
                    case '+':
                        tokens.Add(new Token(TokenKind.Operator, "+"));
                        break;
                    case '-' or '−' or '–':
                        tokens.Add(new Token(TokenKind.Operator, "-"));
                        break;
                    case '*' or '×' or '·' or '✕':
                        tokens.Add(new Token(TokenKind.Operator, "*"));
                        break;
                    case '/' or '÷':
                        tokens.Add(new Token(TokenKind.Operator, "/"));
                        break;
                    case '^':
                        tokens.Add(new Token(TokenKind.Operator, "^"));
                        break;
                    case '%':
                        tokens.Add(new Token(TokenKind.Percent, "%"));
                        break;
                    case '(':
                        depth++;
                        tokens.Add(new Token(TokenKind.LeftParen, "("));
                        break;
                    case ')':
                        depth--;
                        tokens.Add(new Token(TokenKind.RightParen, ")"));
                        break;
                    case ',':
                        tokens.Add(new Token(TokenKind.Comma, ","));
                        break;
                    default:
                        throw new MathException($"'{c}' isn't something I can calculate with.");
                }

                i++;
            }

            return tokens;
        }

        private static Token ReadNumber(string text, ref int i, int depth)
        {
            var start = i;

            // 0x1F, 0b1010, 0o17.
            if (text[i] == '0' && i + 1 < text.Length && text[i + 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
            {
                var marker = char.ToLowerInvariant(text[i + 1]);
                var (radix, numberBase) = marker switch
                {
                    'x' => (16, NumberBase.Hex),
                    'b' => (2, NumberBase.Binary),
                    _ => (8, NumberBase.Octal),
                };

                var j = i + 2;
                decimal value = 0;
                var digits = 0;

                while (j < text.Length)
                {
                    var d = DigitValue(text[j]);
                    if (text[j] == '_')
                    {
                        j++;
                        continue;
                    }

                    if (d < 0 || d >= radix)
                    {
                        break;
                    }

                    value = checked(value * radix + d);
                    digits++;
                    j++;
                }

                // "0b" followed by nothing it can read is a zero and a unit ("0 B"), not a prefix.
                if (digits > 0 && (j >= text.Length || !char.IsLetterOrDigit(text[j])))
                {
                    i = j;
                    return new Token(TokenKind.Number, text[start..j], value, numberBase);
                }
            }

            var builder = new System.Text.StringBuilder();
            var seenDot = false;

            while (i < text.Length)
            {
                var c = text[i];

                if (char.IsAsciiDigit(c))
                {
                    builder.Append(c);
                    i++;
                }
                else if (c == '_' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
                {
                    i++;
                }
                else if (c == '.' && !seenDot && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
                {
                    seenDot = true;
                    builder.Append('.');
                    i++;
                }
                else if (c == ',' && depth == 0 && !seenDot && IsThousandsGroup(text, i))
                {
                    // 1,500,000 outside a function call; inside one, a comma separates arguments.
                    i++;
                }
                else
                {
                    break;
                }
            }

            // Scientific notation, only when digits follow, so "2e" stays two times e.
            if (i + 1 < text.Length && text[i] is 'e' or 'E')
            {
                var j = i + 1;
                if (j < text.Length && text[j] is '+' or '-')
                {
                    j++;
                }

                if (j < text.Length && char.IsAsciiDigit(text[j]))
                {
                    var expStart = j;
                    while (j < text.Length && char.IsAsciiDigit(text[j]))
                    {
                        j++;
                    }

                    var exponent = int.Parse(text.AsSpan(i + 1, j - i - 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    i = j;
                    var mantissa = ParseDecimal(builder.ToString());
                    return new Token(TokenKind.Number, text[start..i], Scale(mantissa, exponent));
                }
            }

            var number = ParseDecimal(builder.ToString());

            // 2.5k: a thousand, when the k is stuck to the number and ends the word.
            if (i < text.Length && text[i] is 'k' or 'K' && (i + 1 >= text.Length || !char.IsLetterOrDigit(text[i + 1])))
            {
                i++;
                number = checked(number * 1000);
            }

            return new Token(TokenKind.Number, text[start..i], number);
        }

        private static bool IsThousandsGroup(string text, int comma)
        {
            var digits = 0;
            var j = comma + 1;
            while (j < text.Length && char.IsAsciiDigit(text[j]))
            {
                digits++;
                j++;
            }

            return digits == 3;
        }

        private static decimal ParseDecimal(string digits)
        {
            if (!decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            {
                throw new MathException("That number is too large.");
            }

            return value;
        }

        private static decimal Scale(decimal mantissa, int exponent)
        {
            if (Math.Abs(exponent) > 28)
            {
                throw new MathException("That number is outside the range I can work with exactly.");
            }

            var factor = 1m;
            for (var k = 0; k < Math.Abs(exponent); k++)
            {
                factor *= 10;
            }

            return exponent >= 0 ? checked(mantissa * factor) : mantissa / factor;
        }

        private static int DigitValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }

    // ================================================================ parser

    /// <summary>Recursive descent over one line; evaluates as it parses.</summary>
    private sealed class Parser(List<Token> tokens, Scope scope)
    {
        private int _position;

        private Token? Peek => _position < tokens.Count ? tokens[_position] : null;

        public Quantity ParseLine()
        {
            var value = ParseConversion();

            if (Peek is { } extra)
            {
                throw new MathException($"I didn't expect '{extra.Text}' there.");
            }

            return value;
        }

        // conversion := additive ( (in | to | as) target )?
        private Quantity ParseConversion()
        {
            var value = ParseAdditive();

            if (Peek is { Kind: TokenKind.Identifier } keyword && keyword.Text.ToLowerInvariant() is "in" or "to" or "as")
            {
                _position++;

                // 300 as % of 1500
                if (Peek is { Kind: TokenKind.Percent })
                {
                    _position++;
                    ExpectWord("of");
                    var whole = ParseAdditive();
                    var (part, total) = Align(value, whole, "compare");
                    if (total == 0)
                    {
                        throw new DivideByZeroException();
                    }

                    return new Quantity(part / total, IsPercent: true);
                }

                if (Peek is not { Kind: TokenKind.Identifier } target)
                {
                    throw new MathException($"Convert to what? Put a unit, hex, bin or oct after '{keyword.Text}'.");
                }

                _position++;
                var targetName = target.Text;

                var numberBase = targetName.ToLowerInvariant() switch
                {
                    "hex" or "hexadecimal" => NumberBase.Hex,
                    "bin" or "binary" => NumberBase.Binary,
                    "oct" or "octal" => NumberBase.Octal,
                    "dec" or "decimal" => NumberBase.Decimal,
                    _ => (NumberBase?)null,
                };

                if (numberBase is { } nb)
                {
                    if (value.Unit is not null)
                    {
                        throw new MathException($"A value in {value.Unit.Symbol} can't be shown in {targetName}.");
                    }

                    return value with { Base = nb, IsPercent = false };
                }

                var unit = Units.Find(targetName)
                    ?? throw new MathException($"I don't know the unit '{targetName}'.");

                return Convert(value, unit);
            }

            return value;
        }

        // additive := multiplicative (( + | - ) multiplicative)*
        private Quantity ParseAdditive()
        {
            var left = ParseMultiplicative();

            while (Peek is { Kind: TokenKind.Operator } op && op.Text is "+" or "-")
            {
                _position++;
                var right = ParseMultiplicative();
                var subtract = op.Text == "-";

                // 1500 + 18%  →  1500 × 1.18
                if (right.IsPercent && !left.IsPercent)
                {
                    var factor = subtract ? 1 - right.Value : 1 + right.Value;
                    left = left with { Value = checked(left.Value * factor), Base = NumberBase.Decimal };
                    continue;
                }

                var (a, b) = Align(left, right, subtract ? "subtract" : "add");
                var unit = left.Unit ?? right.Unit;
                left = new Quantity(subtract ? checked(a - b) : checked(a + b), unit, left.IsPercent && right.IsPercent);
            }

            return left;
        }

        // multiplicative := unary (( * | / | mod | of ) unary)*
        private Quantity ParseMultiplicative()
        {
            var left = ParseUnary();

            while (true)
            {
                string? op = Peek switch
                {
                    { Kind: TokenKind.Operator, Text: "*" or "/" } t => t.Text,
                    { Kind: TokenKind.Identifier } t when t.Text.Equals("mod", StringComparison.OrdinalIgnoreCase) => "mod",
                    { Kind: TokenKind.Identifier } t when t.Text.Equals("of", StringComparison.OrdinalIgnoreCase) => "*",
                    { Kind: TokenKind.Percent } when IsModuloPercent() => "mod",
                    _ => null,
                };

                if (op is null)
                {
                    return left;
                }

                _position++;
                var right = ParseUnary();
                left = op switch
                {
                    "*" => Multiply(left, right),
                    "/" => Divide(left, right),
                    _ => Modulo(left, right),
                };
            }
        }

        // unary := (+ | -) unary | power
        private Quantity ParseUnary()
        {
            if (Peek is { Kind: TokenKind.Operator } op && op.Text is "-" or "+")
            {
                _position++;
                var operand = ParseUnary();
                return op.Text == "-" ? operand with { Value = -operand.Value } : operand;
            }

            return ParsePower();
        }

        // power := postfix (^ unary)?   — right-associative, binding tighter than unary minus
        private Quantity ParsePower()
        {
            var value = ParsePostfix();

            if (Peek is { Kind: TokenKind.Operator, Text: "^" })
            {
                _position++;
                var exponent = ParseUnary();

                if (value.Unit is not null || exponent.Unit is not null)
                {
                    throw new MathException("Units can't be raised to a power.");
                }

                return new Quantity(Power(value.Value, exponent.Value));
            }

            return value;
        }

        // postfix := primary %? unit?
        private Quantity ParsePostfix()
        {
            var value = ParsePrimary();

            if (Peek is { Kind: TokenKind.Percent } && !IsModuloPercent())
            {
                _position++;
                value = new Quantity(value.Value / 100, IsPercent: true);
            }

            if (Peek is { Kind: TokenKind.Identifier } word && !value.IsPercent && value.Unit is null &&
                !Names.Keywords.Contains(word.Text) && Units.Find(word.Text) is { } unit)
            {
                _position++;
                value = value with { Unit = unit, Base = NumberBase.Decimal };
            }

            return value;
        }

        private Quantity ParsePrimary()
        {
            var token = Peek ?? throw new MathException("The calculation ends too early.");
            _position++;

            switch (token.Kind)
            {
                case TokenKind.Number:
                    return new Quantity(token.Number, Base: NumberBase.Decimal);

                case TokenKind.LeftParen:
                {
                    var inner = ParseConversion();
                    Expect(TokenKind.RightParen, "A closing bracket is missing.");
                    return inner;
                }

                case TokenKind.Identifier:
                    return Identifier(token.Text);

                default:
                    throw new MathException($"I didn't expect '{token.Text}' there.");
            }
        }

        private Quantity Identifier(string name)
        {
            if (Names.Functions.Contains(name))
            {
                return Function(name);
            }

            if (scope.Variables.TryGetValue(name, out var variable))
            {
                return variable;
            }

            switch (name.ToLowerInvariant())
            {
                case "prev":
                    return scope.Previous ?? throw new MathException("There's no result above to use as 'prev'.");
                case "sum" or "total":
                    return Sum("sum");
                case "avg" or "average":
                {
                    if (scope.Block.Count == 0)
                    {
                        throw new MathException("There are no results above to average.");
                    }

                    var total = Sum("average");
                    return total with { Value = total.Value / scope.Block.Count };
                }
                case "pi":
                    return Quantity.Of(3.1415926535897932384626433833m);
            }

            if (name == "e")
            {
                return Quantity.Of(2.7182818284590452353602874714m);
            }

            throw new MathException($"I don't know what '{name}' is. Define it first, e.g. {name} = 10.");
        }

        private Quantity Sum(string verb)
        {
            if (scope.Block.Count == 0)
            {
                return Quantity.Of(0);
            }

            var first = scope.Block[0];
            var total = 0m;

            foreach (var item in scope.Block)
            {
                var (_, b) = Align(first, item, verb);
                total = checked(total + b);
            }

            return new Quantity(total, first.Unit);
        }

        private Quantity Function(string name)
        {
            Expect(TokenKind.LeftParen, $"'{name}' needs brackets, e.g. {name}(2).");

            var args = new List<Quantity>();
            if (Peek is not { Kind: TokenKind.RightParen })
            {
                args.Add(ParseConversion());
                while (Peek is { Kind: TokenKind.Comma })
                {
                    _position++;
                    args.Add(ParseConversion());
                }
            }

            Expect(TokenKind.RightParen, "A closing bracket is missing.");

            var lower = name.ToLowerInvariant();

            if (lower is "min" or "max")
            {
                if (args.Count == 0)
                {
                    throw new MathException($"'{name}' needs at least one value.");
                }

                var best = args[0];
                foreach (var arg in args.Skip(1))
                {
                    var (a, b) = Align(best, arg, "compare");
                    if (lower == "min" ? b < a : b > a)
                    {
                        best = arg;
                    }
                }

                return best;
            }

            if (lower == "round")
            {
                if (args.Count is < 1 or > 2)
                {
                    throw new MathException("round takes a value and optionally the number of decimals: round(2.345, 2).");
                }

                var digits = args.Count == 2 ? (int)args[1].Value : 0;
                if (digits is < 0 or > 15)
                {
                    throw new MathException("round can keep 0 to 15 decimals.");
                }

                return args[0] with { Value = Math.Round(args[0].Value, digits, MidpointRounding.AwayFromZero) };
            }

            if (args.Count != 1)
            {
                throw new MathException($"'{name}' takes one value.");
            }

            var x = args[0];

            switch (lower)
            {
                case "abs":
                    return x with { Value = Math.Abs(x.Value) };
                case "floor":
                    return x with { Value = Math.Floor(x.Value) };
                case "ceil":
                    return x with { Value = Math.Ceiling(x.Value) };
            }

            if (x.Unit is not null)
            {
                throw new MathException($"'{name}' works on plain numbers, not {x.Unit.Symbol}.");
            }

            if (lower == "sqrt" && x.Value < 0)
            {
                throw new MathException("A negative number has no real square root.");
            }

            if (lower is "log" or "ln" && x.Value <= 0)
            {
                throw new MathException("A logarithm needs a number above zero.");
            }

            var input = (double)x.Value;
            var result = lower switch
            {
                "sqrt" => Math.Sqrt(input),
                "log" => Math.Log10(input),
                "ln" => Math.Log(input),
                "sin" => Math.Sin(input),
                "cos" => Math.Cos(input),
                _ => Math.Tan(input),
            };

            return Quantity.Of(FromDouble(result));
        }

        /// <summary>A % followed by something to divide by is modulo; otherwise it is a percentage.</summary>
        private bool IsModuloPercent()
        {
            var next = _position + 1 < tokens.Count ? tokens[_position + 1] : (Token?)null;
            return next is { Kind: TokenKind.Number or TokenKind.LeftParen } ||
                   (next is { Kind: TokenKind.Identifier } id &&
                    !Names.Keywords.Contains(id.Text) && Units.Find(id.Text) is null);
        }

        private void Expect(TokenKind kind, string message)
        {
            if (Peek is not { } token || token.Kind != kind)
            {
                throw new MathException(message);
            }

            _position++;
        }

        private void ExpectWord(string word)
        {
            if (Peek is not { Kind: TokenKind.Identifier } token || !token.Text.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                throw new MathException($"Expected '{word}' here.");
            }

            _position++;
        }
    }

    // ================================================================ arithmetic

    /// <summary>
    /// Brings two values to the same unit so they can be added or compared. A plain number
    /// takes the other side's unit — <c>5 GB + 3</c> is 8 GB, as people mean it.
    /// </summary>
    private static (decimal Left, decimal Right) Align(Quantity left, Quantity right, string verb)
    {
        if (left.Unit is null || right.Unit is null)
        {
            return (left.Value, right.Value);
        }

        if (left.Unit.Dimension != right.Unit.Dimension)
        {
            throw new MathException($"Can't {verb} {left.Unit.Symbol} and {right.Unit.Symbol}.");
        }

        if (ReferenceEquals(left.Unit, right.Unit))
        {
            return (left.Value, right.Value);
        }

        return (left.Value, left.Unit.FromBase(right.Unit.ToBase(right.Value)));
    }

    private static Quantity Convert(Quantity value, Unit target)
    {
        if (value.Unit is null)
        {
            return new Quantity(value.Value, target);
        }

        if (value.Unit.Dimension != target.Dimension)
        {
            throw new MathException($"Can't convert {value.Unit.Symbol} to {target.Symbol}.");
        }

        return new Quantity(target.FromBase(value.Unit.ToBase(value.Value)), target);
    }

    private static Quantity Multiply(Quantity left, Quantity right)
    {
        if (left.Unit is not null && right.Unit is not null)
        {
            throw new MathException($"Can't multiply {left.Unit.Symbol} by {right.Unit.Symbol}.");
        }

        return new Quantity(checked(left.Value * right.Value), left.Unit ?? right.Unit);
    }

    private static Quantity Divide(Quantity left, Quantity right)
    {
        if (right.Value == 0)
        {
            throw new DivideByZeroException();
        }

        // 10 GB / 2 GB is a ratio; 10 GB / 2 is still a size.
        if (left.Unit is not null && right.Unit is not null)
        {
            var (a, b) = Align(left, right, "divide");
            return Quantity.Of(a / b);
        }

        if (right.Unit is not null)
        {
            throw new MathException($"Can't divide a plain number by {right.Unit.Symbol}.");
        }

        return new Quantity(left.Value / right.Value, left.Unit);
    }

    private static Quantity Modulo(Quantity left, Quantity right)
    {
        if (right.Value == 0)
        {
            throw new DivideByZeroException();
        }

        var (a, b) = Align(left, right, "take the remainder of");
        return new Quantity(a % b, left.Unit);
    }

    private static decimal Power(decimal value, decimal exponent)
    {
        // Whole exponents stay exact; anything else goes through double.
        if (exponent == Math.Truncate(exponent) && Math.Abs(exponent) <= 1000)
        {
            var n = (int)Math.Abs(exponent);
            var result = 1m;
            var b = value;

            while (n > 0)
            {
                if ((n & 1) == 1)
                {
                    result = checked(result * b);
                }

                n >>= 1;
                if (n > 0)
                {
                    b = checked(b * b);
                }
            }

            if (exponent < 0)
            {
                if (result == 0)
                {
                    throw new DivideByZeroException();
                }

                return 1 / result;
            }

            return result;
        }

        if (value < 0)
        {
            throw new MathException("A negative number to a fractional power has no real result.");
        }

        return FromDouble(Math.Pow((double)value, (double)exponent));
    }

    private static decimal FromDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 7.9e28)
        {
            throw new MathException("The result is outside the range I can show.");
        }

        // Rounded to what a double actually knows, so sqrt(2)^2 does not show 2.0000000000000004.
        return Math.Round((decimal)value, 12);
    }

    // ================================================================ output

    private static class Formatter
    {
        public static string Display(Quantity value) => Format(value, grouped: true);

        public static string Copy(Quantity value) => Format(value, grouped: false);

        private static string Format(Quantity value, bool grouped)
        {
            if (value.IsPercent)
            {
                return Number(value.Value * 100, grouped) + "%";
            }

            if (value.Base != NumberBase.Decimal)
            {
                return InBase(value.Value, value.Base);
            }

            var text = Number(value.Value, grouped);
            return value.Unit is null ? text : $"{text} {value.Unit.Symbol}";
        }

        private static string Number(decimal value, bool grouped)
        {
            // Six decimals for everyday sizes, more for small values so they don't vanish to 0.
            var places = Math.Abs(value) >= 0.001m || value == 0 ? 6 : 12;
            var rounded = Math.Round(value, places, MidpointRounding.AwayFromZero);
            var format = grouped ? "#,##0." + new string('#', places) : "0." + new string('#', places);
            return rounded.ToString(format, CultureInfo.InvariantCulture);
        }

        private static string InBase(decimal value, NumberBase numberBase)
        {
            if (value != Math.Truncate(value))
            {
                throw new MathException("Only whole numbers can be shown in hex, binary or octal.");
            }

            if (Math.Abs(value) > long.MaxValue)
            {
                throw new MathException("That number is too large to show in another base.");
            }

            var whole = (long)Math.Abs(value);
            var sign = value < 0 ? "-" : string.Empty;

            return numberBase switch
            {
                NumberBase.Hex => $"{sign}0x{whole:X}",
                NumberBase.Binary => $"{sign}0b{System.Convert.ToString(whole, 2)}",
                _ => $"{sign}0o{System.Convert.ToString(whole, 8)}",
            };
        }
    }
}
