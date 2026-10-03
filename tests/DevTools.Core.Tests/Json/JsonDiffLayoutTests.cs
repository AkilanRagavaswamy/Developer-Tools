using DevTools.Core.Json;
using Xunit;

namespace DevTools.Core.Tests.Json;

/// <summary>
/// The side-by-side projection (FR-J28): two documents laid out row for row.
/// </summary>
public sealed class JsonDiffLayoutTests
{
    private static JsonDiffLayout Layout(string left, string right, JsonDiffOptions? options = null)
    {
        var diff = JsonDiffer.Compare(left, right, options);
        Assert.True(diff.IsSuccess, diff.Error?.Message);
        return JsonDiffLayoutBuilder.Build(diff.Value);
    }

    [Fact]
    public void Equal_documents_produce_no_differences()
    {
        var layout = Layout("""{"a":1,"b":"x"}""", """{"a":1,"b":"x"}""");

        Assert.Equal(0, layout.Total);
        Assert.Empty(layout.Differences);
        Assert.Equal("No differences", layout.Summary);
        Assert.All(layout.Rows, row => Assert.False(row.IsDifference));
    }

    [Fact]
    public void Equal_documents_line_up_row_for_row()
    {
        var layout = Layout("""{"a":1,"b":2}""", """{"a":1,"b":2}""");

        Assert.All(layout.Rows, row =>
        {
            Assert.Equal(row.LeftLine, row.RightLine);
            Assert.Equal(row.LeftText, row.RightText);
        });
    }

    [Fact]
    public void A_changed_value_is_an_unequal_value()
    {
        var layout = Layout("""{"a":1}""", """{"a":2}""");

        Assert.Equal(1, layout.UnequalValues);
        Assert.Equal(0, layout.IncorrectTypes);
        Assert.Equal(0, layout.MissingProperties);
        Assert.Equal(JsonDiffCategory.UnequalValue, layout.Differences[0].Category);
    }

    [Fact]
    public void A_changed_type_is_not_counted_as_a_changed_value()
    {
        var layout = Layout("""{"a":1}""", """{"a":"1"}""");

        Assert.Equal(1, layout.IncorrectTypes);
        Assert.Equal(0, layout.UnequalValues);
        Assert.Equal(JsonDiffCategory.IncorrectType, layout.Differences[0].Category);
    }

    [Fact]
    public void A_member_on_one_side_only_is_a_missing_property()
    {
        var layout = Layout("""{"a":1,"b":2}""", """{"a":1}""");

        Assert.Equal(1, layout.MissingProperties);
        Assert.Equal(JsonDiffCategory.MissingProperty, layout.Differences[0].Category);
    }

    [Fact]
    public void A_missing_member_leaves_the_other_side_blank()
    {
        var layout = Layout("""{"a":1,"b":2}""", """{"a":1}""");

        var row = layout.Rows.Single(r => r.Category == JsonDiffCategory.MissingProperty);

        Assert.NotNull(row.LeftLine);
        Assert.Contains("\"b\"", row.LeftText, StringComparison.Ordinal);
        Assert.Null(row.RightLine);
        Assert.Equal(string.Empty, row.RightText);
    }

    [Fact]
    public void A_blank_does_not_advance_that_side_s_line_numbers()
    {
        // The right document is one line shorter, so its last line number is one lower —
        // the numbering stays each document's own even though the rows stay level.
        var layout = Layout("""{"a":1,"b":2,"c":3}""", """{"a":1,"c":3}""");

        var lastLeft = layout.Rows.Where(r => r.LeftLine is not null).Max(r => r.LeftLine);
        var lastRight = layout.Rows.Where(r => r.RightLine is not null).Max(r => r.RightLine);

        Assert.Equal(lastLeft - 1, lastRight);
    }

    [Fact]
    public void Every_difference_points_at_a_row_that_carries_it()
    {
        var layout = Layout(
            """{"a":1,"b":{"c":2,"d":3},"e":[1,2,3]}""",
            """{"a":9,"b":{"c":2,"d":"3"},"e":[1,2]}""");

        Assert.NotEmpty(layout.Differences);

        foreach (var difference in layout.Differences)
        {
            var row = layout.Rows[difference.Row];
            Assert.Equal(difference.Category, row.Category);
            Assert.Equal(difference.Index, row.DifferenceIndex);
        }
    }

    [Fact]
    public void Differences_are_numbered_in_the_order_they_appear()
    {
        var layout = Layout("""{"a":1,"b":2,"c":3}""", """{"a":9,"b":8,"c":7}""");

        Assert.Equal([0, 1, 2], layout.Differences.Select(d => d.Index));

        var rows = layout.Differences.Select(d => d.Row).ToList();
        Assert.Equal(rows.OrderBy(r => r), rows);
    }

    [Fact]
    public void The_counts_add_up_to_the_total()
    {
        var layout = Layout(
            """{"same":1,"value":2,"type":3,"only":4}""",
            """{"same":1,"value":99,"type":"3"}""");

        Assert.Equal(1, layout.UnequalValues);
        Assert.Equal(1, layout.IncorrectTypes);
        Assert.Equal(1, layout.MissingProperties);
        Assert.Equal(3, layout.Total);
        Assert.Equal(3, layout.Differences.Count);
        Assert.Equal("Found 3 differences", layout.Summary);
    }

    [Fact]
    public void One_difference_reads_as_one()
    {
        var layout = Layout("""{"a":1}""", """{"a":2}""");

        Assert.Equal("Found 1 difference", layout.Summary);
    }

