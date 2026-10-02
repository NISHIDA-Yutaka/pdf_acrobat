using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfAcrobat.Core.Documents;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.Services;

public enum RenderPriority
{
    Visible = 0,
    Detail = 1,
    Prefetch = 2,
    Thumbnail = 3,
    Background = 4,
}

/// <summary>Region of a page bitmap, in pixels of the full-page rendering.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public readonly record struct RenderKey(
    Guid SourceId,
    int PageIndex,
    int Rotation,
    int PixelWidth,
    int PixelHeight,
    PixelRect? Clip,
    bool Annotations);

/// <summary>
/// Renders pages on background threads with priorities and keeps the results in a memory-bounded
/// LRU cache. Bitmaps are frozen, so they can be handed to the UI thread directly.
/// </summary>
public sealed class PageRenderService : IDisposable
{
    private const long CacheBudgetBytes = 384L * 1024 * 1024;
    private const int WorkerCount = 2;

    private readonly object _sync = new();
    private readonly List<Request> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Dictionary<RenderKey, LinkedListNode<(RenderKey Key, BitmapSource Bitmap)>> _cacheMap = new();
    private readonly LinkedList<(RenderKey Key, BitmapSource Bitmap)> _lru = new();
    private readonly List<Thread> _workers = new();
    private readonly CancellationTokenSource _shutdown = new();
    private long _cacheBytes;
    private long _sequence;
    private int _inFlight;

