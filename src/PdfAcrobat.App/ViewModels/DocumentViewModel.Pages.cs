using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfAcrobat.App.Services;
using PdfAcrobat.Core.Documents;
using PdfAcrobat.Core.Export;
using PdfAcrobat.Core.Import;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.ViewModels;

public enum SplitMode
{
    PageCount,
    FileCount,
    Ranges,
    TopLevelBookmarks,
}

public sealed record SplitOptions(SplitMode Mode, int Value, string OutputFolder, string BaseName)
{
    /// <summary>For <see cref="SplitMode.Ranges"/>: the pages of each output file.</summary>
    public IReadOnlyList<IReadOnlyList<int>> Ranges { get; init; } = [];
}

/// <summary>Page organization, undo/redo and saving (Phase 2).</summary>
public sealed partial class DocumentViewModel
{
    private static readonly PdfSize DefaultPageSize = new(595.28, 841.89); // A4

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyMessage { get; set; } = string.Empty;

    /// <summary>Shows the page grid ("ページを整理") instead of the viewer.</summary>
    [ObservableProperty]
    public partial bool IsOrganizeMode { get; set; }

    /// <summary>Pages selected in the page grid or thumbnail panel (zero-based, ascending).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText), nameof(HasPageSelection))]
    public partial IReadOnlyList<int> SelectedPages { get; set; } = [];

    public bool HasPageSelection => SelectedPages.Count > 0;

    partial void OnSelectedPagesChanged(IReadOnlyList<int> oldValue, IReadOnlyList<int> newValue)
    {
        foreach (var index in oldValue)
        {
            if (index < Thumbnails.Count)
            {
                Thumbnails[index].IsSelected = false;
            }
        }

        foreach (var index in newValue)
        {
            if (index < Thumbnails.Count)
            {
                Thumbnails[index].IsSelected = true;
            }
        }
    }

    public string SelectionText => SelectedPages.Count == 0
        ? $"全 {PageCount} ページ"
        : $"{SelectedPages.Count} / {PageCount} ページを選択";

    /// <summary>Thumbnail width in the page grid (its zoom).</summary>
    [ObservableProperty]
    public partial double OrganizeTileWidth { get; set; } = 150;

    /// <summary>
    /// Pages that page commands act on. In the page grid that is the selection; in the viewer the
    /// selection only counts while it contains the page being viewed, otherwise the current page.
    /// </summary>
    private IReadOnlyList<int> TargetPages =>
        SelectedPages.Count > 0 && (IsOrganizeMode || SelectedPages.Contains(CurrentPageIndex))
            ? SelectedPages
            : [CurrentPageIndex];

    [RelayCommand]
    private void SelectPages(string? kind)
    {
        var all = Enumerable.Range(0, PageCount);
        SelectedPages = kind switch
        {
            "all" => all.ToList(),
            "odd" => all.Where(i => i % 2 == 0).ToList(),
            "even" => all.Where(i => i % 2 == 1).ToList(),
            "portrait" => all.Where(i => Session.GetPageSize(Session.Pages[i]) is var s && s.Height >= s.Width).ToList(),
            "landscape" => all.Where(i => Session.GetPageSize(Session.Pages[i]) is var s && s.Width > s.Height).ToList(),
            _ => [],
        };
    }

    // ---- Undo / Redo ------------------------------------------------------------------

    public bool CanUndo => Session.CanUndo;

    public bool CanRedo => Session.CanRedo;

    public string UndoToolTip => Session.UndoDescription is { } d ? $"元に戻す: {d} (Ctrl+Z)" : "元に戻す (Ctrl+Z)";

    public string RedoToolTip => Session.RedoDescription is { } d ? $"やり直し: {d} (Ctrl+Y)" : "やり直し (Ctrl+Y)";

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Session.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Session.Redo();

