using System.Text.Json;
using System.Text.Json.Serialization;
using DevTools.Core.CodeGen;
using DevTools.Core.Json;
using Xunit;

namespace DevTools.Core.Tests.CodeGen;

public sealed class CSharpFromJsonTests
{
    private static CodeGenResult Generate(string json, CSharpGenOptions? options = null)
    {
        var result = CSharpFromJson.Generate(json, options);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return result.Value!;
    }

    private static string Code(string json, CSharpGenOptions? options = null) => Generate(json, options).Code;

    // ---- shape inference ------------------------------------------------------------

    [Fact]
    public void Scalars_map_to_the_narrowest_type_that_holds_them()
    {
        var code = Code("""
            { "i": 1, "l": 5000000000, "d": 1.5, "b": true, "s": "x", "n": null }
            """, CSharpGenOptions.Default with { DetectDateGuidUri = false });

        Assert.Contains("public int I { get; init; }", code, StringComparison.Ordinal);
        Assert.Contains("public long L { get; init; }", code, StringComparison.Ordinal);
        Assert.Contains("public decimal D { get; init; }", code, StringComparison.Ordinal);
        Assert.Contains("public bool B { get; init; }", code, StringComparison.Ordinal);
        Assert.Contains("public string S { get; init; }", code, StringComparison.Ordinal);
        Assert.Contains("public object? N { get; init; }", code, StringComparison.Ordinal);
    }

    // FR-J40 — the trap: trusting the first element of an array.
    [Fact]
    public void Every_array_element_contributes_to_the_inferred_shape()
    {
        var code = Code("""
            { "items": [ { "a": 1 }, { "a": 2, "b": "later" } ] }
            """);

        Assert.Contains("public int A { get; init; }", code, StringComparison.Ordinal);
        Assert.Contains("public string? B { get; init; }", code, StringComparison.Ordinal);
    }

