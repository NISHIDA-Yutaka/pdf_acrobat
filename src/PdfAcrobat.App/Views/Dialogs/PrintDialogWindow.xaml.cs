using System.Drawing.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfAcrobat.App.Services;
using PdfAcrobat.App.ViewModels;
using PdfAcrobat.Core.Documents;

namespace PdfAcrobat.App.Views.Dialogs;

public partial class PrintDialogWindow
{
    private readonly DocumentViewModel _document;
    private readonly DocumentSession _session;
    private CancellationTokenSource? _printCts;
    private CancellationTokenSource? _previewCts;
    private int _previewPosition;
    private bool _initialized;

    public PrintDialogWindow(DocumentViewModel document)
    {
        InitializeComponent();
        _document = document;
        _session = document.Session;
        var printers = PrintService.GetPrinters();
        PrinterBox.ItemsSource = printers;
        PrinterBox.SelectedItem = PrintService.GetDefaultPrinter() ?? printers.FirstOrDefault();
        RangeBox.Text = $"1-{_session.Pages.Count}";
        _initialized = true;
        Loaded += (_, _) => UpdatePreview();
        Closing += (_, e) =>
        {
            if (_printCts is not null)
            {
                _printCts.Cancel();
            }
        };
        if (printers.Count == 0)
        {
            StatusText.Text = "プリンターが見つかりません。";
            PrintButton.IsEnabled = false;
        }
    }

    private IReadOnlyList<int>? SelectedPages()
    {
        if (CurrentPageRadio.IsChecked == true)
        {
            return [_document.CurrentPageIndex];
        }

        if (RangeRadio.IsChecked == true)
        {
            return PageRangeParser.TryParse(RangeBox.Text, _session.Pages.Count, out var pages) ? pages : null;
        }

        return Enumerable.Range(0, _session.Pages.Count).ToList();
    }

    private PrintScaling Scaling =>
        ActualRadio.IsChecked == true ? PrintScaling.ActualSize : ShrinkRadio.IsChecked == true ? PrintScaling.ShrinkOversized : PrintScaling.Fit;

    private PrintOrientation Orientation =>
        PortraitRadio.IsChecked == true ? PrintOrientation.Portrait : LandscapeRadio.IsChecked == true ? PrintOrientation.Landscape : PrintOrientation.Auto;

