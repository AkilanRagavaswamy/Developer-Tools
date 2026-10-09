using System.Globalization;
using System.Text;
using DevTools.Core.Text;

namespace DevTools.Core.Sql;

/// <summary>The SQL dialects the formatter knows the keywords and quoting rules of.</summary>
public enum SqlDialect
{
    StandardSql,
    TSql,
    MySql,
    MariaDb,
    PostgreSql,
    PlSql,
    Db2,
    Redshift,
    SparkSql,
    N1ql,
}

/// <summary>How reserved words are written in the output.</summary>
public enum SqlKeywordCase
{
    Upper,
    Lower,
    Preserve,
}

/// <summary>Everything the SQL formatter lets the caller choose.</summary>
public sealed record SqlFormatOptions
{
    public SqlDialect Dialect { get; init; } = SqlDialect.StandardSql;

    public IndentStyle IndentStyle { get; init; } = IndentStyle.TwoSpaces;

    public SqlKeywordCase KeywordCase { get; init; } = SqlKeywordCase.Upper;

    /// <summary>Puts the comma at the start of the next line instead of the end of this one.</summary>
    public bool LeadingCommas { get; init; }

    /// <summary>Line breaks after each <c>;</c>: 1 starts the next statement on the next line, 2 leaves a blank line.</summary>
    public int LinesBetweenQueries { get; init; } = 2;

    public static SqlFormatOptions Default { get; } = new();
}

/// <summary>
/// Lays SQL out one clause per line, indenting the columns, conditions and subqueries under them.
/// </summary>
/// <remarks>
/// <para>
/// The layout rules and the dialect keyword tables follow DevToys' port of zeroturnaround's
/// sql-formatter (both MIT), so the output matches what people already know from those tools.
/// </para>
/// <para>
/// The tokenizer is a hand-written scanner rather than their chain of regular expressions:
/// those were rebuilt from keyword lists of hundreds of alternatives on every call, and each
/// token tried up to a dozen of them in turn. The formatter only ever changes whitespace and
/// keyword case — never a string, identifier, number or comment — so a statement cannot change
/// meaning by being formatted.
/// </para>
/// </remarks>
public static class SqlFormatter
{
    public static OperationResult<string> Format(string? sql, SqlFormatOptions? options = null)
    {
        var opts = options ?? SqlFormatOptions.Default;

        if (TextUtil.IsBlank(sql))
        {
            return OperationResult<string>.Ok(string.Empty);
        }

        var text = TextUtil.StripBom(sql);
        var dialect = SqlDialectDefinition.For(opts.Dialect);
        var tokens = SqlTokenizer.Tokenize(text, dialect);
        var output = new SqlLayout(text, tokens, opts).Run();

        return OperationResult<string>.Ok(output);
    }

    /// <summary>The display name of a dialect, for the options list.</summary>
    public static string DisplayName(SqlDialect dialect) => dialect switch
    {
        SqlDialect.StandardSql => "Standard SQL",
        SqlDialect.TSql => "Transact-SQL",
        SqlDialect.MySql => "MySQL",
        SqlDialect.MariaDb => "MariaDB",
        SqlDialect.PostgreSql => "PostgreSQL",
        SqlDialect.PlSql => "Oracle PL/SQL",
        SqlDialect.Db2 => "IBM Db2",
        SqlDialect.Redshift => "Amazon Redshift",
        SqlDialect.SparkSql => "Spark SQL",
        SqlDialect.N1ql => "Couchbase N1QL",
        _ => dialect.ToString(),
    };
}

internal enum SqlTokenType
{
    Word,
    String,
    Reserved,
    ReservedTopLevel,
    ReservedTopLevelNoIndent,
    ReservedNewLine,
    Operator,
    OpenParen,
    CloseParen,
    LineComment,
    BlockComment,
    Number,
    Placeholder,
}

internal readonly record struct SqlToken(int Index, int Length, SqlTokenType Type, int PrecedingWhitespace)
{
    public ReadOnlySpan<char> Text(string source) => source.AsSpan(Index, Length);

    public bool Is(string source, SqlTokenType type, string word) =>
        Type == type && Text(source).Equals(word, StringComparison.OrdinalIgnoreCase);

    public bool IsChar(string source, char c) => Length == 1 && source[Index] == c;
}

