using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using DevTools.Core;
using DevTools.Core.Json;
using DevTools.Http.Capture;
using Microsoft.UI.Dispatching;

namespace DevTools.App.ViewModels.Tools;

/// <summary>One captured call, dressed for the list.</summary>
public sealed record CaptureRow(CapturedExchange Exchange)
{
    public int Index => Exchange.Index;

    public string Time => Exchange.StartedAt.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);

    public string Method => Exchange.Method;

    public string Host => Exchange.Host;

    public string PathAndQuery => Exchange.PathAndQuery;

    public string Status => Exchange.StatusText;

    public string Size => Exchange.SizeText;

    public string Took => SegmentRow.Format(Exchange.Duration);

    public string Note => Exchange.OutcomeNote;

    public bool HasNote => Note.Length > 0;

    /// <summary>A row we could not read through is drawn dimmed, not hidden.</summary>
    public double RowOpacity => Exchange.Outcome == CaptureOutcome.Complete ? 1.0 : 0.72;

    public bool IsFailed => Exchange.Outcome == CaptureOutcome.Failed || Exchange.StatusCode >= 400;

    public string ProcessLabel => Exchange.ProcessId > 0
        ? $"{Exchange.ProcessName} ({Exchange.ProcessId})"
        : "unknown process";

    public string AutomationName =>
        $"{Method} {Host}{PathAndQuery}, {Status}, {Size}, {Took}. {Note}";
}

/// <summary>Which half of a captured exchange the detail pane is showing.</summary>
public enum ExchangeView
{
    Request,
    Response,
    Timings,
}

/// <summary>
/// API Profiler (FR-A01…FR-A09): watches the HTTP calls an application makes.
/// </summary>
/// <remarks>
/// The tool used to measure a URL you typed. It now listens instead, because the question
/// people actually arrive with is "what is this app calling, and why is it slow", and that
/// cannot be answered by a URL you already knew about.
///
/// The constraint that shapes the whole screen: a Windows proxy setting is per user, not per
/// process. Everything on the machine routes through the capture proxy while it runs, so the
/// process dropdown is a <em>filter</em> over what arrives, not a restriction on what is
/// captured. The UI says so rather than implying otherwise.
/// </remarks>
public sealed partial class ApiProfilerViewModel : JobToolViewModelBase
{
    private readonly ICaptureConfigService _config;

    /// <summary>
    /// The proxy raises its event from a thread-pool thread, so every list update has to hop
    /// back. Captured at construction, which happens on the UI thread when the page resolves it.
    /// </summary>
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    private readonly CaptureSession _session;

    private readonly List<CapturedExchange> _all = [];

    public ApiProfilerViewModel(
        ToolServices services,
        CaptureSession session,
        ICaptureConfigService config)
        : base(services)
    {
        _session = session;
        _config = config;

        _session.Captured += OnCaptured;
    }

    public override string ToolId => "api-profiler";

    public override string RunLabel => "Start listening";

    // ---------------------------------------------------------------- processes

    public ObservableCollection<ProcessEntry> Processes { get; } = [];

    [ObservableProperty]
    public partial ProcessEntry? SelectedProcess { get; set; }

    /// <summary>
    /// Whether the list is narrowed to the chosen process. On by default, because the reason
    /// you picked one was to stop looking at everything else.
    /// </summary>
    [ObservableProperty]
    public partial bool OnlySelectedProcess { get; set; } = true;

    partial void OnSelectedProcessChanged(ProcessEntry? value) => ApplyFilter();

    partial void OnOnlySelectedProcessChanged(bool value) => ApplyFilter();

    [RelayCommand]
    private void RefreshProcesses()
    {
        var chosen = SelectedProcess?.Id;

        Processes.Clear();

        foreach (var process in ProcessResolver.ListProcesses())
        {
            Processes.Add(process);
        }

        SelectedProcess = Processes.FirstOrDefault(p => p.Id == chosen) ?? Processes.FirstOrDefault();
    }

    // ---------------------------------------------------------------- capture state

