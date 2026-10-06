using DevTools.Core.Sql;
using Xunit;
using DevTools.Core.Text;

namespace DevTools.Core.Tests.Sql;

/// <summary>The option names the ported cases were written against.</summary>
internal enum Indentation
{
    TwoSpaces,
    FourSpaces,
    OneTab,
}

internal sealed record SqlCaseOptions(
    Indentation Indentation,
    bool Uppercase,
    int LinesBetweenQueries = 1,
    bool UseLeadingComma = false);

/// <summary>Runs the formatter the way the ported cases expect: keywords as written, one line between statements.</summary>
internal static class SqlCaseFormat
{
    public static string Format(SqlDialect dialect, string input) =>
        Format(dialect, input, new SqlCaseOptions(Indentation.TwoSpaces, Uppercase: false));

    public static string Format(SqlDialect dialect, string input, SqlCaseOptions options)
    {
        var result = SqlFormatter.Format(input, new SqlFormatOptions
        {
            Dialect = dialect,
            IndentStyle = options.Indentation switch
            {
                Indentation.FourSpaces => IndentStyle.FourSpaces,
                Indentation.OneTab => IndentStyle.Tab,
                _ => IndentStyle.TwoSpaces,
            },
            KeywordCase = options.Uppercase ? SqlKeywordCase.Upper : SqlKeywordCase.Preserve,
            LinesBetweenQueries = options.LinesBetweenQueries,
            LeadingCommas = options.UseLeadingComma,
        });

        Assert.True(result.IsSuccess);
        return result.Value!;
    }
}

/// <summary>Just enough of an assertion vocabulary for the ported cases to read as they were written.</summary>
internal static class SqlCaseAssertions
{
    public static StringAssertion Should(this string actual) => new(actual);

    internal readonly struct StringAssertion(string actual)
    {
        public void Be(string expected) =>
            Assert.Equal(TextUtil.NormalizeNewlines(expected), TextUtil.NormalizeNewlines(actual));
    }
}
