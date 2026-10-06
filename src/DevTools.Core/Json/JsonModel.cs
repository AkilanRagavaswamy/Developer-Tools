namespace DevTools.Core.Json;

/// <summary>The six JSON value kinds.</summary>
public enum JsonKind
{
    Null,
    Bool,
    Number,
    String,
    Array,
    Object,
}

/// <summary>Where a node started in the source text. Line and column are 1-based.</summary>
public readonly record struct JsonPosition(int Line, int Column, int Offset)
{
    public static JsonPosition None => new(0, 0, -1);

    public bool IsKnown => Offset >= 0;

    public override string ToString() => IsKnown ? $"line {Line}, column {Column}" : "unknown";
}

/// <summary>
/// A parsed JSON value.
/// </summary>
/// <remarks>
/// This model deliberately keeps three things <see cref="System.Text.Json"/>'s own DOM
/// discards, because all three are the difference between a formatter, a differ and a code
/// generator agreeing about a document or quietly disagreeing:
/// <list type="bullet">
/// <item>the <em>source text</em> of every number, so <c>1.0</c>, <c>1e10</c> and a 30-digit
/// integer survive a round trip byte-for-byte;</item>
/// <item>object member <em>order</em>, and any <em>duplicate</em> member names, both of which
/// are legal JSON;</item>
/// <item>the <em>position</em> of every node, so an error or a diff can point at a character.</item>
/// </list>
/// </remarks>
public abstract record JsonNode(JsonPosition Position)
{
    public abstract JsonKind Kind { get; }

    /// <summary>A stable, order-independent hash of the value, used by diff array matching.</summary>
    public abstract void HashInto(System.Text.StringBuilder sink);

    /// <summary>The structural hash as a string. Two semantically equal documents share one.</summary>
    public string StructuralHash()
    {
        var sink = new System.Text.StringBuilder();
        HashInto(sink);
        return sink.ToString();
    }
}

public sealed record JsonNull(JsonPosition Position) : JsonNode(Position)
{
    public override JsonKind Kind => JsonKind.Null;

    public override void HashInto(System.Text.StringBuilder sink) => sink.Append("n;");
}

public sealed record JsonBool(bool Value, JsonPosition Position) : JsonNode(Position)
{
    public override JsonKind Kind => JsonKind.Bool;

    public override void HashInto(System.Text.StringBuilder sink) =>
        sink.Append(Value ? "b1;" : "b0;");
}

public sealed record JsonString(string Value, JsonPosition Position) : JsonNode(Position)
{
    public override JsonKind Kind => JsonKind.String;

    public override void HashInto(System.Text.StringBuilder sink) =>
        sink.Append('s').Append(Value.Length).Append(':').Append(Value).Append(';');
}

/// <summary>
/// A number carried as its original source text. Nothing in the pipeline ever routes a
/// number through <see cref="double"/>, which is what keeps precision intact.
/// </summary>
public sealed record JsonNumber(string Raw, JsonPosition Position) : JsonNode(Position)
{
    public override JsonKind Kind => JsonKind.Number;

    /// <summary>
    /// The canonical form used when comparing numbers for equality: <c>1</c>, <c>1.0</c> and
    /// <c>1e0</c> all normalise to the same string, so a differ can call them equal without
    /// losing the originals.
    /// </summary>
    public string Canonical => JsonNumberText.Canonicalize(Raw);

    public override void HashInto(System.Text.StringBuilder sink) =>
        sink.Append('#').Append(Canonical).Append(';');
}

public sealed record JsonArray(IReadOnlyList<JsonNode> Items, JsonPosition Position) : JsonNode(Position)
{
    public override JsonKind Kind => JsonKind.Array;

    public override void HashInto(System.Text.StringBuilder sink)
    {
        sink.Append("[");
        foreach (var item in Items)
        {
            item.HashInto(sink);
        }

        sink.Append("];");
    }
}

/// <summary>One name/value pair of an object, with the position of the name itself.</summary>
public sealed record JsonMember(string Name, JsonNode Value, JsonPosition NamePosition);

