using System.Globalization;
using System.Windows;
using PdfAcrobat.App.Services;
using PdfAcrobat.App.ViewModels;

namespace PdfAcrobat.App.Views.Dialogs;

/// <summary>"PDF を作成": blank pages, images, the clipboard or several files.</summary>
public partial class CreatePdfWindow
{
    private readonly MainViewModel _main;
    private readonly bool _ready;

    public CreatePdfWindow(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        _ready = true;
        OnKindChanged(this, new RoutedEventArgs());
    }

    private void OnKindChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        BlankPanel.Visibility = Visibility.Collapsed;
        ImagesPanel.Visibility = Visibility.Collapsed;
        ClipboardPanel.Visibility = Visibility.Collapsed;
        CombinePanel.Visibility = Visibility.Collapsed;
        CreateButton.IsEnabled = true;
        CreateButton.Content = "作成";

        if (BlankKind.IsChecked == true)
        {
            KindTitle.Text = "空白ページ";
            KindDescription.Text = "白紙のページだけの新しい PDF を作成します。";
            BlankPanel.Visibility = Visibility.Visible;
        }
        else if (ImagesKind.IsChecked == true)
        {
            KindTitle.Text = "画像から PDF を作成";
            KindDescription.Text = "写真やスキャン画像などを 1 つの PDF にまとめます。";
            ImagesPanel.Visibility = Visibility.Visible;
            CreateButton.Content = "画像を選択...";
        }
        else if (ClipboardKind.IsChecked == true)
        {
            KindTitle.Text = "クリップボードから PDF を作成";
            KindDescription.Text = "コピーした画像やテキストから PDF を作成します。";
            ClipboardPanel.Visibility = Visibility.Visible;
            ShowClipboardContent();
        }
        else
        {
            KindTitle.Text = "複数のファイルから PDF を作成";
            KindDescription.Text = "PDF や画像を 1 つの PDF に結合します。";
            CombinePanel.Visibility = Visibility.Visible;
            CreateButton.Content = "ファイルを結合...";
        }
    }

    private void ShowClipboardContent()
    {
        ClipboardPreviewFrame.Visibility = Visibility.Collapsed;
        ClipboardText.Visibility = Visibility.Collapsed;
        switch (SourceLoader.PeekClipboard())
        {
            case { Image: { } image }:
                ClipboardStatus.Text = $"画像 ({image.PixelWidth} × {image.PixelHeight} ピクセル) を PDF にします。";
                ClipboardPreview.Source = image;
                ClipboardPreviewFrame.Visibility = Visibility.Visible;
                break;
            case { Text: { } text }:
                ClipboardStatus.Text = $"テキスト ({text.Length:N0} 文字) を A4 の PDF にします。";
                ClipboardText.Text = text.Length > 4000 ? text[..4000] + "…" : text;
                ClipboardText.Visibility = Visibility.Visible;
                break;
            default:
                ClipboardStatus.Text = "クリップボードに画像やテキストがありません。画像やテキストをコピーしてから、もう一度選択してください。";
                CreateButton.IsEnabled = false;
                break;
        }
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (BlankKind.IsChecked == true)
        {
            if (!int.TryParse(PageCountBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 1000)
            {
                PageCountBox.Focus();
                PageCountBox.SelectAll();
                return;
            }

            var paper = PaperBox.SelectedItem as PaperSize ?? PaperSize.A4;
            _main.CreateBlankDocument(paper.ToPoints(landscape: PortraitRadio.IsChecked != true), count);
            DialogResult = true;
        }
        else if (ImagesKind.IsChecked == true)
        {
            if (await _main.CreateFromImagesAsync() is not null)
            {
                DialogResult = true;
            }
        }
        else if (ClipboardKind.IsChecked == true)
        {
            if (await _main.CreateFromClipboardAsync() is not null)
            {
                DialogResult = true;
            }
        }
        else
        {
            DialogResult = true;
            _ = Dispatcher.BeginInvoke(() => _main.ShowCombineWindow([]));
        }
    }
}
