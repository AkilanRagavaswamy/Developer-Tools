using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using DevTools.Core;
using DevTools.Core.Json;
using DevTools.Http.Execution;
using DevTools.Http.Interop;
using DevTools.Http.Model;
using DevTools.Http.Workspace;

namespace DevTools.App.ViewModels.Tools;

/// <summary>An editable header or query row.</summary>
public sealed partial class EditableRow : ObservableObject
{
    public EditableRow(string name = "", string value = "", bool enabled = true, string? description = null)
    {
        Name = name;
        Value = value;
        IsEnabled = enabled;
        Description = description ?? string.Empty;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>Free text about what the parameter is for. Round-trips through the collection file.</summary>
    [ObservableProperty]
    public partial string Description { get; set; }

    public KeyValueItem ToItem() => new(Name, Value, IsEnabled, string.IsNullOrWhiteSpace(Description) ? null : Description);

    public FormField ToField() => new(Name, Value, IsEnabled);
}

/// <summary>An editable multipart part: a name plus either a value or a file.</summary>
public sealed partial class MultipartRow : ObservableObject
{
    public MultipartRow(string name = "", string? value = null, string? filePath = null, bool enabled = true)
    {
        Name = name;
        Value = value ?? string.Empty;
        FilePath = filePath;
        IsEnabled = enabled;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; }

    [ObservableProperty]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    public bool IsFile => !string.IsNullOrWhiteSpace(FilePath);

    public string FileLabel => IsFile ? Path.GetFileName(FilePath!) : "No file";

    partial void OnFilePathChanged(string? value)
    {
        OnPropertyChanged(nameof(IsFile));
        OnPropertyChanged(nameof(FileLabel));
    }

    public MultipartPart ToPart() => new(Name, IsFile ? null : Value, FilePath, null, IsEnabled);
}

/// <summary>One entry in the collection tree.</summary>
public sealed record TreeEntry(string Id, string Name, string Method, bool IsFolder, int Depth)
{
    public double Indent => Depth * 16.0;

    public string MethodBadge => IsFolder ? string.Empty : Method;
}

/// <summary>How the response body is shown: reformatted, exactly as it arrived, or rendered.</summary>
public enum BodyPresentation
{
    Pretty,
    Raw,
    Preview,
}

/// <summary>Which response tab is showing.</summary>
public enum ResponseView
{
    Body,
    Headers,
    Cookies,
    Timing,
    History,
}

/// <summary>API Builder (FR-A20…FR-A31).</summary>
public sealed partial class ApiBuilderViewModel : JobToolViewModelBase
{
    private readonly HttpExecutor _executor;
    private readonly IWorkspaceService _workspace;
    private readonly ICredentialStore _credentials;

    private RequestDefinition _current = new();
    private bool _loadingRequest;

    public ApiBuilderViewModel(
        ToolServices services,
        HttpExecutor executor,
        IWorkspaceService workspace,
        ICredentialStore credentials)
        : base(services)
    {
        _executor = executor;
        _workspace = workspace;
        _credentials = credentials;

        Method = "GET";
        Url = string.Empty;
        RequestName = "New request";
        BodyText = string.Empty;
        ResponseText = string.Empty;
        StatusLine = string.Empty;
        ResolvedUrlPreview = string.Empty;
        SecretValue = string.Empty;

        _workspace.Changed += (_, _) => UiDispatcher.Run(RebuildTree);

        Headers.CollectionChanged += OnRowsChanged;
        Query.CollectionChanged += OnRowsChanged;
    }

    public override string ToolId => "api-builder";

    /// <summary>
    /// Banners here report one-off events — sent, copied, token fetched — and the response
    /// pane already holds the result, so they clear after five seconds instead of taking a
    /// row from the editor until something replaces them.
    /// </summary>
    protected override TimeSpan? MessageAutoHideDelay => TimeSpan.FromSeconds(5);

    public override string RunLabel => "Send";

    // ---------------------------------------------------------------- request editor

    [ObservableProperty]
    public partial string RequestName { get; set; }

    [ObservableProperty]
    public partial string Method { get; set; }

    [ObservableProperty]
    public partial string Url { get; set; }

    public IReadOnlyList<string> Methods { get; } =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    public ObservableCollection<EditableRow> Headers { get; } = [];

    public ObservableCollection<EditableRow> Query { get; } = [];

    public ObservableCollection<EditableRow> FormFields { get; } = [];

    [ObservableProperty]
    public partial BodyKind BodyKind { get; set; }

    [ObservableProperty]
    public partial string BodyText { get; set; }

    public IReadOnlyList<BodyKind> BodyKinds { get; } =
        [BodyKind.None, BodyKind.Json, BodyKind.Raw, BodyKind.FormUrlEncoded, BodyKind.Multipart, BodyKind.BinaryFile];

    public bool IsTextBody => BodyKind is BodyKind.Json or BodyKind.Raw;

    public bool IsFormBody => BodyKind == BodyKind.FormUrlEncoded;

    public bool IsMultipartBody => BodyKind == BodyKind.Multipart;

    public bool IsBinaryBody => BodyKind == BodyKind.BinaryFile;

    /// <summary>Multipart parts: a name plus either a value or a file (FR-A24).</summary>
    public ObservableCollection<MultipartRow> Parts { get; } = [];

