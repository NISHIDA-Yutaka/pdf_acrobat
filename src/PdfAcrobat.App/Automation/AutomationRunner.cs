using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfAcrobat.App.Controls.Viewer;
using PdfAcrobat.App.Services;
using PdfAcrobat.App.ViewModels;
using PdfAcrobat.App.Views;
using PdfAcrobat.App.Views.Dialogs;

namespace PdfAcrobat.App.Automation;

/// <summary>
/// Runs a plain-text script of UI steps and captures screenshots. Used during development to check
/// the real application end to end (see docs/DEVELOPMENT.md). One command per line, '#' starts a comment.
/// </summary>
public sealed class AutomationRunner(MainWindow window, MainViewModel viewModel)
{
    private StreamWriter? _log;
    private string _baseDirectory = Environment.CurrentDirectory;

    public async Task RunAsync(string scriptPath)
    {
        var exitCode = 0;
        var fullScript = Path.GetFullPath(scriptPath);
        _baseDirectory = Path.GetDirectoryName(fullScript)!;
        var logPath = Path.ChangeExtension(fullScript, ".log");
        _log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        AppServices.Dialogs.AutomationLog = Log;
        try
        {
            await WaitIdleAsync();
            var lineNumber = 0;
            foreach (var rawLine in await File.ReadAllLinesAsync(fullScript))
            {
                lineNumber++;
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                Log($"> {line}");
                try
                {
                    if (!await ExecuteAsync(line))
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    Log($"ERROR line {lineNumber}: {ex}");
                    break;
                }
            }
        }
        finally
        {
            Log(exitCode == 0 ? "DONE" : "FAILED");
            _log.Dispose();
            Application.Current.Shutdown(exitCode);
        }
    }

