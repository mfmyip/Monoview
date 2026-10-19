using System.Runtime.InteropServices;
using System.Text;

namespace Monoview.Engine;

/// <summary>The kinds of object the markup tools place on a page, each a standard PDF annotation.</summary>
public enum ShapeKind
{
    /// <summary>A picture, such as a signature: a Stamp annotation.</summary>
    Stamp,
    /// <summary>A Square annotation.</summary>
    Rectangle,
    /// <summary>A Circle annotation.</summary>
    Ellipse,
    /// <summary>Freehand strokes, lines and arrows: an Ink annotation.</summary>
    Ink,
    /// <summary>A FreeText annotation.</summary>
    TextBox,
    /// <summary>A sticky note: a Text annotation.</summary>
    Note,
}

/// <summary>Line and fill colors, line width in points, and text size in points.</summary>
public sealed record ShapeStyle(MarkupColor Stroke, MarkupColor? Fill = null, float LineWidth = 2, float FontSize = 14);

/// <summary>An object on a page. Rect is in unrotated normalized page coordinates; Text is a text box's or note's text.</summary>
public sealed record Shape(int PageIndex, int AnnotIndex, ShapeKind Kind, NormRect Rect, string Text = "");

/// <summary>
/// Shapes, freehand ink, text boxes, notes and pictures, stored as standard annotations so other readers show them.
/// After each change the page is reloaded, which makes PDFium build any missing appearance.
/// </summary>
public sealed partial class PdfDocument
{
    private const int AnnotText = 1, AnnotFreeText = 3, AnnotSquare = 5, AnnotCircle = 6, AnnotStamp = 13, AnnotInk = 15;
    private const int ColorTypeInterior = 1; // FPDFANNOT_COLORTYPE_InteriorColor
    private const float NoteSize = 20;       // points; notes show as an icon of this size

    private static ShapeKind? KindOf(int subtype) => subtype switch
    {
        AnnotStamp => ShapeKind.Stamp,
        AnnotSquare => ShapeKind.Rectangle,
        AnnotCircle => ShapeKind.Ellipse,
        AnnotInk => ShapeKind.Ink,
        AnnotFreeText => ShapeKind.TextBox,
        AnnotText => ShapeKind.Note,
        _ => null,
    };

