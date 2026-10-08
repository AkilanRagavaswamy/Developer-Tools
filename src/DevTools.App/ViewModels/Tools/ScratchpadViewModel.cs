using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using DevTools.Core;
using DevTools.Core.Detection;
using DevTools.Core.Json;
using DevTools.Core.Scratch;
using DevTools.Core.Sql;
using DevTools.Core.Text;
using DevTools.Core.Xml;

namespace DevTools.App.ViewModels.Tools;

/// <summary>A note as the list shows it.</summary>
public sealed partial class ScratchNoteItem : ObservableObject
{
    public ScratchNoteItem(ScratchNoteInfo info)
    {
        Title = string.Empty;
        Preview = string.Empty;
        When = string.Empty;
        LanguageName = string.Empty;
        Apply(info);
    }

    public string Id { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Preview { get; set; }

    [ObservableProperty]
    public partial string When { get; set; }

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    public partial int Colour { get; set; }

    [ObservableProperty]
    public partial string LanguageName { get; set; }

    public bool HasPreview => !string.IsNullOrEmpty(Preview);

    public bool HasColour => Colour > 0;

    /// <summary>Spoken by a screen reader, so the colour tag and pin are not visual-only (P6).</summary>
    public string AccessibleName =>
        $"{Title}{(IsPinned ? ", pinned" : string.Empty)}" +
        $"{(Colour > 0 ? $", {ScratchpadViewModel.ColourNames[Colour]} tag" : string.Empty)}, {When}";

    partial void OnPreviewChanged(string value) => OnPropertyChanged(nameof(HasPreview));

    partial void OnColourChanged(int value) => OnPropertyChanged(nameof(HasColour));

    public void Apply(ScratchNoteInfo info)
    {
        Id = info.Id;
        Title = info.DisplayTitle;
        Preview = info.Preview;
        IsPinned = info.Pinned;
        Colour = info.Colour;
        LanguageName = ScratchNotes.DisplayName(info.Language);
        When = info.DeletedUtc is { } deleted
            ? $"Deleted {Relative(deleted)}"
            : Relative(info.ModifiedUtc);
        OnPropertyChanged(nameof(AccessibleName));
    }

    public static string Relative(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var age = DateTime.Now - local;

        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (local.Date == DateTime.Today)
        {
            return $"today {local:HH:mm}";
        }

        if (local.Date == DateTime.Today.AddDays(-1))
        {
            return $"yesterday {local:HH:mm}";
        }

        return local.Year == DateTime.Now.Year ? local.ToString("d MMM HH:mm") : local.ToString("d MMM yyyy");
    }
}

/// <summary>One saved version, as the History panel shows it.</summary>
public sealed record ScratchSnapshotItem(ScratchSnapshot Snapshot, string When, string Size)
{
    public string AccessibleName => $"Version from {When}, {Size}";
}

/// <summary>One line of a Math note's results.</summary>
public sealed record ScratchMathRow(ScratchMathLine Line)
{
    public string LineLabel => $"Line {Line.LineNumber}";

    public string Source => Line.Source;

    public string Value => Line.Result ?? "!";

    public string? Error => Line.Error;

    public bool IsError => Line.IsError;

    public string AccessibleName => Line.IsError
        ? $"Line {Line.LineNumber}, error: {Line.Error}"
        : $"Line {Line.LineNumber}, result {Line.Result}";
}

/// <summary>A colour tag, as the picker lists it.</summary>
public sealed record ScratchColour(int Index, string Name);

/// <summary>A tool a note can be sent to.</summary>
public sealed record ScratchSendTarget(string ToolId, string Name);

/// <summary>
/// Scratchpad: notes that save themselves, keep their history, and can be sent to any other
/// tool (Docs/03-Scratchpad-Requirements.md).
/// </summary>
/// <remarks>
/// Unlike every other tool, what is typed here is kept on disk — that is the point of it. It
/// goes through <see cref="IScratchpadStore"/>, never through tool state, which is options only.
/// </remarks>
public sealed partial class ScratchpadViewModel : ToolViewModelBase
{
    public static readonly string[] ColourNames = ["None", "Red", "Orange", "Yellow", "Green", "Blue", "Purple"];

    private static readonly TimeSpan MathDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ScanDelay = TimeSpan.FromSeconds(1);

    private readonly IScratchpadStore _store;
    private string? _currentId;
    private bool _loadingNote;
    private bool _openedThisSession;
    private string _lastSavedText = string.Empty;
    private CancellationTokenSource? _mathCts;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _searchCts;
    private HashSet<string>? _searchHits;
    private string? _lastTrashedId;
    private int _undoVersion;

    public ScratchpadViewModel(ToolServices services, IScratchpadStore store)
        : base(services)
    {
        _store = store;
        Text = string.Empty;
        SearchQuery = string.Empty;
        NoteTitle = string.Empty;
        FooterText = string.Empty;
        SaveStatus = string.Empty;
        SnapshotPreview = string.Empty;
        MathSummary = string.Empty;
        IsNotesPaneOpen = true;
    }

