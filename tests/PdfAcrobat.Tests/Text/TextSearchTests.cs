using PdfAcrobat.Core.Documents;
using PdfAcrobat.Core.Text;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.Tests.Text;

public class TextSearchTests
{
    [Theory]
    [InlineData("The Quick brown fox", "quick", 4, 5)]
    [InlineData("ＡＢＣ１２３", "abc123", 0, 6)]
    [InlineData("ｶﾀｶﾅの検索", "カタカナ", 0, 4)]
    public void FindInText_FoldsCaseAndWidth(string text, string query, int start, int count)
    {
        var hits = TextSearch.FindInText(text, query, new SearchOptions());

        var hit = Assert.Single(hits);
        Assert.Equal(new PdfTextRange(start, count), hit);
    }

    [Fact]
    public void FindInText_MatchesAcrossJapaneseLineBreak()
    {
        // PDFium inserts generated CR/LF at the end of each line.
        const string text = "これは検索\r\n語のテストです";

        var hits = TextSearch.FindInText(text, "検索語", new SearchOptions());

        var hit = Assert.Single(hits);
        Assert.Equal(3, hit.Start);
        Assert.Equal("検索\r\n語", text.Substring(hit.Start, hit.Count));
    }

    [Fact]
    public void FindInText_TreatsLatinLineBreakAsSpace()
    {
        const string text = "the quick\r\nbrown fox";

        Assert.Single(TextSearch.FindInText(text, "quick brown", new SearchOptions()));
        Assert.Empty(TextSearch.FindInText(text, "quickbrown", new SearchOptions()));
    }

    [Fact]
    public void FindInText_RespectsMatchCaseAndWholeWord()
    {
        const string text = "Cat concatenate cat";

        Assert.Equal(3, TextSearch.FindInText(text, "cat", new SearchOptions()).Count);
        Assert.Equal(2, TextSearch.FindInText(text, "cat", new SearchOptions(WholeWord: true)).Count);
        Assert.Equal(2, TextSearch.FindInText(text, "cat", new SearchOptions(MatchCase: true)).Count);
        Assert.Single(TextSearch.FindInText(text, "cat", new SearchOptions(MatchCase: true, WholeWord: true)));
    }

    [Fact]
    public void WordAt_UsesScriptRunsForJapanese()
    {
        var snapshot = new PdfTextSnapshot("漢字とひらがなとABC", new PdfRect[11], new bool[11]);

        Assert.Equal(new PdfTextRange(0, 2), TextGeometry.WordAt(snapshot, 1));
        // "とひらがなと" is one hiragana run.
        Assert.Equal(new PdfTextRange(2, 6), TextGeometry.WordAt(snapshot, 4));
        Assert.Equal(new PdfTextRange(8, 3), TextGeometry.WordAt(snapshot, 9));
    }

    [Fact]
    public void RangeRects_MergeCharactersPerLine()
    {
        var boxes = new[]
        {
            new PdfRect(0, 100, 10, 112), new PdfRect(10, 100, 20, 112), new PdfRect(20, 100, 30, 112),
            default, default, // generated CR LF
            new PdfRect(0, 80, 10, 92), new PdfRect(10, 80, 20, 92),
        };
        var snapshot = new PdfTextSnapshot("abc\r\nde", boxes, [false, false, false, true, true, false, false]);

        var rects = TextGeometry.GetRangeRects(snapshot, 1, 6);

        Assert.Equal(2, rects.Count);
        Assert.Equal(new PdfRect(10, 100, 30, 112), rects[0]);
        Assert.Equal(new PdfRect(0, 80, 20, 92), rects[1]);
        Assert.Equal("bc" + Environment.NewLine + "de", TextGeometry.GetText(snapshot, 1, 6));
    }

    [Fact]
    public void RangeRects_DoNotMergeLinesWhoseFirstGlyphIsBelowPreviousLine()
    {
        // Line 1 "ab", line 2 "cd" starting at the same x (typical left-aligned paragraph).
        var boxes = new[]
        {
            new PdfRect(0, 100, 10, 112), new PdfRect(10, 100, 20, 112),
            new PdfRect(0, 86, 10, 98), new PdfRect(10, 86, 20, 98),
        };
        var snapshot = new PdfTextSnapshot("abcd", boxes, new bool[4]);

        var rects = TextGeometry.GetRangeRects(snapshot, 0, 4);

        Assert.Equal(2, rects.Count);
    }

    [Fact]
    public void RangeRects_MergeVerticalTextColumn()
    {
        // Three glyphs stacked top to bottom (tategaki).
        var boxes = new[] { new PdfRect(50, 100, 62, 112), new PdfRect(50, 88, 62, 100), new PdfRect(50, 76, 62, 88) };
        var snapshot = new PdfTextSnapshot("縦書き", boxes, new bool[3]);

        var rect = Assert.Single(TextGeometry.GetRangeRects(snapshot, 0, 3));
        Assert.Equal(new PdfRect(50, 76, 62, 112), rect);
    }

    [Fact]
    public async Task SearchDocument_ReportsHitsWithPageIndices()
    {
        using var session = new DocumentSession(PdfSource.Load(TestFiles.ReadSample("basic.pdf"), "basic.pdf"));
        var hits = new List<SearchHit>();

        await TextSearch.SearchDocumentAsync(session, new PageTextCache(), "ユニーク検索語", new SearchOptions(),
            pageHits => { lock (hits) { hits.AddRange(pageHits); } }, null, CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal(4, hit.PageIndex);
        Assert.NotEmpty(hit.Rects);
        Assert.Contains("ユニーク検索語", hit.Context);
    }
}