    public PageRenderService()
    {
        for (var i = 0; i < WorkerCount; i++)
        {
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"PageRender-{i}",
                Priority = ThreadPriority.BelowNormal,
            };
            thread.Start();
            _workers.Add(thread);
        }
    }

    /// <summary>Raised (on a worker thread) whenever the queue becomes empty.</summary>
    public event EventHandler? Idle;

    public bool IsIdle
    {
        get
        {
            lock (_sync)
            {
                return _queue.Count == 0 && _inFlight == 0;
            }
        }
    }

    public Task<BitmapSource?> RenderAsync(
        PdfSource source,
        int pageIndex,
        int rotation,
        int pixelWidth,
        int pixelHeight,
        RenderPriority priority,
        CancellationToken cancellationToken,
        PixelRect? clip = null,
        bool annotations = true)
    {
        pixelWidth = Math.Max(1, pixelWidth);
        pixelHeight = Math.Max(1, pixelHeight);
        var key = new RenderKey(source.Id, pageIndex, rotation & 3, pixelWidth, pixelHeight, clip, annotations);
        if (TryGetCached(key) is { } cached)
        {
            return Task.FromResult<BitmapSource?>(cached);
        }

        var request = new Request(key, source, priority, cancellationToken);
        lock (_sync)
        {
            request.Sequence = ++_sequence;
            _queue.Add(request);
        }

        _signal.Release();
        return request.Completion.Task;
    }

    public BitmapSource? TryGetCached(RenderKey key)
    {
        lock (_sync)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Bitmap;
            }
        }

        return null;
    }

    /// <summary>Largest cached full-page bitmap for a page, used as a placeholder while re-rendering.</summary>
    public BitmapSource? GetBestCached(Guid sourceId, int pageIndex, int rotation)
    {
        lock (_sync)
        {
            BitmapSource? best = null;
            foreach (var (key, bitmap) in _lru)
            {
                if (key.SourceId == sourceId && key.PageIndex == pageIndex && key.Rotation == (rotation & 3) && key.Clip is null
                    && (best is null || bitmap.PixelWidth > best.PixelWidth))
                {
                    best = bitmap;
                }
            }

            return best;
        }
    }

    /// <summary>Completes when no render is queued or running.</summary>
    public async Task WaitForIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!IsIdle && DateTime.UtcNow < deadline)
        {
            await Task.Delay(30).ConfigureAwait(false);
        }
    }

    public void ClearSource(Guid sourceId)
    {
        lock (_sync)
        {
            foreach (var node in _cacheMap.Where(kv => kv.Key.SourceId == sourceId).Select(kv => kv.Value).ToList())
            {
                _cacheBytes -= Bytes(node.Value.Key);
                _cacheMap.Remove(node.Value.Key);
                _lru.Remove(node);
            }
        }
    }

    private void WorkerLoop()
    {
        var buffer = Array.Empty<byte>();
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                _signal.Wait(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Request? request;
            lock (_sync)
            {
                request = TakeNext();
                if (request is null)
                {
                    continue;
                }

                _inFlight++;
            }

            try
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    request.Completion.TrySetResult(null);
                    continue;
                }

                if (TryGetCached(request.Key) is { } cached)
                {
                    request.Completion.TrySetResult(cached);
                    continue;
                }

                var bitmap = Render(request, ref buffer);
                if (bitmap is not null)
                {
                    AddToCache(request.Key, bitmap);
                }

                request.Completion.TrySetResult(bitmap);
            }
            catch (OperationCanceledException)
            {
                request.Completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                request.Completion.TrySetException(ex);
            }
            finally
            {
                bool idle;
                lock (_sync)
                {
                    _inFlight--;
                    idle = _queue.Count == 0 && _inFlight == 0;
                }

                if (idle)
                {
                    Idle?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    private Request? TakeNext()
    {
        // Drop cancelled requests, then pick the most urgent; within a priority prefer the newest.
        for (var i = _queue.Count - 1; i >= 0; i--)
        {
            if (_queue[i].CancellationToken.IsCancellationRequested)
            {
                _queue[i].Completion.TrySetResult(null);
                _queue.RemoveAt(i);
            }
        }

        Request? best = null;
        foreach (var r in _queue)
        {
            if (best is null || r.Priority < best.Priority || (r.Priority == best.Priority && r.Sequence > best.Sequence))
            {
                best = r;
            }
        }

        if (best is not null)
        {
            _queue.Remove(best);
        }

        return best;
    }

    private static BitmapSource? Render(Request request, ref byte[] buffer)
    {
        var key = request.Key;
        var width = key.Clip?.Width ?? key.PixelWidth;
        var height = key.Clip?.Height ?? key.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var stride = width * 4;
        var size = stride * height;
        if (buffer.Length < size)
        {
            buffer = GC.AllocateUninitializedArray<byte>(size, pinned: true);
        }

        using var page = request.Source.Document.OpenPage(key.PageIndex);
        unsafe
        {
            fixed (byte* p = buffer)
            {
                var target = new PdfRenderTarget((nint)p, width, height, stride);
                var flags = key.Annotations ? PdfRenderFlags.Annotations : PdfRenderFlags.None;
                var result = page.Render(
                    target,
                    -(key.Clip?.X ?? 0),
                    -(key.Clip?.Y ?? 0),
                    key.PixelWidth,
                    key.PixelHeight,
                    key.Rotation,
                    flags,
                    cancellationToken: request.CancellationToken);
                if (result != PdfRenderResult.Done)
                {
                    return null;
                }
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, buffer, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private void AddToCache(RenderKey key, BitmapSource bitmap)
    {
        lock (_sync)
        {
            if (_cacheMap.ContainsKey(key))
            {
                return;
            }

            _cacheMap[key] = _lru.AddFirst((key, bitmap));
            _cacheBytes += Bytes(key);
            while (_cacheBytes > CacheBudgetBytes && _lru.Last is { } last)
            {
                _cacheBytes -= Bytes(last.Value.Key);
                _cacheMap.Remove(last.Value.Key);
                _lru.RemoveLast();
            }
        }
    }

    private static long Bytes(RenderKey key) =>
        4L * (key.Clip?.Width ?? key.PixelWidth) * (key.Clip?.Height ?? key.PixelHeight);

    public void Dispose()
    {
        _shutdown.Cancel();
        lock (_sync)
        {
            foreach (var r in _queue)
            {
                r.Completion.TrySetResult(null);
            }

            _queue.Clear();
        }
    }

    private sealed class Request(RenderKey key, PdfSource source, RenderPriority priority, CancellationToken cancellationToken)
    {
        public RenderKey Key { get; } = key;

        public PdfSource Source { get; } = source;

        public RenderPriority Priority { get; } = priority;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public long Sequence { get; set; }

        public TaskCompletionSource<BitmapSource?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
