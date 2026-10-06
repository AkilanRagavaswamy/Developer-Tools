using DevTools.Core.Codecs;
using DevTools.Core.Generators;
using DevTools.Core.Json;
using DevTools.Core.Text;
using DevTools.Core.Time;
using DevTools.Core.Xml;
using Xunit;

namespace DevTools.Core.Tests.Formatters;

public sealed class XmlFormatterTests
{
    [Fact]
    public void Indents_nested_elements()
    {
        var result = XmlFormatter.Format("<a><b x=\"1\"><c>text</c></b><d/></a>");

        Assert.True(result.IsSuccess);
        Assert.Equal("<a>\n  <b x=\"1\">\n    <c>text</c>\n  </b>\n  <d />\n</a>", result.Value!.Output);
        Assert.Equal(4, result.Value.ElementCount);
        Assert.Equal(3, result.Value.MaxDepth);
    }

    [Fact]
    public void Minify_removes_insignificant_whitespace()
    {
        var result = XmlFormatter.Format("<a>\n  <b>1</b>\n  <c />\n</a>", new XmlFormatOptions { Minify = true });

        Assert.Equal("<a><b>1</b><c /></a>", result.Value!.Output);
    }

    [Fact]
    public void Keeps_the_declaration_as_written()
    {
        var result = XmlFormatter.Format("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?><a><b>utf-16</b></a>");

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?>\n<a>", result.Value!.Output);

        // DevToys rewrote every "utf-16" in the document; content must survive.
        Assert.Contains("<b>utf-16</b>", result.Value.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Four_spaces_tabs_and_attributes_on_new_lines()
    {
        Assert.Equal("<a>\n    <b />\n</a>", XmlFormatter.Format("<a><b/></a>", new XmlFormatOptions { IndentStyle = IndentStyle.FourSpaces }).Value!.Output);
        Assert.Equal("<a>\n\t<b />\n</a>", XmlFormatter.Format("<a><b/></a>", new XmlFormatOptions { IndentStyle = IndentStyle.Tab }).Value!.Output);
        Assert.Equal(
            "<a\n  x=\"1\"\n  y=\"2\" />",
            XmlFormatter.Format("<a x=\"1\" y=\"2\"/>", new XmlFormatOptions { NewLineOnAttributes = true }).Value!.Output);
    }

    [Fact]
    public void Preserves_comments_cdata_and_significant_whitespace()
    {
        var xml = "<a><!-- note --><b><![CDATA[x < y]]></b><p xml:space=\"preserve\">  two  spaces  </p></a>";
        var output = XmlFormatter.Format(xml).Value!.Output;

        Assert.Contains("<!-- note -->", output, StringComparison.Ordinal);
        Assert.Contains("<![CDATA[x < y]]>", output, StringComparison.Ordinal);
        Assert.Contains("  two  spaces  ", output, StringComparison.Ordinal);

        Assert.DoesNotContain("note", XmlFormatter.Format(xml, new XmlFormatOptions { RemoveComments = true }).Value!.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_the_position_of_malformed_input()
    {
        var result = XmlFormatter.Format("<a>\n  <b></c>\n</a>");

        Assert.False(result.IsSuccess);
        Assert.Equal(2, result.Error!.Line);
    }

    [Fact]
    public void Never_resolves_external_entities()
    {
        const string Xxe = "<?xml version=\"1.0\"?><!DOCTYPE a [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><a>&x;</a>";
        var result = XmlFormatter.Format(Xxe);

        Assert.DoesNotContain("[fonts]", result.IsSuccess ? result.Value!.Output : result.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class JsonTableTests
{
    [Fact]
    public void Flattens_nested_objects_and_keeps_first_seen_column_order()
    {
        var table = JsonTable.Convert("""[{"id":1,"a":{"b":2,"c":{"d":3}}},{"id":2,"extra":true}]""").Value!;

        Assert.Equal(["id", "a.b", "a.c.d", "extra"], table.Columns);
        Assert.Equal(["1", "2", "3", ""], table.Rows[0]);
        Assert.Equal(["2", "", "", "true"], table.Rows[1]);
    }

    [Fact]
    public void Keeps_arrays_as_json_and_numbers_exact()
    {
        var table = JsonTable.Convert("""[{"tags":["a","b"],"n":1.10,"big":123456789012345678901234567890}]""").Value!;

        Assert.Equal("""["a","b"]""", table.Rows[0][0]);
        Assert.Equal("1.10", table.Rows[0][1]);
        Assert.Equal("123456789012345678901234567890", table.Rows[0][2]);
    }

    [Fact]
    public void Csv_quotes_cells_that_need_it()
    {
        var table = JsonTable.Convert("""[{"name":"Smith, J","quote":"say \"hi\"","plain":"x"}]""").Value!;
        var csv = JsonTable.ToText(table, TableTextFormat.Csv);

        Assert.Equal("name,quote,plain\r\n\"Smith, J\",\"say \"\"hi\"\"\",x\r\n", csv);
    }

    [Fact]
    public void Scalars_become_a_value_column_and_a_single_object_is_one_row()
    {
        Assert.Equal(["value"], JsonTable.Convert("[1,2,3]").Value!.Columns);
        Assert.Single(JsonTable.Convert("""{"a":1}""").Value!.Rows);
        Assert.False(JsonTable.Convert("42").IsSuccess);
    }

    [Fact]
    public void Markdown_escapes_pipes()
    {
        var table = JsonTable.Convert("""[{"a":"x|y"}]""").Value!;
        Assert.Equal("| a |\n| --- |\n| x\\|y |\n", JsonTable.ToText(table, TableTextFormat.Markdown));
    }
}

public sealed class DateTimeConverterTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Theory]
    [InlineData("1700000000", EpochUnit.Seconds)]
    [InlineData("1700000000000", EpochUnit.Milliseconds)]
    [InlineData("1700000000000000", EpochUnit.Microseconds)]
    [InlineData("1700000000000000000", EpochUnit.Nanoseconds)]
    public void Detects_the_unit_from_the_size(string input, EpochUnit unit)
    {
        var result = DateTimeConverter.Parse(input, EpochUnit.Auto, Utc).Value!;

        Assert.Equal(unit, result.Unit);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 22, 13, 20, TimeSpan.Zero), result.Instant);
    }

    [Fact]
    public void Keeps_fractional_seconds_exactly()
    {
        var result = DateTimeConverter.Parse("1700000000.1234567", EpochUnit.Seconds, Utc).Value!;
        Assert.Equal(1234567, result.Instant.Ticks % TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void Reads_a_date_with_an_offset_as_that_instant()
    {
        var result = DateTimeConverter.Parse("2024-05-01T12:00:00+05:30", EpochUnit.Auto, Utc).Value!;
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 6, 30, 0, TimeSpan.Zero), result.Instant.ToUniversalTime());
    }

    [Fact]
    public void Reads_a_date_without_an_offset_in_the_chosen_zone()
    {
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("Test+9", TimeSpan.FromHours(9), "Test+9", "Test+9");
        var result = DateTimeConverter.Parse("2024-05-01 09:00", EpochUnit.Auto, tokyo).Value!;

        Assert.Equal(new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero), result.Instant.ToUniversalTime());
    }

    [Fact]
    public void Fields_round_trip_through_every_unit()
    {
        var instant = new DateTimeOffset(2023, 11, 14, 22, 13, 20, 123, TimeSpan.Zero);
        var fields = DateTimeConverter.Fields(instant, Utc, instant).ToDictionary(f => f.Label, f => f.Value);

        Assert.Equal("1700000000", fields["Unix time (seconds)"]);
        Assert.Equal("1700000000123", fields["Unix time (milliseconds)"]);
        Assert.Equal("2023-11-14T22:13:20.1230000Z", fields["ISO 8601 (UTC)"]);
        Assert.Equal("Tue, 14 Nov 2023 22:13:20 GMT", fields["RFC 1123 / HTTP date"]);
        Assert.Equal("now", fields["Relative"]);
    }

    [Fact]
    public void Rejects_out_of_range_and_garbage()
    {
        Assert.False(DateTimeConverter.Parse("99999999999999999999999", EpochUnit.Seconds, Utc).IsSuccess);
        Assert.False(DateTimeConverter.Parse("not a date", EpochUnit.Auto, Utc).IsSuccess);
    }

    [Fact]
    public void Relative_descriptions()
    {
        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("3 hours ago", DateTimeConverter.Relative(now.AddHours(-3), now));
        Assert.Equal("in 2 days", DateTimeConverter.Relative(now.AddDays(2), now));
    }
}

public sealed class UuidGeneratorTests
{
    [Theory]
    [InlineData(UuidVersion.V1, 1)]
    [InlineData(UuidVersion.V4, 4)]
    [InlineData(UuidVersion.V7, 7)]
    public void Sets_version_and_variant(UuidVersion version, int expected)
    {
        foreach (var line in UuidGenerator.Generate(new UuidOptions { Version = version, Count = 50 }).Split('\n'))
        {
            var bytes = Guid.Parse(line).ToByteArray(bigEndian: true);
            Assert.Equal(expected, bytes[6] >> 4);
            Assert.Equal(0x80, bytes[8] & 0xC0);
        }
    }

    [Fact]
    public void V7_batches_sort_in_generation_order()
    {
        var lines = UuidGenerator.Generate(new UuidOptions { Version = UuidVersion.V7, Count = 5_000 }).Split('\n');
        var sorted = lines.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(lines, sorted);
        Assert.Equal(lines.Length, lines.Distinct().Count());
    }

    [Fact]
    public void Formats()
    {
        var bytes = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e").ToByteArray(bigEndian: true);

        Assert.Equal("0F8FAD5BD9CB469FA16570867728950E", UuidGenerator.Format(bytes, UuidFormat.Compact, uppercase: true));
        Assert.Equal("{0f8fad5b-d9cb-469f-a165-70867728950e}", UuidGenerator.Format(bytes, UuidFormat.Braces, uppercase: false));
        Assert.Equal("urn:uuid:0f8fad5b-d9cb-469f-a165-70867728950e", UuidGenerator.Format(bytes, UuidFormat.Urn, uppercase: false));
        Assert.Equal("00000000-0000-0000-0000-000000000000", UuidGenerator.Generate(new UuidOptions { Version = UuidVersion.Nil }));
    }

    [Fact]
    public void Count_is_clamped()
    {
        Assert.Equal(UuidGenerator.MaxCount, UuidGenerator.Generate(new UuidOptions { Count = 1_000_000 }).Split('\n').Length);
    }
}

public sealed class CodecTests
{
    [Fact]
    public void Base64_round_trips_text_in_each_encoding()
    {
        foreach (var kind in EncodingCatalog.All.Where(k => k is not TextEncodingKind.Ascii and not TextEncodingKind.Latin1))
        {
            var options = new Base64Options { TextEncoding = kind };
            var encoded = Base64Codec.Run("héllo 🌍", CodecDirection.Encode, options).Value!;
            Assert.Equal("héllo 🌍", Base64Codec.Run(encoded, CodecDirection.Decode, options).Value);
        }
    }

    [Fact]
    public void Base64_decoding_forgives_form()
    {
        Assert.Equal("hello?", Base64Codec.Run("aGVs\nbG8/", CodecDirection.Decode).Value); // line break
        Assert.Equal("hello?", Base64Codec.Run("aGVsbG8_", CodecDirection.Decode).Value);   // URL-safe, no padding
        Assert.Equal("hi", Base64Codec.Run("data:text/plain;base64,aGk=", CodecDirection.Decode).Value);
    }

    [Fact]
    public void Base64_refuses_to_show_binary_as_text()
    {
        var png = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);
        var result = Base64Codec.Run(png, CodecDirection.Decode);

        Assert.False(result.IsSuccess);
        Assert.Contains("binary", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Base64_url_safe_and_each_line()
    {
        Assert.Equal("-_8", Base64Codec.EncodeBytes([0xFB, 0xFF], urlSafe: true));
        Assert.Equal("YQ==\nYg==", Base64Codec.Run("a\nb", CodecDirection.Encode, new Base64Options { EachLine = true }).Value);
    }

    [Fact]
    public void Base64_image_detects_by_signature()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var uri = Base64Image.Encode(png, asDataUri: true);

        Assert.StartsWith("data:image/png;base64,", uri, StringComparison.Ordinal);

        // A data URI that lies about its type is decoded by what the bytes are.
        var decoded = Base64Image.Decode(uri.Replace("image/png", "image/jpeg", StringComparison.Ordinal)).Value;
        Assert.Equal("PNG", decoded.Format.Name);
        Assert.False(Base64Image.Decode("aGVsbG8=").IsSuccess);
    }

    [Fact]
    public void Url_modes()
    {
        Assert.Equal("a%20b%26c%3Dd%2F%C3%A9", UrlCodec.Encode("a b&c=d/é", UrlEncodeMode.Component));
        Assert.Equal("https://x.test/a%20b?q=1&r=%C3%A9", UrlCodec.Encode("https://x.test/a b?q=1&r=é", UrlEncodeMode.FullUrl));
        Assert.Equal("a+b", UrlCodec.Encode("a b", UrlEncodeMode.Form));
        Assert.Equal("a b", UrlCodec.Decode("a+b", UrlEncodeMode.Form));
        Assert.Equal("a+b", UrlCodec.Decode("a+b", UrlEncodeMode.Component));
        Assert.Equal("é 100%", UrlCodec.Decode("%C3%A9%20100%", UrlEncodeMode.Component));
    }

    [Fact]
    public void Html_encode_and_decode()
    {
        Assert.Equal("&lt;a href=&quot;x&quot;&gt;Tom &amp; Jerry&#39;s&lt;/a&gt;", HtmlCodec.Encode("<a href=\"x\">Tom & Jerry's</a>", false));
        Assert.Equal("caf&#xE9; &#x1F30D;", HtmlCodec.Encode("café 🌍", true));
        Assert.Equal("café <b> 🌍", HtmlCodec.Decode("caf&eacute; &lt;b&gt; &#x1F30D;"));
    }
}

public sealed class TextAnalysisTests
{
    [Fact]
    public void Counts_words_sentences_paragraphs_and_lines()
    {
        var stats = TextAnalysis.Analyze("Hello world. Don't stop!\n\nWell-known facts? Yes.\nEnd");

        Assert.Equal(8, stats.Words);
        Assert.Equal(5, TextAnalysis.Analyze("Pi is 3.14, about 1,000.").Words); // 3.14 and 1,000 are one word each
        Assert.Equal(5, stats.Sentences);
        Assert.Equal(2, stats.Paragraphs);
        Assert.Equal(4, stats.Lines);
        Assert.Contains(stats.TopWords, w => w.Text == "don't");
        Assert.Contains(stats.TopWords, w => w.Text == "end"); // the last word is counted too
    }

    [Fact]
    public void Counts_graphemes_not_code_units()
    {
        var stats = TextAnalysis.Analyze("👍🏽 é");

        Assert.Equal(3, stats.Characters);
        Assert.Equal(2, stats.CharactersWithoutWhitespace);
        Assert.True(stats.Utf16Units > stats.Characters);
    }

    [Fact]
    public void Decimal_points_do_not_end_sentences()
    {
        Assert.Equal(1, TextAnalysis.Analyze("Pi is 3.14 roughly.").Sentences);
    }

    [Fact]
    public void Count_lines_matches_split_lines()
    {
        foreach (var text in new[] { "", "a", "a\n", "a\r\nb", "\n", "\r\r", "a\rb\n", "\uFEFF" })
        {
            Assert.Equal(TextUtil.SplitLines(text).Length, TextUtil.CountLines(text));
            Assert.Equal(TextUtil.SplitLines(text, keepTrailingEmpty: true).Length, TextUtil.CountLines(text, keepTrailingEmpty: true));
        }
    }

    [Fact]
    public void Grapheme_fast_path_matches_the_general_one()
    {
        // CR LF is one grapheme, which the ASCII fast path has to agree with.
        Assert.Equal(4, TextUtil.GraphemeCount("ab\r\nc"));
        Assert.Equal(new System.Globalization.StringInfo("ab\r\ncd\n").LengthInTextElements, TextUtil.GraphemeCount("ab\r\ncd\n"));
    }
}

public sealed class SmartDetectorNewToolTests
{
    [Theory]
    [InlineData("SELECT id, name FROM users WHERE id = 1", "sql-formatter")]
    [InlineData("update users set name = 'x' where id = 2", "sql-formatter")]
    [InlineData("<catalog><book id=\"1\"/></catalog>", "xml-formatter")]
    [InlineData("1700000000", "date-converter")]
    [InlineData("1700000000000", "date-converter")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", "base64-image")]
    [InlineData("SGVsbG8sIEZvcmdlS2l0UmshIDEyMw==", "base64-text")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e", "uuid-generator")]
    [InlineData("[{\"a\":1},{\"a\":2}]", "json-to-table")]
    public void Suggests_the_new_tools(string clipboard, string tool)
    {
        Assert.Contains(Core.Detection.SmartDetector.Detect(clipboard), h => h.ToolId == tool);
    }

    [Theory]
    [InlineData("Select the best option from the list")]
    [InlineData("1234567890123456")]
    [InlineData("HelloWorld")]
    [InlineData("12345")]
    public void Stays_quiet_on_ordinary_text(string clipboard)
    {
        Assert.Empty(Core.Detection.SmartDetector.Detect(clipboard));
    }
}

public sealed class SqlFormatterExtraTests
{
    [Fact]
    public void Keyword_case_options_and_asc_desc()
    {
        var upper = Core.Sql.SqlFormatter.Format("select a from t order by a desc").Value!;
        Assert.Equal("SELECT\n  a\nFROM\n  t\nORDER BY\n  a DESC", upper);

        var lower = Core.Sql.SqlFormatter.Format("SELECT a FROM t", new Core.Sql.SqlFormatOptions { KeywordCase = Core.Sql.SqlKeywordCase.Lower }).Value!;
        Assert.Equal("select\n  a\nfrom\n  t", lower);
    }

    [Fact]
    public void Never_changes_strings_or_comments()
    {
        var output = Core.Sql.SqlFormatter.Format("select 'from  where' as x -- select me\nfrom t").Value!;
        Assert.Contains("'from  where'", output, StringComparison.Ordinal);
        Assert.Contains("-- select me", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Formats_a_large_script_quickly()
    {
        var script = string.Concat(Enumerable.Repeat("select a, b, c from t1 join t2 on t1.id = t2.id where x = 1 and y in (1, 2, 3);\n", 5_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(Core.Sql.SqlFormatter.Format(script).IsSuccess);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }
}

public sealed class JsonPerformanceTests
{
    /// <summary>A 2 MB document used to take over a minute: every node re-counted lines from the start.</summary>
    [Fact]
    public void Parses_megabytes_in_linear_time()
    {
        var json = "[" + string.Join(',', Enumerable.Range(0, 20_000).Select(i => $"{{\"id\":{i},\"name\":\"user {i}\",\"tags\":[\"a\",\"b\"]}}")) + "]";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var parsed = JsonReader.Parse(json);

        Assert.True(parsed.IsSuccess);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Positions_are_still_exact()
    {
        var parsed = JsonReader.Parse("{\r\n  \"a\": [1,\n   2]\r\n}").Value!;
        var array = (JsonArray)((JsonObject)parsed.Root).Members[0].Value;

        Assert.Equal(new JsonPosition(2, 8, 10), array.Position);
        Assert.Equal(3, array.Items[1].Position.Line);
        Assert.Equal(4, array.Items[1].Position.Column);
    }

    [Fact]
    public void Diff_and_patch_handle_large_arrays_with_one_insertion()
    {
        var items = Enumerable.Range(0, 5_000).Select(i => $"{{\"id\":{i}}}").ToList();
        var left = "[" + string.Join(',', items) + "]";
        items.Insert(2_500, "{\"id\":-1}");
        var right = "[" + string.Join(',', items) + "]";

        var diff = JsonDiffer.Compare(left, right).Value!;

        // One element added, not 2,500 changed: the common ends are aligned, not paired by index.
        Assert.Equal(1, diff.Added);
        Assert.Equal(0, diff.Changed);
        Assert.Equal(right.Replace(" ", string.Empty, StringComparison.Ordinal),
            JsonPatch.Apply(left, diff.JsonPatch, JsonWriterOptions.Compact).Value);
    }
}

public sealed class RegexTesterTests
{
    [Fact]
    public void Lists_matches_with_lines_columns_and_named_groups()
    {
        var result = RegexTester.Test(@"(?<user>\w+)@(?<host>[\w.]+)", "a\nmail ada@x.io and\nbob@y.org").Value!;

        Assert.Equal(2, result.TotalMatches);
        Assert.Equal(["user", "host"], result.GroupNames.Where(n => n is "user" or "host"));

        var second = result.Matches[1];
        Assert.Equal(3, second.Line);
        Assert.Equal(1, second.Column);
        Assert.Equal("bob", second.Groups.Single(g => g.Name == "user").Value);
    }

    [Fact]
    public void Applies_flags_and_replacement()
    {
        var options = new RegexTestOptions { IgnoreCase = true, Replacement = "<$0>" };
        var result = RegexTester.Test("cat", "Cat cAT dog", options).Value!;

        Assert.Equal(2, result.TotalMatches);
        Assert.Equal("<Cat> <cAT> dog", result.Replaced);
    }

    [Fact]
    public void Reports_an_invalid_pattern_with_its_offset()
    {
        var result = RegexTester.Test("(abc", "abc");

        Assert.False(result.IsSuccess);
        Assert.Contains("not valid", result.Error!.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Error.Offset);
    }

    [Fact]
    public void Stops_catastrophic_backtracking()
    {
        var options = new RegexTestOptions { Timeout = TimeSpan.FromMilliseconds(200) };
        var result = RegexTester.Test("^(a+)+$", new string('a', 40) + "!", options);

        Assert.False(result.IsSuccess);
        Assert.Contains("backtracks", result.Error!.Message, StringComparison.Ordinal);
    }
}

public sealed class DiffHtmlExporterTests
{
    [Fact]
    public void Text_diff_page_is_self_contained_and_escaped()
    {
        var diff = TextDiff.Compare("a <b>\nsame", "a <c>\nsame").Value!;
        var html = DiffHtmlExporter.FromText(diff, "Left", "Right", "1 changed");

        Assert.StartsWith("<!DOCTYPE html>", html.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("&lt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        Assert.Contains("class=\"c del\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"c add\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_diff_page_marks_each_kind()
    {
        var diff = JsonDiffer.Compare("""{"a":1,"b":true}""", """{"a":2,"c":null}""").Value!;
        var html = DiffHtmlExporter.FromJson(JsonDiffLayoutBuilder.Build(diff), "Original", "Changed");

        Assert.Contains("class=\"c chg\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"c gap\"", html, StringComparison.Ordinal);
    }
}

public sealed class DateOrderTests
{
    private static readonly System.Globalization.CultureInfo India = new("en-IN");
    private static readonly System.Globalization.CultureInfo Us = new("en-US");

    private static DateTime Read(string text, DateOrder order, System.Globalization.CultureInfo culture) =>
        DateTimeConverter.Parse(text, EpochUnit.Auto, TimeZoneInfo.Utc, order, culture).Value!.Instant.UtcDateTime.Date;

    [Fact]
    public void Auto_follows_the_region()
    {
        Assert.Equal(new DateTime(2026, 10, 6), Read("06-10-2026", DateOrder.Auto, India));
        Assert.Equal(new DateTime(2026, 6, 10), Read("06-10-2026", DateOrder.Auto, Us));
    }

    [Fact]
    public void An_explicit_order_wins_over_the_region()
    {
        Assert.Equal(new DateTime(2026, 6, 10), Read("06-10-2026", DateOrder.MonthDayYear, India));
        Assert.Equal(new DateTime(2026, 10, 6), Read("06/10/2026", DateOrder.DayMonthYear, Us));
    }

    [Fact]
    public void A_four_digit_first_number_is_always_the_year()
    {
        Assert.Equal(new DateTime(2026, 10, 6), Read("2026-10-06", DateOrder.MonthDayYear, Us));
        Assert.Equal(new DateTime(2026, 10, 6), Read("2026.10.06", DateOrder.DayMonthYear, India));
    }

    [Fact]
    public void Says_how_it_read_an_ambiguous_date_and_keeps_the_time()
    {
        var result = DateTimeConverter.Parse("06-10-2026 14:30", EpochUnit.Auto, TimeZoneInfo.Utc, DateOrder.Auto, India);

        Assert.Equal(new DateTime(2026, 10, 6, 14, 30, 0), result.Value!.Instant.UtcDateTime);
        Assert.Contains("6 October 2026", result.Value.ReadAs, StringComparison.Ordinal);
        Assert.Contains("10 June 2026", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unambiguous_date_has_no_warning_and_a_bad_one_is_explained()
    {
        Assert.False(DateTimeConverter.Parse("25-12-2026", EpochUnit.Auto, TimeZoneInfo.Utc, DateOrder.Auto, India).HasWarning);

        var bad = DateTimeConverter.Parse("25-12-2026", EpochUnit.Auto, TimeZoneInfo.Utc, DateOrder.MonthDayYear, India);
        Assert.False(bad.IsSuccess);
        Assert.Contains("no month 25", bad.Error!.Message, StringComparison.Ordinal);
    }
}
