using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using DevTools.Core.Scratch;

namespace DevTools.App.Services;

/// <summary>A note's metadata, as kept in <c>Scratchpad\index.json</c>. The text lives in its own file.</summary>
public sealed class ScratchNoteInfo
{
    public string Id { get; set; } = string.Empty;

    /// <summary>A title the user typed, or null to use the first line.</summary>
    public string? CustomTitle { get; set; }

    /// <summary>The first line, kept so the list never has to open every file.</summary>
    public string? AutoTitle { get; set; }

    public string Preview { get; set; } = string.Empty;

    public ScratchLanguage Language { get; set; }

    public bool Pinned { get; set; }

    /// <summary>Index into the colour tags; 0 is none.</summary>
    public int Colour { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime ModifiedUtc { get; set; }

    public bool Archived { get; set; }

    /// <summary>When it went to Trash, or null when it is not there.</summary>
    public DateTime? DeletedUtc { get; set; }

    public int Caret { get; set; }

    public long Bytes { get; set; }

    /// <summary>Set once the user has dismissed the "looks like a secret" warning for this note.</summary>
    public bool SecretDismissed { get; set; }

    public bool InTrash => DeletedUtc is not null;

    public string DisplayTitle =>
        !string.IsNullOrWhiteSpace(CustomTitle) ? CustomTitle!
        : !string.IsNullOrWhiteSpace(AutoTitle) ? AutoTitle!
        : $"Untitled · {CreatedUtc.ToLocalTime():HH:mm}";

    public ScratchNoteInfo Clone() => (ScratchNoteInfo)MemberwiseClone();
}

/// <summary>One saved version of a note.</summary>
public sealed record ScratchSnapshot(string NoteId, string Path, DateTime CreatedUtc, long Bytes);

public sealed class ScratchIndex
{
    public int Version { get; set; } = 1;

    public string? ActiveId { get; set; }

    public List<ScratchNoteInfo> Notes { get; set; } = [];
}

public interface IScratchpadStore
{
    /// <summary>Raised, on any thread, when the list of notes or a note's metadata changes.</summary>
    event EventHandler? Changed;

    /// <summary>Raised, on any thread, when a save fails or recovers. Null message means recovered.</summary>
    event EventHandler<string?>? SaveStatusChanged;

    /// <summary>The note a tool sent here most recently, waiting for Scratchpad to show it.</summary>
    string? PendingFocusId { get; set; }

    Task InitializeAsync();

    IReadOnlyList<ScratchNoteInfo> Notes { get; }

    ScratchNoteInfo? Find(string id);

    string? ActiveId { get; set; }

    Task<ScratchNoteInfo> CreateAsync(string text = "", ScratchLanguage language = ScratchLanguage.Plain, string? title = null);

    /// <summary>A new note holding a tool's output, for "Send to Scratchpad" (SP-41).</summary>
    Task<ScratchNoteInfo> CreateFromToolAsync(string text, string toolName, ScratchLanguage language);

    Task<string> ReadAsync(string id);

    /// <summary>Queues a save of a note's text; repeated calls coalesce (SP-32).</summary>
    void ScheduleSave(string id, string text);

    /// <summary>Writes any pending saves now — on note switch, tool switch and app close.</summary>
    Task FlushAsync();

    /// <summary>Saves a version of the note unless it matches the newest one (SP-33).</summary>
    Task SnapshotAsync(string id, string text);

    IReadOnlyList<ScratchSnapshot> GetHistory(string id);

    Task<string> ReadSnapshotAsync(ScratchSnapshot snapshot);

    void Update(string id, Action<ScratchNoteInfo> change);

    Task<ScratchNoteInfo?> DuplicateAsync(string id);

    void MoveToTrash(string id);

    void Restore(string id);

    /// <summary>Removes a note and its history for good (Trash → Delete, Empty Trash).</summary>
    void DeleteForever(string id);

    int EmptyTrash();

    /// <summary>Moves every note to Trash (Settings → Stored data).</summary>
    int TrashAll();

    /// <summary>Removes every saved version of every note (Settings → Stored data).</summary>
    void ClearHistory();

