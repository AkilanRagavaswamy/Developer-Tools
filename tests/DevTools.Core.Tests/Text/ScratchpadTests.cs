using DevTools.Core.Scratch;
using Xunit;

namespace DevTools.Core.Tests.Text;

public sealed class ScratchMathTests
{
    private static string? Result(string expression) => ScratchMath.EvaluateExpression(expression)?.Result;

    private static string? Error(string expression) => ScratchMath.EvaluateExpression(expression)?.Error;

    [Theory]
    [InlineData("1 + 2", "3")]
    [InlineData("2 + 3 * 4", "14")]
    [InlineData("(2 + 3) * 4", "20")]
    [InlineData("10 / 4", "2.5")]
    [InlineData("0.1 + 0.2", "0.3")]
    [InlineData("2 ^ 10", "1,024")]
    [InlineData("2 ^ 3 ^ 2", "512")]
    [InlineData("-2 ^ 2", "-4")]
    [InlineData("(-2) ^ 2", "4")]
    [InlineData("2 ^ -1", "0.5")]
    [InlineData("10 mod 3", "1")]
    [InlineData("10 % 3", "1")]
    [InlineData("7 − 2", "5")]
    [InlineData("6 × 7", "42")]
    [InlineData("84 ÷ 2", "42")]
    [InlineData("-5 + 2", "-3")]
    [InlineData("--5", "5")]
    [InlineData("1,500,000 / 2", "750,000")]
    [InlineData("1_000 * 3", "3,000")]
    [InlineData("2.5k", "2,500")]
    [InlineData("1e3 + 1", "1,001")]
    [InlineData(".5 * 4", "2")]
    [InlineData("1 / 3", "0.333333")]
    [InlineData("0.000123 * 1", "0.000123")]
    public void Arithmetic(string expression, string expected) => Assert.Equal(expected, Result(expression));

    [Theory]
    [InlineData("sqrt(16)", "4")]
    [InlineData("sqrt(2) ^ 2", "2")]
    [InlineData("round(2.345, 2)", "2.35")]
    [InlineData("round(2.5)", "3")]
    [InlineData("floor(2.7)", "2")]
    [InlineData("ceil(2.1)", "3")]
    [InlineData("abs(-4)", "4")]
    [InlineData("min(3, 1, 2)", "1")]
    [InlineData("max(3, 1, 2)", "3")]
    [InlineData("log(1000)", "3")]
    [InlineData("ln(e)", "1")]
    [InlineData("round(pi, 4)", "3.1416")]
    [InlineData("sin(0)", "0")]
    [InlineData("cos(0)", "1")]
    public void Functions(string expression, string expected) => Assert.Equal(expected, Result(expression));

    [Theory]
    [InlineData("20% of 1500", "300")]
    [InlineData("1500 + 18%", "1,770")]
    [InlineData("1500 - 10%", "1,350")]
    [InlineData("300 as % of 1500", "20%")]
    [InlineData("15%", "15%")]
    [InlineData("10% + 5%", "15%")]
    public void Percentages(string expression, string expected) => Assert.Equal(expected, Result(expression));

    [Theory]
    [InlineData("3.5 GB in MiB", "3,337.860107 MiB")]
    [InlineData("1 GiB in MiB", "1,024 MiB")]
    [InlineData("1024 KiB to MiB", "1 MiB")]
    [InlineData("8 b in B", "1 B")]
    [InlineData("100 Mb in MB", "12.5 MB")]
    [InlineData("1 KB in B", "1,000 B")]
    [InlineData("5 GB + 500 MB", "5.5 GB")]
    [InlineData("5 GB + 3", "8 GB")]
    [InlineData("10 GB / 2", "5 GB")]
    [InlineData("10 GB / 2 GB", "5")]
    [InlineData("90 min in h", "1.5 h")]
    [InlineData("1 d in s", "86,400 s")]
    [InlineData("250 ms + 1 s", "1,250 ms")]
    [InlineData("2 wk in days", "14 d")]
    [InlineData("1 mi in km", "1.609344 km")]
    [InlineData("12 inches in cm", "30.48 cm")]
    [InlineData("1 kg in lb", "2.204623 lb")]
    [InlineData("100 °C in °F", "212 °F")]
    [InlineData("32 °F in celsius", "0 °C")]
    [InlineData("0 °C in kelvin", "273.15 K")]
    [InlineData("3 * 2 h", "6 h")]
    public void Units(string expression, string expected) => Assert.Equal(expected, Result(expression));

