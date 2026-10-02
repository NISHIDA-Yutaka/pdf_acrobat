using System.Runtime.InteropServices;
using PdfAcrobat.Pdfium.Interop;

namespace PdfAcrobat.Pdfium;

public readonly record struct PdfTextRange(int Start, int Count)
{
    public int End => Start + Count;
}

[Flags]
public enum PdfFindFlags : uint
{
    None = 0,
    MatchCase = 0x1,
    MatchWholeWord = 0x2,
    Consecutive = 0x4,
}

/// <summary>
/// Immutable managed copy of a page's text layer. <see cref="Text"/> has exactly one UTF-16 unit per
/// PDFium character index, so indices can be used interchangeably with the native text API.
/// </summary>
public sealed class PdfTextSnapshot
{
    public PdfTextSnapshot(string text, PdfRect[] boxes, bool[] generated)
    {
        Text = text;
        Boxes = boxes;
        Generated = generated;
    }

    public string Text { get; }

    /// <summary>Loose character boxes in PDF user space (empty for generated characters).</summary>
    public PdfRect[] Boxes { get; }

    public bool[] Generated { get; }

    public int Length => Text.Length;

    public static PdfTextSnapshot Empty { get; } = new(string.Empty, [], []);
}

/// <summary>Text layer of a page. Valid while the owning <see cref="PdfPage"/> lease is alive.</summary>
public sealed unsafe class PdfTextPage
{
    private readonly PdfPage _page;
    private readonly PageHandle _handle;

    internal PdfTextPage(PdfPage page, PageHandle handle)
    {
        _page = page;
        _handle = handle;
    }

    private nint Handle => _handle.TextPage != 0
        ? _handle.TextPage
        : throw new ObjectDisposedException(nameof(PdfTextPage));

    public PdfPage Page => _page;

    public int CharCount
    {
        get
        {
            using var _ = PdfiumLibrary.Acquire();
            return Math.Max(0, PdfiumNative.FPDFText_CountChars(Handle));
        }
    }

