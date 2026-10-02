using System.Runtime.InteropServices;

namespace PdfAcrobat.Pdfium.Interop;

/// <summary>
/// Raw P/Invoke declarations for pdfium.dll (bblanchon/pdfium-binaries).
/// Signatures follow the headers shipped in the NuGet package (build/native/include/pdfium).
/// All calls must be serialized through <see cref="PdfiumLibrary.Sync"/>.
/// </summary>
internal static unsafe partial class PdfiumNative
{
    private const string Lib = "pdfium";

    // ---- fpdfview.h -------------------------------------------------------

    [LibraryImport(Lib)] public static partial void FPDF_InitLibrary();
    [LibraryImport(Lib)] public static partial void FPDF_DestroyLibrary();

    [LibraryImport(Lib)]
    public static partial nint FPDF_LoadMemDocument64(void* dataBuf, nuint size, byte* password);

    [LibraryImport(Lib)] public static partial uint FPDF_GetLastError();
    [LibraryImport(Lib)] public static partial int FPDF_GetFileVersion(nint doc, int* fileVersion);
    [LibraryImport(Lib)] public static partial uint FPDF_GetDocPermissions(nint document);
    [LibraryImport(Lib)] public static partial int FPDF_GetSecurityHandlerRevision(nint document);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(nint document);
    [LibraryImport(Lib)] public static partial nint FPDF_LoadPage(nint document, int pageIndex);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageWidthF(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageHeightF(nint page);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageBoundingBox(nint page, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageSizeByIndexF(nint document, int pageIndex, FS_SIZEF* size);

    [LibraryImport(Lib)]
    public static partial int FPDF_RenderPage(nint dc, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Lib)]
    public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Lib)]
    public static partial void FPDF_RenderPageBitmapWithMatrix(nint bitmap, nint page, FS_MATRIX* matrix, FS_RECTF* clipping, int flags);

    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Lib)] public static partial nint FPDFBitmap_Create(int width, int height, int alpha);
    [LibraryImport(Lib)] public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Lib)] public static partial void* FPDFBitmap_GetBuffer(nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_GetWidth(nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_GetHeight(nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_GetStride(nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_GetFormat(nint bitmap);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Lib)] public static partial nint FPDF_GetNamedDestByName(nint document, byte* name);

    // ---- fpdf_progressive.h ----------------------------------------------

    [LibraryImport(Lib)]
    public static partial int FPDF_RenderPageBitmap_Start(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags, IFSDK_PAUSE* pause);

    [LibraryImport(Lib)]
    public static partial int FPDF_RenderPageBitmapWithColorScheme_Start(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags, FPDF_COLORSCHEME* colorScheme, IFSDK_PAUSE* pause);

    [LibraryImport(Lib)] public static partial int FPDF_RenderPage_Continue(nint page, IFSDK_PAUSE* pause);
    [LibraryImport(Lib)] public static partial void FPDF_RenderPage_Close(nint page);

    // ---- fpdf_text.h ------------------------------------------------------

    [LibraryImport(Lib)] public static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Lib)] public static partial void FPDFText_ClosePage(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_CountChars(nint textPage);
    [LibraryImport(Lib)] public static partial uint FPDFText_GetUnicode(nint textPage, int index);
    [LibraryImport(Lib)] public static partial int FPDFText_IsGenerated(nint textPage, int index);
    [LibraryImport(Lib)] public static partial double FPDFText_GetFontSize(nint textPage, int index);
    [LibraryImport(Lib)] public static partial float FPDFText_GetCharAngle(nint textPage, int index);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharBox(nint textPage, int index, double* left, double* right, double* bottom, double* top);
    [LibraryImport(Lib)] public static partial int FPDFText_GetLooseCharBox(nint textPage, int index, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharIndexAtPos(nint textPage, double x, double y, double xTolerance, double yTolerance);
    [LibraryImport(Lib)] public static partial int FPDFText_GetText(nint textPage, int startIndex, int count, ushort* result);
    [LibraryImport(Lib)] public static partial int FPDFText_CountRects(nint textPage, int startIndex, int count);
    [LibraryImport(Lib)] public static partial int FPDFText_GetRect(nint textPage, int rectIndex, double* left, double* top, double* right, double* bottom);
    [LibraryImport(Lib)] public static partial nint FPDFText_FindStart(nint textPage, ushort* findWhat, uint flags, int startIndex);
    [LibraryImport(Lib)] public static partial int FPDFText_FindNext(nint handle);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchResultIndex(nint handle);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchCount(nint handle);
    [LibraryImport(Lib)] public static partial void FPDFText_FindClose(nint handle);
    [LibraryImport(Lib)] public static partial nint FPDFLink_LoadWebLinks(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFLink_CountWebLinks(nint linkPage);
    [LibraryImport(Lib)] public static partial int FPDFLink_GetURL(nint linkPage, int linkIndex, ushort* buffer, int bufLen);
    [LibraryImport(Lib)] public static partial int FPDFLink_CountRects(nint linkPage, int linkIndex);
    [LibraryImport(Lib)] public static partial int FPDFLink_GetRect(nint linkPage, int linkIndex, int rectIndex, double* left, double* top, double* right, double* bottom);
    [LibraryImport(Lib)] public static partial void FPDFLink_CloseWebLinks(nint linkPage);

    // ---- fpdf_doc.h -------------------------------------------------------

    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);
    [LibraryImport(Lib)] public static partial uint FPDFBookmark_GetTitle(nint bookmark, void* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial int FPDFBookmark_GetCount(nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetDest(nint document, nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetAction(nint bookmark);
    [LibraryImport(Lib)] public static partial uint FPDFAction_GetType(nint action);
    [LibraryImport(Lib)] public static partial nint FPDFAction_GetDest(nint document, nint action);
    [LibraryImport(Lib)] public static partial uint FPDFAction_GetURIPath(nint document, nint action, void* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial int FPDFDest_GetDestPageIndex(nint document, nint dest);
    [LibraryImport(Lib)] public static partial uint FPDFDest_GetView(nint dest, uint* numParams, float* parameters);
    [LibraryImport(Lib)] public static partial int FPDFDest_GetLocationInPage(nint dest, int* hasX, int* hasY, int* hasZoom, float* x, float* y, float* zoom);
    [LibraryImport(Lib)] public static partial nint FPDFLink_GetLinkAtPoint(nint page, double x, double y);
    [LibraryImport(Lib)] public static partial nint FPDFLink_GetDest(nint document, nint link);
    [LibraryImport(Lib)] public static partial nint FPDFLink_GetAction(nint link);
    [LibraryImport(Lib)] public static partial int FPDFLink_Enumerate(nint page, int* startPos, nint* linkAnnot);
    [LibraryImport(Lib)] public static partial int FPDFLink_GetAnnotRect(nint linkAnnot, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial uint FPDF_GetMetaText(nint document, byte* tag, void* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial uint FPDF_GetPageLabel(nint document, int pageIndex, void* buffer, uint bufLen);

    // ---- fpdf_catalog.h ---------------------------------------------------

    [LibraryImport(Lib)] public static partial int FPDFCatalog_IsTagged(nint document);
    [LibraryImport(Lib)] public static partial uint FPDFCatalog_GetLanguage(nint document, ushort* buffer, uint bufLen);

    // ---- fpdf_ext.h -------------------------------------------------------

    [LibraryImport(Lib)] public static partial int FPDFDoc_GetPageMode(nint document);

    // ---- fpdf_edit.h ------------------------------------------------------

    [LibraryImport(Lib)] public static partial nint FPDF_CreateNewDocument();
    [LibraryImport(Lib)] public static partial nint FPDFPage_New(nint document, int pageIndex, double width, double height);
    [LibraryImport(Lib)] public static partial void FPDFPage_Delete(nint document, int pageIndex);
    [LibraryImport(Lib)] public static partial int FPDF_MovePages(nint document, int* pageIndices, uint pageIndicesLen, int destPageIndex);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetRotation(nint page);
    [LibraryImport(Lib)] public static partial void FPDFPage_SetRotation(nint page, int rotate);
    [LibraryImport(Lib)] public static partial int FPDFPage_GenerateContent(nint page);

    // ---- fpdf_transformpage.h --------------------------------------------

    [LibraryImport(Lib)] public static partial int FPDFPage_GetMediaBox(nint page, float* left, float* bottom, float* right, float* top);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetCropBox(nint page, float* left, float* bottom, float* right, float* top);

    // ---- fpdf_ppo.h -------------------------------------------------------

    [LibraryImport(Lib)] public static partial int FPDF_ImportPagesByIndex(nint destDoc, nint srcDoc, int* pageIndices, uint length, int index);
    [LibraryImport(Lib)] public static partial int FPDF_CopyViewerPreferences(nint destDoc, nint srcDoc);

    // ---- fpdf_save.h ------------------------------------------------------

    [LibraryImport(Lib)] public static partial int FPDF_SaveAsCopy(nint document, FPDF_FILEWRITE* fileWrite, uint flags);
    [LibraryImport(Lib)] public static partial int FPDF_SaveWithVersion(nint document, FPDF_FILEWRITE* fileWrite, uint flags, int fileVersion);
}
