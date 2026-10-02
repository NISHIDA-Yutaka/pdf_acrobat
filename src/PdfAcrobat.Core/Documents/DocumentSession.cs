using System.Collections.Immutable;
using System.IO;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.Core.Documents;

public sealed class DocumentChangedEventArgs(DocumentState oldState, DocumentState newState, string? description) : EventArgs
{
    public DocumentState OldState { get; } = oldState;

    public DocumentState NewState { get; } = newState;

    public string? Description { get; } = description;
}

/// <summary>
/// An open document (one tab): the page sources it draws from, the current editable state
/// and the undo/redo history.
/// </summary>
public sealed class DocumentSession : IDisposable
{
    private const int MaxHistory = 200;

    // Read by background exports (saving, auto-save) while the UI may add sources: guarded by itself.
    private readonly Dictionary<Guid, PdfSource> _sources = new();
    private readonly List<(DocumentState State, string Description)> _undo = new();
    private readonly List<(DocumentState State, string Description)> _redo = new();
    private DocumentState? _savedState;

    public DocumentSession(PdfSource primary)
        : this([primary], primary.Name, primary.FilePath, isNew: false)
    {
    }

    private DocumentSession(IReadOnlyList<PdfSource> sources, string displayName, string? filePath, bool isNew)
    {
        PrimarySource = sources[0];
        foreach (var source in sources)
        {
            _sources.Add(source.Id, source);
        }

        FilePath = filePath;
        DisplayName = displayName;
        State = new DocumentState(sources
            .SelectMany(s => Enumerable.Range(0, s.PageCount).Select(i => PageRef.FromSource(s.Id, i)))
            .ToImmutableList());
        // A new (never saved) document counts as modified until it is saved.
        _savedState = isNew ? null : State;
    }

    /// <summary>Creates an unsaved document made of all pages of the given sources, in order.</summary>
    public static DocumentSession CreateUnsaved(string displayName, IReadOnlyList<PdfSource> sources)
    {
        if (sources.Count == 0)
        {
            throw new ArgumentException("少なくとも 1 つのファイルが必要です。", nameof(sources));
        }

        return new DocumentSession(sources, displayName, null, isNew: true);
    }

    /// <summary>A one-page PDF with a blank page of the given size (points).</summary>
    public static PdfSource CreateBlankSource(string name, PdfSize size, int pageCount = 1)
    {
        using var document = PdfDocument.CreateNew();
        for (var i = 0; i < pageCount; i++)
        {
            document.InsertBlankPage(i, size.Width, size.Height);
        }

        return PdfSource.Load(document.Save(), name);
    }

    public event EventHandler<DocumentChangedEventArgs>? Changed;

    public PdfSource PrimarySource { get; }

    /// <summary>Snapshot of the sources, in the order they were added.</summary>
    public IReadOnlyList<PdfSource> Sources
    {
        get
        {
            lock (_sources)
            {
                return _sources.Values.ToList();
            }
        }
    }

    public DocumentState State { get; private set; }

    public ImmutableList<PageRef> Pages => State.Pages;

    public string DisplayName { get; private set; }

    public string? FilePath { get; private set; }

    public bool IsModified => !ReferenceEquals(State, _savedState);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    public void AddSource(PdfSource source)
    {
        lock (_sources)
        {
            _sources.TryAdd(source.Id, source);
        }
    }

    public PdfSource GetSource(Guid id)
    {
        lock (_sources)
        {
            return _sources[id];
        }
    }

    /// <summary>Displayed size (points) of a page including its extra rotation.</summary>
    public PdfSize GetPageSize(PageRef page)
    {
        var size = page.IsBlank
            ? page.BlankSize ?? new PdfSize(595, 842)
            : GetSource(page.SourceId!.Value).PageSizes[page.SourceIndex];
        return page.Rotation % 2 == 0 ? size : new PdfSize(size.Height, size.Width);
    }

    /// <summary>Applies a change as one undoable step.</summary>
    public void Apply(string description, Func<DocumentState, DocumentState> change)
    {
        var oldState = State;
        var newState = change(oldState);
        if (ReferenceEquals(newState, oldState) || newState == oldState)
        {
            return;
        }

        _undo.Add((oldState, description));
        if (_undo.Count > MaxHistory)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        State = newState;
        Changed?.Invoke(this, new DocumentChangedEventArgs(oldState, newState, description));
    }

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        var (previous, description) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((State, description));
        var oldState = State;
        State = previous;
        Changed?.Invoke(this, new DocumentChangedEventArgs(oldState, previous, description));
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        var (next, description) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((State, description));
        var oldState = State;
        State = next;
        Changed?.Invoke(this, new DocumentChangedEventArgs(oldState, next, description));
    }

    /// <summary>Records that the current state has been written to <paramref name="filePath"/>.</summary>
    public void MarkSaved(string filePath)
    {
        FilePath = filePath;
        DisplayName = Path.GetFileName(filePath);
        _savedState = State;
        Changed?.Invoke(this, new DocumentChangedEventArgs(State, State, null));
    }

    public void Dispose()
    {
        foreach (var source in Sources)
        {
            source.Dispose();
        }

        lock (_sources)
        {
            _sources.Clear();
        }
    }
}
