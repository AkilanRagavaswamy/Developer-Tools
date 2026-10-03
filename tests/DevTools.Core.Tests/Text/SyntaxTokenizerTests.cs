using DevTools.Core.Text;
using Xunit;

namespace DevTools.Core.Tests.Text;

public sealed class SyntaxTokenizerTests
{
    private static IReadOnlyList<SyntaxToken> Tokens(string? text, SyntaxLanguage language) =>
        SyntaxTokenizer.Tokenize(text, language);

    /// <summary>The kind covering a given character, which is how the UI actually reads the result.</summary>
    private static TokenKind KindAt(string text, SyntaxLanguage language, int index)
    {
        foreach (var token in Tokens(text, language))
        {
            if (index >= token.Start && index < token.Start + token.Length)
            {
                return token.Kind;
            }
        }

        return TokenKind.Plain;
    }

    private static string TextOf(string source, SyntaxToken token) =>
        source.Substring(token.Start, token.Length);

    /// <summary>
    /// The invariant the presenter depends on: spans tile the input exactly, in order, with no
    /// gaps and no overlaps. If this ever fails the output would silently lose characters.
    /// </summary>
    private static void AssertTilesExactly(string text, SyntaxLanguage language)
    {
        var tokens = Tokens(text, language);
        var offset = 0;

        foreach (var token in tokens)
        {
            Assert.Equal(offset, token.Start);
            Assert.True(token.Length > 0, "a zero-length span would produce an empty Run");
            offset += token.Length;
        }

        Assert.Equal(text.Length, offset);
    }

    [Theory]
    [InlineData(SyntaxLanguage.Json)]
    [InlineData(SyntaxLanguage.Xml)]
    [InlineData(SyntaxLanguage.CSharp)]
    public void Empty_input_produces_no_tokens(SyntaxLanguage language)
    {
        Assert.Empty(Tokens(string.Empty, language));
        Assert.Empty(Tokens(null, language));
    }

    [Fact]
    public void No_language_returns_one_plain_span()
    {
        var tokens = Tokens("anything at all", SyntaxLanguage.None);

        var token = Assert.Single(tokens);
        Assert.Equal(TokenKind.Plain, token.Kind);
        Assert.Equal(15, token.Length);
    }

    // ---- JSON --------------------------------------------------------------------------

    [Fact]
    public void Json_distinguishes_a_member_name_from_a_string_value()
    {
        const string json = """{"name":"value"}""";

        Assert.Equal(TokenKind.PropertyName, KindAt(json, SyntaxLanguage.Json, 2));
        Assert.Equal(TokenKind.String, KindAt(json, SyntaxLanguage.Json, 10));
    }

    [Fact]
    public void Json_colours_numbers_keywords_and_punctuation()
    {
        const string json = """{"a":1.5e3,"b":true,"c":null}""";

        Assert.Equal(TokenKind.Number, KindAt(json, SyntaxLanguage.Json, 5));
        Assert.Equal(TokenKind.Keyword, KindAt(json, SyntaxLanguage.Json, 15));
        Assert.Equal(TokenKind.Keyword, KindAt(json, SyntaxLanguage.Json, 24));
        Assert.Equal(TokenKind.Punctuation, KindAt(json, SyntaxLanguage.Json, 0));
    }

    [Fact]
    public void Json_handles_a_negative_number()
    {
        const string json = """{"a":-42}""";
        Assert.Equal(TokenKind.Number, KindAt(json, SyntaxLanguage.Json, 5));
    }

    [Fact]
    public void Json_colours_comments_when_they_appear()
    {
        const string json = "{\n  // a note\n  \"a\": 1\n}";
        Assert.Equal(TokenKind.Comment, KindAt(json, SyntaxLanguage.Json, 6));
    }

    [Fact]
    public void An_escaped_quote_does_not_end_the_string()
    {
        const string json = """{"a":"say \"hi\" now"}""";

        // The whole literal is one span, so the text after the escape is still string-coloured.
        Assert.Equal(TokenKind.String, KindAt(json, SyntaxLanguage.Json, 17));
    }

    // A tokenizer that stops at the first error would leave half the document uncoloured.
    [Theory]
    [InlineData("""{"a": """)]
    [InlineData("""{"unterminated": "abc""")]
    [InlineData("{{{{")]
    [InlineData("]]]]")]
    [InlineData("""{"a": 1,,,}""")]
    public void Malformed_json_still_tiles_the_whole_input(string json)
    {
        AssertTilesExactly(json, SyntaxLanguage.Json);
    }

    // ---- XML and XAML ------------------------------------------------------------------

    [Fact]
    public void Xml_colours_elements_attributes_and_values()
    {
        const string xaml = """<Path Fill="#FF0000" Data="M 0,0" />""";

        Assert.Equal(TokenKind.Punctuation, KindAt(xaml, SyntaxLanguage.Xml, 0));
        Assert.Equal(TokenKind.ElementName, KindAt(xaml, SyntaxLanguage.Xml, 2));
        Assert.Equal(TokenKind.AttributeName, KindAt(xaml, SyntaxLanguage.Xml, 7));
        Assert.Equal(TokenKind.String, KindAt(xaml, SyntaxLanguage.Xml, 13));
    }

    [Fact]
    public void Xml_colours_a_comment_as_one_span()
    {
        const string xml = "<a><!-- note --></a>";

        Assert.Equal(TokenKind.Comment, KindAt(xml, SyntaxLanguage.Xml, 8));
        Assert.Equal(TokenKind.ElementName, KindAt(xml, SyntaxLanguage.Xml, 18));
    }