/// <summary>The keywords and lexical rules of one dialect, built once and shared.</summary>
internal sealed class SqlDialectDefinition
{
    private static readonly SqlDialectDefinition?[] Cache = new SqlDialectDefinition?[Enum.GetValues<SqlDialect>().Length];

    public static SqlDialectDefinition For(SqlDialect dialect)
    {
        var index = (int)dialect;
        return Cache[index] ??= Build(dialect);
    }

    public required SqlDialect Dialect { get; init; }

    public required KeywordSet TopLevel { get; init; }

    public required KeywordSet NewLine { get; init; }

    public required KeywordSet TopLevelNoIndent { get; init; }

    public required KeywordSet Reserved { get; init; }

    public required string[] StringTypes { get; init; }

    public required string[] OpenParens { get; init; }

    public required string[] CloseParens { get; init; }

    public string IndexedPlaceholders { get; init; } = string.Empty;

    public string NamedPlaceholders { get; init; } = string.Empty;

    public required string[] LineComments { get; init; }

    public string SpecialWordChars { get; init; } = string.Empty;

    /// <summary>Multi-character operators, longest first.</summary>
    public required string[] Operators { get; init; }

    private static SqlDialectDefinition Build(SqlDialect dialect)
    {
        string[] baseOps = ["<>", "<=", ">=", "&&", "||", "!="];
        string[] standardStrings = ["\"\"", "N''", "''", "``", "[]"];
        string[] caseParens = ["(", "CASE"];
        string[] endParens = [")", "END"];

        SqlDialectDefinition Make(
            string reserved, string topLevel, string noIndent, string newLine,
            string[] strings, string[] open, string[] close,
            string indexed, string named, string[] lineComments, string special, string[] extraOps) => new()
            {
                Dialect = dialect,
                // ASC and DESC are missing from every one of the source lists, which left "ORDER BY x desc" half upper case.
                Reserved = new KeywordSet(reserved + "|ASC|DESC"),
                TopLevel = new KeywordSet(topLevel),
                TopLevelNoIndent = new KeywordSet(noIndent),
                NewLine = new KeywordSet(newLine),
                StringTypes = strings,
                OpenParens = open,
                CloseParens = close,
                IndexedPlaceholders = indexed,
                NamedPlaceholders = named,
                LineComments = lineComments,
                SpecialWordChars = special,
                Operators = [.. baseOps.Concat(extraOps).Distinct().OrderByDescending(static o => o.Length)],
            };

        return dialect switch
        {
            SqlDialect.TSql => Make(
                SqlKeywords.TSqlReserved, SqlKeywords.TSqlTopLevel, SqlKeywords.TSqlTopLevelNoIndent, SqlKeywords.TSqlNewLine,
                standardStrings, caseParens, endParens, "", "@", ["--"], "#@$",
                ["!<", "!>", "+=", "-=", "*=", "/=", "%=", "|=", "&=", "^=", "::"]),

            SqlDialect.MySql => Make(
                SqlKeywords.MySqlReserved, SqlKeywords.MySqlTopLevel, SqlKeywords.MySqlTopLevelNoIndent, SqlKeywords.MySqlNewLine,
                ["``", "''", "\"\""], caseParens, endParens, "?", "", ["#", "--"], "#@$",
                [":=", "<<", ">>", "<=>", "->", "->>"]),

            SqlDialect.MariaDb => Make(
                SqlKeywords.MariaDbReserved, SqlKeywords.MariaDbTopLevel, SqlKeywords.MariaDbTopLevelNoIndent, SqlKeywords.MariaDbNewLine,
                ["``", "''", "\"\""], caseParens, endParens, "?", "", ["#", "--"], "#@$",
                [":=", "<<", ">>", "<=>"]),

            SqlDialect.PostgreSql => Make(
                SqlKeywords.PostgreSqlReserved, SqlKeywords.PostgreSqlTopLevel, SqlKeywords.PostgreSqlTopLevelNoIndent, SqlKeywords.PostgreSqlNewLine,
                ["\"\"", "''", "U&''", "U&\"\"", "$$"], caseParens, endParens, "$", ":@", ["--"], "@$",
                ["::", "->>", "->", "~~*", "~~", "!~~*", "!~~", "~*", "!~*", "!~", "!!", "|/", "||/", "<<", ">>"]),

            SqlDialect.PlSql => Make(
                SqlKeywords.PlSqlReserved, SqlKeywords.PlSqlTopLevel, SqlKeywords.PlSqlTopLevelNoIndent, SqlKeywords.PlSqlNewLine,
                ["\"\"", "N''", "''", "``"], caseParens, endParens, "?", ":", ["--"], "_$#.@",
                ["||", "**", ":=", "!="]),

            SqlDialect.Db2 => Make(
                SqlKeywords.Db2Reserved, SqlKeywords.Db2TopLevel, SqlKeywords.Db2TopLevelNoIndent, SqlKeywords.Db2NewLine,
                ["\"\"", "''", "``", "[]"], ["("], [")"], "?", ":", ["--"], "#@",
                ["**", "!>", "||"]),

            SqlDialect.Redshift => Make(
                SqlKeywords.RedshiftReserved, SqlKeywords.RedshiftTopLevel, SqlKeywords.RedshiftTopLevelNoIndent, SqlKeywords.RedshiftNewLine,
                ["\"\"", "''", "``"], ["("], [")"], "?", "@#$", ["--"], "",
                ["|/", "||/", "<<", ">>", "||"]),

            SqlDialect.SparkSql => Make(
                SqlKeywords.SparkSqlReserved, SqlKeywords.SparkSqlTopLevel, SqlKeywords.SparkSqlTopLevelNoIndent, SqlKeywords.SparkSqlNewLine,
                ["\"\"", "''", "``", "{}"], caseParens, endParens, "?", "$", ["--"], "",
                ["<=>", "&&", "||", "=="]),

            SqlDialect.N1ql => Make(
                SqlKeywords.N1qlReserved, SqlKeywords.N1qlTopLevel, SqlKeywords.N1qlTopLevelNoIndent, SqlKeywords.N1qlNewLine,
                ["\"\"", "''", "``"], ["(", "[", "{"], [")", "]", "}"], "", "$", ["#", "--"], "",
                ["==", "!="]),

            _ => Make(
                SqlKeywords.StandardReserved, SqlKeywords.StandardTopLevel, SqlKeywords.StandardTopLevelNoIndent, SqlKeywords.StandardNewLine,
                standardStrings, caseParens, endParens, "?", "@", ["--"], "#@$", []),
        };
    }
}

