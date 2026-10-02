using System.Runtime.InteropServices;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.Tests.Pdfium;

public class PdfDocumentTests
{
    [Fact]
    public void Load_ReportsPagesSizesAndMetadata()
    {
        using var doc = PdfDocument.Load(TestFiles.ReadSample("basic.pdf"));

        Assert.Equal(6, doc.PageCount);
        var a4 = doc.GetPageSize(0);
        Assert.Equal(595, a4.Width, 0);
        Assert.Equal(842, a4.Height, 0);

        // Landscape page and the page with /Rotate 90 are both displayed wider than tall.
        Assert.True(doc.GetPageSize(2).Width > doc.GetPageSize(2).Height);
        Assert.True(doc.GetPageSize(3).Width > doc.GetPageSize(3).Height);

        var letter = doc.GetPageSize(4);
        Assert.Equal(612, letter.Width, 0);
        Assert.Equal(792, letter.Height, 0);

        var info = doc.GetInfo();
        Assert.Equal("サンプル文書 / Sample Document", info.Title);
        Assert.False(info.IsEncrypted);
        Assert.NotNull(info.CreationDate);
    }

    [Fact]
    public void Bookmarks_AreReadAsTree()
    {
        using var doc = PdfDocument.Load(TestFiles.ReadSample("basic.pdf"));

        var bookmarks = doc.GetBookmarks();

        Assert.Equal(6, bookmarks.Count);
        Assert.Equal("第1章 はじめに", bookmarks[1].Title);
        Assert.Equal(2, bookmarks[1].Children.Count);
        Assert.Equal(1, bookmarks[1].Destination?.PageIndex);
        Assert.Equal(5, bookmarks[5].Destination?.PageIndex);
    }

    [Fact]
    public void EncryptedDocument_RequiresPassword()
    {
        var bytes = TestFiles.ReadSample("encrypted.pdf");

        Assert.Throws<PdfPasswordException>(() => PdfDocument.Load(bytes));
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Load(bytes, "wrong"));

        using var doc = PdfDocument.Load(bytes, "test");
        Assert.Equal(6, doc.PageCount);
        Assert.True(doc.IsEncrypted);
    }

    [Fact]
    public void Links_IncludeInternalAndExternalTargets()
    {
        using var doc = PdfDocument.Load(TestFiles.ReadSample("basic.pdf"));
        using var page = doc.OpenPage(0);

        var links = page.GetLinks();

        Assert.Equal(5, links.Count(l => l.Destination is not null));
        Assert.Contains(links, l => l.Destination?.PageIndex == 3);
        Assert.Contains(links, l => l.Uri == "https://github.com/NISHIDA-Yutaka/pdf_acrobat");
    }

    [Fact]
    public void Text_SnapshotAndSearchFindJapaneseKeyword()
    {
        using var doc = PdfDocument.Load(TestFiles.ReadSample("basic.pdf"));
        using var page = doc.OpenPage(4);
        var text = page.GetTextPage();

        var snapshot = text.CreateSnapshot();
        var hits = text.FindAll("ユニーク検索語", PdfFindFlags.None);

        Assert.Contains("ユニーク検索語", snapshot.Text);
        Assert.Single(hits);
        Assert.Equal(snapshot.Text.IndexOf("ユニーク検索語", StringComparison.Ordinal), hits[0].Start);
        var rects = text.GetTextRects(hits[0].Start, hits[0].Count);
        Assert.NotEmpty(rects);
        Assert.All(rects, r => Assert.False(r.IsEmpty));
    }

    [Fact]
    public void RotatedPage_GeometryMatchesDisplaySize()
    {
        using var doc = PdfDocument.Load(TestFiles.ReadSample("basic.pdf"));
        using var page = doc.OpenPage(3);

        var geometry = page.Geometry;

        Assert.Equal(1, geometry.Rotation);
        Assert.Equal(doc.GetPageSize(3).Width, geometry.Width, 3);

        // The bottom-left corner of the unrotated page appears at the top-left of the display.
        var m = geometry.GetDisplayMatrix(0, 0, geometry.Width, geometry.Height, 0);
        var p = m.Transform(geometry.BBox.Left, geometry.BBox.Bottom);
        Assert.Equal(0, p.X, 3);
        Assert.Equal(0, p.Y, 3);

        // Round trip through the inverse matrix.
        var back = m.Invert().Transform(p);
        Assert.Equal(geometry.BBox.Left, back.X, 3);
        Assert.Equal(geometry.BBox.Bottom, back.Y, 3);
    }

    [Fact]
    public void Render_ProducesNonBlankPixels()
    {
        using var doc = PdfDocument.Load(TestFiles.ReadSample("basic.pdf"));
        using var page = doc.OpenPage(5);
        const int width = 300;
        const int height = 424;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var result = page.Render(
                new PdfRenderTarget(handle.AddrOfPinnedObject(), width, height, stride),
                0, 0, width, height, 0, PdfRenderFlags.Annotations);

            Assert.Equal(PdfRenderResult.Done, result);
        }
        finally
        {
            handle.Free();
        }

        var nonWhite = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 250 || pixels[i + 1] < 250 || pixels[i + 2] < 250)
            {
                nonWhite++;
            }
        }

        Assert.True(nonWhite > 1000, $"Expected rendered content, got {nonWhite} non-white pixels");
    }

    [Fact]
    public void Render_CanBeCancelled()
    {
        using var doc = PdfDocument.Load(TestFiles.ReadSample("basic.pdf"));
        using var page = doc.OpenPage(1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var pixels = new byte[100 * 100 * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            Assert.Throws<OperationCanceledException>(() => page.Render(
                new PdfRenderTarget(handle.AddrOfPinnedObject(), 100, 100, 400),
                0, 0, 100, 141, 0, PdfRenderFlags.None, cancellationToken: cts.Token));
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void StructuralEdits_RoundTripThroughSave()
    {
        using var doc = PdfDocument.Load(TestFiles.ReadSample("basic.pdf"));
        using var other = PdfDocument.Load(TestFiles.ReadSample("basic.pdf"));

        doc.DeletePage(1);
        doc.MovePages([0], 4);
        doc.InsertBlankPage(0, 300, 400);
        doc.ImportPages(other, [5], doc.PageCount);
        doc.SetPageRotation(1, 1);
        var bytes = doc.Save();

        using var reloaded = PdfDocument.Load(bytes);
        Assert.Equal(7, reloaded.PageCount);
        Assert.Equal(300, reloaded.GetPageSize(0).Width, 0);
        using var last = reloaded.OpenPage(6);
        Assert.Contains("第5章", last.GetTextPage().CreateSnapshot().Text);
        using var rotated = reloaded.OpenPage(1);
        Assert.Equal(1, rotated.Geometry.Rotation);
    }

    [Fact]
    public void PageLeases_ShareCachedHandles()
    {
        using var doc = PdfDocument.Load(TestFiles.ReadSample("basic.pdf"));
        using var first = doc.OpenPage(0);
        using var second = doc.OpenPage(0);

        Assert.Equal(first.Geometry, second.Geometry);
        second.Dispose();
        Assert.Equal(595, first.Width, 0);
    }
}
