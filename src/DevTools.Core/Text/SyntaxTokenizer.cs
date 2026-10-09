namespace DevTools.Core.Text;

/// <summary>What a token is, kept free of any colour or UI notion.</summary>
public enum TokenKind
{
    Plain,
    PropertyName,
    String,
    Number,
    Keyword,
    Comment,
    Punctuation,
    ElementName,
    AttributeName,
}

/// <summary>A run of text of one kind. Spans tile the input in order with no gaps.</summary>
public readonly record struct SyntaxToken(int Start, int Length, TokenKind Kind);

/// <summary>Which grammar to colour with.</summary>
public enum SyntaxLanguage
{
    None,
    Json,
    Xml,
    CSharp,
    Sql,
}

/// <summary>
/// Produces colouring spans for the read-only output panes (FR-T11).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a <em>tokenizer</em>, not a parser: it never fails, never allocates a tree, and
/// keeps working on text that is half-typed or outright invalid — which is the normal state of
/// an editor. The JSON pass in particular does not reuse <see cref="Json.JsonReader"/>, because
/// that one stops at the first error and colouring must not.
/// </para>
/// <para>
/// Output is capped: colouring a very large document costs more than it is worth, and the
/// remainder is simply returned as one plain span.
/// </para>
/// </remarks>
public static class SyntaxTokenizer
{
    /// <summary>Above this the document is left uncoloured; scrolling matters more than colour.</summary>
    public const int MaxColourisedLength = 200_000;

    public static IReadOnlyList<SyntaxToken> Tokenize(string? text, SyntaxLanguage language)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (language == SyntaxLanguage.None || text.Length > MaxColourisedLength)
        {
            return [new SyntaxToken(0, text.Length, TokenKind.Plain)];
        }

