using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfAcrobat.App.Services;
using PdfAcrobat.Core.Documents;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace PdfAcrobat.App.Controls.PageGrid;

/// <summary>
/// One cell of <see cref="PageGridView"/>: the page thumbnail, its number, the selection highlight
/// and the hover actions (rotate / delete).
/// </summary>
internal sealed class PageTile : Grid
{
    private readonly PageGridView _owner;
    private readonly Border _highlight;
    private readonly Border _outline;
    private readonly Border _frame;
    private readonly Image _image;
    private readonly TextBlock _label;
    private readonly Border _actions;
    private CancellationTokenSource? _renderCts;
    private bool _selected;
    private bool _hover;

    public PageTile(PageGridView owner)
    {
        _owner = owner;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Background = Brushes.Transparent;

        _highlight = new Border { CornerRadius = new CornerRadius(8) };
        _outline = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(2.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        _outline.SetResourceReference(Border.BorderBrushProperty, "ThumbnailSelectedBorderBrush");

        _image = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _frame = new Border
        {
            Background = Brushes.White,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _image,
        };
        _frame.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");

        _label = new TextBlock
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(CreateActionButton(SymbolRegular.ArrowRotateCounterclockwise24, "左に回転", () => _owner.RotateFromTile(Index, -1)));
        buttons.Children.Add(CreateActionButton(SymbolRegular.ArrowRotateClockwise24, "右に回転", () => _owner.RotateFromTile(Index, +1)));
        buttons.Children.Add(CreateActionButton(SymbolRegular.Delete24, "削除", () => _owner.DeleteFromTile(Index)));
        _actions = new Border
        {
            Child = buttons,
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        _actions.SetResourceReference(StyleProperty, "FloatingBarStyle");

        Children.Add(_highlight);
        Children.Add(_outline);
        Children.Add(_frame);
        Children.Add(_label);
        Children.Add(_actions);

        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
        UpdateVisual();
    }

    public int Index { get; private set; } = -1;

    public PageRef? Page { get; private set; }

    /// <summary>Frame of the page image in tile coordinates.</summary>
    public Rect FrameRect { get; private set; }

    public ImageSource? Thumbnail => _image.Source;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected != value)
            {
                _selected = value;
                UpdateVisual();
            }
        }
    }

    private Button CreateActionButton(SymbolRegular symbol, string toolTip, Action action)
    {
        var button = new Button
        {
            Content = new SymbolIcon { Symbol = symbol },
            ToolTip = toolTip,
            Width = 28,
            Height = 28,
            FontSize = 15,
        };
        button.SetResourceReference(StyleProperty, "ToolButtonStyle");
        button.Click += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return button;
    }

    private void SetHover(bool hover)
    {
        _hover = hover && !_owner.IsPointerBusy;
        UpdateVisual();
    }

    private void UpdateVisual()
    {
        if (_selected)
        {
            _highlight.SetResourceReference(Border.BackgroundProperty, "ToolCheckedBackgroundBrush");
            _label.SetResourceReference(TextBlock.ForegroundProperty, "ToolCheckedForegroundBrush");
            _label.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            if (_hover)
            {
                _highlight.SetResourceReference(Border.BackgroundProperty, "ToolHoverBackgroundBrush");
            }
            else
            {
                _highlight.Background = Brushes.Transparent;
            }

            _label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _label.FontWeight = FontWeights.Normal;
        }

        _outline.Visibility = _selected ? Visibility.Visible : Visibility.Collapsed;
        _actions.Visibility = _hover && Page is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows a page. <paramref name="box"/> is the area the page image is fitted into (bottom-aligned).</summary>
    public void Bind(int index, PageRef page, PdfSource? source, PdfAcrobat.Pdfium.PdfSize pageSize, string label, Size cell, Rect box, double dpiScale)
    {
        var sameContent = Page is not null && Page.SourceId == page.SourceId && Page.SourceIndex == page.SourceIndex
                          && Page.Rotation == page.Rotation && FrameRect.Size == FitSize(pageSize, box.Size);
        Index = index;
        Page = page;
        Width = cell.Width;
        Height = cell.Height;
        _label.Text = label;
        _label.Margin = new Thickness(4, box.Bottom + 5, 4, 0);

        var size = FitSize(pageSize, box.Size);
        var frame = new Rect(box.X + (box.Width - size.Width) / 2, box.Bottom - size.Height, size.Width, size.Height);
        FrameRect = frame;
        _frame.Margin = new Thickness(frame.X, frame.Y, 0, 0);
        _frame.Width = frame.Width;
        _frame.Height = frame.Height;
        _outline.Margin = new Thickness(frame.X - 4, frame.Y - 4, 0, 0);
        _outline.Width = frame.Width + 8;
        _outline.Height = frame.Height + 8;
        _actions.Margin = new Thickness(0, frame.Y + 6, 0, 0);
        UpdateVisual();

        if (sameContent && _image.Source is not null)
        {
            return;
        }

        _renderCts?.Cancel();
        _renderCts = null;
        _image.Source = null;
        if (source is null)
        {
            return; // Blank page: the white frame is the page.
        }

        var pixelWidth = (int)Math.Round(frame.Width * dpiScale);
        var pixelHeight = (int)Math.Round(frame.Height * dpiScale);
        var task = AppServices.Render.RenderAsync(source, page.SourceIndex, page.Rotation, pixelWidth, pixelHeight, RenderPriority.Thumbnail, (_renderCts = new CancellationTokenSource()).Token);
        if (task.IsCompletedSuccessfully)
        {
            _image.Source = task.Result;
            return;
        }

        // Show a lower or higher resolution version while the right size renders.
        _image.Source = AppServices.Render.GetBestCached(source.Id, page.SourceIndex, page.Rotation);
        var expected = page;
        task.ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully && t.Result is { } bitmap && ReferenceEquals(Page, expected))
                {
                    _image.Source = bitmap;
                }
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void Unbind()
    {
        _renderCts?.Cancel();
        _renderCts = null;
        _image.Source = null;
        Page = null;
        Index = -1;
        _hover = false;
        _selected = false;
        UpdateVisual();
    }

    private static Size FitSize(PdfAcrobat.Pdfium.PdfSize page, Size box)
    {
        var width = Math.Max(1, page.Width);
        var height = Math.Max(1, page.Height);
        var scale = Math.Min(box.Width / width, box.Height / height);
        return new Size(Math.Max(8, Math.Round(width * scale)), Math.Max(8, Math.Round(height * scale)));
    }
}