    [Fact]
    public void Nested_objects_are_walked_in_step()
    {
        var layout = Layout("""{"a":{"b":{"c":1}}}""", """{"a":{"b":{"c":2}}}""");

        // Three open braces, the leaf, three closes.
        Assert.Equal(7, layout.Rows.Count);
        Assert.All(layout.Rows, row => Assert.Equal(row.LeftLine, row.RightLine));
        Assert.Equal(1, layout.UnequalValues);
    }

    [Fact]
    public void Indentation_follows_the_requested_width()
    {
        var diff = JsonDiffer.Compare("""{"a":{"b":1}}""", """{"a":{"b":1}}""");
        var layout = JsonDiffLayoutBuilder.Build(diff.Value, indentWidth: 4);

        var leaf = layout.Rows.Single(r => r.LeftText.Contains("\"b\"", StringComparison.Ordinal));

        Assert.StartsWith("        \"b\"", leaf.LeftText, StringComparison.Ordinal);
    }

    [Fact]
    public void Members_are_separated_by_commas_except_the_last()
    {
        var layout = Layout("""{"a":1,"b":2}""", """{"a":1,"b":2}""");

        var a = layout.Rows.Single(r => r.LeftText.Contains("\"a\"", StringComparison.Ordinal));
        var b = layout.Rows.Single(r => r.LeftText.Contains("\"b\"", StringComparison.Ordinal));

        Assert.EndsWith(",", a.LeftText, StringComparison.Ordinal);
        Assert.False(b.LeftText.EndsWith(','));
    }

    [Fact]
    public void The_comma_follows_each_side_s_own_last_member()
    {
        // "b" is last on the right, so it takes no comma there — but it does on the left,
        // where "c" still follows it.
        var layout = Layout("""{"a":1,"b":2,"c":3}""", """{"a":1,"b":2}""");

        var b = layout.Rows.Single(r => r.LeftText.Contains("\"b\"", StringComparison.Ordinal));

        Assert.EndsWith(",", b.LeftText, StringComparison.Ordinal);
        Assert.False(b.RightText.EndsWith(','));
    }

    [Fact]
    public void Array_elements_are_rendered_without_a_name()
    {
        var layout = Layout("""{"a":[1,2]}""", """{"a":[1,2]}""");

        var first = layout.Rows.First(r => r.LeftText.Trim() == "1,");

        Assert.DoesNotContain(":", first.LeftText, StringComparison.Ordinal);
    }

    [Fact]
    public void An_element_only_the_right_has_leaves_the_left_blank()
    {
        var layout = Layout("""{"a":[1]}""", """{"a":[1,2]}""");

        var row = layout.Rows.Single(r => r.Category == JsonDiffCategory.MissingProperty);

        Assert.Null(row.LeftLine);
        Assert.Equal(string.Empty, row.LeftText);
        Assert.Contains("2", row.RightText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_whose_type_changed_from_object_to_scalar_keeps_both_sides_readable()
    {
        var layout = Layout("""{"a":{"b":1,"c":2}}""", """{"a":"text"}""");

        Assert.Equal(1, layout.IncorrectTypes);

        // The object side still reads as an object rather than collapsing to one line.
        var rows = layout.Rows.Where(r => r.Category == JsonDiffCategory.IncorrectType).ToList();

        Assert.True(rows.Count > 1);
        Assert.Contains(rows, r => r.LeftText.Contains("\"b\"", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.RightText.Contains("text", StringComparison.Ordinal));
    }

    [Fact]
    public void Awkward_member_names_are_quoted_and_escaped()
    {
        var layout = Layout("""{"we\"ird":1}""", """{"we\"ird":2}""");

        var row = layout.Rows.Single(r => r.Category == JsonDiffCategory.UnequalValue);

        Assert.Contains("we\\\"ird", row.LeftText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_value_is_not_trimmed()
    {
        var long1 = new string('x', 400);
        var long2 = new string('y', 400);

        var layout = Layout($$"""{"a":"{{long1}}"}""", $$"""{"a":"{{long2}}"}""");

        var row = layout.Rows.Single(r => r.Category == JsonDiffCategory.UnequalValue);

        Assert.Contains(long1, row.LeftText, StringComparison.Ordinal);
        Assert.Contains(long2, row.RightText, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_row_carries_the_path_it_came_from()
    {
        var layout = Layout("""{"a":{"b":1}}""", """{"a":{"b":2}}""");

        var row = layout.Rows.Single(r => r.Category == JsonDiffCategory.UnequalValue);

        Assert.Equal("$.a.b", row.Path);
        Assert.Equal("$.a.b", layout.Differences[0].Path);
    }

    [Fact]
    public void A_null_result_is_an_empty_layout()
    {
        var layout = JsonDiffLayoutBuilder.Build(null);

        Assert.Empty(layout.Rows);
        Assert.Equal(0, layout.Total);
    }

    [Fact]
    public void Reordered_key_matched_elements_still_pair_on_one_row()
    {
        var options = new JsonDiffOptions { ArrayStrategy = ArrayStrategy.Key, KeyField = "id" };

        var layout = Layout(
            """{"rows":[{"id":1,"v":"a"},{"id":2,"v":"b"}]}""",
            """{"rows":[{"id":2,"v":"b"},{"id":1,"v":"z"}]}""",
            options);

        // Only the value of id 1 changed; pairing across the reorder means one difference,
        // not two elements replaced.
        Assert.Equal(1, layout.UnequalValues);
        Assert.Equal(0, layout.MissingProperties);

        var row = layout.Rows.Single(r => r.Category == JsonDiffCategory.UnequalValue);

        Assert.Contains("\"a\"", row.LeftText, StringComparison.Ordinal);
        Assert.Contains("\"z\"", row.RightText, StringComparison.Ordinal);
    }
}
