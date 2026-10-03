using DevTools.Core;
using DevTools.Core.Json;
using DevTools.Core.Text;
using Xunit;

namespace DevTools.Core.Tests.Json;

public sealed class JsonFormatterTests
{
    private static string Pretty(string json, JsonFormatOptions? options = null)
    {
        var result = JsonFormatter.Format(json, options);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return result.Value!.Output;
    }

    [Fact]
    public void Pretty_indents_two_spaces_by_default()
    {
        Assert.Equal("{\n  \"a\": 1\n}", Pretty("{\"a\":1}"));
    }

    [Fact]
    public void Pretty_honours_four_spaces_and_tab()
    {
        Assert.Equal("{\n    \"a\": 1\n}",
            Pretty("{\"a\":1}", new JsonFormatOptions { IndentStyle = IndentStyle.FourSpaces }));

        Assert.Equal("{\n\t\"a\": 1\n}",
            Pretty("{\"a\":1}", new JsonFormatOptions { IndentStyle = IndentStyle.Tab }));
    }

    [Fact]
    public void Minify_removes_every_insignificant_byte()
    {
        Assert.Equal("{\"a\":[1,2],\"b\":{\"c\":null}}",
            Pretty("{ \"a\" : [ 1 , 2 ] , \"b\" : { \"c\" : null } }",
                new JsonFormatOptions { Mode = JsonFormatMode.Minify }));
    }

    [Fact]
    public void Empty_and_whitespace_input_produce_an_empty_result_not_an_error()
    {
        foreach (var input in new[] { "", "   ", "\t\n  " })
        {
            var result = JsonFormatter.Format(input);
            Assert.True(result.IsSuccess);
            Assert.Equal(string.Empty, result.Value!.Output);
        }
    }

    // FR-J05 — the trap every JSON formatter that routes through double gets wrong.
    [Theory]
    [InlineData("1.0")]
    [InlineData("1.00")]
    [InlineData("1e10")]
    [InlineData("1E+10")]
    [InlineData("-0")]
    [InlineData("0.1")]
    [InlineData("123456789012345678901234567890")]
    [InlineData("1.7976931348623157e309")]
    [InlineData("0.000000000000000000001")]
    public void Numbers_round_trip_byte_for_byte(string number)
    {
        var output = Pretty($"{{\"n\":{number}}}", new JsonFormatOptions { Mode = JsonFormatMode.Minify });
        Assert.Equal($"{{\"n\":{number}}}", output);
    }

    [Fact]
    public void Sort_keys_is_recursive_and_ordinal()
    {
        var output = Pretty("{\"b\":1,\"a\":{\"z\":1,\"y\":2}}",
            new JsonFormatOptions { Mode = JsonFormatMode.Minify, SortKeys = true });

        Assert.Equal("{\"a\":{\"y\":2,\"z\":1},\"b\":1}", output);
    }