    public override string ToolId => "scratchpad";

    protected override TimeSpan? MessageAutoHideDelay => TimeSpan.FromSeconds(6);

    // ---------------------------------------------------------------- list

    public ObservableCollection<ScratchNoteItem> Notes { get; } = [];

    [ObservableProperty]
    public partial ScratchNoteItem? SelectedNote { get; set; }

    [ObservableProperty]
    public partial string SearchQuery { get; set; }

    /// <summary>0 = notes, 1 = archived, 2 = trash.</summary>
    [ObservableProperty]
    public partial int FilterIndex { get; set; }

    [ObservableProperty]
    public partial bool IsNotesPaneOpen { get; set; }

    public bool IsTrashView => FilterIndex == 2;

    public bool HasNotes => Notes.Count > 0;

    public string EmptyListText => FilterIndex switch
    {
        1 => "No archived notes.",
        2 => $"Trash is empty. Deleted notes stay here for {Services.Settings.ScratchpadRetentionDays} days.",
        _ => string.IsNullOrWhiteSpace(SearchQuery) ? "No notes yet. Press Ctrl+N to start one." : "No notes match.",
    };

    // ---------------------------------------------------------------- current note

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial string NoteTitle { get; set; }

    [ObservableProperty]
    public partial int LanguageIndex { get; set; }

    [ObservableProperty]
    public partial int ColourIndex { get; set; }

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    public partial bool IsArchived { get; set; }

    [ObservableProperty]
    public partial bool IsInTrash { get; set; }

    [ObservableProperty]
    public partial bool HasCurrentNote { get; set; }

    [ObservableProperty]
    public partial string FooterText { get; set; }

    [ObservableProperty]
    public partial string SaveStatus { get; set; }

    public string TitlePlaceholder => ScratchNotes.DeriveTitle(Text) ?? "Title (taken from the first line)";

    public ScratchLanguage Language => (ScratchLanguage)Math.Clamp(LanguageIndex, 0, ScratchNotes.Languages.Count - 1);

    public IReadOnlyList<string> LanguageNames { get; } = [.. ScratchNotes.Languages.Select(ScratchNotes.DisplayName)];

    public IReadOnlyList<ScratchColour> Colours { get; } = [.. ColourNames.Select((name, i) => new ScratchColour(i, name))];

    /// <summary>The editor footer: size, then whether it is on disk yet.</summary>
    public string Footer => string.IsNullOrEmpty(SaveStatus) ? FooterText : $"{FooterText} · {SaveStatus}";

    public bool IsEditable => HasCurrentNote && !IsInTrash;

    public bool CanFormat => IsEditable && Language is ScratchLanguage.Json or ScratchLanguage.Xml or ScratchLanguage.Sql;

    public string TrashBannerText => _currentId is { } id && _store.Find(id)?.DeletedUtc is { } deleted
        ? $"This note is in Trash and will be deleted for good {DaysLeft(deleted)}. Restore it to edit."
        : string.Empty;

    // ---------------------------------------------------------------- side panels

    [ObservableProperty]
    public partial bool IsHistoryOpen { get; set; }

    public ObservableCollection<ScratchSnapshotItem> History { get; } = [];

    [ObservableProperty]
    public partial ScratchSnapshotItem? SelectedSnapshot { get; set; }

    [ObservableProperty]
    public partial string SnapshotPreview { get; set; }

    public bool HasSnapshotSelected => SelectedSnapshot is not null;

    public bool HasHistory => History.Count > 0;

    public ObservableCollection<ScratchMathRow> MathRows { get; } = [];

    [ObservableProperty]
    public partial string MathSummary { get; set; }

    public bool IsMath => HasCurrentNote && Language == ScratchLanguage.Math;

    public bool ShowMathPanel => IsMath && !IsHistoryOpen;

    public bool ShowSidePanel => HasCurrentNote && (IsHistoryOpen || IsMath);

    // ---------------------------------------------------------------- hints

    [ObservableProperty]
    public partial string? SecretWarning { get; set; }

    public bool HasSecretWarning => !string.IsNullOrEmpty(SecretWarning);

    [ObservableProperty]
    public partial string? LanguageSuggestion { get; set; }

    private ScratchLanguage _suggestedLanguage;

    public bool HasLanguageSuggestion => !string.IsNullOrEmpty(LanguageSuggestion);

    [ObservableProperty]
    public partial string? UndoText { get; set; }

    public bool CanUndoDelete => !string.IsNullOrEmpty(UndoText);

    public IReadOnlyList<ScratchSendTarget> SendTargets
    {
        get
        {
            var all = new List<ScratchSendTarget>();
            foreach (var id in SendOrder(Language))
            {
                if (Services.Catalog.ById(id) is { } tool)
                {
                    all.Add(new ScratchSendTarget(tool.Id, tool.Name));
                }
            }

            return all;
        }
    }

