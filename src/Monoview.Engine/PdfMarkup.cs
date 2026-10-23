using System.Runtime.InteropServices;

namespace Monoview.Engine;

/// <summary>Text markup kinds, valued as their PDF annotation subtypes.</summary>
public enum MarkupKind
{
    Highlight = 9,
    Underline = 10,
    StrikeOut = 12,
}

public readonly record struct MarkupColor(byte R, byte G, byte B);

/// <summary>
/// A highlight, underline or strikeout on a page. Rects are in unrotated normalized page coordinates. The comment is
/// the annotation's /Contents, which Acrobat shows in the markup's pop-up note.
/// </summary>
public sealed record Markup(int PageIndex, int AnnotIndex, MarkupKind Kind, MarkupColor Color, IReadOnlyList<NormRect> Rects, string Comment = "");

/// <summary>
/// Text markup stored as standard PDF annotations, so other readers show it too. After each change
/// the page is reloaded, which makes PDFium build the annotation's appearance; that appearance is
/// written into the file on save.
/// </summary>
public sealed partial class PdfDocument
{
    private const int ColorTypeColor = 0;          // FPDFANNOT_COLORTYPE_Color
    private const int AppearanceNormal = 0;         // FPDF_ANNOT_APPEARANCEMODE_NORMAL

    /// <summary>True once the document has changes that are not saved.</summary>
    public bool IsModified { get; private set; }

    /// <summary>Marks the text covered by <paramref name="rects"/> (unrotated normalized page coordinates, one per line).</summary>
    public Task AddMarkupAsync(int pageIndex, MarkupKind kind, MarkupColor color, IReadOnlyList<NormRect> rects, string comment = "") =>
        PdfiumThread.Run(() =>
        {
            if (rects.Count == 0) return;
            var page = GetPage(pageIndex).__Instance;
            var transform = GetTransform(pageIndex);
            IntPtr annot = FPDFPage_CreateAnnot(page, (int)kind);
            if (annot == IntPtr.Zero) throw new PdfException("PDFium could not add the markup.");
            try
            {
                FPDFAnnot_SetColor(annot, ColorTypeColor, color.R, color.G, color.B, 255);
                if (comment.Length > 0) SetString(annot, "Contents", comment);
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var r in rects)
                {
                    // Quad points: top-left, top-right, bottom-left, bottom-right of the line as shown.
                    var (x1, y1) = transform.Invert(r.Left, r.Top);
                    var (x2, y2) = transform.Invert(r.Right, r.Top);
                    var (x3, y3) = transform.Invert(r.Left, r.Bottom);
                    var (x4, y4) = transform.Invert(r.Right, r.Bottom);
                    var quad = new QuadPoints((float)x1, (float)y1, (float)x2, (float)y2, (float)x3, (float)y3, (float)x4, (float)y4);
                    FPDFAnnot_AppendAttachmentPoints(annot, ref quad);
                    foreach (var x in new[] { x1, x2, x3, x4 }) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
                    foreach (var y in new[] { y1, y2, y3, y4 }) { minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
                }
                var bounds = new RectF((float)minX, (float)maxY, (float)maxX, (float)minY);
                FPDFAnnot_SetRect(annot, ref bounds);
            }
            finally
            {
                FPDFPage_CloseAnnot(annot);
            }
            PageEdited(pageIndex);
        });

    public Task<IReadOnlyList<Markup>> GetMarkupsAsync(int pageIndex, WorkPriority priority = WorkPriority.Low) =>
        PdfiumThread.Run(() =>
        {
            var page = GetPage(pageIndex).__Instance;
            var transform = GetTransform(pageIndex);
            var markups = new List<Markup>();
            int count = FPDFPage_GetAnnotCount(page);
            for (int i = 0; i < count; i++)
            {
                IntPtr annot = FPDFPage_GetAnnot(page, i);
                if (annot == IntPtr.Zero) continue;
                try
                {
                    int subtype = FPDFAnnot_GetSubtype(annot);
                    if (subtype is not ((int)MarkupKind.Highlight or (int)MarkupKind.Underline or (int)MarkupKind.StrikeOut)) continue;

                    var rects = new List<NormRect>();
                    nuint quads = FPDFAnnot_CountAttachmentPoints(annot);
                    for (nuint q = 0; q < quads; q++)
                    {
                        if (FPDFAnnot_GetAttachmentPoints(annot, q, out var p) == 0) continue;
                        float[] xs = [p.X1, p.X2, p.X3, p.X4], ys = [p.Y1, p.Y2, p.Y3, p.Y4];
                        rects.Add(transform.ApplyToBox(xs.Min(), ys.Min(), xs.Max(), ys.Max()));
                    }
                    markups.Add(new Markup(pageIndex, i, (MarkupKind)subtype, ReadColor(annot, (MarkupKind)subtype), rects, GetString(annot, "Contents")));
                }
                finally
                {
                    FPDFPage_CloseAnnot(annot);
                }
            }
            return (IReadOnlyList<Markup>)markups;
        }, priority);

