using System.Drawing.Printing;
using System.Runtime.InteropServices;
using PdfAcrobat.Core.Documents;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.Services;

public enum PrintScaling
{
    /// <summary>Scale every page to the printable area.</summary>
    Fit,

    /// <summary>Print at 100 %.</summary>
    ActualSize,

    /// <summary>Only shrink pages that do not fit.</summary>
    ShrinkOversized,
}

public enum PrintOrientation
{
    Auto,
    Portrait,
    Landscape,
}

public sealed record PrintJobOptions(
    string PrinterName,
    short Copies,
    bool Collate,
    IReadOnlyList<int> PageIndices,
    PrintScaling Scaling,
    PrintOrientation Orientation,
    bool PrintAnnotations,
    bool Grayscale,
    string? OutputFile = null);

/// <summary>
/// Prints through the Windows GDI print path. PDFium draws each page straight into the printer's
/// device context, so text and vector graphics stay sharp at any printer resolution.
/// </summary>
public static partial class PrintService
{
    public static IReadOnlyList<string> GetPrinters()
    {
        try
        {
            return PrinterSettings.InstalledPrinters.Cast<string>().ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public static string? GetDefaultPrinter()
    {
        try
        {
            var settings = new PrinterSettings();
            return settings.IsDefaultPrinter ? settings.PrinterName : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Task PrintAsync(DocumentSession session, PrintJobOptions options, IProgress<(int Done, int Total)>? progress, CancellationToken cancellationToken)
    {
        var pages = options.PageIndices.Where(i => i >= 0 && i < session.Pages.Count).Select(i => session.Pages[i]).ToList();
        return Task.Run(() =>
        {
            using var document = new PrintDocument();
            document.PrintController = new StandardPrintController();
            document.DocumentName = session.DisplayName;
            document.PrinterSettings.PrinterName = options.PrinterName;
            if (!document.PrinterSettings.IsValid)
            {
                throw new InvalidOperationException($"プリンター「{options.PrinterName}」が見つかりません。");
            }

            document.PrinterSettings.Copies = Math.Max((short)1, options.Copies);
            document.PrinterSettings.Collate = options.Collate;
            if (options.OutputFile is { } file)
            {
                document.PrinterSettings.PrintToFile = true;
                document.PrinterSettings.PrintFileName = file;
            }

            var index = 0;
            document.QueryPageSettings += (_, e) =>
            {
                if (index >= pages.Count)
                {
                    return;
                }

                var size = session.GetPageSize(pages[index]);
                e.PageSettings.Landscape = options.Orientation switch
                {
                    PrintOrientation.Landscape => true,
                    PrintOrientation.Portrait => false,
                    _ => size.Width > size.Height,
                };
                e.PageSettings.Color = !options.Grayscale;
            };

            document.PrintPage += (_, e) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    e.Cancel = true;
                    return;
                }

                var page = pages[index];
                if (!page.IsBlank)
                {
                    var hdc = e.Graphics!.GetHdc();
                    try
                    {
                        PrintPage(session, page, hdc, options);
                    }
                    finally
                    {
                        e.Graphics.ReleaseHdc(hdc);
                    }
                }

                index++;
                progress?.Report((index, pages.Count));
                e.HasMorePages = index < pages.Count;
            };

            document.Print();
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken);
    }

    private static void PrintPage(DocumentSession session, PageRef page, nint hdc, PrintJobOptions options)
    {
        var caps = DeviceCaps.Read(hdc);
        var size = session.GetPageSize(page);
        var pageWidth = size.Width / 72.0 * caps.DpiX;
        var pageHeight = size.Height / 72.0 * caps.DpiY;
        var fit = Math.Min(caps.PrintableWidth / pageWidth, caps.PrintableHeight / pageHeight);
        var scale = options.Scaling switch
        {
            PrintScaling.Fit => fit,
            PrintScaling.ShrinkOversized => Math.Min(1.0, fit),
            _ => 1.0,
        };

        var width = (int)Math.Round(pageWidth * scale);
        var height = (int)Math.Round(pageHeight * scale);

        // Centre on the physical sheet; device coordinates start at the printable area's corner.
        var x = (int)Math.Round((caps.PhysicalWidth - width) / 2.0) - caps.OffsetX;
        var y = (int)Math.Round((caps.PhysicalHeight - height) / 2.0) - caps.OffsetY;

        var flags = PdfRenderFlags.Printing;
        if (options.PrintAnnotations)
        {
            flags |= PdfRenderFlags.Annotations;
        }

        if (options.Grayscale)
        {
            flags |= PdfRenderFlags.Grayscale;
        }

        var source = session.GetSource(page.SourceId!.Value);
        using var pdfPage = source.Document.OpenPage(page.SourceIndex);
        pdfPage.RenderToDeviceContext(hdc, x, y, width, height, page.Rotation, flags);
    }

    private readonly record struct DeviceCaps(int DpiX, int DpiY, int PrintableWidth, int PrintableHeight, int PhysicalWidth, int PhysicalHeight, int OffsetX, int OffsetY)
    {
        private const int HorzRes = 8;
        private const int VertRes = 10;
        private const int LogPixelsX = 88;
        private const int LogPixelsY = 90;
        private const int PhysicalWidthIndex = 110;
        private const int PhysicalHeightIndex = 111;
        private const int PhysicalOffsetX = 112;
        private const int PhysicalOffsetY = 113;

        public static DeviceCaps Read(nint hdc) => new(
            GetDeviceCaps(hdc, LogPixelsX),
            GetDeviceCaps(hdc, LogPixelsY),
            GetDeviceCaps(hdc, HorzRes),
            GetDeviceCaps(hdc, VertRes),
            GetDeviceCaps(hdc, PhysicalWidthIndex),
            GetDeviceCaps(hdc, PhysicalHeightIndex),
            GetDeviceCaps(hdc, PhysicalOffsetX),
            GetDeviceCaps(hdc, PhysicalOffsetY));
    }

    [LibraryImport("gdi32.dll")]
    private static partial int GetDeviceCaps(nint hdc, int index);
}