    [Fact]
    public void Xml_handles_a_closing_tag_and_text_content()
    {
        const string xml = "<a>hello</a>";

        Assert.Equal(TokenKind.Plain, KindAt(xml, SyntaxLanguage.Xml, 4));
        Assert.Equal(TokenKind.ElementName, KindAt(xml, SyntaxLanguage.Xml, 10));
    }

    [Fact]
    public void Xml_handles_a_namespaced_attribute()
    {
        const string xaml = """<Path x:Key="Icon" />""";
        Assert.Equal(TokenKind.AttributeName, KindAt(xaml, SyntaxLanguage.Xml, 7));
    }

    [Theory]
    [InlineData("<unclosed")]
    [InlineData("<a attr=\"unterminated")]
    [InlineData("<!-- never closed")]
    [InlineData("plain text with no markup")]
    [InlineData("<<>>")]
    public void Malformed_xml_still_tiles_the_whole_input(string xml)
    {
        AssertTilesExactly(xml, SyntaxLanguage.Xml);
    }

    // ---- C# -----------------------------------------------------------------------------

    [Fact]
    public void CSharp_colours_keywords_strings_numbers_and_comments()
    {
        const string code = """public record Root { } // note""";

        Assert.Equal(TokenKind.Keyword, KindAt(code, SyntaxLanguage.CSharp, 0));
        Assert.Equal(TokenKind.Keyword, KindAt(code, SyntaxLanguage.CSharp, 7));
        Assert.Equal(TokenKind.Plain, KindAt(code, SyntaxLanguage.CSharp, 14));
        Assert.Equal(TokenKind.Comment, KindAt(code, SyntaxLanguage.CSharp, 25));
    }

    [Fact]
    public void CSharp_colours_an_attribute_name_after_a_bracket()
    {
        const string code = """[JsonPropertyName("id")]""";

        Assert.Equal(TokenKind.ElementName, KindAt(code, SyntaxLanguage.CSharp, 2));
        Assert.Equal(TokenKind.String, KindAt(code, SyntaxLanguage.CSharp, 19));
    }

    [Fact]
    public void CSharp_treats_an_escaped_keyword_as_a_keyword()
    {
        const string code = "public int @class { get; init; }";

        // '@class' is the keyword escaped, not an ordinary identifier.
        Assert.Equal(TokenKind.Keyword, KindAt(code, SyntaxLanguage.CSharp, 11));
    }

    [Fact]
    public void CSharp_colours_a_block_comment()
    {
        const string code = "/* a\n   note */ public";
        Assert.Equal(TokenKind.Comment, KindAt(code, SyntaxLanguage.CSharp, 6));
    }

    [Theory]
    [InlineData("""var s = "unterminated""")]
    [InlineData("/* never closed")]
    [InlineData("@")]
    [InlineData("###")]
    public void Malformed_csharp_still_tiles_the_whole_input(string code)
    {
        AssertTilesExactly(code, SyntaxLanguage.CSharp);
    }

    // ---- shared behaviour -----------------------------------------------------------------

    [Fact]
    public void Adjacent_spans_of_the_same_kind_are_merged()
    {
        // Three consecutive punctuation characters should not become three separate Runs.
        var tokens = Tokens("[[[", SyntaxLanguage.Json);

        var token = Assert.Single(tokens);
        Assert.Equal(TokenKind.Punctuation, token.Kind);
        Assert.Equal(3, token.Length);
    }

    [Fact]
    public void A_very_large_document_is_left_uncoloured_rather_than_tokenised()
    {
        var huge = new string('a', SyntaxTokenizer.MaxColourisedLength + 1);
        var tokens = Tokens(huge, SyntaxLanguage.Json);

        var token = Assert.Single(tokens);
        Assert.Equal(TokenKind.Plain, token.Kind);
        Assert.Equal(huge.Length, token.Length);
    }

    [Fact]
    public void A_realistic_document_tiles_exactly_and_reassembles_to_the_original()
    {
        const string json = """
            {
              "id": "c8f1", "version": 4, "active": false, "notes": null,
              "tags": ["alpha", "beta"],
              "owner": { "name": "Ada \"A\" Lovelace", "score": -1.5e-3 }
            }
            """;

        AssertTilesExactly(json, SyntaxLanguage.Json);

        var rebuilt = string.Concat(Tokens(json, SyntaxLanguage.Json).Select(t => TextOf(json, t)));
        Assert.Equal(json, rebuilt);
    }

    [Fact]
    public void A_realistic_xaml_document_reassembles_to_the_original()
    {
        const string xaml = """
            <Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="24" Height="24">
                <!-- an icon -->
                <Path Fill="#FF4F46E5" Data="M 12,2 L 22,22 L 2,22 Z" />
            </Canvas>
            """;

        AssertTilesExactly(xaml, SyntaxLanguage.Xml);

        var rebuilt = string.Concat(Tokens(xaml, SyntaxLanguage.Xml).Select(t => TextOf(xaml, t)));
        Assert.Equal(xaml, rebuilt);
    }

    [Fact]
    public void Generated_csharp_reassembles_to_the_original()
    {
        const string code = """
            using System.Text.Json.Serialization;

            namespace Generated;

            public record Root
            {
                [JsonPropertyName("id")]
                public string Id { get; init; }

                [JsonPropertyName("count")]
                public int Count { get; init; }
            }
            """;

        AssertTilesExactly(code, SyntaxLanguage.CSharp);

        var rebuilt = string.Concat(Tokens(code, SyntaxLanguage.CSharp).Select(t => TextOf(code, t)));
        Assert.Equal(code, rebuilt);
    }
}
