using System.Runtime.InteropServices;

namespace PdfAcrobat.Pdfium.Interop;

// Layouts mirror the C structs in the PDFium public headers (fpdfview.h etc.).
// Note: on Windows `unsigned long` is 32-bit, so FPDF_DWORD maps to uint.

[StructLayout(LayoutKind.Sequential)]
internal struct FS_RECTF
{
    public float left;
    public float top;
    public float right;
    public float bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FS_SIZEF
{
    public float width;
    public float height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FS_POINTF
{
    public float x;
    public float y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FS_MATRIX
{
    public float a;
    public float b;
    public float c;
    public float d;
    public float e;
    public float f;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FS_QUADPOINTSF
{
    public float x1;
    public float y1;
    public float x2;
    public float y2;
    public float x3;
    public float y3;
    public float x4;
    public float y4;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FPDF_COLORSCHEME
{
    public uint path_fill_color;
    public uint path_stroke_color;
    public uint text_fill_color;
    public uint text_stroke_color;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct IFSDK_PAUSE
{
    public int version;
    public delegate* unmanaged[Cdecl]<IFSDK_PAUSE*, int> NeedToPauseNow;
    public void* user;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FPDF_FILEWRITE
{
    public int version;
    public delegate* unmanaged[Cdecl]<FPDF_FILEWRITE*, void*, uint, int> WriteBlock;
}
