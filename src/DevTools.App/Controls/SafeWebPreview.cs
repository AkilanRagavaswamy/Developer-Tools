using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace DevTools.App.Controls;

/// <summary>
/// A WebView2 for showing a page the user wrote, on a short leash: every request the page makes
/// is refused, so nothing in the document — an image, a stylesheet, a script's fetch — can reach
/// the network, and a link that is clicked opens in the user's own browser instead.
/// </summary>
/// <remarks>
/// Shared by Markdown Preview and HTML Viewer. Script is off unless <see cref="AllowScripts"/>
/// is set, and even then the page cannot load anything; it can only run what it contains.
/// </remarks>
public sealed partial class SafeWebPreview : UserControl
{
    /// <summary>NavigateToString refuses anything larger than about 2 MB.</summary>
    public const int MaxDocumentLength = 1_900_000;

    private readonly WebView2 _view = new();
    private readonly TextBlock _error = new()
    {
        Margin = new Thickness(24),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };

    private bool _ready;
    private bool _navigatingOurselves;
    private string? _pending;

    public SafeWebPreview()
    {
        _error.Style = (Style)Application.Current.Resources["DevToolsCaptionStyle"];
        Content = new Grid { Children = { _view, _error } };
        Loaded += async (_, _) => await InitializeAsync();
    }

    public static readonly DependencyProperty AllowScriptsProperty = DependencyProperty.Register(
        nameof(AllowScripts), typeof(bool), typeof(SafeWebPreview), new PropertyMetadata(false, OnAllowScriptsChanged));

    public bool AllowScripts
    {
        get => (bool)GetValue(AllowScriptsProperty);
        set => SetValue(AllowScriptsProperty, value);
    }

    private static void OnAllowScriptsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var preview = (SafeWebPreview)d;
        if (preview._ready)
        {
            preview._view.CoreWebView2.Settings.IsScriptEnabled = (bool)e.NewValue;
            preview.Reload();
        }
    }

    private string? _current;

    /// <summary>Shows a complete HTML document.</summary>
    public void Show(string document)
    {
        _current = document;

        if (!_ready)
        {
            _pending = document;
            return;
        }

        if (document.Length > MaxDocumentLength)
        {
            ShowError("This document is too large to preview. It can still be copied or saved.");
            return;
        }

        _error.Visibility = Visibility.Collapsed;
        _view.Visibility = Visibility.Visible;

        _navigatingOurselves = true;
        _view.NavigateToString(document);
    }

    private void Reload()
    {
        if (_current is not null)
        {
            Show(_current);
        }
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
        _view.Visibility = Visibility.Collapsed;
    }

    private async Task InitializeAsync()
    {
        if (_ready)
        {
            return;
        }

        try
        {
            await _view.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            // No WebView2 runtime: say so where the preview would be, and keep the editor usable.
            ShowError("The preview needs the Microsoft Edge WebView2 Runtime, which is not installed. " +
                      "The document can still be copied or saved.");
            App.LogError("Web preview", ex);
            return;
        }

        var core = _view.CoreWebView2;
        core.Settings.IsScriptEnabled = AllowScripts;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);

        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternally(e.Uri);
        };

        _view.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
        _ready = true;

        if (_pending is { } pending)
        {
            _pending = null;
            Show(pending);
        }
    }

    /// <summary>
    /// Only the page this control puts there may load. A link the user clicks opens in their
    /// browser instead, which is what clicking a link in a preview should do.
    /// </summary>
    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_navigatingOurselves)
        {
            _navigatingOurselves = false;
            return;
        }

        e.Cancel = true;

        if (e.IsUserInitiated)
        {
            OpenExternally(e.Uri);
        }
    }

    private static void OpenExternally(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var target) &&
            target.Scheme is "http" or "https" or "mailto")
        {
            _ = Windows.System.Launcher.LaunchUriAsync(target);
        }
    }
}