    /// <summary>The file sent as a raw binary body.</summary>
    [ObservableProperty]
    public partial string? BinaryFilePath { get; set; }

    [ObservableProperty]
    public partial string? BinaryContentType { get; set; }

    /// <summary>Combo-box indices. See <see cref="OptionListExtensions"/> for why these exist.</summary>
    public int MethodIndex
    {
        get => Methods.IndexOfValue(Method);
        set => Method = Methods.ValueAt(value, "GET");
    }

    public int BodyKindIndex
    {
        get => BodyKinds.IndexOfValue(BodyKind);
        set => BodyKind = BodyKinds.ValueAt(value, BodyKind.None);
    }

    public int AuthKindIndex
    {
        get => AuthKinds.IndexOfValue(AuthKind);
        set => AuthKind = AuthKinds.ValueAt(value, AuthKind.None);
    }

    public int ApiKeyInIndex
    {
        get => ApiKeyLocations.IndexOfValue(ApiKeyIn);
        set => ApiKeyIn = ApiKeyLocations.ValueAt(value, ApiKeyLocation.Header);
    }

    public int ResponseViewIndex
    {
        get => ResponseViews.IndexOfValue(ResponseView);
        set => ResponseView = ResponseViews.ValueAt(value, ResponseView.Body);
    }

    partial void OnApiKeyInChanged(ApiKeyLocation value)
    {
        OnPropertyChanged(nameof(ApiKeyInIndex));
        MarkDirty();
    }

    partial void OnBodyKindChanged(BodyKind value)
    {
        OnPropertyChanged(nameof(IsTextBody));
        OnPropertyChanged(nameof(IsFormBody));
        OnPropertyChanged(nameof(IsMultipartBody));
        OnPropertyChanged(nameof(IsBinaryBody));
        OnPropertyChanged(nameof(BodyKindIndex));
        RaiseTabCounts();
        MarkDirty();
    }

    partial void OnBodyTextChanged(string value)
    {
        OnPropertyChanged(nameof(BodyValidationText));
        MarkDirty();
    }

    /// <summary>Live JSON validation under the body editor — the Phase 3 engine, reused.</summary>
    public string BodyValidationText
    {
        get
        {
            if (BodyKind != BodyKind.Json || string.IsNullOrWhiteSpace(BodyText))
            {
                return string.Empty;
            }

            var result = JsonFormatter.Format(BodyText, new JsonFormatOptions { Mode = JsonFormatMode.ValidateOnly });
            return result.IsSuccess ? result.Value!.Output : result.ErrorMessage;
        }
    }

    // ---------------------------------------------------------------- auth

    [ObservableProperty]
    public partial AuthKind AuthKind { get; set; }

    [ObservableProperty]
    public partial string? AuthUsername { get; set; }

    [ObservableProperty]
    public partial string? ApiKeyName { get; set; }

    [ObservableProperty]
    public partial ApiKeyLocation ApiKeyIn { get; set; }

    [ObservableProperty]
    public partial string? TokenUrl { get; set; }

    [ObservableProperty]
    public partial string? ClientId { get; set; }

    [ObservableProperty]
    public partial string? Scope { get; set; }

    /// <summary>
    /// The secret as typed. Held only long enough to put it in the credential vault; it is
    /// never written into the workspace file (FR-A30).
    /// </summary>
    [ObservableProperty]
    public partial string SecretValue { get; set; }

    public IReadOnlyList<AuthKind> AuthKinds { get; } =
        [AuthKind.None, AuthKind.Basic, AuthKind.Bearer, AuthKind.ApiKey, AuthKind.OAuth2ClientCredentials];

    public IReadOnlyList<ApiKeyLocation> ApiKeyLocations { get; } = [ApiKeyLocation.Header, ApiKeyLocation.Query];

    public bool IsBasicAuth => AuthKind == AuthKind.Basic;

    public bool IsApiKeyAuth => AuthKind == AuthKind.ApiKey;

    public bool IsOAuthAuth => AuthKind == AuthKind.OAuth2ClientCredentials;

    public bool NeedsSecret => AuthKind != AuthKind.None;

    public string SecretLabel => AuthKind switch
    {
        AuthKind.Basic => "Password",
        AuthKind.Bearer => "Token",
        AuthKind.ApiKey => "Key",
        AuthKind.OAuth2ClientCredentials => "Client secret",
        _ => "Secret",
    };

    partial void OnAuthKindChanged(AuthKind value)
    {
        OnPropertyChanged(nameof(IsBasicAuth));
        OnPropertyChanged(nameof(IsApiKeyAuth));
        OnPropertyChanged(nameof(IsOAuthAuth));
        OnPropertyChanged(nameof(NeedsSecret));
        OnPropertyChanged(nameof(SecretLabel));
        OnPropertyChanged(nameof(AuthKindIndex));
        RaiseTabCounts();
        MarkDirty();
    }

    // ---------------------------------------------------------------- transport

    [ObservableProperty]
    public partial int TimeoutSeconds { get; set; } = 100;

    [ObservableProperty]
    public partial bool FollowRedirects { get; set; } = true;

    [ObservableProperty]
    public partial bool IgnoreCertificateErrors { get; set; }

    // ---------------------------------------------------------------- collections

    public ObservableCollection<TreeEntry> Tree { get; } = [];

    public ObservableCollection<ApiEnvironment> Environments { get; } = [];