    /// <summary>The ids of notes whose title or text contains <paramref name="query"/>.</summary>
    Task<HashSet<string>> SearchAsync(string query, CancellationToken token);

    /// <summary>Every note not in Trash as plain files in a zip (SP-07).</summary>
    Task<byte[]> ExportAllAsync();

    /// <summary>Where the notes are kept, for Settings.</summary>
    string FolderPath { get; }
}

/// <summary>
/// Scratchpad's notes, on disk in the package's LocalState (SP-30…37).
/// </summary>
/// <remarks>
/// <para>
/// Each note is a plain UTF-8 file in <c>Scratchpad\notes</c>, so it can be opened with any
/// editor; the list, titles and flags are in <c>index.json</c>, which is rebuilt from the
/// files if it is ever lost. Versions go to <c>Scratchpad\history\&lt;id&gt;</c>.
/// </para>
/// <para>
/// Every write goes to a temp file that is then moved over the real one, so a crash or a power
/// cut leaves either the old text or the new — never half a file.
/// </para>
/// <para>
/// This is the one place DevTools keeps what the user types. Every other tool forgets its
/// input when the app closes; a scratchpad that did would be pointless.
/// </para>
/// </remarks>
public sealed class ScratchpadStore(ISettingsService settings) : IScratchpadStore
{
    private const string Root = "Scratchpad";
    private const string IndexFile = "index.json";

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan IndexDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IdleSnapshotDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxSnapshotInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _lock = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private ScratchIndex _index = new();
    private Task? _initialize;

    private readonly ConcurrentDictionary<string, string> _pendingText = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _saveTimers = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _idleTimers = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastSnapshotUtc = new();
    private readonly ConcurrentDictionary<string, string> _lastSnapshotText = new();
    private CancellationTokenSource? _indexTimer;
    private string? _saveError;

    public event EventHandler? Changed;

    public event EventHandler<string?>? SaveStatusChanged;

    public string? PendingFocusId { get; set; }

    public string FolderPath => JsonStore.PathFor(Root);

    private static string NotesFolder => JsonStore.PathFor(Path.Combine(Root, "notes"));

    private static string HistoryFolder => JsonStore.PathFor(Path.Combine(Root, "history"));

    private static string NotePath(string id) => Path.Combine(NotesFolder, $"{Safe(id)}.txt");

    private static string HistoryPath(string id) => Path.Combine(HistoryFolder, Safe(id));

    public IReadOnlyList<ScratchNoteInfo> Notes
    {
        get
        {
            lock (_lock)
            {
                return [.. _index.Notes.Select(n => n.Clone())];
            }
        }
    }

    public string? ActiveId
    {
        get
        {
            lock (_lock)
            {
                return _index.ActiveId;
            }
        }
        set
        {
            lock (_lock)
            {
                if (_index.ActiveId == value)
                {
                    return;
                }

                _index.ActiveId = value;
            }

            ScheduleIndexSave();
        }
    }

    public ScratchNoteInfo? Find(string id)
    {
        lock (_lock)
        {
            return _index.Notes.FirstOrDefault(n => n.Id == id)?.Clone();
        }
    }

    // ---------------------------------------------------------------- start-up

    public Task InitializeAsync()
    {
        lock (_lock)
        {
            return _initialize ??= Task.Run(LoadAsync);
        }
    }