    private void Log(string message) => _log?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    private string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_baseDirectory, path));

    private DocumentViewModel Document => viewModel.ActiveDocument ?? throw new InvalidOperationException("文書が開かれていません。");

    private PdfViewer ActiveViewer =>
        FindVisualChildren<DocumentView>(window).FirstOrDefault(v => v.IsVisible)?.PdfViewer
        ?? throw new InvalidOperationException("ビューアが見つかりません。");

    private async Task<bool> ExecuteAsync(string line)
    {
        var parts = SplitArguments(line);
        var command = parts[0].ToLowerInvariant();
        var args = parts.Skip(1).ToArray();
        switch (command)
        {
            case "exit":
                return false;
            case "wait":
                await Task.Delay(int.Parse(args[0], CultureInfo.InvariantCulture));
                break;
            case "wait-idle":
                await WaitIdleAsync();
                break;
            case "size":
                window.WindowState = WindowState.Normal;
                window.Width = double.Parse(args[0], CultureInfo.InvariantCulture);
                window.Height = double.Parse(args[1], CultureInfo.InvariantCulture);
                await WaitIdleAsync();
                break;
            case "theme":
                ThemeService.Apply(Enum.Parse<AppTheme>(args[0], ignoreCase: true));
                await WaitIdleAsync();
                break;
            case "open":
                var opened = viewModel.OpenFile(Resolve(args[0]), args.Length > 1 ? args[1] : null);
                Log(opened is null ? "open failed" : $"opened {opened.Session.DisplayName}: {opened.PageCount} pages");
                await WaitIdleAsync();
                break;
            case "home":
                viewModel.SelectedTab = viewModel.Home;
                await WaitIdleAsync();
                break;
            case "tab":
                viewModel.SelectedTab = viewModel.Tabs[int.Parse(args[0], CultureInfo.InvariantCulture)];
                await WaitIdleAsync();
                break;
            case "close-tab":
                viewModel.CloseTab(viewModel.SelectedTab);
                await WaitIdleAsync();
                break;
            case "page":
                Document.CurrentPageIndex = int.Parse(args[0], CultureInfo.InvariantCulture) - 1;
                await WaitIdleAsync();
                break;
            case "zoom":
                switch (args[0])
                {
                    case "fit-width":
                        Document.ZoomMode = ZoomMode.FitWidth;
                        break;
                    case "fit-page":
                        Document.ZoomMode = ZoomMode.FitPage;
                        break;
                    default:
                        Document.SetZoom(double.Parse(args[0].TrimEnd('%'), CultureInfo.InvariantCulture) / 100.0);
                        break;
                }

                await WaitIdleAsync();
                break;
            case "layout":
                Document.LayoutMode = args[0] switch
                {
                    "single" => PageLayoutMode.SinglePage,
                    "two" => PageLayoutMode.TwoPage,
                    "two-continuous" => PageLayoutMode.TwoPageContinuous,
                    _ => PageLayoutMode.Continuous,
                };
                await WaitIdleAsync();
                break;
            case "rotate-view":
                Document.RotateViewClockwiseCommand.Execute(null);
                await WaitIdleAsync();
                break;
            case "panel":
                Document.ActivePanel = args[0] switch
                {
                    "thumbnails" => SidePanel.Thumbnails,
                    "bookmarks" => SidePanel.Bookmarks,
                    "search" => SidePanel.SearchResults,
                    _ => SidePanel.None,
                };
                await WaitIdleAsync();
                break;
            case "tools":
                Document.IsToolsPaneOpen = args[0] == "on";
                await WaitIdleAsync();
                break;
            case "find":
                Document.IsFindBarOpen = true;
                Document.SearchText = string.Join(' ', args);
                await Document.StartSearchAsync();
                Log($"search: {Document.SearchStatus}");
                await WaitIdleAsync();
                break;
            case "find-next":
                await Document.FindNextCommand.ExecuteAsync(null);
                Log($"search: {Document.SearchStatus}");
                await WaitIdleAsync();
                break;
            case "select-all":
                ActiveViewer.SelectAll();
                await WaitIdleAsync();
                break;
            case "copy-text":
                var text = await ActiveViewer.GetSelectedTextAsync();
                Log($"selected text ({text.Length} chars): {text[..Math.Min(text.Length, 160)].ReplaceLineEndings(" / ")}");
                break;
            case "scroll":
                ScrollViewerOf(ActiveViewer)?.ScrollToVerticalOffset(double.Parse(args[0], CultureInfo.InvariantCulture));
                await WaitIdleAsync();
                break;
            case "print-to-file":
                await PrintService.PrintAsync(Document.Session, new PrintJobOptions(
                    args.Length > 1 ? args[1] : "Microsoft Print to PDF", 1, true,
                    Enumerable.Range(0, Document.PageCount).ToList(), PrintScaling.Fit, PrintOrientation.Auto, true, false,
                    Resolve(args[0])), null, CancellationToken.None);
                Log($"printed to {Resolve(args[0])}");
                break;
            case "screenshot":
                await WaitIdleAsync();
                SaveScreenshot(window, Resolve(args[0]));
                Log($"saved {Resolve(args[0])}");
                break;
            case "dialog-screenshot":
                await DialogScreenshotAsync(args[0], Resolve(args[1]));
                break;
            case "log":
                Log(string.Join(' ', args));
                break;
            case "click":
            {
                // Viewer coordinates (DIPs from the viewer's top-left).
                var viewer = ActiveViewer;
                var p = new Point(double.Parse(args[0], CultureInfo.InvariantCulture), double.Parse(args[1], CultureInfo.InvariantCulture));
                var count = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 1;
                viewer.HandlePress(viewer.ViewportToCanvas(p), p, count);
                viewer.HandleRelease(p);
                await WaitIdleAsync();
                Log($"after click: page {Document.CurrentPageNumber}, selection={viewer.HasSelection}");
                break;
            }

            case "drag":
            {
                var viewer = ActiveViewer;
                var from = new Point(double.Parse(args[0], CultureInfo.InvariantCulture), double.Parse(args[1], CultureInfo.InvariantCulture));
                var to = new Point(double.Parse(args[2], CultureInfo.InvariantCulture), double.Parse(args[3], CultureInfo.InvariantCulture));
                viewer.HandlePress(viewer.ViewportToCanvas(from), from, 1);
                for (var step = 1; step <= 10; step++)
                {
                    var p = from + (to - from) * (step / 10.0);
                    viewer.HandleMove(viewer.ViewportToCanvas(p), p);
                }

                viewer.HandleRelease(to);
                await WaitIdleAsync();
                break;
            }

            case "layout-info":
                Log(ActiveViewer.DescribeLayout());
                break;
            case "icon-gallery":
                SaveIconGallery(Resolve(args[0]), args.Skip(1));
                break;
            case "state":
                var doc = viewModel.ActiveDocument;
                Log(doc is null
                    ? $"tab: {viewModel.SelectedTab?.Title}"
                    : $"page {doc.CurrentPageNumber}/{doc.PageCount}, zoom {doc.ZoomText} ({doc.ZoomMode}), layout {doc.LayoutMode}, rotation {doc.ViewRotation}, panel {doc.ActivePanel}");
                break;
            default:
                throw new InvalidOperationException($"不明なコマンド: {command}");
        }

        return true;
    }

    private async Task DialogScreenshotAsync(string kind, string path)
    {
        Window dialog = kind switch
        {
            "properties" => new DocumentPropertiesDialog(Document),
            "print" => new PrintDialogWindow(Document),
            "password" => new PasswordDialog("sample.pdf", retry: true),
            _ => throw new InvalidOperationException($"不明なダイアログ: {kind}"),
        };
        dialog.Owner = window;
        dialog.ShowActivated = false;
        dialog.Show();
        await WaitIdleAsync();
        await Task.Delay(300);
        await WaitIdleAsync();
        SaveScreenshot(dialog, path);
        Log($"saved {path}");
        dialog.Close();
    }

    private static async Task WaitIdleAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await AppServices.Render.WaitForIdleAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(60);
        }

        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void SaveScreenshot(Window target, string path)
    {
        if (target.Content is not FrameworkElement content)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(target);
        var width = content.ActualWidth;
        var height = content.ActualHeight;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * dpi.DpiScaleX),
            (int)Math.Ceiling(height * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, width, height);
            dc.DrawRectangle(target.Background ?? Brushes.White, null, rect);
            dc.DrawRectangle(new VisualBrush(content), null, rect);
        }

        bitmap.Render(visual);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Renders the named Fluent icons with their names, to pick icons that exist in the bundled font.</summary>
    private static void SaveIconGallery(string path, IEnumerable<string> names)
    {
        var panel = new WrapPanel { Width = 900, Background = Brushes.White };
        foreach (var name in names)
        {
            if (!Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(name, out var symbol))
            {
                continue;
            }

            var item = new StackPanel { Width = 150, Margin = new Thickness(4) };
            item.Children.Add(new Wpf.Ui.Controls.SymbolIcon { Symbol = symbol, FontSize = 28, Foreground = Brushes.Black });
            item.Children.Add(new TextBlock { Text = name, FontSize = 10, Foreground = Brushes.Black, TextAlignment = TextAlignment.Center });
            panel.Children.Add(item);
        }

        var host = new Window
        {
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -32000,
            Top = -32000,
            Background = Brushes.White,
        };
        host.Show();
        host.UpdateLayout();
        SaveScreenshot(host, path);
        host.Close();
    }

    private static ScrollViewer? ScrollViewerOf(DependencyObject root) => FindVisualChildren<ScrollViewer>(root).FirstOrDefault();

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static string[] SplitArguments(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result.ToArray();
    }
}
