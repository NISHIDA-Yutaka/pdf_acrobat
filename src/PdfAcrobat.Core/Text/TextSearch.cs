using PdfAcrobat.Core.Documents;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.Core.Text;

public sealed record SearchOptions(bool MatchCase = false, bool WholeWord = false, bool MatchWidth = false);

/// <summary>One search result. Rectangles are in the PDF user space of the source page.</summary>
public sealed record SearchHit(int PageIndex, Guid PageId, PdfTextRange Range, IReadOnlyList<PdfRect> Rects, string Context);

public static class TextSearch
{
    /// <summary>Finds all matches of <paramref name="query"/> in one page's text.</summary>
    public static IReadOnlyList<PdfTextRange> FindInText(string text, string query, SearchOptions options)
    {
        var result = new List<PdfTextRange>();
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(text))
        {
            return result;
        }

        var normalizedText = TextNormalizer.Normalize(text, !options.MatchCase, !options.MatchWidth);
        var normalizedQuery = TextNormalizer.Normalize(query.Trim(), !options.MatchCase, !options.MatchWidth).Text;
        if (normalizedQuery.Length == 0)
        {
            return result;
        }

        var position = 0;
        while (position <= normalizedText.Text.Length - normalizedQuery.Length)
        {
            var found = normalizedText.Text.IndexOf(normalizedQuery, position, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            var start = normalizedText.Map[found];
            var end = normalizedText.Map[found + normalizedQuery.Length - 1] + 1;
            if (!options.WholeWord || IsWholeWord(text, start, end))
            {
                result.Add(new PdfTextRange(start, end - start));
            }

            position = found + Math.Max(1, normalizedQuery.Length);
        }

        return result;
    }

    private static bool IsWholeWord(string text, int start, int end)
    {
        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) && !TextGeometry.IsCjk(c);

        var before = start > 0 && IsWordChar(text[start - 1]) && IsWordChar(text[start]);
        var after = end < text.Length && IsWordChar(text[end]) && IsWordChar(text[end - 1]);
        return !before && !after;
    }

    /// <summary>Searches every page of a session in document order, reporting hits as they are found.</summary>
    public static async Task SearchDocumentAsync(
        DocumentSession session,
        PageTextCache cache,
        string query,
        SearchOptions options,
        Action<IReadOnlyList<SearchHit>> onPageHits,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var pages = session.Pages;
        await Task.Run(() =>
        {
            for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = pages[pageIndex];
                if (page.IsBlank)
                {
                    continue;
                }

                var source = session.GetSource(page.SourceId!.Value);
                var snapshot = cache.Get(source, page.SourceIndex);
                var ranges = FindInText(snapshot.Text, query, options);
                if (ranges.Count > 0)
                {
                    var hits = ranges
                        .Select(r => new SearchHit(pageIndex, page.Id, r, TextGeometry.GetRangeRects(snapshot, r.Start, r.Count), Context(snapshot.Text, r)))
                        .ToList();
                    onPageHits(hits);
                }

                progress?.Report((pageIndex + 1) / (double)pages.Count);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string Context(string text, PdfTextRange range)
    {
        const int Radius = 24;
        var start = Math.Max(0, range.Start - Radius);
        var end = Math.Min(text.Length, range.End + Radius);
        var snippet = text[start..end];
        snippet = string.Join(' ', snippet.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        return (start > 0 ? "…" : string.Empty) + snippet + (end < text.Length ? "…" : string.Empty);
    }
}
