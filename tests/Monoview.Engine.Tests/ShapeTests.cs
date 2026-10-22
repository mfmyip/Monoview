namespace Monoview.Engine.Tests;

public class ShapeTests
{
    private static readonly MarkupColor Red = new(230, 30, 30), Blue = new(30, 60, 230), Black = new(0, 0, 0), Yellow = new(255, 210, 0);

    private static async Task<RenderedBitmap> Render(PdfDocument doc, int page = 0) => await doc.RenderAsync(page, 612, 792);

    private static (byte B, byte G, byte R) Pixel(RenderedBitmap bmp, double nx, double ny)
    {
        int i = ((int)(ny * bmp.Height) * bmp.Width + (int)(nx * bmp.Width)) * 4;
        return (bmp.Bgra[i], bmp.Bgra[i + 1], bmp.Bgra[i + 2]);
    }

    private static bool IsRed((byte B, byte G, byte R) p) => p.R > 180 && p.G < 100 && p.B < 100;
    private static bool IsBlue((byte B, byte G, byte R) p) => p.B > 180 && p.R < 100 && p.G < 120;
    private static bool IsWhite((byte B, byte G, byte R) p) => p.R > 240 && p.G > 240 && p.B > 240;

    /// <summary>Pixels in the box, normalized, that are clearly not white.</summary>
    private static int InkIn(RenderedBitmap bmp, NormRect box)
    {
        int n = 0;
        for (int y = (int)(box.Top * bmp.Height); y < (int)(box.Bottom * bmp.Height); y++)
            for (int x = (int)(box.Left * bmp.Width); x < (int)(box.Right * bmp.Width); x++)
            {
                int i = (y * bmp.Width + x) * 4;
                if (bmp.Bgra[i] + bmp.Bgra[i + 1] + bmp.Bgra[i + 2] < 600) n++;
            }
        return n;
    }

    // A blank area of the sample's first page.
    private static readonly NormRect Box = new(0.2f, 0.5f, 0.6f, 0.7f);