    [Theory]
    [InlineData("0xFF", "255")]
    [InlineData("0b1010", "10")]
    [InlineData("0o17", "15")]
    [InlineData("255 in hex", "0xFF")]
    [InlineData("10 in bin", "0b1010")]
    [InlineData("8 in oct", "0o10")]
    [InlineData("0xFF + 1 in hex", "0x100")]
    [InlineData("-16 in hex", "-0x10")]
    public void Bases(string expression, string expected) => Assert.Equal(expected, Result(expression));

    [Theory]
    [InlineData("1 / 0", "Division by zero.")]
    [InlineData("5 GB + 3 s", "Can't add GB and s.")]
    [InlineData("5 GB * 3 GB", "Can't multiply GB by GB.")]
    [InlineData("5 kg in MB", "Can't convert kg to MB.")]
    [InlineData("1.5 in hex", "Only whole numbers can be shown in hex, binary or octal.")]
    [InlineData("sqrt(-1)", "A negative number has no real square root.")]
    [InlineData("(1 + 2", "A closing bracket is missing.")]
    [InlineData("2 +", "The calculation ends too early.")]
    [InlineData("5 in parsecs", "I don't know the unit 'parsecs'.")]
    [InlineData("3 * banana", "I don't know what 'banana' is. Define it first, e.g. banana = 10.")]
    [InlineData("79228162514264337593543950335 * 10", "The result is too large.")]
    [InlineData("99999999999999999999999999999", "That number is too large.")]
    public void Errors(string expression, string expected) => Assert.Equal(expected, Error(expression));

    [Theory]
    [InlineData("Meeting at 3 with Sam")]
    [InlineData("Remember to call the bank")]
    [InlineData("Room 204")]
    [InlineData("TODO: fix the login page")]
    [InlineData("2026-10-07")]
    [InlineData("10:30 standup")]
    [InlineData("Price $5")]
    [InlineData("Notes:")]
    [InlineData("// just a comment")]
    [InlineData("# heading")]
    public void Prose_gives_nothing(string line) => Assert.Null(ScratchMath.EvaluateExpression(line));

    [Fact]
    public void Variables_carry_down_and_recalculate()
    {
        var lines = ScratchMath.Evaluate("rate = 1500\nhours = 12\nrate * hours\n");

        Assert.Equal(["1,500", "12", "18,000"], lines.Select(l => l.Result));
        Assert.Equal([1, 2, 3], lines.Select(l => l.LineNumber));

        var changed = ScratchMath.Evaluate("rate = 2000\nhours = 12\nrate * hours\n");
        Assert.Equal("24,000", changed[2].Result);
    }

    [Fact]
    public void Variable_names_ignore_case()
    {
        var lines = ScratchMath.Evaluate("Total_Cost = 10\ntotal_cost * 2");
        Assert.Equal("20", lines[1].Result);
    }

    [Fact]
    public void Labels_are_ignored()
    {
        Assert.Equal("14,400", Result("Rent: 1200 × 12"));
    }

    [Fact]
    public void Trailing_comments_are_ignored()
    {
        Assert.Equal("4", Result("2 + 2 // easy"));
        Assert.Equal("4", Result("2 + 2 # easy"));
    }

    [Fact]
    public void Prev_sum_and_avg()
    {
        var lines = ScratchMath.Evaluate("10\n20\n30\nsum\n\n4\nprev * 2\navg");

        Assert.Equal("60", lines.Single(l => l.LineNumber == 4).Result);
        Assert.Equal("8", lines.Single(l => l.LineNumber == 7).Result);

        // The blank line started a new block: 4 and 8.
        Assert.Equal("6", lines.Single(l => l.LineNumber == 8).Result);
    }

    [Fact]
    public void Sum_converts_to_the_first_unit()
    {
        var lines = ScratchMath.Evaluate("1 GB\n500 MB\ntotal");
        Assert.Equal("1.5 GB", lines[2].Result);
    }

