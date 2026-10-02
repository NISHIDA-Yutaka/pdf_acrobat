using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfAcrobat.App.Controls.Viewer;
using PdfAcrobat.App.Services;
using PdfAcrobat.Core.Documents;
using PdfAcrobat.Core.Text;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.ViewModels;

public enum SidePanel
{
    None,
    Thumbnails,
    Bookmarks,
    SearchResults,
}

/// <summary>State and commands of one open document tab.</summary>
public sealed partial class DocumentViewModel : TabViewModel, IDisposable
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _searchCts;
    private string? _lastSearchKey;
    private string[]? _pageLabels;

    public DocumentViewModel(DocumentSession session, MainViewModel main)
    {
        Session = session;
        _main = main;
        var settings = AppServices.Settings.Settings;
        IsToolsPaneOpen = settings.ToolsPaneOpen;
        ZoomMode = Enum.TryParse<ZoomMode>(settings.DefaultZoomMode, out var zoomMode) ? zoomMode : ZoomMode.FitWidth;
        LayoutMode = Enum.TryParse<PageLayoutMode>(settings.DefaultLayout, out var layout) ? layout : PageLayoutMode.Continuous;
        Session.Changed += OnSessionChanged;

        var bookmarks = session.PrimarySource.Document.GetBookmarks();
        Bookmarks = new ObservableCollection<BookmarkItemViewModel>(bookmarks.Select(b => new BookmarkItemViewModel(b)));
        LoadPageLabels();
        RebuildThumbnails();
        if (Bookmarks.Count > 0)
        {
            ActivePanel = SidePanel.Bookmarks;
        }
    }

    /// <summary>Raised to ask the view to navigate to a destination (with position inside the page).</summary>
    public event EventHandler<PdfDestination>? NavigateRequested;

    /// <summary>Raised to ask the view to copy the text selection.</summary>
    public event EventHandler? CopyRequested;

    public DocumentSession Session { get; }

    public override string Title => Session.DisplayName + (Session.IsModified ? " *" : string.Empty);

    public override string? ToolTip => Session.FilePath ?? Session.DisplayName;

    public override bool IsClosable => true;

    public string? FilePath => Session.FilePath;

    public PdfDocumentInfo Info => Session.PrimarySource.Info;

    public int PageCount => Session.Pages.Count;

    // ---- View state ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageNumber), nameof(CurrentPageLabel), nameof(CanGoBack), nameof(CanGoForward))]
    public partial int CurrentPageIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    public partial double Zoom { get; set; } = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFitWidth), nameof(IsFitPage))]
    public partial ZoomMode ZoomMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSinglePage), nameof(IsContinuous), nameof(IsTwoPage), nameof(IsTwoPageContinuous))]
    public partial PageLayoutMode LayoutMode { get; set; }

    [ObservableProperty]
    public partial bool CoverPage { get; set; } = true;

    [ObservableProperty]
    public partial int ViewRotation { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectTool), nameof(IsHandTool))]
    public partial ViewerTool Tool { get; set; }

    [ObservableProperty]
    public partial bool IsToolsPaneOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThumbnailsPanelOpen), nameof(IsBookmarksPanelOpen), nameof(IsSearchPanelOpen), nameof(IsSidePanelOpen))]
    public partial SidePanel ActivePanel { get; set; }

    public int CurrentPageNumber => CurrentPageIndex + 1;

    public string CurrentPageLabel => _pageLabels is { } labels && CurrentPageIndex < labels.Length && labels[CurrentPageIndex] != CurrentPageNumber.ToString(CultureInfo.InvariantCulture)
        ? labels[CurrentPageIndex]
        : string.Empty;

    public string ZoomText => $"{Math.Round(Zoom * 100)}%";

    public bool IsFitWidth => ZoomMode == ZoomMode.FitWidth;

    public bool IsFitPage => ZoomMode == ZoomMode.FitPage;

    public bool IsSinglePage => LayoutMode == PageLayoutMode.SinglePage;

    public bool IsContinuous => LayoutMode == PageLayoutMode.Continuous;

    public bool IsTwoPage => LayoutMode == PageLayoutMode.TwoPage;

    public bool IsTwoPageContinuous => LayoutMode == PageLayoutMode.TwoPageContinuous;

    public bool IsSelectTool
    {
        get => Tool == ViewerTool.Select;
        set
        {
            if (value)
            {
                Tool = ViewerTool.Select;
            }
        }
    }

    public bool IsHandTool
    {
        get => Tool == ViewerTool.Hand;
        set
        {
            if (value)
            {
                Tool = ViewerTool.Hand;
            }
        }
    }

    public bool IsThumbnailsPanelOpen
    {
        get => ActivePanel == SidePanel.Thumbnails;
        set => TogglePanel(SidePanel.Thumbnails, value);
    }

    public bool IsBookmarksPanelOpen
    {
        get => ActivePanel == SidePanel.Bookmarks;
        set => TogglePanel(SidePanel.Bookmarks, value);
    }

    public bool IsSearchPanelOpen
    {
        get => ActivePanel == SidePanel.SearchResults;
        set => TogglePanel(SidePanel.SearchResults, value);
    }

    public bool IsSidePanelOpen => ActivePanel != SidePanel.None;

    private void TogglePanel(SidePanel panel, bool open)
    {
        if (open)
        {
            ActivePanel = panel;
        }
        else if (ActivePanel == panel)
        {
            ActivePanel = SidePanel.None;
        }
    }

    public ObservableCollection<ThumbnailItemViewModel> Thumbnails { get; } = new();

    public ObservableCollection<BookmarkItemViewModel> Bookmarks { get; }

    public bool HasBookmarks => Bookmarks.Count > 0;

    public bool CanGoBack => CurrentPageIndex > 0;

    public bool CanGoForward => CurrentPageIndex < PageCount - 1;

    partial void OnCurrentPageIndexChanged(int oldValue, int newValue)
    {
        if (oldValue >= 0 && oldValue < Thumbnails.Count)
        {
            Thumbnails[oldValue].IsCurrent = false;
        }

        if (newValue >= 0 && newValue < Thumbnails.Count)
        {
            Thumbnails[newValue].IsCurrent = true;
        }

        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        FirstPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsToolsPaneOpenChanged(bool value)
    {
        AppServices.Settings.Settings.ToolsPaneOpen = value;
    }

    private void LoadPageLabels()
    {
        var doc = Session.PrimarySource.Document;
        string[]? labels = null;
        for (var i = 0; i < Session.PrimarySource.PageCount; i++)
        {
            var label = doc.GetPageLabel(i);
            if (label is not null)
            {
                labels ??= Enumerable.Range(1, Session.PrimarySource.PageCount).Select(n => n.ToString(CultureInfo.InvariantCulture)).ToArray();
                labels[i] = label;
            }

            if (i > 64 && labels is null)
            {
                break; // No labels in the first pages: assume the document has none.
            }
        }

        _pageLabels = labels;
    }

    private void RebuildThumbnails()
    {
        foreach (var t in Thumbnails)
        {
            t.CancelPending();
        }

        Thumbnails.Clear();
        var primary = Session.PrimarySource.Id;
        for (var i = 0; i < Session.Pages.Count; i++)
        {
            var page = Session.Pages[i];
            var label = page.SourceId == primary && _pageLabels is { } labels && page.SourceIndex < labels.Length ? labels[page.SourceIndex] : null;
            Thumbnails.Add(new ThumbnailItemViewModel(Session, i, page, label) { IsCurrent = i == CurrentPageIndex });
        }
    }

    private void OnSessionChanged(object? sender, DocumentChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PageCount));
        if (!ReferenceEquals(e.OldState.Pages, e.NewState.Pages))
        {
            RebuildThumbnails();
            ClearSearch();
        }

        _main.OnDocumentTitleChanged(this);
    }

    // ---- Navigation commands ------------------------------------------------------

    private IReadOnlyList<int[]> Rows => ViewerLayout.BuildRows(PageCount, LayoutMode, CoverPage);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void NextPage()
    {
        var rows = Rows;
        var row = ViewerLayout.RowOf(rows, CurrentPageIndex);
        if (row + 1 < rows.Count)
        {
            CurrentPageIndex = rows[row + 1][0];
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousPage()
    {
        var rows = Rows;
        var row = ViewerLayout.RowOf(rows, CurrentPageIndex);
        if (row > 0)
        {
            CurrentPageIndex = rows[row - 1][0];
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void FirstPage() => CurrentPageIndex = 0;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void LastPage() => CurrentPageIndex = PageCount - 1;

    /// <summary>Accepts a 1-based page number or a page label.</summary>
    public bool GoToPage(string text)
    {
        text = text.Trim();
        if (_pageLabels is { } labels)
        {
            var labelIndex = Array.FindIndex(labels, l => string.Equals(l, text, StringComparison.OrdinalIgnoreCase));
            if (labelIndex >= 0 && !int.TryParse(text, out _))
            {
                CurrentPageIndex = labelIndex;
                return true;
            }
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= PageCount)
        {
            CurrentPageIndex = number - 1;
            return true;
        }

        OnPropertyChanged(nameof(CurrentPageNumber));
        return false;
    }

    public void NavigateTo(PdfDestination destination) => NavigateRequested?.Invoke(this, destination);

    public void NavigateToBookmark(BookmarkItemViewModel bookmark)
    {
        if (bookmark.Destination is { } destination)
        {
            NavigateTo(destination);
        }
        else if (bookmark.Uri is { } uri)
        {
            OpenExternalLink(uri);
        }
    }

    public void OpenExternalLink(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "mailto"))
        {
            AppServices.Dialogs.ShowError($"このリンクは開けません。\n{uri}");
            return;
        }

        if (!AppServices.Dialogs.Confirm($"次の外部リンクを開こうとしています。\n\n{uri}\n\n既定のブラウザーで開きますか？", "外部リンク"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppServices.Dialogs.ShowError($"リンクを開けませんでした。\n{ex.Message}");
        }
    }

    // ---- Zoom & layout commands ----------------------------------------------------

    [RelayCommand]
    private void ZoomIn() => SetZoom(PdfViewer.NextZoomStep(Zoom, +1));

    [RelayCommand]
    private void ZoomOut() => SetZoom(PdfViewer.NextZoomStep(Zoom, -1));

    [RelayCommand]
    private void ActualSize() => SetZoom(1.0);

    [RelayCommand]
    private void FitWidth() => ZoomMode = ZoomMode.FitWidth;

    [RelayCommand]
    private void FitPage() => ZoomMode = ZoomMode.FitPage;

    [RelayCommand]
    private void SetZoomPercent(string? percent)
    {
        if (double.TryParse(percent?.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            SetZoom(value / 100.0);
        }
        else
        {
            OnPropertyChanged(nameof(ZoomText));
        }
    }

    public void SetZoom(double zoom)
    {
        ZoomMode = ZoomMode.Custom;
        Zoom = Math.Clamp(zoom, PdfViewer.MinZoom, PdfViewer.MaxZoom);
    }

    [RelayCommand]
    private void SetLayout(PageLayoutMode mode) => LayoutMode = mode;

    [RelayCommand]
    private void ToggleCoverPage() => CoverPage = !CoverPage;

    [RelayCommand]
    private void RotateViewClockwise() => ViewRotation = (ViewRotation + 1) & 3;

    [RelayCommand]
    private void RotateViewCounterClockwise() => ViewRotation = (ViewRotation + 3) & 3;

    [RelayCommand]
    private void SetTool(ViewerTool tool) => Tool = tool;

    [RelayCommand]
    private void ToggleToolsPane() => IsToolsPaneOpen = !IsToolsPaneOpen;

    [RelayCommand]
    private void ShowPanel(SidePanel panel) => ActivePanel = ActivePanel == panel ? SidePanel.None : panel;

    [RelayCommand]
    private void Copy() => CopyRequested?.Invoke(this, EventArgs.Empty);

    // ---- Search ------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsFindBarOpen { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial bool WholeWord { get; set; }

    [ObservableProperty]
    public partial bool MatchWidth { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchStatus))]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    public partial double SearchProgress { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<SearchHit>? SearchHits { get; set; }

    [ObservableProperty]
    public partial SearchHit? CurrentSearchHit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchStatus))]
    public partial int CurrentSearchIndex { get; set; } = -1;

    public ObservableCollection<SearchHit> SearchResults { get; } = new();

    public string SearchStatus
    {
        get
        {
            if (_lastSearchKey is null)
            {
                return string.Empty;
            }

            if (SearchResults.Count == 0)
            {
                return IsSearching ? "検索中..." : "見つかりません";
            }

            var position = CurrentSearchIndex >= 0 ? $"{CurrentSearchIndex + 1} / " : string.Empty;
            return $"{position}{SearchResults.Count} 件" + (IsSearching ? " (検索中)" : string.Empty);
        }
    }

    partial void OnCurrentSearchIndexChanged(int value)
    {
        CurrentSearchHit = value >= 0 && value < SearchResults.Count ? SearchResults[value] : null;
        if (CurrentSearchHit is { } hit)
        {
            CurrentPageIndex = hit.PageIndex;
        }
    }

    partial void OnMatchCaseChanged(bool value) => _lastSearchKey = null;

    partial void OnWholeWordChanged(bool value) => _lastSearchKey = null;

    partial void OnMatchWidthChanged(bool value) => _lastSearchKey = null;

    [RelayCommand]
    private void OpenFindBar()
    {
        IsFindBarOpen = true;
    }

    [RelayCommand]
    private void CloseFindBar()
    {
        IsFindBarOpen = false;
        ClearSearch();
    }

    private string SearchKey => $"{SearchText}\u0001{MatchCase}\u0001{WholeWord}\u0001{MatchWidth}";

    [RelayCommand]
    private async Task FindNext()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return;
        }

        if (_lastSearchKey != SearchKey)
        {
            await StartSearchAsync();
            return;
        }

        if (SearchResults.Count > 0)
        {
            CurrentSearchIndex = (CurrentSearchIndex + 1) % SearchResults.Count;
        }
    }

    [RelayCommand]
    private async Task FindPrevious()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return;
        }

        if (_lastSearchKey != SearchKey)
        {
            await StartSearchAsync();
            return;
        }

        if (SearchResults.Count > 0)
        {
            CurrentSearchIndex = (CurrentSearchIndex - 1 + SearchResults.Count) % SearchResults.Count;
        }
    }

    public async Task StartSearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        ClearSearch(keepKey: true);
        _lastSearchKey = SearchKey;
        IsSearching = true;
        SearchProgress = 0;
        OnPropertyChanged(nameof(SearchStatus));

        var startPage = CurrentPageIndex;
        var collected = new List<SearchHit>();
        var dispatcher = Application.Current.Dispatcher;
        try
        {
            await TextSearch.SearchDocumentAsync(
                Session,
                AppServices.Text,
                SearchText,
                new SearchOptions(MatchCase, WholeWord, MatchWidth),
                pageHits => dispatcher.Invoke(() =>
                {
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    foreach (var hit in pageHits)
                    {
                        SearchResults.Add(hit);
                    }

                    collected.AddRange(pageHits);
                    SearchHits = collected.ToList();

                    // Jump to the first hit at or after the page the user was looking at.
                    if (CurrentSearchIndex < 0 && pageHits[0].PageIndex >= startPage)
                    {
                        CurrentSearchIndex = SearchResults.IndexOf(pageHits[0]);
                    }

                    OnPropertyChanged(nameof(SearchStatus));
                }),
                new Progress<double>(p => SearchProgress = p),
                cts.Token);

            if (!cts.IsCancellationRequested && CurrentSearchIndex < 0 && SearchResults.Count > 0)
            {
                CurrentSearchIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearching = false;
                OnPropertyChanged(nameof(SearchStatus));
            }
        }
    }

    private void ClearSearch(bool keepKey = false)
    {
        if (!keepKey)
        {
            _searchCts?.Cancel();
            _lastSearchKey = null;
            IsSearching = false;
        }

        SearchResults.Clear();
        SearchHits = null;
        CurrentSearchIndex = -1;
        CurrentSearchHit = null;
        OnPropertyChanged(nameof(SearchStatus));
    }

    // ---- File commands -----------------------------------------------------------------

    [RelayCommand]
    private void SaveAs()
    {
        var suggested = Path.GetFileName(Session.FilePath ?? Session.DisplayName);
        var path = AppServices.Dialogs.PickSavePath(suggested);
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(path, Session.PrimarySource.Bytes);
            Session.MarkSaved(path);
            AppServices.Settings.AddRecentFile(path, PageCount, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppServices.Dialogs.ShowError($"保存できませんでした。\n{ex.Message}");
        }
    }

    [RelayCommand]
    private void ShowProperties() => _main.ShowDocumentProperties(this);

    [RelayCommand]
    private void Print() => _main.PrintDocument(this);

    [RelayCommand]
    private void FullScreen() => _main.ShowFullScreen(this);

    [RelayCommand]
    private void Close() => _main.CloseTab(this);

    public void Dispose()
    {
        _searchCts?.Cancel();
        Session.Changed -= OnSessionChanged;
        foreach (var t in Thumbnails)
        {
            t.CancelPending();
        }

        foreach (var source in Session.Sources.Values)
        {
            AppServices.Render.ClearSource(source.Id);
        }

        Session.Dispose();
    }
}