public sealed record JsonObject(IReadOnlyList<JsonMember> Members, JsonPosition Position) : JsonNode(Position)
{
    public override JsonKind Kind => JsonKind.Object;

    /// <summary>The first member with this name, or <see langword="null"/>.</summary>
    public JsonNode? Find(string name)
    {
        foreach (var member in Members)
        {
            if (string.Equals(member.Name, name, StringComparison.Ordinal))
            {
                return member.Value;
            }
        }

        return null;
    }

    public override void HashInto(System.Text.StringBuilder sink)
    {
        // Sorted, so two objects that differ only in member order hash identically. That is
        // what lets the differ call them equal in semantic mode.
        var ordered = Members.ToArray();
        if (ordered.Length > 1)
        {
            // Stable, like the ordering it replaces, so duplicate names keep their source order.
            var keys = new (string Name, int Index)[ordered.Length];
            for (var i = 0; i < keys.Length; i++)
            {
                keys[i] = (ordered[i].Name, i);
            }

            Array.Sort(keys, ordered, Comparer<(string Name, int Index)>.Create(static (x, y) =>
            {
                var byName = string.CompareOrdinal(x.Name, y.Name);
                return byName != 0 ? byName : x.Index.CompareTo(y.Index);
            }));
        }

        sink.Append('{');
        foreach (var member in ordered)
        {
            sink.Append(member.Name.Length).Append(':').Append(member.Name).Append('=');
            member.Value.HashInto(sink);
        }

        sink.Append("};");
    }
}

/// <summary>Number-text helpers shared by the model, the differ and the code generator.</summary>
public static class JsonNumberText
{
    /// <summary>
    /// Reduces a JSON number to a canonical decimal string so that <c>1</c>, <c>1.0</c>,
    /// <c>1.00</c>, <c>1e0</c> and <c>0.1e1</c> all compare equal, without ever going through
    /// a binary floating-point type. Returns the trimmed original if the text is not a shape
    /// this understands.
    /// </summary>
    public static string Canonicalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Trim();
        var negative = false;
        var index = 0;

        if (index < text.Length && (text[index] == '-' || text[index] == '+'))
        {
            negative = text[index] == '-';
            index++;
        }

        var intStart = index;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        var integerPart = text[intStart..index];

        var fractionPart = string.Empty;
        if (index < text.Length && text[index] == '.')
        {
            index++;
            var fracStart = index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
            }

            fractionPart = text[fracStart..index];
        }

        var exponent = 0;
        if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
        {
            index++;
            var expNegative = false;
            if (index < text.Length && (text[index] == '-' || text[index] == '+'))
            {
                expNegative = text[index] == '-';
                index++;
            }

            var expStart = index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
            }

            if (index == expStart || !int.TryParse(text[expStart..index], out exponent))
            {
                return text;
            }

            if (expNegative)
            {
                exponent = -exponent;
            }
        }

        if (index != text.Length || integerPart.Length == 0)
        {
            return text;
        }

        // Fold the exponent into a plain digit string, then trim.
        var digits = integerPart + fractionPart;
        var pointFromRight = fractionPart.Length - exponent;

        if (pointFromRight < 0)
        {
            digits += new string('0', -pointFromRight);
            pointFromRight = 0;
        }

        while (pointFromRight > digits.Length - 1)
        {
            digits = "0" + digits;
        }

        var wholeLength = digits.Length - pointFromRight;
        var whole = digits[..wholeLength].TrimStart('0');
        var fraction = digits[wholeLength..].TrimEnd('0');

        if (whole.Length == 0)
        {
            whole = "0";
        }

        var canonical = fraction.Length > 0 ? $"{whole}.{fraction}" : whole;

        // -0 and 0 are the same value.
        if (negative && canonical != "0")
        {
            canonical = "-" + canonical;
        }

        return canonical;
    }

    /// <summary>True when the number has no fractional part and no negative exponent.</summary>
    public static bool IsIntegral(string? raw)
    {
        var canonical = Canonicalize(raw);
        return canonical.Length > 0 && !canonical.Contains('.', StringComparison.Ordinal);
    }
}
