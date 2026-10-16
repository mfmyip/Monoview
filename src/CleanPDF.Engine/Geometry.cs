namespace CleanPDF.Engine;

/// <summary>Page size in PDF points (1/72 inch) as displayed, with the page's /Rotate applied.</summary>
public readonly record struct PageSize(double Width, double Height);

/// <summary>A pixel rectangle inside a rendered page.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// A rectangle in normalized page coordinates: 0..1 on each axis, origin at the top-left of
/// the page as displayed. All geometry the engine returns uses this, so the UI only has to
/// multiply by the size it draws the page at.
/// </summary>
public readonly record struct NormRect(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public double DistanceTo(double x, double y)
    {
        double dx = Math.Max(Math.Max(Left - x, 0), x - Right);
        double dy = Math.Max(Math.Max(Top - y, 0), y - Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public NormRect Union(NormRect o) =>
        new(Math.Min(Left, o.Left), Math.Min(Top, o.Top), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));
}

/// <summary>Maps PDF user space (points, origin bottom-left, unrotated) to normalized display coordinates.</summary>
internal readonly record struct PageTransform(double A, double B, double C, double D, double E, double F)
{
    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    public NormRect ApplyToBox(double left, double bottom, double right, double top)
    {
        var (x0, y0) = Apply(left, bottom);
        var (x1, y1) = Apply(right, top);
        var (x2, y2) = Apply(left, top);
        var (x3, y3) = Apply(right, bottom);
        return new NormRect(
            (float)Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)),
            (float)Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)),
            (float)Math.Max(Math.Max(x0, x1), Math.Max(x2, x3)),
            (float)Math.Max(Math.Max(y0, y1), Math.Max(y2, y3)));
    }
}
