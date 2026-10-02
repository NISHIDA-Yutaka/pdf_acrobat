using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PdfAcrobat.App.Services;
using PdfAcrobat.Core.Documents;
using PdfAcrobat.Core.Text;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.Controls.Viewer;

/// <summary>
/// Scrollable, virtualized PDF page viewer. Only pages near the viewport get a <see cref="PageView"/>;
/// layout, zoom and navigation are computed here, input handling lives in PdfViewer.Input.cs.
/// </summary>
public sealed partial class PdfViewer : UserControl
{
    public const double MinZoom = 0.08;
    public const double MaxZoom = 64;

    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(
        nameof(Session), typeof(DocumentSession), typeof(PdfViewer), new PropertyMetadata(null, (d, e) => ((PdfViewer)d).OnSessionChanged((DocumentSession?)e.OldValue, (DocumentSession?)e.NewValue)));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(PdfViewer),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((PdfViewer)d).OnZoomChanged(), (_, v) => Math.Clamp((double)v, MinZoom, MaxZoom)));

    public static readonly DependencyProperty ZoomModeProperty = DependencyProperty.Register(
        nameof(ZoomMode), typeof(ZoomMode), typeof(PdfViewer),
        new FrameworkPropertyMetadata(ZoomMode.FitWidth, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((PdfViewer)d).OnZoomModeChanged()));

    public static readonly DependencyProperty LayoutModeProperty = DependencyProperty.Register(
        nameof(LayoutMode), typeof(PageLayoutMode), typeof(PdfViewer), new PropertyMetadata(PageLayoutMode.Continuous, (d, _) => ((PdfViewer)d).OnLayoutModeChanged()));

    public static readonly DependencyProperty CoverPageProperty = DependencyProperty.Register(
        nameof(CoverPage), typeof(bool), typeof(PdfViewer), new PropertyMetadata(true, (d, _) => ((PdfViewer)d).OnLayoutModeChanged()));

    public static readonly DependencyProperty ViewRotationProperty = DependencyProperty.Register(
        nameof(ViewRotation), typeof(int), typeof(PdfViewer), new PropertyMetadata(0, (d, _) => ((PdfViewer)d).OnLayoutModeChanged()));

    public static readonly DependencyProperty CurrentPageIndexProperty = DependencyProperty.Register(
        nameof(CurrentPageIndex), typeof(int), typeof(PdfViewer),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((PdfViewer)d).OnCurrentPageIndexChanged((int)e.NewValue)));

    public static readonly DependencyProperty ToolProperty = DependencyProperty.Register(
        nameof(Tool), typeof(ViewerTool), typeof(PdfViewer), new PropertyMetadata(ViewerTool.Select, (d, _) => ((PdfViewer)d).UpdateIdleCursor()));

    public static readonly DependencyProperty SearchHitsProperty = DependencyProperty.Register(
        nameof(SearchHits), typeof(IReadOnlyList<SearchHit>), typeof(PdfViewer), new PropertyMetadata(null, (d, _) => ((PdfViewer)d).OnSearchHitsChanged()));

    public static readonly DependencyProperty CurrentSearchHitProperty = DependencyProperty.Register(
        nameof(CurrentSearchHit), typeof(SearchHit), typeof(PdfViewer), new PropertyMetadata(null, (d, _) => ((PdfViewer)d).OnCurrentSearchHitChanged()));

    private readonly ScrollViewer _scroll;
    private readonly Canvas _canvas;
    private readonly Dictionary<int, PageView> _views = new();
    private readonly Stack<PageView> _pool = new();
    private readonly DispatcherTimer _renderTimer;
    private ViewerLayout _layout = ViewerLayout.Empty;
    private Size[] _pageSizes = [];
    private Dictionary<int, List<SearchHit>> _hitsByPage = new();
    private bool _syncingCurrentPage;
    private bool _settingZoom;
    private double _dpi = 1.0;

    public PdfViewer()
    {
        _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = false };
        _scroll = new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = false,
            Focusable = false,
            PanningMode = PanningMode.Both,
        };
        Content = _scroll;
        Focusable = true;
        FocusVisualStyle = null;
        SetResourceReference(BackgroundProperty, "CanvasBackgroundBrush");
        UseLayoutRounding = true;

        _scroll.ScrollChanged += OnScrollChanged;
        SizeChanged += (_, _) => OnViewportSizeChanged();
        Loaded += (_, _) =>
        {
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            RefreshBrushes();
            Relayout(null);
        };
        ThemeService.ThemeChanged += (_, _) => Dispatcher.BeginInvoke(RefreshBrushes);
        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Background, (_, _) =>
        {
            _renderTimer!.Stop();
            UpdateVisiblePages(render: true);
        }, Dispatcher)
        { IsEnabled = false };

        InitializeInput();
    }

    /// <summary>Raised when the user activates a link to an external URI.</summary>
    public event EventHandler<string>? ExternalLinkRequested;

    /// <summary>Raised when the text selection changes.</summary>
    public event EventHandler? SelectionChanged;

    public DocumentSession? Session
    {
        get => (DocumentSession?)GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public ZoomMode ZoomMode
    {
        get => (ZoomMode)GetValue(ZoomModeProperty);
        set => SetValue(ZoomModeProperty, value);
    }

    public PageLayoutMode LayoutMode
    {
        get => (PageLayoutMode)GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    public bool CoverPage
    {
        get => (bool)GetValue(CoverPageProperty);
        set => SetValue(CoverPageProperty, value);
    }

    /// <summary>Temporary view rotation in quarter turns (not saved to the file).</summary>
    public int ViewRotation
    {
        get => (int)GetValue(ViewRotationProperty);
        set => SetValue(ViewRotationProperty, value);
    }

    public int CurrentPageIndex
    {
        get => (int)GetValue(CurrentPageIndexProperty);
        set => SetValue(CurrentPageIndexProperty, value);
    }

    public ViewerTool Tool
    {
        get => (ViewerTool)GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    public IReadOnlyList<SearchHit>? SearchHits
    {
        get => (IReadOnlyList<SearchHit>?)GetValue(SearchHitsProperty);
        set => SetValue(SearchHitsProperty, value);
    }

    public SearchHit? CurrentSearchHit
    {
        get => (SearchHit?)GetValue(CurrentSearchHitProperty);
        set => SetValue(CurrentSearchHitProperty, value);
    }

    internal PageInfoCache InfoCache => AppServices.PageInfo;

    internal Brush SelectionBrush { get; private set; } = Brushes.Transparent;

    internal Brush SearchHitBrush { get; private set; } = Brushes.Transparent;

    internal Brush CurrentSearchHitBrush { get; private set; } = Brushes.Transparent;

    private bool IsContinuous => LayoutMode is PageLayoutMode.Continuous or PageLayoutMode.TwoPageContinuous;

    private double ViewportWidth => _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : Math.Max(0, ActualWidth);

    private double ViewportHeight => _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : Math.Max(0, ActualHeight);

    private void RefreshBrushes()
    {
        SelectionBrush = TryFindResource("SelectionHighlightBrush") as Brush ?? new SolidColorBrush(Color.FromArgb(0x4D, 0x33, 0x90, 0xFF));
        SearchHitBrush = TryFindResource("SearchHitBrush") as Brush ?? new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xD4, 0));
        CurrentSearchHitBrush = TryFindResource("CurrentSearchHitBrush") as Brush ?? new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0x7A, 0));
        InvalidatePages();
    }

    // ---- Property change handlers -------------------------------------------

    private void OnSessionChanged(DocumentSession? oldSession, DocumentSession? newSession)
    {
        if (oldSession is not null)
        {
            oldSession.Changed -= OnSessionStateChanged;
        }

        foreach (var view in _views.Values)
        {
            view.Unbind();
            _pool.Push(view);
            view.Visibility = Visibility.Collapsed;
        }

        _views.Clear();
        ClearSelection();
        if (newSession is not null)
        {
            newSession.Changed += OnSessionStateChanged;
        }

        Relayout(null);
        GoToPage(Math.Clamp(CurrentPageIndex, 0, Math.Max(0, (newSession?.Pages.Count ?? 1) - 1)));
    }

    private void OnSessionStateChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (ReferenceEquals(e.OldState.Pages, e.NewState.Pages))
        {
            return;
        }

        // Keep the page that was current, following it by id if it moved.
        var currentId = CurrentPageIndex < e.OldState.Pages.Count ? e.OldState.Pages[CurrentPageIndex].Id : Guid.Empty;
        var newIndex = e.NewState.IndexOf(currentId);
        ClearSelection();
        foreach (var view in _views.Values)
        {
            view.Unbind();
            _pool.Push(view);
            view.Visibility = Visibility.Collapsed;
        }

        _views.Clear();
        var target = newIndex >= 0 ? newIndex : Math.Clamp(CurrentPageIndex, 0, Math.Max(0, e.NewState.Pages.Count - 1));
        SyncCurrentPage(target);
        Relayout(null);
        GoToPage(target);
    }

    private void OnZoomChanged()
    {
        if (_settingZoom)
        {
            return;
        }

        Relayout(CaptureAnchor(new Point(ViewportWidth / 2, ViewportHeight / 2)), deferRender: true);
    }

    private void OnZoomModeChanged()
    {
        if (ZoomMode == ZoomMode.Custom)
        {
            return;
        }

        Relayout(CaptureAnchor(new Point(0, 0)));
        if (ZoomMode == ZoomMode.FitPage)
        {
            GoToPage(CurrentPageIndex);
        }
    }

    private void OnLayoutModeChanged()
    {
        var page = CurrentPageIndex;
        Relayout(null);
        GoToPage(page);
    }

    private void OnCurrentPageIndexChanged(int index)
    {
        if (_syncingCurrentPage)
        {
            return;
        }

        GoToPage(index);
    }

    private void OnViewportSizeChanged()
    {
        if (!IsLoaded)
        {
            return;
        }

        Relayout(CaptureAnchor(new Point(0, 0)));
    }

    private void OnSearchHitsChanged()
    {
        _hitsByPage = (SearchHits ?? [])
            .GroupBy(h => h.PageIndex)
            .ToDictionary(g => g.Key, g => g.ToList());
        InvalidatePages();
    }

    private void OnCurrentSearchHitChanged()
    {
        if (CurrentSearchHit is { } hit)
        {
            ScrollIntoView(hit.PageIndex, hit.Rects);
        }

        InvalidatePages();
    }

    internal IEnumerable<(IReadOnlyList<PdfRect> Rects, bool IsCurrent)> GetSearchHighlights(int pageIndex)
    {
        if (!_hitsByPage.TryGetValue(pageIndex, out var hits))
        {
            yield break;
        }

        var current = CurrentSearchHit;
        foreach (var hit in hits)
        {
            yield return (hit.Rects, current is not null && hit.PageIndex == current.PageIndex && hit.Range == current.Range);
        }
    }

    private void InvalidatePages()
    {
        foreach (var view in _views.Values)
        {
            view.InvalidateVisual();
        }
    }

    // ---- Zoom ----------------------------------------------------------------

    public void ZoomIn() => SetZoomAt(NextZoomStep(Zoom, +1), null);

    public void ZoomOut() => SetZoomAt(NextZoomStep(Zoom, -1), null);

    private static readonly double[] ZoomSteps =
        [0.08, 0.125, 0.25, 0.33, 0.5, 0.667, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0, 12.0, 16.0, 24.0, 32.0, 64.0];

    public static double NextZoomStep(double zoom, int direction)
    {
        if (direction > 0)
        {
            return ZoomSteps.FirstOrDefault(z => z > zoom + 1e-3, MaxZoom);
        }

        return ZoomSteps.LastOrDefault(z => z < zoom - 1e-3, MinZoom);
    }

    /// <summary>Changes the zoom keeping the content under <paramref name="viewportPoint"/> fixed.</summary>
    internal void SetZoomAt(double zoom, Point? viewportPoint)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var anchor = CaptureAnchor(viewportPoint ?? new Point(ViewportWidth / 2, ViewportHeight / 2));
        _settingZoom = true;
        try
        {
            SetCurrentValue(ZoomModeProperty, ZoomMode.Custom);
            SetCurrentValue(ZoomProperty, zoom);
        }
        finally
        {
            _settingZoom = false;
        }

        Relayout(anchor, deferRender: true);
    }

    private double? _pendingZoom;

    /// <summary>Pushes a zoom computed by a fit mode to the bound property after the current binding work.</summary>
    private void PublishZoom(double zoom)
    {
        var scheduled = _pendingZoom is not null;
        _pendingZoom = zoom;
        if (scheduled)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_pendingZoom is not { } value)
            {
                return;
            }

            _pendingZoom = null;
            _settingZoom = true;
            try
            {
                SetCurrentValue(ZoomProperty, value);
            }
            finally
            {
                _settingZoom = false;
            }
        }, DispatcherPriority.DataBind);
    }

    private double ComputeFitZoom()
    {
        if (_pageSizes.Length == 0)
        {
            return Zoom;
        }

        var availableWidth = Math.Max(50, ViewportWidth - 2 * ViewerLayout.Margin);
        var availableHeight = Math.Max(50, ViewportHeight - 2 * ViewerLayout.Margin);
        var spread = LayoutMode is PageLayoutMode.TwoPage or PageLayoutMode.TwoPageContinuous;
        var rows = ViewerLayout.BuildRows(_pageSizes.Length, LayoutMode, CoverPage);

        double RowWidthPt(int[] row) => spread ? 2 * row.Max(i => _pageSizes[i].Width) : _pageSizes[row[0]].Width;

        double RowHeightPt(int[] row) => row.Max(i => _pageSizes[i].Height);

        // Like Acrobat, fit to the page (or spread) the user is looking at.
        var current = rows[ViewerLayout.RowOf(rows, Math.Clamp(CurrentPageIndex, 0, _pageSizes.Length - 1))];
        var fitWidth = availableWidth / (RowWidthPt(current) * ViewerLayout.PointsToDip);
        if (ZoomMode == ZoomMode.FitWidth)
        {
            return Math.Clamp(fitWidth, MinZoom, MaxZoom);
        }

        var fitHeight = availableHeight / (RowHeightPt(current) * ViewerLayout.PointsToDip);
        return Math.Clamp(Math.Min(fitWidth, fitHeight), MinZoom, MaxZoom);
    }

    // ---- Layout & virtualization ----------------------------------------------

    private readonly record struct Anchor(int PageIndex, double FracX, double FracY, Point ViewportPoint);

    private Anchor? CaptureAnchor(Point viewportPoint)
    {
        if (_layout.PageRects.Length == 0)
        {
            return null;
        }

        var canvasPoint = new Point(viewportPoint.X + _scroll.HorizontalOffset, viewportPoint.Y + _scroll.VerticalOffset);
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _layout.PageRects.Length; i++)
        {
            var r = _layout.PageRects[i];
            if (r.IsEmpty)
            {
                continue;
            }

            if (r.Contains(canvasPoint))
            {
                best = i;
                break;
            }

            var dy = canvasPoint.Y < r.Top ? r.Top - canvasPoint.Y : Math.Max(0, canvasPoint.Y - r.Bottom);
            var dx = canvasPoint.X < r.Left ? r.Left - canvasPoint.X : Math.Max(0, canvasPoint.X - r.Right);
            var distance = dy * 4 + dx;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        if (best < 0)
        {
            return null;
        }

        var rect = _layout.PageRects[best];
        return new Anchor(best, (canvasPoint.X - rect.X) / rect.Width, (canvasPoint.Y - rect.Y) / rect.Height, viewportPoint);
    }

    private void Relayout(Anchor? anchor, bool deferRender = false)
    {
        var session = Session;
        if (session is null || session.Pages.Count == 0)
        {
            _layout = ViewerLayout.Empty;
            _pageSizes = [];
            _canvas.Width = 0;
            _canvas.Height = 0;
            UpdateVisiblePages(render: false);
            return;
        }

        var swap = (ViewRotation & 1) == 1;
        _pageSizes = session.Pages
            .Select(p =>
            {
                var s = session.GetPageSize(p);
                return swap ? new Size(s.Height, s.Width) : new Size(s.Width, s.Height);
            })
            .ToArray();

        // Fit modes use the computed zoom right away; the bound property is updated afterwards so that
        // the change is not lost when it happens in the middle of a binding transfer.
        var zoom = Zoom;
        if (ZoomMode != ZoomMode.Custom && IsLoaded && ViewportWidth > 0)
        {
            var fit = ComputeFitZoom();
            if (Math.Abs(fit - zoom) > 1e-4)
            {
                zoom = fit;
                PublishZoom(fit);
            }
        }

        _layout = ViewerLayout.Compute(_pageSizes, zoom, LayoutMode, CoverPage, ViewportWidth, CurrentPageIndex);
        _canvas.Width = _layout.Extent.Width;
        _canvas.Height = Math.Max(_layout.Extent.Height, ViewportHeight);
        _scroll.UpdateLayout();

        if (anchor is { } a && a.PageIndex < _layout.PageRects.Length && !_layout.PageRects[a.PageIndex].IsEmpty)
        {
            var rect = _layout.PageRects[a.PageIndex];
            ScrollToOffset(rect.X + a.FracX * rect.Width - a.ViewportPoint.X, rect.Y + a.FracY * rect.Height - a.ViewportPoint.Y);
        }

        // Pages narrower than the viewport stay centred even when wider pages make the canvas wider.
        if (CurrentRowBounds() is { } current && current.Width <= ViewportWidth)
        {
            ScrollToOffset(current.X + current.Width / 2 - ViewportWidth / 2, _scroll.VerticalOffset);
        }

        UpdateVisiblePages(render: !deferRender);
        if (deferRender)
        {
            _renderTimer.Stop();
            _renderTimer.Start();
        }
    }

    /// <summary>Union of the page rectangles in the row containing <paramref name="pageIndex"/>.</summary>
    private Rect? RowBounds(int pageIndex)
    {
        if (_layout.Rows.Count == 0 || _layout.PageRects.Length == 0)
        {
            return null;
        }

        var row = _layout.Rows[ViewerLayout.RowOf(_layout.Rows, Math.Clamp(pageIndex, 0, _layout.PageRects.Length - 1))];
        var rects = row.Select(i => _layout.PageRects[i]).Where(r => !r.IsEmpty).ToList();
        return rects.Count == 0 ? null : rects.Aggregate(Rect.Union);
    }

    private Rect? CurrentRowBounds() => RowBounds(CurrentPageIndex);

    /// <summary>Diagnostics for automation logs.</summary>
    internal string DescribeLayout()
    {
        var rects = string.Join(" ", _views.OrderBy(v => v.Key).Select(v => $"#{v.Key}@({Canvas.GetLeft(v.Value):0},{Canvas.GetTop(v.Value):0} {v.Value.Width:0}x{v.Value.Height:0})"));
        return $"viewer {ActualWidth:0}x{ActualHeight:0}, viewport {_scroll.ViewportWidth:0}x{_scroll.ViewportHeight:0}, " +
               $"extent {_scroll.ExtentWidth:0}x{_scroll.ExtentHeight:0}, canvas {_canvas.Width:0}x{_canvas.Height:0}, " +
               $"offset ({_scroll.HorizontalOffset:0},{_scroll.VerticalOffset:0}), zoom {Zoom:0.###} scale {_layout.Scale:0.###}, dpi {_dpi}, views {rects}";
    }

    private void ScrollToOffset(double x, double y)
    {
        var maxX = Math.Max(0, _canvas.Width - ViewportWidth);
        var maxY = Math.Max(0, _canvas.Height - ViewportHeight);
        x = Math.Round(Math.Clamp(x, 0, maxX) * _dpi) / _dpi;
        y = Math.Round(Math.Clamp(y, 0, maxY) * _dpi) / _dpi;
        _scroll.ScrollToHorizontalOffset(x);
        _scroll.ScrollToVerticalOffset(y);
        _scroll.UpdateLayout();
    }

    private Rect Snap(Rect r) => new(
        Math.Round(r.X * _dpi) / _dpi,
        Math.Round(r.Y * _dpi) / _dpi,
        Math.Max(1, Math.Round(r.Width * _dpi)) / _dpi,
        Math.Max(1, Math.Round(r.Height * _dpi)) / _dpi);

    private Rect Viewport => new(_scroll.HorizontalOffset, _scroll.VerticalOffset, ViewportWidth, ViewportHeight);

    private void UpdateVisiblePages(bool render)
    {
        var session = Session;
        var viewport = Viewport;
        var prefetch = viewport;
        prefetch.Inflate(0, viewport.Height * 0.75);

        var needed = new List<int>();
        for (var i = 0; i < _layout.PageRects.Length; i++)
        {
            var r = _layout.PageRects[i];
            if (!r.IsEmpty && r.IntersectsWith(prefetch))
            {
                needed.Add(i);
            }
        }

        foreach (var index in _views.Keys.Except(needed).ToList())
        {
            var view = _views[index];
            view.Unbind();
            view.Visibility = Visibility.Collapsed;
            _views.Remove(index);
            _pool.Push(view);
        }

        if (session is null)
        {
            return;
        }

        foreach (var index in needed)
        {
            if (!_views.TryGetValue(index, out var view))
            {
                view = _pool.Count > 0 ? _pool.Pop() : new PageView(this);
                if (view.Parent is null)
                {
                    _canvas.Children.Add(view);
                }

                view.Visibility = Visibility.Visible;
                _views[index] = view;
            }

            var page = session.Pages[index];
            var source = page.IsBlank ? null : session.GetSource(page.SourceId!.Value);
            var rect = Snap(_layout.PageRects[index]);
            if (view.Width != rect.Width || view.Height != rect.Height)
            {
                view.Width = rect.Width;
                view.Height = rect.Height;
                view.InvalidateVisual();
            }

            Canvas.SetLeft(view, rect.X);
            Canvas.SetTop(view, rect.Y);
            view.Bind(index, page, source, (page.Rotation + ViewRotation) & 3);

            var visible = Rect.Intersect(rect, viewport);
            var isVisible = !visible.IsEmpty;
            if (isVisible)
            {
                visible.Offset(-rect.X, -rect.Y);
            }

            // New pages render at once; existing ones wait for the debounce timer while zooming.
            if (render || view.Source is not null && !view.HasBitmap)
            {
                view.EnsureRendered(_dpi, visible, isVisible);
            }
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateVisiblePages(render: true);
        if (IsContinuous)
        {
            SyncCurrentPage(FindCurrentPage());
        }

    }

    private int FindCurrentPage()
    {
        var viewport = Viewport;
        var best = CurrentPageIndex;
        var bestArea = 0.0;
        foreach (var (index, _) in _views)
        {
            var visible = Rect.Intersect(_layout.PageRects[index], viewport);
            if (visible.IsEmpty)
            {
                continue;
            }

            var area = visible.Width * visible.Height;
            if (area > bestArea + 1 || (Math.Abs(area - bestArea) <= 1 && index < best))
            {
                best = index;
                bestArea = area;
            }
        }

        return best;
    }

    private void SyncCurrentPage(int index)
    {
        if (index == CurrentPageIndex)
        {
            return;
        }

        _syncingCurrentPage = true;
        try
        {
            SetCurrentValue(CurrentPageIndexProperty, index);
        }
        finally
        {
            _syncingCurrentPage = false;
        }
    }

    // ---- Navigation ----------------------------------------------------------

    /// <summary>Scrolls so that the top of the page is at the top of the viewport.</summary>
    public void GoToPage(int index)
    {
        if (_layout.PageRects.Length == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, _layout.PageRects.Length - 1);
        SyncCurrentPage(index);
        if (!IsContinuous || _layout.PageRects[index].IsEmpty)
        {
            Relayout(null);
        }

        var rect = _layout.PageRects[index];
        if (rect.IsEmpty)
        {
            return;
        }

        // Centre the page (or spread) horizontally when it fits, otherwise show its left edge.
        var rowRect = RowBounds(index) ?? rect;
        var x = rowRect.Width <= ViewportWidth ? rowRect.X + rowRect.Width / 2 - ViewportWidth / 2 : rect.X - ViewerLayout.Margin;
        ScrollToOffset(x, IsContinuous ? rect.Y - ViewerLayout.Margin / 2 : 0);
        UpdateVisiblePages(render: true);
    }

    /// <summary>Navigates to a destination, honouring its position inside the page when known.</summary>
    public async void GoToDestination(PdfDestination destination, Guid? sourceId = null)
    {
        var session = Session;
        if (session is null || destination.PageIndex < 0)
        {
            return;
        }

        // Destinations refer to source pages; map to the current page order.
        var target = FindPageForSourceIndex(sourceId ?? session.PrimarySource.Id, destination.PageIndex);
        if (target < 0)
        {
            return;
        }

        GoToPage(target);
        if (destination.X is null && destination.Y is null)
        {
            return;
        }

        var page = session.Pages[target];
        if (page.IsBlank)
        {
            return;
        }

        var info = await InfoCache.GetAsync(session.GetSource(page.SourceId!.Value), page.SourceIndex);
        var rect = Snap(_layout.PageRects[target]);
        if (rect.IsEmpty)
        {
            return;
        }

        var matrix = info.Geometry.GetDisplayMatrix(0, 0, rect.Width, rect.Height, (page.Rotation + ViewRotation) & 3);
        var x = destination.X ?? info.Geometry.BBox.Left;
        var y = destination.Y ?? info.Geometry.BBox.Top;
        var local = matrix.Transform(x, y);
        var targetX = destination.X is null || rect.Width <= ViewportWidth ? _scroll.HorizontalOffset : rect.X + local.X - 8;
        ScrollToOffset(targetX, rect.Y + local.Y - 8);
    }

    private int FindPageForSourceIndex(Guid sourceId, int sourceIndex)
    {
        var session = Session;
        if (session is null)
        {
            return -1;
        }

        return session.Pages.FindIndex(p => p.SourceId == sourceId && p.SourceIndex == sourceIndex);
    }

    /// <summary>Scrolls the minimum amount needed to show the given rectangles of a page.</summary>
    private void ScrollIntoView(int pageIndex, IReadOnlyList<PdfRect> rects)
    {
        if (pageIndex < 0 || pageIndex >= _layout.PageRects.Length)
        {
            return;
        }

        if (_layout.PageRects[pageIndex].IsEmpty)
        {
            GoToPage(pageIndex);
        }

        var session = Session;
        var page = session?.Pages[pageIndex];
        if (session is null || page is null || page.IsBlank)
        {
            return;
        }

        var source = session.GetSource(page.SourceId!.Value);
        var info = InfoCache.TryGet(source, page.SourceIndex);
        if (info is null)
        {
            _ = InfoCache.GetAsync(source, page.SourceIndex).ContinueWith(
                _ => ScrollIntoView(pageIndex, rects), TaskScheduler.FromCurrentSynchronizationContext());
            GoToPage(pageIndex);
            return;
        }

        var pageRect = Snap(_layout.PageRects[pageIndex]);
        var matrix = info.Geometry.GetDisplayMatrix(0, 0, pageRect.Width, pageRect.Height, (page.Rotation + ViewRotation) & 3);
        var target = Rect.Empty;
        foreach (var r in rects)
        {
            var local = PageView.ToLocal(matrix, r);
            local.Offset(pageRect.X, pageRect.Y);
            target.Union(local);
        }

        if (target.IsEmpty)
        {
            GoToPage(pageIndex);
            return;
        }

        var viewport = Viewport;
        var margin = Math.Min(80, viewport.Height / 4);
        var x = _scroll.HorizontalOffset;
        var y = _scroll.VerticalOffset;
        if (target.Top < viewport.Top + 8 || target.Bottom > viewport.Bottom - 8)
        {
            y = target.Top - margin;
        }

        if (target.Left < viewport.Left || target.Right > viewport.Right)
        {
            x = target.Left - margin;
        }

        ScrollToOffset(x, y);
    }

    public void NextPage() => GoToPage(StepPage(+1));

    public void PreviousPage() => GoToPage(StepPage(-1));

    private int StepPage(int direction)
    {
        if (_layout.Rows.Count == 0)
        {
            return 0;
        }

        var row = ViewerLayout.RowOf(_layout.Rows, CurrentPageIndex);
        var next = Math.Clamp(row + direction, 0, _layout.Rows.Count - 1);
        return _layout.Rows[next][0];
    }
}
