using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using PdfAcrobat.Pdfium.Interop;

namespace PdfAcrobat.Pdfium;

[Flags]
public enum PdfSaveFlags : uint
{
    None = 0,
    Incremental = 1 << 0,
    NoIncremental = 1 << 1,
    RemoveSecurity = 1 << 2,
}

/// <summary>
/// A PDFium document. Thread-safe: every member takes the global PDFium lock.
/// Pages are handed out as ref-counted leases (<see cref="PdfPage"/>) backed by a small LRU cache,
/// so repeatedly rendering or querying the same page does not re-parse its content.
/// </summary>
public sealed unsafe class PdfDocument : IDisposable
{
    private const int MaxCachedIdlePages = 24;

    private readonly Dictionary<int, PageHandle> _pages = new();
    private nint _handle;
    private void* _data;
    private long _useCounter;
    private bool _disposeRequested;

    private PdfDocument(nint handle, void* data)
    {
        _handle = handle;
        _data = data;
    }

    internal nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return _handle;
        }
    }

    public bool IsDisposed => _handle == 0 || _disposeRequested;

    /// <summary>Loads a document from memory. The bytes are copied into native memory owned by the document.</summary>
    /// <exception cref="PdfPasswordException">The document is encrypted and the password is missing or wrong.</exception>
    /// <exception cref="PdfiumException">The data is not a valid PDF.</exception>
    public static PdfDocument Load(ReadOnlySpan<byte> data, string? password = null)
    {
        var size = (nuint)data.Length;
        var buffer = NativeMemory.Alloc(size == 0 ? 1 : size);
        data.CopyTo(new Span<byte>(buffer, data.Length));

        using var _ = PdfiumLibrary.Acquire();
        var handle = LoadCore(buffer, size, password);
        if (handle == 0)
        {
            var code = (PdfiumErrorCode)PdfiumNative.FPDF_GetLastError();
            NativeMemory.Free(buffer);
            throw PdfiumException.FromCode(code);
        }

        return new PdfDocument(handle, buffer);
    }

    private static nint LoadCore(void* buffer, nuint size, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return PdfiumNative.FPDF_LoadMemDocument64(buffer, size, null);
        }

        var bytes = Encoding.UTF8.GetBytes(password + "\0");
        fixed (byte* p = bytes)
        {
            return PdfiumNative.FPDF_LoadMemDocument64(buffer, size, p);
        }
    }

    /// <summary>Creates a new, empty document.</summary>
    public static PdfDocument CreateNew()
    {
        using var _ = PdfiumLibrary.Acquire();
        var handle = PdfiumNative.FPDF_CreateNewDocument();
        if (handle == 0)
        {
            throw new PdfiumException(PdfiumErrorCode.Unknown, "新しい PDF を作成できませんでした。");
        }

        return new PdfDocument(handle, null);
    }

    public int PageCount
    {
        get
        {
            using var _ = PdfiumLibrary.Acquire();
            return PdfiumNative.FPDF_GetPageCount(Handle);
        }
    }

    /// <summary>PDF version multiplied by 10 (e.g. 17 for PDF 1.7), or null if unknown.</summary>
    public int? FileVersion
    {
        get
        {
            using var _ = PdfiumLibrary.Acquire();
            int version;
            return PdfiumNative.FPDF_GetFileVersion(Handle, &version) != 0 ? version : null;
        }
    }

    public PdfPermissions Permissions
    {
        get
        {
            using var _ = PdfiumLibrary.Acquire();
            return (PdfPermissions)PdfiumNative.FPDF_GetDocPermissions(Handle);
        }
    }

    /// <summary>Revision of the standard security handler, or -1 when the document is not encrypted.</summary>
    public int SecurityHandlerRevision
    {
        get
        {
            using var _ = PdfiumLibrary.Acquire();
            return PdfiumNative.FPDF_GetSecurityHandlerRevision(Handle);
        }
    }

    public bool IsEncrypted => SecurityHandlerRevision >= 0;

    /// <summary>Displayed page size in points (after applying /Rotate). Does not parse page content.</summary>
    public PdfSize GetPageSize(int pageIndex)
    {
        using var _ = PdfiumLibrary.Acquire();
        FS_SIZEF size;
        if (PdfiumNative.FPDF_GetPageSizeByIndexF(Handle, pageIndex, &size) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        return new PdfSize(size.width, size.height);
    }

    public PdfSize[] GetAllPageSizes()
    {
        using var _ = PdfiumLibrary.Acquire();
        var count = PdfiumNative.FPDF_GetPageCount(Handle);
        var result = new PdfSize[count];
        for (var i = 0; i < count; i++)
        {
            FS_SIZEF size;
            result[i] = PdfiumNative.FPDF_GetPageSizeByIndexF(Handle, i, &size) != 0
                ? new PdfSize(size.width, size.height)
                : new PdfSize(612, 792);
        }

        return result;
    }

    public string? GetPageLabel(int pageIndex)
    {
        using var _ = PdfiumLibrary.Acquire();
        var text = NativeStrings.ReadUtf16((buffer, length) => PdfiumNative.FPDF_GetPageLabel(Handle, pageIndex, buffer, length));
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public string GetMetaText(string tag)
    {
        using var _ = PdfiumLibrary.Acquire();
        var tagBytes = Encoding.ASCII.GetBytes(tag + "\0");
        fixed (byte* pTag = tagBytes)
        {
            var p = pTag;
            return NativeStrings.ReadUtf16((buffer, length) => PdfiumNative.FPDF_GetMetaText(Handle, p, buffer, length));
        }
    }

    public PdfDocumentInfo GetInfo()
    {
        using var _ = PdfiumLibrary.Acquire();
        string? language;
        {
            var length = PdfiumNative.FPDFCatalog_GetLanguage(Handle, null, 0);
            if (length > 2)
            {
                var buffer = new ushort[length / 2];
                fixed (ushort* p = buffer)
                {
                    PdfiumNative.FPDFCatalog_GetLanguage(Handle, p, length);
                }

                language = new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, buffer.Length - 1)));
            }
            else
            {
                language = null;
            }
        }

        return new PdfDocumentInfo
        {
            Title = GetMetaText("Title"),
            Author = GetMetaText("Author"),
            Subject = GetMetaText("Subject"),
            Keywords = GetMetaText("Keywords"),
            Creator = GetMetaText("Creator"),
            Producer = GetMetaText("Producer"),
            CreationDate = PdfDate.TryParse(GetMetaText("CreationDate")),
            ModificationDate = PdfDate.TryParse(GetMetaText("ModDate")),
            FileVersion = FileVersion,
            PageCount = PageCount,
            IsTagged = PdfiumNative.FPDFCatalog_IsTagged(Handle) != 0,
            Language = language,
            SecurityHandlerRevision = SecurityHandlerRevision,
            Permissions = Permissions,
        };
    }

    /// <summary>Returns the outline (bookmark) tree.</summary>
    public IReadOnlyList<PdfBookmark> GetBookmarks()
    {
        using var _ = PdfiumLibrary.Acquire();
        var visited = new HashSet<nint>();
        return ReadBookmarkLevel(0, visited, depth: 0);
    }

    private List<PdfBookmark> ReadBookmarkLevel(nint parent, HashSet<nint> visited, int depth)
    {
        var result = new List<PdfBookmark>();
        if (depth > 64)
        {
            return result;
        }

        var current = PdfiumNative.FPDFBookmark_GetFirstChild(Handle, parent);
        while (current != 0 && visited.Add(current))
        {
            var node = current;
            var title = NativeStrings.ReadUtf16((buffer, length) => PdfiumNative.FPDFBookmark_GetTitle(node, buffer, length));
            var count = PdfiumNative.FPDFBookmark_GetCount(node);
            PdfDestination? destination = null;
            string? uri = null;

            var dest = PdfiumNative.FPDFBookmark_GetDest(Handle, node);
            if (dest != 0)
            {
                destination = ReadDestination(dest);
            }
            else
            {
                var action = PdfiumNative.FPDFBookmark_GetAction(node);
                (destination, uri) = ReadAction(action);
            }

            var children = ReadBookmarkLevel(node, visited, depth + 1);
            result.Add(new PdfBookmark(title, destination, uri, children, IsOpen: count > 0));
            current = PdfiumNative.FPDFBookmark_GetNextSibling(Handle, node);
        }

        return result;
    }

    internal (PdfDestination? Destination, string? Uri) ReadAction(nint action)
    {
        if (action == 0)
        {
            return (null, null);
        }

        switch (PdfiumNative.FPDFAction_GetType(action))
        {
            case 1: // PDFACTION_GOTO
                var dest = PdfiumNative.FPDFAction_GetDest(Handle, action);
                return (dest != 0 ? ReadDestination(dest) : null, null);
            case 3: // PDFACTION_URI
                var length = PdfiumNative.FPDFAction_GetURIPath(Handle, action, null, 0);
                if (length <= 1)
                {
                    return (null, null);
                }

                var bytes = new byte[length];
                fixed (byte* p = bytes)
                {
                    PdfiumNative.FPDFAction_GetURIPath(Handle, action, p, length);
                }

                return (null, Encoding.UTF8.GetString(bytes, 0, (int)length - 1));
            default:
                return (null, null);
        }
    }

    internal PdfDestination? ReadDestination(nint dest)
    {
        var pageIndex = PdfiumNative.FPDFDest_GetDestPageIndex(Handle, dest);
        if (pageIndex < 0)
        {
            return null;
        }

        int hasX, hasY, hasZoom;
        float x, y, zoom;
        double? dx = null, dy = null, dz = null;
        if (PdfiumNative.FPDFDest_GetLocationInPage(dest, &hasX, &hasY, &hasZoom, &x, &y, &zoom) != 0)
        {
            dx = hasX != 0 ? x : null;
            dy = hasY != 0 ? y : null;
            dz = hasZoom != 0 && zoom > 0 ? zoom : null;
        }

        uint numParams;
        var parameters = stackalloc float[4];
        var view = (PdfDestinationView)PdfiumNative.FPDFDest_GetView(dest, &numParams, parameters);
        return new PdfDestination(pageIndex, dx, dy, dz, view);
    }

    /// <summary>Resolves a named destination.</summary>
    public PdfDestination? GetNamedDestination(string name)
    {
        using var _ = PdfiumLibrary.Acquire();
        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes)
        {
            var dest = PdfiumNative.FPDF_GetNamedDestByName(Handle, p);
            return dest != 0 ? ReadDestination(dest) : null;
        }
    }

    // ---- Pages -----------------------------------------------------------

    /// <summary>
    /// Opens (or reuses) a page and returns a lease. Dispose the lease when done; the page stays
    /// cached for a while so subsequent calls are cheap.
    /// </summary>
    public PdfPage OpenPage(int pageIndex)
    {
        using var _ = PdfiumLibrary.Acquire();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!_pages.TryGetValue(pageIndex, out var handle))
        {
            var page = PdfiumNative.FPDF_LoadPage(Handle, pageIndex);
            if (page == 0)
            {
                throw new PdfiumException(PdfiumErrorCode.Page, $"ページ {pageIndex + 1} を読み込めませんでした。");
            }

            handle = new PageHandle(page);
            _pages.Add(pageIndex, handle);
        }

        handle.RefCount++;
        handle.LastUsed = ++_useCounter;
        return new PdfPage(this, pageIndex, handle);
    }

    internal void Release(PageHandle handle)
    {
        using var _ = PdfiumLibrary.Acquire();
        handle.RefCount--;
        if (handle.RefCount > 0)
        {
            return;
        }

        if (_disposeRequested)
        {
            TryFinalClose();
            return;
        }

        TrimCache();
    }

    private void TrimCache()
    {
        var idle = _pages.Where(kv => kv.Value.RefCount == 0).ToList();
        if (idle.Count <= MaxCachedIdlePages)
        {
            return;
        }

        foreach (var kv in idle.OrderBy(kv => kv.Value.LastUsed).Take(idle.Count - MaxCachedIdlePages))
        {
            kv.Value.Close();
            _pages.Remove(kv.Key);
        }
    }

    /// <summary>Closes every cached page. Must be called before structural edits shift page indices.</summary>
    private void InvalidatePageCache()
    {
        if (_pages.Values.Any(p => p.RefCount > 0))
        {
            throw new InvalidOperationException("ページが使用中のため、文書の構造を変更できません。");
        }

        foreach (var page in _pages.Values)
        {
            page.Close();
        }

        _pages.Clear();
    }

    // ---- Structural editing (used on export documents) -----------------------

    /// <summary>Imports pages from another document at <paramref name="insertIndex"/>.</summary>
    public void ImportPages(PdfDocument source, ReadOnlySpan<int> pageIndices, int insertIndex)
    {
        using var _ = PdfiumLibrary.Acquire();
        InvalidatePageCache();
        fixed (int* p = pageIndices)
        {
            if (PdfiumNative.FPDF_ImportPagesByIndex(Handle, source.Handle, p, (uint)pageIndices.Length, insertIndex) == 0)
            {
                throw new PdfiumException(PdfiumErrorCode.Unknown, "ページを取り込めませんでした。");
            }
        }
    }

    public void DeletePage(int pageIndex)
    {
        using var _ = PdfiumLibrary.Acquire();
        InvalidatePageCache();
        PdfiumNative.FPDFPage_Delete(Handle, pageIndex);
    }

    /// <summary>Moves the given pages (in the given order) so that they start at <paramref name="destIndex"/>.</summary>
    public void MovePages(ReadOnlySpan<int> pageIndices, int destIndex)
    {
        using var _ = PdfiumLibrary.Acquire();
        InvalidatePageCache();
        fixed (int* p = pageIndices)
        {
            if (PdfiumNative.FPDF_MovePages(Handle, p, (uint)pageIndices.Length, destIndex) == 0)
            {
                throw new PdfiumException(PdfiumErrorCode.Unknown, "ページを移動できませんでした。");
            }
        }
    }

    /// <summary>Inserts a blank page of the given size (points).</summary>
    public void InsertBlankPage(int pageIndex, double width, double height)
    {
        using var _ = PdfiumLibrary.Acquire();
        InvalidatePageCache();
        var page = PdfiumNative.FPDFPage_New(Handle, pageIndex, width, height);
        if (page == 0)
        {
            throw new PdfiumException(PdfiumErrorCode.Unknown, "空白ページを追加できませんでした。");
        }

        PdfiumNative.FPDF_ClosePage(page);
    }

    /// <summary>Sets /Rotate of a page (quarter turns, 0-3).</summary>
    public void SetPageRotation(int pageIndex, int quarterTurns)
    {
        using var _ = PdfiumLibrary.Acquire();
        InvalidatePageCache();
        var page = PdfiumNative.FPDF_LoadPage(Handle, pageIndex);
        if (page == 0)
        {
            throw new PdfiumException(PdfiumErrorCode.Page, $"ページ {pageIndex + 1} を読み込めませんでした。");
        }

        PdfiumNative.FPDFPage_SetRotation(page, ((quarterTurns % 4) + 4) % 4);
        PdfiumNative.FPDF_ClosePage(page);
    }

    public bool CopyViewerPreferences(PdfDocument source)
    {
        using var _ = PdfiumLibrary.Acquire();
        return PdfiumNative.FPDF_CopyViewerPreferences(Handle, source.Handle) != 0;
    }

    // ---- Saving ----------------------------------------------------------

    public byte[] Save(PdfSaveFlags flags = PdfSaveFlags.NoIncremental, int? fileVersion = null)
    {
        using var stream = new MemoryStream();
        Save(stream, flags, fileVersion);
        return stream.ToArray();
    }

    public void Save(Stream stream, PdfSaveFlags flags = PdfSaveFlags.NoIncremental, int? fileVersion = null)
    {
        using var _ = PdfiumLibrary.Acquire();
        var gch = GCHandle.Alloc(stream);
        try
        {
            FileWriteContext context;
            context.Base.version = 1;
            context.Base.WriteBlock = &WriteBlock;
            context.Stream = GCHandle.ToIntPtr(gch);
            var ok = fileVersion is { } v
                ? PdfiumNative.FPDF_SaveWithVersion(Handle, &context.Base, (uint)flags, v)
                : PdfiumNative.FPDF_SaveAsCopy(Handle, &context.Base, (uint)flags);
            if (ok == 0)
            {
                throw new PdfiumException(PdfiumErrorCode.Unknown, "PDF を保存できませんでした。");
            }
        }
        finally
        {
            gch.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileWriteContext
    {
        public FPDF_FILEWRITE Base;
        public nint Stream;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteBlock(FPDF_FILEWRITE* self, void* data, uint size)
    {
        try
        {
            var context = (FileWriteContext*)self;
            var stream = (Stream)GCHandle.FromIntPtr(context->Stream).Target!;
            stream.Write(new ReadOnlySpan<byte>(data, (int)size));
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    // ---- Lifetime ----------------------------------------------------------

    public void Dispose()
    {
        using var _ = PdfiumLibrary.Acquire();
        if (_handle == 0 || _disposeRequested)
        {
            return;
        }

        _disposeRequested = true;
        TryFinalClose();
    }

    private void TryFinalClose()
    {
        foreach (var (index, page) in _pages.Where(kv => kv.Value.RefCount == 0).ToList())
        {
            page.Close();
            _pages.Remove(index);
        }

        if (_pages.Count > 0 || _handle == 0)
        {
            return; // Outstanding leases; closed when the last one is released.
        }

        PdfiumNative.FPDF_CloseDocument(_handle);
        _handle = 0;
        if (_data != null)
        {
            NativeMemory.Free(_data);
            _data = null;
        }
    }
}

/// <summary>Native page and text-page handles shared by all leases of one page.</summary>
internal sealed class PageHandle(nint page)
{
    public nint Page { get; private set; } = page;

    public nint TextPage { get; set; }

    public int RefCount { get; set; }

    public long LastUsed { get; set; }

    public PdfPageGeometry? Geometry { get; set; }

    /// <summary>Only one progressive render may run on a page at a time.</summary>
    public SemaphoreSlim RenderGate { get; } = new(1, 1);

    public void Close()
    {
        if (TextPage != 0)
        {
            PdfiumNative.FPDFText_ClosePage(TextPage);
            TextPage = 0;
        }

        if (Page != 0)
        {
            PdfiumNative.FPDF_ClosePage(Page);
            Page = 0;
        }
    }
}
