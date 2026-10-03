using DevTools.Core.Json;
using Xunit;

namespace DevTools.Core.Tests.Json;

public sealed class JsonPathQueryTests
{
    private const string Store = """
        {
          "store": {
            "book": [
              { "category": "reference", "author": "Nigel Rees",       "title": "Sayings of the Century", "price": 8.95 },
              { "category": "fiction",   "author": "Evelyn Waugh",     "title": "Sword of Honour",        "price": 12.99 },
              { "category": "fiction",   "author": "Herman Melville",  "title": "Moby Dick",              "price": 8.99, "isbn": "0-553-21311-3" },
              { "category": "fiction",   "author": "J. R. R. Tolkien", "title": "The Lord of the Rings",  "price": 22.99, "isbn": "0-395-19395-8" }
            ],
            "bicycle": { "color": "red", "price": 19.95 }
          },
          "expensive": 10
        }
        """;

    private static IReadOnlyList<JsonNode> Select(string path, string json = Store)
    {
        var parsed = JsonReader.Parse(json);
        Assert.True(parsed.IsSuccess, parsed.ErrorMessage);

        var result = JsonPathQuery.Select(parsed.Value!.Root, path);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return [.. result.Value!.Select(static m => m.Node)];
    }

    private static IReadOnlyList<string> Strings(string path, string json = Store) =>
        [.. Select(path, json).OfType<JsonString>().Select(static s => s.Value)];

    [Fact]
    public void Root_returns_the_whole_document()
    {
        Assert.Single(Select("$"));
        Assert.Single(Select(""));
    }

    [Fact]
    public void Dot_notation_walks_members()
    {
        var colors = Strings("$.store.bicycle.color");
        Assert.Equal(["red"], colors);
    }

    [Fact]
    public void Bracket_notation_walks_members()
    {
        Assert.Equal(["red"], Strings("$['store']['bicycle']['color']"));
    }

    [Fact]
    public void Index_selects_one_element_and_negative_counts_from_the_end()
    {
        Assert.Equal(["Sayings of the Century"], Strings("$.store.book[0].title"));
        Assert.Equal(["The Lord of the Rings"], Strings("$.store.book[-1].title"));
    }

    [Fact]
    public void Out_of_range_index_selects_nothing_rather_than_failing()
    {
        Assert.Empty(Select("$.store.book[99]"));
    }

    [Fact]
    public void Wildcard_selects_every_element_or_member()
    {
        Assert.Equal(4, Select("$.store.book[*]").Count);
        Assert.Equal(2, Select("$.store.*").Count);
    }

    [Fact]
    public void Slices_support_start_end_and_step()
    {
        Assert.Equal(["Sayings of the Century", "Sword of Honour"], Strings("$.store.book[0:2].title"));
        Assert.Equal(["Sword of Honour", "The Lord of the Rings"], Strings("$.store.book[1::2].title"));
        Assert.Equal(["Moby Dick", "The Lord of the Rings"], Strings("$.store.book[-2:].title"));
    }

    [Fact]
    public void Recursive_descent_finds_a_member_at_any_depth()
    {
        var prices = Select("$..price");
        Assert.Equal(5, prices.Count);
    }

    [Fact]
    public void Union_of_names_and_of_indices()
    {
        Assert.Equal(2, Select("$.store.book[0,2]").Count);
        Assert.Equal(2, Select("$.store.bicycle['color','price']").Count);
    }

    [Fact]
    public void Filters_compare_numerically()
    {
        var titles = Strings("$.store.book[?(@.price > 10)].title");
        Assert.Equal(["Sword of Honour", "The Lord of the Rings"], titles);
    }

    [Fact]
    public void Filters_compare_strings_for_equality()
    {
        Assert.Equal(3, Select("$.store.book[?(@.category == 'fiction')]").Count);
        Assert.Single(Select("$.store.book[?(@.category != 'fiction')]"));
    }

    [Fact]
    public void Filter_without_an_operator_tests_for_existence()
    {
        Assert.Equal(2, Select("$.store.book[?(@.isbn)]").Count);
    }

    [Fact]
    public void Filter_containment_operator_matches_substrings()
    {
        Assert.Equal(["Moby Dick"], Strings("$.store.book[?(@.title =~ 'moby')].title"));
        Assert.Empty(Select("$.store.book[?(@.title =~ 'nothing here')]"));
    }

    [Fact]
    public void Query_renders_matches_as_a_json_array_with_a_count()
    {
        var parsed = JsonReader.Parse(Store);
        var result = JsonPathQuery.Query(parsed.Value!.Root, "$.store.book[*].category", JsonWriterOptions.Compact);

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Value!.MatchCount);
        Assert.Equal("[\"reference\",\"fiction\",\"fiction\",\"fiction\"]", result.Value.Output);
    }

    [Fact]
    public void Matches_carry_the_path_that_reached_them()
    {
        var parsed = JsonReader.Parse(Store);
        var result = JsonPathQuery.Select(parsed.Value!.Root, "$.store.book[1].title");

        Assert.True(result.IsSuccess);
        Assert.Equal("$.store.book[1].title", result.Value![0].Path);
    }

    [Theory]
    [InlineData("$.store[")]
    [InlineData("$.store.book[1:2:0]")]
    [InlineData("$..")]
    [InlineData("$.store.book[?()]")]
    public void Malformed_expressions_fail_with_a_message(string path)
    {
        var parsed = JsonReader.Parse(Store);
        var result = JsonPathQuery.Select(parsed.Value!.Root, path);

        Assert.False(result.IsSuccess, $"'{path}' should not have parsed");
        Assert.NotEmpty(result.ErrorMessage);
    }

    [Fact]
    public void Number_comparisons_do_not_go_through_double()
    {
        const string json = """{ "values": [ { "n": 0.1 }, { "n": 0.2 }, { "n": 0.30000000000000004 } ] }""";
        Assert.Single(Select("$.values[?(@.n > 0.25)]", json));
    }
}