    private void OnRangeBoxFocus(object sender, RoutedEventArgs e) => RangeRadio.IsChecked = true;

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            _previewPosition = 0;
            UpdatePreview();
        }
    }

    private void OnPreviewPrevious(object sender, RoutedEventArgs e)
    {
        _previewPosition = Math.Max(0, _previewPosition - 1);
        UpdatePreview();
    }

    private void OnPreviewNext(object sender, RoutedEventArgs e)
    {
        _previewPosition++;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var pages = SelectedPages();
        RangeHint.Foreground = pages is null && RangeRadio.IsChecked == true
            ? (Brush)FindResource("SystemFillColorCriticalBrush")
            : (Brush)FindResource("MutedForegroundBrush");
        PrintButton.IsEnabled = pages is not null && PrinterBox.SelectedItem is not null;
        if (pages is null || pages.Count == 0)
        {
            PreviewText.Text = "ページ指定が正しくありません";
            PreviewImage.Source = null;
            return;
        }

        _previewPosition = Math.Clamp(_previewPosition, 0, pages.Count - 1);
        var pageIndex = pages[_previewPosition];
        var page = _session.Pages[pageIndex];
        PreviewText.Text = $"{_previewPosition + 1} / {pages.Count} 枚 (ページ {pageIndex + 1})";

        // Paper size of the selected printer, in points.
        var (paperWidth, paperHeight, paperName) = GetPaper();
        var pageSize = _session.GetPageSize(page);
        var landscape = Orientation switch
        {
            PrintOrientation.Landscape => true,
            PrintOrientation.Portrait => false,
            _ => pageSize.Width > pageSize.Height,
        };
        if (landscape)
        {
            (paperWidth, paperHeight) = (paperHeight, paperWidth);
        }

        PaperText.Text = $"用紙: {paperName} ({(landscape ? "横" : "縦")})";

        const double Margin = 16;
        var canvasScale = Math.Min((PreviewCanvas.Width - 2 * Margin) / paperWidth, (PreviewCanvas.Height - 2 * Margin) / paperHeight);
        var paperW = paperWidth * canvasScale;
        var paperH = paperHeight * canvasScale;
        var paperX = (PreviewCanvas.Width - paperW) / 2;
        var paperY = (PreviewCanvas.Height - paperH) / 2;
        PaperRect.Width = paperW;
        PaperRect.Height = paperH;
        Canvas.SetLeft(PaperRect, paperX);
        Canvas.SetTop(PaperRect, paperY);

        // Same placement rule as PrintService (printable margin approximated as 0.25 inch).
        const double HardMargin = 18;
        var fit = Math.Min((paperWidth - 2 * HardMargin) / pageSize.Width, (paperHeight - 2 * HardMargin) / pageSize.Height);
        var scale = Scaling switch
        {
            PrintScaling.Fit => fit,
            PrintScaling.ShrinkOversized => Math.Min(1, fit),
            _ => 1.0,
        };
        var w = pageSize.Width * scale * canvasScale;
        var h = pageSize.Height * scale * canvasScale;
        PreviewImage.Width = w;
        PreviewImage.Height = h;
        Canvas.SetLeft(PreviewImage, paperX + (paperW - w) / 2);
        Canvas.SetTop(PreviewImage, paperY + (paperH - h) / 2);
        PreviewImage.Clip = new RectangleGeometry(new Rect(
            Math.Max(0, -(paperW - w) / 2), Math.Max(0, -(paperH - h) / 2), Math.Min(w, paperW), Math.Min(h, paperH)));

        PreviewImage.Source = null;
        if (page.IsBlank)
        {
            return;
        }

        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var source = _session.GetSource(page.SourceId!.Value);
        var task = AppServices.Render.RenderAsync(source, page.SourceIndex, page.Rotation,
            (int)Math.Max(1, Math.Round(w * dpi)), (int)Math.Max(1, Math.Round(h * dpi)), RenderPriority.Visible, cts.Token);
        var grayscale = GrayscaleBox.IsChecked == true;
        task.ContinueWith(t =>
        {
            if (!cts.IsCancellationRequested && t.IsCompletedSuccessfully && t.Result is { } bitmap)
            {
                PreviewImage.Source = grayscale
                    ? new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0)
                    : bitmap;
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private (double Width, double Height, string Name) GetPaper()
    {
        try
        {
            if (PrinterBox.SelectedItem is string printer)
            {
                var settings = new PrinterSettings { PrinterName = printer };
                var paper = settings.DefaultPageSettings.PaperSize;
                if (paper.Width > 0 && paper.Height > 0)
                {
                    // PaperSize is in hundredths of an inch.
                    return (paper.Width * 0.72, paper.Height * 0.72, paper.PaperName);
                }
            }
        }
        catch (Exception)
        {
            // Fall back to A4.
        }

        return (595.3, 841.9, "A4");
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        var pages = SelectedPages();
        if (pages is null || PrinterBox.SelectedItem is not string printer)
        {
            return;
        }

        if (!short.TryParse(CopiesBox.Text, out var copies) || copies < 1 || copies > 999)
        {
            StatusText.Text = "部数は 1〜999 で指定してください。";
            return;
        }

        var options = new PrintJobOptions(
            printer,
            copies,
            CollateBox.IsChecked == true,
            pages,
            Scaling,
            Orientation,
            AnnotationsBox.IsChecked == true,
            GrayscaleBox.IsChecked == true);

        SettingsPanel.IsEnabled = false;
        PrintButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = "印刷の準備をしています...";
        _printCts = new CancellationTokenSource();
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Progress.Value = p.Done / (double)p.Total;
            StatusText.Text = $"印刷中... {p.Done} / {p.Total} ページ";
        });

        try
        {
            await PrintService.PrintAsync(_session, options, progress, _printCts.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "印刷を中止しました。";
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Empty;
            AppServices.Dialogs.ShowError($"印刷できませんでした。\n{ex.Message}", "印刷");
        }
        finally
        {
            _printCts = null;
            SettingsPanel.IsEnabled = true;
            PrintButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_printCts is not null)
        {
            _printCts.Cancel();
            return;
        }

        DialogResult = false;
    }
}
