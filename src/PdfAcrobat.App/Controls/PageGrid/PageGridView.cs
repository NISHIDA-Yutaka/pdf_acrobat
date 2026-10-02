using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PdfAcrobat.App.ViewModels;
using PdfAcrobat.Core.Documents;
using PdfAcrobat.Core.Import;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace PdfAcrobat.App.Controls.PageGrid;

/// <summary>
/// The "organize pages" grid: virtualized page thumbnails with multi-selection (click, Ctrl, Shift,
/// rubber band, keyboard), drag &amp; drop reordering, files dropped from Explorer, an insert button
/// between pages and per-page hover actions. Edits go through <see cref="DocumentViewModel"/>, so
/// they are undoable.
/// </summary>
public sealed class PageGridView : UserControl
{
    public const double MinTileWidth = 80;
    public const double MaxTileWidth = 360;

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(DocumentViewModel), typeof(PageGridView),
        new PropertyMetadata(null, (d, e) => ((PageGridView)d).OnDocumentChanged((DocumentViewModel?)e.OldValue, (DocumentViewModel?)e.NewValue)));

    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(
        nameof(TileWidth), typeof(double), typeof(PageGridView),
        new FrameworkPropertyMetadata(150.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((PageGridView)d).Relayout(), (_, v) => Math.Clamp((double)v, MinTileWidth, MaxTileWidth)));

    private const double PageAspect = 1.414;
    private const double CellPadding = 10;
    private const double LabelHeight = 26;
    private const double HorizontalGap = 26;
    private const double VerticalGap = 10;
    private const double SideMargin = 24;
    private const double TopMargin = 16;
    private const double BottomMargin = 56;
    private const double AutoScrollZone = 44;

    private readonly ScrollViewer _scroll;
    private readonly Canvas _canvas;
    private readonly Dictionary<int, PageTile> _tiles = new();
    private readonly Stack<PageTile> _pool = new();
    private readonly Grid _marker;
    private readonly Border _markerButton;
    private readonly Rectangle _marquee;
    private readonly Border _ghost;
    private readonly Image _ghostImage;
    private readonly TextBlock _ghostCount;
    private readonly DispatcherTimer _autoScroll;
    private DocumentSession? _session;
    private HashSet<int> _selection = new();
    private int _columns = 1;
    private double _left;
    private double _dpi = 1;
    private int _anchor = -1;
    private int _focus = -1;

    // Pointer state
    private Point _pressPoint;
    private int _pressIndex = -1;
    private int _selectOnlyOnRelease = -1;
    private bool _pressed;
    private bool _dragging;
    private bool _selecting;
    private HashSet<int> _marqueeBase = new();
    private Point _lastViewportPoint;
    private int _markerGap = -1;

    public PageGridView()
    {
        _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
        _scroll = new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = false,
            Focusable = false,
        };
        Content = _scroll;
        Focusable = true;
        FocusVisualStyle = null;
        AllowDrop = true;
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "CanvasBackgroundBrush");

        // Insertion marker: a vertical line with a "+" button between two pages.
        var line = new Rectangle { Width = 3, RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Center };
        line.SetResourceReference(Shape.FillProperty, "ThumbnailSelectedBorderBrush");
        _markerButton = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip = "ここにページを挿入",
            Child = new SymbolIcon { Symbol = SymbolRegular.Add24, FontSize = 15, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        _markerButton.SetResourceReference(Border.BackgroundProperty, "ThumbnailSelectedBorderBrush");
        _markerButton.MouseLeftButtonDown += OnMarkerButtonDown;
        _marker = new Grid { Width = 28, Visibility = Visibility.Collapsed };
        _marker.Children.Add(line);
        _marker.Children.Add(_markerButton);
        Panel.SetZIndex(_marker, 10);

        _marquee = new Rectangle { StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _marquee.SetResourceReference(Shape.StrokeProperty, "ThumbnailSelectedBorderBrush");
        _marquee.SetResourceReference(Shape.FillProperty, "SelectionHighlightBrush");
        Panel.SetZIndex(_marquee, 11);

        _ghostImage = new Image { Stretch = Stretch.Uniform, MaxWidth = 90, MaxHeight = 120 };
        _ghostCount = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(7, 2, 7, 3) };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -8, -10, 0),
            Child = _ghostCount,
        };
        badge.SetResourceReference(Border.BackgroundProperty, "ThumbnailSelectedBorderBrush");
        var ghostContent = new Grid();
        ghostContent.Children.Add(new Border { Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _ghostImage, MinWidth = 40, MinHeight = 50 });
        ghostContent.Children.Add(badge);
        _ghost = new Border { Child = ghostContent, Opacity = 0.85, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Panel.SetZIndex(_ghost, 12);

        _canvas.Children.Add(_marker);
        _canvas.Children.Add(_marquee);
        _canvas.Children.Add(_ghost);

        _scroll.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0)
            {
                Relayout();
            }
            else if (e.VerticalChange != 0 || e.ViewportHeightChange != 0)
            {
                UpdateVisibleTiles(rebind: false);
            }
        };
        Loaded += (_, _) =>
        {
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Relayout();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Relayout();
                Dispatcher.BeginInvoke(() =>
                {
                    ScrollIntoView(_focus >= 0 ? _focus : Document?.CurrentPageIndex ?? 0);
                    Focus();
                }, DispatcherPriority.Loaded);
            }
            else
            {
                HideMarker();
            }
        };
        _autoScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, OnAutoScrollTick, Dispatcher) { IsEnabled = false };
    }

    public DocumentViewModel? Document
    {
        get => (DocumentViewModel?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>Width of a page thumbnail in DIPs (the grid zoom).</summary>
    public double TileWidth
    {
        get => (double)GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    /// <summary>True while dragging pages or selecting with the rubber band (hover actions are hidden).</summary>
    internal bool IsPointerBusy => _dragging || _selecting;

    internal int Columns => _columns;

    private int PageCount => _session?.Pages.Count ?? 0;

    private Size CellSize => new(TileWidth + 2 * CellPadding, Math.Round(TileWidth * PageAspect) + 2 * CellPadding + LabelHeight);

    private Rect BoxRect => new(CellPadding, CellPadding, TileWidth, Math.Round(TileWidth * PageAspect));

    // ---- Document binding ----------------------------------------------------------------

    private void OnDocumentChanged(DocumentViewModel? oldDocument, DocumentViewModel? newDocument)
    {
        if (oldDocument is not null)
        {
            oldDocument.PropertyChanged -= OnDocumentPropertyChanged;
        }

        if (_session is not null)
        {
            _session.Changed -= OnSessionChanged;
        }

        RecycleAll();
        _session = newDocument?.Session;
        if (newDocument is not null)
        {
            newDocument.PropertyChanged += OnDocumentPropertyChanged;
            _session!.Changed += OnSessionChanged;
            _selection = newDocument.SelectedPages.ToHashSet();
            _focus = newDocument.CurrentPageIndex;
        }

        Relayout();
    }

    private void OnSessionChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OldState.Pages, e.NewState.Pages))
        {
            _focus = Math.Min(_focus, PageCount - 1);
            _anchor = Math.Min(_anchor, PageCount - 1);
            Relayout();
            UpdateVisibleTiles(rebind: true);
        }
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.SelectedPages) && Document is { } document)
        {
            _selection = document.SelectedPages.ToHashSet();
            if (_selection.Count > 0 && !_selection.Contains(_focus))
            {
                _focus = document.SelectedPages[^1];
                _anchor = document.SelectedPages[0];
                if (IsVisible)
                {
                    ScrollIntoView(_focus);
                }
            }

            foreach (var (index, tile) in _tiles)
            {
                tile.IsSelected = _selection.Contains(index);
            }
        }
    }

    private void SetSelection(IEnumerable<int> pages)
    {
        if (Document is { } document)
        {
            var list = pages.Where(i => i >= 0 && i < PageCount).Distinct().Order().ToList();
            if (!list.SequenceEqual(document.SelectedPages))
            {
                document.SelectedPages = list;
            }
        }
    }

    private void SelectOnly(int index)
    {
        _anchor = _focus = index;
        SetSelection([index]);
        if (Document is { } document && index >= 0)
        {
            document.CurrentPageIndex = index;
        }
    }

    // ---- Layout & virtualization ------------------------------------------------------------

    private void Relayout()
    {
        var viewportWidth = _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth;
        var viewportHeight = _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : ActualHeight;
        var cell = CellSize;
        _columns = Math.Max(1, (int)((viewportWidth - 2 * SideMargin + HorizontalGap) / (cell.Width + HorizontalGap)));
        var gridWidth = _columns * cell.Width + (_columns - 1) * HorizontalGap;
        _left = Math.Max(SideMargin, Math.Floor((viewportWidth - gridWidth) / 2));
        var rows = (PageCount + _columns - 1) / _columns;
        _canvas.Width = Math.Max(0, Math.Max(viewportWidth, _left + gridWidth + SideMargin));
        _canvas.Height = Math.Max(viewportHeight, TopMargin + rows * (cell.Height + VerticalGap) + BottomMargin);
        UpdateVisibleTiles(rebind: true);
    }

    private Point TilePosition(int index)
    {
        var cell = CellSize;
        var row = index / _columns;
        var column = index % _columns;
        return new Point(_left + column * (cell.Width + HorizontalGap), TopMargin + row * (cell.Height + VerticalGap));
    }

    private void UpdateVisibleTiles(bool rebind)
    {
        var count = PageCount;
        if (_session is null || count == 0 || !IsLoaded || !IsVisible)
        {
            RecycleAll();
            return;
        }

        var cell = CellSize;
        var rowHeight = cell.Height + VerticalGap;
        var top = _scroll.VerticalOffset;
        var bottom = top + (_scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : ActualHeight);
        var firstRow = Math.Max(0, (int)Math.Floor((top - TopMargin) / rowHeight) - 1);
        var lastRow = Math.Max(firstRow, (int)Math.Floor((bottom - TopMargin) / rowHeight) + 1);
        var first = Math.Min(count, firstRow * _columns);
        var last = Math.Min(count - 1, ((lastRow + 1) * _columns) - 1);

        foreach (var (index, tile) in _tiles.ToList())
        {
            if (index < first || index > last)
            {
                Recycle(index, tile);
            }
        }

        for (var i = first; i <= last; i++)
        {
            if (!_tiles.TryGetValue(i, out var tile))
            {
                tile = _pool.Count > 0 ? _pool.Pop() : CreateTile();
                tile.Visibility = Visibility.Visible;
                _tiles[i] = tile;
                BindTile(tile, i);
            }
            else if (rebind)
            {
                BindTile(tile, i);
            }
        }
    }

    private PageTile CreateTile()
    {
        var tile = new PageTile(this);
        _canvas.Children.Add(tile);
        return tile;
    }

    private void BindTile(PageTile tile, int index)
    {
        var page = _session!.Pages[index];
        var source = page.IsBlank ? null : _session.GetSource(page.SourceId!.Value);
        tile.Bind(index, page, source, _session.GetPageSize(page), (index + 1).ToString(), CellSize, BoxRect, _dpi);
        tile.IsSelected = _selection.Contains(index);
        tile.Opacity = _dragging && tile.IsSelected ? 0.4 : 1;
        var position = TilePosition(index);
        Canvas.SetLeft(tile, position.X);
        Canvas.SetTop(tile, position.Y);
    }

    private void Recycle(int index, PageTile tile)
    {
        _tiles.Remove(index);
        tile.Unbind();
        tile.Visibility = Visibility.Collapsed;
        _pool.Push(tile);
    }

    private void RecycleAll()
    {
        foreach (var (index, tile) in _tiles.ToList())
        {
            Recycle(index, tile);
        }
    }

    public void ScrollIntoView(int index)
    {
        if (index < 0 || index >= PageCount)
        {
            return;
        }

        var top = TilePosition(index).Y;
        var bottom = top + CellSize.Height;
        if (top < _scroll.VerticalOffset)
        {
            _scroll.ScrollToVerticalOffset(top - TopMargin);
        }
        else if (bottom > _scroll.VerticalOffset + _scroll.ViewportHeight)
        {
            _scroll.ScrollToVerticalOffset(bottom - _scroll.ViewportHeight + VerticalGap * 2);
        }
    }

    // ---- Hit testing --------------------------------------------------------------------------

    /// <summary>Index of the page whose cell contains <paramref name="p"/> (canvas coordinates), or -1.</summary>
    private int TileAt(Point p)
    {
        var cell = CellSize;
        var x = p.X - _left;
        var y = p.Y - TopMargin;
        if (x < 0 || y < 0)
        {
            return -1;
        }

        var column = (int)(x / (cell.Width + HorizontalGap));
        var row = (int)(y / (cell.Height + VerticalGap));
        if (column >= _columns || x - column * (cell.Width + HorizontalGap) > cell.Width || y - row * (cell.Height + VerticalGap) > cell.Height)
        {
            return -1;
        }

        var index = row * _columns + column;
        return index < PageCount ? index : -1;
    }

    /// <summary>The insertion point nearest to <paramref name="p"/>: index to insert before, plus its row and column.</summary>
    private (int Index, int Row, int Column) GapAt(Point p)
    {
        var count = PageCount;
        if (count == 0)
        {
            return (0, 0, 0);
        }

        var cell = CellSize;
        var rows = (count + _columns - 1) / _columns;
        var row = Math.Clamp((int)Math.Floor((p.Y - TopMargin + VerticalGap / 2) / (cell.Height + VerticalGap)), 0, rows - 1);
        var inRow = Math.Min(_columns, count - row * _columns);
        var column = Math.Clamp((int)Math.Round((p.X - _left + HorizontalGap / 2) / (cell.Width + HorizontalGap)), 0, inRow);
        return (row * _columns + column, row, column);
    }

    private Point GapPosition(int row, int column)
    {
        var cell = CellSize;
        return new Point(_left + column * (cell.Width + HorizontalGap) - HorizontalGap / 2, TopMargin + row * (cell.Height + VerticalGap) + CellPadding);
    }

    private void ShowMarker(int row, int column, bool withButton)
    {
        var position = GapPosition(row, column);
        _marker.Height = BoxRect.Height;
        Canvas.SetLeft(_marker, position.X - _marker.Width / 2);
        Canvas.SetTop(_marker, position.Y);
        _markerButton.Visibility = withButton ? Visibility.Visible : Visibility.Collapsed;
        _marker.IsHitTestVisible = withButton;
        _marker.Visibility = Visibility.Visible;
    }

    private void HideMarker()
    {
        _marker.Visibility = Visibility.Collapsed;
        _markerGap = -1;
    }

    // ---- Pointer input --------------------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        HandlePress(e.GetPosition(_canvas), Keyboard.Modifiers, e.ClickCount);
        if (_pressed)
        {
            CaptureMouse();
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressed && e.LeftButton != MouseButtonState.Pressed)
        {
            HandleRelease(e.GetPosition(_canvas), cancel: true);
            return;
        }

        HandleMove(e.GetPosition(_canvas), e.GetPosition(_scroll));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pressed)
        {
            HandleRelease(e.GetPosition(_canvas), cancel: false);
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_pressed)
        {
            HandleRelease(default, cancel: true);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_pressed)
        {
            HideMarker();
        }
    }

    /// <summary>Mouse press at a canvas position (also used by UI automation).</summary>
    internal void HandlePress(Point p, ModifierKeys modifiers, int clickCount)
    {
        HideMarker();
        var index = TileAt(p);
        _pressPoint = p;
        _pressIndex = index;
        _selectOnlyOnRelease = -1;
        _pressed = true;

        if (index < 0)
        {
            // Empty area: start a rubber-band selection (Ctrl adds to the current selection).
            _marqueeBase = (modifiers & ModifierKeys.Control) != 0 ? _selection.ToHashSet() : new HashSet<int>();
            if ((modifiers & ModifierKeys.Control) == 0)
            {
                SetSelection([]);
            }

            _selecting = true;
            return;
        }

        if (clickCount >= 2)
        {
            _pressed = false;
            Document?.OpenPageInViewer(index);
            return;
        }

        if ((modifiers & ModifierKeys.Shift) != 0 && _anchor >= 0 && _anchor < PageCount)
        {
            var (from, to) = (Math.Min(_anchor, index), Math.Max(_anchor, index));
            var range = Enumerable.Range(from, to - from + 1);
            SetSelection((modifiers & ModifierKeys.Control) != 0 ? _selection.Concat(range) : range);
            _focus = index;
        }
        else if ((modifiers & ModifierKeys.Control) != 0)
        {
            var updated = _selection.ToHashSet();
            if (!updated.Remove(index))
            {
                updated.Add(index);
            }

            _anchor = _focus = index;
            SetSelection(updated);
        }
        else if (_selection.Contains(index))
        {
            // Keep a multi-selection so it can be dragged; a plain click selects just this page.
            _selectOnlyOnRelease = index;
            _anchor = _focus = index;
        }
        else
        {
            SelectOnly(index);
        }
    }

    internal void HandleMove(Point p, Point viewportPoint)
    {
        _lastViewportPoint = viewportPoint;
        if (!_pressed)
        {
            UpdateHoverMarker(p);
            return;
        }

        if (_selecting)
        {
            UpdateMarquee(p);
            UpdateAutoScroll(viewportPoint);
            return;
        }

        if (!_dragging)
        {
            var delta = p - _pressPoint;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 2
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2)
            {
                return;
            }

            if (_pressIndex < 0 || !_selection.Contains(_pressIndex))
            {
                return;
            }

            BeginDrag();
        }

        UpdateDrag(p);
        UpdateAutoScroll(viewportPoint);
    }

    internal void HandleRelease(Point p, bool cancel)
    {
        var wasDragging = _dragging;
        var wasSelecting = _selecting;
        _pressed = false;
        _dragging = false;
        _selecting = false;
        _autoScroll.Stop();
        _marquee.Visibility = Visibility.Collapsed;
        _ghost.Visibility = Visibility.Collapsed;
        HideMarker();
        foreach (var tile in _tiles.Values)
        {
            tile.Opacity = 1;
        }

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (cancel)
        {
            return;
        }

        if (wasDragging && Document is { } document)
        {
            var (insertBefore, _, _) = GapAt(p);
            document.MovePages(_selection.ToList(), insertBefore);
        }
        else if (!wasDragging && !wasSelecting && _selectOnlyOnRelease >= 0)
        {
            SelectOnly(_selectOnlyOnRelease);
        }
        else if (!wasSelecting && _pressIndex >= 0 && Document is { } doc)
        {
            doc.CurrentPageIndex = _pressIndex;
        }
    }

    private void BeginDrag()
    {
        _dragging = true;
        _selectOnlyOnRelease = -1;
        _ghostImage.Source = _tiles.TryGetValue(_pressIndex, out var pressed) ? pressed.Thumbnail : null;
        _ghostCount.Text = _selection.Count.ToString();
        ((FrameworkElement)_ghostCount.Parent).Visibility = _selection.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _ghost.Visibility = Visibility.Visible;
        foreach (var (index, tile) in _tiles)
        {
            tile.Opacity = _selection.Contains(index) ? 0.4 : 1;
        }
    }

    private void UpdateDrag(Point p)
    {
        Canvas.SetLeft(_ghost, p.X + 12);
        Canvas.SetTop(_ghost, p.Y + 12);
        var (_, row, column) = GapAt(p);
        ShowMarker(row, column, withButton: false);
    }

    private void UpdateMarquee(Point p)
    {
        var rect = new Rect(_pressPoint, p);
        Canvas.SetLeft(_marquee, rect.X);
        Canvas.SetTop(_marquee, rect.Y);
        _marquee.Width = rect.Width;
        _marquee.Height = rect.Height;
        _marquee.Visibility = Visibility.Visible;

        var hits = new HashSet<int>(_marqueeBase);
        var cell = CellSize;
        for (var i = 0; i < PageCount; i++)
        {
            var position = TilePosition(i);
            if (position.Y > rect.Bottom)
            {
                break;
            }

            if (rect.IntersectsWith(new Rect(position, cell)))
            {
                hits.Add(i);
            }
        }

        SetSelection(hits);
        if (hits.Count > 0)
        {
            _focus = hits.Max();
            _anchor = hits.Min();
        }
    }

    private void UpdateHoverMarker(Point p)
    {
        if (PageCount == 0)
        {
            return;
        }

        var (index, row, column) = GapAt(p);
        var position = GapPosition(row, column);
        var near = Math.Abs(p.X - position.X) <= HorizontalGap / 2 + 1 && p.Y >= position.Y && p.Y <= position.Y + BoxRect.Height;
        if (near)
        {
            _markerGap = index;
            ShowMarker(row, column, withButton: true);
        }
        else if (_marker.Visibility == Visibility.Visible && !_marker.IsMouseOver)
        {
            HideMarker();
        }
    }

    private void UpdateAutoScroll(Point viewportPoint)
    {
        var height = _scroll.ViewportHeight;
        var needed = viewportPoint.Y < AutoScrollZone || viewportPoint.Y > height - AutoScrollZone;
        if (needed && !_autoScroll.IsEnabled)
        {
            _autoScroll.Start();
        }
        else if (!needed)
        {
            _autoScroll.Stop();
        }
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (!_pressed)
        {
            _autoScroll.Stop();
            return;
        }

        var y = _lastViewportPoint.Y;
        var height = _scroll.ViewportHeight;
        var step = y < AutoScrollZone ? -(AutoScrollZone - y) : y > height - AutoScrollZone ? y - (height - AutoScrollZone) : 0;
        if (step == 0)
        {
            _autoScroll.Stop();
            return;
        }

        _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + Math.Clamp(step, -AutoScrollZone, AutoScrollZone) * 0.6);
        var canvasPoint = new Point(_lastViewportPoint.X + _scroll.HorizontalOffset, _lastViewportPoint.Y + _scroll.VerticalOffset);
        if (_selecting)
        {
            UpdateMarquee(canvasPoint);
        }
        else if (_dragging)
        {
            UpdateDrag(canvasPoint);
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            TileWidth *= e.Delta > 0 ? 1.15 : 1 / 1.15;
            e.Handled = true;
            return;
        }

        base.OnPreviewMouseWheel(e);
    }

    // ---- Context menus ----------------------------------------------------------------------------

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (Document is not { } document)
        {
            return;
        }

        Focus();
        ShowContextMenu(document, TileAt(e.GetPosition(_canvas)));
        e.Handled = true;
    }

    /// <summary>Opens the menu for a page (selecting it first) or, for -1, for the empty area.</summary>
    internal ContextMenu ShowContextMenu(DocumentViewModel document, int index)
    {
        if (index >= 0 && !_selection.Contains(index))
        {
            SelectOnly(index);
        }

        var menu = index >= 0 ? CreatePageMenu(document, index) : CreateBackgroundMenu(document);
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        return menu;
    }

    private static MenuItem Item(string header, SymbolRegular? icon, ICommand? command = null, Action? action = null, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? string.Empty };
        if (icon is { } symbol)
        {
            item.Icon = new SymbolIcon { Symbol = symbol };
        }

        if (command is not null)
        {
            item.Command = command;
        }
        else if (action is not null)
        {
            item.Click += (_, _) => action();
        }

        return item;
    }

    private ContextMenu CreatePageMenu(DocumentViewModel document, int index)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("ページを表示", SymbolRegular.Eye24, action: () => document.OpenPageInViewer(index), gesture: "Enter"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("左に回転", SymbolRegular.ArrowRotateCounterclockwise24, document.RotateLeftCommand));
        menu.Items.Add(Item("右に回転", SymbolRegular.ArrowRotateClockwise24, document.RotateRightCommand));
        menu.Items.Add(Item("削除", SymbolRegular.Delete24, document.DeleteSelectedPagesCommand, gesture: "Delete"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("切り取り", SymbolRegular.Cut24, document.CutPagesCommand, gesture: "Ctrl+X"));
        menu.Items.Add(Item("コピー", SymbolRegular.Copy24, document.CopyPagesCommand, gesture: "Ctrl+C"));
        menu.Items.Add(Item("後ろに貼り付け", SymbolRegular.ClipboardPaste24, document.PastePagesCommand, gesture: "Ctrl+V"));
        menu.Items.Add(Item("複製", SymbolRegular.DocumentCopy24, document.DuplicateSelectedPagesCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("空白ページを後ろに挿入", SymbolRegular.DocumentAdd24, document.InsertBlankPageCommand));
        menu.Items.Add(Item("ファイルを後ろに挿入...", SymbolRegular.DocumentArrowRight24, document.InsertFromFileCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("ページを抽出...", SymbolRegular.DocumentArrowUp20, document.ExtractPagesCommand));
        menu.Items.Add(Item("ページを置換...", SymbolRegular.ArrowSwap24, document.ReplacePagesCommand));
        return menu;
    }

    private ContextMenu CreateBackgroundMenu(DocumentViewModel document)
    {
        var end = PageCount;
        var menu = new ContextMenu();
        menu.Items.Add(Item("すべてのページを選択", SymbolRegular.SelectAllOn24, action: () => SetSelection(Enumerable.Range(0, PageCount)), gesture: "Ctrl+A"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("末尾に貼り付け", SymbolRegular.ClipboardPaste24, action: () => _ = document.PastePagesAtAsync(end)));
        menu.Items.Add(Item("空白ページを末尾に挿入", SymbolRegular.DocumentAdd24, action: () => document.InsertBlankPages(end, 1)));
        menu.Items.Add(Item("ファイルを末尾に挿入...", SymbolRegular.DocumentArrowRight24, action: () => _ = document.InsertFromFileAtAsync(end)));
        return menu;
    }

    private void OnMarkerButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (Document is not { } document || _markerGap < 0)
        {
            return;
        }

        var at = _markerGap;
        var menu = new ContextMenu();
        menu.Items.Add(Item("空白ページを挿入", SymbolRegular.DocumentAdd24, action: () => document.InsertBlankPages(at, 1)));
        menu.Items.Add(Item("ファイルから挿入...", SymbolRegular.DocumentArrowRight24, action: () => _ = document.InsertFromFileAtAsync(at)));
        menu.Items.Add(Item("クリップボードから挿入", SymbolRegular.ClipboardPaste24, action: () => _ = document.PastePagesAtAsync(at)));
        menu.PlacementTarget = _markerButton;
        menu.Placement = PlacementMode.Bottom;
        menu.Closed += (_, _) => HideMarker();
        menu.IsOpen = true;
    }

    // ---- Hover actions (called by tiles) ------------------------------------------------------------

    /// <summary>Pages a hover action applies to: the whole selection when the page is part of it.</summary>
    private IReadOnlyList<int> ActionTargets(int index) =>
        _selection.Contains(index) ? _selection.Order().ToList() : [index];

    internal void RotateFromTile(int index, int quarterTurns)
    {
        if (index >= 0)
        {
            Document?.RotatePages(ActionTargets(index), quarterTurns);
        }
    }

    internal void DeleteFromTile(int index)
    {
        if (index >= 0)
        {
            Document?.DeletePages(ActionTargets(index));
        }
    }

    // ---- Files from Explorer --------------------------------------------------------------------------

    private static List<string> DroppedFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => File.Exists(f) && (string.Equals(System.IO.Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase) || ImagePdfConverter.IsImage(f))).ToList()
            : [];

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (DroppedFiles(e).Count == 0)
        {
            e.Effects = DragDropEffects.None;
            HideMarker();
        }
        else
        {
            e.Effects = DragDropEffects.Copy;
            var (_, row, column) = GapAt(e.GetPosition(_canvas));
            ShowMarker(row, column, withButton: false);
            var y = e.GetPosition(_scroll).Y;
            if (y < AutoScrollZone)
            {
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - 12);
            }
            else if (y > _scroll.ViewportHeight - AutoScrollZone)
            {
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 12);
            }
        }

        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        HideMarker();
    }

    protected override async void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        e.Handled = true;
        HideMarker();
        var files = DroppedFiles(e);
        if (files.Count > 0 && Document is { } document)
        {
            var (at, _, _) = GapAt(e.GetPosition(_canvas));
            await document.InsertFilesAsync(at, files);
        }
    }

    // ---- Keyboard -------------------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Document is not { } document || PageCount == 0)
        {
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var rowsPerPage = Math.Max(1, (int)(_scroll.ViewportHeight / (CellSize.Height + VerticalGap)));
        int? move = e.Key switch
        {
            Key.Left => -1,
            Key.Right => +1,
            Key.Up => -_columns,
            Key.Down => +_columns,
            Key.PageUp => -_columns * rowsPerPage,
            Key.PageDown => +_columns * rowsPerPage,
            Key.Home => int.MinValue / 2,
            Key.End => int.MaxValue / 2,
            _ => null,
        };

        if (move is { } delta && !ctrl)
        {
            var from = _focus >= 0 ? _focus : 0;
            var target = (int)Math.Clamp((long)from + delta, 0, PageCount - 1);
            if (shift)
            {
                if (_anchor < 0)
                {
                    _anchor = from;
                }

                _focus = target;
                SetSelection(Enumerable.Range(Math.Min(_anchor, target), Math.Abs(target - _anchor) + 1));
                document.CurrentPageIndex = target;
            }
            else
            {
                SelectOnly(target);
            }

            ScrollIntoView(target);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.A when ctrl:
                SetSelection(Enumerable.Range(0, PageCount));
                e.Handled = true;
                break;
            case Key.C when ctrl:
                document.CopyPagesCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.X when ctrl:
                document.CutPagesCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.V when ctrl:
                document.PastePagesCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete:
            case Key.Back:
                document.DeleteSelectedPagesCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when _focus >= 0:
                document.OpenPageInViewer(_focus);
                e.Handled = true;
                break;
            case Key.Escape when _pressed:
                HandleRelease(default, cancel: true);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Layout summary for UI automation logs.</summary>
    internal string Describe() =>
        $"grid: {_columns} columns, tile {TileWidth:0}, {_tiles.Count} realized, selection [{string.Join(",", _selection.Order().Select(i => i + 1))}]";

    /// <summary>Center of a page's thumbnail in canvas coordinates (for UI automation).</summary>
    internal Point TileCenter(int index)
    {
        var position = TilePosition(index);
        var box = BoxRect;
        return new Point(position.X + box.X + box.Width / 2, position.Y + box.Y + box.Height / 2);
    }

    /// <summary>Middle of the insertion gap before page <paramref name="insertBefore"/> (for UI automation).</summary>
    internal Point GapPoint(int insertBefore)
    {
        var count = PageCount;
        insertBefore = Math.Clamp(insertBefore, 0, count);
        var row = insertBefore == count && count > 0 ? (count - 1) / _columns : insertBefore / _columns;
        var position = GapPosition(row, insertBefore - row * _columns);
        return new Point(position.X, position.Y + BoxRect.Height / 2);
    }

    /// <summary>Converts canvas coordinates to viewport coordinates (for UI automation).</summary>
    internal Point CanvasToViewport(Point p) => new(p.X - _scroll.HorizontalOffset, p.Y - _scroll.VerticalOffset);
}