    /// <summary>
    /// The markup's color. PDFium won't read /C once the annotation has an appearance, which every markup
    /// gets after a page reload, so read it from the drawn shapes: the fill of a highlight, the line of the others.
    /// </summary>
    private static MarkupColor ReadColor(IntPtr annot, MarkupKind kind)
    {
        uint r, g, b;
        if (FPDFAnnot_GetColor(annot, ColorTypeColor, out r, out g, out b, out _) != 0)
            return new MarkupColor((byte)r, (byte)g, (byte)b);
        int count = FPDFAnnot_GetObjectCount(annot);
        for (int i = 0; i < count; i++)
        {
            IntPtr obj = FPDFAnnot_GetObject(annot, i);
            if (obj == IntPtr.Zero) continue;
            bool found = kind == MarkupKind.Highlight
                ? FPDFPageObj_GetFillColor(obj, out r, out g, out b, out _) != 0
                : FPDFPageObj_GetStrokeColor(obj, out r, out g, out b, out _) != 0;
            if (found) return new MarkupColor((byte)r, (byte)g, (byte)b);
        }
        return new MarkupColor(0, 0, 0);
    }

    public Task SetMarkupColorAsync(Markup markup, MarkupColor color) => PdfiumThread.Run(() =>
    {
        IntPtr annot = FPDFPage_GetAnnot(GetPage(markup.PageIndex).__Instance, markup.AnnotIndex);
        if (annot == IntPtr.Zero) return;
        try
        {
            // PDFium won't set the color while an appearance exists; drop it first, and it's rebuilt in the new color.
            FPDFAnnot_SetAP(annot, AppearanceNormal, IntPtr.Zero);
            if (FPDFAnnot_SetColor(annot, ColorTypeColor, color.R, color.G, color.B, 255) == 0)
                throw new PdfException("PDFium could not change the markup color.");
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
        PageEdited(markup.PageIndex);
    });

    public Task SetMarkupCommentAsync(Markup markup, string comment) => PdfiumThread.Run(() =>
    {
        IntPtr annot = FPDFPage_GetAnnot(GetPage(markup.PageIndex).__Instance, markup.AnnotIndex);
        if (annot == IntPtr.Zero) return;
        try
        {
            SetString(annot, "Contents", comment);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
        PageEdited(markup.PageIndex);
    });

    public Task RemoveMarkupAsync(Markup markup) => PdfiumThread.Run(() =>
    {
        if (FPDFPage_RemoveAnnot(GetPage(markup.PageIndex).__Instance, markup.AnnotIndex) != 0)
            PageEdited(markup.PageIndex);
    });

    /// <summary>Reload the page on next use, so renders show the change and appearances get built.</summary>
    private void PageEdited(int pageIndex)
    {
        _openPages.Remove(pageIndex);
        IsModified = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QuadPoints(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)
    {
        public float X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, X3 = x3, Y3 = y3, X4 = x4, Y4 = y4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectF(float left, float top, float right, float bottom)
    {
        public float Left = left, Top = top, Right = right, Bottom = bottom;
    }

    [DllImport("pdfium")] private static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [DllImport("pdfium")] private static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport("pdfium")] private static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [DllImport("pdfium")] private static extern int FPDFPage_RemoveAnnot(IntPtr page, int index);
    [DllImport("pdfium")] private static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport("pdfium")] private static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport("pdfium")] private static extern int FPDFAnnot_SetColor(IntPtr annot, int type, uint r, uint g, uint b, uint a);
    [DllImport("pdfium")] private static extern int FPDFAnnot_GetColor(IntPtr annot, int type, out uint r, out uint g, out uint b, out uint a);
    [DllImport("pdfium")] private static extern int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, ref QuadPoints quad);
    [DllImport("pdfium")] private static extern nuint FPDFAnnot_CountAttachmentPoints(IntPtr annot);
    [DllImport("pdfium")] private static extern int FPDFAnnot_GetAttachmentPoints(IntPtr annot, nuint index, out QuadPoints quad);
    [DllImport("pdfium")] private static extern int FPDFAnnot_SetRect(IntPtr annot, ref RectF rect);
    [DllImport("pdfium")] private static extern int FPDFAnnot_GetObjectCount(IntPtr annot);
    [DllImport("pdfium")] private static extern IntPtr FPDFAnnot_GetObject(IntPtr annot, int index);
    [DllImport("pdfium")] private static extern int FPDFPageObj_GetFillColor(IntPtr obj, out uint r, out uint g, out uint b, out uint a);
    [DllImport("pdfium")] private static extern int FPDFPageObj_GetStrokeColor(IntPtr obj, out uint r, out uint g, out uint b, out uint a);
    [DllImport("pdfium")] private static extern int FPDFAnnot_SetAP(IntPtr annot, int mode, IntPtr value);
}
