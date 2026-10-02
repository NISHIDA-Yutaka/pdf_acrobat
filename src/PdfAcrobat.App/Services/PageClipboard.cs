using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace PdfAcrobat.App.Services;

/// <summary>
/// Copied pages, kept as a small PDF so they can be pasted into any open document (even after the
/// original tab is closed). A token on the Windows clipboard marks them as the latest copy: once
/// another application copies something, pasting uses that content instead.
/// </summary>
public static class PageClipboard
{
    private const string Format = "PdfAcrobat.Pages";

    private static (string Token, byte[] Pdf, int PageCount)? _content;

    public static void Set(byte[] pdf, int pageCount)
    {
        var token = Guid.NewGuid().ToString("N");
        _content = (token, pdf, pageCount);
        try
        {
            Clipboard.SetData(Format, new MemoryStream(Encoding.ASCII.GetBytes(token)));
        }
        catch (COMException)
        {
            // Clipboard busy: pasting inside this app still works through the token check below failing,
            // so keep the pages but without the marker.
            _content = (string.Empty, pdf, pageCount);
        }
    }

    /// <summary>The copied pages when they are still the latest clipboard content.</summary>
    public static (byte[] Pdf, int PageCount)? Get()
    {
        if (_content is not { } content)
        {
            return null;
        }

        try
        {
            if (content.Token.Length == 0)
            {
                return (content.Pdf, content.PageCount);
            }

            if (Clipboard.ContainsData(Format) && Clipboard.GetData(Format) is MemoryStream stream
                && Encoding.ASCII.GetString(stream.ToArray()) == content.Token)
            {
                return (content.Pdf, content.PageCount);
            }
        }
        catch (COMException)
        {
            return (content.Pdf, content.PageCount);
        }

        return null;
    }
}
