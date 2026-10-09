using CommunityToolkit.Mvvm.ComponentModel;
using DevTools.App.Services;
using Markdig;

namespace DevTools.App.ViewModels.Tools;

/// <summary>Markdown Preview.</summary>
/// <remarks>
/// Markdig (BSD-2-Clause) renders CommonMark plus the GitHub extensions — tables, task lists,
/// strikethrough, autolinks, footnotes — to HTML, which the page shows in a WebView2 that is
/// not allowed to run script or fetch anything from the network.
/// </remarks>
public sealed partial class MarkdownPreviewViewModel : TextToolViewModelBase
{
    // Built once: a pipeline is immutable and thread-safe, and building it is not free.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .UseYamlFrontMatter()
        .Build();

    public MarkdownPreviewViewModel(ToolServices services)
        : base(services)
    {
        Html = string.Empty;
        StatsText = string.Empty;
    }

    public override string ToolId => "markdown-preview";

    protected override string SuggestedFileName => "document.html";

    protected override string[] OutputExtensions => [".html"];

    protected override string[] InputExtensions => [".md", ".markdown", ".mdown", ".txt"];

    /// <summary>The rendered body, without the page around it.</summary>
    [ObservableProperty]
    public partial string Html { get; set; }

    /// <summary>0 = follow the app, 1 = light, 2 = dark.</summary>
    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    public partial string StatsText { get; set; }

    partial void OnThemeIndexChanged(int value) => PersistState();

    protected override async Task RunCoreAsync(CancellationToken token)
    {
        var input = Input;
        var html = await ComputeAsync(() => Markdown.ToHtml(input ?? string.Empty, Pipeline), token);

        Html = html;
        Output = html;

        var words = Core.Text.TextAnalysis.Analyze(input).Words;
        StatsText = string.IsNullOrWhiteSpace(input)
            ? string.Empty
            : $"{words:N0} words · {Core.Text.TextAnalysis.DescribeDuration(TimeSpan.FromMinutes(words / 238.0))} read";

        ClearMessage();
    }

    /// <summary>Saves a complete, self-contained HTML page — the same one the preview shows, in light.</summary>
    public override async Task SaveOutputAsync()
    {
        if (string.IsNullOrWhiteSpace(Html))
        {
            SetInfo("There is nothing to save yet.");
            return;
        }

        var path = await Services.Files.SaveTextFileAsync(SuggestedFileName, Document(Html, dark: false, forPreview: false), ".html");
        if (path is not null)
        {
            SetSuccess($"Saved to {path}.");
        }
    }

    /// <summary>
    /// Wraps the rendered body in a page with a GitHub-like reading style.
    /// </summary>
    /// <remarks>
    /// For the preview the page carries a Content-Security-Policy that allows nothing but its
    /// own inline style and inline images, so even raw HTML in the Markdown cannot run script
    /// or make the preview reach the network.
    /// </remarks>
    public static string Document(string body, bool dark, bool forPreview)
    {
        var csp = forPreview
            ? "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:;\">"
            : string.Empty;

        var (fg, bg, muted, border, code, link, quote) = dark
            ? ("#e6edf3", "#0d1117", "#8d96a0", "#30363d", "#161b22", "#4493f8", "#9198a1")
            : ("#1f2328", "#ffffff", "#59636e", "#d1d9e0", "#f6f8fa", "#0969da", "#59636e");

        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8">{{csp}}
            <meta name="color-scheme" content="{{(dark ? "dark" : "light")}}">
            <style>
            html { background: {{bg}}; }
            body { color: {{fg}}; background: {{bg}}; font: 15px/1.6 "Segoe UI Variable Text", "Segoe UI", sans-serif; max-width: 900px; margin: 0 auto; padding: 24px 32px 48px; word-wrap: break-word; }
            h1, h2, h3, h4, h5, h6 { margin: 24px 0 12px; font-weight: 600; line-height: 1.25; }
            h1 { font-size: 2em; padding-bottom: .3em; border-bottom: 1px solid {{border}}; }
            h2 { font-size: 1.5em; padding-bottom: .3em; border-bottom: 1px solid {{border}}; }
            h3 { font-size: 1.25em; } h4 { font-size: 1em; } h5 { font-size: .875em; } h6 { font-size: .85em; color: {{muted}}; }
            p, ul, ol, blockquote, table, pre, dl { margin: 0 0 16px; }
            a { color: {{link}}; text-decoration: none; } a:hover { text-decoration: underline; }
            code, pre, kbd { font-family: "Cascadia Mono", Consolas, monospace; font-size: 85%; }
            code { background: {{code}}; padding: .2em .4em; border-radius: 6px; }
            pre { background: {{code}}; padding: 16px; border-radius: 6px; overflow: auto; line-height: 1.45; }
            pre code { background: none; padding: 0; font-size: 100%; }
            blockquote { color: {{quote}}; border-left: .25em solid {{border}}; padding: 0 1em; margin-left: 0; }
            table { border-collapse: collapse; display: block; overflow: auto; }
            th, td { border: 1px solid {{border}}; padding: 6px 13px; }
            th { font-weight: 600; } tr:nth-child(2n) { background: {{code}}; }
            hr { border: 0; height: .25em; background: {{border}}; margin: 24px 0; }
            img { max-width: 100%; }
            ul.contains-task-list { list-style: none; padding-left: 1.2em; }
            .task-list-item input { margin: 0 .35em 0 -1.2em; vertical-align: middle; }
            kbd { border: 1px solid {{border}}; border-radius: 6px; padding: 2px 5px; }
            </style></head><body>
            {{body}}
            </body></html>
            """;
    }

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("theme", ThemeIndex);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        ThemeIndex = Math.Clamp(state.GetInt("theme"), 0, 2);
    }

    protected override void ResetOptions() => ThemeIndex = 0;
}
