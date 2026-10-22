using System.Globalization;
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
    /// <summary>Text in a box. Ours are stamps we draw ourselves; a FreeText annotation from another app is one too.</summary>
    TextBox,
    /// <summary>A sticky note: a Text annotation.</summary>
    Note,
}

/// <summary>The PDF standard fonts a text box can use. Every reader has them, so nothing is embedded.</summary>
public enum TextFont { Helvetica, Times, Courier }

/// <summary>
/// How an object looks. Stroke is the line or border color, and Fill the inside; null means none. LineWidth and
/// FontSize are in points. TextColor and Font are for text boxes. A note's color is its Fill.
/// </summary>
public sealed record ShapeStyle(MarkupColor? Stroke, MarkupColor? Fill = null, float LineWidth = 2, float FontSize = 14,
    MarkupColor? TextColor = null, TextFont Font = TextFont.Helvetica);

/// <summary>
/// An object on a page. Rect is in unrotated normalized page coordinates; Text is a text box's or note's text.
/// Style is null for pictures and for objects another app made. Ink is an ink object's strokes, in the same coordinates.
/// </summary>
public sealed record Shape(int PageIndex, int AnnotIndex, ShapeKind Kind, NormRect Rect, string Text = "", ShapeStyle? Style = null,
    IReadOnlyList<IReadOnlyList<(double X, double Y)>>? Ink = null);

/// <summary>
/// Shapes, freehand ink, text boxes, notes and pictures, stored as standard annotations so other readers show them.
/// After each change the page is reloaded, which makes PDFium build any missing appearance.
/// </summary>
public sealed partial class PdfDocument
{
    private const int AnnotText = 1, AnnotFreeText = 3, AnnotSquare = 5, AnnotCircle = 6, AnnotStamp = 13, AnnotInk = 15, AnnotWidget = 20;
    private const int ColorTypeInterior = 1; // FPDFANNOT_COLORTYPE_InteriorColor
    private const float NoteSize = 20;       // points; notes show as an icon of this size
    private const int AnnotFlagHidden = 2;   // FPDF_ANNOT_FLAG_HIDDEN
    private static readonly MarkupColor Black = new(0, 0, 0);