    [ObservableProperty]
    public partial bool IsListening { get; set; }

    [ObservableProperty]
    public partial int ProxyPort { get; set; }

    [ObservableProperty]
    public partial bool IsCertificateTrusted { get; set; }

    public bool NeedsCertificate => !IsCertificateTrusted;

    partial void OnIsCertificateTrustedChanged(bool value) => OnPropertyChanged(nameof(NeedsCertificate));

    /// <summary>
    /// Whether DevTools is routing traffic itself. False inside its MSIX package, which cannot
    /// change the machine's proxy setting — the proxy still captures whatever is pointed at it.
    /// </summary>
    [ObservableProperty]
    public partial bool RoutesAutomatically { get; set; }

    public bool NeedsManualProxy => IsListening && !RoutesAutomatically;

    public string ProxyAddress => $"127.0.0.1:{ProxyPort}";

    /// <summary>
    /// The one-line state under the capture bar, in the words of what it actually changed.
    /// </summary>
    /// <remarks>
    /// Three different things can be true, and saying the wrong one is worse than saying nothing:
    /// not listening, listening with the machine pointed at us, or listening and waiting for an
    /// app to be pointed at us by hand.
    /// </remarks>
    public string ProxyNotice => (IsListening, RoutesAutomatically) switch
    {
        (false, _) =>
            $"Not listening. Starting puts a capture proxy on {ProxyAddress}.",

        (true, true) =>
            $"Capture proxy on {ProxyAddress}. The Windows proxy setting points at it while you listen " +
            "and is put back when you stop. That setting is per user, so other programs route through " +
            "it too — calls are attributed to a process from the TCP table, and Only this process " +
            "filters the list.",

        (true, false) =>
            $"Capture proxy on {ProxyAddress}. DevTools runs in an MSIX package, which cannot change " +
            "the Windows proxy setting — a packaged app's registry writes go to its own private copy. " +
            $"Point the app you want to watch at {ProxyAddress} yourself: in its own proxy settings, or " +
            $"by starting it with HTTP_PROXY and HTTPS_PROXY set to http://{ProxyAddress}.",
    };

    partial void OnIsListeningChanged(bool value)
    {
        RaiseProxyNotice();
        OnPropertyChanged(nameof(StartLabel));
    }

    partial void OnRoutesAutomaticallyChanged(bool value) => RaiseProxyNotice();

    partial void OnProxyPortChanged(int value) => RaiseProxyNotice();

    private void RaiseProxyNotice()
    {
        OnPropertyChanged(nameof(ProxyNotice));
        OnPropertyChanged(nameof(ProxyAddress));
        OnPropertyChanged(nameof(NeedsManualProxy));
    }

    [RelayCommand]
    private void CopyProxyAddress()
    {
        Services.Clipboard.SetText(ProxyAddress);
        SetSuccess($"Copied {ProxyAddress}.");
    }

    public string StartLabel => IsListening ? "Stop listening" : "Start listening";

    // ---------------------------------------------------------------- the list

    public ObservableCollection<CaptureRow> Rows { get; } = [];

