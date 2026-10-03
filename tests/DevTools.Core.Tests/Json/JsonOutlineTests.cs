using DevTools.Core.Json;
using Xunit;

namespace DevTools.Core.Tests.Json;

/// <summary>
/// The browsable projection behind the tree view. What matters here is that a collapsed row
/// says something useful, that paths are queries that actually select the node they name, and
/// that children are not walked until something asks.
/// </summary>
public sealed class JsonOutlineTests
{
    [Fact]
    public void The_root_of_an_object_summarises_its_keys()
    {
        var outline = Build("""{"a":1,"b":2}""");

        Assert.Equal("$", outline.Name);
        Assert.Equal(JsonKind.Object, outline.Kind);
        Assert.True(outline.IsBranch);
        Assert.Equal(2, outline.ChildCount);
        Assert.Equal("{ 2 keys }", outline.ChildSummary);
    }

    [Fact]
    public void One_key_and_one_item_are_not_pluralised()
    {
        Assert.Equal("{ 1 key }", Build("""{"a":1}""").ChildSummary);
        Assert.Equal("[ 1 item ]", Build("[1]").ChildSummary);
    }

    [Fact]
    public void An_array_names_its_elements_by_index()
    {
        var outline = Build("""["x","y"]""");

        Assert.Equal(["[0]", "[1]"], outline.Children.Select(c => c.Name));
        Assert.Equal(["$[0]", "$[1]"], outline.Children.Select(c => c.Path));
    }

    [Fact]
    public void A_leaf_shows_its_value_in_json_notation()
    {
        var outline = Build("""{"s":"hi","n":1,"t":true,"z":null}""");
        var summaries = outline.Children.ToDictionary(c => c.Name, c => c.Summary);

        Assert.Equal("\"hi\"", summaries["s"]);
        Assert.Equal("1", summaries["n"]);
        Assert.Equal("true", summaries["t"]);
        Assert.Equal("null", summaries["z"]);
    }

    /// <summary>
    /// The same fidelity rule the formatter keeps: a number is shown as it was written, because
    /// 1.0 is not 1 and a 30-digit integer does not survive a double.
    /// </summary>
    [Fact]
    public void A_number_keeps_its_source_text()
    {
        var outline = Build("""{"a":1.0,"b":123456789012345678901234567890,"c":1e3}""");
        var summaries = outline.Children.ToDictionary(c => c.Name, c => c.Summary);

        Assert.Equal("1.0", summaries["a"]);
        Assert.Equal("123456789012345678901234567890", summaries["b"]);
        Assert.Equal("1e3", summaries["c"]);
    }

    [Fact]
    public void A_branch_has_no_value_summary_and_a_leaf_has_no_child_summary()
    {
        var outline = Build("""{"branch":{"x":1},"leaf":2}""");
        var branch = outline.Children.First(c => c.Name == "branch");
        var leaf = outline.Children.First(c => c.Name == "leaf");

        Assert.Empty(branch.Summary);
        Assert.Empty(leaf.ChildSummary);
        Assert.False(leaf.IsBranch);
        Assert.Empty(leaf.Children);
    }

    [Fact]
    public void Nested_paths_read_as_jsonpath()
    {
        var outline = Build("""{"store":{"book":[{"title":"t"}]}}""");

        var store = outline.Children.Single();
        var book = store.Children.Single();
        var first = book.Children.Single();
        var title = first.Children.Single();

        Assert.Equal("$.store", store.Path);
        Assert.Equal("$.store.book", book.Path);
        Assert.Equal("$.store.book[0]", first.Path);
        Assert.Equal("$.store.book[0].title", title.Path);
    }

    /// <summary>
    /// A name with a dot or a space in it is legal JSON and illegal in dot notation, so the
    /// path has to bracket it — otherwise the copied query selects something else entirely.
    /// </summary>
    [Theory]
    [InlineData("a.b", "$['a.b']")]
    [InlineData("with space", "$['with space']")]
    [InlineData("has[bracket]", "$['has[bracket]']")]
    [InlineData("", "$['']")]
    [InlineData("plain_Name9", "$.plain_Name9")]
    public void An_awkward_member_name_is_bracketed(string name, string expected)
    {
        var json = $$"""{"{{name}}":1}""";

        Assert.Equal(expected, Build(json).Children.Single().Path);
    }

    [Fact]
    public void A_quote_in_a_name_is_escaped_in_the_path()
    {
        var outline = Build("""{"it's":1}""");

        Assert.Equal(@"$['it\'s']", outline.Children.Single().Path);
    }

    /// <summary>Every row carries the line it starts on, so the tree can drive the text view.</summary>
    [Fact]
    public void Every_row_knows_its_line()
    {
        var outline = Build("{\n  \"a\": 1,\n  \"b\": {\n    \"c\": 2\n  }\n}");
        var children = outline.Children.ToDictionary(c => c.Name);

        Assert.Equal(1, outline.Line);
        Assert.Equal(2, children["a"].Line);
        Assert.Equal(3, children["b"].Line);
        Assert.Equal(4, children["b"].Children.Single().Line);
    }

    [Fact]
    public void Duplicate_member_names_are_both_kept()
    {
        var outline = Build("""{"a":1,"a":2}""");

        Assert.Equal(2, outline.ChildCount);
        Assert.Equal(["1", "2"], outline.Children.Select(c => c.Summary));
    }

    [Fact]
    public void A_scalar_document_is_a_single_leaf()
    {
        var outline = Build("42");

        Assert.False(outline.IsBranch);
        Assert.Equal("42", outline.Summary);
        Assert.Empty(outline.Children);
    }

    [Fact]
    public void Bad_input_fails_rather_than_throwing()
    {
        var result = JsonOutline.Build("{not json");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Empty_input_fails_with_a_message_rather_than_an_empty_tree()
    {
        Assert.False(JsonOutline.Build(string.Empty).IsSuccess);
        Assert.False(JsonOutline.Build(null).IsSuccess);
    }

    /// <summary>
    /// Children are built on the first ask and then kept: the tree view asks repeatedly as rows
    /// are expanded and collapsed, and rebuilding each time would lose the node identity the
    /// view relies on.
    /// </summary>
    [Fact]
    public void Children_are_built_once()
    {
        var outline = Build("""{"a":{"b":1}}""");

        Assert.Same(outline.Children, outline.Children);
        Assert.Same(outline.Children[0], outline.Children[0]);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_accepted_when_the_options_say_so()
    {
        var result = JsonOutline.Build(
            """
            {
              // a comment
              "a": 1,
            }
            """,
            new JsonFormatOptions { AllowComments = true, AllowTrailingCommas = true });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, result.Value!.ChildCount);
    }

    private static JsonOutlineNode Build(string json)
    {
        var result = JsonOutline.Build(json);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }
}
