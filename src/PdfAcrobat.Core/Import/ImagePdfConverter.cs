using System.IO;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using SharpPdfDocument = PdfSharp.Pdf.PdfDocument;

namespace PdfAcrobat.Core.Import;

/// <summary>
/// Converts image files to PDF pages (one page per image frame, so multi-page TIFFs become several pages).
/// JPEG files are embedded as-is; other formats are decoded with Windows Imaging Component (WIC), which
/// also covers HEIC / WebP when the corresponding Windows codecs are installed.
/// </summary>
public static class ImagePdfConverter
{
    private const double MaxPageSide = 14400; // PDF implementation limit (200 inch)

    public static IReadOnlyList<string> Extensions { get; } =
        [".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".heif", ".webp"];

    public static string FileFilter =>
        "画像ファイル|" + string.Join(';', Extensions.Select(e => "*" + e));

    public static bool IsImage(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static byte[] ConvertFiles(IEnumerable<string> paths)
    {
        var document = new SharpPdfDocument();
        foreach (var path in paths)
        {
            AddFile(document, path);
        }

        return Save(document);
    }

    public static byte[] ConvertBitmaps(IEnumerable<BitmapSource> bitmaps)
    {
        var document = new SharpPdfDocument();
        foreach (var bitmap in bitmaps)
        {
            AddBitmap(document, bitmap);
        }

        return Save(document);
    }

    private static byte[] Save(SharpPdfDocument document)
    {
        if (document.PageCount == 0)
        {
            throw new InvalidOperationException("変換できる画像がありませんでした。");
        }

        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    private static void AddFile(SharpPdfDocument document, string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".jpg" or ".jpeg" or ".jfif")
        {
            // Keep the original JPEG data (no re-compression).
            using var image = XImage.FromFile(path);
            AddPage(document, image, image.PixelWidth, image.PixelHeight, image.HorizontalResolution, image.VerticalResolution);
            return;
        }

        BitmapDecoder decoder;
        using (var stream = File.OpenRead(path))
        {
            decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }

        foreach (var frame in decoder.Frames)
        {
            AddBitmap(document, frame);
        }
    }

    private static void AddBitmap(SharpPdfDocument document, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        png.Position = 0;
        using var image = XImage.FromStream(png);
        AddPage(document, image, bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, bitmap.DpiY);
    }

    private static void AddPage(SharpPdfDocument document, XImage image, int pixelWidth, int pixelHeight, double dpiX, double dpiY)
    {
        // Physical size from the image resolution (screen images without DPI information count as 96 dpi).
        dpiX = dpiX is > 1 and < 10000 ? dpiX : 96;
        dpiY = dpiY is > 1 and < 10000 ? dpiY : 96;
        var width = pixelWidth / dpiX * 72;
        var height = pixelHeight / dpiY * 72;
        var scale = Math.Min(1, MaxPageSide / Math.Max(width, height));
        width *= scale;
        height *= scale;

        var page = document.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(width);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(height);
        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawImage(image, 0, 0, width, height);
    }
}
