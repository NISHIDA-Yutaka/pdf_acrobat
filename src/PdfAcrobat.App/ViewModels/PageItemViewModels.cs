using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfAcrobat.App.Services;
using PdfAcrobat.Core.Documents;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.ViewModels;

/// <summary>A page thumbnail in the side panel; the image is rendered lazily when the item scrolls into view.</summary>
public sealed partial class ThumbnailItemViewModel : ObservableObject
{
    public const double ThumbnailWidth = 118;

    private readonly DocumentSession _session;
    private CancellationTokenSource? _cts;

    public ThumbnailItemViewModel(DocumentSession session, int index, PageRef page, string? label)
    {
        _session = session;
        Index = index;
        Page = page;
        Label = label is null || label == (index + 1).ToString() ? (index + 1).ToString() : $"{label} ({index + 1})";
        var size = session.GetPageSize(page);
        ThumbnailHeight = Math.Round(ThumbnailWidth * size.Height / Math.Max(1, size.Width));
    }

    public int Index { get; }

    public PageRef Page { get; }

    public string Label { get; }

    public double ThumbnailHeight { get; }

    [ObservableProperty]
    public partial BitmapSource? Image { get; set; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>Part of the page selection (shared with the page grid).</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public void EnsureImage(double dpiScale)
    {
        if (Image is not null || _cts is not null || Page.IsBlank)
        {
            return;
        }

        var source = _session.GetSource(Page.SourceId!.Value);
        var cts = _cts = new CancellationTokenSource();
        var width = (int)Math.Round(ThumbnailWidth * dpiScale);
        var height = (int)Math.Round(ThumbnailHeight * dpiScale);
        var task = AppServices.Render.RenderAsync(source, Page.SourceIndex, Page.Rotation, width, height, RenderPriority.Thumbnail, cts.Token);
        task.ContinueWith(t =>
        {
            _cts = null;
            if (t.IsCompletedSuccessfully && t.Result is { } bitmap)
            {
                Image = bitmap;
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void CancelPending()
    {
        _cts?.Cancel();
        _cts = null;
    }
}

public sealed partial class BookmarkItemViewModel : ObservableObject
{
    public BookmarkItemViewModel(PdfBookmark bookmark)
    {
        Title = string.IsNullOrWhiteSpace(bookmark.Title) ? "(無題)" : bookmark.Title.Trim();
        Destination = bookmark.Destination;
        Uri = bookmark.Uri;
        IsExpanded = bookmark.IsOpen;
        Children = new ObservableCollection<BookmarkItemViewModel>(bookmark.Children.Select(c => new BookmarkItemViewModel(c)));
    }

    public string Title { get; }

    public PdfDestination? Destination { get; }

    public string? Uri { get; }

    public ObservableCollection<BookmarkItemViewModel> Children { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public string? PageText => Destination is { } d ? $"{d.PageIndex + 1}" : null;
}
