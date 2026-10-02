using System.Globalization;
using System.Text.RegularExpressions;

namespace PdfAcrobat.Pdfium;

/// <summary>Fit type of an explicit destination (FPDFDest_GetView).</summary>
public enum PdfDestinationView
{
    Unknown = 0,
    Xyz = 1,
    Fit = 2,
    FitH = 3,
    FitV = 4,
    FitR = 5,
    FitB = 6,
    FitBH = 7,
    FitBV = 8,
}

/// <summary>A destination inside the document. X/Y are in PDF user space of the target page.</summary>
public sealed record PdfDestination(int PageIndex, double? X, double? Y, double? Zoom, PdfDestinationView View);

public sealed record PdfBookmark(
    string Title,
    PdfDestination? Destination,
    string? Uri,
    IReadOnlyList<PdfBookmark> Children,
    bool IsOpen);

/// <summary>A clickable area on a page: either an internal destination or an external URI.</summary>
public sealed record PdfLink(PdfRect Bounds, PdfDestination? Destination, string? Uri, bool IsDetectedWebLink);

/// <summary>Document permission bits (PDF 32000-1, table 22).</summary>
[Flags]
public enum PdfPermissions : uint
{
    None = 0,
    Print = 1 << 2,
    Modify = 1 << 3,
    CopyContent = 1 << 4,
    Annotate = 1 << 5,
    FillForms = 1 << 8,
    ExtractForAccessibility = 1 << 9,
    Assemble = 1 << 10,
    PrintHighQuality = 1 << 11,
}

public sealed class PdfDocumentInfo
{
    public string Title { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string Keywords { get; init; } = string.Empty;

    public string Creator { get; init; } = string.Empty;

    public string Producer { get; init; } = string.Empty;

    public DateTimeOffset? CreationDate { get; init; }

    public DateTimeOffset? ModificationDate { get; init; }

    public int? FileVersion { get; init; }

    public int PageCount { get; init; }

    public bool IsTagged { get; init; }

    public string? Language { get; init; }

    public int SecurityHandlerRevision { get; init; }

    public PdfPermissions Permissions { get; init; }

    public bool IsEncrypted => SecurityHandlerRevision >= 0;

    public string VersionText => FileVersion is { } v ? $"{v / 10}.{v % 10}" : "不明";
}

/// <summary>Parser for PDF date strings such as <c>D:20240131235959+09'00'</c>.</summary>
public static partial class PdfDate
{
    [GeneratedRegex(@"^(?:D:)?(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?\s*(?:(Z)|([+\-])(\d{2})'?(\d{2})?'?)?", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static DateTimeOffset? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = Pattern().Match(text.Trim());
        if (!m.Success)
        {
            return null;
        }

        static int Part(Group g, int fallback) =>
            g.Success ? int.Parse(g.Value, CultureInfo.InvariantCulture) : fallback;

        try
        {
            var offset = TimeSpan.Zero;
            if (m.Groups[8].Success)
            {
                var sign = m.Groups[8].Value == "-" ? -1 : 1;
                offset = sign * new TimeSpan(Part(m.Groups[9], 0), Part(m.Groups[10], 0), 0);
            }

            return new DateTimeOffset(
                Part(m.Groups[1], 1),
                Math.Clamp(Part(m.Groups[2], 1), 1, 12),
                Math.Clamp(Part(m.Groups[3], 1), 1, 31),
                Math.Clamp(Part(m.Groups[4], 0), 0, 23),
                Math.Clamp(Part(m.Groups[5], 0), 0, 59),
                Math.Clamp(Part(m.Groups[6], 0), 0, 59),
                offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Formats a date as a PDF date string.</summary>
    public static string Format(DateTimeOffset value)
    {
        var offset = value.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return $"D:{value:yyyyMMddHHmmss}{sign}{offset.Hours:00}'{offset.Minutes:00}'";
    }
}
