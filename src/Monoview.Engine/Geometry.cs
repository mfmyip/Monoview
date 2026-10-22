namespace Monoview.Engine;

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

/// <summary>Smooth ink, drawn the same way on the page and while the pen is still down.</summary>
public static class InkCurve
{
    /// <summary>
    /// The Bezier segments of a Catmull-Rom curve through every point: two control points and an end point each.
    /// Strokes of three points or fewer, such as lines and arrowheads, keep their corners.
    /// </summary>
    public static IEnumerable<((double X, double Y) C1, (double X, double Y) C2, (double X, double Y) End)> Segments(IReadOnlyList<(double X, double Y)> p)
    {
        for (int i = 0; i < p.Count - 1; i++)
        {
            var (b, d) = (p[i], p[i + 1]);
            if (p.Count <= 3)
            {
                yield return (b, d, d);
                continue;
            }
            var (a, e) = (p[Math.Max(0, i - 1)], p[Math.Min(p.Count - 1, i + 2)]);
            yield return ((b.X + (d.X - a.X) / 6, b.Y + (d.Y - a.Y) / 6), (d.X - (e.X - b.X) / 6, d.Y - (e.Y - b.Y) / 6), d);
        }
    }
}

/// <summary>Maps PDF user space (points, origin bottom-left, unrotated) to normalized display coordinates.</summary>
internal readonly record struct PageTransform(double A, double B, double C, double D, double E, double F)
{
    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>Normalized display coordinates back to PDF user space.</summary>
    public (double X, double Y) Invert(double x, double y)
    {
        double det = A * D - B * C;
        double dx = x - E, dy = y - F;
        return ((D * dx - C * dy) / det, (A * dy - B * dx) / det);
    }

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
