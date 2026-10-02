using System.IO;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.Core.Documents;

/// <summary>
/// A PDF file loaded into PDFium that pages of a <see cref="DocumentSession"/> refer to.
/// Sources are never modified after loading; edits live in <see cref="DocumentState"/>.
/// </summary>
public sealed class PdfSource : IDisposable
{
    private PdfSource(string name, string? filePath, byte[] bytes, string? password, PdfDocument document)
    {
        Id = Guid.NewGuid();
        Name = name;
        FilePath = filePath;
        Bytes = bytes;
        Password = password;
        Document = document;
        PageSizes = document.GetAllPageSizes();
        Info = document.GetInfo();
    }

    public Guid Id { get; }

    /// <summary>File name shown to the user.</summary>
    public string Name { get; }

    public string? FilePath { get; }

    /// <summary>Original file contents (kept for saving and re-opening).</summary>
    public byte[] Bytes { get; }

    public string? Password { get; }

    public PdfDocument Document { get; }

    public int PageCount => PageSizes.Length;

    /// <summary>Displayed page sizes in points (after the page's own /Rotate).</summary>
    public PdfSize[] PageSizes { get; }

    public PdfDocumentInfo Info { get; }

    /// <exception cref="PdfPasswordException">A password is required or wrong.</exception>
    public static PdfSource Load(byte[] bytes, string name, string? filePath = null, string? password = null)
    {
        var document = PdfDocument.Load(bytes, password);
        try
        {
            return new PdfSource(name, filePath, bytes, password, document);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public static PdfSource LoadFile(string path, string? password = null) =>
        Load(File.ReadAllBytes(path), Path.GetFileName(path), path, password);

    public void Dispose() => Document.Dispose();
}
