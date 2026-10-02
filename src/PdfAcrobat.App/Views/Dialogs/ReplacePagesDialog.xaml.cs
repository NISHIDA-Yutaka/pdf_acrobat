using System.Windows;
using System.Windows.Media;
using PdfAcrobat.App.ViewModels;
using PdfAcrobat.Core.Documents;

namespace PdfAcrobat.App.Views.Dialogs;

public partial class ReplacePagesDialog
{
    private readonly int _targetCount;
    private readonly int _sourceCount;
    private bool _ready;

    public ReplacePagesDialog(DocumentViewModel document, PdfSource source)
    {
        InitializeComponent();
        _targetCount = document.PageCount;
        _sourceCount = source.PageCount;
        TargetHeader.Text = $"置換するページ（この文書: 全 {_targetCount} ページ）";
        SourceHeader.Text = $"置換に使うページ（{source.Name}: 全 {_sourceCount} ページ）";
        var selected = document.SelectedPages.Count > 0 ? document.SelectedPages : [document.CurrentPageIndex];
        TargetBox.Text = PageRangeParser.Format(selected);
        SourceBox.Text = $"1-{Math.Min(selected.Count, _sourceCount)}";
        _ready = true;
        OnChanged(this, new RoutedEventArgs());
    }

    public IReadOnlyList<int> TargetPages { get; private set; } = [];

    public IReadOnlyList<int> SourcePages { get; private set; } = [];

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var targetOk = PageRangeParser.TryParse(TargetBox.Text, _targetCount, out var targets);
        var sourceOk = PageRangeParser.TryParse(SourceBox.Text, _sourceCount, out var sources);
        TargetPages = targetOk ? targets.Distinct().Order().ToList() : [];
        SourcePages = sourceOk ? sources : [];
        var ok = targetOk && sourceOk && TargetPages.Count == SourcePages.Count;
        Hint.Text = !targetOk || !sourceOk
            ? "ページ範囲の指定が正しくありません（例: 1-3, 5）"
            : TargetPages.Count != SourcePages.Count
                ? $"置換するページ数（{TargetPages.Count}）と置換に使うページ数（{SourcePages.Count}）をそろえてください。"
                : $"{TargetPages.Count} ページを置換します。";
        Hint.Foreground = (Brush)FindResource(ok ? "MutedForegroundBrush" : "SystemFillColorCriticalBrush");
        OkButton.IsEnabled = ok;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
