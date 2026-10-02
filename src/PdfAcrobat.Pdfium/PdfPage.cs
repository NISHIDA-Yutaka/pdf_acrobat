using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PdfAcrobat.Pdfium.Interop;

namespace PdfAcrobat.Pdfium;

[Flags]
public enum PdfRenderFlags
{
    None = 0,
    /// <summary>Draw annotation appearances (FPDF_ANNOT).</summary>
    Annotations = 0x01,
    LcdText = 0x02,
    NoNativeText = 0x04,
    Grayscale = 0x08,
    ReverseByteOrder = 0x10,
    ConvertFillToStroke = 0x20,
    LimitedImageCache = 0x200,
    ForceHalftone = 0x400,
    Printing = 0x800,
    NoSmoothText = 0x1000,
    NoSmoothImage = 0x2000,
    NoSmoothPath = 0x4000,
}

/// <summary>A caller-owned 32bpp BGR(A) pixel buffer that PDFium renders into.</summary>
public readonly record struct PdfRenderTarget(nint Buffer, int Width, int Height, int Stride, bool HasAlpha = false);

/// <summary>Colors used to repaint text and paths (e.g. for high-contrast / dark reading mode). ARGB.</summary>
public readonly record struct PdfColorScheme(uint PathFill, uint PathStroke, uint TextFill, uint TextStroke);

public enum PdfRenderResult
{
    Done,
    Cancelled,
    Failed,
}

/// <summary>
/// A lease on a loaded page. Dispose to release it back to the document's page cache.
/// The text page returned by <see cref="GetTextPage"/> is valid while the lease is alive.
/// </summary>
public sealed unsafe class PdfPage : IDisposable
{
    private const int FpdfRenderToBeContinued = 1;
    private const int FpdfRenderDone = 2;

    private PageHandle? _handle;

    internal PdfPage(PdfDocument document, int index, PageHandle handle)
    {
        Document = document;
        Index = index;
        _handle = handle;
    }

    public PdfDocument Document { get; }

    public int Index { get; }

    private PageHandle Handle => _handle ?? throw new ObjectDisposedException(nameof(PdfPage));

    internal nint NativePage => Handle.Page;

    /// <summary>Page box and intrinsic rotation, used to map between user space and device space.</summary>
    public PdfPageGeometry Geometry
    {
        get
        {
            using var _ = PdfiumLibrary.Acquire();
            var handle = Handle;
            if (handle.Geometry is null)
            {
                FS_RECTF box;
                var bbox = PdfiumNative.FPDF_GetPageBoundingBox(handle.Page, &box) != 0
                    ? PdfRect.FromPoints(box.left, box.bottom, box.right, box.top)
                    : new PdfRect(0, 0, PdfiumNative.FPDF_GetPageWidthF(handle.Page), PdfiumNative.FPDF_GetPageHeightF(handle.Page));
                var rotation = PdfiumNative.FPDFPage_GetRotation(handle.Page);
                handle.Geometry = new PdfPageGeometry(bbox, ((rotation % 4) + 4) % 4);
            }

            return handle.Geometry;
        }
    }

    public double Width => Geometry.Width;

    public double Height => Geometry.Height;

