using System.IO;
using System.Collections.Immutable;
using PdfAcrobat.Core.Documents;
using PdfAcrobat.Core.Export;
using PdfAcrobat.Core.Import;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.Tests.Export;

public class DocumentExporterTests
{
    private static DocumentSession OpenBasic() => new(PdfSource.Load(TestFiles.ReadSample("basic.pdf"), "basic.pdf"));

    private static string PageText(PdfDocument document, int index)
    {
        using var page = document.OpenPage(index);
        return page.GetTextPage().CreateSnapshot().Text;
    }

    private static IEnumerable<string> FlattenTitles(IEnumerable<PdfBookmark> bookmarks) =>
        bookmarks.SelectMany(b => new[] { b.Title }.Concat(FlattenTitles(b.Children)));

    [Fact]
    public void UnchangedDocument_ReturnsOriginalBytes()
    {
        using var session = OpenBasic();

        var bytes = DocumentExporter.Export(session);

        Assert.Same(session.PrimarySource.Bytes, bytes);
    }

    [Fact]
    public void DeletingAPage_RemovesItsContentAndBookmarks()
    {
        using var session = OpenBasic();
        var originalSize = session.PrimarySource.Bytes.Length;

        // Page 6 holds the large image; page 2 is chapter 1 (two nested bookmarks point at it).
        session.Apply("削除", s => s with { Pages = s.Pages.RemoveAt(5).RemoveAt(1) });
        var bytes = DocumentExporter.Export(session);

        using var output = PdfDocument.Load(bytes);
        Assert.Equal(4, output.PageCount);
        // Footer "- 2 -" only exists on the deleted chapter page (the cover TOC also lists chapter titles).
        Assert.DoesNotContain(Enumerable.Range(0, 4), i => PageText(output, i).Contains("- 2 -"));

        var titles = FlattenTitles(output.GetBookmarks()).ToList();
        Assert.DoesNotContain("第1章 はじめに", titles);
        Assert.DoesNotContain("1.1 背景", titles);
        Assert.DoesNotContain("第5章 画像", titles);
        Assert.Contains("第4章 レターサイズ", titles);
        Assert.All(output.GetBookmarks(), b => Assert.True(b.Destination is null || b.Destination.PageIndex < 4));

        // The image page is gone from the file, not just hidden.
        Assert.True(bytes.Length < originalSize / 2, $"{bytes.Length} should be much smaller than {originalSize}");
    }

    [Fact]
    public void ReorderRotateDuplicateAndBlank_AreWritten()
    {
        using var session = OpenBasic();
        session.Apply("操作", s =>
        {
            var pages = s.Pages;
            var last = pages[5];
            pages = pages.RemoveAt(5).Insert(0, last);           // image page first
            pages = pages.SetItem(1, pages[1].Rotate(1));         // cover rotated 90°
            pages = pages.Insert(2, pages[1].Duplicate().Rotate(-1)); // duplicate of the cover, unrotated
            pages = pages.Add(PageRef.Blank(new PdfSize(300, 400)));
            return s with { Pages = pages };
        });

        using var output = PdfDocument.Load(DocumentExporter.Export(session));

        Assert.Equal(8, output.PageCount);
        Assert.Contains("第5章", PageText(output, 0));
        Assert.Contains("サンプル文書", PageText(output, 1));
        Assert.Contains("サンプル文書", PageText(output, 2));
        Assert.Equal(1, output.GetPageRotation(1));
        Assert.Equal(0, output.GetPageRotation(2));
        Assert.Equal(300, output.GetPageSize(7).Width, 0);

        // Bookmarks still point at the moved pages.
        var bookmarks = output.GetBookmarks();
        Assert.Equal(0, bookmarks.Single(b => b.Title == "第5章 画像").Destination!.PageIndex);
    }