/// <summary>
/// One category of keywords, indexed by first letter and longest first, so a lookup tries a
/// couple of dozen candidates rather than every keyword in the dialect.
/// </summary>
internal sealed class KeywordSet
{
    private readonly Dictionary<char, string[]> _byFirst = [];

    public KeywordSet(string joined)
    {
        foreach (var group in joined
                     .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .GroupBy(static k => char.ToUpperInvariant(k[0])))
        {
            _byFirst[group.Key] = [.. group.OrderByDescending(static k => k.Length)];
        }
    }

    /// <summary>The length of the longest keyword that starts at <paramref name="at"/>, or 0.</summary>
    public int Match(string text, int at)
    {
        if (!_byFirst.TryGetValue(char.ToUpperInvariant(text[at]), out var candidates))
        {
            return 0;
        }

        var best = 0;
        foreach (var keyword in candidates)
        {
            var length = MatchOne(text, at, keyword);
            if (length > best)
            {
                best = length;
            }
        }

        return best;
    }

    /// <summary>
    /// Matches one keyword case-insensitively, letting each space in it stand for any run of
    /// whitespace, and requiring it to end at a word boundary.
    /// </summary>
    private static int MatchOne(string text, int at, string keyword)
    {
        var i = at;

        for (var k = 0; k < keyword.Length; k++)
        {
            var c = keyword[k];

            if (c == ' ')
            {
                if (i >= text.Length || !char.IsWhiteSpace(text[i]))
                {
                    return 0;
                }

                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                continue;
            }

            if (i >= text.Length || char.ToUpperInvariant(text[i]) != char.ToUpperInvariant(c))
            {
                return 0;
            }

            i++;
        }

        // "SELECTED" is a name, not SELECT followed by "ED".
        if (i < text.Length && SqlTokenizer.IsRegexWordChar(text[i]) && SqlTokenizer.IsRegexWordChar(text[i - 1]))
        {
            return 0;
        }

        return i - at;
    }
}