    /// <summary>Raised before the note on screen changes, so the page can report where the caret was.</summary>
    public event EventHandler? NoteLeaving;

    /// <summary>Raised after a note is shown, with the caret offset to restore.</summary>
    public event EventHandler<int>? NoteShown;

    /// <summary>Set by the page from <see cref="NoteLeaving"/>.</summary>
    public int CaretToRemember { get; set; }

    // ================================================================ lifecycle

    protected override async Task OnActivatedAsync()
    {
        _store.Changed += OnStoreChanged;
        _store.SaveStatusChanged += OnSaveStatusChanged;

        await _store.InitializeAsync();

        RefreshList();

        // A tool's output sent here, or a note asked for by the palette, wins over everything.
        var pending = _store.PendingFocusId;
        _store.PendingFocusId = null;

        if (Services.Handoff.Take(ToolId) is ToolPayload.Text handed && !string.IsNullOrEmpty(handed.Value))
        {
            var created = await _store.CreateAsync(handed.Value);
            pending = created.Id;
        }

        if (pending is not null)
        {
            FilterIndex = 0;
            await ShowNoteAsync(pending);
        }
        else if (!_openedThisSession && Services.Settings.ScratchpadStartWithNewNote &&
                 (_store.ActiveId is not { } active || !ScratchNotes.IsEmpty(await _store.ReadAsync(active))))
        {
            await NewNoteAsync();
        }
        else if (_store.ActiveId is { } last && _store.Find(last) is { InTrash: false })
        {
            await ShowNoteAsync(last);
        }
        else if (_store.Notes.Where(n => !n.InTrash && !n.Archived).OrderByDescending(n => n.ModifiedUtc).FirstOrDefault() is { } newest)
        {
            await ShowNoteAsync(newest.Id);
        }
        else
        {
            await NewNoteAsync();
        }

        _openedThisSession = true;
    }

    public override void Deactivate()
    {
        _store.Changed -= OnStoreChanged;
        _store.SaveStatusChanged -= OnSaveStatusChanged;

        LeaveCurrentNote();
        _ = _store.FlushAsync();

        CancelToken(ref _mathCts);
        CancelToken(ref _scanCts);
        CancelToken(ref _searchCts);

        base.Deactivate();
    }

    private void OnStoreChanged(object? sender, EventArgs e) => UiDispatcher.Run(RefreshList);

    private void OnSaveStatusChanged(object? sender, string? error) => UiDispatcher.Run(() =>
    {
        if (error is null)
        {
            SaveStatus = "Saved";
            ClearMessage();
        }
        else
        {
            SaveStatus = "Not saved";
            SetError(error + " Your text is kept here and saving is retried every 10 seconds.");
        }
    });

    // ================================================================ list

    private void RefreshList()
    {
        var notes = _store.Notes;

        var visible = notes.Where(n => FilterIndex switch
            {
                1 => n.Archived && !n.InTrash,
                2 => n.InTrash,
                _ => !n.Archived && !n.InTrash,
            })
            .Where(n => _searchHits is null || _searchHits.Contains(n.Id));

        var ordered = FilterIndex == 2
            ? visible.OrderByDescending(n => n.DeletedUtc)
            : visible.OrderByDescending(n => n.Pinned).ThenByDescending(n => n.ModifiedUtc);

        var list = ordered.ToList();

        // Update in place where the order allows, so the selection and scroll position survive
        // a keystroke changing the title.
        var sameOrder = list.Count == Notes.Count && list.Select(n => n.Id).SequenceEqual(Notes.Select(n => n.Id));
        if (sameOrder)
        {
            for (var i = 0; i < list.Count; i++)
            {
                Notes[i].Apply(list[i]);
            }
        }
        else
        {
            var selectedId = _currentId;
            Notes.Clear();
            foreach (var info in list)
            {
                Notes.Add(new ScratchNoteItem(info));
            }

            _suppressSelection = true;
            try
            {
                SelectedNote = Notes.FirstOrDefault(n => n.Id == selectedId);
            }
            finally
            {
                _suppressSelection = false;
            }
        }

        if (_currentId is { } current && _store.Find(current) is { } info2)
        {
            IsPinned = info2.Pinned;
            IsArchived = info2.Archived;
            IsInTrash = info2.InTrash;
        }

        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(EmptyListText));
        OnPropertyChanged(nameof(TrashBannerText));
    }

    private bool _suppressSelection;

    partial void OnSelectedNoteChanged(ScratchNoteItem? value)
    {
        if (_suppressSelection || value is null || value.Id == _currentId)
        {
            return;
        }

        _ = ShowNoteAsync(value.Id);
    }

