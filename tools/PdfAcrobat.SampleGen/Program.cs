using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfAcrobat.Core.Fonts;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Pdf;

namespace PdfAcrobat.SampleGen;

/// <summary>
/// Generates sample PDFs used by tests and manual checks.
/// Usage: PdfAcrobat.SampleGen [outputDir] [--large]
/// </summary>
internal static class Program
{
    private const string JapaneseFont = "Yu Gothic";
    private const string Lorem =
        "PDF (Portable Document Format) は、アドビが開発した電子文書のためのフォーマットです。" +
        "文字・図形・画像を含む文書を、環境に依存せず同じ見た目で表示・印刷できます。" +
        "This paragraph mixes English text so that search, selection and copy can be tested " +
        "with both Japanese and Latin characters. The quick brown fox jumps over the lazy dog. ";

    [STAThread]
    private static int Main(string[] args)
    {
        WindowsFontResolver.Install();
        var outputDir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "samples";
        Directory.CreateDirectory(outputDir);

        WriteBasic(Path.Combine(outputDir, "basic.pdf"), password: null);
        WriteBasic(Path.Combine(outputDir, "encrypted.pdf"), password: "test");
        if (args.Contains("--large"))
        {
            WriteLarge(Path.Combine(outputDir, "large.pdf"), pageCount: 1200);
        }

        Console.WriteLine($"Samples written to {Path.GetFullPath(outputDir)}");
        return 0;
    }

    private static XFont Font(double size, XFontStyleEx style = XFontStyleEx.Regular) =>
        new(JapaneseFont, size, style, new XPdfFontOptions(PdfFontEncoding.Unicode));

    private static void WriteBasic(string path, string? password)
    {
        var doc = new PdfDocument();
        doc.Info.Title = "サンプル文書 / Sample Document";
        doc.Info.Author = "PDF Acrobat サンプル生成";
        doc.Info.Subject = "ビューアの動作確認用";
        doc.Info.Keywords = "sample, テスト, viewer";

        // 1: cover (A4 portrait) with links
        var cover = AddPage(doc, PageSize.A4, PageOrientation.Portrait);
        using (var gfx = XGraphics.FromPdfPage(cover))
        {
            Header(gfx, cover, "PDF Acrobat サンプル文書");
            gfx.DrawString("目次", Font(18, XFontStyleEx.Bold), XBrushes.Black, 60, 160);
            var entries = new[] { "第1章 はじめに", "第2章 横向きのページ", "第3章 回転したページ", "第4章 レターサイズ", "第5章 画像" };
            for (var i = 0; i < entries.Length; i++)
            {
                var y = 200 + i * 28;
                gfx.DrawString(entries[i], Font(13), XBrushes.DarkBlue, 80, y);
                gfx.DrawString($"{i + 2}", Font(13), XBrushes.DarkBlue, 480, y);
                // Link rectangles are in PDF space (origin bottom-left).
                var rect = new PdfRectangle(new XPoint(76, cover.Height.Point - y - 6), new XPoint(500, cover.Height.Point - y + 16));
                cover.AddDocumentLink(rect, i + 2);
            }

            gfx.DrawString("Web サイト: https://github.com/NISHIDA-Yutaka/pdf_acrobat", Font(11), XBrushes.Blue, 60, 380);
            cover.AddWebLink(
                new PdfRectangle(new XPoint(56, cover.Height.Point - 392), new XPoint(420, cover.Height.Point - 368)),
                "https://github.com/NISHIDA-Yutaka/pdf_acrobat");
            Paragraphs(gfx, new XRect(60, 420, cover.Width.Point - 120, 360), 3);
        }

        // 2: long text page (A4 portrait)
        var chapter1 = AddPage(doc, PageSize.A4, PageOrientation.Portrait);
        using (var gfx = XGraphics.FromPdfPage(chapter1))
        {
            Header(gfx, chapter1, "第1章 はじめに");
            Paragraphs(gfx, new XRect(60, 110, chapter1.Width.Point - 120, chapter1.Height.Point - 180), 8);
            Footer(gfx, chapter1, 2);
        }

        // 3: landscape
        var landscape = AddPage(doc, PageSize.A4, PageOrientation.Landscape);
        using (var gfx = XGraphics.FromPdfPage(landscape))
        {
            Header(gfx, landscape, "第2章 横向きのページ");
            DrawTable(gfx, new XRect(60, 120, landscape.Width.Point - 120, 260));
            Footer(gfx, landscape, 3);
        }

        // 4: page with /Rotate 90
        var rotated = AddPage(doc, PageSize.A4, PageOrientation.Portrait);
        using (var gfx = XGraphics.FromPdfPage(rotated))
        {
            Header(gfx, rotated, "第3章 回転したページ (/Rotate 90)");
            Paragraphs(gfx, new XRect(60, 110, rotated.Width.Point - 120, 300), 3);
            Footer(gfx, rotated, 4);
        }

        rotated.Rotate = 90;

        // 5: Letter size
        var letter = AddPage(doc, PageSize.Letter, PageOrientation.Portrait);
        using (var gfx = XGraphics.FromPdfPage(letter))
        {
            Header(gfx, letter, "第4章 レターサイズ");
            Paragraphs(gfx, new XRect(60, 110, letter.Width.Point - 120, 400), 4);
            gfx.DrawString("検索テスト: 特定のキーワード「ユニーク検索語」はこのページにだけあります。", Font(12), XBrushes.Black, 60, 560);
            Footer(gfx, letter, 5);
        }

        // 6: image page
        var imagePage = AddPage(doc, PageSize.A4, PageOrientation.Portrait);
        using (var gfx = XGraphics.FromPdfPage(imagePage))
        {
            Header(gfx, imagePage, "第5章 画像");
            using var image = XImage.FromStream(CreateGradientPng(800, 500));
            gfx.DrawImage(image, 60, 120, imagePage.Width.Point - 120, (imagePage.Width.Point - 120) * 500 / 800);
            gfx.DrawString("グラデーション画像 (800×500 PNG)", Font(11), XBrushes.Gray, 60, 520);
            Footer(gfx, imagePage, 6);
        }

        // Outline (bookmarks) with nesting
        var root = doc.Outlines.Add("表紙", cover, true);
        var ch1 = doc.Outlines.Add("第1章 はじめに", chapter1, true);
        ch1.Outlines.Add("1.1 背景", chapter1);
        ch1.Outlines.Add("1.2 目的", chapter1);
        doc.Outlines.Add("第2章 横向きのページ", landscape);
        doc.Outlines.Add("第3章 回転したページ", rotated);
        var ch4 = doc.Outlines.Add("第4章 レターサイズ", letter);
        ch4.Outlines.Add("4.1 検索テスト", letter);
        doc.Outlines.Add("第5章 画像", imagePage);
        _ = root;

        if (password is not null)
        {
            doc.SecuritySettings.UserPassword = password;
            doc.SecuritySettings.OwnerPassword = password + "-owner";
        }

        doc.Save(path);
    }