internal static class SqlTokenizer
{
    public static List<SqlToken> Tokenize(string text, SqlDialectDefinition dialect)
    {
        var tokens = new List<SqlToken>(text.Length / 4);
        var i = 0;

        while (i < text.Length)
        {
            var whitespaceStart = i;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            var previous = tokens.Count > 0 ? tokens[^1] : (SqlToken?)null;
            var (length, type) = Next(text, i, dialect, previous);
            tokens.Add(new SqlToken(i, length, type, i - whitespaceStart));
            i += length;
        }

        return tokens;
    }

    private static (int Length, SqlTokenType Type) Next(string text, int at, SqlDialectDefinition d, SqlToken? previous)
    {
        int length;

        if ((length = LineComment(text, at, d)) > 0)
        {
            return (length, SqlTokenType.LineComment);
        }

        if ((length = BlockComment(text, at)) > 0)
        {
            return (length, SqlTokenType.BlockComment);
        }

        if ((length = StringLiteral(text, at, d)) > 0)
        {
            return (length, SqlTokenType.String);
        }

        if ((length = Paren(text, at, d.OpenParens)) > 0)
        {
            return (length, SqlTokenType.OpenParen);
        }

        if ((length = Paren(text, at, d.CloseParens)) > 0)
        {
            return (length, SqlTokenType.CloseParen);
        }

        if ((length = Placeholder(text, at, d)) > 0)
        {
            return (length, SqlTokenType.Placeholder);
        }

        // A minus starts a negative number only where a value could not have come before it, so
        // "(12, -1)" keeps its sign attached while "a-1" is still a subtraction.
        var signed = previous is not { Type: SqlTokenType.Word or SqlTokenType.Number or SqlTokenType.String or
            SqlTokenType.CloseParen or SqlTokenType.Placeholder };

        if ((length = Number(text, at, signed)) > 0)
        {
            return (length, SqlTokenType.Number);
        }

        // A keyword after a dot is a name: in "orders.from", "from" is a column.
        var afterDot = previous is { Length: 1 } p && text[p.Index] == '.';
        if (!afterDot)
        {
            if ((length = d.TopLevel.Match(text, at)) > 0)
            {
                return (length, SqlTokenType.ReservedTopLevel);
            }

            if ((length = d.NewLine.Match(text, at)) > 0)
            {
                return (length, SqlTokenType.ReservedNewLine);
            }

            if ((length = d.TopLevelNoIndent.Match(text, at)) > 0)
            {
                return (length, SqlTokenType.ReservedTopLevelNoIndent);
            }

            if ((length = d.Reserved.Match(text, at)) > 0)
            {
                return (length, SqlTokenType.Reserved);
            }
        }

        if ((length = Word(text, at, d)) > 0)
        {
            return (length, SqlTokenType.Word);
        }

        return (Operator(text, at, d), SqlTokenType.Operator);
    }

    /// <summary>What a regular expression's <c>\w</c> and <c>\b</c> consider part of a word.</summary>
    public static bool IsRegexWordChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' ||
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.ConnectorPunctuation;

    private static int LineComment(string text, int at, SqlDialectDefinition d)
    {
        foreach (var prefix in d.LineComments)
        {
            if (string.CompareOrdinal(text, at, prefix, 0, prefix.Length) == 0)
            {
                var end = text.AsSpan(at).IndexOfAny('\r', '\n');
                return end < 0 ? text.Length - at : end;
            }
        }

        return 0;
    }