    partial void OnFilterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsTrashView));
        RefreshList();
        PersistState();
    }

    partial void OnIsNotesPaneOpenChanged(bool value) => PersistState();

    partial void OnSearchQueryChanged(string value)
    {
        CancelToken(ref _searchCts);

        if (string.IsNullOrWhiteSpace(value))
        {
            _searchHits = null;
            RefreshList();
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = SearchAsync(value.Trim(), cts.Token);
    }

    private async Task SearchAsync(string query, CancellationToken token)
    {
        try
        {
            await Task.Delay(200, token);

            // Unsaved text of the open note must be findable too.
            if (_currentId is { } id)
            {
                _store.ScheduleSave(id, Text);
            }

            var hits = await Task.Run(() => _store.SearchAsync(query, token), token);
            token.ThrowIfCancellationRequested();

            _searchHits = hits;
            RefreshList();
        }
        catch (OperationCanceledException)
        {
            // A newer query replaced this one.
        }
    }

    // ================================================================ showing a note

    private async Task ShowNoteAsync(string id)
    {
        if (_store.Find(id) is not { } info)
        {
            return;
        }

        LeaveCurrentNote();

        var text = await _store.ReadAsync(id);

        _loadingNote = true;
        try
        {
            _currentId = id;
            _store.ActiveId = id;
            _lastSavedText = text;

            HasCurrentNote = true;

            // In the editor's own line endings, so its echo of the text is not taken for an edit.
            text = AsEditorText(text);
            _lastSavedText = text;
            Text = text;
            NoteTitle = info.CustomTitle ?? string.Empty;
            LanguageIndex = (int)info.Language;
            ColourIndex = Math.Clamp(info.Colour, 0, ColourNames.Length - 1);
            IsPinned = info.Pinned;
            IsArchived = info.Archived;
            IsInTrash = info.InTrash;

            LanguageSuggestion = null;
            SecretWarning = null;
            IsHistoryOpen = false;
            SelectedSnapshot = null;
            History.Clear();
        }
        finally
        {
            _loadingNote = false;
        }

        // Select it in the list if it is there; show the list it lives in if it is not.
        var item = Notes.FirstOrDefault(n => n.Id == id);
        if (item is null && string.IsNullOrWhiteSpace(SearchQuery))
        {
            var wanted = info.InTrash ? 2 : info.Archived ? 1 : 0;
            if (FilterIndex != wanted)
            {
                FilterIndex = wanted;
                item = Notes.FirstOrDefault(n => n.Id == id);
            }
        }

        _suppressSelection = true;
        try
        {
            SelectedNote = item;
        }
        finally
        {
            _suppressSelection = false;
        }

        UpdateDerived();
        ScheduleMath();
        ScheduleScan(info.SecretDismissed);
        NotifyNoteProperties();

        NoteShown?.Invoke(this, info.Caret);
    }

    /// <summary>
    /// Saves where the user was in the note being left, and drops it altogether if it was
    /// never written in (edge case 9) — an empty "Untitled" is not worth keeping.
    /// </summary>
    private void LeaveCurrentNote()
    {
        if (_currentId is not { } id)
        {
            return;
        }

        NoteLeaving?.Invoke(this, EventArgs.Empty);

        if (ScratchNotes.IsEmpty(Text) && _store.GetHistory(id).Count == 0 && _store.Find(id) is { InTrash: false, CustomTitle: null })
        {
            _store.DeleteForever(id);
        }
        else
        {
            var caret = CaretToRemember;
            _store.Update(id, n => n.Caret = caret);

            if (Text != _lastSavedText)
            {
                _store.ScheduleSave(id, Text);
            }
        }

        _currentId = null;
        HasCurrentNote = false;
    }

    private void NotifyNoteProperties()
    {
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(CanFormat));
        OnPropertyChanged(nameof(IsMath));
        OnPropertyChanged(nameof(ShowMathPanel));
        OnPropertyChanged(nameof(ShowSidePanel));
        OnPropertyChanged(nameof(SendTargets));
        OnPropertyChanged(nameof(TrashBannerText));
        OnPropertyChanged(nameof(TitlePlaceholder));
    }

    partial void OnHasCurrentNoteChanged(bool value) => NotifyNoteProperties();

    partial void OnIsInTrashChanged(bool value) => NotifyNoteProperties();

    partial void OnIsHistoryOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMathPanel));
        OnPropertyChanged(nameof(ShowSidePanel));

        if (value)
        {
            _ = LoadHistoryAsync();
        }
    }

    // ================================================================ editing

    partial void OnTextChanged(string oldValue, string newValue)
    {
        if (_loadingNote || _currentId is not { } id)
        {
            return;
        }

        if (IsInTrash)
        {
            return;
        }

        // A paste over everything, a drop or a clear: keep what was there first (SP-33).
        if (IsWholesaleReplace(oldValue, newValue))
        {
            _ = _store.SnapshotAsync(id, oldValue);
        }

        _store.ScheduleSave(id, newValue);
        SaveStatus = "Saving…";
        _ = MarkSavedAsync();

        if (Language == ScratchLanguage.Plain && ScratchNotes.IsEmpty(oldValue) && newValue.Length >= 20)
        {
            SuggestLanguage(newValue);
        }

        UpdateDerived();
        ScheduleMath();
        ScheduleScan(_store.Find(id)?.SecretDismissed ?? false);
    }

    private int _saveVersion;

    private async Task MarkSavedAsync()
    {
        var version = ++_saveVersion;
        await Task.Delay(900);

        if (version == _saveVersion && SaveStatus == "Saving…")
        {
            SaveStatus = "Saved";
            _lastSavedText = Text;
        }
    }

    private static bool IsWholesaleReplace(string oldValue, string newValue)
    {
        if (oldValue.Length < 200)
        {
            return false;
        }

        if (newValue.Length == 0)
        {
            return true;
        }

        // Shares neither the beginning nor the end: everything was replaced.
        var probe = Math.Min(64, Math.Min(oldValue.Length, newValue.Length));
        return !newValue.AsSpan(0, probe).SequenceEqual(oldValue.AsSpan(0, probe)) &&
               !newValue.AsSpan(newValue.Length - probe).SequenceEqual(oldValue.AsSpan(oldValue.Length - probe));
    }

    private void UpdateDerived()
    {
        var text = Text ?? string.Empty;

        if (text.Length == 0)
        {
            FooterText = "0 characters";
        }
        else
        {
            var lines = TextUtil.CountLines(text);
            var characters = text.Length > 1_000_000 ? text.Length : TextUtil.GraphemeCount(text);
            FooterText = $"{characters:N0} characters · {lines:N0} lines";
        }

        OnPropertyChanged(nameof(TitlePlaceholder));
    }

    partial void OnNoteTitleChanged(string value)
    {
        if (_loadingNote || _currentId is not { } id)
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _store.Update(id, n => n.CustomTitle = title);
    }

    partial void OnLanguageIndexChanged(int value)
    {
        if (!_loadingNote && _currentId is { } id)
        {
            var language = Language;
            _store.Update(id, n => n.Language = language);
            LanguageSuggestion = null;
        }

        NotifyNoteProperties();
        ScheduleMath();
    }

    partial void OnColourIndexChanged(int value)
    {
        if (!_loadingNote && _currentId is { } id)
        {
            _store.Update(id, n => n.Colour = value);
        }
    }

    partial void OnIsPinnedChanged(bool value)
    {
        if (!_loadingNote && _currentId is { } id && _store.Find(id) is { } info && info.Pinned != value)
        {
            _store.Update(id, n => n.Pinned = value);
        }
    }

    partial void OnFooterTextChanged(string value) => OnPropertyChanged(nameof(Footer));

    partial void OnSaveStatusChanged(string value) => OnPropertyChanged(nameof(Footer));

    partial void OnSecretWarningChanged(string? value) => OnPropertyChanged(nameof(HasSecretWarning));

    partial void OnLanguageSuggestionChanged(string? value) => OnPropertyChanged(nameof(HasLanguageSuggestion));

    partial void OnUndoTextChanged(string? value) => OnPropertyChanged(nameof(CanUndoDelete));

    partial void OnSelectedSnapshotChanged(ScratchSnapshotItem? value)
    {
        OnPropertyChanged(nameof(HasSnapshotSelected));
        _ = LoadSnapshotPreviewAsync(value);
    }

    // ================================================================ math

    private void ScheduleMath()
    {
        CancelToken(ref _mathCts);

        if (!IsMath)
        {
            MathRows.Clear();
            MathSummary = string.Empty;
            return;
        }

        if (Text.Length > ScratchNotes.LargeNoteChars)
        {
            MathRows.Clear();
            MathSummary = "This note is too large to calculate live.";
            return;
        }

        var cts = new CancellationTokenSource();
        _mathCts = cts;
        _ = RunMathAsync(Text, cts.Token);
    }

    private async Task RunMathAsync(string text, CancellationToken token)
    {
        try
        {
            await Task.Delay(MathDelay, token);
            var lines = await ComputeAsync(() => ScratchMath.Evaluate(text), token);

            MathRows.Clear();
            foreach (var line in lines)
            {
                MathRows.Add(new ScratchMathRow(line));
            }

            var results = lines.Count(l => !l.IsError);
            var errors = lines.Count - results;
            MathSummary = lines.Count == 0
                ? "Type a calculation on any line: 20% of 1500, rate = 40, 3.5 GB in MiB."
                : errors == 0
                    ? $"{results:N0} results · click one to copy it"
                    : $"{results:N0} results · {errors:N0} with a problem — hover for why";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
    }

    [RelayCommand]
    private void CopyMathResult(ScratchMathRow? row)
    {
        if (row?.Line.CopyText is { } value)
        {
            Services.Clipboard.SetText(value);
            SetSuccess($"Copied {value}.");
        }
        else if (row?.Error is { } error)
        {
            SetWarning($"Line {row.Line.LineNumber}: {error}");
        }
    }

    // ================================================================ hints

    private void ScheduleScan(bool dismissed)
    {
        CancelToken(ref _scanCts);

        if (dismissed)
        {
            SecretWarning = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var text = Text;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ScanDelay, cts.Token);
                var found = ScratchSecrets.Find(text);
                UiDispatcher.Run(() =>
                {
                    if (!cts.IsCancellationRequested)
                    {
                        SecretWarning = found is null
                            ? null
                            : $"This note seems to contain {found}. Notes are saved unencrypted in {_store.FolderPath}.";
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Typing continued.
            }
        });
    }

    [RelayCommand]
    private void DismissSecret()
    {
        if (_currentId is { } id)
        {
            _store.Update(id, n => n.SecretDismissed = true);
        }

        SecretWarning = null;
    }

    private void SuggestLanguage(string text)
    {
        var hits = SmartDetector.Detect(text);
        var language = hits.Select(h => h.ToolId).FirstOrDefault() switch
        {
            "json-formatter" or "json-to-csharp" or "json-diff" or "json-to-table" => ScratchLanguage.Json,
            "xml-formatter" or "svg-to-xaml" => ScratchLanguage.Xml,
            "sql-formatter" => ScratchLanguage.Sql,
            _ => ScratchLanguage.Plain,
        };

        if (language == ScratchLanguage.Plain)
        {
            return;
        }

        _suggestedLanguage = language;
        LanguageSuggestion = $"This looks like {ScratchNotes.DisplayName(language)}. Set the note's language?";
    }

    [RelayCommand]
    private void ApplyLanguageSuggestion()
    {
        LanguageIndex = (int)_suggestedLanguage;
        LanguageSuggestion = null;
    }

    [RelayCommand]
    private void DismissLanguageSuggestion() => LanguageSuggestion = null;

    // ================================================================ note commands

    [RelayCommand]
    private async Task NewNoteAsync()
    {
        if (FilterIndex != 0)
        {
            FilterIndex = 0;
        }

        if (!string.IsNullOrEmpty(SearchQuery))
        {
            SearchQuery = string.Empty;
        }

        // Pressing New on an empty note just stays there.
        if (_currentId is { } id && ScratchNotes.IsEmpty(Text) && _store.Find(id) is { InTrash: false, Archived: false })
        {
            NoteShown?.Invoke(this, 0);
            return;
        }

        LeaveCurrentNote();
        var created = await _store.CreateAsync();
        RefreshList();
        await ShowNoteAsync(created.Id);
    }

    [RelayCommand]
    private async Task DuplicateAsync()
    {
        if (_currentId is not { } id)
        {
            return;
        }

        _store.ScheduleSave(id, Text);
        var copy = await _store.DuplicateAsync(id);
        if (copy is not null)
        {
            await ShowNoteAsync(copy.Id);
            SetSuccess("Duplicated.");
        }
    }

    [RelayCommand]
    private void ToggleArchive()
    {
        if (_currentId is not { } id)
        {
            return;
        }

        var archive = !IsArchived;
        _store.Update(id, n => n.Archived = archive);
        IsArchived = archive;
        SetInfo(archive ? "Archived. Find it under Archived." : "Moved back to Notes.");
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_currentId is not { } id)
        {
            return;
        }

        // Nothing to keep: an empty note just goes.
        if (ScratchNotes.IsEmpty(Text) && _store.GetHistory(id).Count == 0)
        {
            _currentId = null;
            _store.DeleteForever(id);
        }
        else
        {
            _store.ScheduleSave(id, Text);
            await _store.SnapshotAsync(id, Text);
            _store.MoveToTrash(id);
            _lastTrashedId = id;
            _ = ShowUndoAsync($"Moved \"{_store.Find(id)?.DisplayTitle}\" to Trash.");
            _currentId = null;
        }

        HasCurrentNote = false;
        await ShowNextAsync();
    }

    private async Task ShowNextAsync()
    {
        RefreshList();

        if (Notes.FirstOrDefault() is { } next)
        {
            await ShowNoteAsync(next.Id);
        }
        else if (FilterIndex == 0)
        {
            await NewNoteAsync();
        }
        else
        {
            _loadingNote = true;
            try
            {
                Text = string.Empty;
                NoteTitle = string.Empty;
            }
            finally
            {
                _loadingNote = false;
            }

            HasCurrentNote = false;
        }
    }

    private async Task ShowUndoAsync(string text)
    {
        var version = ++_undoVersion;
        UndoText = text;
        await Task.Delay(TimeSpan.FromSeconds(8));

        if (version == _undoVersion)
        {
            UndoText = null;
        }
    }

    [RelayCommand]
    private async Task UndoDeleteAsync()
    {
        _undoVersion++;
        UndoText = null;

        if (_lastTrashedId is { } id && _store.Find(id) is { InTrash: true })
        {
            _store.Restore(id);
            _lastTrashedId = null;
            await ShowNoteAsync(id);
        }
    }

    [RelayCommand]
    private async Task RestoreNoteAsync()
    {
        if (_currentId is not { } id)
        {
            return;
        }

        _store.Restore(id);
        IsInTrash = false;
        SetSuccess("Restored.");
        FilterIndex = _store.Find(id)?.Archived == true ? 1 : 0;
        await ShowNoteAsync(id);
    }

    [RelayCommand]
    private async Task DeleteForeverAsync()
    {
        if (_currentId is not { } id)
        {
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync(
                "Delete for good",
                $"\"{_store.Find(id)?.DisplayTitle}\" and all its saved versions will be removed. This cannot be undone.",
                "Delete"))
        {
            return;
        }

        _currentId = null;
        _store.DeleteForever(id);
        HasCurrentNote = false;
        await ShowNextAsync();
    }

    [RelayCommand]
    private async Task EmptyTrashAsync()
    {
        var count = _store.Notes.Count(n => n.InTrash);
        if (count == 0)
        {
            SetInfo("Trash is already empty.");
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync(
                "Empty Trash",
                $"{count:N0} {(count == 1 ? "note" : "notes")} and their saved versions will be removed. This cannot be undone.",
                "Empty Trash"))
        {
            return;
        }

        if (_currentId is { } id && _store.Find(id) is { InTrash: true })
        {
            _currentId = null;
            HasCurrentNote = false;
        }

        _store.EmptyTrash();
        SetSuccess("Trash emptied.");
        await ShowNextAsync();
    }

    [RelayCommand]
    private void CopyNote()
    {
        if (!string.IsNullOrEmpty(Text))
        {
            Services.Clipboard.SetText(ScratchNotes.NormalizeLineEndings(Text));
        }
    }

    [RelayCommand]
    private void ToggleNotesPane() => IsNotesPaneOpen = !IsNotesPaneOpen;

    [RelayCommand]
    private void ToggleHistory() => IsHistoryOpen = !IsHistoryOpen;

    // ================================================================ format and send

    [RelayCommand]
    private async Task FormatAsync()
    {
        if (!CanFormat || _currentId is not { } id || ScratchNotes.IsEmpty(Text))
        {
            return;
        }

        var text = Text;
        var language = Language;

        var result = await Task.Run(() => language switch
        {
            ScratchLanguage.Json => Map(JsonFormatter.Format(text), r => r.Output),
            ScratchLanguage.Xml => Map(XmlFormatter.Format(text), r => r.Output),
            _ => SqlFormatter.Format(text),
        });

        if (!result.IsSuccess)
        {
            SetError(result.Error!);
            return;
        }

        if (ScratchNotes.SameText(result.Value, text))
        {
            SetInfo("Already formatted.");
            return;
        }

        await _store.SnapshotAsync(id, text);
        Text = AsEditorText(result.Value);
        SetSuccess("Formatted. The previous version is in History.");

        static OperationResult<string> Map<T>(OperationResult<T> source, Func<T, string> select) =>
            source.IsSuccess ? OperationResult<string>.Ok(select(source.Value!), source.Warning) : OperationResult<string>.Fail(source.Error!);
    }

    /// <summary>Sends the selection, or the whole note, to another tool (SP-40).</summary>
    public void SendTo(string toolId, string selection)
    {
        var text = string.IsNullOrEmpty(selection) ? Text : selection;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetInfo("There is nothing to send yet.");
            return;
        }

        if (_currentId is { } id)
        {
            _store.ScheduleSave(id, Text);
        }

        Services.Handoff.Send(toolId, new ToolPayload.Text(text));
    }

    private static IEnumerable<string> SendOrder(ScratchLanguage language)
    {
        string[] first = language switch
        {
            ScratchLanguage.Json => ["json-formatter", "json-to-csharp", "json-to-table", "json-diff"],
            ScratchLanguage.Xml => ["xml-formatter"],
            ScratchLanguage.Sql => ["sql-formatter"],
            ScratchLanguage.Markdown => ["markdown-preview"],
            ScratchLanguage.Html => ["html-viewer", "html-encoder"],
            _ => [],
        };

        string[] all =
        [
            "json-formatter", "sql-formatter", "xml-formatter", "text-compare", "json-diff", "regex-validator",
            "json-to-csharp", "json-to-table", "markdown-preview", "html-viewer", "base64-text", "url-encoder",
            "html-encoder", "character-counter", "qr-code",
        ];

        return first.Concat(all).Distinct();
    }

    // ================================================================ history

    private async Task LoadHistoryAsync()
    {
        History.Clear();
        SelectedSnapshot = null;
        SnapshotPreview = string.Empty;

        if (_currentId is not { } id)
        {
            return;
        }

        // The current text becomes a version first, so comparing against "now" is meaningful.
        await _store.SnapshotAsync(id, Text);

        var snapshots = await Task.Run(() => _store.GetHistory(id));
        foreach (var snapshot in snapshots)
        {
            History.Add(new ScratchSnapshotItem(
                snapshot,
                ScratchNoteItem.Relative(snapshot.CreatedUtc),
                Limits.Describe(snapshot.Bytes)));
        }

        OnPropertyChanged(nameof(HasHistory));
    }

    private async Task LoadSnapshotPreviewAsync(ScratchSnapshotItem? item)
    {
        SnapshotPreview = item is null ? string.Empty : AsEditorText(await _store.ReadSnapshotAsync(item.Snapshot));
    }

    [RelayCommand]
    private async Task RestoreSnapshotAsync()
    {
        if (SelectedSnapshot is not { } item || _currentId is not { } id)
        {
            return;
        }

        var text = AsEditorText(await _store.ReadSnapshotAsync(item.Snapshot));
        if (ScratchNotes.SameText(text, Text))
        {
            SetInfo("That version is the same as the note.");
            return;
        }

        await _store.SnapshotAsync(id, Text);
        Text = text;
        SetSuccess($"Restored the version from {item.When}. What was there before is kept in History.");
        await LoadHistoryAsync();
    }

    [RelayCommand]
    private async Task CompareSnapshotAsync()
    {
        if (SelectedSnapshot is not { } item)
        {
            return;
        }

        var old = await _store.ReadSnapshotAsync(item.Snapshot);
        Services.Handoff.Send("text-compare", new ToolPayload.TextPair(AsEditorText(old), Text));
    }

    // ================================================================ files

    [RelayCommand]
    private async Task ImportAsync()
    {
        var result = await Services.Files.OpenTextFileAsync(".txt", ".md", ".json", ".sql", ".xml", ".log", ".csv", ".html", ".cs", ".yaml", ".yml");

        if (result.WasCancelled)
        {
            return;
        }

        if (!result.Success)
        {
            SetError(result.Error ?? "The file could not be opened.");
            return;
        }

        LeaveCurrentNote();
        var language = ScratchNotes.FromExtension(Path.GetExtension(result.FileName));
        var created = await _store.CreateAsync(result.Text ?? string.Empty, language, result.FileName);
        FilterIndex = 0;
        RefreshList();
        await ShowNoteAsync(created.Id);
        SetSuccess($"Opened {result.FileName} as a new note.");
    }

    /// <summary>Ctrl+S: exports the note as a file, with the extension its language suggests.</summary>
    public override async Task SaveOutputAsync()
    {
        if (_currentId is not { } id || ScratchNotes.IsEmpty(Text))
        {
            SetInfo("There is nothing to export yet.");
            return;
        }

        var info = _store.Find(id);
        var name = ScratchpadStore.FileStem(info?.DisplayTitle ?? "note") + ScratchNotes.Extension(Language);

        try
        {
            var path = await Services.Files.SaveTextFileAsync(name, Text, ScratchNotes.Extension(Language));
            if (path is not null)
            {
                SetSuccess($"Exported to {path}.");
            }
        }
        catch (IOException ex)
        {
            SetError(ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportAllAsync()
    {
        if (_currentId is { } id)
        {
            _store.ScheduleSave(id, Text);
        }

        var count = _store.Notes.Count(n => !n.InTrash);
        if (count == 0)
        {
            SetInfo("There are no notes to export.");
            return;
        }

        try
        {
            var zip = await _store.ExportAllAsync();
            var path = await Services.Files.SaveBytesAsync($"scratchpad-{DateTime.Now:yyyy-MM-dd}.zip", zip, ".zip");
            if (path is not null)
            {
                SetSuccess($"Exported {count:N0} {(count == 1 ? "note" : "notes")} to {path}.");
            }
        }
        catch (IOException ex)
        {
            SetError(ex.Message);
        }
    }

    // ================================================================ base overrides

    /// <summary>Ctrl+L clears the note — after keeping what was there as a version.</summary>
    public override void Clear()
    {
        if (_currentId is { } id && !ScratchNotes.IsEmpty(Text) && !IsInTrash)
        {
            _ = _store.SnapshotAsync(id, Text);
            Text = string.Empty;
            SetInfo("Cleared. The previous text is in History.");
            return;
        }

        ClearMessage();
    }

    /// <summary>Ctrl+Enter makes a new note: there is nothing to "run".</summary>
    public override Task RunAsync() => NewNoteAsync();

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("notesPane", IsNotesPaneOpen);
        state.Set("filter", FilterIndex);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        IsNotesPaneOpen = state.GetBool("notesPane", true);

        // Always open on the notes themselves; a Trash view from last time would look like data loss.
        FilterIndex = 0;
    }

    protected override void ResetOptions()
    {
        IsNotesPaneOpen = true;
        FilterIndex = 0;
    }

    /// <summary>Text with the bare CR line breaks that WinUI's text box uses itself.</summary>
    private static string AsEditorText(string? text) => ScratchNotes.NormalizeLineEndings(text, "\r");

    private string DaysLeft(DateTime deletedUtc)
    {
        var days = Services.Settings.ScratchpadRetentionDays;
        var left = (deletedUtc.AddDays(days) - DateTime.UtcNow).TotalDays;
        return left < 1 ? "within a day" : $"in {Math.Ceiling(left):N0} days";
    }

    private static void CancelToken(ref CancellationTokenSource? cts)
    {
        var current = cts;
        cts = null;

        if (current is null)
        {
            return;
        }

        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already done.
        }
    }
}