    /// <summary>
    /// Renders the page into <paramref name="target"/>. The page occupies the device rectangle
    /// (startX, startY, sizeX, sizeY), which may extend beyond the target (tile rendering).
    /// Rendering is progressive: the global lock is released between steps and the call can be cancelled.
    /// </summary>
    public PdfRenderResult Render(
        PdfRenderTarget target,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int extraRotation,
        PdfRenderFlags flags,
        uint backgroundArgb = 0xFFFFFFFF,
        PdfColorScheme? colorScheme = null,
        CancellationToken cancellationToken = default)
    {
        var handle = Handle;
        handle.RenderGate.Wait(cancellationToken);
        var state = new PauseState(cancellationToken);
        var gch = GCHandle.Alloc(state);
        nint bitmap = 0;
        try
        {
            IFSDK_PAUSE pause;
            pause.version = 1;
            pause.NeedToPauseNow = &NeedToPauseNow;
            pause.user = (void*)GCHandle.ToIntPtr(gch);

            int status;
            using (PdfiumLibrary.Acquire())
            {
                var page = Handle.Page;
                bitmap = PdfiumNative.FPDFBitmap_CreateEx(target.Width, target.Height, target.HasAlpha ? 4 : 3, (void*)target.Buffer, target.Stride);
                if (bitmap == 0)
                {
                    return PdfRenderResult.Failed;
                }

                PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, target.Width, target.Height, backgroundArgb);
                state.Restart();
                if (colorScheme is { } scheme)
                {
                    FPDF_COLORSCHEME cs;
                    cs.path_fill_color = scheme.PathFill;
                    cs.path_stroke_color = scheme.PathStroke;
                    cs.text_fill_color = scheme.TextFill;
                    cs.text_stroke_color = scheme.TextStroke;
                    status = PdfiumNative.FPDF_RenderPageBitmapWithColorScheme_Start(bitmap, page, startX, startY, sizeX, sizeY, extraRotation & 3, (int)flags, &cs, &pause);
                }
                else
                {
                    status = PdfiumNative.FPDF_RenderPageBitmap_Start(bitmap, page, startX, startY, sizeX, sizeY, extraRotation & 3, (int)flags, &pause);
                }
            }

            while (status == FpdfRenderToBeContinued && !cancellationToken.IsCancellationRequested)
            {
                PdfiumLibrary.YieldToWaiters();
                using (PdfiumLibrary.Acquire())
                {
                    state.Restart();
                    status = PdfiumNative.FPDF_RenderPage_Continue(Handle.Page, &pause);
                }
            }

            using (PdfiumLibrary.Acquire())
            {
                PdfiumNative.FPDF_RenderPage_Close(Handle.Page);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return PdfRenderResult.Cancelled;
            }

            return status == FpdfRenderDone ? PdfRenderResult.Done : PdfRenderResult.Failed;
        }
        finally
        {
            if (bitmap != 0)
            {
                using (PdfiumLibrary.Acquire())
                {
                    PdfiumNative.FPDFBitmap_Destroy(bitmap);
                }
            }

            gch.Free();
            handle.RenderGate.Release();
        }
    }

    /// <summary>
    /// Renders the page directly into a Windows GDI device context (e.g. a printer DC), producing
    /// vector output where possible. Coordinates are device pixels of that DC.
    /// </summary>
    public bool RenderToDeviceContext(nint hdc, int startX, int startY, int sizeX, int sizeY, int extraRotation, PdfRenderFlags flags)
    {
        var handle = Handle;
        handle.RenderGate.Wait();
        try
        {
            using var _ = PdfiumLibrary.Acquire();
            return PdfiumNative.FPDF_RenderPage(hdc, handle.Page, startX, startY, sizeX, sizeY, extraRotation & 3, (int)flags) != 0;
        }
        finally
        {
            handle.RenderGate.Release();
        }
    }

    /// <summary>Renders a region using an explicit matrix (user space → device), non-progressively.</summary>
    public void RenderWithMatrix(PdfRenderTarget target, PdfMatrix userToDevice, PdfRenderFlags flags, uint backgroundArgb = 0xFFFFFFFF)
    {
        // FPDF_RenderPageBitmapWithMatrix applies its matrix on top of the page's own display matrix
        // at scale 1 (rotated page space with y down, integer page size), so convert from user space first.
        var geometry = Geometry;
        var baseMatrix = geometry.GetDisplayMatrix(0, 0, (int)geometry.Width, (int)geometry.Height, 0);
        var extra = baseMatrix.Invert() * userToDevice;

        var handle = Handle;
        handle.RenderGate.Wait();
        try
        {
            using var _ = PdfiumLibrary.Acquire();
            var bitmap = PdfiumNative.FPDFBitmap_CreateEx(target.Width, target.Height, target.HasAlpha ? 4 : 3, (void*)target.Buffer, target.Stride);
            if (bitmap == 0)
            {
                return;
            }

            try
            {
                PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, target.Width, target.Height, backgroundArgb);
                FS_MATRIX m;
                m.a = (float)extra.A;
                m.b = (float)extra.B;
                m.c = (float)extra.C;
                m.d = (float)extra.D;
                m.e = (float)extra.E;
                m.f = (float)extra.F;
                FS_RECTF clip;
                clip.left = 0;
                clip.top = 0;
                clip.right = target.Width;
                clip.bottom = target.Height;
                PdfiumNative.FPDF_RenderPageBitmapWithMatrix(bitmap, handle.Page, &m, &clip, (int)flags);
            }
            finally
            {
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }
        }
        finally
        {
            handle.RenderGate.Release();
        }
    }

    /// <summary>Returns the text layer of the page (loaded on first use, cached with the page).</summary>
    public PdfTextPage GetTextPage()
    {
        using var _ = PdfiumLibrary.Acquire();
        var handle = Handle;
        if (handle.TextPage == 0)
        {
            handle.TextPage = PdfiumNative.FPDFText_LoadPage(handle.Page);
            if (handle.TextPage == 0)
            {
                throw new PdfiumException(PdfiumErrorCode.Page, "テキスト情報を読み込めませんでした。");
            }
        }

        return new PdfTextPage(this, handle);
    }

    /// <summary>Link annotations of the page, optionally followed by URLs detected in the text.</summary>
    public IReadOnlyList<PdfLink> GetLinks(bool includeDetectedWebLinks = true)
    {
        using var _ = PdfiumLibrary.Acquire();
        var page = Handle.Page;
        var result = new List<PdfLink>();
        var position = 0;
        nint link;
        while (PdfiumNative.FPDFLink_Enumerate(page, &position, &link) != 0)
        {
            FS_RECTF r;
            if (PdfiumNative.FPDFLink_GetAnnotRect(link, &r) == 0)
            {
                continue;
            }

            var bounds = PdfRect.FromPoints(r.left, r.bottom, r.right, r.top);
            PdfDestination? destination = null;
            string? uri = null;
            var dest = PdfiumNative.FPDFLink_GetDest(Document.Handle, link);
            if (dest != 0)
            {
                destination = Document.ReadDestination(dest);
            }
            else
            {
                (destination, uri) = Document.ReadAction(PdfiumNative.FPDFLink_GetAction(link));
            }

            if (destination is not null || uri is not null)
            {
                result.Add(new PdfLink(bounds, destination, uri, IsDetectedWebLink: false));
            }
        }

        if (includeDetectedWebLinks)
        {
            foreach (var (url, rects) in GetTextPage().GetWebLinks())
            {
                foreach (var rect in rects)
                {
                    if (!result.Any(l => l.Bounds.IntersectsWith(rect)))
                    {
                        result.Add(new PdfLink(rect, null, url, IsDetectedWebLink: true));
                    }
                }
            }
        }

        return result;
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, null);
        if (handle is not null)
        {
            Document.Release(handle);
        }
    }

    private sealed class PauseState(CancellationToken cancellationToken)
    {
        private static readonly long SliceTicks = Stopwatch.Frequency / 40; // 25 ms per step
        private long _deadline;

        public void Restart() => _deadline = Stopwatch.GetTimestamp() + SliceTicks;

        public bool ShouldPause =>
            cancellationToken.IsCancellationRequested
            || PdfiumLibrary.HasWaiters
            || Stopwatch.GetTimestamp() >= _deadline;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NeedToPauseNow(IFSDK_PAUSE* self)
    {
        try
        {
            var state = (PauseState)GCHandle.FromIntPtr((nint)self->user).Target!;
            return state.ShouldPause ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }
}