    private static int BlockComment(string text, int at)
    {
        if (at + 1 >= text.Length || text[at] != '/' || text[at + 1] != '*')
        {
            return 0;
        }

        var close = text.IndexOf("*/", at + 2, StringComparison.Ordinal);
        return close < 0 ? text.Length - at : close + 2 - at;
    }

    private static int StringLiteral(string text, int at, SqlDialectDefinition d)
    {
        foreach (var type in d.StringTypes)
        {
            var length = type switch
            {
                "\"\"" => Quoted(text, at, "", '"', backslash: true),
                "''" => Quoted(text, at, "", '\'', backslash: true),
                "N''" => Quoted(text, at, "N", '\'', backslash: true),
                "U&''" => Quoted(text, at, "U&", '\'', backslash: true),
                "U&\"\"" => Quoted(text, at, "U&", '"', backslash: true),
                "``" => Quoted(text, at, "", '`', backslash: false),
                "[]" => Bracketed(text, at, '[', ']'),
                "{}" => Bracketed(text, at, '{', '}'),
                "$$" => DollarQuoted(text, at),
                _ => 0,
            };

            if (length > 0)
            {
                return length;
            }
        }

        return 0;
    }

    /// <summary>
    /// A quoted run: the quote doubled, or (where allowed) backslash-escaped, stays inside it,
    /// and one left open runs to the end rather than failing.
    /// </summary>
    private static int Quoted(string text, int at, string prefix, char quote, bool backslash)
    {
        if (prefix.Length > 0 &&
            (at + prefix.Length >= text.Length ||
             string.Compare(text, at, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) != 0))
        {
            return 0;
        }

        var i = at + prefix.Length;
        if (i >= text.Length || text[i] != quote)
        {
            return 0;
        }

        i++;
        while (i < text.Length)
        {
            var c = text[i];

            if (backslash && c == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (c == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1 - at;
            }

            i++;
        }

        return text.Length - at;
    }

    private static int Bracketed(string text, int at, char open, char close)
    {
        if (text[at] != open)
        {
            return 0;
        }

        var i = at + 1;
        while (i < text.Length)
        {
            if (text[i] == close)
            {
                // "]]" is an escaped bracket inside a T-SQL name.
                if (close == ']' && i + 1 < text.Length && text[i + 1] == ']')
                {
                    i += 2;
                    continue;
                }

                return i + 1 - at;
            }

            i++;
        }

        return text.Length - at;
    }

    /// <summary>PostgreSQL's <c>$tag$ … $tag$</c>.</summary>
    private static int DollarQuoted(string text, int at)
    {
        if (text[at] != '$')
        {
            return 0;
        }

        var i = at + 1;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
        {
            i++;
        }

        if (i >= text.Length || text[i] != '$')
        {
            return 0;
        }

        var tag = text.Substring(at, i + 1 - at);
        var close = text.IndexOf(tag, i + 1, StringComparison.Ordinal);
        return close < 0 ? text.Length - at : close + tag.Length - at;
    }

    private static int Paren(string text, int at, string[] parens)
    {
        foreach (var paren in parens)
        {
            if (paren.Length == 1)
            {
                if (text[at] == paren[0])
                {
                    return 1;
                }

                continue;
            }

            // CASE and END open and close a block like a parenthesis does, but only as whole words.
            if (at + paren.Length <= text.Length &&
                string.Compare(text, at, paren, 0, paren.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
                (at == 0 || !IsRegexWordChar(text[at - 1])) &&
                (at + paren.Length == text.Length || !IsRegexWordChar(text[at + paren.Length])))
            {
                return paren.Length;
            }
        }

        return 0;
    }

    private static int Placeholder(string text, int at, SqlDialectDefinition d)
    {
        var c = text[at];

        // Named first: "@name", ":name", "$name".
        if (d.NamedPlaceholders.Contains(c, StringComparison.Ordinal))
        {
            var i = at + 1;
            while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '.' or '_' or '$'))
            {
                i++;
            }

            if (i > at + 1)
            {
                return i - at;
            }
        }

        // Then indexed: "?", "?1", "$1".
        if (d.IndexedPlaceholders.Contains(c, StringComparison.Ordinal))
        {
            var i = at + 1;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }

            return i - at;
        }

