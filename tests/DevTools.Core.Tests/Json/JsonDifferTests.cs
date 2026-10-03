using DevTools.Core.Json;
using Xunit;

namespace DevTools.Core.Tests.Json;

public sealed class JsonDifferTests
{
    private static JsonDiffResult Diff(string left, string right, JsonDiffOptions? options = null)
    {
        var result = JsonDiffer.Compare(left, right, options);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return result.Value!;
    }

    /// <summary>
    /// The guarantee that matters: whatever the differ reports, applying its patch to the
    /// left document must produce the right one. A diff that cannot be applied is a diff
    /// that is describing something other than the change in front of it.
    /// </summary>
    private static void AssertPatchReproducesRight(string left, string right, JsonDiffOptions? options = null)
    {
        var diff = Diff(left, right, options);

        var applied = JsonPatch.Apply(left, diff.JsonPatch, JsonWriterOptions.Compact);
        Assert.True(applied.IsSuccess, $"patch failed to apply: {applied.ErrorMessage}\npatch was:\n{diff.JsonPatch}");

        var expected = JsonFormatter.Format(right, new JsonFormatOptions { Mode = JsonFormatMode.Minify, SortKeys = true });
        var actual = JsonFormatter.Format(applied.Value!, new JsonFormatOptions { Mode = JsonFormatMode.Minify, SortKeys = true });

        Assert.True(expected.IsSuccess);
        Assert.True(actual.IsSuccess);
        Assert.Equal(expected.Value!.Output, actual.Value!.Output);
    }

    // ---- equality ------------------------------------------------------------------

    // FR-J20 — this is the whole point of a semantic differ.
    [Fact]
    public void Member_order_is_never_a_difference()
    {
        var diff = Diff("{\"a\":1,\"b\":2}", "{\"b\":2,\"a\":1}");

        Assert.True(diff.AreEqual);
        Assert.Equal(0, diff.TotalDifferences);
        Assert.Equal("[]", diff.JsonPatch);
    }

    [Fact]
    public void Numbers_written_differently_but_equal_in_value_are_equal()
    {
        var diff = Diff("{\"a\":1}", "{\"a\":1.0}");
        Assert.True(diff.AreEqual);

        Assert.True(Diff("{\"a\":1e2}", "{\"a\":100}").AreEqual);
        Assert.True(Diff("{\"a\":0}", "{\"a\":-0}").AreEqual);
    }

    [Fact]
    public void Identical_documents_are_equal()
    {
        const string json = """{ "a": [1, 2, {"b": null}], "c": "x" }""";
        Assert.True(Diff(json, json).AreEqual);
    }