    /// <summary>
    /// The style of every object we make, kept in the annotation: PDFium won't read colors back once an appearance
    /// exists. On a stamp it also marks one of our text boxes.
    /// </summary>
    private const string StyleKey = "MonoviewStyle";

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
            var bounds = PlaceRect(pageIndex, rect, out var m);
            BeginFramedDrawing(annot, bounds, out double sx, out double sy);
            var frame = AppearanceFrame;
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
        AddBox(pageIndex, kind, rect, style);
        PageEdited(pageIndex);
    });

    private void AddBox(int pageIndex, ShapeKind kind, NormRect rect, ShapeStyle style)
    {
        if (kind is not (ShapeKind.Rectangle or ShapeKind.Ellipse)) throw new ArgumentException("Only rectangles and ellipses are boxes.", nameof(kind));
        IntPtr annot = CreateAnnot(pageIndex, kind == ShapeKind.Rectangle ? AnnotSquare : AnnotCircle);
        try
        {
            var bounds = PlaceRect(pageIndex, rect, out _);
            FPDFAnnot_SetRect(annot, ref bounds);
            var c = style.Stroke ?? Black;
            FPDFAnnot_SetColor(annot, ColorTypeColor, c.R, c.G, c.B, 255);
            if (style.Fill is { } f) FPDFAnnot_SetColor(annot, ColorTypeInterior, f.R, f.G, f.B, 255);
            FPDFAnnot_SetBorder(annot, 0, 0, style.Stroke is null ? 0 : style.LineWidth); // PDFium draws no border at width 0
            SetString(annot, StyleKey, FormatStyle(style));
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>Freehand strokes, a line or an arrow: each stroke is a list of points in unrotated normalized page coordinates.</summary>
    public Task AddInkAsync(int pageIndex, IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes, ShapeStyle style) => PdfiumThread.Run(() =>
    {
        var transform = GetTransform(pageIndex);
        var points = strokes.Where(s => s.Count > 0)
            .Select(s => Thin(s.Select(p => { var (x, y) = transform.Invert(p.X, p.Y); return new PointF((float)x, (float)y); }).ToArray()))
            .ToList();
        if (points.Count == 0) return;
        AddInk(pageIndex, points, style);
        PageEdited(pageIndex);
    });

    /// <summary>Ink from strokes in page space.</summary>
    private void AddInk(int pageIndex, IReadOnlyList<PointF[]> strokes, ShapeStyle style)
    {
        IntPtr annot = CreateAnnot(pageIndex, AnnotInk);
        try
        {
            var c = style.Stroke ?? Black;
            FPDFAnnot_SetColor(annot, ColorTypeColor, c.R, c.G, c.B, 255);
            FPDFAnnot_SetBorder(annot, 0, 0, style.LineWidth);
            foreach (var points in strokes)
                FPDFAnnot_AddInkStroke(annot, points, (nuint)points.Length);
            var all = strokes.SelectMany(s => s).ToList();
            float pad = style.LineWidth / 2 + 1;
            var bounds = new RectF(all.Min(p => p.X) - pad, all.Max(p => p.Y) + pad, all.Max(p => p.X) + pad, all.Min(p => p.Y) - pad);
            FPDFAnnot_SetRect(annot, ref bounds);
            SetString(annot, StyleKey, FormatStyle(style));
            DrawInk(annot, strokes, style);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>
    /// Draws the ink appearance ourselves. PDFium joins the points with straight lines, which shows as corners on a
    /// curve; we draw a smooth curve through them with round ends and joins.
    /// </summary>
    private static void DrawInk(IntPtr annot, IReadOnlyList<PointF[]> strokes, ShapeStyle style)
    {
        var c = style.Stroke ?? Black;
        var s = new StringBuilder(FormattableString.Invariant(
            $"{c.R / 255.0:0.###} {c.G / 255.0:0.###} {c.B / 255.0:0.###} RG {style.LineWidth:0.##} w 1 J 1 j\n"));
        foreach (var p in strokes.Where(p => p.Length > 0))
        {
            s.Append(Invariant($"{p[0].X:0.##} {p[0].Y:0.##} m\n"));
            if (p.Length == 1) s.Append(Invariant($"{p[0].X:0.##} {p[0].Y:0.##} l\n"));
            foreach (var (c1, c2, end) in InkCurve.Segments(p.Select(q => ((double)q.X, (double)q.Y)).ToList()))
                s.Append(Invariant($"{c1.X:0.##} {c1.Y:0.##} {c2.X:0.##} {c2.Y:0.##} {end.X:0.##} {end.Y:0.##} c\n"));
            s.Append("S\n");
        }
        FPDFAnnot_SetAPText(annot, AppearanceNormal, Encoding.Unicode.GetBytes(s + "\0"));
    }

    /// <summary>Drops freehand points closer than half a point to the last one kept: pointer jitter, not shape.</summary>
    private static PointF[] Thin(PointF[] points)
    {
        if (points.Length <= 3) return points;
        var kept = new List<PointF> { points[0] };
        foreach (var p in points.Skip(1).SkipLast(1))
            if (Math.Abs(p.X - kept[^1].X) + Math.Abs(p.Y - kept[^1].Y) >= 0.5f) kept.Add(p);
        kept.Add(points[^1]);
        return kept.ToArray();
    }

    /// <summary>A text box with <paramref name="text"/> in <paramref name="rect"/>, in the text color, font, size, fill and border of <paramref name="style"/>.</summary>
    public Task AddTextBoxAsync(int pageIndex, NormRect rect, string text, ShapeStyle style) => PdfiumThread.Run(() =>
    {
        AddTextBox(pageIndex, rect, text, style);
        PageEdited(pageIndex);
    });

    /// <summary>
    /// Our text boxes are stamps we draw: PDFium's FreeText appearance always has a 1 pt border in the text color,
    /// and it draws every font as Helvetica. Other readers show the drawing; only Monoview edits the words.
    /// </summary>
    private void AddTextBox(int pageIndex, NormRect rect, string text, ShapeStyle style)
    {
        IntPtr annot = CreateAnnot(pageIndex, AnnotStamp);
        try
        {
            SetString(annot, "Contents", text);
            SetString(annot, StyleKey, FormatStyle(style));
            DrawTextBox(annot, pageIndex, rect, text, style);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>
    /// Draws a text box's fill, border and wrapped lines of text into its stamp, replacing what was there. A box too
    /// short for its text grows downward, as shown, to fit it, as in Preview.
    /// </summary>
    private void DrawTextBox(IntPtr annot, int pageIndex, NormRect rect, string text, ShapeStyle style)
    {
        for (int i = FPDFAnnot_GetObjectCount(annot) - 1; i >= 0; i--)
            FPDFAnnot_RemoveObject(annot, i);
        PlaceRect(pageIndex, rect, out var m);
        // Lay out in the box as shown, in points from its bottom-left corner.
        double width = Math.Sqrt(m.A * m.A + m.B * m.B), height = Math.Sqrt(m.C * m.C + m.D * m.D);
        float border = style.Stroke is null ? 0 : style.LineWidth;
        // Spacing as Monoview's editor shows it, in Arial, Times New Roman and Courier New, so typing and the drawn
        // box line up: lines 1.15 sizes apart, the first baseline 0.9 sizes below the padding.
        float size = style.FontSize, pad = 2 + border;

        IntPtr font = FPDFText_LoadStandardFont(Doc.__Instance, style.Font switch
        {
            TextFont.Times => "Times-Roman",
            TextFont.Courier => "Courier",
            _ => "Helvetica",
        });
        try
        {
            IntPtr Line(string s)
            {
                IntPtr obj = FPDFPageObj_CreateTextObj(Doc.__Instance, font, size);
                FPDFText_SetText(obj, Encoding.Unicode.GetBytes(s + "\0"));
                return obj;
            }
            var lines = Wrap(text, width - 2 * pad, Line);
            double needed = 2 * pad + lines.Count * size * 1.15;
            if (needed > height)
            {
                // Keep the top edge as shown and move the bottom one down.
                double grow = needed / height;
                m = (m.A, m.B, m.C * grow, m.D * grow, m.E + m.C * (1 - grow), m.F + m.D * (1 - grow));
                height = needed;
            }
            double[] xs = [m.E, m.E + m.A, m.E + m.C, m.E + m.A + m.C], ys = [m.F, m.F + m.B, m.F + m.D, m.F + m.B + m.D];
            var bounds = new RectF((float)xs.Min(), (float)ys.Max(), (float)xs.Max(), (float)ys.Min());

            // (a..f) maps the box's points into the stamp's frame.
            BeginFramedDrawing(annot, bounds, out double sx, out double sy);
            var frame = AppearanceFrame;
            double a = m.A / width * sx, b = m.B / width * sy, c = m.C / height * sx, d = m.D / height * sy;
            double e = frame.Left + (m.E - bounds.Left) * sx, f = frame.Bottom + (m.F - bounds.Bottom) * sy;
            void Add(IntPtr obj)
            {
                FPDFPageObj_Transform(obj, a, b, c, d, e, f);
                if (FPDFAnnot_AppendObject(annot, obj) == 0) throw new PdfException("PDFium could not draw the text box.");
            }

            if (style.Fill is not null || style.Stroke is not null)
            {
                IntPtr box = FPDFPageObj_CreateNewRect(border / 2, border / 2, (float)Math.Max(0, width - border), (float)Math.Max(0, height - border));
                if (style.Fill is { } fill) FPDFPageObj_SetFillColor(box, fill.R, fill.G, fill.B, 255);
                if (style.Stroke is { } stroke)
                {
                    FPDFPageObj_SetStrokeColor(box, stroke.R, stroke.G, stroke.B, 255);
                    FPDFPageObj_SetStrokeWidth(box, border);
                }
                FPDFPath_SetDrawMode(box, style.Fill is null ? 0 : 1, style.Stroke is null ? 0 : 1);
                Add(box);
            }

            var color = style.TextColor ?? Black;
            double baseline = height - pad - 0.9 * size;
            foreach (string line in lines)
            {
                if (line.Length > 0)
                {
                    IntPtr obj = Line(line);
                    FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, 255);
                    FPDFPageObj_Transform(obj, 1, 0, 0, 1, pad, baseline);
                    Add(obj);
                }
                baseline -= size * 1.15;
            }
            FPDFAnnot_SetRect(annot, ref bounds);
        }
        finally
        {
            FPDFFont_Close(font);
        }
    }

    /// <summary>
    /// Breaks text into lines no wider than <paramref name="width"/> points, at spaces and at line breaks. A word
    /// wider than a line is broken between letters, as the editor does.
    /// </summary>
    private static List<string> Wrap(string text, double width, Func<string, IntPtr> measure)
    {
        double Width(string s)
        {
            IntPtr obj = measure(s);
            FPDFPageObj_GetBounds(obj, out float left, out _, out float right, out _);
            FPDFPageObj_Destroy(obj);
            return right - left;
        }
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = "";
            foreach (string word in paragraph.Split(' '))
            {
                string longer = line.Length == 0 ? word : line + " " + word;
                if (Width(longer) <= width + 0.5) // the editor rounds in favour of fitting; so do we
                {
                    line = longer;
                    continue;
                }
                if (line.Length > 0) lines.Add(line);
                line = "";
                foreach (char c in word)
                {
                    if (line.Length > 0 && Width(line + c) > width + 0.5)
                    {
                        lines.Add(line);
                        line = "";
                    }
                    line += c;
                }
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>A sticky note whose icon's top-left corner is at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public Task AddNoteAsync(int pageIndex, double x, double y, string text, MarkupColor color) => PdfiumThread.Run(() =>
    {
        var (px, py) = GetTransform(pageIndex).Invert(x, y);
        AddNote(pageIndex, (float)px, (float)py, text, color);
        PageEdited(pageIndex);
    });

    /// <summary>A note with its icon's top-left corner at (<paramref name="left"/>, <paramref name="top"/>) in page space.</summary>
    private void AddNote(int pageIndex, float left, float top, string text, MarkupColor color)
    {
        IntPtr annot = CreateAnnot(pageIndex, AnnotText);
        try
        {
            var bounds = new RectF(left, top, left + NoteSize, top - NoteSize);
            FPDFAnnot_SetRect(annot, ref bounds);
            FPDFAnnot_SetColor(annot, ColorTypeColor, color.R, color.G, color.B, 255);
            SetString(annot, "Contents", text);
            SetString(annot, "Name", "Comment");
            SetString(annot, StyleKey, FormatStyle(new ShapeStyle(null, color)));
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

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
                    var style = ParseStyle(GetString(annot, StyleKey));
                    if (kind == ShapeKind.Stamp && style is not null) kind = ShapeKind.TextBox;
                    var rect = transform.ApplyToBox(Math.Min(r.Left, r.Right), Math.Min(r.Top, r.Bottom), Math.Max(r.Left, r.Right), Math.Max(r.Top, r.Bottom));
                    string text = kind is ShapeKind.TextBox or ShapeKind.Note ? GetString(annot, "Contents") : "";
                    var ink = kind == ShapeKind.Ink
                        ? ReadInk(annot).Select(points => (IReadOnlyList<(double X, double Y)>)points.Select(p => transform.Apply(p.X, p.Y)).ToList()).ToList()
                        : null;
                    shapes.Add(new Shape(pageIndex, i, kind, rect, text, style, ink));
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
    /// into it; our text boxes are drawn again to wrap to the new width; the others get a fresh appearance in
    /// their new place. Notes keep their icon size.
    /// </summary>
    public Task MoveShapeAsync(Shape shape, NormRect rect) => PdfiumThread.Run(() =>
    {
        IntPtr annot = FPDFPage_GetAnnot(GetPage(shape.PageIndex).__Instance, shape.AnnotIndex);
        if (annot == IntPtr.Zero) return;
        try
        {
            // Read from the annotation, not the shape: a selection can be older than the last text edit.
            var style = ParseStyle(GetString(annot, StyleKey));
            if (shape.Kind == ShapeKind.TextBox && style is not null)
            {
                DrawTextBox(annot, shape.PageIndex, rect, GetString(annot, "Contents"), style);
                return;
            }
            var bounds = PlaceRect(shape.PageIndex, rect, out _);
            if (shape.Kind == ShapeKind.Note)
                bounds = new RectF(bounds.Left, bounds.Top, bounds.Left + NoteSize, bounds.Top - NoteSize);
            if (shape.Kind != ShapeKind.Stamp)
                FPDFAnnot_SetAP(annot, AppearanceNormal, IntPtr.Zero); // rebuilt in the new place on reload
            var strokes = shape.Kind == ShapeKind.Ink && FPDFAnnot_GetRect(annot, out var old) != 0 ? ScaleInk(annot, Normalized(old), bounds) : null;
            FPDFAnnot_SetRect(annot, ref bounds);
            if (strokes is not null && style is not null) DrawInk(annot, strokes, style); // after the rect: it sets the drawing's frame
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
            PageEdited(shape.PageIndex);
        }
    });

    /// <summary>Changes a text box's or note's text.</summary>
    public Task SetShapeTextAsync(Shape shape, string text) => PdfiumThread.Run(() =>
    {
        IntPtr annot = FPDFPage_GetAnnot(GetPage(shape.PageIndex).__Instance, shape.AnnotIndex);
        if (annot == IntPtr.Zero) return;
        try
        {
            SetString(annot, "Contents", text);
            if (shape.Kind == ShapeKind.TextBox && ParseStyle(GetString(annot, StyleKey)) is { } style)
                DrawTextBox(annot, shape.PageIndex, shape.Rect, text, style);
            else if (shape.Kind == ShapeKind.TextBox)
                FPDFAnnot_SetAP(annot, AppearanceNormal, IntPtr.Zero);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
        PageEdited(shape.PageIndex);
    });

    /// <summary>
    /// Gives a shape a new style. PDFium can't take a fill away from an annotation, so the shape is made again in its
    /// place and comes out on top, the newest on its page. A FreeText box from another app becomes one of ours.
    /// </summary>
    public Task RestyleShapeAsync(Shape shape, ShapeStyle style) => PdfiumThread.Run(() =>
    {
        if (shape.Kind == ShapeKind.Stamp) throw new ArgumentException("Pictures have no style.", nameof(shape));
        var page = GetPage(shape.PageIndex).__Instance;
        IntPtr annot = FPDFPage_GetAnnot(page, shape.AnnotIndex);
        if (annot == IntPtr.Zero) return;
        RectF bounds;
        List<PointF[]> strokes;
        try
        {
            FPDFAnnot_GetRect(annot, out bounds);
            bounds = Normalized(bounds);
            strokes = shape.Kind == ShapeKind.Ink ? ReadInk(annot) : [];
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
        FPDFPage_RemoveAnnot(page, shape.AnnotIndex);
        switch (shape.Kind)
        {
            case ShapeKind.Rectangle or ShapeKind.Ellipse: AddBox(shape.PageIndex, shape.Kind, shape.Rect, style); break;
            case ShapeKind.Ink: AddInk(shape.PageIndex, strokes, style); break;
            case ShapeKind.TextBox: AddTextBox(shape.PageIndex, shape.Rect, shape.Text, style); break;
            case ShapeKind.Note: AddNote(shape.PageIndex, bounds.Left, bounds.Top, shape.Text, style.Fill ?? Black); break;
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

    /// <summary>
    /// Readies a stamp to draw into: it gets its frame from the rect when objects are added, so use the fixed frame,
    /// then set the real rect, <paramref name="bounds"/>, after drawing. PDFium maps the frame onto the rect from then
    /// on. Gives the scale from page space to the frame.
    /// </summary>
    private static void BeginFramedDrawing(IntPtr annot, RectF bounds, out double sx, out double sy)
    {
        var frame = AppearanceFrame;
        FPDFAnnot_SetRect(annot, ref frame);
        sx = (frame.Right - frame.Left) / (bounds.Right - bounds.Left);
        sy = (frame.Top - frame.Bottom) / (bounds.Top - bounds.Bottom);
    }

    private static List<PointF[]> ReadInk(IntPtr annot)
    {
        var strokes = new List<PointF[]>();
        uint count = FPDFAnnot_GetInkListCount(annot);
        for (uint i = 0; i < count; i++)
        {
            uint n = FPDFAnnot_GetInkListPath(annot, i, null, 0);
            var points = new PointF[n];
            FPDFAnnot_GetInkListPath(annot, i, points, n);
            strokes.Add(points);
        }
        return strokes;
    }

    /// <summary>Maps every ink point from the <paramref name="from"/> box to the <paramref name="to"/> box, and returns the new strokes.</summary>
    private static List<PointF[]> ScaleInk(IntPtr annot, RectF from, RectF to)
    {
        double sx = (to.Right - to.Left) / Math.Max(0.01, from.Right - from.Left);
        double sy = (to.Top - to.Bottom) / Math.Max(0.01, from.Top - from.Bottom);
        var strokes = ReadInk(annot)
            .Select(points => points.Select(p => new PointF((float)(to.Left + (p.X - from.Left) * sx), (float)(to.Bottom + (p.Y - from.Bottom) * sy))).ToArray())
            .ToList();
        FPDFAnnot_RemoveInkList(annot);
        foreach (var points in strokes)
            FPDFAnnot_AddInkStroke(annot, points, (nuint)points.Length);
        return strokes;
    }

    // ---- The style kept in each annotation: "stroke=#RRGGBB fill=- width=2 size=14 text=#000000 font=Helvetica" ----

    private static string FormatStyle(ShapeStyle s)
    {
        static string Hex(MarkupColor? c) => c is { } v ? $"#{v.R:X2}{v.G:X2}{v.B:X2}" : "-";
        return Invariant($"stroke={Hex(s.Stroke)} fill={Hex(s.Fill)} width={s.LineWidth:0.##} size={s.FontSize:0.##} text={Hex(s.TextColor)} font={s.Font}");
    }

    private static ShapeStyle? ParseStyle(string value)
    {
        if (value.Length == 0) return null;
        var parts = value.Split(' ').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        MarkupColor? Color(string key) =>
            parts.TryGetValue(key, out var v) && v is ['#', .. var hex] && hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, null, out int rgb)
                ? new MarkupColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb) : null;
        float Number(string key, float fallback) =>
            parts.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) ? n : fallback;
        var font = parts.TryGetValue("font", out var f) && Enum.TryParse<TextFont>(f, out var parsed) ? parsed : TextFont.Helvetica;
        return new ShapeStyle(Color("stroke"), Color("fill"), Number("width", 2), Number("size", 14), Color("text"), font);
    }

    private static string Invariant(FormattableString s) => FormattableString.Invariant(s);

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
    [DllImport("pdfium")] private static extern int FPDFAnnot_RemoveObject(IntPtr annot, int index);
    [DllImport("pdfium")] private static extern int FPDFAnnot_SetBorder(IntPtr annot, float hRadius, float vRadius, float width);
    [DllImport("pdfium")] private static extern int FPDFAnnot_AddInkStroke(IntPtr annot, PointF[] points, nuint count);
    [DllImport("pdfium")] private static extern int FPDFAnnot_RemoveInkList(IntPtr annot);
    [DllImport("pdfium")] private static extern uint FPDFAnnot_GetInkListCount(IntPtr annot);
    [DllImport("pdfium")] private static extern uint FPDFAnnot_GetInkListPath(IntPtr annot, uint index, [In, Out] PointF[]? buffer, uint length);
    [DllImport("pdfium", EntryPoint = "FPDFAnnot_SetAP")] private static extern int FPDFAnnot_SetAPText(IntPtr annot, int mode, byte[] value);
    [DllImport("pdfium", CharSet = CharSet.Ansi)] private static extern int FPDFAnnot_SetStringValue(IntPtr annot, string key, byte[] value);
    [DllImport("pdfium", CharSet = CharSet.Ansi)] private static extern uint FPDFAnnot_GetStringValue(IntPtr annot, string key, [In, Out] byte[]? buffer, uint length);
    [DllImport("pdfium", CharSet = CharSet.Ansi)] private static extern IntPtr FPDFText_LoadStandardFont(IntPtr document, string font);
    [DllImport("pdfium")] private static extern void FPDFFont_Close(IntPtr font);
    [DllImport("pdfium")] private static extern IntPtr FPDFPageObj_CreateTextObj(IntPtr document, IntPtr font, float size);
    [DllImport("pdfium")] private static extern int FPDFText_SetText(IntPtr textObject, byte[] text);
    [DllImport("pdfium")] private static extern IntPtr FPDFPageObj_CreateNewRect(float x, float y, float w, float h);
    [DllImport("pdfium")] private static extern int FPDFPath_SetDrawMode(IntPtr path, int fillMode, int stroke);
    [DllImport("pdfium")] private static extern int FPDFPageObj_SetFillColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [DllImport("pdfium")] private static extern int FPDFPageObj_SetStrokeColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [DllImport("pdfium")] private static extern int FPDFPageObj_SetStrokeWidth(IntPtr obj, float width);
    [DllImport("pdfium")] private static extern void FPDFPageObj_Transform(IntPtr obj, double a, double b, double c, double d, double e, double f);
    [DllImport("pdfium")] private static extern int FPDFPageObj_GetBounds(IntPtr obj, out float left, out float bottom, out float right, out float top);
    [DllImport("pdfium")] private static extern void FPDFPageObj_Destroy(IntPtr obj);
    [DllImport("pdfium")] private static extern int FPDFAnnot_GetFlags(IntPtr annot);
    [DllImport("pdfium")] private static extern int FPDFAnnot_SetFlags(IntPtr annot, int flags);
}
