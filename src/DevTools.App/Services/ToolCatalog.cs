using DevTools.App.Models;
using DevTools.App.Views.Tools;

namespace DevTools.App.Services;

/// <summary>A search hit, carrying the score so the caller can rank without re-scoring.</summary>
public sealed record ToolSearchResult(ToolDescriptor Tool, int Score);

/// <summary>
/// The single registry of every tool in the app. Navigation, the dashboard, the command
/// palette, Smart Detect and <c>forgekitrk:</c> activation all resolve through this one list,
/// so adding a tool is a one-line change here plus its page.
/// </summary>
public sealed class ToolCatalog
{
    private readonly Dictionary<string, ToolDescriptor> _byId;

    public ToolCatalog()
    {
        All = BuildCatalog();
        _byId = All.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        ByCategory = All
            .GroupBy(t => t.Category)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ToolDescriptor>)[.. g]);
    }

    public IReadOnlyList<ToolDescriptor> All { get; }

    public IReadOnlyDictionary<ToolCategory, IReadOnlyList<ToolDescriptor>> ByCategory { get; }

    public ToolDescriptor? ById(string? id) =>
        id is not null && _byId.TryGetValue(id, out var tool) ? tool : null;

    public ToolDescriptor? ByPageType(Type pageType) =>
        All.FirstOrDefault(t => t.PageType == pageType);

    /// <summary>
    /// Ranked search over name, acronym, keywords and description. Higher scores first;
    /// ties break alphabetically so the order is stable between keystrokes.
    /// </summary>
    public IReadOnlyList<ToolSearchResult> Search(string? query, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var q = query.Trim();
        var results = new List<ToolSearchResult>();

        foreach (var tool in All)
        {
            var score = Score(tool, q);
            if (score > 0)
            {
                results.Add(new ToolSearchResult(tool, score));
            }
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Tool.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static int Score(ToolDescriptor tool, string query)
    {
        const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

        if (tool.Id.Equals(query, Ci))
        {
            return 1000;
        }

        if (tool.Name.Equals(query, Ci))
        {
            return 900;
        }

        if (tool.Name.StartsWith(query, Ci))
        {
            return 800;
        }

        if (tool.Acronym.StartsWith(query, Ci) && query.Length >= 2)
        {
            return 750;
        }

        // A hit at a word boundary is far more meaningful than one buried mid-word.
        if (WordStartsWith(tool.Name, query))
        {
            return 700;
        }

        if (tool.Name.Contains(query, Ci))
        {
            return 600;
        }

        foreach (var keyword in tool.Keywords)
        {
            if (keyword.Equals(query, Ci))
            {
                return 550;
            }

            if (keyword.StartsWith(query, Ci))
            {
                return 500;
            }
        }

        if (tool.Id.Contains(query, Ci))
        {
            return 400;
        }

        foreach (var keyword in tool.Keywords)
        {
            if (keyword.Contains(query, Ci))
            {
                return 300;
            }
        }

        if (tool.Description.Contains(query, Ci))
        {
            return 200;
        }

        // Last resort: every query character appears in order in the name (fuzzy).
        return SubsequenceMatch(tool.Name, query) ? 100 : 0;
    }

    private static bool WordStartsWith(string text, string query)
    {
        var atBoundary = true;
        for (var i = 0; i < text.Length; i++)
        {
            if (atBoundary &&
                i + query.Length <= text.Length &&
                text.AsSpan(i, query.Length).Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            atBoundary = !char.IsLetterOrDigit(text[i]);
        }

        return false;
    }

    private static bool SubsequenceMatch(string text, string query)
    {
        var qi = 0;
        foreach (var c in text)
        {
            if (qi < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[qi]))
            {
                qi++;
            }
        }

        return qi == query.Length;
    }


    /// <summary>For tools whose glyph is a few characters of text — "C#", "64" — rather than a symbol.</summary>
    private static readonly Microsoft.UI.Xaml.Media.FontFamily TextGlyphFont = new("Segoe UI Black,Segoe UI Variable Display,Segoe UI");

    private static IReadOnlyList<ToolDescriptor> BuildCatalog() =>
    [
        // ---------- Formatters ----------
        new("json-formatter", "JSON Formatter", "Format, minify and validate JSON, and query it with JSONPath",
            ToolCategory.Formatters, "", typeof(JsonFormatterPage),
            ["json", "format", "pretty", "beautify", "minify", "validate", "jsonpath", "lint", "sort"]),

        new("sql-formatter", "SQL Formatter", "Lay out SQL one clause per line, in ten dialects",
            ToolCategory.Formatters, "", typeof(SqlFormatterPage),
            ["sql", "format", "pretty", "beautify", "query", "tsql", "mysql", "postgres", "postgresql", "oracle", "plsql", "db2", "spark", "redshift"]),

        new("xml-formatter", "XML Formatter", "Indent, minify and validate XML",
            ToolCategory.Formatters, "</>", typeof(XmlFormatterPage),
            ["xml", "format", "pretty", "beautify", "minify", "validate", "indent", "xsd", "soap", "config"])
        {
            GlyphFont = TextGlyphFont,
        },

        // ---------- Validators ----------
        new("json-diff", "JSON Diff Checker", "Compare two JSON documents semantically and emit a JSON Patch",
            ToolCategory.Validators, "", typeof(JsonDiffPage),
            ["json", "diff", "compare", "difference", "patch", "rfc6902", "merge", "changes", "semantic"]),

        new("text-compare", "Text Compare", "Compare two texts line by line, side by side or inline",
            ToolCategory.Validators, "", typeof(TextComparePage),
            ["text", "compare", "diff", "difference", "merge", "changes", "side by side"]),

        new("regex-validator", "Regex Validator", "Test a .NET regular expression against text: matches, groups and replacements",
            ToolCategory.Validators, ".*", typeof(RegexValidatorPage),
            ["regex", "regexp", "regular expression", "pattern", "match", "test", "validate", "replace", "groups", "capture"])
        {
            GlyphFont = TextGlyphFont,
        },

        // ---------- Converters ----------
        // "C#" drawn as a mark, because what the tool does is turn JSON into C#: a blank page
        // glyph said nothing about that, and the icon font has no symbol that does.
        new("json-to-csharp", "JSON to C#", "Generate compilable C# models from a JSON sample",
            ToolCategory.Converters, "C#", typeof(JsonToCSharpPage),
            ["json", "csharp", "c#", "class", "record", "poco", "dto", "model", "generate", "codegen", "deserialize"])
        {
            GlyphFont = TextGlyphFont,
        },

        new("json-to-table", "JSON to Table", "Lay a JSON array out as rows and columns, and copy it as CSV, TSV or Markdown",
            ToolCategory.Converters, "", typeof(JsonToTablePage),
            ["json", "table", "grid", "csv", "tsv", "markdown", "excel", "spreadsheet", "flatten", "rows", "columns"]),

        new("date-converter", "Date & Unix Time", "Convert between Unix timestamps and dates in any time zone",
            ToolCategory.Converters, "", typeof(DateConverterPage),
            ["date", "time", "unix", "epoch", "timestamp", "utc", "iso", "8601", "timezone", "milliseconds", "now", "convert"]),

        // ---------- Encoders & decoders ----------
        new("base64-text", "Base64 Text", "Encode and decode Base64 in any text encoding, URL-safe or standard",
            ToolCategory.Encoders, "64", typeof(Base64TextPage),
            ["base64", "encode", "decode", "b64", "text", "utf8", "url-safe", "base64url"])
        {
            GlyphFont = TextGlyphFont,
        },

        new("base64-image", "Base64 Image", "Turn an image into Base64 or a data URI, and back into an image",
            ToolCategory.Encoders, "", typeof(Base64ImagePage),
            ["base64", "image", "data uri", "datauri", "png", "jpeg", "gif", "webp", "svg", "encode", "decode", "picture"]),

        new("url-encoder", "URL Encoder", "Percent-encode and decode URLs, query values and form data",
            ToolCategory.Encoders, "", typeof(UrlEncoderPage),
            ["url", "uri", "encode", "decode", "percent", "escape", "query", "form", "urlencode"]),

        new("html-encoder", "HTML Encoder", "Escape and unescape HTML entities",
            ToolCategory.Encoders, "&;", typeof(HtmlEncoderPage),
            ["html", "entity", "entities", "encode", "decode", "escape", "unescape", "amp", "xss"])
        {
            GlyphFont = TextGlyphFont,
        },

        // ---------- Generators ----------
        new("uuid-generator", "UUID Generator", "Generate version 1, 4 and 7 UUIDs in bulk",
            ToolCategory.Generators, "", typeof(UuidGeneratorPage),
            ["uuid", "guid", "generate", "random", "v4", "v7", "v1", "id", "identifier", "unique"]),

        new("qr-code", "QR Code Generator", "Make a QR code from text or a URL, and save it as PNG or SVG",
            ToolCategory.Generators, "", typeof(QrCodePage),
            ["qr", "qrcode", "barcode", "generate", "png", "svg", "url", "wifi", "scan"]),

        // ---------- Text ----------
        new("character-counter", "Character Counter", "Count characters, words, lines and bytes, and check them against common limits",
            ToolCategory.Text, "", typeof(CharacterCounterPage),
            ["character", "count", "counter", "words", "length", "letters", "bytes", "tweet", "sms", "limit", "statistics", "frequency"]),

        new("markdown-preview", "Markdown Preview", "Write or open Markdown and see it rendered as you type",
            ToolCategory.Text, "M↓", typeof(MarkdownPreviewPage),
            ["markdown", "md", "preview", "readme", "render", "gfm", "github"])
        {
            GlyphFont = TextGlyphFont,
        },

        new("html-viewer", "HTML Viewer", "Open or write HTML and see the page as you edit it",
            ToolCategory.Text, "", typeof(HtmlViewerPage),
            ["html", "viewer", "preview", "render", "page", "web", "browser", "htm"]),

        // ---------- Vector ----------
        new("svg-to-xaml", "SVG to XAML", "Convert SVG shapes, paths and gradients to WPF or WinUI XAML",
            ToolCategory.Vector, "", typeof(SvgToXamlPage),
            ["svg", "xaml", "wpf", "winui", "vector", "icon", "path", "geometry", "convert", "pathicon"]),

        // ---------- API ----------
        new("api-builder", "API Builder", "Compose, organise and send HTTP requests with environments and auth",
            ToolCategory.Api, "", typeof(ApiBuilderPage),
            ["api", "http", "rest", "request", "postman", "curl", "openapi", "collection", "environment", "send", "client"]),
    ];
}
