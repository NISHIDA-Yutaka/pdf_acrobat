using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfAcrobat.App.Services;
using PdfAcrobat.App.Views;
using PdfAcrobat.App.Views.Dialogs;
using PdfAcrobat.Core.Documents;
using PdfAcrobat.Core.Export;
using PdfAcrobat.Core.Import;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private int _untitledCounter;

    public MainViewModel()
    {
        Home = new HomeViewModel(this);
        Tabs.Add(Home);
        SelectedTab = Home;
    }

    public HomeViewModel Home { get; }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveDocument), nameof(WindowTitle))]
    public partial TabViewModel? SelectedTab { get; set; }

    public DocumentViewModel? ActiveDocument => SelectedTab as DocumentViewModel;

    public IEnumerable<DocumentViewModel> Documents => Tabs.OfType<DocumentViewModel>();

    public string WindowTitle => ActiveDocument is { } doc ? $"{doc.Title} - {AppInfo.DisplayName}" : AppInfo.DisplayName;

    [ObservableProperty]
    public partial bool IsDarkTheme { get; set; } = ThemeService.IsDark;

    private static Window? Owner => Application.Current?.MainWindow;

    partial void OnSelectedTabChanged(TabViewModel? value)
    {
        foreach (var tab in Tabs)
        {
            tab.IsSelected = ReferenceEquals(tab, value);
        }
    }

    public void OnDocumentTitleChanged(DocumentViewModel document)
    {
        if (ReferenceEquals(document, SelectedTab))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    // ---- Opening & closing -----------------------------------------------------------------

    [RelayCommand]
    private void OpenFile()
    {
        var files = AppServices.Dialogs.PickPdfFiles();
        if (files.Length > 0)
        {
            OpenFiles(files);
        }
    }

    public void OpenFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            OpenFile(path);
        }
    }

    /// <summary>Opens a file (or activates it when already open). Returns null on failure or cancel.</summary>
    public DocumentViewModel? OpenFile(string path, string? password = null)
    {
        var fullPath = Path.GetFullPath(path);
        var existing = Documents.FirstOrDefault(d => string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedTab = existing;
            return existing;
        }

        if (SourceLoader.LoadPdf(fullPath, password) is not { } source)
        {
            return null;
        }

        var document = AddDocument(new DocumentSession(source));
        var recent = AppServices.Settings.Settings.RecentFiles
            .FirstOrDefault(r => string.Equals(r.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (recent is not null && recent.PageIndex > 0 && recent.PageIndex < document.PageCount)
        {
            document.CurrentPageIndex = recent.PageIndex;
        }

        AppServices.Settings.AddRecentFile(fullPath, source.PageCount, null);
        _ = SaveRecentThumbnailAsync(fullPath, source, 0, 0);
        return document;
    }

    /// <summary>Records a saved document in the recent files, with a thumbnail of its first page.</summary>
    public void OnDocumentSaved(DocumentViewModel document, string path)
    {
        AppServices.Settings.AddRecentFile(path, document.PageCount, null);
        if (document.Session.Pages.FirstOrDefault() is { IsBlank: false } first)
        {
            _ = SaveRecentThumbnailAsync(Path.GetFullPath(path), document.Session.GetSource(first.SourceId!.Value), first.SourceIndex, first.Rotation);
        }
    }

    /// <summary>Adds a tab for a session and activates it.</summary>
    public DocumentViewModel AddDocument(DocumentSession session)
    {
        var document = new DocumentViewModel(session, this);
        Tabs.Add(document);
        SelectedTab = document;
        return document;
    }

    private string NextUntitledName(string prefix) => $"{prefix} {++_untitledCounter}.pdf";

    private static async Task SaveRecentThumbnailAsync(string path, PdfSource source, int pageIndex, int rotation)
    {
        try
        {
            var size = source.PageSizes[pageIndex];
            if (rotation % 2 == 1)
            {
                size = new PdfSize(size.Height, size.Width);
            }

            const int Width = 160;
            var height = (int)Math.Round(Width * size.Height / Math.Max(1, size.Width));
            var bitmap = await AppServices.Render.RenderAsync(source, pageIndex, rotation, Width, height, RenderPriority.Background, CancellationToken.None);
            if (bitmap is null)
            {
                return;
            }

            var folder = Path.Combine(AppInfo.LocalDataFolder, "thumbnails");
            Directory.CreateDirectory(folder);
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
            var file = Path.Combine(folder, hash + ".png");
            await Task.Run(() =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(file);
                encoder.Save(stream);
            });

            var entry = AppServices.Settings.Settings.RecentFiles
                .FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                entry.ThumbnailPath = file;
                AppServices.Settings.Save();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfiumException)
        {
            // Thumbnails are cosmetic.
        }
    }

    [RelayCommand]
    public async Task CloseTab(TabViewModel? tab)
    {
        if (tab is not DocumentViewModel document)
        {
            return;
        }

        if (document.IsBusy)
        {
            // Saving, extracting...: the document is in use until the operation finishes.
            SelectedTab = document;
            return;
        }

        if (!await ConfirmDiscardAsync(document))
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (document.FilePath is { } path)
        {
            AppServices.Settings.UpdateLastPage(path, document.CurrentPageIndex);
            AppServices.Settings.Save();
        }

        // Saved or discarded: drop the auto-saved copy (after a running auto-save that still uses the document).
        await AppServices.AutoSave.CloseAsync(document.Session);
        if (!Tabs.Contains(tab))
        {
            return;
        }

        Tabs.Remove(tab);
        document.Dispose();
        if (ReferenceEquals(SelectedTab, tab) || SelectedTab is null)
        {
            SelectedTab = Tabs[Math.Clamp(index - 1, 0, Tabs.Count - 1)];
        }
    }

    [RelayCommand]
    private Task CloseActiveTab() => CloseTab(SelectedTab);

    [RelayCommand]
    private void NextTab() => SelectedTab = Tabs[(Tabs.IndexOf(SelectedTab!) + 1) % Tabs.Count];

    [RelayCommand]
    private void PreviousTab() => SelectedTab = Tabs[(Tabs.IndexOf(SelectedTab!) - 1 + Tabs.Count) % Tabs.Count];

    [RelayCommand]
    private void GoHome() => SelectedTab = Home;

    /// <summary>Asks whether unsaved changes should be saved. Returns false to cancel closing.</summary>
    private async Task<bool> ConfirmDiscardAsync(DocumentViewModel document)
    {
        if (!document.Session.IsModified)
        {
            return true;
        }

        SelectedTab = document;
        var answer = AppServices.Dialogs.AskYesNoCancel($"「{document.Session.DisplayName}」への変更を保存しますか？", "変更の保存");
        return answer switch
        {
            null => false,
            true => await document.SaveInteractiveAsync(),
            _ => true,
        };
    }

    /// <summary>Called when the main window is about to close.</summary>
    public async Task<bool> PrepareToExitAsync()
    {
        if (Documents.FirstOrDefault(d => d.IsBusy) is { } busy)
        {
            SelectedTab = busy;
            AppServices.Dialogs.ShowInfo($"「{busy.Session.DisplayName}」の処理が終わってから終了してください。", "終了");
            return false;
        }

        foreach (var document in Documents.ToList())
        {
            if (!await ConfirmDiscardAsync(document))
            {
                return false;
            }
        }

        foreach (var document in Documents)
        {
            if (document.FilePath is { } path)
            {
                AppServices.Settings.UpdateLastPage(path, document.CurrentPageIndex);
            }
        }

        AppServices.Settings.Save();
        return true;
    }

    // ---- Crash recovery ---------------------------------------------------------------------

    /// <summary>
    /// Offers documents auto-saved by an instance that did not exit normally. Restored documents open
    /// as unsaved copies; declined ones are deleted.
    /// </summary>
    public int RecoverDocuments(bool ask)
    {
        var documents = AppServices.AutoSave.FindRecoverable();
        if (documents.Count == 0)
        {
            return 0;
        }

        if (ask)
        {
            var list = string.Join("\n", documents.Take(12).Select(d => $"・{d.DisplayName}（{d.SavedAt:M/d HH:mm} 時点、{d.PageCount} ページ）"));
            if (!AppServices.Dialogs.AskYesNo(
                    $"{AppInfo.DisplayName} が前回正常に終了しなかったため、保存されていない変更が見つかりました。\n\n{list}\n\n" +
                    "自動保存された内容を復元しますか？\n［いいえ］を選ぶと自動保存データは削除されます。",
                    "文書の復元"))
            {
                AppServices.AutoSave.Discard(documents);
                return 0;
            }
        }

        var restored = 0;
        foreach (var recovered in documents)
        {
            try
            {
                var name = $"{Path.GetFileNameWithoutExtension(recovered.DisplayName)} (復元).pdf";
                AddDocument(DocumentSession.CreateUnsaved(name, [PdfSource.Load(File.ReadAllBytes(recovered.PdfPath), name)]));
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfiumException)
            {
                AppServices.Dialogs.ShowError($"「{recovered.DisplayName}」を復元できませんでした。\n{ex.Message}", "文書の復元");
            }
        }

        AppServices.AutoSave.Discard(documents);
        return restored;
    }

    // ---- Creating documents -------------------------------------------------------------------

    /// <summary>New document with one blank A4 page (Ctrl+N).</summary>
    [RelayCommand]
    private void NewBlankDocument() => CreateBlankDocument(PaperSize.A4.ToPoints(landscape: false), 1);

    public DocumentViewModel CreateBlankDocument(PdfSize size, int pageCount)
    {
        var name = NextUntitledName("無題");
        return AddDocument(DocumentSession.CreateUnsaved(name, [DocumentSession.CreateBlankSource(name, size, pageCount)]));
    }

    [RelayCommand]
    private Task CreateFromImages() => CreateFromImagesAsync();

    public async Task<DocumentViewModel?> CreateFromImagesAsync()
    {
        var files = AppServices.Dialogs.PickFiles(ImagePdfConverter.FileFilter, multiselect: true, title: "PDF にする画像を選択");
        return files.Length == 0 ? null : await CreateFromImageFilesAsync(files);
    }

    /// <summary>Converts image files (one page per image) into a new, unsaved document.</summary>
    public async Task<DocumentViewModel?> CreateFromImageFilesAsync(IReadOnlyList<string> files)
    {
        try
        {
            var bytes = await Task.Run(() => ImagePdfConverter.ConvertFiles(files));
            var name = files.Count == 1 ? Path.ChangeExtension(Path.GetFileName(files[0]), ".pdf") : NextUntitledName("画像");
            return AddDocument(DocumentSession.CreateUnsaved(name, [PdfSource.Load(bytes, name)]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                                   or System.Runtime.InteropServices.COMException or FileFormatException or PdfiumException)
        {
            AppServices.Dialogs.ShowError($"画像を PDF に変換できませんでした。\n{ex.Message}");
            return null;
        }
    }

    [RelayCommand]
    private Task CreateFromClipboard() => CreateFromClipboardAsync();

    /// <summary>Creates a document from the image or text on the clipboard.</summary>
    public async Task<DocumentViewModel?> CreateFromClipboardAsync()
    {
        var name = NextUntitledName("クリップボード");
        return await SourceLoader.LoadClipboardAsync(name, "クリップボードから作成") is { } source
            ? AddDocument(DocumentSession.CreateUnsaved(name, [source]))
            : null;
    }

    [RelayCommand]
    private void CombineFiles() => ShowCombineWindow([]);

    public void ShowCombineWindow(IReadOnlyList<string> initialFiles)
    {
        var window = new CombineFilesWindow(this, initialFiles) { Owner = Owner };
        window.ShowDialog();
    }

    /// <summary>Loads files and combines them into a new, unsaved document.</summary>
    public async Task<DocumentViewModel?> CombineFilesAsync(IReadOnlyList<string> paths, bool addBookmarks)
    {
        var sources = await SourceLoader.LoadAsync(paths);
        try
        {
            return sources.Count == 0 ? null : await CombineAsync(sources, addBookmarks);
        }
        finally
        {
            foreach (var source in sources)
            {
                AppServices.Render.ClearSource(source.Id);
                source.Dispose();
            }
        }
    }

    /// <summary>
    /// Combines loaded files into a new, unsaved document. The caller keeps ownership of
    /// <paramref name="sources"/> (they are copied into the new document).
    /// </summary>
    public async Task<DocumentViewModel?> CombineAsync(IReadOnlyList<PdfSource> sources, bool addBookmarks)
    {
        if (sources.Count == 0)
        {
            return null;
        }

        try
        {
            // Every file keeps its own bookmarks: nested under a bookmark named after the file, or
            // merged one after another when no file bookmarks are wanted.
            var name = NextUntitledName("結合ファイル");
            var bookmarks = new List<ExportBookmark>();
            var pageIndex = 0;
            foreach (var source in sources)
            {
                var outline = ExportBookmark.FromOutline(source.Document.GetBookmarks(), pageIndex, source.PageCount);
                if (addBookmarks)
                {
                    bookmarks.Add(new ExportBookmark(Path.GetFileNameWithoutExtension(source.Name), pageIndex) { Children = outline });
                }
                else
                {
                    bookmarks.AddRange(outline);
                }

                pageIndex += source.PageCount;
            }

            // Not disposed on purpose: disposing the session would dispose the caller's sources.
            var combined = DocumentSession.CreateUnsaved(name, sources);
            var bytes = await Task.Run(() => DocumentExporter.Export(combined, new ExportOptions { Bookmarks = bookmarks, ReplaceBookmarks = true }));
            return AddDocument(DocumentSession.CreateUnsaved(name, [PdfSource.Load(bytes, name)]));
        }
        catch (Exception ex) when (ex is IOException or PdfiumException or InvalidOperationException)
        {
            AppServices.Dialogs.ShowError($"ファイルを結合できませんでした。\n{ex.Message}");
            return null;
        }
    }

    // ---- Page tool dialogs -------------------------------------------------------------------

    public async Task ShowExtractDialogAsync(DocumentViewModel document)
    {
        var dialog = new ExtractPagesDialog(document) { Owner = Owner };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string? folder = null;
        if (dialog.SeparateFiles)
        {
            folder = AppServices.Dialogs.PickFolder("抽出したページの保存先フォルダーを選択");
            if (folder is null)
            {
                return;
            }
        }

        await document.ExtractAsync(dialog.Pages, dialog.DeleteAfterExtract, folder);
    }

    public async Task ShowReplaceDialogAsync(DocumentViewModel document)
    {
        var path = AppServices.Dialogs.PickFiles(DocumentViewModel.InsertFileFilter, multiselect: false, title: "置換に使うファイルを選択").FirstOrDefault();
        if (path is null)
        {
            return;
        }

        var sources = await document.LoadSourcesAsync([path]);
        if (sources.Count == 0)
        {
            return;
        }

        var dialog = new ReplacePagesDialog(document, sources[0]) { Owner = Owner };
        if (dialog.ShowDialog() == true)
        {
            document.ReplacePagesWith(dialog.TargetPages, sources[0], dialog.SourcePages);
        }
        else
        {
            sources[0].Dispose();
        }
    }

    public async Task ShowSplitDialogAsync(DocumentViewModel document)
    {
        var dialog = new SplitDialog(document) { Owner = Owner };
        if (dialog.ShowDialog() != true || dialog.Options is not { } options)
        {
            return;
        }

        var count = await document.SplitAsync(options);
        if (count > 0 && AppServices.Dialogs.Confirm($"{count} 個のファイルを作成しました。\n{options.OutputFolder}\n\nフォルダーを開きますか？", "文書の分割"))
        {
            document.OpenFolder(options.OutputFolder);
        }
    }

    // ---- Misc -------------------------------------------------------------------------------

    [RelayCommand]
    private void ToggleTheme()
    {
        var theme = ThemeService.IsDark ? AppTheme.Light : AppTheme.Dark;
        ThemeService.Apply(theme);
        IsDarkTheme = ThemeService.IsDark;
        AppServices.Settings.Settings.Theme = theme;
        AppServices.Settings.Save();
    }

    /// <summary>Runs a tool from the home screen or the "all tools" pane.</summary>
    public void RunTool(string key)
    {
        switch (key)
        {
            case ToolCatalog.Combine:
                ShowCombineWindow(ActiveDocument?.FilePath is { } current ? [current] : []);
                break;
            case ToolCatalog.Create:
                new CreatePdfWindow(this) { Owner = Owner }.ShowDialog();
                break;
            case ToolCatalog.Organize:
                var document = ActiveDocument;
                if (document is null)
                {
                    var files = AppServices.Dialogs.PickPdfFiles(multiselect: false, title: "整理する PDF を選択");
                    document = files.Length > 0 ? OpenFile(files[0]) : null;
                }

                if (document is not null)
                {
                    document.IsOrganizeMode = true;
                    document.SelectedPages = [document.CurrentPageIndex];
                }

                break;
            default:
                OpenFile();
                break;
        }
    }

    public void ShowDocumentProperties(DocumentViewModel document)
    {
        var dialog = new DocumentPropertiesDialog(document) { Owner = Owner };
        dialog.ShowDialog();
    }

    public void PrintDocument(DocumentViewModel document)
    {
        var dialog = new PrintDialogWindow(document) { Owner = Owner };
        dialog.ShowDialog();
    }

    public void ShowFullScreen(DocumentViewModel document)
    {
        var window = new FullScreenWindow(document);
        window.Show();
    }
}