    [ObservableProperty]
    public partial ApiEnvironment? ActiveEnvironment { get; set; }

    [ObservableProperty]
    public partial string ResolvedUrlPreview { get; set; }

    partial void OnActiveEnvironmentChanged(ApiEnvironment? value)
    {
        var workspace = _workspace.Current with { ActiveEnvironmentId = value?.Id };
        _workspace.Update(workspace);
        UpdatePreview();
    }


    // ---------------------------------------------------------------- collections panel

    /// <summary>
    /// Whether the collections rail is showing. Collapsed it gives the request editor another
    /// 232 px, which is the difference between reading a JSON body and scrolling it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCollectionsOpen { get; set; } = true;

    [RelayCommand]
    private void ToggleCollections() => IsCollectionsOpen = !IsCollectionsOpen;

    /// <summary>Narrows the tree by name. A collection of forty requests is unusable without it.</summary>
    [ObservableProperty]
    public partial string CollectionFilter { get; set; } = string.Empty;

    partial void OnCollectionFilterChanged(string value) => RebuildTree();

    // ---------------------------------------------------------------- request tab counts

    /// <summary>
    /// How many rows are actually being sent. The counts sit on the tabs because the one thing
    /// a tab strip hides is what is inside the tabs you are not looking at, and a forgotten
    /// header is the usual reason a request behaves differently from the last one.
    /// </summary>
    public int EnabledQueryCount => Query.Count(r => r.IsEnabled && !string.IsNullOrWhiteSpace(r.Name));

    public int EnabledHeaderCount => Headers.Count(r => r.IsEnabled && !string.IsNullOrWhiteSpace(r.Name));

    public bool HasQueryCount => EnabledQueryCount > 0;

    public bool HasHeaderCount => EnabledHeaderCount > 0;

    /// <summary>A dot rather than a number: auth is on or off, there is nothing to count.</summary>
    public bool HasAuth => AuthKind != AuthKind.None;

    public bool HasBody => BodyKind != BodyKind.None;


    /// <summary>
    /// Keeps the tab counts honest. They have to follow the rows themselves and not just the
    /// list, because a row that is unticked or has had its name cleared stops being sent, and a
    /// count that does not notice is worse than no count at all.
    /// </summary>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var row in e.OldItems.OfType<EditableRow>())
            {
                row.PropertyChanged -= OnRowPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var row in e.NewItems.OfType<EditableRow>())
            {
                row.PropertyChanged += OnRowPropertyChanged;
            }
        }

