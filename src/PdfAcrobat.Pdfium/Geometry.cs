namespace PdfAcrobat.Pdfium;

/// <summary>A point in PDF user space (points, y axis pointing up) or device space.</summary>
public readonly record struct PdfPoint(double X, double Y);

public readonly record struct PdfSize(double Width, double Height);

/// <summary>Axis-aligned rectangle in PDF user space (y axis pointing up).</summary>
public readonly record struct PdfRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PdfPoint Center => new((Left + Right) / 2, (Bottom + Top) / 2);

    public static PdfRect FromPoints(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    public PdfRect Normalize() => FromPoints(Left, Bottom, Right, Top);

    public bool Contains(PdfPoint p) => p.X >= Left && p.X <= Right && p.Y >= Bottom && p.Y <= Top;

    public bool IntersectsWith(PdfRect other) =>
        other.Left <= Right && other.Right >= Left && other.Bottom <= Top && other.Top >= Bottom;

    public PdfRect Union(PdfRect other) =>
        new(Math.Min(Left, other.Left), Math.Min(Bottom, other.Bottom), Math.Max(Right, other.Right), Math.Max(Top, other.Top));

    public PdfRect Inflate(double dx, double dy) => new(Left - dx, Bottom - dy, Right + dx, Top + dy);
}

/// <summary>
/// Affine matrix using the PDF convention: a point (x, y) maps to (a·x + c·y + e, b·x + d·y + f).
/// <c>m1 * m2</c> applies m1 first, then m2 (same as PDFium's CFX_Matrix and the PDF spec).
/// </summary>
public readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static PdfMatrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public static PdfMatrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    public static PdfMatrix Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    public double Determinant => A * D - B * C;

    public PdfPoint Transform(PdfPoint p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);

    public PdfPoint Transform(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);

    /// <summary>Transforms a rectangle and returns the bounding box of the result.</summary>
    public PdfRect TransformBounds(PdfRect r)
    {
        var p1 = Transform(r.Left, r.Bottom);
        var p2 = Transform(r.Right, r.Bottom);
        var p3 = Transform(r.Left, r.Top);
        var p4 = Transform(r.Right, r.Top);
        return new PdfRect(
            Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X)),
            Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y)),
            Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X)),
            Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y)));
    }

    public PdfMatrix Invert()
    {
        var det = Determinant;
        if (Math.Abs(det) < 1e-12)
        {
            return Identity;
        }

        return new PdfMatrix(
            D / det,
            -B / det,
            -C / det,
            A / det,
            (C * F - D * E) / det,
            (B * E - A * F) / det);
    }

    public static PdfMatrix operator *(PdfMatrix m1, PdfMatrix m2) => new(
        m1.A * m2.A + m1.B * m2.C,
        m1.A * m2.B + m1.B * m2.D,
        m1.C * m2.A + m1.D * m2.C,
        m1.C * m2.B + m1.D * m2.D,
        m1.E * m2.A + m1.F * m2.C + m2.E,
        m1.E * m2.B + m1.F * m2.D + m2.F);
}

/// <summary>
/// Page box and intrinsic rotation (/Rotate) of a page. Reproduces PDFium's
/// CPDF_Page::GetDisplayMatrix so that coordinates computed in managed code match
/// exactly what <c>FPDF_RenderPageBitmap</c> draws.
/// </summary>
public sealed record PdfPageGeometry(PdfRect BBox, int Rotation)
{
    /// <summary>Displayed width in points (after /Rotate).</summary>
    public double Width => Rotation % 2 == 0 ? BBox.Width : BBox.Height;

    /// <summary>Displayed height in points (after /Rotate).</summary>
    public double Height => Rotation % 2 == 0 ? BBox.Height : BBox.Width;

    public PdfSize Size => new(Width, Height);

    /// <summary>Maps user space to the rotated page space (origin bottom-left, y up).</summary>
    public PdfMatrix PageMatrix => (Rotation & 3) switch
    {
        1 => new PdfMatrix(0, -1, 1, 0, -BBox.Bottom, BBox.Right),
        2 => new PdfMatrix(-1, 0, 0, -1, BBox.Right, BBox.Top),
        3 => new PdfMatrix(0, 1, -1, 0, BBox.Top, -BBox.Left),
        _ => new PdfMatrix(1, 0, 0, 1, -BBox.Left, -BBox.Bottom),
    };

    /// <summary>
    /// Matrix mapping PDF user space to device space (y down) for a page drawn into the
    /// device rectangle (left, top, width, height) with an extra rotation in quarter turns.
    /// For odd extra rotations the device rectangle must already have swapped proportions.
    /// </summary>
    public PdfMatrix GetDisplayMatrix(double left, double top, double width, double height, int extraRotation)
    {
        if (Width <= 0 || Height <= 0)
        {
            return PdfMatrix.Identity;
        }

        var right = left + width;
        var bottom = top + height;
        double x0, y0, x1, y1, x2, y2;
        switch (extraRotation & 3)
        {
            case 1:
                (x0, y0, x1, y1, x2, y2) = (left, top, right, top, left, bottom);
                break;
            case 2:
                (x0, y0, x1, y1, x2, y2) = (right, top, right, bottom, left, top);
                break;
            case 3:
                (x0, y0, x1, y1, x2, y2) = (right, bottom, left, bottom, right, top);
                break;
            default:
                (x0, y0, x1, y1, x2, y2) = (left, bottom, left, top, right, bottom);
                break;
        }

        var device = new PdfMatrix(
            (x2 - x0) / Width,
            (y2 - y0) / Width,
            (x1 - x0) / Height,
            (y1 - y0) / Height,
            x0,
            y0);
        return PageMatrix * device;
    }
}
