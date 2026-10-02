using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.ViewModels;

/// <summary>A named paper size (portrait, millimetres) for new blank pages.</summary>
public sealed record PaperSize(string Name, double WidthMm, double HeightMm)
{
    private const double PointsPerMm = 72 / 25.4;

    public static IReadOnlyList<PaperSize> Common { get; } =
    [
        new("A4 (210 × 297 mm)", 210, 297),
        new("A3 (297 × 420 mm)", 297, 420),
        new("A5 (148 × 210 mm)", 148, 210),
        new("B4 (JIS, 257 × 364 mm)", 257, 364),
        new("B5 (JIS, 182 × 257 mm)", 182, 257),
        new("はがき (100 × 148 mm)", 100, 148),
        new("レター (8.5 × 11 インチ)", 215.9, 279.4),
        new("リーガル (8.5 × 14 インチ)", 215.9, 355.6),
    ];

    public static PaperSize A4 => Common[0];

    public PdfSize ToPoints(bool landscape) => landscape
        ? new PdfSize(HeightMm * PointsPerMm, WidthMm * PointsPerMm)
        : new PdfSize(WidthMm * PointsPerMm, HeightMm * PointsPerMm);

    public override string ToString() => Name;
}
