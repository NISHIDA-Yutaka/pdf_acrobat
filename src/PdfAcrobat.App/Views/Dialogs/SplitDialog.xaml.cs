using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfAcrobat.App.Services;
using PdfAcrobat.App.ViewModels;
using PdfAcrobat.Core.Documents;

namespace PdfAcrobat.App.Views.Dialogs;

public partial class SplitDialog
{
    private readonly DocumentViewModel _document;
    private readonly bool _ready;

    public SplitDialog(DocumentViewModel document)
    {
        InitializeComponent();
        _document = document;
        ByBookmarksRadio.IsEnabled = document.HasBookmarks;
        RangesBox.Text = document.PageCount > 1 ? $"1-{(document.PageCount + 1) / 2}, {(document.PageCount + 1) / 2 + 1}-" : "1";
        NameBox.Text = Path.GetFileNameWithoutExtension(document.Session.DisplayName);
        FolderBox.Text = document.FilePath is { } path
            ? Path.GetDirectoryName(path)!
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _ready = true;
        OnChanged(this, new RoutedEventArgs());
    }

    public SplitOptions? Options { get; private set; }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (AppServices.Dialogs.PickFolder("分割したファイルの保存先を選択") is { } folder)
        {
            FolderBox.Text = folder;
        }
    }

    /// <summary>Typing into a mode's box selects that mode.</summary>
    private void OnBoxFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { Tag: RadioButton radio })
        {
            radio.IsChecked = true;
        }
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var pageCount = _document.PageCount;
        var folder = FolderBox.Text.Trim();
        var name = NameBox.Text.Trim();
        Options = null;
        string message;
        if (ByPagesRadio.IsChecked == true)
        {
            if (int.TryParse(PagesBox.Text, out var perFile) && perFile >= 1 && perFile <= pageCount)
            {
                Options = new SplitOptions(SplitMode.PageCount, perFile, folder, name);
                message = $"{(pageCount + perFile - 1) / perFile} 個のファイルを作成します。";
            }
            else
            {
                message = $"1〜{pageCount} の数値を入力してください。";
            }
        }
        else if (ByFilesRadio.IsChecked == true)
        {
            if (int.TryParse(FilesBox.Text, out var files) && files >= 1 && files <= pageCount)
            {
                Options = new SplitOptions(SplitMode.FileCount, files, folder, name);
                var perFile = (pageCount + files - 1) / files;
                message = $"{perFile} ページずつ、{(pageCount + perFile - 1) / perFile} 個のファイルを作成します。";
            }
            else
            {
                message = $"1〜{pageCount} の数値を入力してください。";
            }
        }
        else if (ByRangesRadio.IsChecked == true)
        {
            if (PageRangeParser.TryParseGroups(RangesBox.Text, pageCount, out var groups))
            {
                Options = new SplitOptions(SplitMode.Ranges, groups.Count, folder, name) { Ranges = groups };
                message = $"{groups.Count} 個のファイルを作成します（{string.Join("、", groups.Select(g => $"{g.Count} ページ"))}）。";
            }
            else
            {
                message = $"1〜{pageCount} の範囲をカンマ区切りで入力してください（例: 1-3, 4-10）。";
            }
        }
        else
        {
            Options = new SplitOptions(SplitMode.TopLevelBookmarks, 0, folder, name);
            message = $"最上位のしおり（{_document.Bookmarks.Count} 個）の位置で分割します。";
        }

        if (Options is not null && (name.Length == 0 || folder.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            Options = null;
            message = "有効なファイル名と保存先フォルダーを指定してください。";
        }
        else if (Options is not null)
        {
            message += $"\n{name}_01.pdf, {name}_02.pdf ... として保存します。";
        }

        Hint.Text = message;
        Hint.Foreground = (Brush)FindResource(Options is null ? "SystemFillColorCriticalBrush" : "MutedForegroundBrush");
        OkButton.IsEnabled = Options is not null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = Options is not null;
}