    [ObservableProperty]
    public partial CaptureRow? SelectedRow { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    partial void OnFilterChanged(string value) => ApplyFilter();

    [ObservableProperty]
    public partial string CountsText { get; set; } = "Nothing captured yet";

    partial void OnSelectedRowChanged(CaptureRow? value) => RefreshDetail();

    // ---------------------------------------------------------------- the detail pane

    [ObservableProperty]
    public partial ExchangeView DetailView { get; set; } = ExchangeView.Request;

    public int DetailViewIndex
    {
        get => (int)DetailView;
        set => DetailView = (ExchangeView)Math.Clamp(value, 0, 2);
    }

    /// <summary>
    /// The three views are gated on there being a selection as well as on which tab is active,
    /// so an empty detail pane shows its own message rather than an empty body editor.
    /// </summary>
    public bool IsRequestView => HasSelection && DetailView == ExchangeView.Request;

    public bool IsResponseView => HasSelection && DetailView == ExchangeView.Response;

    public bool IsTimingsView => HasSelection && DetailView == ExchangeView.Timings;

    partial void OnDetailViewChanged(ExchangeView value)
    {
        OnPropertyChanged(nameof(IsRequestView));
        OnPropertyChanged(nameof(IsResponseView));
        OnPropertyChanged(nameof(IsTimingsView));
        OnPropertyChanged(nameof(DetailViewIndex));
    }

    public ObservableCollection<CapturedHeader> DetailRequestHeaders { get; } = [];

    public ObservableCollection<CapturedHeader> DetailResponseHeaders { get; } = [];

    public ObservableCollection<SegmentRow> DetailTimings { get; } = [];

    [ObservableProperty]
    public partial string DetailRequestBody { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailResponseBody { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Core.Text.SyntaxLanguage DetailResponseSyntax { get; set; } = Core.Text.SyntaxLanguage.None;

    [ObservableProperty]
    public partial string DetailTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailSubtitle { get; set; } = string.Empty;

    public bool HasSelection => SelectedRow is not null;

    // ---------------------------------------------------------------- lifecycle

    protected override async Task OnActivatedAsync()
    {
        await base.OnActivatedAsync();

        await _config.LoadAsync();

        ProxyPort = _config.Current.Port;
        IsCertificateTrusted = _session.LoadCertificate(_config.Current.CertificateThumbprint) && _session.CanDecryptTls;
        IsListening = _session.State == DevTools.Http.Capture.CaptureState.Running;

        if (Processes.Count == 0)
        {
            RefreshProcesses();
        }
    }

    // ---------------------------------------------------------------- commands

    /// <summary>Run is Start / Stop, so the shared Ctrl+Enter accelerator does the obvious thing.</summary>
    protected override async Task RunJobAsync(CancellationToken token)
    {
        if (IsListening)
        {
            await StopListeningAsync();
        }
        else
        {
            await StartListeningAsync();
        }
    }

    [RelayCommand]
    private async Task StartListeningAsync()
    {
        if (IsListening)
        {
            return;
        }

        // Written down before anything changes, so a crash cannot leave the machine pointing at
        // a port that is no longer listening.
        await _config.ArmRestoreAsync(SystemProxy.Capture());

        var started = _session.Start(new CaptureSettings
        {
            Port = ProxyPort,
            CertificateThumbprint = _config.Current.CertificateThumbprint,
        });

        if (!started.IsSuccess)
        {
            await _config.DisarmRestoreAsync();
            SetError(started.Error!);
            return;
        }

        ProxyPort = started.Value;
        RoutesAutomatically = _session.RoutesAutomatically;
        IsListening = true;
        IsCertificateTrusted = _session.CanDecryptTls;

        await _config.RememberAsync(ProxyPort, _session.CertificateThumbprint);

        // Two independent facts, and the one that decides whether anything will be captured at
        // all goes first.
        var routing = RoutesAutomatically
            ? "Listening."
            : $"Listening on {ProxyAddress}, but nothing is routed there yet — point the app at it.";

        var tls = IsCertificateTrusted
            ? " https bodies are readable because the DevTools root certificate is trusted."
            : " https calls appear as tunnels — host, size and timing but no body — until the certificate is installed.";

        SetInfo(routing + tls);
    }

    [RelayCommand]
    private async Task StopListeningAsync()
    {
        if (!IsListening)
        {
            return;
        }

        var stopped = _session.Stop();
        IsListening = false;

        await _config.DisarmRestoreAsync();

        if (!stopped.IsSuccess)
        {
            SetError(stopped.Error!);
            return;
        }

        SetInfo("Stopped. The Windows proxy setting is back as it was.");
    }

    /// <summary>
    /// Creates the root certificate and asks Windows to trust it for this user.
    /// </summary>
    /// <remarks>
    /// Its own command, with its own confirmation in the page, because trusting a root is a
    /// standing change to the machine and not something Start should slip in.
    /// </remarks>
    [RelayCommand]
    private async Task InstallCertificateAsync()
    {
        var installed = _session.InstallCertificate();

        if (!installed.IsSuccess)
        {
            SetError(installed.Error!);
            return;
        }

        IsCertificateTrusted = _session.CanDecryptTls;
        await _config.RememberAsync(ProxyPort, installed.Value);

        SetSuccess(IsListening
            ? "The certificate is trusted. Restart listening for it to take effect on new connections."
            : "The certificate is trusted. https bodies will be readable from the next capture.");
    }

    [RelayCommand]
    private async Task RemoveCertificateAsync()
    {
        var removed = _session.RemoveCertificate();
        IsCertificateTrusted = false;

        await _config.RememberAsync(ProxyPort, null);

        if (!removed.IsSuccess)
        {
            SetError(removed.Error!);
            return;
        }

        SetInfo("The certificate was removed from your trusted roots.");
    }

    [RelayCommand]
    private void ClearCaptures()
    {
        _all.Clear();
        Rows.Clear();
        SelectedRow = null;
        UpdateCounts();
    }

    /// <summary>Hands the selected call to the API Builder, ready to send again.</summary>
    [RelayCommand]
    private void SendToBuilder()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        Services.Handoff.Send("api-builder", new ToolPayload.Text(ToCurl(row.Exchange)));
        SetInfo("Sent to the API Builder as a cURL command.");
    }

    [RelayCommand]
    private void CopyAsCurl()
    {
        if (SelectedRow is { } row)
        {
            Services.Clipboard.SetText(ToCurl(row.Exchange));
            SetSuccess("Copied as a cURL command.");
        }
    }

    // ---------------------------------------------------------------- capture plumbing

    private void OnCaptured(object? sender, CapturedExchange exchange)
    {
        // The proxy raises this from a thread-pool thread, so the hop to the UI thread is the
        // whole reason this method exists separately from the one that updates the list.
        _dispatcher.TryEnqueue(() =>
        {
            _all.Add(exchange);

            if (Matches(exchange))
            {
                Rows.Add(new CaptureRow(exchange));
            }

            UpdateCounts();
        });
    }

    private bool Matches(CapturedExchange exchange)
    {
        if (OnlySelectedProcess && SelectedProcess is { } process && exchange.ProcessId != process.Id)
        {
            return false;
        }

        var filter = Filter.Trim();

        if (filter.Length == 0)
        {
            return true;
        }

        return exchange.Host.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || exchange.PathAndQuery.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || exchange.Method.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || exchange.StatusText.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilter()
    {
        var selected = SelectedRow?.Index;

        Rows.Clear();

        foreach (var exchange in _all.Where(Matches))
        {
            Rows.Add(new CaptureRow(exchange));
        }

        SelectedRow = Rows.FirstOrDefault(r => r.Index == selected);
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        if (_all.Count == 0)
        {
            CountsText = "Nothing captured yet";
            return;
        }

        var failed = _all.Count(e => e.Outcome == CaptureOutcome.Failed || e.StatusCode >= 400);
        var bytes = _all.Sum(e => e.TotalBytes);

        // Bodies are kept whole, so the total is worth showing: it is the cost of that choice.
        CountsText = Rows.Count == _all.Count
            ? $"{_all.Count:N0} calls · {failed:N0} failed · {Limits.Describe(bytes)} held"
            : $"{Rows.Count:N0} of {_all.Count:N0} calls · {failed:N0} failed · {Limits.Describe(bytes)} held";
    }

    private void RefreshDetail()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsRequestView));
        OnPropertyChanged(nameof(IsResponseView));
        OnPropertyChanged(nameof(IsTimingsView));

        DetailRequestHeaders.Clear();
        DetailResponseHeaders.Clear();
        DetailTimings.Clear();

        if (SelectedRow?.Exchange is not { } exchange)
        {
            DetailTitle = string.Empty;
            DetailSubtitle = string.Empty;
            DetailRequestBody = string.Empty;
            DetailResponseBody = string.Empty;
            return;
        }

        DetailTitle = $"{exchange.Method} {(exchange.PathAndQuery.Length > 0 ? exchange.PathAndQuery : exchange.Host)}";
        DetailSubtitle =
            $"{exchange.Host} · {SelectedRow.ProcessLabel} · " +
            exchange.StartedAt.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.CurrentCulture);