    [Fact]
    public void Prose_lines_do_not_break_the_calculation()
    {
        var lines = ScratchMath.Evaluate("Budget for the trip\nflights = 450\nhotel = 3 * 120\nflights + hotel\nThat's it.");

        Assert.Equal(3, lines.Count);
        Assert.Equal("810", lines[2].Result);
    }

    [Fact]
    public void Assignment_errors_are_always_reported()
    {
        var line = ScratchMath.EvaluateExpression("x = banana + 1");
        Assert.NotNull(line);
        Assert.True(line!.IsError);
    }

    [Fact]
    public void Built_in_names_cannot_be_assigned()
    {
        Assert.Equal("'sqrt' is a built-in name and can't be used for a value.", Error("sqrt = 4"));
    }

    [Fact]
    public void Copy_text_has_no_grouping()
    {
        var line = ScratchMath.EvaluateExpression("1200 * 12")!;
        Assert.Equal("14,400", line.Result);
        Assert.Equal("14400", line.CopyText);
    }

    [Fact]
    public void A_failing_line_does_not_stop_later_lines()
    {
        var lines = ScratchMath.Evaluate("1 / 0\n2 + 2");
        Assert.True(lines[0].IsError);
        Assert.Equal("4", lines[1].Result);
    }

    [Fact]
    public void Prev_without_a_result_is_an_error()
    {
        Assert.Equal("There's no result above to use as 'prev'.", Error("prev + 1"));
    }

    [Fact]
    public void Empty_input_gives_nothing()
    {
        Assert.Empty(ScratchMath.Evaluate(null));
        Assert.Empty(ScratchMath.Evaluate(""));
        Assert.Empty(ScratchMath.Evaluate("\n\n"));
    }

    [Fact]
    public void Windows_line_endings_are_handled()
    {
        var lines = ScratchMath.Evaluate("a = 2\r\na * 3\r\n");
        Assert.Equal("6", lines[1].Result);
    }
}