    [Fact]
    public void RotatingAPageWithExistingRotation_AddsUp()
    {
        using var session = OpenBasic();
        session.Apply("回転", s => s with { Pages = s.Pages.SetItem(3, s.Pages[3].Rotate(1)) });

        using var output = PdfDocument.Load(DocumentExporter.Export(session));

        // Page 4 already has /Rotate 90.
        Assert.Equal(2, output.GetPageRotation(3));
    }

    [Fact]
    public void CombiningSources_AddsPagesAndBookmarks()
    {
        var first = PdfSource.Load(TestFiles.ReadSample("basic.pdf"), "a.pdf");
        var second = PdfSource.Load(TestFiles.ReadSample("encrypted.pdf"), "b.pdf", password: "test");
        using var session = DocumentSession.CreateUnsaved("結合.pdf", [first, second]);

        Assert.True(session.IsModified);
        var bytes = DocumentExporter.Export(session, new ExportOptions
        {
            Bookmarks = [new ExportBookmark("a.pdf", 0), new ExportBookmark("b.pdf", 6)],
        });

        using var output = PdfDocument.Load(bytes);
        Assert.Equal(12, output.PageCount);
        Assert.False(output.IsEncrypted);
        Assert.Contains("サンプル文書", PageText(output, 6));
        var titles = output.GetBookmarks().Select(b => b.Title).ToList();
        Assert.Contains("b.pdf", titles);
        Assert.Equal(6, output.GetBookmarks().Single(b => b.Title == "b.pdf").Destination!.PageIndex);
    }

    [Fact]
    public void EncryptedPrimary_IsSavedWithoutSecurity()
    {
        using var session = new DocumentSession(PdfSource.Load(TestFiles.ReadSample("encrypted.pdf"), "e.pdf", password: "test"));
        session.Apply("削除", s => s with { Pages = s.Pages.RemoveAt(0) });

        using var output = PdfDocument.Load(DocumentExporter.Export(session));

        Assert.False(output.IsEncrypted);
        Assert.Equal(5, output.PageCount);
    }

    [Fact]
    public void ExtractingPages_CreatesAFreshDocument()
    {
        using var session = OpenBasic();

        var bytes = DocumentExporter.Export(session, new ExportOptions { PageIndices = [4, 1], PreserveDocumentStructure = false });

        using var output = PdfDocument.Load(bytes);
        Assert.Equal(2, output.PageCount);
        Assert.Contains("ユニーク検索語", PageText(output, 0));
        Assert.Contains("第1章", PageText(output, 1));
        Assert.Empty(output.GetBookmarks());
    }

    [Fact]
    public void Images_AreConvertedToPages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfacrobat-test-{Guid.NewGuid():N}.png");
        try
        {
            var pixels = new byte[200 * 100 * 4];
            Array.Fill<byte>(pixels, 0x80);
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(200, 100, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 800);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = File.Create(path))
            {
                encoder.Save(file);
            }

            using var output = PdfDocument.Load(ImagePdfConverter.ConvertFiles([path, path]));

            Assert.Equal(2, output.PageCount);
            Assert.Equal(150, output.GetPageSize(0).Width, 0); // 200 px at 96 dpi = 150 pt
            Assert.Equal(75, output.GetPageSize(0).Height, 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BlankSource_HasRequestedSize()
    {
        using var source = DocumentSession.CreateBlankSource("無題.pdf", new PdfSize(595, 842), 2);

        Assert.Equal(2, source.PageCount);
        Assert.Equal(842, source.PageSizes[1].Height, 0);
    }

    [Fact]
    public void PageRangeParser_ParsesCommonForms()
    {
        Assert.True(PageRangeParser.TryParse("1-3, 5, 8-", 10, out var pages));
        Assert.Equal([0, 1, 2, 4, 7, 8, 9], pages);
        Assert.True(PageRangeParser.TryParse("３～１", 5, out var reversed));
        Assert.Equal([2, 1, 0], reversed);
        Assert.False(PageRangeParser.TryParse("0", 5, out _));
        Assert.False(PageRangeParser.TryParse("2-x", 5, out _));
        Assert.Equal("1-3, 5", PageRangeParser.Format([2, 0, 1, 4]));
    }
}