        foreach (var header in exchange.RequestHeaders)
        {
            DetailRequestHeaders.Add(Redact(header));
        }

        foreach (var header in exchange.ResponseHeaders)
        {
            DetailResponseHeaders.Add(header);
        }

        DetailRequestBody = Decode(exchange.RequestBody, exchange.ResponseMediaType);
        DetailResponseBody = Decode(exchange.ResponseBody, exchange.ResponseMediaType);

        DetailResponseSyntax = (exchange.ResponseMediaType ?? string.Empty) switch
        {
            var media when media.Contains("json", StringComparison.OrdinalIgnoreCase) => Core.Text.SyntaxLanguage.Json,
            var media when media.Contains("xml", StringComparison.OrdinalIgnoreCase) => Core.Text.SyntaxLanguage.Xml,
            var media when media.Contains("html", StringComparison.OrdinalIgnoreCase) => Core.Text.SyntaxLanguage.Xml,
            _ => Core.Text.SyntaxLanguage.None,
        };

        DetailTimings.Add(new SegmentRow("Total", exchange.Duration));
    }

    /// <summary>
    /// Hides the value of a credential header.
    /// </summary>
    /// <remarks>
    /// A capture window is the kind of thing people screenshot into a bug report. The header is
    /// still listed, because knowing an Authorization header was sent is often the point; only
    /// the value is covered, and the detail pane says it is hidden rather than pretending the
    /// header was absent.
    /// </remarks>
    private static CapturedHeader Redact(CapturedHeader header)
    {
        var sensitive = header.Name is "Authorization" or "Proxy-Authorization" or "Cookie"
            || header.Name.Contains("api-key", StringComparison.OrdinalIgnoreCase)
            || header.Name.Contains("token", StringComparison.OrdinalIgnoreCase);

        if (!sensitive || header.Value.Length == 0)
        {
            return header;
        }

        // The scheme is kept: "Bearer" versus "Basic" is diagnostic, the token is not.
        var space = header.Value.IndexOf(' ', StringComparison.Ordinal);
        var scheme = space > 0 ? header.Value[..(space + 1)] : string.Empty;

        return header with { Value = $"{scheme}•••••••• hidden" };
    }

    private static string Decode(byte[] body, string? mediaType)
    {
        if (body.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            var text = new UTF8Encoding(false, true).GetString(body);

            return (mediaType ?? string.Empty).Contains("json", StringComparison.OrdinalIgnoreCase)
                ? JsonFormatter.PrettyOrOriginal(text)
                : text;
        }
        catch (DecoderFallbackException)
        {
            // Not text at all. Saying so beats printing mojibake.
            return $"[{Limits.Describe(body.LongLength)} of binary content]";
        }
    }

    private static string ToCurl(CapturedExchange exchange)
    {
        var builder = new StringBuilder();
        builder.Append("curl -X ").Append(exchange.Method).Append(" \"").Append(exchange.Url).Append('"');

        foreach (var header in exchange.RequestHeaders)
        {
            if (string.Equals(header.Name, "Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.Append(" \\\n  -H \"").Append(header.Name).Append(": ").Append(header.Value).Append('"');
        }

        if (exchange.RequestBody.Length > 0)
        {
            var body = Encoding.UTF8.GetString(exchange.RequestBody).Replace("\"", "\\\"", StringComparison.Ordinal);
            builder.Append(" \\\n  --data \"").Append(body).Append('"');
        }

        return builder.ToString();
    }

    // ---------------------------------------------------------------- state

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("onlyProcess", OnlySelectedProcess);
        state.Set("filter", Filter);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        OnlySelectedProcess = state.GetBool("onlyProcess", true);
        Filter = state.GetString("filter");
    }

    protected override void ResetOptions()
    {
        Filter = string.Empty;
        OnlySelectedProcess = true;
        ClearCaptures();
    }
}