    [Fact]
    public async Task Rectangle_draws_its_border_and_leaves_the_middle_empty()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddBoxAsync(0, ShapeKind.Rectangle, Box, new ShapeStyle(Red, LineWidth: 4));
        var bmp = await Render(doc);
        Assert.True(IsRed(Pixel(bmp, 0.4, 0.5 + 2.0 / 792)), "top edge");
        Assert.True(IsWhite(Pixel(bmp, 0.4, 0.6)), "middle");
        Assert.Equal(ShapeKind.Rectangle, Assert.Single(await doc.GetShapesAsync(0)).Kind);
    }

    [Fact]
    public async Task Ellipse_with_a_fill()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddBoxAsync(0, ShapeKind.Ellipse, Box, new ShapeStyle(Red, Blue, 2));
        var bmp = await Render(doc);
        Assert.True(IsBlue(Pixel(bmp, 0.4, 0.6)), "filled middle");
        Assert.True(IsWhite(Pixel(bmp, 0.205, 0.505)), "corner outside the ellipse");
    }

    [Fact]
    public async Task Ink_draws_a_stroke()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddInkAsync(0, [[(0.2, 0.6), (0.6, 0.6)]], new ShapeStyle(Red, LineWidth: 4));
        var bmp = await Render(doc);
        Assert.True(IsRed(Pixel(bmp, 0.4, 0.6)), "on the line");
        Assert.True(IsWhite(Pixel(bmp, 0.4, 0.65)), "below the line");
        Assert.Equal(ShapeKind.Ink, Assert.Single(await doc.GetShapesAsync(0)).Kind);
    }

    [Fact]
    public async Task Text_box_shows_its_text_and_reads_it_back()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddTextBoxAsync(0, Box, "Hello there", new ShapeStyle(null, FontSize: 24));
        var bmp = await Render(doc);
        Assert.True(InkIn(bmp, Box) > 100, "text is drawn");
        var shape = Assert.Single(await doc.GetShapesAsync(0));
        Assert.Equal(("Hello there", ShapeKind.TextBox), (shape.Text, shape.Kind));

        await doc.SetShapeTextAsync(shape, "Changed");
        Assert.Equal("Changed", Assert.Single(await doc.GetShapesAsync(0)).Text);
    }

    [Fact]
    public async Task Note_shows_an_icon_and_keeps_its_text()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddNoteAsync(0, 0.5, 0.6, "Check this", Yellow);
        var bmp = await Render(doc);
        Assert.True(InkIn(bmp, new NormRect(0.5f, 0.6f, 0.54f, 0.63f)) > 20, "icon is drawn");
        var note = Assert.Single(await doc.GetShapesAsync(0));
        Assert.Equal(("Check this", ShapeKind.Note), (note.Text, note.Kind));
    }

    [Fact]
    public async Task Moving_redraws_rectangles_and_scales_ink()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddBoxAsync(0, ShapeKind.Rectangle, Box, new ShapeStyle(Red, Blue, 2));
        await doc.AddInkAsync(0, [[(0.1, 0.2), (0.3, 0.2)]], new ShapeStyle(Red, LineWidth: 4));
        await Render(doc);
        var shapes = await doc.GetShapesAsync(0);

        await doc.MoveShapeAsync(shapes[0], new NormRect(0.5f, 0.75f, 0.9f, 0.95f));
        var ink = shapes[1];
        await doc.MoveShapeAsync(ink, new NormRect(0.5f, 0.3f, 0.9f, ink.Rect.Bottom - ink.Rect.Top + 0.3f));
        var bmp = await Render(doc);
        Assert.True(IsWhite(Pixel(bmp, 0.4, 0.6)), "old rectangle place is empty");
        Assert.True(IsBlue(Pixel(bmp, 0.7, 0.85)), "rectangle in its new place");
        Assert.True(IsWhite(Pixel(bmp, 0.2, 0.2)), "old line place is empty");
        Assert.True(InkIn(bmp, new NormRect(0.55f, 0.28f, 0.85f, 0.33f)) > 100, "line in its new place, stretched");
    }

    [Fact]
    public async Task Rectangle_with_no_border_is_fill_to_the_edge()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddBoxAsync(0, ShapeKind.Rectangle, Box, new ShapeStyle(null, Blue, 4));
        var bmp = await Render(doc);
        Assert.True(IsBlue(Pixel(bmp, 0.4, 0.5 + 1.5 / 792)), "top edge");
        Assert.True(IsBlue(Pixel(bmp, 0.4, 0.6)), "middle");
    }

    [Fact]
    public async Task Text_box_has_no_border_unless_it_has_a_border_color()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddTextBoxAsync(0, Box, "Hi", new ShapeStyle(null));
        var bmp = await Render(doc);
        Assert.True(IsWhite(Pixel(bmp, 0.5, 0.5 + 1.0 / 792)), "no top edge");
        Assert.True(IsWhite(Pixel(bmp, 0.2 + 1.0 / 612, 0.65)), "no left edge");

        var box = Assert.Single(await doc.GetShapesAsync(0));
        await doc.RestyleShapeAsync(box, new ShapeStyle(Red, Blue, 3));
        bmp = await Render(doc);
        Assert.True(IsRed(Pixel(bmp, 0.5, 0.5 + 1.0 / 792)), "red top edge");
        Assert.True(IsBlue(Pixel(bmp, 0.5, 0.65)), "blue inside");
    }

    [Fact]
    public async Task Text_box_fonts_are_drawn_differently()
    {
        async Task<int> Width(TextFont font)
        {
            using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
            await doc.AddTextBoxAsync(0, Box, "iiiiiiiiii", new ShapeStyle(null, FontSize: 24, Font: font));
            var bmp = await Render(doc);
            int right = 0;
            for (int y = (int)(Box.Top * bmp.Height); y < (int)(Box.Bottom * bmp.Height); y++)
                for (int x = (int)(Box.Left * bmp.Width); x < (int)(Box.Right * bmp.Width); x++)
                {
                    int i = (y * bmp.Width + x) * 4;
                    if (bmp.Bgra[i] + bmp.Bgra[i + 1] + bmp.Bgra[i + 2] < 600) right = Math.Max(right, x);
                }
            return right;
        }
        int helvetica = await Width(TextFont.Helvetica), courier = await Width(TextFont.Courier);
        Assert.True(courier > helvetica * 1.5, $"Courier {courier} vs Helvetica {helvetica}");
    }

    [Fact]
    public async Task Text_box_wraps_long_text_and_breaks_long_words_inside_the_box()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddTextBoxAsync(0, Box, string.Join(" ", Enumerable.Repeat("word", 30)) + " " + new string('w', 80), new ShapeStyle(null, FontSize: 14));
        var bmp = await Render(doc);
        Assert.True(InkIn(bmp, new NormRect(0.2f, 0.56f, 0.6f, 0.6f)) > 50, "a later line is drawn");
        Assert.Equal(0, InkIn(bmp, new NormRect(0.61f, 0.5f, 0.9f, 0.6f)));
    }

    [Fact]
    public async Task Restyle_changes_colors_and_removes_the_fill()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddBoxAsync(0, ShapeKind.Rectangle, Box, new ShapeStyle(Red, Blue, 2));
        var style = new ShapeStyle(Blue, null, 4);
        await doc.RestyleShapeAsync(Assert.Single(await doc.GetShapesAsync(0)), style);
        var bmp = await Render(doc);
        Assert.True(IsBlue(Pixel(bmp, 0.4, 0.5 + 2.0 / 792)), "blue top edge");
        Assert.True(IsWhite(Pixel(bmp, 0.4, 0.6)), "empty middle");
        Assert.Equal(style, Assert.Single(await doc.GetShapesAsync(0)).Style);
    }

    [Fact]
    public async Task Restyle_keeps_a_text_box_text_and_note_text()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddTextBoxAsync(0, Box, "Keep me", new ShapeStyle(null, FontSize: 10));
        await doc.AddNoteAsync(0, 0.8, 0.1, "A note", Yellow);
        var shapes = await doc.GetShapesAsync(0);
        int small = InkIn(await Render(doc), Box);
        await doc.RestyleShapeAsync(shapes[0], new ShapeStyle(null, FontSize: 30));
        Assert.True(InkIn(await Render(doc), Box) > small * 2, "bigger text");
        shapes = await doc.GetShapesAsync(0);
        await doc.RestyleShapeAsync(shapes[0], new ShapeStyle(null, Blue));
        shapes = await doc.GetShapesAsync(0);
        Assert.Equal(["Keep me", "A note"], shapes.Select(s => s.Text));
        Assert.Equal((ShapeKind.Note, Blue), (shapes[1].Kind, shapes[1].Style!.Fill!.Value));
    }

    [Fact]
    public async Task Moved_ink_keeps_its_color_and_width()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddInkAsync(0, [[(0.1, 0.2), (0.2, 0.25), (0.3, 0.2), (0.4, 0.25)]], new ShapeStyle(Blue, LineWidth: 6));
        var ink = Assert.Single(await doc.GetShapesAsync(0));
        await doc.MoveShapeAsync(ink, new NormRect(ink.Rect.Left, ink.Rect.Top + 0.4f, ink.Rect.Right, ink.Rect.Bottom + 0.4f));
        var bmp = await Render(doc);
        Assert.True(IsBlue(Pixel(bmp, 0.1 + 2.0 / 612, 0.6)), "start of the moved stroke");
        Assert.Equal(new ShapeStyle(Blue, LineWidth: 6), Assert.Single(await doc.GetShapesAsync(0)).Style);
    }

    [Fact]
    public async Task Render_can_leave_out_one_object_without_changing_the_document()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddBoxAsync(0, ShapeKind.Rectangle, Box, new ShapeStyle(null, Blue));
        await doc.SaveCopyAsync(Path.Combine(Path.GetTempPath(), $"monoview-hide-{Guid.NewGuid():N}.pdf"));
        bool modified = doc.IsModified;
        var page = new PixelRect(0, 0, 612, 792);
        var box = Assert.Single(await doc.GetShapesAsync(0));
        var hidden = await doc.RenderAsync(0, 612, 792, page, 0, hideAnnot: box.AnnotIndex);
        Assert.True(IsWhite(Pixel(hidden, 0.4, 0.6)), "left out");
        Assert.True(IsBlue(Pixel(await Render(doc), 0.4, 0.6)), "drawn again");
        Assert.Equal(modified, doc.IsModified);
    }

    [Fact]
    public async Task Object_added_over_a_form_field_is_drawn_on_top_of_it()
    {
        using var doc = await PdfDocument.OpenAsync(FormPdf.Path);
        var box = FormPdf.AgreeBox;
        double edgeX = box.Left + 0.6 / 612, middleY = (box.Top + box.Bottom) / 2; // on the check box's drawn border
        Assert.False(IsWhite(Pixel(await Render(doc), edgeX, middleY)), "the field's border shows first");
        await doc.AddBoxAsync(0, ShapeKind.Rectangle, new NormRect(box.Left - 0.02f, box.Top - 0.02f, box.Right + 0.02f, box.Bottom + 0.02f), new ShapeStyle(null, Blue));
        Assert.True(IsBlue(Pixel(await Render(doc), edgeX, middleY)), "the rectangle covers the field");
    }

    [Fact]
    public async Task Shapes_survive_save()
    {
        string path = Path.Combine(Path.GetTempPath(), $"monoview-shapes-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = await PdfDocument.OpenAsync(SamplePdf.Path))
            {
                await doc.AddBoxAsync(0, ShapeKind.Ellipse, Box, new ShapeStyle(Red, Blue, 2));
                await doc.AddInkAsync(0, [[(0.1, 0.2), (0.3, 0.25)]], new ShapeStyle(Red));
                await doc.AddTextBoxAsync(0, new NormRect(0.1f, 0.8f, 0.5f, 0.9f), "Saved text", new ShapeStyle(null, TextColor: Black));
                await doc.AddNoteAsync(0, 0.8, 0.1, "A note", Yellow);
                await doc.SaveCopyAsync(path);
                await doc.CloseAsync();
            }
            using var saved = await PdfDocument.OpenAsync(path);
            var shapes = await saved.GetShapesAsync(0);
            Assert.Equal([ShapeKind.Ellipse, ShapeKind.Ink, ShapeKind.TextBox, ShapeKind.Note], shapes.Select(s => s.Kind));
            Assert.Equal(new ShapeStyle(Red, Blue, 2), shapes[0].Style);
            Assert.Equal(("Saved text", Black), (shapes[2].Text, shapes[2].Style!.TextColor!.Value));
            Assert.True(IsBlue(Pixel(await Render(saved), 0.4, 0.6)), "saved file draws the ellipse");
            await saved.CloseAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
