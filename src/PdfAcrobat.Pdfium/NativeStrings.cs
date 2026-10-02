using System.Runtime.InteropServices;

namespace PdfAcrobat.Pdfium;

internal static unsafe class NativeStrings
{
    /// <summary>A PDFium "two call" getter: returns the required byte length, fills the buffer when large enough.</summary>
    public delegate uint Utf16Reader(void* buffer, uint length);

    /// <summary>Reads a UTF-16LE string from a PDFium getter, dropping the terminating NUL.</summary>
    public static string ReadUtf16(Utf16Reader reader)
    {
        var length = reader(null, 0);
        if (length <= 2)
        {
            return string.Empty;
        }

        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            length = Math.Min(reader(p, length), (uint)buffer.Length);
        }

        var chars = MemoryMarshal.Cast<byte, char>(buffer.AsSpan(0, (int)(length / 2) * 2));
        var end = chars.IndexOf('\0');
        if (end >= 0)
        {
            chars = chars[..end];
        }

        return new string(chars);
    }
}
