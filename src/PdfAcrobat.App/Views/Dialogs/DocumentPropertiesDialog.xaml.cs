using System.IO;
using PdfAcrobat.App.ViewModels;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.Views.Dialogs;

public partial class DocumentPropertiesDialog
{
    public DocumentPropertiesDialog(DocumentViewModel document)
    {
        InitializeComponent();
        var info = document.Info;
        var session = document.Session;
        var currentSize = session.GetPageSize(session.Pages[Math.Clamp(document.CurrentPageIndex, 0, session.Pages.Count - 1)]);

        DocumentRows.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("タイトル", Fallback(info.Title)),
            new("作成者", Fallback(info.Author)),
            new("サブタイトル", Fallback(info.Subject)),
            new("キーワード", Fallback(info.Keywords)),
            new("作成日", FormatDate(info.CreationDate)),
            new("更新日", FormatDate(info.ModificationDate)),
            new("アプリケーション", Fallback(info.Creator)),
            new("PDF 変換", Fallback(info.Producer)),
            new("PDF のバージョン", info.VersionText),
            new("ページ数", $"{session.Pages.Count}"),
            new("ページサイズ", FormatPageSize(currentSize.Width, currentSize.Height)),
            new("タグ付き PDF", info.IsTagged ? "はい" : "いいえ"),
            new("言語", Fallback(info.Language)),
        };

        var path = session.FilePath;
        var fileInfo = path is not null ? new FileInfo(path) : null;
        FileRows.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("ファイル名", session.DisplayName),
            new("場所", fileInfo?.DirectoryName ?? "(未保存)"),
            new("ファイルサイズ", fileInfo is { Exists: true } ? $"{RecentFileItem.FormatSize(fileInfo.Length)} ({fileInfo.Length:N0} バイト)" : "-"),
            new("更新日時", fileInfo is { Exists: true } ? fileInfo.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss") : "-"),
        };

        var permissions = info.Permissions;
        string Allowed(PdfPermissions flag) => !info.IsEncrypted || permissions.HasFlag(flag) ? "許可" : "許可しない";
        SecurityRows.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("セキュリティ方式", info.IsEncrypted ? $"パスワードによるセキュリティ (リビジョン {info.SecurityHandlerRevision})" : "セキュリティなし"),
            new("印刷", Allowed(PdfPermissions.Print)),
            new("高品質の印刷", Allowed(PdfPermissions.PrintHighQuality)),
            new("文書の変更", Allowed(PdfPermissions.Modify)),
            new("文書アセンブリ", Allowed(PdfPermissions.Assemble)),
            new("内容のコピー", Allowed(PdfPermissions.CopyContent)),
            new("アクセシビリティのための内容の抽出", Allowed(PdfPermissions.ExtractForAccessibility)),
            new("注釈", Allowed(PdfPermissions.Annotate)),
            new("フォームフィールドの入力", Allowed(PdfPermissions.FillForms)),
        };
    }

    private static string Fallback(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string FormatDate(DateTimeOffset? date) => date is { } d ? d.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") : "-";

    private static string FormatPageSize(double widthPt, double heightPt)
    {
        var widthMm = widthPt / 72 * 25.4;
        var heightMm = heightPt / 72 * 25.4;
        var name = PaperName(widthMm, heightMm);
        return $"{widthMm:0.0} × {heightMm:0.0} mm" + (name is null ? string.Empty : $" ({name})");
    }

    private static string? PaperName(double w, double h)
    {
        var (shortSide, longSide) = w < h ? (w, h) : (h, w);
        (string Name, double Short, double Long)[] papers =
        [
            ("A3", 297, 420), ("A4", 210, 297), ("A5", 148, 210), ("B4", 257, 364), ("B5", 182, 257),
            ("レター", 215.9, 279.4), ("リーガル", 215.9, 355.6), ("はがき", 100, 148),
        ];
        foreach (var paper in papers)
        {
            if (Math.Abs(paper.Short - shortSide) < 2 && Math.Abs(paper.Long - longSide) < 2)
            {
                return paper.Name + (w > h ? " 横" : string.Empty);
            }
        }

        return null;
    }
}
