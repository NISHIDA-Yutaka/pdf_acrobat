using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfAcrobat.App.ViewModels;
using PdfAcrobat.Core.Text;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.Views;

public partial class DocumentView
{
    private DocumentViewModel? _viewModel;
    private int _thumbnailAnchor = -1;

    public DocumentView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Viewer.ExternalLinkRequested += (_, uri) => _viewModel?.OpenExternalLink(uri);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Dispatcher.BeginInvoke(() => Viewer.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }
        };
    }

    public Controls.Viewer.PdfViewer PdfViewer => Viewer;

    public Controls.PageGrid.PageGridView PageGridView => PageGrid;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.NavigateRequested -= OnNavigateRequested;
            _viewModel.CopyRequested -= OnCopyRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as DocumentViewModel;
        if (_viewModel is not null)
        {
            _viewModel.NavigateRequested += OnNavigateRequested;
            _viewModel.CopyRequested += OnCopyRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnNavigateRequested(object? sender, PdfDestination destination) => Viewer.GoToDestination(destination);

    private void OnCopyRequested(object? sender, EventArgs e) => Viewer.CopySelection();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.IsFindBarOpen) when _viewModel?.IsFindBarOpen == true:
                Dispatcher.BeginInvoke(() =>
                {
                    FindBox.Focus();
                    FindBox.SelectAll();
                }, System.Windows.Threading.DispatcherPriority.Input);
                break;
            case nameof(DocumentViewModel.CurrentPageIndex):
                ScrollThumbnailIntoView();
                break;
            case nameof(DocumentViewModel.CurrentSearchIndex):
                if (_viewModel is { CurrentSearchIndex: >= 0 } vm && vm.CurrentSearchIndex < vm.SearchResults.Count)
                {
                    SearchResultList.SelectedIndex = vm.CurrentSearchIndex;
                    SearchResultList.ScrollIntoView(SearchResultList.SelectedItem);
                }

                break;
            case nameof(DocumentViewModel.ActivePanel):
                Dispatcher.BeginInvoke(ScrollThumbnailIntoView, System.Windows.Threading.DispatcherPriority.Loaded);
                break;
            case nameof(DocumentViewModel.IsOrganizeMode) when _viewModel?.IsOrganizeMode == false:
                Dispatcher.BeginInvoke(() => Viewer.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                break;
        }
    }

    private void ScrollThumbnailIntoView()
    {
        if (_viewModel is not { IsThumbnailsPanelOpen: true } vm || vm.CurrentPageIndex < 0 || vm.CurrentPageIndex >= vm.Thumbnails.Count)
        {
            return;
        }

        ThumbnailList.ScrollIntoView(vm.Thumbnails[vm.CurrentPageIndex]);
    }

    // ---- Find bar ---------------------------------------------------------------------

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                _viewModel.FindPreviousCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                _viewModel.FindNextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                _viewModel.CloseFindBarCommand.Execute(null);
                Viewer.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnShowSearchResultsClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ActivePanel = SidePanel.SearchResults;
        if (_viewModel.SearchResults.Count == 0)
        {
            _viewModel.FindNextCommand.Execute(null);
        }
    }

    private void OnSearchResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && SearchResultList.SelectedIndex >= 0 && SearchResultList.SelectedIndex != _viewModel.CurrentSearchIndex)
        {
            _viewModel.CurrentSearchIndex = SearchResultList.SelectedIndex;
        }
    }

    // ---- Page box -----------------------------------------------------------------------

    private void OnPageBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (_viewModel?.GoToPage(PageBox.Text) != true)
            {
                ResetPageBox();
            }

            Viewer.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ResetPageBox();
            Viewer.Focus();
            e.Handled = true;
        }
    }

    private void OnPageBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e) => PageBox.SelectAll();

    private void OnPageBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ResetPageBox();

    private void ResetPageBox() => PageBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();

    // ---- Menus ---------------------------------------------------------------------------

    private void OnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = DataContext;
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToolCard tool } && Window.GetWindow(this)?.DataContext is MainViewModel main)
        {
            main.Home.RunToolCommand.Execute(tool);
        }
    }

    // ---- Thumbnails -----------------------------------------------------------------------

    private void OnThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ThumbnailItemViewModel item })
        {
            item.EnsureImage(VisualTreeHelper.GetDpi(this).DpiScaleX);
        }
    }

    /// <summary>Click navigates; Ctrl+click and Shift+click select several pages for the context menu.</summary>
    private void OnThumbnailMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ThumbnailItemViewModel item } || _viewModel is not { } vm)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Shift) != 0 && _thumbnailAnchor >= 0 && _thumbnailAnchor < vm.PageCount)
        {
            var from = Math.Min(_thumbnailAnchor, item.Index);
            vm.SelectedPages = Enumerable.Range(from, Math.Abs(item.Index - _thumbnailAnchor) + 1).ToList();
            vm.CurrentPageIndex = item.Index;
        }
        else if ((modifiers & ModifierKeys.Control) != 0)
        {
            var selection = vm.SelectedPages.Count > 0 ? vm.SelectedPages.ToHashSet() : [vm.CurrentPageIndex];
            if (!selection.Remove(item.Index))
            {
                selection.Add(item.Index);
            }

            vm.SelectedPages = selection.Order().ToList();
            if (selection.Count > 0)
            {
                // Show a selected page so that the selection stays the target of page commands.
                vm.CurrentPageIndex = selection.Contains(item.Index) ? item.Index : selection.Max();
            }

            _thumbnailAnchor = item.Index;
        }
        else
        {
            vm.SelectedPages = [item.Index];
            vm.CurrentPageIndex = item.Index;
            _thumbnailAnchor = item.Index;
            Viewer.Focus();
        }

        e.Handled = true;
    }

    /// <summary>Right-click targets the clicked page (or the selection it belongs to).</summary>
    private void OnThumbnailRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ThumbnailItemViewModel item } && _viewModel is { } vm)
        {
            if (!vm.SelectedPages.Contains(item.Index))
            {
                vm.SelectedPages = [item.Index];
                _thumbnailAnchor = item.Index;
            }

            vm.CurrentPageIndex = item.Index;
        }
    }

    private static List<string> DroppedInsertableFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => System.IO.File.Exists(f)
                               && (string.Equals(System.IO.Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase)
                                   || Core.Import.ImagePdfConverter.IsImage(f))).ToList()
            : [];

    private void OnThumbnailDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedInsertableFiles(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Files dropped on the thumbnails are inserted after the page they are dropped on.</summary>
    private async void OnThumbnailDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = DroppedInsertableFiles(e);
        if (files.Count == 0 || _viewModel is not { } vm)
        {
            return;
        }

        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not ListBoxItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        var at = element is ListBoxItem { DataContext: ThumbnailItemViewModel target } ? target.Index + 1 : vm.PageCount;
        await vm.InsertFilesAsync(at, files);
    }

    // ---- Bookmarks --------------------------------------------------------------------------

    private void OnBookmarkSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is BookmarkItemViewModel bookmark)
        {
            _viewModel?.NavigateToBookmark(bookmark);
        }
    }

    private void OnBookmarkClick(object sender, MouseButtonEventArgs e)
    {
        // Re-clicking the selected bookmark navigates again (SelectedItemChanged does not fire).
        if (sender is TreeViewItem { IsSelected: true, DataContext: BookmarkItemViewModel bookmark } item
            && ReferenceEquals(e.OriginalSource is DependencyObject d ? FindItem(d) : null, item))
        {
            _viewModel?.NavigateToBookmark(bookmark);
        }
    }

    private static TreeViewItem? FindItem(DependencyObject element)
    {
        while (element is not null and not TreeViewItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        return element as TreeViewItem;
    }
}
