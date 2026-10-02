namespace PdfAcrobat.App.Controls.Viewer;

public enum ZoomMode
{
    Custom,
    FitWidth,
    FitPage,
}

public enum PageLayoutMode
{
    /// <summary>One page at a time.</summary>
    SinglePage,

    /// <summary>All pages in one scrolling column (default).</summary>
    Continuous,

    /// <summary>Two-page spread, one spread at a time.</summary>
    TwoPage,

    /// <summary>Two-page spreads in a scrolling column.</summary>
    TwoPageContinuous,
}

public enum ViewerTool
{
    Select,
    Hand,
}
