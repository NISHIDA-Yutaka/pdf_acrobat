using System.Windows;

namespace PdfAcrobat.App.Controls.Viewer;

/// <summary>Placement of pages on the viewer canvas, in device-independent pixels.</summary>
public sealed class ViewerLayout
{
    public const double Margin = 16;
    public const double RowGap = 12;
    public const double SpreadGap = 4;
    public const double PointsToDip = 96.0 / 72.0;

    private ViewerLayout(Rect[] pageRects, IReadOnlyList<int[]> rows, Size extent, double scale)
    {
        PageRects = pageRects;
        Rows = rows;
        Extent = extent;
        Scale = scale;
    }

    /// <summary>Rectangle of each page on the canvas; <see cref="Rect.Empty"/> when the page is not shown.</summary>
    public Rect[] PageRects { get; }

    /// <summary>Page indices per row (one or two pages).</summary>
    public IReadOnlyList<int[]> Rows { get; }

    public Size Extent { get; }

    /// <summary>DIPs per PDF point.</summary>
    public double Scale { get; }

    public static ViewerLayout Empty { get; } = new([], [], new Size(0, 0), 1);

    public static IReadOnlyList<int[]> BuildRows(int pageCount, PageLayoutMode mode, bool coverPage)
    {
        var rows = new List<int[]>();
        if (mode is PageLayoutMode.SinglePage or PageLayoutMode.Continuous)
        {
            for (var i = 0; i < pageCount; i++)
            {
                rows.Add([i]);
            }

            return rows;
        }

        var start = 0;
        if (coverPage && pageCount > 0)
        {
            rows.Add([0]);
            start = 1;
        }

        for (var i = start; i < pageCount; i += 2)
        {
            rows.Add(i + 1 < pageCount ? [i, i + 1] : [i]);
        }

        return rows;
    }

    public static int RowOf(IReadOnlyList<int[]> rows, int pageIndex)
    {
        for (var r = 0; r < rows.Count; r++)
        {
            if (Array.IndexOf(rows[r], pageIndex) >= 0)
            {
                return r;
            }
        }

        return 0;
    }

    /// <param name="pageSizes">Displayed page sizes in points (including page and view rotation).</param>
    /// <param name="zoom">1.0 = 100 %.</param>
    /// <param name="currentPage">Page whose row is shown in non-continuous modes.</param>
    public static ViewerLayout Compute(
        IReadOnlyList<Size> pageSizes,
        double zoom,
        PageLayoutMode mode,
        bool coverPage,
        double viewportWidth,
        int currentPage)
    {
        if (pageSizes.Count == 0)
        {
            return Empty;
        }

        var scale = zoom * PointsToDip;
        var rows = BuildRows(pageSizes.Count, mode, coverPage);
        var spread = mode is PageLayoutMode.TwoPage or PageLayoutMode.TwoPageContinuous;
        var continuous = mode is PageLayoutMode.Continuous or PageLayoutMode.TwoPageContinuous;
        var visibleRows = continuous
            ? Enumerable.Range(0, rows.Count).ToList()
            : [RowOf(rows, Math.Clamp(currentPage, 0, pageSizes.Count - 1))];

        // Width needed by the widest row.
        double contentWidth = 0;
        foreach (var r in visibleRows)
        {
            var row = rows[r];
            double width;
            if (spread)
            {
                // Spreads are centred on the spine, so each half needs the wider page's width.
                var half = row.Max(i => pageSizes[i].Width * scale);
                width = 2 * half + SpreadGap;
            }
            else
            {
                width = pageSizes[row[0]].Width * scale;
            }

            contentWidth = Math.Max(contentWidth, width);
        }

        var extentWidth = Math.Max(viewportWidth, contentWidth + 2 * Margin);
        var center = extentWidth / 2;
        var rects = Enumerable.Repeat(Rect.Empty, pageSizes.Count).ToArray();
        var y = Margin;
        foreach (var r in visibleRows)
        {
            var row = rows[r];
            var rowHeight = row.Max(i => pageSizes[i].Height * scale);
            if (!spread)
            {
                var size = pageSizes[row[0]];
                var w = size.Width * scale;
                var h = size.Height * scale;
                rects[row[0]] = new Rect(center - w / 2, y + (rowHeight - h) / 2, w, h);
            }
            else
            {
                for (var k = 0; k < row.Length; k++)
                {
                    var index = row[k];
                    var w = pageSizes[index].Width * scale;
                    var h = pageSizes[index].Height * scale;
                    // A lone page is the cover (right side) or the trailing page (left side).
                    var onRight = row.Length == 2 ? k == 1 : coverPage && index == 0;
                    var x = onRight ? center + SpreadGap / 2 : center - SpreadGap / 2 - w;
                    rects[index] = new Rect(x, y + (rowHeight - h) / 2, w, h);
                }
            }

            y += rowHeight + RowGap;
        }

        var extentHeight = y - RowGap + Margin;
        return new ViewerLayout(rects, rows, new Size(extentWidth, extentHeight), scale);
    }
}