        return 0;
    }

    private static int Number(string text, int at, bool signed)
    {
        var i = at;

        if (signed && text[i] == '-')
        {
            var j = i + 1;
            while (j < text.Length && char.IsWhiteSpace(text[j]))
            {
                j++;
            }

            if (j >= text.Length || !char.IsAsciiDigit(text[j]))
            {
                return 0;
            }

            var rest = Number(text, j, signed: false);
            return rest > 0 ? j - at + rest : 0;
        }

        if (text[i] == '0' && i + 1 < text.Length && text[i + 1] is 'x' or 'X' or 'b' or 'B')
        {
            var hex = text[i + 1] is 'x' or 'X';
            var j = i + 2;
            while (j < text.Length && (hex ? char.IsAsciiHexDigit(text[j]) : text[j] is '0' or '1'))
            {
                j++;
            }

            if (j > i + 2 && (j == text.Length || !IsRegexWordChar(text[j])))
            {
                return j - at;
            }
        }

        if (!char.IsAsciiDigit(text[i]))
        {
            return 0;
        }

        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        if (i + 1 < text.Length && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
        {
            i++;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }
        }

        if (i < text.Length && text[i] is 'e' or 'E')
        {
            var j = i + 1;
            if (j < text.Length && text[j] is '-' or '+')
            {
                j++;
            }

            if (j < text.Length && char.IsAsciiDigit(text[j]))
            {
                while (j < text.Length && char.IsAsciiDigit(text[j]))
                {
                    j++;
                }

                i = j;
            }
        }

        // "1st" is a name, not the number 1 followed by "st".
        return i < text.Length && IsRegexWordChar(text[i]) ? 0 : i - at;
    }

    private static int Word(string text, int at, SqlDialectDefinition d)
    {
        var i = at;
        while (i < text.Length && IsWordChar(text[i], d))
        {
            i++;
        }

        return i - at;
    }

    private static bool IsWordChar(char c, SqlDialectDefinition d)
    {
        if (d.SpecialWordChars.Contains(c, StringComparison.Ordinal))
        {
            return true;
        }

        return CharUnicodeInfo.GetUnicodeCategory(c) is
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.Format or
            UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse;
    }

    private static int Operator(string text, int at, SqlDialectDefinition d)
    {
        foreach (var op in d.Operators)
        {
            if (string.CompareOrdinal(text, at, op, 0, op.Length) == 0)
            {
                return op.Length;
            }
        }

        // Anything else is a one-character token, so the scanner always makes progress. A
        // surrogate pair stays together.
        return char.IsHighSurrogate(text[at]) && at + 1 < text.Length ? 2 : 1;
    }
}

/// <summary>Turns the token stream into laid-out text.</summary>
internal sealed class SqlLayout(string source, List<SqlToken> tokens, SqlFormatOptions options)
{
    private const int InlineMaxLength = 50;

    private readonly StringBuilder _out = new(source.Length + (source.Length / 2));
    private readonly Stack<bool> _indents = new(); // true = top level, false = block
    private SqlToken? _previousReserved;
    private int _inlineLevel;
    private int _index;

    public string Run()
    {
        for (_index = 0; _index < tokens.Count; _index++)
        {
            var token = Override(tokens[_index]);

            switch (token.Type)
            {
                case SqlTokenType.LineComment:
                    Append(token);
                    NewLine();
                    break;

                case SqlTokenType.BlockComment:
                    NewLine();
                    AppendComment(token);
                    NewLine();
                    break;

                case SqlTokenType.ReservedTopLevel:
                    PopTopLevel();
                    NewLine();
                    _indents.Push(true);
                    Append(token);
                    NewLine();
                    _previousReserved = token;
                    break;

                case SqlTokenType.ReservedTopLevelNoIndent:
                    PopTopLevel();
                    NewLine();
                    Append(token);
                    NewLine();
                    _previousReserved = token;
                    break;

                case SqlTokenType.ReservedNewLine:
                    FormatNewLineKeyword(token);
                    _previousReserved = token;
                    break;

                case SqlTokenType.Reserved:
                    AppendWithSpace(token);
                    _previousReserved = token;
                    break;

                case SqlTokenType.OpenParen:
                    FormatOpenParen(token);
                    break;

                case SqlTokenType.CloseParen:
                    FormatCloseParen(token);
                    break;

                case SqlTokenType.Placeholder:
                    AppendWithSpace(token);
                    break;

                default:
                    FormatOther(token);
                    break;
            }
        }

        return _out.ToString().Trim();
    }

