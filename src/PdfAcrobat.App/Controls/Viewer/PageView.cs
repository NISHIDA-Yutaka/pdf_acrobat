using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfAcrobat.App.Services;
using PdfAcrobat.Core.Documents;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.Controls.Viewer;

/// <summary>
/// One visible page in <see cref="PdfViewer"/>. Draws the rendered bitmap (plus a high-resolution
/// tile of the visible region at large zoom levels) and overlays such as text selection and search hits.
/// </summary>
internal sealed class PageView : FrameworkElement
{
    /// <summary>Above this size the full page is rendered at reduced resolution plus a sharp tile.</summary>
    private const long MaxFullPagePixels = 10_000_000;

    private static readonly Brush ShadowBrush1 = Freeze(new SolidColorBrush(Color.FromArgb(0x16, 0, 0, 0)));
    private static readonly Brush ShadowBrush2 = Freeze(new SolidColorBrush(Color.FromArgb(0x10, 0, 0, 0)));
    private static readonly Pen BorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x28, 0, 0, 0)), 1));

    private readonly PdfViewer _viewer;
    private BitmapSource? _bitmap;
    private (int Width, int Height) _requestedSize;
    private CancellationTokenSource? _renderCts;
    private BitmapSource? _detail;
    private Rect _detailRect;
    private Rect _requestedDetailRect;
    private CancellationTokenSource? _detailCts;

    public PageView(PdfViewer viewer)
    {
        _viewer = viewer;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
    }

    public int PageIndex { get; private set; } = -1;

    public PageRef? Page { get; private set; }

    public PdfSource? Source { get; private set; }

    /// <summary>Extra rotation (page rotation + view rotation) in quarter turns.</summary>
    public int DisplayRotation { get; private set; }

    public PageInfo? Info { get; private set; }

    public bool HasBitmap => _bitmap is not null;

    /// <summary>Geometry used for coordinate mapping (a synthetic box for blank pages).</summary>
    public PdfPageGeometry? Geometry => Info?.Geometry ?? (Page is { IsBlank: true, BlankSize: { } s } ? new PdfPageGeometry(new PdfRect(0, 0, s.Width, s.Height), 0) : null);

    /// <summary>Maps PDF user space to this element's local coordinates (DIPs).</summary>
    public PdfMatrix? DisplayMatrix => Geometry is { } g ? g.GetDisplayMatrix(0, 0, Width, Height, DisplayRotation) : null;

    public void Bind(int pageIndex, PageRef page, PdfSource? source, int displayRotation)
    {
        var sameContent = Page is not null
            && Page.SourceId == page.SourceId
            && Page.SourceIndex == page.SourceIndex
            && DisplayRotation == displayRotation;
        PageIndex = pageIndex;
        Page = page;
        Source = source;
        if (sameContent)
        {
            return;
        }

        DisplayRotation = displayRotation;
        CancelRenders();
        _bitmap = source is null ? null : AppServices.Render.GetBestCached(source.Id, page.SourceIndex, displayRotation);
        _requestedSize = default;
        _detail = null;
        _requestedDetailRect = Rect.Empty;
        Info = null;
        if (source is not null)
        {
            var infoTask = _viewer.InfoCache.GetAsync(source, page.SourceIndex);
            if (infoTask.IsCompletedSuccessfully)
            {
                Info = infoTask.Result;
            }
            else
            {
                var expected = page;
                infoTask.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && ReferenceEquals(Page, expected))
                    {
                        Info = t.Result;
                        InvalidateVisual();
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        InvalidateVisual();
    }

    public void Unbind()
    {
        CancelRenders();
        PageIndex = -1;
        Page = null;
        Source = null;
        Info = null;
        _bitmap = null;
        _detail = null;
    }

    private void CancelRenders()
    {
        _renderCts?.Cancel();
        _renderCts = null;
        _detailCts?.Cancel();
        _detailCts = null;
    }

    /// <summary>Requests bitmaps for the current size. <paramref name="visible"/> is the visible part in local DIPs.</summary>
    public void EnsureRendered(double dpiScale, Rect visible, bool isVisible)
    {
        if (Source is null || Page is null || Width <= 0 || Height <= 0)
        {
            return;
        }

        var fullWidth = Width * dpiScale;
        var fullHeight = Height * dpiScale;
        var factor = fullWidth * fullHeight > MaxFullPagePixels ? Math.Sqrt(MaxFullPagePixels / (fullWidth * fullHeight)) : 1.0;
        var size = ((int)Math.Round(fullWidth * factor), (int)Math.Round(fullHeight * factor));
        if (size != _requestedSize)
        {
            _requestedSize = size;
            _renderCts?.Cancel();
            var cts = _renderCts = new CancellationTokenSource();
            var task = AppServices.Render.RenderAsync(Source, Page.SourceIndex, DisplayRotation, size.Item1, size.Item2,
                isVisible ? RenderPriority.Visible : RenderPriority.Prefetch, cts.Token);
            Apply(task, cts, bitmap => _bitmap = bitmap);
        }

        if (factor >= 1.0 || !isVisible || visible.IsEmpty)
        {
            _detailCts?.Cancel();
            _detail = null;
            _requestedDetailRect = Rect.Empty;
            return;
        }

        // Sharp tile for the visible region, slightly larger than the viewport to absorb small scrolls.
        var tile = visible;
        tile.Inflate(visible.Width * 0.25, visible.Height * 0.25);
        tile.Intersect(new Rect(0, 0, Width, Height));
        if (tile.IsEmpty)
        {
            return;
        }

        var pixelTile = new PixelRect(
            (int)Math.Floor(tile.X * dpiScale),
            (int)Math.Floor(tile.Y * dpiScale),
            (int)Math.Ceiling(tile.Width * dpiScale),
            (int)Math.Ceiling(tile.Height * dpiScale));
        var tileRect = new Rect(pixelTile.X / dpiScale, pixelTile.Y / dpiScale, pixelTile.Width / dpiScale, pixelTile.Height / dpiScale);
        if (_requestedDetailRect.Contains(visible) && Math.Abs(_requestedDetailRect.Width - tileRect.Width) < 1)
        {
            return;
        }

        _requestedDetailRect = tileRect;
        _detailCts?.Cancel();
        var detailCts = _detailCts = new CancellationTokenSource();
        var detailTask = AppServices.Render.RenderAsync(Source, Page.SourceIndex, DisplayRotation,
            (int)Math.Round(fullWidth), (int)Math.Round(fullHeight), RenderPriority.Detail, detailCts.Token, pixelTile);
        Apply(detailTask, detailCts, bitmap =>
        {
            _detail = bitmap;
            _detailRect = tileRect;
        });
    }

    private void Apply(Task<BitmapSource?> task, CancellationTokenSource cts, Action<BitmapSource> assign)
    {
        if (task.IsCompletedSuccessfully)
        {
            if (task.Result is { } immediate)
            {
                assign(immediate);
                InvalidateVisual();
            }

            return;
        }

        task.ContinueWith(t =>
        {
            if (!cts.IsCancellationRequested && t.IsCompletedSuccessfully && t.Result is { } bitmap)
            {
                assign(bitmap);
                InvalidateVisual();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, Width, Height);
        if (rect.IsEmpty || Width <= 0)
        {
            return;
        }

        // Soft shadow and frame.
        dc.DrawRectangle(ShadowBrush2, null, new Rect(rect.X - 1, rect.Y, rect.Width + 2, rect.Height + 4));
        dc.DrawRectangle(ShadowBrush1, null, new Rect(rect.X, rect.Y + 1, rect.Width, rect.Height + 2));
        dc.DrawRectangle(Brushes.White, null, rect);
        if (_bitmap is not null)
        {
            dc.DrawImage(_bitmap, rect);
        }

        if (_detail is not null)
        {
            dc.DrawImage(_detail, _detailRect);
        }

        dc.DrawRectangle(null, BorderPen, rect);

        if (DisplayMatrix is not { } matrix)
        {
            return;
        }

        foreach (var (rects, isCurrent) in _viewer.GetSearchHighlights(PageIndex))
        {
            var brush = isCurrent ? _viewer.CurrentSearchHitBrush : _viewer.SearchHitBrush;
            foreach (var r in rects)
            {
                dc.DrawRectangle(brush, null, ToLocal(matrix, r.Inflate(0.5, 0.5)));
            }
        }

        if (Info is { } info)
        {
            foreach (var r in _viewer.GetSelectionRects(PageIndex, info.Text))
            {
                dc.DrawRectangle(_viewer.SelectionBrush, null, ToLocal(matrix, r));
            }
        }
    }

    public static Rect ToLocal(PdfMatrix matrix, PdfRect r)
    {
        var bounds = matrix.TransformBounds(r);
        return new Rect(bounds.Left, bounds.Bottom, bounds.Width, bounds.Height);
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