    [Fact]
    public void Two_empty_documents_are_equal()
    {
        var result = JsonDiffer.Compare("", "");
        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.AreEqual);
    }

    [Fact]
    public void One_empty_side_is_reported_rather_than_diffed()
    {
        Assert.False(JsonDiffer.Compare("", "{}").IsSuccess);
        Assert.False(JsonDiffer.Compare("{}", "").IsSuccess);
    }

    [Fact]
    public void A_malformed_side_is_named_in_the_message()
    {
        var left = JsonDiffer.Compare("{", "{}");
        Assert.False(left.IsSuccess);
        Assert.Contains("Left-hand", left.ErrorMessage, StringComparison.Ordinal);

        var right = JsonDiffer.Compare("{}", "{");
        Assert.False(right.IsSuccess);
        Assert.Contains("Right-hand", right.ErrorMessage, StringComparison.Ordinal);
    }

    // ---- differences ---------------------------------------------------------------

    [Fact]
    public void Added_removed_and_changed_members_are_counted()
    {
        var diff = Diff("{\"keep\":1,\"drop\":2,\"edit\":3}", "{\"keep\":1,\"edit\":4,\"new\":5}");

        Assert.False(diff.AreEqual);
        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);
        Assert.Equal(1, diff.Changed);
        Assert.Equal(1, diff.Unchanged);
    }

    [Fact]
    public void A_kind_change_is_a_change_not_an_add_and_a_remove()
    {
        var diff = Diff("{\"a\":1}", "{\"a\":\"1\"}");
        Assert.Equal(1, diff.Changed);
        Assert.Equal(0, diff.Added);
        Assert.Equal(0, diff.Removed);
    }

    [Fact]
    public void Diff_nodes_carry_a_marker_glyph_as_well_as_a_kind()
    {
        var diff = Diff("{\"a\":1}", "{\"b\":1}");
        var markers = diff.Root.Children.Select(static c => c.Marker).ToList();

        Assert.Contains("−", markers);
        Assert.Contains("+", markers);
    }

    [Fact]
    public void Nested_changes_carry_a_full_path()
    {
        var diff = Diff("{\"a\":{\"b\":{\"c\":1}}}", "{\"a\":{\"b\":{\"c\":2}}}");

        var leaf = Flatten(diff.Root).Single(static n => n.Kind == JsonDiffKind.Changed && !n.HasChildren);
        Assert.Equal("$.a.b.c", leaf.Path);
        Assert.Equal("1", leaf.Left);
        Assert.Equal("2", leaf.Right);
    }

    // ---- array strategies ----------------------------------------------------------

    // FR-J21 — the case that separates a good differ from a naive one.
    [Fact]
    public void Best_match_reports_one_insertion_not_a_cascade()
    {
        var diff = Diff("[1,2,3]", "[1,9,2,3]", new JsonDiffOptions { ArrayStrategy = ArrayStrategy.BestMatch });

        Assert.Equal(1, diff.Added);
        Assert.Equal(0, diff.Changed);
        Assert.Equal(0, diff.Removed);
    }

    [Fact]
    public void Index_strategy_reports_the_cascade_that_best_match_avoids()
    {
        var diff = Diff("[1,2,3]", "[1,9,2,3]", new JsonDiffOptions { ArrayStrategy = ArrayStrategy.Index });

        // 2→9, 3→2, then 3 added: positional comparison, exactly as asked for.
        Assert.Equal(2, diff.Changed);
        Assert.Equal(1, diff.Added);
    }

    [Fact]
    public void Best_match_reports_a_deletion_in_the_middle()
    {
        var diff = Diff("[1,2,3,4]", "[1,3,4]");

        Assert.Equal(1, diff.Removed);
        Assert.Equal(0, diff.Changed);
        Assert.Equal(0, diff.Added);
    }

    [Fact]
    public void Key_strategy_pairs_records_that_reordered()
    {
        const string left = """[{"id":1,"v":"a"},{"id":2,"v":"b"}]""";
        const string right = """[{"id":2,"v":"b"},{"id":1,"v":"z"}]""";

        var diff = Diff(left, right, new JsonDiffOptions { ArrayStrategy = ArrayStrategy.Key, KeyField = "id" });

        Assert.Equal(1, diff.Changed);
        Assert.Equal(0, diff.Added);
        Assert.Equal(0, diff.Removed);
    }

    [Fact]
    public void Key_strategy_warns_when_elements_have_no_key()
    {
        var diff = Diff("""[{"id":1},{"nokey":true}]""", """[{"id":1}]""",
            new JsonDiffOptions { ArrayStrategy = ArrayStrategy.Key, KeyField = "id" });

        Assert.Contains(diff.Warnings, w => w.Contains("no \"id\" member", StringComparison.Ordinal));
    }

    [Fact]
    public void Ignore_array_order_treats_arrays_as_multisets()
    {
        var strict = Diff("[1,2,3]", "[3,2,1]");
        Assert.False(strict.AreEqual);

        var loose = Diff("[1,2,3]", "[3,2,1]", new JsonDiffOptions { IgnoreArrayOrder = true });
        Assert.True(loose.AreEqual);
    }

    [Fact]
    public void Ignore_array_order_still_detects_a_genuine_difference()
    {
        var diff = Diff("[1,2,3]", "[3,2,4]", new JsonDiffOptions { IgnoreArrayOrder = true });
        Assert.False(diff.AreEqual);
    }

    // ---- loosening options ----------------------------------------------------------

    [Fact]
    public void Numeric_tolerance_absorbs_small_differences()
    {
        Assert.False(Diff("{\"a\":1.00}", "{\"a\":1.02}").AreEqual);
        Assert.True(Diff("{\"a\":1.00}", "{\"a\":1.02}", new JsonDiffOptions { NumericTolerance = 0.05m }).AreEqual);
        Assert.False(Diff("{\"a\":1.00}", "{\"a\":1.20}", new JsonDiffOptions { NumericTolerance = 0.05m }).AreEqual);
    }

    [Fact]
    public void Ignore_case_in_values_and_in_keys_are_independent()
    {
        Assert.False(Diff("{\"a\":\"X\"}", "{\"a\":\"x\"}").AreEqual);
        Assert.True(Diff("{\"a\":\"X\"}", "{\"a\":\"x\"}", new JsonDiffOptions { IgnoreCaseInValues = true }).AreEqual);

        Assert.False(Diff("{\"A\":1}", "{\"a\":1}").AreEqual);
        Assert.True(Diff("{\"A\":1}", "{\"a\":1}", new JsonDiffOptions { IgnoreCaseInKeys = true }).AreEqual);
    }

    [Fact]
    public void Null_equals_missing_when_asked()
    {
        Assert.False(Diff("{\"a\":null}", "{}").AreEqual);
        Assert.True(Diff("{\"a\":null}", "{}", new JsonDiffOptions { NullEqualsMissing = true }).AreEqual);
    }

    // FR-J22 — the option that makes diffing two API responses usable at all.
    [Fact]
    public void Ignore_paths_excludes_matching_nodes_at_any_depth()
    {
        const string left = """{ "data": {"v": 1}, "meta": {"timestamp": "2026-01-01"} }""";
        const string right = """{ "data": {"v": 1}, "meta": {"timestamp": "2026-09-17"} }""";

        Assert.False(Diff(left, right).AreEqual);

        var ignored = Diff(left, right, new JsonDiffOptions { IgnorePaths = ["$..timestamp"] });
        Assert.True(ignored.AreEqual);
    }

    [Fact]
    public void Ignore_paths_supports_single_segment_and_index_wildcards()
    {
        const string left = """{ "items": [ {"id":1,"etag":"a"}, {"id":2,"etag":"b"} ] }""";
        const string right = """{ "items": [ {"id":1,"etag":"x"}, {"id":2,"etag":"y"} ] }""";

        Assert.True(Diff(left, right, new JsonDiffOptions { IgnorePaths = ["$.items[*].etag"] }).AreEqual);
    }

    [Fact]
    public void Ignore_paths_does_not_swallow_unrelated_changes()
    {
        const string left = """{ "meta": {"ts": 1}, "value": 1 }""";
        const string right = """{ "meta": {"ts": 2}, "value": 2 }""";

        var diff = Diff(left, right, new JsonDiffOptions { IgnorePaths = ["$.meta.*"] });
        Assert.False(diff.AreEqual);
        Assert.Equal(1, diff.Changed);
    }

    // ---- the RFC 6902 patch ----------------------------------------------------------

    [Fact]
    public void Equal_documents_produce_an_empty_patch()
    {
        Assert.Equal("[]", Diff("{\"a\":1}", "{\"a\":1}").JsonPatch);
    }

    [Theory]
    // object edits
    [InlineData("""{"a":1}""", """{"a":2}""")]
    [InlineData("""{"a":1}""", """{"b":1}""")]
    [InlineData("""{}""", """{"a":1,"b":[1,2]}""")]
    [InlineData("""{"a":1,"b":2}""", """{}""")]
    [InlineData("""{"a":{"b":{"c":1}}}""", """{"a":{"b":{"c":2,"d":3}}}""")]
    // type changes
    [InlineData("""{"a":1}""", """{"a":"1"}""")]
    [InlineData("""{"a":[1]}""", """{"a":{"x":1}}""")]
    [InlineData("""{"a":null}""", """{"a":false}""")]
    // array edits — the ones where indices shift under you
    [InlineData("""[1,2,3]""", """[1,9,2,3]""")]
    [InlineData("""[1,2,3,4]""", """[1,3,4]""")]
    [InlineData("""[1,2,3]""", """[3,2,1]""")]
    [InlineData("""[]""", """[1,2,3]""")]
    [InlineData("""[1,2,3]""", """[]""")]
    [InlineData("""[1,2,3,4,5]""", """[5,4,3,2,1]""")]
    [InlineData("""[{"id":1},{"id":2}]""", """[{"id":2},{"id":3},{"id":1}]""")]
    // nested arrays inside objects
    [InlineData("""{"xs":[1,2],"y":1}""", """{"xs":[2,3,4],"y":1}""")]
    [InlineData("""{"a":[{"b":[1]}]}""", """{"a":[{"b":[1,2]}]}""")]
    // whole-document replacement
    [InlineData("""{"a":1}""", """[1,2]""")]
    [InlineData("5", "\"text\"")]
    public void The_patch_turns_the_left_document_into_the_right_one(string left, string right)
    {
        AssertPatchReproducesRight(left, right);
    }

    [Fact]
    public void The_patch_round_trips_a_realistic_payload()
    {
        const string left = """
            {
              "id": "c8f1", "version": 3, "active": true,
              "tags": ["alpha", "beta", "gamma"],
              "owner": { "name": "Ada", "email": "ada@example.com" },
              "items": [
                { "sku": "A1", "qty": 2, "price": 9.99 },
                { "sku": "B2", "qty": 1, "price": 24.50 }
              ],
              "notes": null
            }
            """;

        const string right = """
            {
              "id": "c8f1", "version": 4, "active": false,
              "tags": ["alpha", "gamma", "delta"],
              "owner": { "name": "Ada Lovelace", "email": "ada@example.com", "role": "admin" },
              "items": [
                { "sku": "A1", "qty": 5, "price": 9.99 },
                { "sku": "C3", "qty": 7, "price": 1.00 },
                { "sku": "B2", "qty": 1, "price": 24.50 }
              ]
            }
            """;

        AssertPatchReproducesRight(left, right);
    }

    [Fact]
    public void Patch_escapes_json_pointer_special_characters()
    {
        AssertPatchReproducesRight("""{"a/b":1,"c~d":2}""", """{"a/b":2,"c~d":3}""");
    }

    [Fact]
    public void Patch_apply_reports_a_failed_test_operation()
    {
        var result = JsonPatch.Apply("""{"a":1}""", """[{"op":"test","path":"/a","value":2}]""");
        Assert.False(result.IsSuccess);
        Assert.Contains("test", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Patch_apply_supports_move_and_copy()
    {
        var moved = JsonPatch.Apply("""{"a":1,"b":2}""", """[{"op":"move","from":"/a","path":"/c"}]""", JsonWriterOptions.Compact);
        Assert.True(moved.IsSuccess, moved.ErrorMessage);
        Assert.Equal("""{"b":2,"c":1}""", moved.Value);

        var copied = JsonPatch.Apply("""{"a":1}""", """[{"op":"copy","from":"/a","path":"/b"}]""", JsonWriterOptions.Compact);
        Assert.True(copied.IsSuccess, copied.ErrorMessage);
        Assert.Equal("""{"a":1,"b":1}""", copied.Value);
    }

    [Fact]
    public void Patch_apply_rejects_an_unknown_operation()
    {
        var result = JsonPatch.Apply("""{"a":1}""", """[{"op":"frobnicate","path":"/a"}]""");
        Assert.False(result.IsSuccess);
        Assert.Contains("RFC 6902", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Patch_apply_appends_with_the_dash_token()
    {
        var result = JsonPatch.Apply("""{"a":[1,2]}""", """[{"op":"add","path":"/a/-","value":3}]""", JsonWriterOptions.Compact);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("""{"a":[1,2,3]}""", result.Value);
    }

    // ---- textual fallback -----------------------------------------------------------

    [Fact]
    public void Textual_mode_falls_back_to_the_line_differ()
    {
        var result = JsonDiffer.CompareAsText("{\n\"a\": 1\n}", "{\n\"a\": 2\n}");
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.NotNull(result.Value);
    }

    private static IEnumerable<JsonDiffNode> Flatten(JsonDiffNode node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }
}