    // FR-J41 — widening rather than picking whichever came first.
    [Fact]
    public void Fractional_numbers_can_be_emitted_as_double()
    {
        var code = Code(
            """{ "price": 1.5, "count": 2, "xs": [ {"n": 1}, {"n": 1.5} ] }""",
            CSharpGenOptions.Default with { FractionalNumberType = FractionalNumberType.Double });

        Assert.Contains("public double Price { get; init; }", code, StringComparison.Ordinal);
        Assert.Contains("public double N { get; init; }", code, StringComparison.Ordinal);
        Assert.Contains("public int Count { get; init; }", code, StringComparison.Ordinal);
        Assert.DoesNotContain("decimal", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_widen_across_the_sample()
    {
        Assert.Contains("public long N { get; init; }",
            Code("""{ "xs": [ {"n": 1}, {"n": 5000000000} ] }"""), StringComparison.Ordinal);

        Assert.Contains("public decimal N { get; init; }",
            Code("""{ "xs": [ {"n": 1}, {"n": 1.5} ] }"""), StringComparison.Ordinal);
    }

    // FR-J41 — irreconcilable evidence is reported, never guessed at.
    [Fact]
    public void Conflicting_types_become_object_and_name_the_path()
    {
        var result = Generate("""{ "xs": [ {"v": 1}, {"v": "text"} ] }""");

        Assert.Contains("public object V { get; init; }", result.Code, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("$.xs[*].v", StringComparison.Ordinal));
    }

    // FR-J42
    [Fact]
    public void A_member_missing_from_some_samples_is_nullable()
    {
        var code = Code("""{ "xs": [ {"always": 1, "sometimes": 2}, {"always": 3} ] }""");

        Assert.Contains("public int Always { get; init; }", code, StringComparison.Ordinal);
        Assert.Contains("public int? Sometimes { get; init; }", code, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicitly_null_member_is_nullable_even_when_always_present()
    {
        var code = Code("""{ "xs": [ {"v": 1}, {"v": null} ] }""");
        Assert.Contains("public int? V { get; init; }", code, StringComparison.Ordinal);
    }

    // FR-J43
    [Fact]
    public void Dates_guids_and_uris_are_detected_from_string_shape()
    {
        var code = Code("""
            {
              "when": "2026-09-17T10:30:00+00:00",
              "id": "d9b2d63d-a233-4123-847a-d2a1a39e1aa9",
              "link": "https://example.com/a",
              "plain": "just text"
            }
            """);

        Assert.Contains("public DateTimeOffset When { get; init; }", code, StringComparison.Ordinal);
        Assert.Contains("public Guid Id { get; init; }", code, StringComparison.Ordinal);
        Assert.Contains("public Uri Link { get; init; }", code, StringComparison.Ordinal);
        Assert.Contains("public string Plain { get; init; }", code, StringComparison.Ordinal);
        Assert.Contains("using System;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Detection_can_be_turned_off()
    {
        var code = Code("""{ "id": "d9b2d63d-a233-4123-847a-d2a1a39e1aa9" }""",
            CSharpGenOptions.Default with { DetectDateGuidUri = false });

        Assert.Contains("public string Id { get; init; }", code, StringComparison.Ordinal);
    }

    // The probe must not turn ordinary short strings into timestamps.
    [Theory]
    [InlineData("3")]
    [InlineData("May")]
    [InlineData("12:30")]
    [InlineData("2026")]
    public void Short_strings_are_not_mistaken_for_dates(string value)
    {
        var code = Code($$"""{ "v": "{{value}}" }""");
        Assert.Contains("public string V { get; init; }", code, StringComparison.Ordinal);
    }

    // ---- naming ---------------------------------------------------------------------

    // FR-J45
    [Theory]
    [InlineData("first-name", "FirstName")]
    [InlineData("first_name", "FirstName")]
    [InlineData("FirstName", "FirstName")]
    [InlineData("$ref", "Ref")]
    [InlineData("user.id", "UserId")]
    [InlineData("XMLHttpRequest", "XmlHttpRequest")]
    public void Member_names_become_pascal_case(string jsonName, string expected)
    {
        var code = Code($$"""{ "{{jsonName}}": 1 }""");
        Assert.Contains($"public int {expected} ", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_starting_with_a_digit_get_a_legal_prefix()
    {
        var code = Code("""{ "2fa": true }""");
        Assert.Contains("_2Fa", code, StringComparison.Ordinal);
        Assert.True(RoslynHarness.Compile(code).Succeeded);
    }

    [Fact]
    public void A_member_named_like_its_type_is_renamed()
    {
        var code = Code("""{ "root": 1 }""", CSharpGenOptions.Default with { RootTypeName = "Root" });

        Assert.Contains("RootValue", code, StringComparison.Ordinal);
        Assert.True(RoslynHarness.Compile(code).Succeeded, RoslynHarness.Compile(code).ErrorText);
    }

    [Fact]
    public void Colliding_member_names_are_disambiguated_and_reported()
    {
        var result = Generate("""{ "first-name": 1, "first_name": 2 }""");

        Assert.True(RoslynHarness.Compile(result.Code).Succeeded, RoslynHarness.Compile(result.Code).ErrorText);
        Assert.Contains("FirstName2", result.Code, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("collides", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Array_members_name_their_element_type_in_the_singular()
    {
        var code = Code("""{ "addresses": [ { "city": "X" } ], "categories": [ { "n": 1 } ] }""");

        Assert.Contains("public record Address", code, StringComparison.Ordinal);
        Assert.Contains("public record Category", code, StringComparison.Ordinal);
    }

    // FR-J46
    [Fact]
    public void Structurally_identical_objects_emit_one_type()
    {
        var result = Generate("""
            {
              "billing":  { "line1": "a", "city": "b" },
              "shipping": { "line1": "c", "city": "d" }
            }
            """);

        Assert.Equal(2, result.TypeCount);
        Assert.Contains("public Billing Billing { get; init; }", result.Code, StringComparison.Ordinal);
        Assert.Contains("public Billing Shipping { get; init; }", result.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void Differently_shaped_objects_emit_separate_types()
    {
        var result = Generate("""
            { "a": { "x": 1 }, "b": { "y": 1 } }
            """);

        Assert.Equal(3, result.TypeCount);
    }

    // ---- options --------------------------------------------------------------------

    [Theory]
    [InlineData(CSharpTypeKind.Record, "public record Root")]
    [InlineData(CSharpTypeKind.Class, "public class Root")]
    [InlineData(CSharpTypeKind.ReadonlyRecordStruct, "public readonly record struct Root")]
    public void Type_kind_option_changes_the_declaration(CSharpTypeKind kind, string expected)
    {
        var code = Code("""{ "a": 1 }""", CSharpGenOptions.Default with { TypeKind = kind });
        Assert.Contains(expected, code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MemberStyle.GetSet, "public int A { get; set; }")]
    [InlineData(MemberStyle.GetInit, "public int A { get; init; }")]
    [InlineData(MemberStyle.Required, "public required int A { get; init; }")]
    public void Member_style_option_changes_the_accessors(MemberStyle style, string expected)
    {
        var code = Code("""{ "a": 1 }""", CSharpGenOptions.Default with { MemberStyle = style });
        Assert.Contains(expected, code, StringComparison.Ordinal);
    }

    [Fact]
    public void Required_is_not_applied_to_a_nullable_member()
    {
        var code = Code("""{ "xs": [ {"a": 1}, {} ] }""",
            CSharpGenOptions.Default with { MemberStyle = MemberStyle.Required });

        Assert.Contains("public int? A { get; init; }", code, StringComparison.Ordinal);
        Assert.DoesNotContain("required int?", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AttributeStyle.SystemTextJson, "[JsonPropertyName(\"my-field\")]", "using System.Text.Json.Serialization;")]
    [InlineData(AttributeStyle.NewtonsoftJson, "[JsonProperty(\"my-field\")]", "using Newtonsoft.Json;")]
    public void Attribute_style_option_changes_the_attribute_and_the_using(
        AttributeStyle style, string attribute, string import)
    {
        var code = Code("""{ "my-field": 1 }""", CSharpGenOptions.Default with { AttributeStyle = style });

        Assert.Contains(attribute, code, StringComparison.Ordinal);
        Assert.Contains(import, code, StringComparison.Ordinal);
    }

    [Fact]
    public void No_attributes_emits_no_serializer_import()
    {
        var code = Code("""{ "a": 1 }""", CSharpGenOptions.Default with { AttributeStyle = AttributeStyle.None });

        Assert.DoesNotContain("JsonProperty", code, StringComparison.Ordinal);
        Assert.DoesNotContain("using System.Text.Json", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CollectionKind.List, "public List<int> Xs { get; init; }")]
    [InlineData(CollectionKind.Array, "public int[] Xs { get; init; }")]
    [InlineData(CollectionKind.IReadOnlyList, "public IReadOnlyList<int> Xs { get; init; }")]
    public void Collection_kind_option_changes_the_member_type(CollectionKind kind, string expected)
    {
        var code = Code("""{ "xs": [1,2] }""", CSharpGenOptions.Default with { CollectionKind = kind });
        Assert.Contains(expected, code, StringComparison.Ordinal);
    }

    [Fact]
    public void Nullable_annotations_can_be_turned_off_for_reference_types_only()
    {
        const string json = """{ "xs": [ {"s": "a", "i": 1}, {} ] }""";

        var annotated = Code(json, CSharpGenOptions.Default with { NullableAnnotations = true });
        Assert.Contains("public string? S", annotated, StringComparison.Ordinal);
        Assert.Contains("public int? I", annotated, StringComparison.Ordinal);

        var bare = Code(json, CSharpGenOptions.Default with { NullableAnnotations = false });
        Assert.Contains("public string S", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("public string? S", bare, StringComparison.Ordinal);

        // A nullable value type is a different type, not a hint, so it is always annotated.
        Assert.Contains("public int? I", bare, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NamespaceStyle.FileScoped, "namespace My.Models;")]
    [InlineData(NamespaceStyle.Block, "namespace My.Models")]
    public void Namespace_style_option(NamespaceStyle style, string expected)
    {
        var code = Code("""{ "a": 1 }""",
            CSharpGenOptions.Default with { NamespaceStyle = style, NamespaceName = "My.Models" });

        Assert.Contains(expected, code, StringComparison.Ordinal);
        Assert.True(RoslynHarness.Compile(code).Succeeded, RoslynHarness.Compile(code).ErrorText);
    }

    [Fact]
    public void No_namespace_emits_none()
    {
        var code = Code("""{ "a": 1 }""", CSharpGenOptions.Default with { NamespaceStyle = NamespaceStyle.None });
        Assert.DoesNotContain("namespace", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_types_are_declared_inside_their_parent()
    {
        var code = Code("""{ "owner": { "name": "a" } }""", CSharpGenOptions.Default with { NestTypes = true });

        var rootAt = code.IndexOf("public record Root", StringComparison.Ordinal);
        var ownerAt = code.IndexOf("public record Owner", StringComparison.Ordinal);

        Assert.True(rootAt >= 0 && ownerAt > rootAt);
        Assert.True(RoslynHarness.Compile(code).Succeeded, RoslynHarness.Compile(code).ErrorText);
    }

    // ---- edges ----------------------------------------------------------------------

    [Fact]
    public void Empty_input_generates_nothing_rather_than_failing()
    {
        var result = Generate("");
        Assert.Equal(string.Empty, result.Code);
        Assert.Equal(0, result.TypeCount);
    }

    [Fact]
    public void A_scalar_root_generates_nothing_and_says_why()
    {
        var result = Generate("42");
        Assert.Empty(result.Code);
        Assert.Contains(result.Warnings, w => w.Contains("no object", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_array_root_generates_the_element_type_and_says_how_to_use_it()
    {
        var result = Generate("""[ { "a": 1 }, { "a": 2 } ]""");

        Assert.Contains("public record Root", result.Code, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("List<Root>", StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_json_fails_with_a_position()
    {
        var result = CSharpFromJson.Generate("{\n \"a\": }");
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error!.Line);
    }

    [Fact]
    public void An_empty_object_generates_an_empty_type()
    {
        var code = Code("{}");
        Assert.Contains("public record Root", code, StringComparison.Ordinal);
        Assert.True(RoslynHarness.Compile(code).Succeeded);
    }

    // ---- FR-J47: the acceptance test ------------------------------------------------

    /// <summary>
    /// The bar the generator is actually held to: compile the generated source, deserialise
    /// the original sample into it, serialise it back, and require the result to be
    /// semantically the same document. "It generated code" is not the test.
    /// </summary>
    private static void AssertCompilesAndRoundTrips(string json, CSharpGenOptions? options = null)
    {
        var opts = (options ?? CSharpGenOptions.Default) with
        {
            // Date detection is off here because System.Text.Json re-emits a DateTimeOffset in
            // its own canonical form ("+00:00" rather than "Z"), which is the same instant but
            // not the same text. Detection has its own dedicated test above.
            DetectDateGuidUri = false,
            NamespaceStyle = NamespaceStyle.None,
        };

        var generated = Generate(json, opts);
        Assert.NotEmpty(generated.Code);

        var compiled = RoslynHarness.Compile(generated.Code);
        Assert.True(compiled.Succeeded,
            $"generated code did not compile:\n{compiled.ErrorText}\n\n{RoslynHarness.Numbered(generated.Code)}");

        var rootType = compiled.Assembly!.GetType(opts.RootTypeName, throwOnError: false);
        Assert.NotNull(rootType);

        var serializerOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var parsed = JsonReader.Parse(json);
        Assert.True(parsed.IsSuccess, parsed.ErrorMessage);

        // An array root deserialises as a list of the element type.
        var isArrayRoot = parsed.Value!.Root is JsonArray;
        var targetType = isArrayRoot ? typeof(List<>).MakeGenericType(rootType!) : rootType!;

        var instance = JsonSerializer.Deserialize(json, targetType, serializerOptions);
        Assert.NotNull(instance);

        var roundTripped = JsonSerializer.Serialize(instance, targetType, serializerOptions);

        var diff = JsonDiffer.Compare(json, roundTripped, new JsonDiffOptions
        {
            // A member absent from the sample comes back as an unset nullable, which serialises
            // to nothing at all — the same document, said once.
            NullEqualsMissing = true,
        });

        Assert.True(diff.IsSuccess, diff.ErrorMessage);
        Assert.True(diff.Value!.AreEqual,
            $"round trip changed the document: {diff.Value.Summary}\n{diff.Value.JsonPatch}\n\n{roundTripped}\n\n{generated.Code}");
    }

    [Theory]
    [InlineData("""{ "a": 1, "b": "text", "c": true, "d": 1.5 }""")]
    [InlineData("""{ "nested": { "deep": { "deeper": 1 } } }""")]
    [InlineData("""{ "xs": [1, 2, 3] }""")]
    [InlineData("""{ "xs": [] }""")]
    [InlineData("""{ "objects": [ { "a": 1 }, { "a": 2 } ] }""")]
    [InlineData("""{ "mixed": [ { "a": 1 }, { "a": 2, "b": "x" } ] }""")]
    [InlineData("""{ "matrix": [ [1, 2], [3, 4] ] }""")]
    [InlineData("""{ "first-name": "Ada", "last_name": "Lovelace", "$id": 7 }""")]
    [InlineData("""{ "class": "a", "namespace": "b", "int": 1 }""")]
    [InlineData("""{ "big": 123456789012345, "small": 0.001 }""")]
    [InlineData("""[ { "a": 1 }, { "a": 2, "b": 3 } ]""")]
    [InlineData("""{ "billing": {"line1":"a"}, "shipping": {"line1":"b"} }""")]
    [InlineData("""{ "unicode": "héllo 🎉", "escaped": "line\nbreak" }""")]
    public void Generated_code_compiles_and_round_trips_the_sample(string json)
    {
        AssertCompilesAndRoundTrips(json);
    }

    [Theory]
    [InlineData(CSharpTypeKind.Record)]
    [InlineData(CSharpTypeKind.Class)]
    [InlineData(CSharpTypeKind.ReadonlyRecordStruct)]
    public void Every_type_kind_round_trips(CSharpTypeKind kind)
    {
        AssertCompilesAndRoundTrips(
            """{ "id": 1, "name": "x", "tags": ["a","b"], "owner": { "email": "e" } }""",
            CSharpGenOptions.Default with { TypeKind = kind });
    }

    [Theory]
    [InlineData(MemberStyle.GetSet)]
    [InlineData(MemberStyle.GetInit)]
    [InlineData(MemberStyle.Required)]
    public void Every_member_style_round_trips(MemberStyle style)
    {
        AssertCompilesAndRoundTrips(
            """{ "id": 1, "name": "x", "owner": { "email": "e" } }""",
            CSharpGenOptions.Default with { MemberStyle = style });
    }

    [Theory]
    [InlineData(CollectionKind.List)]
    [InlineData(CollectionKind.Array)]
    [InlineData(CollectionKind.IReadOnlyList)]
    public void Every_collection_kind_round_trips(CollectionKind kind)
    {
        AssertCompilesAndRoundTrips(
            """{ "xs": [1,2,3], "objects": [ {"a": 1} ] }""",
            CSharpGenOptions.Default with { CollectionKind = kind });
    }

    [Fact]
    public void A_realistic_payload_compiles_and_round_trips()
    {
        AssertCompilesAndRoundTrips("""
            {
              "id": "c8f1-order",
              "version": 4,
              "active": true,
              "total": 1299.95,
              "tags": ["priority", "gift"],
              "customer": {
                "name": "Ada Lovelace",
                "email": "ada@example.com",
                "address": { "line1": "12 Analytical Way", "city": "London", "postcode": "E1 6AN" }
              },
              "items": [
                { "sku": "A1", "qty": 2, "price": 9.99, "note": "gift wrap" },
                { "sku": "B2", "qty": 1, "price": 1279.97 }
              ],
              "metadata": { "source": "web", "campaign": null }
            }
            """);
    }

    [Fact]
    public void Nested_type_layout_round_trips_too()
    {
        AssertCompilesAndRoundTrips(
            """{ "owner": { "name": "a", "address": { "city": "b" } } }""",
            CSharpGenOptions.Default with { NestTypes = true });
    }
}