    private static void WriteLarge(string path, int pageCount)
    {
        var doc = new PdfDocument();
        doc.Info.Title = $"大きな文書 ({pageCount} ページ)";
        var font = Font(11);
        var title = Font(28, XFontStyleEx.Bold);
        for (var i = 1; i <= pageCount; i++)
        {
            var page = AddPage(doc, PageSize.A4, PageOrientation.Portrait);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"ページ {i}", title, XBrushes.Black, 60, 100);
            for (var line = 0; line < 30; line++)
            {
                gfx.DrawString($"{i}-{line + 1}: 大量ページの描画とスクロール性能を確認するための行です。Line {line + 1} of page {i}.", font, XBrushes.Black, 60, 150 + line * 20);
            }

            if (i % 100 == 1)
            {
                doc.Outlines.Add($"{i} ページ目から", page);
            }
        }

        doc.Save(path);
    }

    private static PdfPage AddPage(PdfDocument doc, PageSize size, PageOrientation orientation)
    {
        var page = doc.AddPage();
        page.Size = size;
        page.Orientation = orientation;
        return page;
    }

    private static void Header(XGraphics gfx, PdfPage page, string text)
    {
        gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 235, 16, 0)), 0, 0, page.Width.Point, 12);
        gfx.DrawString(text, Font(22, XFontStyleEx.Bold), XBrushes.Black, 60, 80);
        gfx.DrawLine(new XPen(XColors.LightGray, 1), 60, 92, page.Width.Point - 60, 92);
    }

    private static void Footer(XGraphics gfx, PdfPage page, int number)
    {
        gfx.DrawString($"- {number} -", Font(10), XBrushes.Gray, new XRect(0, page.Height.Point - 40, page.Width.Point, 20), XStringFormats.Center);
    }

    private static void Paragraphs(XGraphics gfx, XRect area, int count)
    {
        var formatter = new XTextFormatter(gfx);
        var text = string.Join("\n\n", Enumerable.Range(1, count).Select(i => $"{i}. {Lorem}"));
        formatter.DrawString(text, Font(11), XBrushes.Black, area, XStringFormats.TopLeft);
    }

    private static void DrawTable(XGraphics gfx, XRect area)
    {
        string[] headers = ["品目", "数量", "単価", "金額"];
        string[][] rows =
        [
            ["りんご", "3", "120", "360"],
            ["みかん", "10", "40", "400"],
            ["Banana", "6", "30", "180"],
            ["合計", "", "", "940"],
        ];
        var colWidth = area.Width / headers.Length;
        const double rowHeight = 32;
        var pen = new XPen(XColors.Gray, 0.8);
        for (var r = 0; r <= rows.Length; r++)
        {
            for (var c = 0; c < headers.Length; c++)
            {
                var cell = new XRect(area.X + c * colWidth, area.Y + r * rowHeight, colWidth, rowHeight);
                if (r == 0)
                {
                    gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 242, 242, 242)), cell);
                }

                gfx.DrawRectangle(pen, cell);
                var text = r == 0 ? headers[c] : rows[r - 1][c];
                gfx.DrawString(text, Font(12, r == 0 ? XFontStyleEx.Bold : XFontStyleEx.Regular), XBrushes.Black, cell, XStringFormats.Center);
            }
        }
    }

    private static MemoryStream CreateGradientPng(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(255 * x / width);          // B
                pixels[i + 1] = (byte)(255 * y / height);     // G
                pixels[i + 2] = (byte)(255 - 255 * x / width); // R
                pixels[i + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return stream;
    }
}