    /// <summary>
    /// Places a picture of <paramref name="width"/> x <paramref name="height"/> straight-alpha BGRA pixels in
    /// <paramref name="rect"/> (unrotated normalized page coordinates). Transparent pixels stay transparent.
    /// </summary>
    public Task AddStampAsync(int pageIndex, NormRect rect, int width, int height, byte[] bgra) => PdfiumThread.Run(() =>
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * 4 * height) throw new PdfException("The picture has no pixels.");
        IntPtr annot = CreateAnnot(pageIndex, AnnotStamp);
        var pixels = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        IntPtr bitmap = FPDFBitmap_CreateEx(width, height, BitmapFormatBgra, pixels.AddrOfPinnedObject(), width * 4);
        try
        {
            // The appearance gets its frame from the rect when the picture is added: use the fixed frame, draw
            // the picture into it, then set the real rect. PDFium maps the frame onto the rect from then on.
            var frame = AppearanceFrame;
            FPDFAnnot_SetRect(annot, ref frame);
            var bounds = PlaceRect(pageIndex, rect, out var m);
            double sx = (frame.Right - frame.Left) / (bounds.Right - bounds.Left), sy = (frame.Top - frame.Bottom) / (bounds.Top - bounds.Bottom);
            IntPtr image = FPDFPageObj_NewImageObj(Doc.__Instance);
            if (FPDFImageObj_SetBitmap(IntPtr.Zero, 0, image, bitmap) == 0)
                throw new PdfException("PDFium could not read the picture.");
            FPDFImageObj_SetMatrix(image, m.A * sx, m.B * sy, m.C * sx, m.D * sy,
                frame.Left + (m.E - bounds.Left) * sx, frame.Bottom + (m.F - bounds.Bottom) * sy);
            if (FPDFAnnot_AppendObject(annot, image) == 0)
                throw new PdfException("PDFium could not add the picture to the stamp.");
            FPDFAnnot_SetRect(annot, ref bounds);
        }
        finally
        {
            FPDFBitmap_Destroy(bitmap);
            pixels.Free();
            FPDFPage_CloseAnnot(annot);
        }
        PageEdited(pageIndex);
    });

    /// <summary>A rectangle or ellipse filling <paramref name="rect"/>.</summary>
    public Task AddBoxAsync(int pageIndex, ShapeKind kind, NormRect rect, ShapeStyle style) => PdfiumThread.Run(() =>
    {
        if (kind is not (ShapeKind.Rectangle or ShapeKind.Ellipse)) throw new ArgumentException("Only rectangles and ellipses are boxes.", nameof(kind));
        IntPtr annot = CreateAnnot(pageIndex, kind == ShapeKind.Rectangle ? AnnotSquare : AnnotCircle);
        try
        {
            var bounds = PlaceRect(pageIndex, rect, out _);
            FPDFAnnot_SetRect(annot, ref bounds);
            SetStyle(annot, style);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
        PageEdited(pageIndex);
    });

    /// <summary>Freehand strokes, a line or an arrow: each stroke is a list of points in unrotated normalized page coordinates.</summary>
    public Task AddInkAsync(int pageIndex, IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes, ShapeStyle style) => PdfiumThread.Run(() =>
    {
        if (strokes.All(s => s.Count == 0)) return;
        var transform = GetTransform(pageIndex);
        IntPtr annot = CreateAnnot(pageIndex, AnnotInk);
        try
        {
            SetStyle(annot, style with { Fill = null });
            var all = new List<PointF>();
            foreach (var stroke in strokes.Where(s => s.Count > 0))
            {
                var points = stroke.Select(p => { var (x, y) = transform.Invert(p.X, p.Y); return new PointF((float)x, (float)y); }).ToArray();
                FPDFAnnot_AddInkStroke(annot, points, (nuint)points.Length);
                all.AddRange(points);
            }
            float pad = style.LineWidth / 2 + 1;
            var bounds = new RectF(all.Min(p => p.X) - pad, all.Max(p => p.Y) + pad, all.Max(p => p.X) + pad, all.Min(p => p.Y) - pad);
            FPDFAnnot_SetRect(annot, ref bounds);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
        PageEdited(pageIndex);
    });

    /// <summary>A text box with <paramref name="text"/> in <paramref name="rect"/>, in the stroke color and font size of <paramref name="style"/>.</summary>
    public Task AddTextBoxAsync(int pageIndex, NormRect rect, string text, ShapeStyle style) => PdfiumThread.Run(() =>
    {
        IntPtr annot = CreateAnnot(pageIndex, AnnotFreeText);
        try
        {
            var bounds = PlaceRect(pageIndex, rect, out _);
            FPDFAnnot_SetRect(annot, ref bounds);
            var c = style.Stroke;
            SetString(annot, "DA", FormattableString.Invariant($"/Helv {style.FontSize:0.##} Tf {c.R / 255.0:0.###} {c.G / 255.0:0.###} {c.B / 255.0:0.###} rg"));
            SetString(annot, "Contents", text);
            if (style.Fill is { } fill) FPDFAnnot_SetColor(annot, ColorTypeColor, fill.R, fill.G, fill.B, 255);
            FPDFAnnot_SetBorder(annot, 0, 0, 0);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
        PageEdited(pageIndex);
    });

    /// <summary>A sticky note whose icon's top-left corner is at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public Task AddNoteAsync(int pageIndex, double x, double y, string text, MarkupColor color) => PdfiumThread.Run(() =>
    {
        IntPtr annot = CreateAnnot(pageIndex, AnnotText);
        try
        {
            var (px, py) = GetTransform(pageIndex).Invert(x, y);
            var bounds = new RectF((float)px, (float)py, (float)px + NoteSize, (float)py - NoteSize);
            FPDFAnnot_SetRect(annot, ref bounds);
            FPDFAnnot_SetColor(annot, ColorTypeColor, color.R, color.G, color.B, 255);
            SetString(annot, "Contents", text);
            SetString(annot, "Name", "Comment");
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
        PageEdited(pageIndex);
    });

    public Task<IReadOnlyList<Shape>> GetShapesAsync(int pageIndex, WorkPriority priority = WorkPriority.Low) =>
        PdfiumThread.Run(() =>
        {
            var page = GetPage(pageIndex).__Instance;
            var transform = GetTransform(pageIndex);
            var shapes = new List<Shape>();
            int count = FPDFPage_GetAnnotCount(page);
            for (int i = 0; i < count; i++)
            {
                IntPtr annot = FPDFPage_GetAnnot(page, i);
                if (annot == IntPtr.Zero) continue;
                try
                {
                    if (KindOf(FPDFAnnot_GetSubtype(annot)) is not { } kind || FPDFAnnot_GetRect(annot, out var r) == 0) continue;
                    var rect = transform.ApplyToBox(Math.Min(r.Left, r.Right), Math.Min(r.Top, r.Bottom), Math.Max(r.Left, r.Right), Math.Max(r.Top, r.Bottom));
                    string text = kind is ShapeKind.TextBox or ShapeKind.Note ? GetString(annot, "Contents") : "";
                    shapes.Add(new Shape(pageIndex, i, kind, rect, text));
                }
                finally
                {
                    FPDFPage_CloseAnnot(annot);
                }
            }
            return (IReadOnlyList<Shape>)shapes;
        }, priority);

    /// <summary>
    /// Moves or resizes a shape to <paramref name="rect"/>. Pictures follow their rect; ink strokes are scaled
    /// into it; the others get a fresh appearance in their new place. Notes keep their icon size.
    /// </summary>
    public Task MoveShapeAsync(Shape shape, NormRect rect) => PdfiumThread.Run(() =>
    {
        IntPtr annot = FPDFPage_GetAnnot(GetPage(shape.PageIndex).__Instance, shape.AnnotIndex);
        if (annot == IntPtr.Zero) return;
        try
        {
            var bounds = PlaceRect(shape.PageIndex, rect, out _);
            if (shape.Kind == ShapeKind.Note)
                bounds = new RectF(bounds.Left, bounds.Top, bounds.Left + NoteSize, bounds.Top - NoteSize);
            if (shape.Kind == ShapeKind.Ink && FPDFAnnot_GetRect(annot, out var old) != 0)
                ScaleInk(annot, Normalized(old), bounds);
            FPDFAnnot_SetRect(annot, ref bounds);
            if (shape.Kind != ShapeKind.Stamp)
                FPDFAnnot_SetAP(annot, AppearanceNormal, IntPtr.Zero); // rebuilt in the new place on reload
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
        PageEdited(shape.PageIndex);
    });

    /// <summary>Changes a text box's or note's text.</summary>
    public Task SetShapeTextAsync(Shape shape, string text) => PdfiumThread.Run(() =>
    {
        IntPtr annot = FPDFPage_GetAnnot(GetPage(shape.PageIndex).__Instance, shape.AnnotIndex);
        if (annot == IntPtr.Zero) return;
        try
        {
            SetString(annot, "Contents", text);
            if (shape.Kind == ShapeKind.TextBox)
                FPDFAnnot_SetAP(annot, AppearanceNormal, IntPtr.Zero);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
        PageEdited(shape.PageIndex);
    });

    public Task RemoveShapeAsync(Shape shape) => PdfiumThread.Run(() =>
    {
        if (FPDFPage_RemoveAnnot(GetPage(shape.PageIndex).__Instance, shape.AnnotIndex) != 0)
            PageEdited(shape.PageIndex);
    });

    private IntPtr CreateAnnot(int pageIndex, int subtype)
    {
        IntPtr annot = FPDFPage_CreateAnnot(GetPage(pageIndex).__Instance, subtype);
        return annot != IntPtr.Zero ? annot : throw new PdfException("PDFium could not add the annotation.");
    }

    private static void SetStyle(IntPtr annot, ShapeStyle style)
    {
        var c = style.Stroke;
        FPDFAnnot_SetColor(annot, ColorTypeColor, c.R, c.G, c.B, 255);
        if (style.Fill is { } f) FPDFAnnot_SetColor(annot, ColorTypeInterior, f.R, f.G, f.B, 255);
        FPDFAnnot_SetBorder(annot, 0, 0, style.LineWidth);
    }

    /// <summary>Maps every ink point from the <paramref name="from"/> box to the <paramref name="to"/> box.</summary>
    private static void ScaleInk(IntPtr annot, RectF from, RectF to)
    {
        double sx = (to.Right - to.Left) / Math.Max(0.01, from.Right - from.Left);
        double sy = (to.Top - to.Bottom) / Math.Max(0.01, from.Top - from.Bottom);
        var strokes = new List<PointF[]>();
        uint count = FPDFAnnot_GetInkListCount(annot);
        for (uint i = 0; i < count; i++)
        {
            uint n = FPDFAnnot_GetInkListPath(annot, i, null, 0);
            var points = new PointF[n];
            FPDFAnnot_GetInkListPath(annot, i, points, n);
            strokes.Add(points.Select(p => new PointF((float)(to.Left + (p.X - from.Left) * sx), (float)(to.Bottom + (p.Y - from.Bottom) * sy))).ToArray());
        }
        FPDFAnnot_RemoveInkList(annot);
        foreach (var points in strokes)
            FPDFAnnot_AddInkStroke(annot, points, (nuint)points.Length);
    }

    private static RectF Normalized(RectF r) =>
        new(Math.Min(r.Left, r.Right), Math.Max(r.Top, r.Bottom), Math.Max(r.Left, r.Right), Math.Min(r.Top, r.Bottom));

    private static void SetString(IntPtr annot, string key, string value) =>
        FPDFAnnot_SetStringValue(annot, key, Encoding.Unicode.GetBytes(value + "\0"));

    private static string GetString(IntPtr annot, string key)
    {
        uint bytes = FPDFAnnot_GetStringValue(annot, key, null, 0);
        if (bytes <= 2) return "";
        var buffer = new byte[bytes];
        FPDFAnnot_GetStringValue(annot, key, buffer, bytes);
        return Encoding.Unicode.GetString(buffer, 0, (int)bytes - 2);
    }

    /// <summary>
    /// The annotation bounds in page space for <paramref name="rect"/>, and the matrix that draws a picture upright
    /// into it: the picture's bottom-left, bottom-right and top-left corners land on the rect's as shown.
    /// </summary>
    private RectF PlaceRect(int pageIndex, NormRect rect, out (double A, double B, double C, double D, double E, double F) matrix)
    {
        var transform = GetTransform(pageIndex);
        var (x0, y0) = transform.Invert(rect.Left, rect.Bottom);
        var (x1, y1) = transform.Invert(rect.Right, rect.Bottom);
        var (x2, y2) = transform.Invert(rect.Left, rect.Top);
        var (x3, y3) = transform.Invert(rect.Right, rect.Top);
        matrix = (x1 - x0, y1 - y0, x2 - x0, y2 - y0, x0, y0);
        double[] xs = [x0, x1, x2, x3], ys = [y0, y1, y2, y3];
        return new RectF((float)xs.Min(), (float)ys.Max(), (float)xs.Max(), (float)ys.Min());
    }

    /// <summary>
    /// The frame our stamp appearances are drawn in. PDFium resets a frame to the new rect only when that rect
    /// contains it, which no rect on a page does for this one, so moves never disturb the drawing.
    /// </summary>
    private static readonly RectF AppearanceFrame = new(-30000, 30000, 30000, -30000);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PointF(float x, float y)
    {
        public readonly float X = x, Y = y;
    }

    [DllImport("pdfium")] private static extern int FPDFAnnot_GetRect(IntPtr annot, out RectF rect);
    [DllImport("pdfium")] private static extern int FPDFAnnot_AppendObject(IntPtr annot, IntPtr obj);
    [DllImport("pdfium")] private static extern int FPDFAnnot_SetBorder(IntPtr annot, float hRadius, float vRadius, float width);
    [DllImport("pdfium")] private static extern int FPDFAnnot_AddInkStroke(IntPtr annot, PointF[] points, nuint count);
    [DllImport("pdfium")] private static extern int FPDFAnnot_RemoveInkList(IntPtr annot);
    [DllImport("pdfium")] private static extern uint FPDFAnnot_GetInkListCount(IntPtr annot);
    [DllImport("pdfium")] private static extern uint FPDFAnnot_GetInkListPath(IntPtr annot, uint index, [In, Out] PointF[]? buffer, uint length);
    [DllImport("pdfium", CharSet = CharSet.Ansi)] private static extern int FPDFAnnot_SetStringValue(IntPtr annot, string key, byte[] value);
    [DllImport("pdfium", CharSet = CharSet.Ansi)] private static extern uint FPDFAnnot_GetStringValue(IntPtr annot, string key, [In, Out] byte[]? buffer, uint length);
}
