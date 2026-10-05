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


    private static IReadOnlyList<ToolDescriptor> BuildCatalog() =>
    [
        // ---------- JSON ----------
        new("json-formatter", "JSON Formatter", "Format, minify and validate JSON, and query it with JSONPath",
            ToolCategory.Json, "\uE943", typeof(JsonFormatterPage),
            ["json", "format", "pretty", "beautify", "minify", "validate", "jsonpath", "lint", "sort"]),

        new("json-diff", "JSON Diff Checker", "Compare two JSON documents semantically and emit a JSON Patch",
            ToolCategory.Json, "\uE8AB", typeof(JsonDiffPage),
            ["json", "diff", "compare", "difference", "patch", "rfc6902", "merge", "changes", "semantic"]),

        // "C#" drawn as a mark, because what the tool does is turn JSON into C#: a blank page
        // glyph said nothing about that, and the icon font has no symbol that does.
        new("json-to-csharp", "JSON to C#", "Generate compilable C# models from a JSON sample",
            ToolCategory.Json, "C#", typeof(JsonToCSharpPage),
            ["json", "csharp", "c#", "class", "record", "poco", "dto", "model", "generate", "codegen", "deserialize"])
        {
            GlyphFont = new("Segoe UI Black,Segoe UI Variable Display,Segoe UI"),
        },

        // ---------- Vector ----------
        new("svg-to-xaml", "SVG to XAML", "Convert SVG shapes, paths and gradients to WPF or WinUI XAML",
            ToolCategory.Vector, "\uE91B", typeof(SvgToXamlPage),
            ["svg", "xaml", "wpf", "winui", "vector", "icon", "path", "geometry", "convert", "pathicon"]),

        // ---------- API ----------
        new("api-builder", "API Builder", "Compose, organise and send HTTP requests with environments and auth",
            ToolCategory.Api, "\uE968", typeof(ApiBuilderPage),
            ["api", "http", "rest", "request", "postman", "curl", "openapi", "collection", "environment", "send", "client"]),

        new("api-profiler", "API Profiler", "Watch the HTTP calls an application makes and read the whole exchange",
            ToolCategory.Api, "", typeof(ApiProfilerPage),
            ["api", "profile", "capture", "proxy", "listen", "watch", "traffic", "sniff", "fiddler", "inspect", "timing", "latency"]),
    ];
}
