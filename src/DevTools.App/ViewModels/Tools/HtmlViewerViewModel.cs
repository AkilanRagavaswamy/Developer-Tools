using CommunityToolkit.Mvvm.ComponentModel;
using DevTools.App.Services;
using DevTools.Core;

namespace DevTools.App.ViewModels.Tools;

/// <summary>HTML Viewer: edit HTML on the left, see the page on the right.</summary>
public sealed partial class HtmlViewerViewModel : TextToolViewModelBase
{
    public HtmlViewerViewModel(ToolServices services)
        : base(services)
    {
        Document = string.Empty;
        StatsText = string.Empty;
    }

    public override string ToolId => "html-viewer";

    protected override string SuggestedFileName => "page.html";

    protected override string[] OutputExtensions => [".html", ".htm"];

    protected override string[] InputExtensions => [".html", ".htm", ".xhtml", ".txt"];

    /// <summary>Lets the page's own inline scripts run. They still cannot load or fetch anything.</summary>
    [ObservableProperty]
    public partial bool AllowScripts { get; set; }

    /// <summary>The page the preview shows: the input, with the safety policy added to its head.</summary>
    [ObservableProperty]
    public partial string Document { get; set; }

    [ObservableProperty]
    public partial string StatsText { get; set; }

    partial void OnAllowScriptsChanged(bool value) => OnOptionChanged();

    protected override Task RunCoreAsync(CancellationToken token)
    {
        var html = Input ?? string.Empty;
        Output = html;
        Document = Prepare(html, AllowScripts);

        StatsText = string.IsNullOrWhiteSpace(html)
            ? string.Empty
            : $"{Limits.Describe(System.Text.Encoding.UTF8.GetByteCount(html))}" +
              (AllowScripts ? " · scripts on" : " · scripts off");

        ClearMessage();

        if (html.Contains("src=\"http", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("href=\"http", StringComparison.OrdinalIgnoreCase) && html.Contains("stylesheet", StringComparison.OrdinalIgnoreCase))
        {
            SetInfo("Images, stylesheets and scripts from the web are not loaded: the preview never goes online. Inline styles and data: images work.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds a Content-Security-Policy and a plain white page to the document's head.
    /// </summary>
    /// <remarks>
    /// The policy is defence in depth — the preview control already refuses every request — and
    /// the white background goes first, so the page's own CSS still overrides it. Without it a
    /// page with no background of its own would show the app's dark surface behind black text.
    /// </remarks>
    internal static string Prepare(string html, bool allowScripts)
    {
        var script = allowScripts ? " script-src 'unsafe-inline';" : string.Empty;
        var head =
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline' data:; " +
            $"img-src data:; font-src data:; media-src data:;{script}\">" +
            "<style>html{background:#fff;color:#000;}</style>";

        var at = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            var close = html.IndexOf('>', at);
            if (close > 0)
            {
                return html.Insert(close + 1, head);
            }
        }

        at = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            var close = html.IndexOf('>', at);
            if (close > 0)
            {
                return html.Insert(close + 1, "<head>" + head + "</head>");
            }
        }

        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" + head + "</head><body>" + html + "</body></html>";
    }

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("scripts", AllowScripts);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        AllowScripts = state.GetBool("scripts");
    }

    protected override void ResetOptions() => AllowScripts = false;
}