        return language switch
        {
            SyntaxLanguage.Json => TokenizeJson(text),
            SyntaxLanguage.Xml => TokenizeXml(text),
            SyntaxLanguage.CSharp => TokenizeCSharp(text),
            SyntaxLanguage.Sql => TokenizeSql(text),
            _ => [new SyntaxToken(0, text.Length, TokenKind.Plain)],
        };
    }

    // ---- SQL ----------------------------------------------------------------------------

    /// <summary>
    /// Colours SQL with the formatter's own scanner, in Standard SQL, so a word is coloured as a
    /// keyword exactly when the formatter would treat it as one.
    /// </summary>
    private static List<SyntaxToken> TokenizeSql(string text)
    {
        var tokens = new List<SyntaxToken>();
        var at = 0;

        foreach (var token in Sql.SqlTokenizer.Tokenize(text, Sql.SqlDialectDefinition.For(Sql.SqlDialect.StandardSql)))
        {
            if (token.Index > at)
            {
                tokens.Add(new SyntaxToken(at, token.Index - at, TokenKind.Plain));
            }

            var kind = token.Type switch
            {
                Sql.SqlTokenType.Reserved or Sql.SqlTokenType.ReservedTopLevel or Sql.SqlTokenType.ReservedTopLevelNoIndent or
                    Sql.SqlTokenType.ReservedNewLine => TokenKind.Keyword,
                Sql.SqlTokenType.OpenParen or Sql.SqlTokenType.CloseParen when token.Length > 1 => TokenKind.Keyword,
                Sql.SqlTokenType.String => TokenKind.String,
                Sql.SqlTokenType.Number => TokenKind.Number,
                Sql.SqlTokenType.LineComment or Sql.SqlTokenType.BlockComment => TokenKind.Comment,
                Sql.SqlTokenType.Placeholder => TokenKind.PropertyName,
                Sql.SqlTokenType.Operator or Sql.SqlTokenType.OpenParen or Sql.SqlTokenType.CloseParen => TokenKind.Punctuation,
                _ => TokenKind.Plain,
            };

            tokens.Add(new SyntaxToken(token.Index, token.Length, kind));
            at = token.Index + token.Length;
        }

        if (at < text.Length)
        {
            tokens.Add(new SyntaxToken(at, text.Length - at, TokenKind.Plain));
        }

        return tokens;
    }

    // ---- JSON ---------------------------------------------------------------------------

    private static List<SyntaxToken> TokenizeJson(string text)
    {
        var tokens = new List<SyntaxToken>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                var start = i;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Plain));
                continue;
            }

            if (c == '"')
            {
                var start = i;
                i = SkipQuoted(text, i, '"');

                // A string followed by a colon is a member name, which is what makes a JSON
                // document readable at a glance.
                var kind = PeekNonSpace(text, i) == ':' ? TokenKind.PropertyName : TokenKind.String;
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            if (c is '/' && i + 1 < text.Length && text[i + 1] is '/' or '*')
            {
                var start = i;
                i = SkipComment(text, i);
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Comment));
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '-' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                var start = i;
                i++;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] is '.' or 'e' or 'E' or '+' or '-'))
                {
                    i++;
                }

                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            if (char.IsLetter(c))
            {
                var start = i;
                while (i < text.Length && char.IsLetter(text[i]))
                {
                    i++;
                }

                var word = text.AsSpan(start, i - start);
                var kind = word is "true" or "false" or "null" ? TokenKind.Keyword : TokenKind.Plain;
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            if (c is '{' or '}' or '[' or ']' or ':' or ',')
            {
                tokens.Add(new SyntaxToken(i, 1, TokenKind.Punctuation));
                i++;
                continue;
            }

            tokens.Add(new SyntaxToken(i, 1, TokenKind.Plain));
            i++;
        }

        return Merge(tokens);
    }

    // ---- XML and XAML --------------------------------------------------------------------

    private static List<SyntaxToken> TokenizeXml(string text)
    {
        var tokens = new List<SyntaxToken>();
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] != '<')
            {
                var start = i;
                while (i < text.Length && text[i] != '<')
                {
                    i++;
                }

                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Plain));
                continue;
            }

            if (text.AsSpan(i).StartsWith("<!--"))
            {
                var start = i;
                var end = text.IndexOf("-->", i, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 3;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Comment));
                continue;
            }

            // '<' plus any '/' or '?' that follows it.
            var punctuationStart = i;
            i++;
            while (i < text.Length && text[i] is '/' or '?' or '!')
            {
                i++;
            }

            tokens.Add(new SyntaxToken(punctuationStart, i - punctuationStart, TokenKind.Punctuation));

            var nameStart = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is ':' or '.' or '_' or '-'))
            {
                i++;
            }

            if (i > nameStart)
            {
                tokens.Add(new SyntaxToken(nameStart, i - nameStart, TokenKind.ElementName));
            }

            // Attributes up to the closing '>'.
            while (i < text.Length && text[i] != '>')
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    var wsStart = i;
                    while (i < text.Length && char.IsWhiteSpace(text[i]))
                    {
                        i++;
                    }

                    tokens.Add(new SyntaxToken(wsStart, i - wsStart, TokenKind.Plain));
                    continue;
                }

                if (text[i] is '"' or '\'')
                {
                    var quoteStart = i;
                    i = SkipQuoted(text, i, text[i]);
                    tokens.Add(new SyntaxToken(quoteStart, i - quoteStart, TokenKind.String));
                    continue;
                }

                if (char.IsLetter(text[i]) || text[i] == '_')
                {
                    var attrStart = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is ':' or '.' or '_' or '-'))
                    {
                        i++;
                    }

                    tokens.Add(new SyntaxToken(attrStart, i - attrStart, TokenKind.AttributeName));
                    continue;
                }

                tokens.Add(new SyntaxToken(i, 1, TokenKind.Punctuation));
                i++;
            }

            if (i < text.Length)
            {
                tokens.Add(new SyntaxToken(i, 1, TokenKind.Punctuation));
                i++;
            }
        }

        return Merge(tokens);
    }

    // ---- C# ------------------------------------------------------------------------------

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "get", "global", "goto", "if", "implicit", "in", "init", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator",
        "out", "override", "params", "partial", "private", "protected", "public", "readonly",
        "record", "ref", "required", "return", "sbyte", "sealed", "set", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try",
        "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "var", "virtual",
        "void", "volatile", "while",
    };

    private static List<SyntaxToken> TokenizeCSharp(string text)
    {
        var tokens = new List<SyntaxToken>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                var start = i;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Plain));
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*')
            {
                var start = i;
                i = SkipComment(text, i);
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Comment));
                continue;
            }

            if (c is '"' or '\'')
            {
                var start = i;
                i = SkipQuoted(text, i, c);
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            if (char.IsLetter(c) || c is '_' or '@')
            {
                var start = i;
                if (c == '@')
                {
                    i++;
                }

                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                var word = text[start..i];
                var bare = word.StartsWith('@') ? word[1..] : word;

                // An attribute name sits between '[' and its arguments; colouring it as a type
                // is what makes a generated model scan the way it does in an editor.
                var kind = CSharpKeywords.Contains(bare)
                    ? TokenKind.Keyword
                    : PreviousNonSpace(text, start) == '[' ? TokenKind.ElementName : TokenKind.Plain;

                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            tokens.Add(new SyntaxToken(i, 1, TokenKind.Punctuation));
            i++;
        }

        return Merge(tokens);
    }

    // ---- shared ---------------------------------------------------------------------------

    private static int SkipQuoted(string text, int i, char quote)
    {
        i++; // opening quote

        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (text[i] == quote)
            {
                return i + 1;
            }

            i++;
        }

        // Unterminated: colour to the end rather than losing the rest of the document.
        return text.Length;
    }

    private static int SkipComment(string text, int i)
    {
        if (text[i + 1] == '/')
        {
            while (i < text.Length && text[i] is not ('\n' or '\r'))
            {
                i++;
            }

            return i;
        }

        var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return end < 0 ? text.Length : end + 2;
    }

    private static char PeekNonSpace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i < text.Length ? text[i] : '\0';
    }

    private static char PreviousNonSpace(string text, int i)
    {
        i--;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            i--;
        }

        return i >= 0 ? text[i] : '\0';
    }

    /// <summary>Joins adjacent spans of the same kind, so the UI builds far fewer Runs.</summary>
    private static List<SyntaxToken> Merge(List<SyntaxToken> tokens)
    {
        if (tokens.Count < 2)
        {
            return tokens;
        }

        var merged = new List<SyntaxToken>(tokens.Count) { tokens[0] };

        for (var i = 1; i < tokens.Count; i++)
        {
            var last = merged[^1];
            var next = tokens[i];

            if (last.Kind == next.Kind && last.Start + last.Length == next.Start)
            {
                merged[^1] = last with { Length = last.Length + next.Length };
            }
            else
            {
                merged.Add(next);
            }
        }

        return merged;
    }
}