    /// <summary>The two dialect quirks where the same word means different things in context.</summary>
    private SqlToken Override(SqlToken token)
    {
        switch (options.Dialect)
        {
            // PL/SQL: in "ORDER SIBLINGS BY … SET", SET is not the UPDATE clause.
            case SqlDialect.PlSql when token.Is(source, SqlTokenType.ReservedTopLevel, "SET") &&
                                       _previousReserved is { } by && by.Is(source, SqlTokenType.Reserved, "BY"):
                return token with { Type = SqlTokenType.Reserved };

            // Spark: WINDOW( is a function, and .END is a property, not CASE … END.
            case SqlDialect.SparkSql when token.Is(source, SqlTokenType.ReservedTopLevel, "WINDOW") &&
                                          Ahead(1) is { Type: SqlTokenType.OpenParen }:
                return token with { Type = SqlTokenType.Reserved };

            case SqlDialect.SparkSql when token.Is(source, SqlTokenType.CloseParen, "END") &&
                                          Behind(1) is { Type: SqlTokenType.Operator } dot && dot.IsChar(source, '.'):
                return token with { Type = SqlTokenType.Word };

            default:
                return token;
        }
    }

    private SqlToken? Behind(int n) => _index - n >= 0 ? tokens[_index - n] : null;

    private SqlToken? Ahead(int n) => _index + n < tokens.Count ? tokens[_index + n] : null;

    private void FormatNewLineKeyword(SqlToken token)
    {
        // The AND of "BETWEEN 1 AND 5" belongs to the range, not to the WHERE clause.
        if (token.Is(source, SqlTokenType.ReservedNewLine, "AND") &&
            Behind(2) is { } between && between.Is(source, SqlTokenType.Reserved, "BETWEEN"))
        {
            AppendWithSpace(token);
            return;
        }

        NewLine();
        Append(token);
        _out.Append(' ');
    }

    private void FormatOpenParen(SqlToken token)
    {
        // "COUNT(*)" stays tight; "IN (1, 2)" keeps the space it was written with.
        if (token.PrecedingWhitespace == 0 &&
            Behind(1) is { } behind &&
            behind.Type is not (SqlTokenType.OpenParen or SqlTokenType.LineComment or SqlTokenType.Operator) &&
            !behind.Is(source, SqlTokenType.ReservedTopLevel, "VALUES"))
        {
            TrimSpaces();
        }

        Append(token);

        if (_inlineLevel > 0)
        {
            _inlineLevel++;
        }
        else if (FitsInline())
        {
            _inlineLevel = 1;
        }

        if (_inlineLevel == 0)
        {
            _indents.Push(false);
            NewLine();
        }
    }

    private void FormatCloseParen(SqlToken token)
    {
        if (_inlineLevel > 0)
        {
            _inlineLevel--;
            TrimSpaces();
            Append(token);
            _out.Append(' ');
            return;
        }

        while (_indents.Count > 0 && _indents.Pop())
        {
            // Unwind any clause opened inside the block along with the block itself.
        }

        NewLine();
        AppendWithSpace(token);
    }