    private async Task LoadAsync()
    {
        Directory.CreateDirectory(NotesFolder);

        var stored = await JsonStore.LoadAsync<ScratchIndex>(Path.Combine(Root, IndexFile)).ConfigureAwait(false);
        var index = stored ?? new ScratchIndex();

        // Edge case 4: the index is a cache of what is in the folder. Any note file it does not
        // know about — because the index was lost, or never written — is put back in the list.
        var known = index.Notes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rebuilt = false;

        foreach (var file in SafeFiles(NotesFolder, "*.txt"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (known.Contains(id))
            {
                continue;
            }

            var text = await ReadFileAsync(file).ConfigureAwait(false);
            var info = new FileInfo(file);
            index.Notes.Add(new ScratchNoteInfo
            {
                Id = id,
                AutoTitle = ScratchNotes.DeriveTitle(text),
                Preview = ScratchNotes.Preview(text),
                CreatedUtc = info.CreationTimeUtc,
                ModifiedUtc = info.LastWriteTimeUtc,
                Bytes = info.Length,
            });
            rebuilt = true;
        }

        // A note whose file has gone (deleted outside the app) is dropped from the list rather
        // than shown as a note that cannot be opened.
        var removed = index.Notes.RemoveAll(n => !File.Exists(NotePath(n.Id)));

        lock (_lock)
        {
            _index = index;
        }

        if (rebuilt || removed > 0 || stored is null)
        {
            await SaveIndexNowAsync().ConfigureAwait(false);
        }

        Changed?.Invoke(this, EventArgs.Empty);

        // Pruning touches every history folder, so it runs after the list is up.
        _ = Task.Run(Prune);
    }

    /// <summary>Applies the retention rules: Trash, then each note's history, then the size cap.</summary>
    private void Prune()
    {
        try
        {
            var days = settings.ScratchpadRetentionDays;
            var now = DateTime.UtcNow;

            foreach (var note in Notes.Where(n => n.DeletedUtc is { } deleted && ScratchRetention.IsTrashExpired(deleted, now, days)))
            {
                DeleteForever(note.Id);
            }

            var all = new List<ScratchSnapshotInfo>();
            var live = Notes.Select(n => Safe(n.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in SafeDirectories(HistoryFolder))
            {
                // History for a note that no longer exists has nothing left to restore into.
                if (!live.Contains(Path.GetFileName(folder)))
                {
                    TryDeleteDirectory(folder);
                    continue;
                }

                var snapshots = ListSnapshots(folder);
                foreach (var expired in ScratchRetention.SelectExpired(snapshots, now, days))
                {
                    TryDeleteFile(expired.Key);
                }

                all.AddRange(ListSnapshots(folder));
            }

            var notesBytes = SafeFiles(NotesFolder, "*.txt").Sum(f => new FileInfo(f).Length);
            foreach (var over in ScratchRetention.SelectOverCap(all, notesBytes))
            {
                TryDeleteFile(over.Key);
            }
        }
        catch (Exception ex)
        {
            App.LogError("Scratchpad pruning", ex);
        }
    }

    // ---------------------------------------------------------------- notes

    public async Task<ScratchNoteInfo> CreateAsync(string text = "", ScratchLanguage language = ScratchLanguage.Plain, string? title = null)
    {
        await InitializeAsync().ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var info = new ScratchNoteInfo
        {
            Id = NewId(),
            CustomTitle = title,
            AutoTitle = ScratchNotes.DeriveTitle(text),
            Preview = ScratchNotes.Preview(text),
            Language = language,
            CreatedUtc = now,
            ModifiedUtc = now,
            Bytes = Utf8.GetByteCount(text),
        };

        await WriteNoteFileAsync(info.Id, text).ConfigureAwait(false);

        lock (_lock)
        {
            _index.Notes.Add(info);
        }

        ScheduleIndexSave();
        Changed?.Invoke(this, EventArgs.Empty);
        return info.Clone();
    }

    public async Task<ScratchNoteInfo> CreateFromToolAsync(string text, string toolName, ScratchLanguage language)
    {
        var title = $"From {toolName} · {DateTime.Now:yyyy-MM-dd HH:mm}";
        var info = await CreateAsync(text, language, title).ConfigureAwait(false);
        PendingFocusId = info.Id;
        return info;
    }

    public async Task<string> ReadAsync(string id)
    {
        await InitializeAsync().ConfigureAwait(false);

        // Text not yet on disk is newer than the file.
        if (_pendingText.TryGetValue(id, out var pending))
        {
            return pending;
        }

        return await ReadFileAsync(NotePath(id)).ConfigureAwait(false);
    }

    public void ScheduleSave(string id, string text)
    {
        _pendingText[id] = text;

        // Metadata follows the text at once, so the list shows the new title while typing.
        UpdateFromText(id, text);

        Restart(_saveTimers, id, SaveDelay, () => SavePendingAsync(id));
        Restart(_idleTimers, id, IdleSnapshotDelay, async () =>
        {
            var current = await ReadAsync(id).ConfigureAwait(false);
            await SnapshotAsync(id, current).ConfigureAwait(false);
        });
    }

    public async Task FlushAsync()
    {
        foreach (var id in _pendingText.Keys.ToList())
        {
            if (_saveTimers.TryRemove(id, out var timer))
            {
                timer.Cancel();
                timer.Dispose();
            }

            await SavePendingAsync(id).ConfigureAwait(false);
        }

        // Taken, not just read, so a flush with no change since the last one writes nothing.
        if (Interlocked.Exchange(ref _indexTimer, null) is { } indexTimer)
        {
            indexTimer.Cancel();
            await SaveIndexNowAsync().ConfigureAwait(false);
        }
    }

    private async Task SavePendingAsync(string id)
    {
        if (!_pendingText.TryGetValue(id, out var text))
        {
            return;
        }

        try
        {
            await WriteNoteFileAsync(id, text).ConfigureAwait(false);

            // Only forget the text if nothing newer arrived while it was being written (edge case 1).
            _pendingText.TryRemove(new KeyValuePair<string, string>(id, text));

            if (_saveError is not null)
            {
                _saveError = null;
                SaveStatusChanged?.Invoke(this, null);
            }

            // A long, unbroken stretch of typing still gets a version every few minutes.
            var last = _lastSnapshotUtc.GetValueOrDefault(id, DateTime.MinValue);
            if (DateTime.UtcNow - last >= MaxSnapshotInterval && last != DateTime.MinValue)
            {
                await SnapshotAsync(id, text).ConfigureAwait(false);
            }
            else if (last == DateTime.MinValue)
            {
                _lastSnapshotUtc[id] = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            // Edge case 2: the text stays in memory and the save is retried until it lands.
            _saveError = $"Can't save the note: {ex.Message}";
            SaveStatusChanged?.Invoke(this, _saveError);
            Restart(_saveTimers, id, RetryDelay, () => SavePendingAsync(id));
        }
    }

    private void UpdateFromText(string id, string text)
    {
        var changed = false;

        lock (_lock)
        {
            var note = _index.Notes.FirstOrDefault(n => n.Id == id);
            if (note is null)
            {
                return;
            }

            var title = ScratchNotes.DeriveTitle(text);
            var preview = ScratchNotes.Preview(text);

            changed = title != note.AutoTitle || preview != note.Preview;
            note.AutoTitle = title;
            note.Preview = preview;
            note.ModifiedUtc = DateTime.UtcNow;
            note.Bytes = text.Length;
        }

        ScheduleIndexSave();

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Update(string id, Action<ScratchNoteInfo> change)
    {
        lock (_lock)
        {
            var note = _index.Notes.FirstOrDefault(n => n.Id == id);
            if (note is null)
            {
                return;
            }

            change(note);
        }

        ScheduleIndexSave();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ScratchNoteInfo?> DuplicateAsync(string id)
    {
        var source = Find(id);
        if (source is null)
        {
            return null;
        }

        var text = await ReadAsync(id).ConfigureAwait(false);
        var title = $"{source.DisplayTitle} (copy)";
        var copy = await CreateAsync(text, source.Language, title).ConfigureAwait(false);
        Update(copy.Id, n => n.Colour = source.Colour);
        return copy;
    }

    public void MoveToTrash(string id) => Update(id, n =>
    {
        n.DeletedUtc = DateTime.UtcNow;
        n.Pinned = false;
    });

    public void Restore(string id) => Update(id, n => n.DeletedUtc = null);

    public void DeleteForever(string id)
    {
        Cancel(_saveTimers, id);
        Cancel(_idleTimers, id);
        _pendingText.TryRemove(id, out _);
        _lastSnapshotText.TryRemove(id, out _);
        _lastSnapshotUtc.TryRemove(id, out _);

        lock (_lock)
        {
            _index.Notes.RemoveAll(n => n.Id == id);
            if (_index.ActiveId == id)
            {
                _index.ActiveId = null;
            }
        }

        TryDeleteFile(NotePath(id));
        TryDeleteDirectory(HistoryPath(id));

        ScheduleIndexSave();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public int EmptyTrash()
    {
        var trashed = Notes.Where(n => n.InTrash).ToList();
        foreach (var note in trashed)
        {
            DeleteForever(note.Id);
        }

        return trashed.Count;
    }

    public int TrashAll()
    {
        var now = DateTime.UtcNow;
        var count = 0;

        lock (_lock)
        {
            foreach (var note in _index.Notes.Where(n => !n.InTrash))
            {
                note.DeletedUtc = now;
                note.Pinned = false;
                count++;
            }
        }

        ScheduleIndexSave();
        Changed?.Invoke(this, EventArgs.Empty);
        return count;
    }

    // ---------------------------------------------------------------- history

    public async Task SnapshotAsync(string id, string text)
    {
        if (ScratchNotes.IsEmpty(text) || Find(id) is null)
        {
            return;
        }

        var newest = _lastSnapshotText.TryGetValue(id, out var cached)
            ? cached
            : GetHistory(id).FirstOrDefault() is { } latest ? await ReadSnapshotAsync(latest).ConfigureAwait(false) : null;

        if (ScratchNotes.SameText(newest, text))
        {
            _lastSnapshotText[id] = text;
            return;
        }

        var folder = HistoryPath(id);
        var now = DateTime.UtcNow;

        // Named in UTC so a clock change or DST cannot reorder them (edge case 8).
        var name = now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            await _io.WaitAsync().ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(folder);
                await WriteAtomicAsync(Path.Combine(folder, name + ".txt"), text).ConfigureAwait(false);
            }
            finally
            {
                _io.Release();
            }

            _lastSnapshotText[id] = text;
            _lastSnapshotUtc[id] = now;

            // Keep the newest within the per-note limit as versions are added, not only at start-up.
            var snapshots = ListSnapshots(folder);
            if (snapshots.Count > ScratchRetention.MaxSnapshotsPerNote)
            {
                foreach (var expired in ScratchRetention.SelectExpired(snapshots, now, settings.ScratchpadRetentionDays))
                {
                    TryDeleteFile(expired.Key);
                }
            }
        }
        catch (Exception ex)
        {
            App.LogError("Scratchpad snapshot", ex);
        }
    }

    public IReadOnlyList<ScratchSnapshot> GetHistory(string id) =>
        [.. ListSnapshots(HistoryPath(id))
            .OrderByDescending(s => s.CreatedUtc)
            .Select(s => new ScratchSnapshot(id, s.Key, s.CreatedUtc, s.Bytes))];

    public Task<string> ReadSnapshotAsync(ScratchSnapshot snapshot) => ReadFileAsync(snapshot.Path);

    public void ClearHistory()
    {
        _lastSnapshotText.Clear();
        _lastSnapshotUtc.Clear();
        TryDeleteDirectory(HistoryFolder);
    }

    private static List<ScratchSnapshotInfo> ListSnapshots(string folder)
    {
        var list = new List<ScratchSnapshotInfo>();

        foreach (var file in SafeFiles(folder, "*.txt"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (!DateTime.TryParseExact(
                    stem,
                    ["yyyyMMdd-HHmmss-fff", "yyyyMMdd-HHmmss"],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var created))
            {
                continue;
            }

            long bytes;
            try
            {
                bytes = new FileInfo(file).Length;
            }
            catch (IOException)
            {
                continue;
            }

            list.Add(new ScratchSnapshotInfo(file, created, bytes));
        }

        return list;
    }

    // ---------------------------------------------------------------- search and export

    public async Task<HashSet<string>> SearchAsync(string query, CancellationToken token)
    {
        var hits = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(query))
        {
            return hits;
        }

        foreach (var note in Notes)
        {
            token.ThrowIfCancellationRequested();

            if (note.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                hits.Add(note.Id);
                continue;
            }

            var text = await ReadAsync(note.Id).ConfigureAwait(false);
            if (text.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                hits.Add(note.Id);
            }
        }

        return hits;
    }

    public async Task<byte[]> ExportAllAsync()
    {
        await FlushAsync().ConfigureAwait(false);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var note in Notes.Where(n => !n.InTrash).OrderBy(n => n.CreatedUtc))
            {
                var stem = FileStem(note.DisplayTitle);
                var name = stem + ScratchNotes.Extension(note.Language);
                for (var i = 2; !used.Add(name); i++)
                {
                    name = $"{stem} ({i}){ScratchNotes.Extension(note.Language)}";
                }

                var folder = note.Archived ? "Archived/" : string.Empty;
                var entry = zip.CreateEntry(folder + name, CompressionLevel.Optimal);
                entry.LastWriteTime = note.ModifiedUtc.ToLocalTime();

                var bytes = Utf8.GetBytes(await ReadAsync(note.Id).ConfigureAwait(false));
                var stream = entry.Open();
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(bytes).ConfigureAwait(false);
                }
            }
        }

        return buffer.ToArray();
    }

    /// <summary>A note title made safe to be a file name.</summary>
    public static string FileStem(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(title.Length);

        foreach (var c in title)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 || c == '…' ? '_' : c);
        }

