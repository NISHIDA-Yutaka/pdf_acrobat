using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfAcrobat.App.Services;
using PdfAcrobat.App.ViewModels;
using PdfAcrobat.Core.Documents;

namespace PdfAcrobat.App.Views.Dialogs;

/// <summary>One file in the "combine files" list. The window owns <see cref="Source"/>.</summary>
public sealed partial class CombineFileItem(string path, PdfSource source) : ObservableObject
{
    public string Path { get; } = path;

    public PdfSource Source { get; } = source;

    public string Name { get; } = System.IO.Path.GetFileName(path);

    public string Details { get; } = $"{source.PageCount} ページ ・ {FormatSize(path)}";

    [ObservableProperty]
    public partial int Position { get; set; }

    [ObservableProperty]
    public partial BitmapSource? Thumbnail { get; set; }

    private static string FormatSize(string path)
    {
        try
        {
            var bytes = new FileInfo(path).Length;
            return bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}

public partial class CombineFilesWindow
{
    private readonly MainViewModel _main;
    private readonly CancellationTokenSource _thumbnails = new();
    private Point? _dragStart;
    private CombineFileItem? _dragItem;
    private bool _busy;

    public CombineFilesWindow(MainViewModel main, IReadOnlyList<string> initialFiles)
    {
        InitializeComponent();
        _main = main;
        FileList.ItemsSource = Items;
        Items.CollectionChanged += (_, _) => OnItemsChanged();
        DragOver += OnWindowDragOver;
        Drop += OnWindowDrop;
        OnItemsChanged();
        if (initialFiles.Count > 0)
        {
            Loaded += async (_, _) => await AddFilesAsync(initialFiles, Items.Count);
        }
    }

    public ObservableCollection<CombineFileItem> Items { get; } = new();

    // ---- Adding files ---------------------------------------------------------------------

    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var paths = AppServices.Dialogs.PickFiles(DocumentViewModel.InsertFileFilter, multiselect: true, title: "結合するファイルを選択");
        if (paths.Length > 0)
        {
            await AddFilesAsync(paths, Items.Count);
        }
    }

    /// <summary>Loads PDFs and images (asking for passwords as needed) and inserts them at <paramref name="index"/>.</summary>
    public async Task AddFilesAsync(IReadOnlyList<string> paths, int index)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "ファイルを読み込んでいます...");
        try
        {
            foreach (var path in paths)
            {
                var sources = await SourceLoader.LoadAsync([path], message => BusyText.Text = message);
                if (sources.Count == 0)
                {
                    continue;
                }

                var item = new CombineFileItem(path, sources[0]);
                Items.Insert(Math.Clamp(index++, 0, Items.Count), item);
                _ = LoadThumbnailAsync(item);
            }
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task LoadThumbnailAsync(CombineFileItem item)
    {
        var size = item.Source.PageSizes[0];
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var width = (int)Math.Round(46 * scale);
        var height = (int)Math.Round(46 * scale * size.Height / Math.Max(1, size.Width));
        try
        {
            item.Thumbnail = await AppServices.Render.RenderAsync(item.Source, 0, 0, width, height, RenderPriority.Thumbnail, _thumbnails.Token);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or Pdfium.PdfiumException)
        {
            // The window was closed while rendering.
        }
    }

    // ---- List editing ----------------------------------------------------------------------

    private void OnItemsChanged()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].Position = i + 1;
        }

        EmptyState.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var pages = Items.Sum(i => i.Source.PageCount);
        SummaryText.Text = Items.Count == 0 ? string.Empty : $"{Items.Count} ファイル ・ 合計 {pages} ページ";
        CombineButton.IsEnabled = Items.Count >= 1 && !_busy;
        CombineButton.ToolTip = Items.Count == 1 ? "1 ファイルだけを新しい文書として開きます" : null;
        SortButton.IsEnabled = Items.Count > 1;
        OnSelectionChanged(this, null);
    }

    private List<CombineFileItem> SelectedItems =>
        FileList.SelectedItems.Cast<CombineFileItem>().OrderBy(Items.IndexOf).ToList();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs? e)
    {
        var selected = SelectedItems;
        RemoveButton.IsEnabled = selected.Count > 0;
        MoveUpButton.IsEnabled = selected.Count > 0 && Items.IndexOf(selected[0]) > 0;
        MoveDownButton.IsEnabled = selected.Count > 0 && Items.IndexOf(selected[^1]) < Items.Count - 1;
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelection(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelection(+1);

    private void MoveSelection(int delta)
    {
        var selected = SelectedItems;
        if (selected.Count == 0)
        {
            return;
        }

        var ordered = delta < 0 ? selected : Enumerable.Reverse(selected).ToList();
        foreach (var item in ordered)
        {
            var index = Items.IndexOf(item);
            var target = index + delta;
            if (target < 0 || target >= Items.Count || selected.Contains(Items[target]))
            {
                return;
            }

            Items.Move(index, target);
        }

        foreach (var item in selected)
        {
            FileList.SelectedItems.Add(item);
        }

        FileList.ScrollIntoView(delta < 0 ? selected[0] : selected[^1]);
        OnSelectionChanged(this, null);
    }

    private void OnRemove(object sender, RoutedEventArgs e) => RemoveSelection();

    private void RemoveSelection()
    {
        foreach (var item in SelectedItems)
        {
            Items.Remove(item);
            Release(item);
        }
    }

    private void OnSortByName(object sender, RoutedEventArgs e)
    {
        var sorted = Items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            Items.Move(Items.IndexOf(sorted[i]), i);
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Delete)
        {
            RemoveSelection();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down)
        {
            MoveSelection(key == Key.Up ? -1 : +1);
            e.Handled = true;
        }
    }

    // ---- Drag & drop -------------------------------------------------------------------------

    private static CombineFileItem? ItemAt(DependencyObject? element)
    {
        while (element is not null and not ListBoxItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        return (element as ListBoxItem)?.DataContext as CombineFileItem;
    }

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = ItemAt(e.OriginalSource as DependencyObject);
        _dragStart = _dragItem is null ? null : e.GetPosition(FileList);
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _dragItem is not { } item || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(FileList) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragStart = null;
        DragDrop.DoDragDrop(FileList, new DataObject(typeof(CombineFileItem), item), DragDropEffects.Move);
        _dragItem = null;
    }

    /// <summary>Reorders live while an item is dragged over the list; accepts files from Explorer.</summary>
    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(CombineFileItem)) is CombineFileItem dragged)
        {
            e.Effects = DragDropEffects.Move;
            var target = ItemAt(e.OriginalSource as DependencyObject);
            if (target is not null && !ReferenceEquals(target, dragged))
            {
                Items.Move(Items.IndexOf(dragged), Items.IndexOf(target));
            }
        }
        else
        {
            e.Effects = DroppedFiles(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        }

        e.Handled = true;
    }

    private async void OnListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = DroppedFiles(e);
        if (files.Count == 0)
        {
            return;
        }

        var target = ItemAt(e.OriginalSource as DependencyObject);
        await AddFilesAsync(files, target is null ? Items.Count : Items.IndexOf(target) + 1);
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFiles(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = DroppedFiles(e);
        if (files.Count > 0)
        {
            await AddFilesAsync(files, Items.Count);
        }
    }

    private static List<string> DroppedFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => File.Exists(f) && (string.Equals(System.IO.Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase)
                                                  || Core.Import.ImagePdfConverter.IsImage(f))).ToList()
            : [];

    // ---- Combine -------------------------------------------------------------------------------

    private async void OnCombine(object sender, RoutedEventArgs e)
    {
        if (Items.Count == 0 || _busy)
        {
            return;
        }

        SetBusy(true, "ファイルを結合しています...");
        DocumentViewModel? result;
        try
        {
            result = await _main.CombineAsync(Items.Select(i => i.Source).ToList(), BookmarksBox.IsChecked == true);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }

        if (result is not null)
        {
            DialogResult = true;
        }
    }

    private void SetBusy(bool busy, string message)
    {
        _busy = busy;
        BusyText.Text = message;
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CombineButton.IsEnabled = !busy && Items.Count > 0;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _thumbnails.Cancel();
        foreach (var item in Items)
        {
            Release(item);
        }

        base.OnClosed(e);
    }

    private static void Release(CombineFileItem item)
    {
        AppServices.Render.ClearSource(item.Source.Id);
        item.Source.Dispose();
    }
}
