using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using PdfAcrobat.Core.Documents;
using PdfAcrobat.Core.Export;

namespace PdfAcrobat.App.Services;

/// <summary>A document left behind by an instance that did not exit normally.</summary>
public sealed record RecoveredDocument(string Folder, string PdfPath, string DisplayName, string? OriginalPath, DateTime SavedAt, int PageCount);

/// <summary>
/// Auto-save and crash recovery. Every running instance writes the unsaved state of its open
/// documents to its own folder under <c>AutoSave</c> and keeps a lock file open there. On start-up,
/// folders whose lock is no longer held belong to instances that crashed (or were killed), so their
/// documents can be offered for recovery. A normal exit deletes the instance folder.
/// </summary>
public sealed class AutoSaveService : IDisposable
{
    private const string LockFileName = ".lock";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _root;
    private readonly string _folder;
    private readonly Dictionary<DocumentSession, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DocumentSession, object> _closed = new();
    private FileStream? _lock;
    private DispatcherTimer? _timer;
    private Func<IEnumerable<DocumentSession>>? _openSessions;
    private bool _saving;

    public AutoSaveService(string root)
    {
        _root = root;
        _folder = Path.Combine(root, $"{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}");
    }

    /// <summary>How often modified documents are written (Acrobat uses 5 minutes by default).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(2);

    public string Folder => _folder;

    /// <summary>Starts the periodic auto-save of the sessions returned by <paramref name="openSessions"/>.</summary>
    public void Start(Func<IEnumerable<DocumentSession>> openSessions)
    {
        _openSessions = openSessions;
        _timer = new DispatcherTimer(Interval, DispatcherPriority.Background, async (_, _) => await SaveNowAsync(), Dispatcher.CurrentDispatcher);
        _timer.Start();
    }

    private bool EnsureFolder()
    {
        if (_lock is not null)
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(_folder);
            _lock = new FileStream(Path.Combine(_folder, LockFileName), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Writes every document whose state changed since its last auto-save.</summary>
    public async Task SaveNowAsync()
    {
        if (_saving || _openSessions is null)
        {
            return;
        }

        _saving = true;
        try
        {
            foreach (var session in _openSessions().ToList())
            {
                if (_closed.TryGetValue(session, out _))
                {
                    continue; // Closed while an earlier document was being written.
                }

                _entries.TryGetValue(session, out var entry);
                if (!session.IsModified)
                {
                    if (entry is not null)
                    {
                        DeleteFiles(entry);
                        entry.State = null;
                    }

                    continue;
                }

                if (entry?.State is { } saved && ReferenceEquals(saved, session.State))
                {
                    continue;
                }

                if (!EnsureFolder())
                {
                    return;
                }

                entry ??= _entries[session] = new Entry(Guid.NewGuid());
                var state = session.State;
                var manifest = new Manifest(session.DisplayName, session.FilePath, DateTime.Now, state.Pages.Count);
                entry.Pending = Task.Run(() => Write(session, entry.Id, manifest));
                if (await entry.Pending)
                {
                    entry.State = state;
                }
            }
        }
        finally
        {
            _saving = false;
        }
    }

    private bool Write(DocumentSession session, Guid id, Manifest manifest)
    {
        try
        {
            var bytes = DocumentExporter.Export(session);
            var pdf = Path.Combine(_folder, $"{id:N}.pdf");
            File.WriteAllBytes(pdf + ".tmp", bytes);
            File.Move(pdf + ".tmp", pdf, overwrite: true);
            File.WriteAllText(Path.Combine(_folder, $"{id:N}.json"), JsonSerializer.Serialize(manifest, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            // Auto-save must never disturb editing (the document may also be closing meanwhile).
            ErrorLog.Write(ex);
            return false;
        }
    }

    /// <summary>The document was saved: its auto-saved copy is no longer needed.</summary>
    public async Task DiscardAsync(DocumentSession session)
    {
        if (!_entries.TryGetValue(session, out var entry))
        {
            return;
        }

        if (entry.Pending is { } pending)
        {
            await pending;
        }

        DeleteFiles(entry);
        entry.State = null;
    }

    /// <summary>The document is being closed: waits for a running write (it uses the document) and deletes its files.</summary>
    public async Task CloseAsync(DocumentSession session)
    {
        _closed.AddOrUpdate(session, new object());
        if (!_entries.Remove(session, out var entry))
        {
            return;
        }

        if (entry.Pending is { } pending)
        {
            await pending;
        }

        DeleteFiles(entry);
    }

    private void DeleteFiles(Entry entry)
    {
        foreach (var extension in new[] { ".pdf", ".json" })
        {
            try
            {
                File.Delete(Path.Combine(_folder, $"{entry.Id:N}{extension}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Documents left by instances that are no longer running.</summary>
    public IReadOnlyList<RecoveredDocument> FindRecoverable()
    {
        var result = new List<RecoveredDocument>();
        if (!Directory.Exists(_root))
        {
            return result;
        }

        foreach (var folder in Directory.GetDirectories(_root))
        {
            if (string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase) || IsInUse(folder))
            {
                continue;
            }

            foreach (var json in Directory.GetFiles(folder, "*.json"))
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(json), JsonOptions);
                    var pdf = Path.ChangeExtension(json, ".pdf");
                    if (manifest is not null && File.Exists(pdf))
                    {
                        result.Add(new RecoveredDocument(folder, pdf, manifest.DisplayName, manifest.OriginalPath, manifest.SavedAt, manifest.PageCount));
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                }
            }

            if (!result.Any(r => r.Folder == folder))
            {
                DeleteFolder(folder);
            }
        }

        return result.OrderBy(r => r.SavedAt).ToList();
    }

    /// <summary>Removes the folders of recovered (or declined) documents.</summary>
    public void Discard(IEnumerable<RecoveredDocument> documents)
    {
        foreach (var folder in documents.Select(d => d.Folder).Distinct())
        {
            DeleteFolder(folder);
        }
    }

    /// <summary>An instance holds its lock file open with no sharing while it runs.</summary>
    private static bool IsInUse(string folder)
    {
        try
        {
            using var probe = new FileStream(Path.Combine(folder, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Normal exit: nothing needs recovering.</summary>
    public void Dispose()
    {
        _timer?.Stop();

        // Let a write that is still running finish, so that no half-written copy is left behind.
        var pending = _entries.Values.Select(e => e.Pending).OfType<Task>().Where(t => !t.IsCompleted).ToArray();
        try
        {
            Task.WaitAll(pending, TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _lock?.Dispose();
        _lock = null;
        if (Directory.Exists(_folder))
        {
            DeleteFolder(_folder);
        }
    }

    private sealed class Entry(Guid id)
    {
        public Guid Id { get; } = id;

        /// <summary>State that the files on disk contain (null: nothing written).</summary>
        public DocumentState? State { get; set; }

        public Task<bool>? Pending { get; set; }
    }

    private sealed record Manifest(string DisplayName, string? OriginalPath, DateTime SavedAt, int PageCount);
}