        var stem = builder.ToString().Trim().TrimEnd('.');
        if (stem.Length > 80)
        {
            stem = stem[..80].TrimEnd();
        }

        return stem.Length == 0 ? "note" : stem;
    }

    // ---------------------------------------------------------------- files

    private async Task WriteNoteFileAsync(string id, string text)
    {
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(NotesFolder);
            await WriteAtomicAsync(NotePath(id), text).ConfigureAwait(false);
        }
        finally
        {
            _io.Release();
        }
    }

    private static async Task WriteAtomicAsync(string path, string text)
    {
        var temp = path + ".tmp";
        // On disk every line ends in CRLF, so the file reads properly in any Windows editor.
        await File.WriteAllTextAsync(temp, ScratchNotes.NormalizeLineEndings(text), Utf8).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private static async Task<string> ReadFileAsync(string path)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            return Decode(bytes);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>UTF-8 (with or without a BOM), UTF-16 by BOM, else Windows-1252 (edge case 5).</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private void ScheduleIndexSave()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _indexTimer, cts)?.Cancel();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(IndexDelay, cts.Token).ConfigureAwait(false);
                await SaveIndexNowAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A newer change will write the index instead.
            }
        });
    }

    private async Task SaveIndexNowAsync()
    {
        ScratchIndex snapshot;
        lock (_lock)
        {
            snapshot = new ScratchIndex
            {
                Version = _index.Version,
                ActiveId = _index.ActiveId,
                Notes = [.. _index.Notes.Select(n => n.Clone())],
            };
        }

        await JsonStore.SaveAsync(Path.Combine(Root, IndexFile), snapshot).ConfigureAwait(false);
    }

    private static void Restart(ConcurrentDictionary<string, CancellationTokenSource> timers, string id, TimeSpan delay, Func<Task> work)
    {
        var cts = new CancellationTokenSource();
        if (timers.TryRemove(id, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        timers[id] = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                timers.TryRemove(new KeyValuePair<string, CancellationTokenSource>(id, cts));
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded.
            }
            catch (Exception ex)
            {
                App.LogError("Scratchpad background save", ex);
            }
        });
    }

    private static void Cancel(ConcurrentDictionary<string, CancellationTokenSource> timers, string id)
    {
        if (timers.TryRemove(id, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private string NewId()
    {
        while (true)
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            lock (_lock)
            {
                if (_index.Notes.All(n => n.Id != id))
                {
                    return id;
                }
            }
        }
    }

    /// <summary>Ids are ours, but a hand-edited index must never steer a path.</summary>
    private static string Safe(string id)
    {
        Span<char> buffer = stackalloc char[Math.Min(id.Length, 64)];
        for (var i = 0; i < buffer.Length; i++)
        {
            var c = id[i];
            buffer[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }

        return buffer.Length == 0 ? "_" : new string(buffer);
    }

    private static IEnumerable<string> SafeFiles(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder) : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort; pruning will try again next start.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
