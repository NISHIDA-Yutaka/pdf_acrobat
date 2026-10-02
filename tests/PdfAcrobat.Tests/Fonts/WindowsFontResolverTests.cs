using System.Buffers.Binary;
using System.IO;
using PdfAcrobat.Core.Fonts;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfAcrobat.Tests.Fonts;

public class WindowsFontResolverTests
{
    private static readonly string MsGothic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msgothic.ttc");

    [Fact]
    public void ExtractFace_ProducesStandaloneTrueTypeFont()
    {
        var collection = File.ReadAllBytes(MsGothic);
        Assert.True(TrueTypeCollection.IsCollection(collection));
        Assert.True(TrueTypeCollection.GetFaceCount(collection) >= 2);

        var face = TrueTypeCollection.ExtractFace(collection, 1);

        Assert.Equal(0x00010000u, BinaryPrimitives.ReadUInt32BigEndian(face));
        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(face.AsSpan(4));
        var tags = Enumerable.Range(0, tableCount)
            .Select(i => System.Text.Encoding.ASCII.GetString(face, 12 + 16 * i, 4))
            .ToList();
        Assert.Contains("glyf", tags);
        Assert.Contains("cmap", tags);
    }

    [Fact]
    public void JapaneseCollectionFont_IsEmbeddedAsSubset()
    {
        WindowsFontResolver.Install();
        var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Meiryo", 12, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
            gfx.DrawString("日本語のテキスト", font, XBrushes.Black, 40, 40);
        }

        using var stream = new MemoryStream();
        doc.Save(stream);

        // Meiryo is ~9 MB; a subset for a handful of glyphs stays small.
        Assert.InRange(stream.Length, 1_000, 200_000);
    }

    [Fact]
    public void UnknownFamily_IsNotResolved()
    {
        var resolver = new WindowsFontResolver();

        Assert.Null(resolver.ResolveTypeface("No Such Font Family 12345", false, false));
    }
}