public sealed class ScratchNotesTests
{
    [Theory]
    [InlineData("Shopping list\nmilk", "Shopping list")]
    [InlineData("\n\n   Plan  \nrest", "Plan")]
    [InlineData("# Release notes", "Release notes")]
    [InlineData("// TODO later", "TODO later")]
    [InlineData("- item one", "item one")]
    [InlineData("{\r  \"name\": \"forge\",\r}", "\"name\": \"forge\",")]
    [InlineData("---\n\n<root>", "<root>")]
    public void Title_is_the_first_line(string text, string expected) =>
        Assert.Equal(expected, ScratchNotes.DeriveTitle(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t\n")]
    [InlineData("###")]
    public void Empty_notes_have_no_title(string? text) => Assert.Null(ScratchNotes.DeriveTitle(text));

    [Fact]
    public void Long_titles_are_truncated()
    {
        var title = ScratchNotes.DeriveTitle(new string('a', 200))!;
        Assert.Equal(ScratchNotes.MaxTitleLength, title.Length);
        Assert.EndsWith("…", title, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_is_the_rest_on_one_line()
    {
        Assert.Equal("first second third", ScratchNotes.Preview("Title\nfirst\n\n  second\tthird\n"));
        Assert.Equal(string.Empty, ScratchNotes.Preview("Only a title"));
        Assert.Equal(string.Empty, ScratchNotes.Preview(null));
    }

    [Theory]
    [InlineData(ScratchLanguage.Json, ".json")]
    [InlineData(ScratchLanguage.Markdown, ".md")]
    [InlineData(ScratchLanguage.Math, ".txt")]
    [InlineData(ScratchLanguage.Plain, ".txt")]
    public void Export_extension(ScratchLanguage language, string extension) =>
        Assert.Equal(extension, ScratchNotes.Extension(language));

    [Theory]
    [InlineData(".JSON", ScratchLanguage.Json)]
    [InlineData(".htm", ScratchLanguage.Html)]
    [InlineData(".log", ScratchLanguage.Plain)]
    [InlineData(null, ScratchLanguage.Plain)]
    public void Language_from_extension(string? extension, ScratchLanguage expected) =>
        Assert.Equal(expected, ScratchNotes.FromExtension(extension));

    [Theory]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIE...", "a private key")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "a JSON Web Token")]
    [InlineData("AKIAIOSFODNN7EXAMPLE", "an AWS access key")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", "a GitHub token")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz123456", "a bearer token")]
    [InlineData("Server=db;User Id=sa;Password=hunter22;", "a connection-string password")]
    [InlineData("\"api_key\": \"sk_live_12345678\"", "a password")]
    public void Secrets_are_spotted(string text, string label) => Assert.Equal(label, ScratchSecrets.Find(text));

    [Theory]
    [InlineData("Remember the password for the wifi is on the fridge")]
    [InlineData("Bearer of bad news")]
    [InlineData("{ \"name\": \"value\" }")]
    [InlineData(null)]
    public void Ordinary_text_is_not_a_secret(string? text) => Assert.Null(ScratchSecrets.Find(text));
}

public sealed class ScratchRetentionTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Days_are_clamped_to_one_to_sixty()
    {
        Assert.Equal(1, ScratchRetention.ClampDays(0));
        Assert.Equal(60, ScratchRetention.ClampDays(365));
        Assert.Equal(20, ScratchRetention.ClampDays(20));
    }

    [Fact]
    public void Trash_expires_after_the_configured_days()
    {
        Assert.False(ScratchRetention.IsTrashExpired(Now.AddDays(-19), Now, 20));
        Assert.True(ScratchRetention.IsTrashExpired(Now.AddDays(-20), Now, 20));
        Assert.True(ScratchRetention.IsTrashExpired(Now.AddDays(-2), Now, 1));
    }

    [Fact]
    public void Snapshots_older_than_the_period_expire()
    {
        var snapshots = new[]
        {
            new ScratchSnapshotInfo("new", Now.AddDays(-1), 10),
            new ScratchSnapshotInfo("edge", Now.AddDays(-19.9), 10),
            new ScratchSnapshotInfo("old", Now.AddDays(-21), 10),
        };

        var expired = ScratchRetention.SelectExpired(snapshots, Now, 20);
        Assert.Equal(["old"], expired.Select(s => s.Key));
    }

    [Fact]
    public void Only_the_newest_hundred_are_kept()
    {
        var snapshots = Enumerable.Range(0, 130)
            .Select(i => new ScratchSnapshotInfo(i.ToString(), Now.AddMinutes(-i), 1))
            .ToList();

        var expired = ScratchRetention.SelectExpired(snapshots, Now, 20);

        Assert.Equal(30, expired.Count);
        Assert.All(expired, s => Assert.True(int.Parse(s.Key) >= 100));
    }

    [Fact]
    public void Over_the_cap_the_oldest_go_first()
    {
        var snapshots = new[]
        {
            new ScratchSnapshotInfo("c", Now.AddHours(-1), 40),
            new ScratchSnapshotInfo("a", Now.AddHours(-3), 40),
            new ScratchSnapshotInfo("b", Now.AddHours(-2), 40),
        };

        var remove = ScratchRetention.SelectOverCap(snapshots, notesBytes: 50, capBytes: 100);

        Assert.Equal(["a", "b"], remove.Select(s => s.Key));
    }

    [Fact]
    public void Under_the_cap_nothing_goes()
    {
        var snapshots = new[] { new ScratchSnapshotInfo("a", Now, 10) };
        Assert.Empty(ScratchRetention.SelectOverCap(snapshots, 10, 100));
    }
}

public sealed class ScratchLineEndingTests
{
    [Fact]
    public void Editor_line_breaks_are_lines()
    {
        // WinUI's TextBox ends lines with a bare CR.
        var lines = ScratchMath.Evaluate("a = 2\rb = 3\ra * b");
        Assert.Equal("6", lines[2].Result);
        Assert.Equal(3, lines[2].LineNumber);
    }

    [Fact]
    public void Title_and_preview_stop_at_a_bare_cr()
    {
        Assert.Equal("Trip budget", ScratchNotes.DeriveTitle("Trip budget\rflights = 450"));
        Assert.Equal("flights = 450", ScratchNotes.Preview("Trip budget\rflights = 450"));
    }

    [Fact]
    public void Line_endings_do_not_count_as_a_change()
    {
        Assert.True(ScratchNotes.SameText("a\rb\r", "a\r\nb\r\n"));
        Assert.False(ScratchNotes.SameText("a\rb", "a\rc"));
        Assert.Equal("a\r\nb\r\nc", ScratchNotes.NormalizeLineEndings("a\rb\nc"));
    }
}
