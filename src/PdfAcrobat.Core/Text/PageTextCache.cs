using PdfAcrobat.Core.Documents;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.Core.Text;

/// <summary>
/// Caches managed text snapshots of source pages (LRU). Loading a snapshot opens the page in PDFium,
/// so callers should use it from a background thread.
/// </summary>
public sealed class PageTextCache(int capacity = 96)
{
    private readonly object _sync = new();
    private readonly Dictionary<(Guid, int), LinkedListNode<((Guid, int) Key, PdfTextSnapshot Snapshot)>> _map = new();
    private readonly LinkedList<((Guid, int) Key, PdfTextSnapshot Snapshot)> _lru = new();

    public PdfTextSnapshot Get(PdfSource source, int pageIndex)
    {
        var key = (source.Id, pageIndex);
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Snapshot;
            }
        }

        PdfTextSnapshot snapshot;
        try
        {
            using var page = source.Document.OpenPage(pageIndex);
            snapshot = page.GetTextPage().CreateSnapshot();
        }
        catch (PdfiumException)
        {
            snapshot = PdfTextSnapshot.Empty;
        }

        lock (_sync)
        {
            if (!_map.ContainsKey(key))
            {
                _map[key] = _lru.AddFirst((key, snapshot));
                while (_lru.Count > capacity)
                {
                    _map.Remove(_lru.Last!.Value.Key);
                    _lru.RemoveLast();
                }
            }
        }

        return snapshot;
    }

    public bool TryGet(PdfSource source, int pageIndex, out PdfTextSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_map.TryGetValue((source.Id, pageIndex), out var node))
            {
                snapshot = node.Value.Snapshot;
                return true;
            }
        }

        snapshot = PdfTextSnapshot.Empty;
        return false;
    }
}
