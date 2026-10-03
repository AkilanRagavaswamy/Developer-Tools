using System.Globalization;
using DevTools.Core.Json;

namespace DevTools.Core.CodeGen;

/// <summary>The C# type a JSON scalar maps to, ordered so that widening is a <c>Math.Max</c>.</summary>
internal enum ScalarKind
{
    /// <summary>Nothing but nulls were ever seen.</summary>
    Unknown = 0,

    Bool = 1,
    Integer = 2,
    Long = 3,
    Decimal = 4,
    Double = 5,
    Guid = 6,
    DateTimeOffset = 7,
    Uri = 8,
    String = 9,

    /// <summary>Irreconcilable evidence — this becomes <c>object</c>, and the caller is told where.</summary>
    Any = 10,
}

/// <summary>An inferred shape: a scalar, an array of a shape, or an object of named shapes.</summary>
internal abstract class JsonShape
{
    public static JsonShape Unknown { get; } = new ScalarShape(ScalarKind.Unknown);
}

internal sealed class ScalarShape(ScalarKind kind) : JsonShape
{
    public ScalarKind Kind { get; } = kind;
}

internal sealed class ArrayShape(JsonShape element) : JsonShape
{
    public JsonShape Element { get; set; } = element;
}

/// <summary>One member of an object shape, with the evidence needed to decide nullability.</summary>
internal sealed class PropertyShape(string name, int order)
{
    public string Name { get; } = name;

    /// <summary>First-seen position, so the emitted members follow the document's own order.</summary>
    public int Order { get; } = order;

    public JsonShape Shape { get; set; } = JsonShape.Unknown;

    /// <summary>How many sampled objects carried this member at all.</summary>
    public int Present { get; set; }

    /// <summary>Whether any sample had it explicitly null.</summary>
    public bool SawNull { get; set; }
}

internal sealed class ObjectShape : JsonShape
{
    private readonly Dictionary<string, PropertyShape> _properties = new(StringComparer.Ordinal);

    /// <summary>How many objects were merged into this shape — the denominator for "always present".</summary>
    public int SampleCount { get; set; }

    public IEnumerable<PropertyShape> Properties => _properties.Values.OrderBy(static p => p.Order);

    public int Count => _properties.Count;

    public PropertyShape GetOrAdd(string name)
    {
        if (!_properties.TryGetValue(name, out var property))
        {
            _properties[name] = property = new PropertyShape(name, _properties.Count);
        }

        return property;
    }

    public bool TryGet(string name, out PropertyShape property) => _properties.TryGetValue(name, out property!);

    /// <summary>
    /// A member is nullable when some sample lacked it, or some sample had it null. That is
    /// the only honest reading of a sample: absence is evidence, not a rounding error.
    /// </summary>
    public bool IsNullable(PropertyShape property) => property.SawNull || property.Present < SampleCount;
}

/// <summary>
/// Infers a type graph from a parsed document by merging the evidence of every sample it
/// can find, rather than trusting the first element of an array (FR-J40).
/// </summary>
internal sealed class ShapeBuilder(bool detectDateGuidUri)
{
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings => _warnings;

    public JsonShape Build(JsonNode node) => Build(node, "$");

    private JsonShape Build(JsonNode node, string path) => node switch
    {
        JsonNull => JsonShape.Unknown,
        JsonBool => new ScalarShape(ScalarKind.Bool),
        JsonNumber number => new ScalarShape(NumberKind(number)),
        JsonString text => new ScalarShape(StringKind(text.Value)),
        JsonArray array => BuildArray(array, path),
        JsonObject obj => BuildObject(obj, path),
        _ => JsonShape.Unknown,
    };

    private JsonShape BuildArray(JsonArray array, string path)
    {
        JsonShape element = JsonShape.Unknown;

        // Every element contributes. An array whose first element happens to be the sparse
        // one is exactly the case a first-element-only generator gets wrong.
        foreach (var item in array.Items)
        {
            element = Merge(element, Build(item, $"{path}[*]"), $"{path}[*]");
        }

        return new ArrayShape(element);
    }

    private JsonShape BuildObject(JsonObject obj, string path)
    {
        var shape = new ObjectShape { SampleCount = 1 };

        foreach (var member in obj.Members)
        {
            var property = shape.GetOrAdd(member.Name);
            var childPath = $"{path}.{member.Name}";

            property.Present = 1;

            if (member.Value is JsonNull)
            {
                property.SawNull = true;
            }
            else
            {
                property.Shape = Merge(property.Shape, Build(member.Value, childPath), childPath);
            }
        }

        return shape;
    }

    /// <summary>Combines two readings of the same position into one that explains both.</summary>
    public JsonShape Merge(JsonShape left, JsonShape right, string path)
    {
        if (ReferenceEquals(left, right))
        {
            return left;
        }

        if (IsUnknown(left))
        {
            return right;
        }

        if (IsUnknown(right))
        {
            return left;
        }

        switch (left, right)
        {
            case (ScalarShape a, ScalarShape b):
                return new ScalarShape(MergeScalar(a.Kind, b.Kind, path));

            case (ArrayShape a, ArrayShape b):
                return new ArrayShape(Merge(a.Element, b.Element, $"{path}[*]"));

            case (ObjectShape a, ObjectShape b):
                return MergeObjects(a, b, path);

            default:
                Warn($"{path} holds more than one kind of value across the sample, so it is generated as 'object'.");
                return new ScalarShape(ScalarKind.Any);
        }
    }