    public string GetText(int start, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        using var _ = PdfiumLibrary.Acquire();
        var buffer = new ushort[count + 1];
        int written;
        fixed (ushort* p = buffer)
        {
            written = PdfiumNative.FPDFText_GetText(Handle, start, count, p);
        }

        if (written <= 1)
        {
            return string.Empty;
        }

        return new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, written - 1)));
    }

    public PdfRect GetCharBox(int index)
    {
        using var _ = PdfiumLibrary.Acquire();
        double left, right, bottom, top;
        return PdfiumNative.FPDFText_GetCharBox(Handle, index, &left, &right, &bottom, &top) != 0
            ? PdfRect.FromPoints(left, bottom, right, top)
            : default;
    }

    public PdfRect GetLooseCharBox(int index)
    {
        using var _ = PdfiumLibrary.Acquire();
        FS_RECTF r;
        return PdfiumNative.FPDFText_GetLooseCharBox(Handle, index, &r) != 0
            ? PdfRect.FromPoints(r.left, r.bottom, r.right, r.top)
            : default;
    }

    public double GetFontSize(int index)
    {
        using var _ = PdfiumLibrary.Acquire();
        return PdfiumNative.FPDFText_GetFontSize(Handle, index);
    }

    /// <summary>Character index at a point in user space, or -1.</summary>
    public int GetCharIndexAtPos(double x, double y, double xTolerance, double yTolerance)
    {
        using var _ = PdfiumLibrary.Acquire();
        return PdfiumNative.FPDFText_GetCharIndexAtPos(Handle, x, y, xTolerance, yTolerance);
    }

    /// <summary>Line-merged rectangles covering a character range (user space).</summary>
    public IReadOnlyList<PdfRect> GetTextRects(int start, int count)
    {
        using var _ = PdfiumLibrary.Acquire();
        var n = PdfiumNative.FPDFText_CountRects(Handle, start, count);
        var result = new List<PdfRect>(Math.Max(n, 0));
        for (var i = 0; i < n; i++)
        {
            double left, top, right, bottom;
            if (PdfiumNative.FPDFText_GetRect(Handle, i, &left, &top, &right, &bottom) != 0)
            {
                result.Add(PdfRect.FromPoints(left, bottom, right, top));
            }
        }

        return result;
    }

    /// <summary>Finds every occurrence of <paramref name="query"/> using PDFium's matcher.</summary>
    public IReadOnlyList<PdfTextRange> FindAll(string query, PdfFindFlags flags)
    {
        var result = new List<PdfTextRange>();
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        using var _ = PdfiumLibrary.Acquire();
        var chars = (query + "\0").ToCharArray();
        fixed (char* p = chars)
        {
            var search = PdfiumNative.FPDFText_FindStart(Handle, (ushort*)p, (uint)flags, 0);
            if (search == 0)
            {
                return result;
            }

            try
            {
                while (PdfiumNative.FPDFText_FindNext(search) != 0)
                {
                    result.Add(new PdfTextRange(
                        PdfiumNative.FPDFText_GetSchResultIndex(search),
                        PdfiumNative.FPDFText_GetSchCount(search)));
                }
            }
            finally
            {
                PdfiumNative.FPDFText_FindClose(search);
            }
        }

        return result;
    }

    /// <summary>Copies characters, loose boxes and generated flags into managed memory in one pass.</summary>
    public PdfTextSnapshot CreateSnapshot()
    {
        using var _ = PdfiumLibrary.Acquire();
        var handle = Handle;
        var count = Math.Max(0, PdfiumNative.FPDFText_CountChars(handle));
        var chars = new char[count];
        var boxes = new PdfRect[count];
        var generated = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var unicode = PdfiumNative.FPDFText_GetUnicode(handle, i);
            chars[i] = unicode switch
            {
                0 => ' ',
                <= 0xFFFF => (char)unicode,
                _ => '�',
            };
            generated[i] = PdfiumNative.FPDFText_IsGenerated(handle, i) == 1;
            FS_RECTF r;
            if (PdfiumNative.FPDFText_GetLooseCharBox(handle, i, &r) != 0)
            {
                boxes[i] = PdfRect.FromPoints(r.left, r.bottom, r.right, r.top);
            }
        }

        return new PdfTextSnapshot(new string(chars), boxes, generated);
    }

    /// <summary>URLs that appear as plain text on the page.</summary>
    public IReadOnlyList<(string Url, IReadOnlyList<PdfRect> Rects)> GetWebLinks()
    {
        using var _ = PdfiumLibrary.Acquire();
        var result = new List<(string, IReadOnlyList<PdfRect>)>();
        var links = PdfiumNative.FPDFLink_LoadWebLinks(Handle);
        if (links == 0)
        {
            return result;
        }

        try
        {
            var count = PdfiumNative.FPDFLink_CountWebLinks(links);
            for (var i = 0; i < count; i++)
            {
                var length = PdfiumNative.FPDFLink_GetURL(links, i, null, 0);
                if (length <= 1)
                {
                    continue;
                }

                var buffer = new ushort[length];
                fixed (ushort* p = buffer)
                {
                    PdfiumNative.FPDFLink_GetURL(links, i, p, length);
                }

                var url = new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, length - 1)));
                var rectCount = PdfiumNative.FPDFLink_CountRects(links, i);
                var rects = new List<PdfRect>(rectCount);
                for (var r = 0; r < rectCount; r++)
                {
                    double left, top, right, bottom;
                    if (PdfiumNative.FPDFLink_GetRect(links, i, r, &left, &top, &right, &bottom) != 0)
                    {
                        rects.Add(PdfRect.FromPoints(left, bottom, right, top));
                    }
                }

                result.Add((url, rects));
            }
        }
        finally
        {
            PdfiumNative.FPDFLink_CloseWebLinks(links);
        }

        return result;
    }
}