    [Fact]
    public void Trailing_commas_are_refused_by_default_and_accepted_when_allowed()
    {
        var strict = JsonFormatter.Format("{\"a\":1,}");
        Assert.False(strict.IsSuccess);
        Assert.Contains("trailing comma", strict.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var tolerant = JsonFormatter.Format("{\"a\":1,}", new JsonFormatOptions { AllowTrailingCommas = true });
        Assert.True(tolerant.IsSuccess);
    }

    [Fact]
    public void Comments_are_refused_by_default_and_dropped_when_allowed()
    {
        Assert.False(JsonFormatter.Format("{// hi\n\"a\":1}").IsSuccess);

        var output = Pretty("{// hi\n\"a\":1 /* there */}",
            new JsonFormatOptions { Mode = JsonFormatMode.Minify, AllowComments = true });

        Assert.Equal("{\"a\":1}", output);
    }

    [Fact]
    public void Unclosed_block_comment_is_reported()
    {
        var result = JsonFormatter.Format("{/* never closed \n \"a\":1}", new JsonFormatOptions { AllowComments = true });
        Assert.False(result.IsSuccess);
        Assert.Contains("never closed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // FR-J08 — a position a developer can act on, not "parse error".
    [Fact]
    public void Errors_carry_line_column_and_offset()
    {
        var result = JsonFormatter.Format("{\n  \"a\": 1\n  \"b\": 2\n}");

        Assert.False(result.IsSuccess);
        Assert.Equal(3, result.Error!.Line);
        Assert.NotNull(result.Error.Column);
        Assert.NotNull(result.Error.Offset);
        Assert.Contains("line 3", result.Error.ToDisplayString(), StringComparison.Ordinal);
    }

    // FR-J09 — silently merging duplicates is how a differ reports a false 'equal'.
    [Fact]
    public void Duplicate_keys_are_kept_and_warned_about()
    {
        var result = JsonFormatter.Format("{\"a\":1,\"a\":2}", new JsonFormatOptions { Mode = JsonFormatMode.Minify });

        Assert.True(result.IsSuccess);
        Assert.Equal("{\"a\":1,\"a\":2}", result.Value!.Output);
        Assert.Contains(result.Value.Warnings, w => w.Contains("more than one member", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bom_is_stripped_before_parsing()
    {
        var result = JsonFormatter.Format("\uFEFF{\"a\":1}", new JsonFormatOptions { Mode = JsonFormatMode.Minify });
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("{\"a\":1}", result.Value!.Output);
    }

    [Fact]
    public void Escape_non_ascii_emits_surrogate_safe_escapes()
    {
        var output = Pretty("{\"a\":\"héllo 🎉\"}",
            new JsonFormatOptions { Mode = JsonFormatMode.Minify, EscapeNonAscii = true });

        Assert.Equal("{\"a\":\"h\\u00e9llo \\ud83c\\udf89\"}", output);
    }

    [Fact]
    public void Astral_characters_survive_a_round_trip()
    {
        var output = Pretty("{\"a\":\"🎉\"}", new JsonFormatOptions { Mode = JsonFormatMode.Minify });
        Assert.Equal("{\"a\":\"🎉\"}", output);
    }

    // Edge case 4 — an unpaired surrogate is not representable; say so rather than crash.
    [Fact]
    public void Unpaired_surrogate_becomes_replacement_char_with_a_warning()
    {
        var result = JsonFormatter.Format("{\"a\":\"\\ud83c\"}", new JsonFormatOptions { Mode = JsonFormatMode.Minify });

        Assert.True(result.IsSuccess);
        Assert.Contains("\uFFFD", result.Value!.Output, StringComparison.Ordinal);
        Assert.Contains(result.Value.Warnings, w => w.Contains("surrogate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Paired_surrogate_escapes_are_decoded_as_one_character()
    {
        var output = Pretty("{\"a\":\"\\ud83c\\udf89\"}", new JsonFormatOptions { Mode = JsonFormatMode.Minify });
        Assert.Equal("{\"a\":\"🎉\"}", output);
    }

    [Fact]
    public void Raw_control_characters_inside_a_string_are_rejected()
    {
        var result = JsonFormatter.Format("{\"a\":\"x\u0001y\"}");
        Assert.False(result.IsSuccess);
        Assert.Contains("control character", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_escape_sequences_are_named()
    {
        var result = JsonFormatter.Format(@"{""a"":""\q""}");
        Assert.False(result.IsSuccess);
        Assert.Contains("\\q", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Short_unicode_escape_is_reported()
    {
        var result = JsonFormatter.Format(@"{""a"":""\u12""}");
        Assert.False(result.IsSuccess);
        Assert.Contains("hexadecimal", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // Edge case 16 — refuse rather than overflow the stack.
    [Fact]
    public void Nesting_beyond_the_depth_limit_is_refused_not_crashed()
    {
        var deep = new string('[', Limits.MaxJsonDepth + 10) + new string(']', Limits.MaxJsonDepth + 10);
        var result = JsonFormatter.Format(deep);

        Assert.False(result.IsSuccess);
        Assert.Contains("nests deeper", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Content_after_the_document_is_rejected()
    {
        var result = JsonFormatter.Format("{\"a\":1} trailing");
        Assert.False(result.IsSuccess);
        Assert.Contains("after the document", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_only_reports_a_verdict_instead_of_a_document()
    {
        var result = JsonFormatter.Format("{\"a\":[1,2,3],\"b\":{\"c\":1}}",
            new JsonFormatOptions { Mode = JsonFormatMode.ValidateOnly });

        Assert.True(result.IsSuccess);
        Assert.Contains("Valid JSON", result.Value!.Output, StringComparison.Ordinal);
        Assert.Equal(2, result.Value.Stats.ObjectCount);
        Assert.Equal(1, result.Value.Stats.ArrayCount);
    }

    [Fact]
    public void Stats_count_objects_arrays_keys_and_depth()
    {
        var result = JsonFormatter.Inspect("{\"a\":{\"b\":[1,{\"c\":2}]}}");

        Assert.True(result.IsSuccess);
        var stats = result.Value!;
        Assert.Equal(3, stats.ObjectCount);
        Assert.Equal(1, stats.ArrayCount);
        Assert.Equal(3, stats.KeyCount);
        Assert.Equal(5, stats.MaxDepth);
    }

    [Fact]
    public void Empty_containers_render_compactly()
    {
        Assert.Equal("{\n  \"a\": {},\n  \"b\": []\n}", Pretty("{\"a\":{},\"b\":[]}"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[")]
    [InlineData("{\"a\"}")]
    [InlineData("{\"a\":}")]
    [InlineData("[1,]")]
    [InlineData("tru")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData("1e")]
    [InlineData("\"unterminated")]
    public void Malformed_documents_fail_with_a_message(string input)
    {
        var result = JsonFormatter.Format(input);
        Assert.False(result.IsSuccess, $"'{input}' should not have parsed");
        Assert.NotEmpty(result.ErrorMessage);
    }

    [Fact]
    public void Is_valid_answers_without_throwing()
    {
        Assert.True(JsonFormatter.IsValid("{\"a\":1}"));
        Assert.False(JsonFormatter.IsValid("{"));
        Assert.False(JsonFormatter.IsValid(""));
    }

    [Fact]
    public void Pretty_or_original_leaves_non_json_untouched()
    {
        Assert.Equal("not json at all", JsonFormatter.PrettyOrOriginal("not json at all"));
        Assert.Equal("{\n  \"a\": 1\n}", JsonFormatter.PrettyOrOriginal("{\"a\":1}"));
    }
}
