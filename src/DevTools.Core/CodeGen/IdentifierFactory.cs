using System.Text;
using DevTools.Core.Text;

namespace DevTools.Core.CodeGen;

/// <summary>
/// Turns arbitrary JSON member names into legal, idiomatic C# identifiers (FR-J45).
/// </summary>
/// <remarks>
/// JSON names are unconstrained; C# identifiers are not. Everything here exists because some
/// real payload does it: <c>"first-name"</c>, <c>"2fa"</c>, <c>"class"</c>, <c>"$ref"</c>,
/// <c>""</c>, and two names that differ only in case.
/// </remarks>
internal static class IdentifierFactory
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this",
        "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    };

    /// <remarks>
    /// Acronyms are deliberately <em>not</em> preserved. .NET naming guidelines PascalCase
    /// acronyms of three letters or more, so <c>XMLHttpRequest</c> should become
    /// <c>XmlHttpRequest</c> and <c>id</c> should become <c>Id</c> — preserving the original
    /// run would produce <c>XMLHttpRequest</c> and <c>ID</c>, which is not how C# is written.
    /// </remarks>
    private static readonly CaseConverterOptions PascalOptions = CaseConverterOptions.Default with
    {
        PreserveAcronyms = false,
    };

    /// <summary>A PascalCase identifier, guaranteed legal and never empty.</summary>
    public static string Pascal(string? name)
    {
        var cleaned = Clean(name);

        if (cleaned.Length == 0)
        {
            return "Value";
        }

        var converted = CaseConverter.Convert(cleaned, LetterCase.Pascal, PascalOptions);
        var result = converted.IsSuccess && !string.IsNullOrWhiteSpace(converted.Value!.Converted)
            ? converted.Value.Converted
            : cleaned;

        result = StripIllegal(result);

        if (result.Length == 0)
        {
            return "Value";
        }

        // An identifier may not start with a digit, but may start with an underscore.
        if (char.IsAsciiDigit(result[0]))
        {
            result = "_" + result;
        }

        return Escape(result);
    }

    /// <summary>Escapes a reserved word with <c>@</c> rather than mangling it.</summary>
    public static string Escape(string identifier) =>
        Keywords.Contains(identifier) ? "@" + identifier : identifier;

    /// <summary>
    /// A type name for the elements of a collection member: <c>"addresses"</c> becomes
    /// <c>Address</c>, so the generated model reads the way a hand-written one would.
    /// </summary>
    public static string Singular(string name)
    {
        var pascal = Pascal(name);
        var bare = pascal.StartsWith('@') ? pascal[1..] : pascal;

        if (bare.Length > 3 && bare.EndsWith("ies", StringComparison.Ordinal))
        {
            return Escape(string.Concat(bare.AsSpan(0, bare.Length - 3), "y"));
        }

        if (bare.Length > 4 && (bare.EndsWith("ches", StringComparison.Ordinal) ||
                                bare.EndsWith("shes", StringComparison.Ordinal) ||
                                bare.EndsWith("sses", StringComparison.Ordinal) ||
                                bare.EndsWith("xes", StringComparison.Ordinal)))
        {
            return Escape(bare[..^2]);
        }

        // "Status" and "Address" are not plurals; only a single trailing 's' is.
        if (bare.Length > 3 && bare.EndsWith('s') && !bare.EndsWith("ss", StringComparison.Ordinal))
        {
            return Escape(bare[..^1]);
        }

        return pascal;
    }

    /// <summary>Replaces characters that cannot appear in an identifier with word breaks.</summary>
    private static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().Trim();
    }

    private static string StripIllegal(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Makes <paramref name="candidate"/> unique within <paramref name="taken"/>, and records it.
    /// </summary>
    public static string Unique(string candidate, ISet<string> taken)
    {
        if (taken.Add(candidate))
        {
            return candidate;
        }

        for (var i = 2; ; i++)
        {
            var attempt = $"{candidate}{i}";
            if (taken.Add(attempt))
            {
                return attempt;
            }
        }
    }
}
