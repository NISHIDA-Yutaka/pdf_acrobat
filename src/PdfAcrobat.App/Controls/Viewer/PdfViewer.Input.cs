using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PdfAcrobat.Core.Text;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.Controls.Viewer;

/// <summary>Mouse and keyboard handling: text selection, links, panning and zooming.</summary>
public sealed partial class PdfViewer
{
    private const double DragThreshold = 4;

    private readonly record struct TextPosition(int PageIndex, int CharIndex);

    private DispatcherTimer? _autoScrollTimer;
    private TextPosition? _selectionAnchor;
    private TextPosition? _selectionFocus;
    private bool _selecting;
    private bool _panning;
    private bool _spaceHeld;
    private Point _pressPoint;
    private Point _panScrollStart;
    private (PdfLink Link, Guid SourceId)? _pressedLink;
    private Point _lastMouseViewport;

    public bool HasSelection => _selectionAnchor is not null && _selectionFocus is not null;

    private void InitializeInput()
    {
        _canvas.MouseLeftButtonDown += OnCanvasMouseLeftButtonDown;
        _canvas.MouseMove += OnCanvasMouseMove;
        _canvas.MouseLeftButtonUp += OnCanvasMouseLeftButtonUp;
        _canvas.MouseRightButtonUp += OnCanvasMouseRightButtonUp;
        _canvas.LostMouseCapture += (_, _) => EndDrag();
        _scroll.PreviewMouseWheel += OnPreviewMouseWheel;

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelection(), (_, e) => e.CanExecute = HasSelection));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll, (_, _) => SelectAll(), (_, e) => e.CanExecute = Session is not null));
        UpdateIdleCursor();
    }

    // ---- Hit testing -----------------------------------------------------------

    private readonly record struct PageHit(int PageIndex, PageView View, PdfPoint Point, Point Local);

    private PageHit? HitTestPage(Point canvasPoint, bool nearest = false)
    {
        PageView? best = null;
        var bestDistance = double.MaxValue;
        foreach (var view in _views.Values)
        {
            var rect = new Rect(Canvas.GetLeft(view), Canvas.GetTop(view), view.Width, view.Height);
            if (rect.Contains(canvasPoint))
            {
                best = view;
                bestDistance = 0;
                break;
            }

            if (nearest)
            {
                var dy = canvasPoint.Y < rect.Top ? rect.Top - canvasPoint.Y : Math.Max(0, canvasPoint.Y - rect.Bottom);
                var dx = canvasPoint.X < rect.Left ? rect.Left - canvasPoint.X : Math.Max(0, canvasPoint.X - rect.Right);
                var d = dy * 4 + dx;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = view;
                }
            }
        }

        if (best?.DisplayMatrix is not { } matrix)
        {
            return null;
        }

        var local = new Point(canvasPoint.X - Canvas.GetLeft(best), canvasPoint.Y - Canvas.GetTop(best));
        var user = matrix.Invert().Transform(local.X, local.Y);
        return new PageHit(best.PageIndex, best, user, local);
    }

    /// <summary>Tolerance in PDF points equivalent to a few screen pixels.</summary>
    private double PointTolerance(double pixels) => pixels / Math.Max(_layout.Scale, 0.01);

    private (PdfLink Link, Guid SourceId)? LinkAt(PageHit hit)
    {
        if (hit.View.Info is not { } info || hit.View.Source is not { } source)
        {
            return null;
        }

        foreach (var link in info.Links)
        {
            if (link.Bounds.Contains(hit.Point))
            {
                return (link, source.Id);
            }
        }

        return null;
    }

    private bool IsOverText(PageHit hit) =>
        hit.View.Info is { } info && TextGeometry.HitTest(info.Text, hit.Point, PointTolerance(2)) >= 0;

    // ---- Mouse -------------------------------------------------------------------

    private bool IsHandMode => Tool == ViewerTool.Hand || _spaceHeld;

    private void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        HandlePress(e.GetPosition(_canvas), e.GetPosition(this), e.ClickCount);
        e.Handled = true;
    }

    /// <summary>Viewport (control) coordinates → canvas coordinates.</summary>
    internal Point ViewportToCanvas(Point viewportPoint) =>
        new(viewportPoint.X + _scroll.HorizontalOffset, viewportPoint.Y + _scroll.VerticalOffset);

    // Press / move / release take explicit points so that automation can drive them too.
    internal void HandlePress(Point canvasPoint, Point viewportPoint, int clickCount)
    {
        _pressPoint = viewportPoint;
        _pressedLink = null;

        if (IsHandMode)
        {
            StartPan();
            return;
        }

        var hit = HitTestPage(canvasPoint);
        if (hit is { } h)
        {
            var link = LinkAt(h);
            if (link is not null && clickCount == 1)
            {
                _pressedLink = link;
                _canvas.CaptureMouse();
                return;
            }

            if (h.View.Info is { } info)
            {
                var index = TextGeometry.HitTest(info.Text, h.Point, PointTolerance(6));
                if (index >= 0)
                {
                    if (clickCount == 2)
                    {
                        var word = TextGeometry.WordAt(info.Text, index);
                        SetSelection(new TextPosition(h.PageIndex, word.Start), new TextPosition(h.PageIndex, Math.Max(word.Start, word.End - 1)));
                    }
                    else if (clickCount >= 3)
                    {
                        var line = TextGeometry.LineAt(info.Text, index);
                        SetSelection(new TextPosition(h.PageIndex, line.Start), new TextPosition(h.PageIndex, Math.Max(line.Start, line.End - 1)));
                    }
                    else
                    {
                        _selecting = true;
                        SetSelection(new TextPosition(h.PageIndex, index), new TextPosition(h.PageIndex, index), clearOnly: true);
                        _canvas.CaptureMouse();
                    }

                    return;
                }
            }
        }

        // Pressing on empty space clears the selection and pans the view.
        ClearSelection();
        StartPan();
    }

    private void StartPan()
    {
        _panning = true;
        _panScrollStart = new Point(_scroll.HorizontalOffset, _scroll.VerticalOffset);
        _canvas.CaptureMouse();
        _canvas.Cursor = Cursors.SizeAll;
        if (IsHandMode)
        {
            _canvas.Cursor = HandCursors.Grabbing;
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e) => HandleMove(e.GetPosition(_canvas), e.GetPosition(this));

    internal void HandleMove(Point canvasPoint, Point viewportPoint)
    {
        _lastMouseViewport = viewportPoint;

        if (_panning)
        {
            var delta = viewportPoint - _pressPoint;
            ScrollToOffset(_panScrollStart.X - delta.X, _panScrollStart.Y - delta.Y);
            return;
        }

        if (_selecting)
        {
            ExtendSelection(canvasPoint);
            UpdateAutoScroll(viewportPoint);
            return;
        }

        if (_pressedLink is not null)
        {
            return;
        }

        if (IsHandMode)
        {
            _canvas.Cursor = HandCursors.Open;
            return;
        }

        var hit = HitTestPage(canvasPoint);
        if (hit is { } h && LinkAt(h) is { } link)
        {
            _canvas.Cursor = Cursors.Hand;
            _canvas.ToolTip = link.Link.Uri ?? (link.Link.Destination is { } d ? $"{d.PageIndex + 1} ページへ移動" : null);
            return;
        }

        _canvas.ToolTip = null;
        _canvas.Cursor = hit is { } th && IsOverText(th) ? Cursors.IBeam : Cursors.Arrow;
    }

    private void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        HandleRelease(e.GetPosition(this));
        e.Handled = true;
    }

    internal void HandleRelease(Point viewportPoint)
    {
        var moved = (viewportPoint - _pressPoint).Length > DragThreshold;
        if (_pressedLink is { } link && !moved)
        {
            _pressedLink = null;
            _canvas.ReleaseMouseCapture();
            FollowLink(link.Link, link.SourceId);
            return;
        }

        if (_selecting && !moved && _selectionAnchor == _selectionFocus)
        {
            // A plain click on text places nothing selected.
            ClearSelection();
        }

        EndDrag();
    }

    private void EndDrag()
    {
        var wasSelecting = _selecting;
        _selecting = false;
        _panning = false;
        _pressedLink = null;
        StopAutoScroll();
        if (_canvas.IsMouseCaptured)
        {
            _canvas.ReleaseMouseCapture();
        }

        UpdateIdleCursor();
        if (wasSelecting)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void FollowLink(PdfLink link, Guid sourceId)
    {
        if (link.Destination is { } destination)
        {
            GoToDestination(destination, sourceId);
        }
        else if (!string.IsNullOrEmpty(link.Uri))
        {
            ExternalLinkRequested?.Invoke(this, link.Uri);
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var factor = Math.Pow(1.1, e.Delta / 120.0);
            SetZoomAt(Zoom * factor, e.GetPosition(_scroll));
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            ScrollToOffset(_scroll.HorizontalOffset - e.Delta, _scroll.VerticalOffset);
            e.Handled = true;
            return;
        }

        if (!IsContinuous)
        {
            // Single page views flip pages at the scroll boundaries.
            var atTop = _scroll.VerticalOffset <= 0.5;
            var atBottom = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 0.5;
            if (e.Delta < 0 && atBottom)
            {
                NextPage();
                e.Handled = true;
            }
            else if (e.Delta > 0 && atTop && CurrentPageIndex > 0)
            {
                PreviousPage();
                ScrollToOffset(_scroll.HorizontalOffset, _scroll.ScrollableHeight);
                e.Handled = true;
            }
        }
    }

    private void OnCanvasMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "コピー", InputGestureText = "Ctrl+C", Command = ApplicationCommands.Copy, CommandTarget = this };
        var selectAll = new MenuItem { Header = "すべてを選択", InputGestureText = "Ctrl+A", Command = ApplicationCommands.SelectAll, CommandTarget = this };
        menu.Items.Add(copy);
        menu.Items.Add(selectAll);
        menu.Items.Add(new Separator());
        var selectTool = new MenuItem { Header = "テキスト選択ツール", IsCheckable = true, IsChecked = Tool == ViewerTool.Select };
        selectTool.Click += (_, _) => SetCurrentValue(ToolProperty, ViewerTool.Select);
        var handTool = new MenuItem { Header = "手のひらツール", IsCheckable = true, IsChecked = Tool == ViewerTool.Hand };
        handTool.Click += (_, _) => SetCurrentValue(ToolProperty, ViewerTool.Hand);
        menu.Items.Add(selectTool);
        menu.Items.Add(handTool);
        menu.PlacementTarget = _canvas;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void UpdateIdleCursor()
    {
        if (_canvas is null)
        {
            return;
        }

        _canvas.Cursor = IsHandMode ? HandCursors.Open : Cursors.Arrow;
    }

    // ---- Keyboard ------------------------------------------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || Session is null)
        {
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var line = 48.0;
        switch (e.Key)
        {
            case Key.Space when !e.IsRepeat && Tool == ViewerTool.Select && !_selecting:
                _spaceHeld = true;
                UpdateIdleCursor();
                e.Handled = true;
                break;
            case Key.Space when e.IsRepeat:
                e.Handled = true;
                break;
            case Key.Down:
                ScrollToOffset(_scroll.HorizontalOffset, _scroll.VerticalOffset + line);
                e.Handled = true;
                break;
            case Key.Up:
                ScrollToOffset(_scroll.HorizontalOffset, _scroll.VerticalOffset - line);
                e.Handled = true;
                break;
            case Key.Left when _scroll.ScrollableWidth > 0:
                ScrollToOffset(_scroll.HorizontalOffset - line, _scroll.VerticalOffset);
                e.Handled = true;
                break;
            case Key.Right when _scroll.ScrollableWidth > 0:
                ScrollToOffset(_scroll.HorizontalOffset + line, _scroll.VerticalOffset);
                e.Handled = true;
                break;
            case Key.Left:
                PreviousPage();
                e.Handled = true;
                break;
            case Key.Right:
                NextPage();
                e.Handled = true;
                break;
            case Key.PageDown:
                if (IsContinuous)
                {
                    ScrollToOffset(_scroll.HorizontalOffset, _scroll.VerticalOffset + ViewportHeight * 0.9);
                }
                else
                {
                    NextPage();
                }

                e.Handled = true;
                break;
            case Key.PageUp:
                if (IsContinuous)
                {
                    ScrollToOffset(_scroll.HorizontalOffset, _scroll.VerticalOffset - ViewportHeight * 0.9);
                }
                else
                {
                    PreviousPage();
                }

                e.Handled = true;
                break;
            case Key.Home:
                GoToPage(0);
                e.Handled = true;
                break;
            case Key.End:
                GoToPage(Session.Pages.Count - 1);
                e.Handled = true;
                break;
            case Key.Escape when HasSelection:
                ClearSelection();
                e.Handled = true;
                break;
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key == Key.Space && _spaceHeld)
        {
            _spaceHeld = false;
            if (_panning)
            {
                EndDrag();
            }

            UpdateIdleCursor();
            e.Handled = true;
        }
    }

    // ---- Selection -----------------------------------------------------------------

    private void SetSelection(TextPosition anchor, TextPosition focus, bool clearOnly = false)
    {
        _selectionAnchor = anchor;
        _selectionFocus = clearOnly ? anchor : focus;
        InvalidatePages();
        if (!clearOnly)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ClearSelection()
    {
        if (_selectionAnchor is null && _selectionFocus is null)
        {
            return;
        }

        _selectionAnchor = null;
        _selectionFocus = null;
        InvalidatePages();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ExtendSelection(Point canvasPoint)
    {
        if (_selectionAnchor is null)
        {
            return;
        }

        var hit = HitTestPage(canvasPoint, nearest: true);
        if (hit is not { } h || h.View.Info is not { } info || info.Text.Length == 0)
        {
            return;
        }

        var index = TextGeometry.Nearest(info.Text, h.Point);
        if (index < 0)
        {
            return;
        }

        var focus = new TextPosition(h.PageIndex, index);
        if (focus != _selectionFocus)
        {
            _selectionFocus = focus;
            InvalidatePages();
        }
    }

    private (TextPosition Start, TextPosition End)? NormalizedSelection()
    {
        if (_selectionAnchor is not { } a || _selectionFocus is not { } f)
        {
            return null;
        }

        return (a.PageIndex, a.CharIndex).CompareTo((f.PageIndex, f.CharIndex)) <= 0 ? (a, f) : (f, a);
    }

    internal IReadOnlyList<PdfRect> GetSelectionRects(int pageIndex, PdfTextSnapshot text)
    {
        if (NormalizedSelection() is not { } sel || pageIndex < sel.Start.PageIndex || pageIndex > sel.End.PageIndex || text.Length == 0)
        {
            return [];
        }

        if (_selecting && sel.Start == sel.End)
        {
            return [];
        }

        var from = pageIndex == sel.Start.PageIndex ? sel.Start.CharIndex : 0;
        var to = pageIndex == sel.End.PageIndex ? Math.Min(sel.End.CharIndex, text.Length - 1) : text.Length - 1;
        return to >= from ? TextGeometry.GetRangeRects(text, from, to - from + 1) : [];
    }

    public void SelectAll()
    {
        if (Session is not { } session || session.Pages.Count == 0)
        {
            return;
        }

        SetSelection(new TextPosition(0, 0), new TextPosition(session.Pages.Count - 1, int.MaxValue / 2));
    }

    /// <summary>Text of the current selection (loads page text as needed).</summary>
    public async Task<string> GetSelectedTextAsync()
    {
        if (Session is not { } session || NormalizedSelection() is not { } sel)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var p = sel.Start.PageIndex; p <= sel.End.PageIndex && p < session.Pages.Count; p++)
        {
            var page = session.Pages[p];
            if (page.IsBlank)
            {
                continue;
            }

            var info = await InfoCache.GetAsync(session.GetSource(page.SourceId!.Value), page.SourceIndex);
            var text = info.Text;
            if (text.Length == 0)
            {
                continue;
            }

            var from = p == sel.Start.PageIndex ? sel.Start.CharIndex : 0;
            var to = p == sel.End.PageIndex ? Math.Min(sel.End.CharIndex, text.Length - 1) : text.Length - 1;
            if (to >= from)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(TextGeometry.GetText(text, from, to - from + 1));
            }
        }

        return builder.ToString();
    }

    public async void CopySelection()
    {
        var text = await GetSelectedTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                await Task.Delay(50);
            }
        }
    }

    // ---- Auto scroll while selecting ----------------------------------------------

    private void UpdateAutoScroll(Point viewportPoint)
    {
        var outside = viewportPoint.Y < 0 || viewportPoint.Y > ViewportHeight || viewportPoint.X < 0 || viewportPoint.X > ViewportWidth;
        if (outside)
        {
            _autoScrollTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => AutoScrollTick(), Dispatcher);
            _autoScrollTimer.Start();
        }
        else
        {
            StopAutoScroll();
        }
    }

    private void StopAutoScroll() => _autoScrollTimer?.Stop();

    private void AutoScrollTick()
    {
        if (!_selecting || _autoScrollTimer is not { IsEnabled: true })
        {
            return;
        }

        var p = _lastMouseViewport;
        var dy = p.Y < 0 ? p.Y : p.Y > ViewportHeight ? p.Y - ViewportHeight : 0;
        var dx = p.X < 0 ? p.X : p.X > ViewportWidth ? p.X - ViewportWidth : 0;
        if (dx == 0 && dy == 0)
        {
            return;
        }

        ScrollToOffset(_scroll.HorizontalOffset + dx / 2, _scroll.VerticalOffset + dy / 2);
        ExtendSelection(new Point(p.X + _scroll.HorizontalOffset, p.Y + _scroll.VerticalOffset));
    }
}

/// <summary>Open/closed hand cursors for the hand tool.</summary>
internal static class HandCursors
{
    public static Cursor Open => Cursors.Hand;

    public static Cursor Grabbing => Cursors.SizeAll;
}
