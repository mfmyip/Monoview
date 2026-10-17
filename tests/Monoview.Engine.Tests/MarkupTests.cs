namespace Monoview.Engine.Tests;

public class MarkupTests
{
    private static readonly MarkupColor Yellow = new(255, 230, 0);
    private static readonly MarkupColor Green = new(80, 220, 80);

    [Fact]
    public async Task Highlight_renders_and_survives_save()
    {
        string path = TempPath();
        try
        {
            using (var doc = await PdfDocument.OpenAsync(SamplePdf.Path))
            {
                var title = await TitleRects(doc);
                Assert.False(doc.IsModified);
                await doc.AddMarkupAsync(0, MarkupKind.Highlight, Yellow, title);
                Assert.True(doc.IsModified);

                var markup = Assert.Single(await doc.GetMarkupsAsync(0));
                Assert.Equal(MarkupKind.Highlight, markup.Kind);
                Assert.Equal(Yellow, markup.Color);
                Assert.InRange(markup.Rects[0].Left, title[0].Left - 0.01f, title[0].Left + 0.01f);
                Assert.True(YellowPixels(await doc.RenderAsync(0, 612, 792)) > 500);

                await doc.SaveCopyAsync(path);
                await doc.CloseAsync();
            }

            using var saved = await PdfDocument.OpenAsync(path);
            Assert.Single(await saved.GetMarkupsAsync(0));
            Assert.True(YellowPixels(await saved.RenderAsync(0, 612, 792)) > 500, "saved file shows the highlight");
            Assert.False(saved.IsModified);
            await saved.CloseAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Recolor_and_remove()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddMarkupAsync(0, MarkupKind.Highlight, Yellow, await TitleRects(doc));

        var markup = Assert.Single(await doc.GetMarkupsAsync(0));
        await doc.SetMarkupColorAsync(markup, Green);
        var recolored = Assert.Single(await doc.GetMarkupsAsync(0));
        Assert.Equal(Green, recolored.Color);
        Assert.Equal(0, YellowPixels(await doc.RenderAsync(0, 612, 792)));

        await doc.RemoveMarkupAsync(recolored);
        Assert.Empty(await doc.GetMarkupsAsync(0));
    }

    [Fact]
    public async Task Recolor_after_the_page_was_drawn()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddMarkupAsync(0, MarkupKind.Highlight, Yellow, await TitleRects(doc));
        Assert.NotEqual(0, YellowPixels(await doc.RenderAsync(0, 612, 792)));
        Assert.Equal(Yellow, Assert.Single(await doc.GetMarkupsAsync(0)).Color);

        await doc.SetMarkupColorAsync(Assert.Single(await doc.GetMarkupsAsync(0)), Green);
        Assert.Equal(0, YellowPixels(await doc.RenderAsync(0, 612, 792)));
        Assert.Equal(Green, Assert.Single(await doc.GetMarkupsAsync(0)).Color);
    }

    [Theory]
    [InlineData(MarkupKind.Underline)]
    [InlineData(MarkupKind.StrikeOut)]
    public async Task Color_reads_back_after_save(MarkupKind kind)
    {
        string path = TempPath();
        try
        {
            using (var doc = await PdfDocument.OpenAsync(SamplePdf.Path))
            {
                await doc.AddMarkupAsync(0, kind, Green, await TitleRects(doc));
                await doc.RenderAsync(0, 612, 792);
                await doc.SaveCopyAsync(path);
                await doc.CloseAsync();
            }
            using var saved = await PdfDocument.OpenAsync(path);
            Assert.Equal(Green, Assert.Single(await saved.GetMarkupsAsync(0)).Color);
            await saved.CloseAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(MarkupKind.Underline)]
    [InlineData(MarkupKind.StrikeOut)]
    public async Task Underline_and_strikeout_are_listed(MarkupKind kind)
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddMarkupAsync(0, kind, Green, await TitleRects(doc));
        Assert.Equal(kind, Assert.Single(await doc.GetMarkupsAsync(0)).Kind);
    }

    [Fact]
    public async Task Highlight_on_a_rotated_page_lines_up_with_the_text()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        var text = await doc.GetTextAsync(1);
        var rects = text.GetRects(0, "Rotated".Length);
        await doc.AddMarkupAsync(1, MarkupKind.Highlight, Yellow, rects);

        var stored = Assert.Single(await doc.GetMarkupsAsync(1)).Rects[0];
        Assert.InRange(stored.Left, rects[0].Left - 0.01f, rects[0].Left + 0.01f);
        Assert.InRange(stored.Top, rects[0].Top - 0.01f, rects[0].Top + 0.01f);
    }

    private static async Task<List<NormRect>> TitleRects(PdfDocument doc) =>
        (await doc.GetTextAsync(0)).GetRects(0, SamplePdf.Page1Title.Length);

    /// <summary>Clearly yellow pixels: strong red and green, weak blue.</summary>
    private static int YellowPixels(RenderedBitmap bmp)
    {
        int n = 0;
        for (int i = 0; i < bmp.Bgra.Length; i += 4)
            if (bmp.Bgra[i] < 120 && bmp.Bgra[i + 1] > 180 && bmp.Bgra[i + 2] > 200) n++;
        return n;
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"monoview-markup-{Guid.NewGuid():N}.pdf");
}
