namespace Monoview.Engine;

/// <summary>
/// View rotation in quarter turns clockwise (0–3), applied on top of each page's own /Rotate.
/// Geometry from <see cref="PageText"/> is unrotated; these map it to and from the rotated view.
/// </summary>
public static class Rotation
{
    public static int Normalize(int quarterTurns) => ((quarterTurns % 4) + 4) % 4;

    public static PageSize Apply(PageSize size, int rotation) =>
        Normalize(rotation) % 2 == 1 ? new PageSize(size.Height, size.Width) : size;

    /// <summary>Unrotated normalized point → rotated view.</summary>
    public static (double X, double Y) ToView(double x, double y, int rotation) => Normalize(rotation) switch
    {
        1 => (1 - y, x),
        2 => (1 - x, 1 - y),
        3 => (y, 1 - x),
        _ => (x, y),
    };

    /// <summary>Rotated view point → unrotated normalized page coordinates.</summary>
    public static (double X, double Y) FromView(double x, double y, int rotation) => ToView(x, y, 4 - Normalize(rotation));

    public static NormRect ToView(NormRect r, int rotation)
    {
        var (x0, y0) = ToView(r.Left, r.Top, rotation);
        var (x1, y1) = ToView(r.Right, r.Bottom, rotation);
        return new NormRect((float)Math.Min(x0, x1), (float)Math.Min(y0, y1), (float)Math.Max(x0, x1), (float)Math.Max(y0, y1));
    }
}
