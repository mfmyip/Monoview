using System.Runtime.InteropServices;

namespace Monoview.Engine;

/// <summary>A stamp annotation, such as a placed signature. Rect is in unrotated normalized page coordinates.</summary>
public sealed record Stamp(int PageIndex, int AnnotIndex, NormRect Rect);

/// <summary>
/// Signatures and other pictures placed on a page, stored as standard Stamp annotations that hold the picture,
/// so other readers show them too.
/// </summary>
public sealed partial class PdfDocument
{
    private const int AnnotStamp = 13; // FPDF_ANNOT_STAMP

    /// <summary>
    /// Places a picture of <paramref name="width"/> x <paramref name="height"/> straight-alpha BGRA pixels in
    /// <paramref name="rect"/> (unrotated normalized page coordinates). Transparent pixels stay transparent.
    /// </summary>
    public Task AddStampAsync(int pageIndex, NormRect rect, int width, int height, byte[] bgra) => PdfiumThread.Run(() =>
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * 4 * height) throw new PdfException("The picture has no pixels.");
        var page = GetPage(pageIndex).__Instance;
        IntPtr annot = FPDFPage_CreateAnnot(page, AnnotStamp);
        if (annot == IntPtr.Zero) throw new PdfException("PDFium could not add the stamp.");
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

    public Task<IReadOnlyList<Stamp>> GetStampsAsync(int pageIndex, WorkPriority priority = WorkPriority.Low) =>
        PdfiumThread.Run(() =>
        {
            var page = GetPage(pageIndex).__Instance;
            var transform = GetTransform(pageIndex);
            var stamps = new List<Stamp>();
            int count = FPDFPage_GetAnnotCount(page);
            for (int i = 0; i < count; i++)
            {
                IntPtr annot = FPDFPage_GetAnnot(page, i);
                if (annot == IntPtr.Zero) continue;
                try
                {
                    if (FPDFAnnot_GetSubtype(annot) != AnnotStamp || FPDFAnnot_GetRect(annot, out var r) == 0) continue;
                    stamps.Add(new Stamp(pageIndex, i, transform.ApplyToBox(
                        Math.Min(r.Left, r.Right), Math.Min(r.Top, r.Bottom), Math.Max(r.Left, r.Right), Math.Max(r.Top, r.Bottom))));
                }
                finally
                {
                    FPDFPage_CloseAnnot(annot);
                }
            }
            return (IReadOnlyList<Stamp>)stamps;
        }, priority);

    /// <summary>
    /// Moves or resizes a stamp to <paramref name="rect"/>. Its appearance is drawn into its own frame, which
    /// readers map onto the rect, so the picture follows. Stamps from other apps work the same way.
    /// </summary>
    public Task MoveStampAsync(Stamp stamp, NormRect rect) => PdfiumThread.Run(() =>
    {
        IntPtr annot = FPDFPage_GetAnnot(GetPage(stamp.PageIndex).__Instance, stamp.AnnotIndex);
        if (annot == IntPtr.Zero) return;
        try
        {
            var bounds = PlaceRect(stamp.PageIndex, rect, out _);
            FPDFAnnot_SetRect(annot, ref bounds);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
        PageEdited(stamp.PageIndex);
    });

    public Task RemoveStampAsync(Stamp stamp) => PdfiumThread.Run(() =>
    {
        if (FPDFPage_RemoveAnnot(GetPage(stamp.PageIndex).__Instance, stamp.AnnotIndex) != 0)
            PageEdited(stamp.PageIndex);
    });

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

    [DllImport("pdfium")] private static extern int FPDFAnnot_GetRect(IntPtr annot, out RectF rect);
    [DllImport("pdfium")] private static extern int FPDFAnnot_AppendObject(IntPtr annot, IntPtr obj);
}
