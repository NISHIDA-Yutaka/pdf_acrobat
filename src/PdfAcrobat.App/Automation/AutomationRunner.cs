using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using PdfAcrobat.App.Controls.PageGrid;
using PdfAcrobat.App.Controls.Viewer;
using PdfAcrobat.App.Services;
using PdfAcrobat.App.ViewModels;
using PdfAcrobat.App.Views;
using PdfAcrobat.App.Views.Dialogs;
using PdfAcrobat.Core.Documents;
using PdfAcrobat.Core.Import;

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
                await viewModel.CloseTab(viewModel.SelectedTab);
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
                await DialogScreenshotAsync(args[0], Resolve(args[1]), args.Skip(2).Select(Resolve).ToList());
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
                    : $"page {doc.CurrentPageNumber}/{doc.PageCount}, zoom {doc.ZoomText} ({doc.ZoomMode}), layout {doc.LayoutMode}, rotation {doc.ViewRotation}, panel {doc.ActivePanel}, title {doc.Title}");
                break;

            // ---- Phase 2: page organization, saving, combining, creating ----
            case "organize":
                Document.IsOrganizeMode = args[0] != "off";
                await WaitIdleAsync();
                if (Document.IsOrganizeMode)
                {
                    Log(ActiveGrid.Describe());
                }

                break;
            case "grid-size":
                Document.OrganizeTileWidth = double.Parse(args[0], CultureInfo.InvariantCulture);
                await WaitIdleAsync();
                Log(ActiveGrid.Describe());
                break;
            case "select-pages":
                Document.SelectedPages = args.Length == 0 ? [] : PageRangeParser.TryParse(string.Join(' ', args), Document.PageCount, out var selection)
                    ? selection.Distinct().Order().ToList()
                    : throw new InvalidOperationException($"ページ範囲が不正です: {string.Join(' ', args)}");
                await WaitIdleAsync();
                break;
            case "page-op":
                await ExecutePageOperationAsync(args[0]);
                await WaitIdleAsync();
                LogPages();
                break;
            case "move-pages":
                Document.MovePages(Document.SelectedPages, int.Parse(args[0], CultureInfo.InvariantCulture) - 1);
                await WaitIdleAsync();
                LogPages();
                break;
            case "insert-file":
                await Document.InsertFilesAsync(int.Parse(args[1], CultureInfo.InvariantCulture) - 1, [Resolve(args[0])]);
                await WaitIdleAsync();
                LogPages();
                break;
            case "pages":
                LogPages();
                break;
            case "grid-click":
            {
                var grid = ActiveGrid;
                var center = grid.TileCenter(int.Parse(args[0], CultureInfo.InvariantCulture) - 1);
                var modifiers = (args.Contains("ctrl") ? ModifierKeys.Control : ModifierKeys.None) | (args.Contains("shift") ? ModifierKeys.Shift : ModifierKeys.None);
                grid.HandlePress(center, modifiers, args.Contains("double") ? 2 : 1);
                grid.HandleRelease(center, cancel: false);
                await WaitIdleAsync();
                Log($"after grid-click: organize={Document.IsOrganizeMode}, page {Document.CurrentPageNumber}, {grid.Describe()}");
                break;
            }

            case "grid-drag":
            {
                // grid-drag <page> <insert before page>: drags the selection (or the page) with the real pointer logic.
                var grid = ActiveGrid;
                var from = grid.TileCenter(int.Parse(args[0], CultureInfo.InvariantCulture) - 1);
                var to = grid.GapPoint(int.Parse(args[1], CultureInfo.InvariantCulture) - 1);
                grid.HandlePress(from, ModifierKeys.None, 1);
                for (var step = 1; step <= 12; step++)
                {
                    var p = from + (to - from) * (step / 12.0);
                    grid.HandleMove(p, grid.CanvasToViewport(p));
                    if (step == 8 && args.Contains("screenshot-midway"))
                    {
                        await WaitIdleAsync();
                        SaveScreenshot(window, Resolve(args[^1]));
                    }
                }

                grid.HandleRelease(to, cancel: false);
                await WaitIdleAsync();
                LogPages();
                break;
            }

            case "save-as":
                Log($"save-as {Resolve(args[0])}: {(await Document.SaveToAsync(Resolve(args[0])) ? "ok" : "failed")}, title {Document.Title}");
                break;
            case "extract":
                PageRangeParser.TryParse(args[0], Document.PageCount, out var extractPages);
                await Document.ExtractAsync(extractPages, deleteAfter: false, args.Length > 1 ? Resolve(args[1]) : null);
                await WaitIdleAsync();
                break;
            case "split":
            {
                // split <pages|files|ranges|bookmarks> <value> <folder> <base name>
                var mode = args[0] switch
                {
                    "files" => SplitMode.FileCount,
                    "ranges" => SplitMode.Ranges,
                    "bookmarks" => SplitMode.TopLevelBookmarks,
                    _ => SplitMode.PageCount,
                };
                PageRangeParser.TryParseGroups(args[1], Document.PageCount, out var groups);
                var options = new SplitOptions(mode, mode == SplitMode.Ranges ? groups.Count : int.TryParse(args[1], out var value) ? value : 0, Resolve(args[2]), args[3]) { Ranges = groups };
                Log($"split: {await Document.SplitAsync(options)} files");
                break;
            }

            case "combine":
            {
                var combined = await viewModel.CombineFilesAsync(args.Skip(1).Select(Resolve).ToList(), args[0] == "bookmarks");
                Log(combined is null ? "combine failed" : $"combined: {combined.Title}, {combined.PageCount} pages, {combined.Bookmarks.Count} bookmarks");
                await WaitIdleAsync();
                break;
            }

            case "create-blank":
            {
                var paper = PaperSize.Common[int.Parse(args[0], CultureInfo.InvariantCulture)];
                var created = viewModel.CreateBlankDocument(paper.ToPoints(args.Contains("landscape")), int.Parse(args[1], CultureInfo.InvariantCulture));
                Log($"created: {created.Title}, {created.PageCount} pages ({paper.Name})");
                await WaitIdleAsync();
                break;
            }

            case "create-images":
            {
                var created = await viewModel.CreateFromImageFilesAsync(args.Select(Resolve).ToList());
                Log(created is null ? "create failed" : $"created: {created.Title}, {created.PageCount} pages");
                await WaitIdleAsync();
                break;
            }

            case "grid-hover":
            {
                // grid-hover <page>: hover actions of a page; grid-hover gap <before page>: the "+" insert button.
                var grid = ActiveGrid;
                if (args[0] == "gap")
                {
                    var point = grid.GapPoint(int.Parse(args[1], CultureInfo.InvariantCulture) - 1);
                    grid.HandleMove(point, grid.CanvasToViewport(point));
                }
                else
                {
                    var index = int.Parse(args[0], CultureInfo.InvariantCulture) - 1;
                    var tile = FindVisualChildren<PageTile>(grid).First(t => t.Index == index);
                    tile.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                }

                await WaitIdleAsync();
                break;
            }

            case "grid-menu":
            {
                // grid-menu <page|0 for the empty area> OUT.png
                var menu = ActiveGrid.ShowContextMenu(Document, int.Parse(args[0], CultureInfo.InvariantCulture) - 1);
                await WaitIdleAsync();
                SaveElementScreenshot(menu, Resolve(args[1]));
                menu.IsOpen = false;
                Log($"saved {Resolve(args[1])}");
                break;
            }

            case "autosave-now":
                await AppServices.AutoSave.SaveNowAsync();
                Log($"auto-save folder: {string.Join(", ", Directory.Exists(AppServices.AutoSave.Folder) ? Directory.GetFiles(AppServices.AutoSave.Folder).Select(Path.GetFileName) : [])}");
                break;
            case "recover":
                Log($"recovered {viewModel.RecoverDocuments(ask: false)} documents");
                await WaitIdleAsync();
                break;
            case "crash":
                // Simulates a crash: the process ends without any clean-up.
                Log("killing the process");
                _log?.Flush();
                System.Diagnostics.Process.GetCurrentProcess().Kill();
                break;
            case "create-text":
            {
                var bytes = TextPdfConverter.Convert(await File.ReadAllTextAsync(Resolve(args[0])));
                var created = viewModel.AddDocument(DocumentSession.CreateUnsaved("テキスト.pdf", [PdfSource.Load(bytes, "テキスト.pdf")]));
                Log($"created: {created.Title}, {created.PageCount} pages");
                await WaitIdleAsync();
                break;
            }
            default:
                throw new InvalidOperationException($"不明なコマンド: {command}");
        }

        return true;
    }

    private async Task ExecutePageOperationAsync(string operation)
    {
        var document = Document;
        switch (operation)
        {
            case "rotate-left":
                document.RotateLeftCommand.Execute(null);
                break;
            case "rotate-right":
                document.RotateRightCommand.Execute(null);
                break;
            case "delete":
                document.DeleteSelectedPagesCommand.Execute(null);
                break;
            case "duplicate":
                document.DuplicateSelectedPagesCommand.Execute(null);
                break;
            case "insert-blank":
                document.InsertBlankPageCommand.Execute(null);
                break;
            case "undo":
                document.UndoCommand.Execute(null);
                break;
            case "redo":
                document.RedoCommand.Execute(null);
                break;
            case "select-odd":
                document.SelectPagesCommand.Execute("odd");
                break;
            case "select-all":
                document.SelectPagesCommand.Execute("all");
                break;
            default:
                throw new InvalidOperationException($"不明なページ操作: {operation}");
        }

        await Task.CompletedTask;
    }

    /// <summary>Logs the page order as source tags ("P3" = page 3 of the original file, "S1p2" = page 2 of the first inserted file).</summary>
    private void LogPages()
    {
        var document = Document;
        var tags = document.Session.Sources
            .Select((s, k) => (s.Id, Tag: s.Id == document.Session.PrimarySource.Id ? "P" : $"S{k}p"))
            .ToDictionary(x => x.Id, x => x.Tag);
        var pages = document.Session.Pages.Select(p =>
            (p.IsBlank ? "blank" : $"{tags[p.SourceId!.Value]}{p.SourceIndex + 1}") + (p.Rotation != 0 ? $"@{p.Rotation * 90}" : string.Empty));
        Log($"pages ({document.PageCount}): {string.Join(' ', pages)} | selected [{PageRangeParser.Format(document.SelectedPages)}] | current {document.CurrentPageNumber}"
            + $" | modified={document.Session.IsModified} | undo={document.Session.UndoDescription ?? "-"} | redo={document.Session.RedoDescription ?? "-"}");
    }

    private PageGridView ActiveGrid =>
        FindVisualChildren<DocumentView>(window).FirstOrDefault(v => v.IsVisible)?.PageGridView
        ?? throw new InvalidOperationException("ページグリッドが見つかりません。");

    private async Task DialogScreenshotAsync(string kind, string path, IReadOnlyList<string> files)
    {
        Window dialog = kind switch
        {
            "properties" => new DocumentPropertiesDialog(Document),
            "print" => new PrintDialogWindow(Document),
            "password" => new PasswordDialog("sample.pdf", retry: true),
            "extract" => new ExtractPagesDialog(Document),
            "split" => new SplitDialog(Document),
            "replace" => new ReplacePagesDialog(Document, Document.Session.PrimarySource),
            "create" => new CreatePdfWindow(viewModel),
            "combine" => new CombineFilesWindow(viewModel, files),
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
        if (target.Content is FrameworkElement content)
        {
            SaveElementScreenshot(content, path, target.Background);
        }
    }

    private static void SaveElementScreenshot(FrameworkElement content, string path, Brush? background = null)
    {
        var dpi = VisualTreeHelper.GetDpi(content);
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
            dc.DrawRectangle(background ?? Brushes.White, null, rect);
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