        RaiseTabCounts();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditableRow.IsEnabled) or nameof(EditableRow.Name))
        {
            RaiseTabCounts();
        }
    }

    private void RaiseTabCounts()
    {
        OnPropertyChanged(nameof(EnabledQueryCount));
        OnPropertyChanged(nameof(EnabledHeaderCount));
        OnPropertyChanged(nameof(HasQueryCount));
        OnPropertyChanged(nameof(HasHeaderCount));
        OnPropertyChanged(nameof(HasAuth));
        OnPropertyChanged(nameof(HasBody));
    }

    // ---------------------------------------------------------------- response strip

    /// <summary>"200 OK" on its own, so it can be coloured without colouring the timings too.</summary>
    [ObservableProperty]
    public partial string ResponseStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResponseTimeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResponseSizeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResponseMediaType { get; set; } = string.Empty;

    /// <summary>The body exactly as it arrived, before any reformatting.</summary>
    [ObservableProperty]
    public partial string RawResponseText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial BodyPresentation BodyPresentation { get; set; } = BodyPresentation.Pretty;

    public bool IsPrettyBody => BodyPresentation == BodyPresentation.Pretty;

    public bool IsRawBody => BodyPresentation == BodyPresentation.Raw;

    public bool IsPreviewBody => BodyPresentation == BodyPresentation.Preview;

    public int BodyPresentationIndex
    {
        get => (int)BodyPresentation;
        set => BodyPresentation = (BodyPresentation)Math.Clamp(value, 0, 2);
    }

    partial void OnBodyPresentationChanged(BodyPresentation value)
    {
        OnPropertyChanged(nameof(IsPrettyBody));
        OnPropertyChanged(nameof(IsRawBody));
        OnPropertyChanged(nameof(IsPreviewBody));
        OnPropertyChanged(nameof(BodyPresentationIndex));
    }

    public bool IsHistoryView => ResponseView == ResponseView.History;

    /// <summary>
    /// Counts as plain properties rather than binding to <c>Collection.Count</c>: x:Bind reads
    /// that once and never hears about it again, so the nav would show the first response's
    /// numbers for the rest of the session.
    /// </summary>
    [ObservableProperty]
    public partial int ResponseHeaderCount { get; set; }

    [ObservableProperty]
    public partial int ResponseCookieCount { get; set; }

    [ObservableProperty]
    public partial int HistoryCount { get; set; }

    public bool HasResponseHeaders => ResponseHeaderCount > 0;

    public bool HasResponseCookies => ResponseCookieCount > 0;

    public bool HasHistory => HistoryCount > 0;

    /// <summary>The response is an image, so Preview can actually show something.</summary>
    [ObservableProperty]
    public partial bool IsImageResponse { get; set; }

    /// <summary>The body as it arrived, for the page to decode when it is an image.</summary>
    public byte[] ResponseBytes => _lastResponse?.Body ?? [];

    /// <summary>What Preview says when there is nothing to render.</summary>
    [ObservableProperty]
    public partial string PreviewNotice { get; set; } = "Send a request to see a preview.";

    // ---------------------------------------------------------------- response

    [ObservableProperty]
    public partial string ResponseText { get; set; }

    [ObservableProperty]
    public partial string StatusLine { get; set; }

    [ObservableProperty]
    public partial bool ResponseIsSuccess { get; set; }

    [ObservableProperty]
    public partial ResponseView ResponseView { get; set; }

    public ObservableCollection<KeyValueItem> ResponseHeaders { get; } = [];

    public ObservableCollection<CookieItem> ResponseCookies { get; } = [];

    public ObservableCollection<SegmentRow> ResponseTiming { get; } = [];

    public ObservableCollection<ResponseRecord> History { get; } = [];

    public IReadOnlyList<ResponseView> ResponseViews { get; } =
        [ResponseView.Body, ResponseView.Headers, ResponseView.Cookies, ResponseView.Timing];

    public bool IsBodyView => ResponseView == ResponseView.Body;

    public bool IsHeadersView => ResponseView == ResponseView.Headers;

    public bool IsCookiesView => ResponseView == ResponseView.Cookies;

    public bool IsTimingView => ResponseView == ResponseView.Timing;

    partial void OnResponseViewChanged(ResponseView value)
    {
        OnPropertyChanged(nameof(IsBodyView));
        OnPropertyChanged(nameof(IsHeadersView));
        OnPropertyChanged(nameof(IsCookiesView));
        OnPropertyChanged(nameof(IsTimingView));
        OnPropertyChanged(nameof(IsHistoryView));
        OnPropertyChanged(nameof(ResponseViewIndex));
    }

    private ResponseRecord? _lastResponse;

    /// <summary>The grammar the response pane colours with, chosen from the content type.</summary>
    [ObservableProperty]
    public partial Core.Text.SyntaxLanguage ResponseSyntax { get; set; } = Core.Text.SyntaxLanguage.None;

    // ---------------------------------------------------------------- editing plumbing

    partial void OnUrlChanged(string value)
    {
        MarkDirty();
        UpdatePreview();
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnMethodChanged(string value)
    {
        OnPropertyChanged(nameof(MethodIndex));
        MarkDirty();
    }

    partial void OnRequestNameChanged(string value) => MarkDirty();

    private void MarkDirty()
    {
        if (_loadingRequest || IsRestoring)
        {
            return;
        }

        PersistState();
    }

    private void UpdatePreview()
    {
        var resolver = new VariableResolver(_workspace.Current);
        var result = resolver.Substitute(Url);

        ResolvedUrlPreview = result.HasMissing
            ? $"{result.Text}   —   unresolved: {string.Join(", ", result.Missing)}"
            : result.Text;
    }

    protected override bool CanStart() => !IsRunning && !string.IsNullOrWhiteSpace(Url);

    // ---------------------------------------------------------------- send

    protected override async Task RunJobAsync(CancellationToken token)
    {
        var request = BuildRequest();

        if (!string.IsNullOrEmpty(SecretValue) && request.Auth.SecretRef is { } reference)
        {
            await _credentials.SetAsync(reference, SecretValue, token);
        }

        var resolver = new VariableResolver(_workspace.Current, CurrentCollection());
        var (resolved, missing) = resolver.Apply(request);

        if (missing.Count > 0)
        {
            SetWarning($"These variables have no value and were left as written: {string.Join(", ", missing)}.");
        }
        else if (IgnoreCertificateErrors)
        {
            SetWarning("Certificate validation is off for this request. Only leave it off for a development server.");
        }
        else
        {
            ClearMessage();
        }

        StatusText = "Sending…";
        Progress = -1;

        var result = await _executor.SendAsync(resolved, ConnectionMode.Pooled, token);

        if (!result.IsSuccess)
        {
            StatusLine = "No response";
            ResponseIsSuccess = false;
            SetError(result.Error!);
            return;
        }

        Present(result.Value!);
    }

    private void Present(ResponseRecord response)
    {
        _lastResponse = response;
        HasResult = true;

        StatusLine =
            $"{response.StatusLine} · {SegmentRow.Format(response.Timing.Total)} · {response.DescribeSize()}";

        // The same facts split apart, because the strip colours the status and not the rest.
        ResponseStatusText = response.StatusLine;
        ResponseTimeText = SegmentRow.Format(response.Timing.Total);
        ResponseSizeText = response.DescribeSize();
        ResponseMediaType = response.MediaType ?? string.Empty;

        // Preview only has something to draw for an image. For anything else it says why,
        // rather than showing an empty box and leaving the user to guess.
        IsImageResponse = ResponseMediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        PreviewNotice = IsImageResponse
            ? string.Empty
            : $"Nothing to preview for {(ResponseMediaType.Length == 0 ? "this response" : ResponseMediaType)}. " +
              "HTML is shown as text rather than rendered, so a response cannot run script inside this app.";
        RawResponseText = response.TryGetText() ?? string.Empty;

        ResponseIsSuccess = response.IsSuccess;

        var text = response.TryGetText();

        var mediaType = response.MediaType ?? string.Empty;

        if (text is null)
        {
            // Edge case 20: a body that is not text renders as a hex dump, not mojibake.
            ResponseText = HexDump(response.Body);
            ResponseSyntax = Core.Text.SyntaxLanguage.None;
        }
        else if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            ResponseText = JsonFormatter.PrettyOrOriginal(text);
            ResponseSyntax = Core.Text.SyntaxLanguage.Json;
        }
        else if (mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
                 mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            ResponseText = text;
            ResponseSyntax = Core.Text.SyntaxLanguage.Xml;
        }
        else
        {
            ResponseText = text;

            // No content type is no excuse for guessing wrong: colour it only if it parses.
            ResponseSyntax = JsonFormatter.IsValid(text)
                ? Core.Text.SyntaxLanguage.Json
                : Core.Text.SyntaxLanguage.None;
        }

        if (response.Truncated)
        {
            SetWarning($"The response was larger than {Limits.Describe(Limits.MaxResponseBytes)} and was truncated.");
        }

        ResponseHeaders.Clear();
        foreach (var header in response.Headers.OrderBy(static h => h.Name, StringComparer.OrdinalIgnoreCase))
        {
            ResponseHeaders.Add(header);
        }

        ResponseCookies.Clear();
        foreach (var cookie in response.Cookies)
        {
            ResponseCookies.Add(cookie);
        }

        ResponseTiming.Clear();
        AddTiming("DNS", response.Timing.Dns);
        AddTiming("Connect", response.Timing.Connect);
        AddTiming("TLS", response.Timing.Tls);
        AddTiming("Time to first byte", response.Timing.TimeToFirstByte);
        AddTiming("Download", response.Timing.Download);
        AddTiming("Total", response.Timing.Total);

        History.Insert(0, response);

        while (History.Count > Limits.MaxHistoryPerRequest)
        {
            History.RemoveAt(History.Count - 1);
        }

        ResponseHeaderCount = ResponseHeaders.Count;
        ResponseCookieCount = ResponseCookies.Count;
        HistoryCount = History.Count;

        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasResponseHeaders));
        OnPropertyChanged(nameof(HasResponseCookies));
    }

    private void AddTiming(string name, TimeSpan value) =>
        ResponseTiming.Add(new SegmentRow(name, value, HandshakeOnly: name is "DNS" or "Connect" or "TLS"));

    /// <summary>A classic offset / hex / ASCII dump, for a body that is not text.</summary>
    private static string HexDump(byte[] bytes)
    {
        const int PerLine = 16;
        var limit = Math.Min(bytes.Length, 64 * 1024);
        var builder = new StringBuilder();

        for (var offset = 0; offset < limit; offset += PerLine)
        {
            builder.Append(offset.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).Append("  ");

            for (var i = 0; i < PerLine; i++)
            {
                builder.Append(offset + i < limit
                    ? bytes[offset + i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + " "
                    : "   ");
            }

            builder.Append(' ');

            for (var i = 0; i < PerLine && offset + i < limit; i++)
            {
                var b = bytes[offset + i];
                builder.Append(b is >= 32 and < 127 ? (char)b : '.');
            }

            builder.AppendLine();
        }

        if (bytes.Length > limit)
        {
            builder.AppendLine().Append($"… {Limits.Describe(bytes.Length - limit)} more not shown.");
        }

        return builder.ToString();
    }

    private RequestDefinition BuildRequest()
    {
        var id = string.IsNullOrEmpty(_current.Id) ? Guid.NewGuid().ToString("N") : _current.Id;

        return _current = new RequestDefinition
        {
            Id = id,
            Name = RequestName,
            Method = Method,
            Url = Url.Trim(),
            Headers = [.. Headers.Where(static h => !string.IsNullOrWhiteSpace(h.Name)).Select(static h => h.ToItem())],
            Query = [.. Query.Where(static q => !string.IsNullOrWhiteSpace(q.Name)).Select(static q => q.ToItem())],
            Body = BuildBody(),
            Auth = new AuthSpec
            {
                Kind = AuthKind,
                Username = AuthUsername,
                ApiKeyName = ApiKeyName,
                ApiKeyIn = ApiKeyIn,
                TokenUrl = TokenUrl,
                ClientId = ClientId,
                Scope = Scope,
                SecretRef = AuthKind == AuthKind.None ? null : $"auth:{id}",
            },
            Options = RequestOptions.Default with
            {
                TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, 600),
                FollowRedirects = FollowRedirects,
                IgnoreCertificateErrors = IgnoreCertificateErrors,
            },
        };
    }

    private BodySpec BuildBody() => BodyKind switch
    {
        BodyKind.Json => new BodySpec { Kind = BodyKind.Json, Text = BodyText },
        BodyKind.Raw => new BodySpec { Kind = BodyKind.Raw, Text = BodyText },
        BodyKind.FormUrlEncoded => new BodySpec
        {
            Kind = BodyKind.FormUrlEncoded,
            Form = [.. FormFields.Where(static f => !string.IsNullOrWhiteSpace(f.Name)).Select(static f => f.ToField())],
        },
        BodyKind.Multipart => new BodySpec
        {
            Kind = BodyKind.Multipart,
            Parts = [.. Parts.Where(static p => !string.IsNullOrWhiteSpace(p.Name)).Select(static p => p.ToPart())],
        },
        BodyKind.BinaryFile => new BodySpec
        {
            Kind = BodyKind.BinaryFile,
            FilePath = BinaryFilePath,
            ContentType = string.IsNullOrWhiteSpace(BinaryContentType) ? null : BinaryContentType,
        },
        _ => BodySpec.None,
    };

    [RelayCommand]
    private void AddPart() => Parts.Add(new MultipartRow());

    [RelayCommand]
    private void RemovePart(MultipartRow? row)
    {
        if (row is not null)
        {
            Parts.Remove(row);
        }
    }

    [RelayCommand]
    private async Task ChoosePartFileAsync(MultipartRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (await Services.Files.PickFilePathAsync() is { } path)
        {
            row.FilePath = path;
            MarkDirty();
        }
    }

    [RelayCommand]
    private void ClearPartFile(MultipartRow? row)
    {
        if (row is not null)
        {
            row.FilePath = null;
            MarkDirty();
        }
    }

    [RelayCommand]
    private async Task ChooseBinaryFileAsync()
    {
        if (await Services.Files.PickFilePathAsync() is { } path)
        {
            BinaryFilePath = path;
            MarkDirty();
        }
    }

    /// <summary>
    /// Fetches an OAuth 2.0 access token and stores it as this request's bearer secret (FR-A25).
    /// </summary>
    /// <remarks>
    /// The token exchange is an ordinary request through the same executor, so it obeys the same
    /// timeout, proxy and TLS settings — and, like every other outbound call, goes through the
    /// one file allowed to open a socket.
    /// </remarks>
    [RelayCommand]
    private async Task GetTokenAsync()
    {
        var auth = new AuthSpec
        {
            Kind = AuthKind.OAuth2ClientCredentials,
            TokenUrl = TokenUrl,
            ClientId = ClientId,
            Scope = Scope,
        };

        var resolver = new VariableResolver(_workspace.Current, CurrentCollection());

        var built = RequestFactory.BuildTokenRequest(auth, SecretValue);

        if (!built.IsSuccess)
        {
            SetError(built.Error!);
            return;
        }

        var (tokenRequest, _) = resolver.Apply(built.Value!);

        IsRunning = true;
        StatusText = "Requesting a token…";

        try
        {
            var response = await _executor.SendAsync(tokenRequest, ConnectionMode.Pooled, CurrentToken);

            if (!response.IsSuccess)
            {
                SetError(response.Error!);
                return;
            }

            var token = RequestFactory.ReadAccessToken(response.Value!);

            if (!token.IsSuccess)
            {
                SetError(token.Error!);
                return;
            }

            var id = string.IsNullOrEmpty(_current.Id) ? Guid.NewGuid().ToString("N") : _current.Id;
            await _credentials.SetAsync($"auth:{id}", token.Value!, CurrentToken);

            // The typed client secret is cleared: it has served its purpose and the access token
            // is what the request needs from here.
            SecretValue = string.Empty;
            SetSuccess("Access token obtained and stored in the credential vault.");
        }
        finally
        {
            IsRunning = false;
            StatusText = string.Empty;
        }
    }

    private void LoadRequest(RequestDefinition request)
    {
        _loadingRequest = true;

        try
        {
            _current = request;

            RequestName = request.Name;
            Method = request.Method;
            Url = request.Url;

            Headers.Clear();
            foreach (var header in request.Headers)
            {
                Headers.Add(new EditableRow(header.Name, header.Value, header.Enabled, header.Description));
            }

            Query.Clear();
            foreach (var item in request.Query)
            {
                Query.Add(new EditableRow(item.Name, item.Value, item.Enabled, item.Description));
            }

            BodyKind = request.Body.Kind;
            BodyText = request.Body.Text ?? string.Empty;

            FormFields.Clear();
            foreach (var field in request.Body.Form)
            {
                FormFields.Add(new EditableRow(field.Name, field.Value, field.Enabled));
            }

            Parts.Clear();
            foreach (var part in request.Body.Parts)
            {
                Parts.Add(new MultipartRow(part.Name, part.Value, part.FilePath, part.Enabled));
            }

            BinaryFilePath = request.Body.FilePath;
            BinaryContentType = request.Body.ContentType;

            AuthKind = request.Auth.Kind;
            AuthUsername = request.Auth.Username;
            ApiKeyName = request.Auth.ApiKeyName;
            ApiKeyIn = request.Auth.ApiKeyIn;
            TokenUrl = request.Auth.TokenUrl;
            ClientId = request.Auth.ClientId;
            Scope = request.Auth.Scope;
            SecretValue = string.Empty;

            TimeoutSeconds = request.Options.TimeoutSeconds;
            FollowRedirects = request.Options.FollowRedirects;
            IgnoreCertificateErrors = false;
        }
        finally
        {
            _loadingRequest = false;
        }

        UpdatePreview();
    }

    // ---------------------------------------------------------------- collection tree

    private RequestCollection? CurrentCollection() => _workspace.Current.Collections.FirstOrDefault();

    private void RebuildTree()
    {
        Tree.Clear();

        // A filter flattens the tree deliberately: when you are hunting for one request by
        // name, the folder it lives in is not what you are looking at, and keeping the
        // hierarchy would mean scrolling past empty branches to find it.
        var filter = CollectionFilter.Trim();
        var filtering = filter.Length > 0;

        if (filtering)
        {
            foreach (var collection in _workspace.Current.Collections)
            {
                foreach (var request in collection.AllRequests())
                {
                    if (request.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        Tree.Add(new TreeEntry(request.Id, request.Name, request.Method, false, 0));
                    }
                }
            }

            RefreshEnvironments();
            return;
        }

        foreach (var collection in _workspace.Current.Collections)
        {
            Tree.Add(new TreeEntry(collection.Id, collection.Name, string.Empty, true, 0));

            foreach (var request in collection.Requests)
            {
                Tree.Add(new TreeEntry(request.Id, request.Name, request.Method, false, 1));
            }

            foreach (var folder in collection.Folders)
            {
                AddFolder(folder, 1);
            }
        }

        RefreshEnvironments();

        void AddFolder(RequestFolder folder, int depth)
        {
            Tree.Add(new TreeEntry(folder.Id, folder.Name, string.Empty, true, depth));

            foreach (var request in folder.Requests)
            {
                Tree.Add(new TreeEntry(request.Id, request.Name, request.Method, false, depth + 1));
            }

            foreach (var child in folder.Folders)
            {
                AddFolder(child, depth + 1);
            }
        }
    }

    /// <summary>
    /// Mirrors the workspace environments into the bound list and keeps the selection pointing
    /// at the same object, so rebuilding the tree does not look like the user changed it.
    /// </summary>
    private void RefreshEnvironments()
    {
        Environments.Clear();

        foreach (var environment in _workspace.Current.Environments)
        {
            Environments.Add(environment);
        }

        var active = _workspace.Current.ActiveEnvironment;

        if (!ReferenceEquals(ActiveEnvironment, active))
        {
            _loadingRequest = true;
            try
            {
                ActiveEnvironment = active;
            }
            finally
            {
                _loadingRequest = false;
            }
        }
    }

    [RelayCommand]
    private void OpenTreeEntry(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        foreach (var collection in _workspace.Current.Collections)
        {
            if (collection.AllRequests().FirstOrDefault(r => r.Id == id) is { } request)
            {
                LoadRequest(request);
                SetInfo($"Opened \"{request.Name}\".");
                return;
            }
        }
    }

    [RelayCommand]
    private void SaveToCollection()
    {
        var request = BuildRequest();
        var workspace = _workspace.Current;

        var collection = workspace.Collections.FirstOrDefault()
                         ?? new RequestCollection { Name = "My collection" };

        var requests = collection.AllRequests().Any(r => r.Id == request.Id)
            ? collection.Requests.Select(r => r.Id == request.Id ? request : r).ToList()
            : [.. collection.Requests, request];

        var updated = collection with { Requests = requests };

        _workspace.Update(workspace with
        {
            Collections = workspace.Collections.Count == 0
                ? [updated]
                : [updated, .. workspace.Collections.Skip(1)],
        });

        SetSuccess($"Saved \"{request.Name}\" to {updated.Name}.");
    }

    [RelayCommand]
    private void NewRequest()
    {
        LoadRequest(new RequestDefinition());
        ClearMessage();
    }

    // ---------------------------------------------------------------- import and export

    [RelayCommand]
    private async Task ImportCurlAsync()
    {
        var text = await Services.Clipboard.GetTextAsync();

        if (string.IsNullOrWhiteSpace(text))
        {
            SetInfo("Copy a cURL command first, then try again.");
            return;
        }

        var result = CurlCodec.FromCurl(text);

        if (!result.IsSuccess)
        {
            SetError(result.Error!);
            return;
        }

        LoadRequest(result.Value!);
        SetSuccess("Imported from the cURL command on the clipboard.");
    }

    [RelayCommand]
    private void CopyAsCurl()
    {
        Services.Clipboard.SetText(CurlCodec.ToCurl(BuildRequest()));
        SetSuccess("Copied as a cURL command. Secrets were left out.");
    }

    [RelayCommand]
    private async Task ImportCollectionAsync()
    {
        var file = await Services.Files.OpenTextFileAsync([".json"]);

        if (file.WasCancelled)
        {
            return;
        }

        if (!file.Success)
        {
            SetError(file.Error ?? "The file could not be opened.");
            return;
        }

        var json = file.Text ?? string.Empty;

        // Try each format in turn and report what all three said, rather than one generic
        // "could not import" that leaves the user guessing which it should have been.
        var openApi = OpenApiImporter.Import(json);

        if (openApi.IsSuccess)
        {
            AddCollection(openApi.Value!, "OpenAPI 3");
            return;
        }

        var postman = PostmanImporter.Import(json);

        if (postman.IsSuccess)
        {
            AddCollection(postman.Value!, "Postman");
            return;
        }

        var native = WorkspaceStore.ImportCollection(json);

        if (native.IsSuccess)
        {
            AddCollection(native.Value!, "Forgekit");
            return;
        }

        SetError(
            $"That file could not be read as any supported collection. " +
            $"OpenAPI: {openApi.ErrorMessage} Postman: {postman.ErrorMessage}");
    }

    private void AddCollection(RequestCollection collection, string source)
    {
        var workspace = _workspace.Current;
        _workspace.Update(workspace with { Collections = [.. workspace.Collections, collection] });

        SetSuccess($"Imported \"{collection.Name}\" from {source} — {collection.AllRequests().Count()} requests.");
    }

    [RelayCommand]
    private async Task ExportCollectionAsync()
    {
        if (CurrentCollection() is not { } collection)
        {
            SetInfo("There is no collection to export yet.");
            return;
        }

        var exported = WorkspaceStore.ExportCollection(collection);

        if (!exported.IsSuccess)
        {
            SetError(exported.Error!);
            return;
        }

        try
        {
            var path = await Services.Files.SaveTextFileAsync(collection.Name, exported.Value!, [".json"]);

            if (path is not null)
            {
                SetSuccess($"Exported to {path}. Secrets were not included.");
            }
        }
        catch (IOException ex)
        {
            SetError(ex.Message);
        }
    }

    // ---------------------------------------------------------------- hand-off (FR-A31)

    [RelayCommand]
    private void FormatResponse() => HandOff("json-formatter");

    [RelayCommand]
    private void GenerateModel() => HandOff("json-to-csharp");

    private void HandOff(string toolId)
    {
        if (string.IsNullOrWhiteSpace(ResponseText))
        {
            SetInfo("Send the request first, then hand the response on.");
            return;
        }

        Services.Handoff.Send(toolId, new ToolPayload.Text(ResponseText));
    }

    /// <summary>Compares the two most recent responses — the reason history is kept.</summary>
    [RelayCommand]
    private void CompareWithPrevious()
    {
        if (History.Count < 2)
        {
            SetInfo("Send the request at least twice before comparing responses.");
            return;
        }

        var newest = History[0].TryGetText() ?? string.Empty;
        var previous = History[1].TryGetText() ?? string.Empty;

        Services.Handoff.Send("json-diff", new ToolPayload.TextPair(previous, newest));
    }

    [RelayCommand]
    private void ProfileThis()
    {
        if (string.IsNullOrWhiteSpace(Url))
        {
            SetInfo("Enter a URL first.");
            return;
        }

        Services.Handoff.Send("api-profiler", new ToolPayload.Request(BuildRequest()));
    }

    [RelayCommand]
    private void AddHeader() => Headers.Add(new EditableRow());

    [RelayCommand]
    private void RemoveHeader(EditableRow? row)
    {
        if (row is not null)
        {
            Headers.Remove(row);
            MarkDirty();
        }
    }

    [RelayCommand]
    private void AddQuery() => Query.Add(new EditableRow());

    [RelayCommand]
    private void RemoveQuery(EditableRow? row)
    {
        if (row is not null)
        {
            Query.Remove(row);
            MarkDirty();
        }
    }

    [RelayCommand]
    private void AddFormField() => FormFields.Add(new EditableRow());

    [RelayCommand]
    private void RemoveFormField(EditableRow? row)
    {
        if (row is not null)
        {
            FormFields.Remove(row);
            MarkDirty();
        }
    }

    [RelayCommand]
    private void CopyResponse()
    {
        if (string.IsNullOrEmpty(ResponseText))
        {
            return;
        }

        Services.Clipboard.SetText(ResponseText);
        SetSuccess("Copied to clipboard.");
    }

    // ---------------------------------------------------------------- lifecycle

    protected override async Task OnActivatedAsync()
    {
        await base.OnActivatedAsync();

        await _workspace.LoadAsync();
        RebuildTree();

        if (Services.Handoff.Take(ToolId) is ToolPayload.Request handed)
        {
            LoadRequest(handed.Value);
            SetInfo($"Loaded \"{handed.Value.Name}\".");
        }

        UpdatePreview();
    }

    public override void Deactivate()
    {
        base.Deactivate();
        _ = _workspace.FlushAsync();
    }

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        // The request being composed is the user's data: kept while DevTools runs, never
        // written to disk. Saved collections are the place for requests worth keeping.
        state.SetData("name", RequestName);
        state.SetData("method", Method);
        state.SetData("url", Url);
        state.SetData("bodyKind", BodyKind.ToString());
        state.SetData("body", BodyText);
        state.SetData("authKind", AuthKind.ToString());
        state.SetData("username", AuthUsername ?? string.Empty);
        state.SetData("apiKeyName", ApiKeyName ?? string.Empty);
        state.Set("timeout", TimeoutSeconds);
        state.Set("redirects", FollowRedirects);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        RequestName = state.GetString("name", "New request");
        Method = state.GetString("method", "GET");
        Url = state.GetString("url");
        BodyKind = state.GetEnum("bodyKind", BodyKind.None);
        BodyText = state.GetString("body");
        AuthKind = state.GetEnum("authKind", AuthKind.None);
        AuthUsername = state.GetString("username");
        ApiKeyName = state.GetString("apiKeyName");
        TimeoutSeconds = Math.Clamp(state.GetInt("timeout", 100), 1, 600);
        FollowRedirects = state.GetBool("redirects", true);

        // Never restored as true, and no secret is ever restored: both are decisions the user
        // makes deliberately rather than ones a saved file makes for them.
        IgnoreCertificateErrors = false;
        SecretValue = string.Empty;
    }

    protected override void ResetOptions()
    {
        LoadRequest(new RequestDefinition());
        ResponseText = string.Empty;
        StatusLine = string.Empty;
        ResponseHeaders.Clear();
        ResponseCookies.Clear();
        ResponseTiming.Clear();
        History.Clear();
        HasResult = false;
        _lastResponse = null;
    }
}