    private ObjectShape MergeObjects(ObjectShape left, ObjectShape right, string path)
    {
        var merged = new ObjectShape { SampleCount = left.SampleCount + right.SampleCount };

        foreach (var property in left.Properties)
        {
            var target = merged.GetOrAdd(property.Name);
            target.Shape = property.Shape;
            target.Present = property.Present;
            target.SawNull = property.SawNull;
        }

        foreach (var property in right.Properties)
        {
            var target = merged.GetOrAdd(property.Name);
            target.Shape = Merge(target.Shape, property.Shape, $"{path}.{property.Name}");
            target.Present += property.Present;
            target.SawNull |= property.SawNull;
        }

        return merged;
    }

    private ScalarKind MergeScalar(ScalarKind a, ScalarKind b, string path)
    {
        if (a == b)
        {
            return a;
        }

        if (a == ScalarKind.Unknown)
        {
            return b;
        }

        if (b == ScalarKind.Unknown)
        {
            return a;
        }

        // Numbers widen along int → long → decimal → double (FR-J41).
        if (IsNumeric(a) && IsNumeric(b))
        {
            return (ScalarKind)Math.Max((int)a, (int)b);
        }

        // Every string-ish reading degrades to string rather than to object: a field that is
        // sometimes a Guid and sometimes free text is still a string.
        if (IsStringLike(a) && IsStringLike(b))
        {
            return ScalarKind.String;
        }

        Warn($"{path} is sometimes {Describe(a)} and sometimes {Describe(b)}, so it is generated as 'object'.");
        return ScalarKind.Any;
    }

    private static bool IsNumeric(ScalarKind kind) =>
        kind is ScalarKind.Integer or ScalarKind.Long or ScalarKind.Decimal or ScalarKind.Double;

    private static bool IsStringLike(ScalarKind kind) =>
        kind is ScalarKind.String or ScalarKind.Guid or ScalarKind.DateTimeOffset or ScalarKind.Uri;

    private static string Describe(ScalarKind kind) => kind switch
    {
        ScalarKind.Bool => "a boolean",
        ScalarKind.Integer or ScalarKind.Long or ScalarKind.Decimal or ScalarKind.Double => "a number",
        ScalarKind.String or ScalarKind.Guid or ScalarKind.DateTimeOffset or ScalarKind.Uri => "a string",
        _ => "another type",
    };

    private static bool IsUnknown(JsonShape shape) =>
        shape is ScalarShape { Kind: ScalarKind.Unknown };

    private static ScalarKind NumberKind(JsonNumber number)
    {
        var canonical = number.Canonical;

        if (JsonNumberText.IsIntegral(number.Raw))
        {
            if (int.TryParse(canonical, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return ScalarKind.Integer;
            }

            if (long.TryParse(canonical, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return ScalarKind.Long;
            }

            // Bigger than long: decimal holds 28 digits, beyond that only double will take it.
            return decimal.TryParse(canonical, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                ? ScalarKind.Decimal
                : ScalarKind.Double;
        }

        // A fractional value keeps its precision in decimal when it fits, which matters for
        // money — the single most common thing in a JSON payload with a decimal point.
        return decimal.TryParse(canonical, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? ScalarKind.Decimal
            : ScalarKind.Double;
    }

    private ScalarKind StringKind(string value)
    {
        if (!detectDateGuidUri || value.Length == 0)
        {
            return ScalarKind.String;
        }

        if (Guid.TryParseExact(value, "D", out _) || Guid.TryParseExact(value, "B", out _))
        {
            return ScalarKind.Guid;
        }

        if (LooksLikeTimestamp(value) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
        {
            return ScalarKind.DateTimeOffset;
        }

        if ((value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
            Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return ScalarKind.Uri;
        }

        return ScalarKind.String;
    }

    /// <summary>
    /// Guards the date probe. <see cref="DateTimeOffset.TryParse(string, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/>
    /// accepts things like "3" and "May", so a bare parse attempt would turn half the strings
    /// in a document into timestamps.
    /// </summary>
    private static bool LooksLikeTimestamp(string value)
    {
        if (value.Length < 8)
        {
            return false;
        }

        // ISO-8601-ish: four digits, a separator, two digits, a separator, two digits.
        return char.IsAsciiDigit(value[0]) && char.IsAsciiDigit(value[1]) &&
               char.IsAsciiDigit(value[2]) && char.IsAsciiDigit(value[3]) &&
               value[4] is '-' or '/' &&
               char.IsAsciiDigit(value[5]) && char.IsAsciiDigit(value[6]) &&
               value[7] is '-' or '/';
    }

    private void Warn(string message)
    {
        if (!_warnings.Contains(message, StringComparer.Ordinal))
        {
            _warnings.Add(message);
        }
    }
}