    private void OnHistoryChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoToolTip));
        OnPropertyChanged(nameof(RedoToolTip));
        OnPropertyChanged(nameof(SelectionText));
    }

    [RelayCommand]
    private void ToggleOrganizeMode() => IsOrganizeMode = !IsOrganizeMode;

    partial void OnIsOrganizeModeChanged(bool value)
    {
        // Entering the grid selects the page being viewed. Leaving it clears the selection so that page
        // commands in the viewer act on the visible page rather than on pages selected out of sight.
        if (value)
        {
            if (!SelectedPages.Contains(CurrentPageIndex))
            {
                SelectedPages = [CurrentPageIndex];
            }
        }
        else
        {
            SelectedPages = [];
        }
    }

    /// <summary>Leaves the page grid and shows a page in the viewer.</summary>
    public void OpenPageInViewer(int pageIndex)
    {
        CurrentPageIndex = Math.Clamp(pageIndex, 0, PageCount - 1);
        IsOrganizeMode = false;
    }

    // ---- Page operations ----------------------------------------------------------------

    [RelayCommand]
    private void RotateLeft() => RotatePages(TargetPages, -1);

    [RelayCommand]
    private void RotateRight() => RotatePages(TargetPages, +1);

    [RelayCommand]
    private void DeleteSelectedPages() => DeletePages(TargetPages);

    [RelayCommand]
    private void InsertBlankPage() => InsertBlankPages(InsertionIndex, 1);

    [RelayCommand]
    private async Task InsertFromFile()
    {
        var paths = AppServices.Dialogs.PickFiles(InsertFileFilter, multiselect: true, title: "挿入するファイルを選択");
        if (paths.Length > 0)
        {
            await InsertFilesAsync(InsertionIndex, paths);
        }
    }

    [RelayCommand]
    private Task InsertFromClipboard() => PastePagesAtAsync(InsertionIndex);

    /// <summary>Asks for files and inserts them before page <paramref name="at"/>.</summary>
    public async Task InsertFromFileAtAsync(int at)
    {
        var paths = AppServices.Dialogs.PickFiles(InsertFileFilter, multiselect: true, title: "挿入するファイルを選択");
        if (paths.Length > 0)
        {
            await InsertFilesAsync(at, paths);
        }
    }

    [RelayCommand]
    private void DuplicateSelectedPages() => DuplicatePages(TargetPages);

    // ---- Copy & paste ---------------------------------------------------------------

    [RelayCommand]
    private Task CopyPages() => CopyPagesAsync(TargetPages, cut: false);

    [RelayCommand]
    private Task CutPages() => CopyPagesAsync(TargetPages, cut: true);

    [RelayCommand]
    private Task PastePages() => PastePagesAtAsync(InsertionIndex);

    /// <summary>Copies pages (as a small PDF) so they can be pasted into this or another document.</summary>
    public async Task CopyPagesAsync(IReadOnlyList<int> pages, bool cut)
    {
        var ordered = pages.Where(i => i >= 0 && i < PageCount).Distinct().Order().ToList();
        if (ordered.Count == 0)
        {
            return;
        }

        await RunBusyAsync("ページをコピーしています...", async () =>
        {
            var bytes = await Task.Run(() => DocumentExporter.Export(Session, new ExportOptions { PageIndices = ordered, PreserveDocumentStructure = false }));
            PageClipboard.Set(bytes, ordered.Count);
        });

        if (cut)
        {
            DeletePages(ordered);
        }
    }

    /// <summary>Pastes copied pages before page <paramref name="at"/>; falls back to an image or text on the clipboard.</summary>
    public async Task PastePagesAtAsync(int at)
    {
        if (PageClipboard.Get() is { } copied)
        {
            try
            {
                var source = PdfSource.Load(copied.Pdf, "貼り付けたページ.pdf");
                InsertSources(at, [source], copied.PageCount == 1 ? "ページを貼り付け" : $"{copied.PageCount} ページを貼り付け");
            }
            catch (PdfiumException ex)
            {
                AppServices.Dialogs.ShowError($"ページを貼り付けられませんでした。\n{ex.Message}");
            }

            return;
        }

        if (await SourceLoader.LoadClipboardAsync("クリップボード.pdf", "貼り付け") is { } clip)
        {
            InsertSources(at, [clip], "クリップボードから挿入");
        }
    }

    [RelayCommand]
    private Task ExtractPages() => _main.ShowExtractDialogAsync(this);

    [RelayCommand]
    private Task ReplacePages() => _main.ShowReplaceDialogAsync(this);

    [RelayCommand]
    private Task SplitDocument() => _main.ShowSplitDialogAsync(this);

    public const string InsertFileFilter = "PDF・画像ファイル|*.pdf;*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.heif;*.webp|PDF ファイル|*.pdf|すべてのファイル|*.*";

    /// <summary>Insert after the selection (or after the current page).</summary>
    private int InsertionIndex => (SelectedPages.Count > 0 ? SelectedPages.Max() : CurrentPageIndex) + 1;

    public void RotatePages(IReadOnlyCollection<int> indices, int quarterTurns)
    {
        if (indices.Count == 0)
        {
            return;
        }

        var set = indices.ToHashSet();
        Session.Apply(quarterTurns > 0 ? "ページを右に回転" : "ページを左に回転", s => s with
        {
            Pages = s.Pages.Select((p, i) => set.Contains(i) ? p.Rotate(quarterTurns) : p).ToImmutableList(),
        });
    }

    public bool DeletePages(IReadOnlyCollection<int> indices)
    {
        var set = indices.Where(i => i >= 0 && i < PageCount).ToHashSet();
        if (set.Count == 0)
        {
            return false;
        }

        if (set.Count >= PageCount)
        {
            AppServices.Dialogs.ShowInfo("すべてのページを削除することはできません。少なくとも 1 ページは残す必要があります。", "ページの削除");
            return false;
        }

        var first = set.Min();
        Session.Apply($"{set.Count} ページを削除", s => s with
        {
            Pages = s.Pages.Where((_, i) => !set.Contains(i)).ToImmutableList(),
        });
        SelectedPages = [Math.Min(first, PageCount - 1)];
        return true;
    }

    /// <summary>Moves the given pages (kept in document order) so they start before <paramref name="insertBefore"/>.</summary>
    public void MovePages(IReadOnlyCollection<int> indices, int insertBefore)
    {
        var set = indices.Where(i => i >= 0 && i < PageCount).Distinct().Order().ToList();
        if (set.Count == 0)
        {
            return;
        }

        var pages = Session.Pages;
        var moving = set.Select(i => pages[i]).ToList();
        var remaining = pages.Where((_, i) => !set.Contains(i)).ToList();
        var target = Math.Clamp(insertBefore - set.Count(i => i < insertBefore), 0, remaining.Count);
        remaining.InsertRange(target, moving);
        if (remaining.SequenceEqual(pages))
        {
            return;
        }

        Session.Apply(set.Count == 1 ? "ページを移動" : $"{set.Count} ページを移動", s => s with { Pages = remaining.ToImmutableList() });
        SelectedPages = Enumerable.Range(target, moving.Count).ToList();
    }

    public void InsertBlankPages(int at, int count)
    {
        at = Math.Clamp(at, 0, PageCount);
        var neighbor = Session.Pages[Math.Clamp(at == 0 ? 0 : at - 1, 0, PageCount - 1)];
        var size = PageCount > 0 ? Session.GetPageSize(neighbor) : DefaultPageSize;
        var blanks = Enumerable.Range(0, count).Select(_ => PageRef.Blank(size)).ToList();
        Session.Apply("空白ページを挿入", s => s with { Pages = s.Pages.InsertRange(at, blanks) });
        SelectedPages = Enumerable.Range(at, count).ToList();
    }

    public void DuplicatePages(IReadOnlyCollection<int> indices)
    {
        var ordered = indices.Where(i => i >= 0 && i < PageCount).Distinct().Order().ToList();
        if (ordered.Count == 0)
        {
            return;
        }

        var copies = ordered.Select(i => Session.Pages[i].Duplicate()).ToList();
        var at = ordered[^1] + 1;
        Session.Apply("ページを複製", s => s with { Pages = s.Pages.InsertRange(at, copies) });
        SelectedPages = Enumerable.Range(at, copies.Count).ToList();
    }

    /// <summary>Loads PDF or image files (asking for passwords as needed) and inserts their pages.</summary>
    public async Task<bool> InsertFilesAsync(int at, IReadOnlyList<string> paths)
    {
        var sources = await LoadSourcesAsync(paths);
        if (sources.Count == 0)
        {
            return false;
        }

        InsertSources(at, sources, sources.Count == 1 ? $"{sources[0].Name} を挿入" : $"{sources.Count} ファイルを挿入");
        return true;
    }

    private void InsertSources(int at, IReadOnlyList<PdfSource> sources, string description)
    {
        at = Math.Clamp(at, 0, PageCount);
        var refs = new List<PageRef>();
        foreach (var source in sources)
        {
            Session.AddSource(source);
            refs.AddRange(Enumerable.Range(0, source.PageCount).Select(i => PageRef.FromSource(source.Id, i)));
        }

        Session.Apply(description, s => s with { Pages = s.Pages.InsertRange(at, refs) });
        SelectedPages = Enumerable.Range(at, refs.Count).ToList();
    }

    /// <summary>Loads files as page sources. PDFs may prompt for a password; images are converted.</summary>
    public async Task<List<PdfSource>> LoadSourcesAsync(IReadOnlyList<string> paths)
    {
        try
        {
            return await SourceLoader.LoadAsync(paths, message =>
            {
                BusyMessage = message;
                IsBusy = true;
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Replaces pages starting at <paramref name="firstIndex"/> with pages of another file.</summary>
    public void ReplacePagesWith(IReadOnlyList<int> targets, PdfSource source, IReadOnlyList<int> sourcePages)
    {
        var count = Math.Min(targets.Count, sourcePages.Count);
        if (count == 0)
        {
            return;
        }

        Session.AddSource(source);
        var ordered = targets.Order().Take(count).ToList();
        Session.Apply($"{count} ページを置換", s =>
        {
            var pages = s.Pages;
            for (var k = 0; k < count; k++)
            {
                pages = pages.SetItem(ordered[k], PageRef.FromSource(source.Id, sourcePages[k]));
            }

            return s with { Pages = pages };
        });
        SelectedPages = ordered;
    }

    /// <summary>Extracts pages into a new tab, or into one file per page in a folder.</summary>
    public async Task ExtractAsync(IReadOnlyList<int> pages, bool deleteAfter, string? separateFilesFolder)
    {
        if (pages.Count == 0)
        {
            return;
        }

        var baseName = Path.GetFileNameWithoutExtension(Session.DisplayName);
        await RunBusyAsync("ページを抽出しています...", async () =>
        {
            if (separateFilesFolder is not null)
            {
                await Task.Run(() =>
                {
                    foreach (var index in pages)
                    {
                        var bytes = DocumentExporter.Export(Session, new ExportOptions { PageIndices = [index], PreserveDocumentStructure = false });
                        File.WriteAllBytes(UniquePath(separateFilesFolder, $"{baseName}_{index + 1}.pdf"), bytes);
                    }
                });
                AppServices.Dialogs.ShowInfo($"{pages.Count} 個のファイルを作成しました。\n{separateFilesFolder}", "ページの抽出");
            }
            else
            {
                var bytes = await Task.Run(() => DocumentExporter.Export(Session, new ExportOptions { PageIndices = pages, PreserveDocumentStructure = false }));
                var source = PdfSource.Load(bytes, $"{baseName} のページ {PageRangeParser.Format(pages)}.pdf");
                _main.AddDocument(DocumentSession.CreateUnsaved(source.Name, [source]));
            }
        });

        if (deleteAfter)
        {
            DeletePages(pages.ToList());
        }
    }

    public async Task<int> SplitAsync(SplitOptions options)
    {
        var groups = new List<(string Name, List<int> Pages)>();
        switch (options.Mode)
        {
            case SplitMode.PageCount:
                for (var start = 0; start < PageCount; start += options.Value)
                {
                    groups.Add(($"{options.BaseName}_{groups.Count + 1:00}", Enumerable.Range(start, Math.Min(options.Value, PageCount - start)).ToList()));
                }

                break;
            case SplitMode.FileCount:
                var perFile = (int)Math.Ceiling(PageCount / (double)Math.Max(1, options.Value));
                for (var start = 0; start < PageCount; start += perFile)
                {
                    groups.Add(($"{options.BaseName}_{groups.Count + 1:00}", Enumerable.Range(start, Math.Min(perFile, PageCount - start)).ToList()));
                }

                break;
            case SplitMode.Ranges:
                foreach (var range in options.Ranges)
                {
                    groups.Add(($"{options.BaseName}_{groups.Count + 1:00}", range.ToList()));
                }

                break;
            case SplitMode.TopLevelBookmarks:
                var starts = Bookmarks
                    .Where(b => b.Destination is not null)
                    .Select(b => (b.Title, Index: Session.Pages.FindIndex(p => p.SourceId == Session.PrimarySource.Id && p.SourceIndex == b.Destination!.PageIndex)))
                    .Where(x => x.Index >= 0)
                    .DistinctBy(x => x.Index)
                    .OrderBy(x => x.Index)
                    .ToList();
                if (starts.Count == 0)
                {
                    AppServices.Dialogs.ShowInfo("分割に使える最上位のしおりがありません。", "文書の分割");
                    return 0;
                }

                if (starts[0].Index > 0)
                {
                    starts.Insert(0, ("先頭", 0));
                }

                for (var k = 0; k < starts.Count; k++)
                {
                    var end = k + 1 < starts.Count ? starts[k + 1].Index : PageCount;
                    groups.Add(($"{options.BaseName}_{k + 1:00}_{SafeFileName(starts[k].Title)}", Enumerable.Range(starts[k].Index, end - starts[k].Index).ToList()));
                }

                break;
        }

        await RunBusyAsync("文書を分割しています...", () => Task.Run(() =>
        {
            Directory.CreateDirectory(options.OutputFolder);
            foreach (var (name, pages) in groups)
            {
                var bytes = DocumentExporter.Export(Session, new ExportOptions { PageIndices = pages, PreserveDocumentStructure = false });
                File.WriteAllBytes(UniquePath(options.OutputFolder, name + ".pdf"), bytes);
            }
        }));
        return groups.Count;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }

    private static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
        }

        return path;
    }

    // ---- Saving ----------------------------------------------------------------------------

    public bool IsNewDocument => Session.FilePath is null;

    /// <summary>Save (or Save As for new documents). Returns false when cancelled or failed.</summary>
    public async Task<bool> SaveInteractiveAsync() =>
        Session.FilePath is { } existing ? await SaveToAsync(existing) : await SaveAsCore();

    [RelayCommand]
    private async Task Save()
    {
        if (Session.FilePath is not { } path)
        {
            await SaveAsCore();
            return;
        }

        await SaveToAsync(path);
    }

    [RelayCommand]
    private Task SaveAs() => SaveAsCore();

    private async Task<bool> SaveAsCore()
    {
        var suggested = Path.GetFileName(Session.FilePath ?? Session.DisplayName);
        var path = AppServices.Dialogs.PickSavePath(suggested);
        return path is not null && await SaveToAsync(path);
    }

    /// <summary>Writes the document to <paramref name="path"/> (atomically) and marks it saved.</summary>
    public async Task<bool> SaveToAsync(string path)
    {
        var unchanged = DocumentExporter.IsUnchanged(Session, Session.Pages);
        if (Session.PrimarySource.Info.IsEncrypted && !unchanged
            && !AppServices.Dialogs.Confirm(
                "この文書はパスワードで保護されています。\n現在のバージョンでは、変更して保存するとパスワード保護が解除されます（保護の再設定は Phase 8 で対応予定）。\n\n保存を続けますか？",
                "保存"))
        {
            return false;
        }

        var ok = false;
        await RunBusyAsync("保存しています...", async () =>
        {
            var bytes = await Task.Run(() => DocumentExporter.Export(Session));
            await Task.Run(() =>
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
                var temp = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
            });
            Session.MarkSaved(path);
            _main.OnDocumentSaved(this, path);
            ok = true;
        });
        if (ok)
        {
            await AppServices.AutoSave.DiscardAsync(Session);
        }

        OnPropertyChanged(nameof(IsNewDocument));
        return ok;
    }

    private async Task RunBusyAsync(string message, Func<Task> action)
    {
        BusyMessage = message;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfiumException or InvalidOperationException)
        {
            AppServices.Dialogs.ShowError(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void OpenFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening Explorer is a convenience.
        }
    }
}