    /// <summary>
    /// A short parenthesised group — a function call, an IN list — stays on one line, as long as
    /// it holds no clause keyword, comment or statement end that would need lines of its own.
    /// </summary>
    private bool FitsInline()
    {
        var length = 0;
        var level = 0;

        for (var i = _index; i < tokens.Count; i++)
        {
            var token = tokens[i];
            length += token.Length;

            if (length > InlineMaxLength)
            {
                return false;
            }

            if (token.Type == SqlTokenType.OpenParen)
            {
                level++;
            }
            else if (token.Type == SqlTokenType.CloseParen)
            {
                level--;
                if (level == 0)
                {
                    return true;
                }
            }

            if (token.Type is SqlTokenType.ReservedTopLevel or SqlTokenType.ReservedNewLine or
                    SqlTokenType.BlockComment ||
                token.IsChar(source, ';'))
            {
                return false;
            }
        }

        return false;
    }

    private void FormatOther(SqlToken token)
    {
        if (token.Length != 1)
        {
            AppendWithSpace(token);
            return;
        }

        switch (source[token.Index])
        {
            case ',':
                FormatComma(token);
                break;

            case ':' when token.Type == SqlTokenType.Operator:
                TrimSpaces();
                Append(token);
                _out.Append(' ');
                break;

            case '.' when token.Type == SqlTokenType.Operator:
                TrimSpaces();
                Append(token);
                break;

            case ';' when token.Type == SqlTokenType.Operator:
                _indents.Clear();
                _inlineLevel = 0;
                TrimSpaces();
                Append(token);
                for (var i = 0; i < Math.Max(1, options.LinesBetweenQueries); i++)
                {
                    _out.Append('\n');
                }

                break;

            default:
                AppendWithSpace(token);
                break;
        }
    }

    private void FormatComma(SqlToken token)
    {
        TrimSpaces();

        var breakLine = _inlineLevel == 0 &&
                        !(_previousReserved is { } limit && limit.Is(source, SqlTokenType.ReservedTopLevel, "LIMIT"));

        if (options.LeadingCommas && breakLine)
        {
            NewLine();
        }

        Append(token);
        _out.Append(' ');

        if (!options.LeadingCommas && breakLine)
        {
            NewLine();
        }
    }

    private void PopTopLevel()
    {
        if (_indents.TryPeek(out var topLevel) && topLevel)
        {
            _indents.Pop();
        }
    }

    private void AppendWithSpace(SqlToken token)
    {
        Append(token);
        _out.Append(' ');
    }

    private void Append(SqlToken token)
    {
        var text = token.Text(source);

        var isKeyword = token.Type is SqlTokenType.Reserved or SqlTokenType.ReservedTopLevel or
            SqlTokenType.ReservedTopLevelNoIndent or SqlTokenType.ReservedNewLine or
            SqlTokenType.OpenParen or SqlTokenType.CloseParen;

        if (!isKeyword)
        {
            _out.Append(text);
            return;
        }

        // "GROUP   BY" written across a line break comes out as "GROUP BY".
        var lastWasSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    _out.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            lastWasSpace = false;
            _out.Append(options.KeywordCase switch
            {
                SqlKeywordCase.Upper => char.ToUpperInvariant(c),
                SqlKeywordCase.Lower => char.ToLowerInvariant(c),
                _ => c,
            });
        }
    }

    /// <summary>A block comment keeps its lines, re-indented to where it now sits.</summary>
    private void AppendComment(SqlToken token)
    {
        var indent = TextUtil.Indent(options.IndentStyle, _indents.Count);
        var lines = TextUtil.NormalizeNewlines(token.Text(source).ToString()).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                _out.Append('\n').Append(indent).Append(' ');
                _out.Append(lines[i].TrimStart(' ', '\t'));
            }
            else
            {
                _out.Append(lines[i]);
            }
        }
    }

    private void NewLine()
    {
        TrimSpaces();

        if (_out.Length > 0 && _out[^1] != '\n')
        {
            _out.Append('\n');
        }

        _out.Append(TextUtil.Indent(options.IndentStyle, _indents.Count));
    }

    private void TrimSpaces()
    {
        // Tabs too, so a tab-indented layout behaves exactly like a space-indented one.
        var end = _out.Length;
        while (end > 0 && _out[end - 1] is ' ' or '\t')
        {
            end--;
        }

        _out.Length = end;
    }
}
