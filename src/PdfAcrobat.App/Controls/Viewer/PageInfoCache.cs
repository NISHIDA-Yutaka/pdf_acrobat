using PdfAcrobat.Core.Documents;
using PdfAcrobat.Core.Text;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.Controls.Viewer;

/// <summary>Geometry, links and text of a source page, loaded off the UI thread.</summary>
public sealed class PageInfo(PdfPageGeometry geometry, IReadOnlyList<PdfLink> links, PdfTextSnapshot text)
{
    public PdfPageGeometry Geometry { get; } = geometry;

    public IReadOnlyList<PdfLink> Links { get; } = links;

    public PdfTextSnapshot Text { get; } = text;
}

/// <summary>Per-source-page <see cref="PageInfo"/> cache with a bounded size.</summary>
public sealed class PageInfoCache(PageTextCache textCache, int capacity = 128)
{
    private readonly object _sync = new();
    private readonly Dictionary<(Guid, int), Task<PageInfo>> _tasks = new();
    private readonly LinkedList<(Guid, int)> _order = new();

    public PageTextCache TextCache { get; } = textCache;

    public Task<PageInfo> GetAsync(PdfSource source, int pageIndex)
    {
        var key = (source.Id, pageIndex);
        lock (_sync)
        {
            if (_tasks.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var task = Task.Run(() => Load(source, pageIndex));
            _tasks[key] = task;
            _order.AddLast(key);
            while (_order.Count > capacity)
            {
                _tasks.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }

            return task;
        }
    }

    /// <summary>Returns the info if it has already finished loading.</summary>
    public PageInfo? TryGet(PdfSource source, int pageIndex)
    {
        lock (_sync)
        {
            return _tasks.TryGetValue((source.Id, pageIndex), out var task) && task.IsCompletedSuccessfully
                ? task.Result
                : null;
        }
    }

    private PageInfo Load(PdfSource source, int pageIndex)
    {
        try
        {
            using var page = source.Document.OpenPage(pageIndex);
            var geometry = page.Geometry;
            IReadOnlyList<PdfLink> links;
            try
            {
                links = page.GetLinks();
            }
            catch (PdfiumException)
            {
                links = [];
            }

            var text = TextCache.Get(source, pageIndex);
            return new PageInfo(geometry, links, text);
        }
        catch (PdfiumException)
        {
            var size = source.PageSizes[pageIndex];
            return new PageInfo(new PdfPageGeometry(new PdfRect(0, 0, size.Width, size.Height), 0), [], PdfTextSnapshot.Empty);
        }
    }
}
