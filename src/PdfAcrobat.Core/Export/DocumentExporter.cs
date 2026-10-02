using PdfAcrobat.Core.Documents;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.Core.Export;

/// <summary>A bookmark added while exporting (e.g. one per file when combining).</summary>
public sealed record ExportBookmark(string Title, int PageIndex);

public sealed record ExportOptions
{
    /// <summary>Pages to write (indices into the session's pages). Null writes every page.</summary>
    public IReadOnlyList<int>? PageIndices { get; init; }

    /// <summary>
    /// Build the output on top of the primary file so that bookmarks, forms, metadata and other
    /// document-level data survive. When false a fresh document is assembled (used for extract/split).
    /// </summary>
    public bool PreserveDocumentStructure { get; init; } = true;

    /// <summary>Top-level bookmarks to append.</summary>
    public IReadOnlyList<ExportBookmark> Bookmarks { get; init; } = [];

    public string? Title { get; init; }
}

/// <summary>
/// Turns a <see cref="DocumentSession"/> into PDF bytes. Pages are assembled with PDFium
/// (import, reorder, rotate) and the result is finalized with PDFsharp, which also drops objects that
/// are no longer reachable (e.g. the content of deleted pages).
/// </summary>
public static class DocumentExporter
{
    public static byte[] Export(DocumentSession session, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        var pages = (options.PageIndices ?? Enumerable.Range(0, session.Pages.Count).ToList())
            .Select(i => session.Pages[i])
            .ToList();
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("書き出すページがありません。");
        }

        if (options.PageIndices is null && options.Bookmarks.Count == 0 && options.Title is null && IsUnchanged(session, pages))
        {
            return session.PrimarySource.Bytes;
        }

        var assembled = options.PreserveDocumentStructure
            ? AssembleOnPrimary(session, pages)
            : AssembleFresh(session, pages);
        return PdfFinalizer.Finalize(assembled, new FinalizeOptions(options.Bookmarks, options.Title));
    }

    /// <summary>True when the pages are exactly the primary file's pages in their original state.</summary>
    public static bool IsUnchanged(DocumentSession session, IReadOnlyList<PageRef> pages)
    {
        var primary = session.PrimarySource;
        if (pages.Count != primary.PageCount)
        {
            return false;
        }

        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            if (page.SourceId != primary.Id || page.SourceIndex != i || page.Rotation != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] AssembleOnPrimary(DocumentSession session, IReadOnlyList<PageRef> pages)
    {
        var primary = session.PrimarySource;
        using var output = PdfDocument.Load(primary.Bytes, primary.Password);
        PdfDocument? primaryCopy = null;
        try
        {
            var originalCount = output.PageCount;
            var used = new HashSet<int>();
            var slots = new int[pages.Count];
            var count = originalCount;

            // 1. Keep the first use of each original page in place; append everything else.
            for (var i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                if (page.IsBlank)
                {
                    var size = page.BlankSize ?? new PdfSize(595, 842);
                    output.InsertBlankPage(count, size.Width, size.Height);
                    slots[i] = count++;
                }
                else if (page.SourceId == primary.Id && used.Add(page.SourceIndex))
                {
                    slots[i] = page.SourceIndex;
                }
                else
                {
                    var source = page.SourceId == primary.Id
                        ? primaryCopy ??= PdfDocument.Load(primary.Bytes, primary.Password)
                        : session.GetSource(page.SourceId!.Value).Document;
                    output.ImportPages(source, [page.SourceIndex], count);
                    slots[i] = count++;
                }
            }

            // 2. Remove original pages that are no longer used.
            var unused = Enumerable.Range(0, originalCount).Where(i => !used.Contains(i)).ToArray();
            for (var k = unused.Length - 1; k >= 0; k--)
            {
                output.DeletePage(unused[k]);
            }

            for (var i = 0; i < slots.Length; i++)
            {
                var shift = 0;
                foreach (var removed in unused)
                {
                    if (removed < slots[i])
                    {
                        shift++;
                    }
                }

                slots[i] -= shift;
            }

            // 3. Put the pages in the requested order.
            if (!slots.SequenceEqual(Enumerable.Range(0, slots.Length)))
            {
                output.MovePages(slots, 0);
            }

            // 4. Apply rotation changes.
            for (var i = 0; i < pages.Count; i++)
            {
                output.RotatePage(i, pages[i].Rotation);
            }

            return output.Save(PdfSaveFlags.NoIncremental | (primary.Info.IsEncrypted ? PdfSaveFlags.RemoveSecurity : PdfSaveFlags.None));
        }
        finally
        {
            primaryCopy?.Dispose();
        }
    }

    private static byte[] AssembleFresh(DocumentSession session, IReadOnlyList<PageRef> pages)
    {
        using var output = PdfDocument.CreateNew();
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            if (page.IsBlank)
            {
                var size = page.BlankSize ?? new PdfSize(595, 842);
                output.InsertBlankPage(i, size.Width, size.Height);
            }
            else
            {
                output.ImportPages(session.GetSource(page.SourceId!.Value).Document, [page.SourceIndex], i);
            }

            output.RotatePage(i, page.Rotation);
        }

        output.CopyViewerPreferences(session.PrimarySource.Document);
        return output.Save();
    }
}
