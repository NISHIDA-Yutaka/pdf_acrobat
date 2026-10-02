using System.Windows;
using System.Windows.Media;
using PdfAcrobat.App.ViewModels;
using PdfAcrobat.Core.Documents;

namespace PdfAcrobat.App.Views.Dialogs;

public partial class ExtractPagesDialog
{
    private readonly int _pageCount;

    public ExtractPagesDialog(DocumentViewModel document)
    {
        InitializeComponent();
        _pageCount = document.PageCount;
        var initial = document.SelectedPages.Count > 0 ? document.SelectedPages : [document.CurrentPageIndex];
        RangeBox.Text = PageRangeParser.Format(initial);
        Loaded += (_, _) =>
        {
            RangeBox.Focus();
            RangeBox.SelectAll();
        };
    }

    public IReadOnlyList<int> Pages { get; private set; } = [];

    public bool DeleteAfterExtract => DeleteBox.IsChecked == true;

    public bool SeparateFiles => SeparateBox.IsChecked == true;

    private void OnRangeChanged(object sender, RoutedEventArgs e)
    {
        var ok = PageRangeParser.TryParse(RangeBox.Text, _pageCount, out var pages);
        Pages = ok ? pages.Distinct().ToList() : [];
        RangeHint.Text = ok ? $"{Pages.Count} ページ（全 {_pageCount} ページ中）" : $"1〜{_pageCount} の範囲で指定してください（例: 1-3, 5）";
        RangeHint.Foreground = (Brush)FindResource(ok ? "MutedForegroundBrush" : "SystemFillColorCriticalBrush");
        OkButton.IsEnabled = ok;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = Pages.Count > 0;
}
