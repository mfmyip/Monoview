namespace CleanPDF.Engine.Tests;

public class PdfDocumentTests
{
    private static Task<PdfDocument> OpenSample() => PdfDocument.OpenAsync(SamplePdf.Path);

    [Fact]
    public async Task Opens_and_reports_displayed_page_sizes()
    {
        using var doc = await OpenSample();

        Assert.Equal(3, doc.PageCount);
        Assert.Equal(new PageSize(612, 792), doc.PageSizes[0]);
        Assert.Equal(new PageSize(792, 612), doc.PageSizes[1]); // /Rotate 90 swaps width and height
        Assert.Equal(new PageSize(300, 400), doc.PageSizes[2]);
    }

    [Fact]
    public async Task Open_throws_PdfException_for_a_non_pdf()
    {
        string path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "not a pdf");
        try
        {
            await Assert.ThrowsAsync<PdfException>(() => PdfDocument.OpenAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Renders_text_where_the_page_draws_it()
    {
        using var doc = await OpenSample();
        var bmp = await doc.RenderAsync(0, 612, 792);

        Assert.Equal(612 * 792 * 4, bmp.Bgra.Length);
        // The title's baseline is 92 px from the top; glyphs sit just above it.
        Assert.True(DarkPixels(bmp, new PixelRect(72, 72, 200, 20)) > 100);
        // A blank area stays white.
        Assert.Equal(0, DarkPixels(bmp, new PixelRect(400, 400, 100, 100)));
    }

    [Fact]
    public async Task A_tile_matches_the_same_region_of_a_full_render()
    {
        using var doc = await OpenSample();
        const int w = 900, h = 1165;
        var full = await doc.RenderAsync(0, w, h);
        var region = new PixelRect(90, 120, 256, 128);
        var tile = await doc.RenderAsync(0, w, h, region);

        int mismatched = 0;
        for (int y = 0; y < region.Height; y++)
            for (int x = 0; x < region.Width * 4; x++)
            {
                byte a = tile.Bgra[y * region.Width * 4 + x];
                byte b = full.Bgra[(region.Y + y) * w * 4 + region.X * 4 + x];
                if (Math.Abs(a - b) > 8) mismatched++;
            }
        Assert.True(mismatched < 10, $"{mismatched} bytes differ");
    }

    [Fact]
    public async Task Extracts_text_with_character_boxes_in_normalized_coordinates()
    {
        using var doc = await OpenSample();
        var text = await doc.GetTextAsync(0);

        Assert.StartsWith(SamplePdf.Page1Title, text.Text);
        var h = text.GetCharBox(0);
        Assert.InRange(h.Left, 72 / 612f - 0.01f, 72 / 612f + 0.01f);
        // The baseline is at y = 700 from the bottom, i.e. 92/792 from the top.
        Assert.InRange(h.Bottom, 92 / 792f - 0.01f, 92 / 792f + 0.01f);
        Assert.True(h.Top < h.Bottom);
    }

    [Fact]
    public async Task Character_boxes_follow_page_rotation()
    {
        using var doc = await OpenSample();
        var text = await doc.GetTextAsync(1);
        Assert.StartsWith("Rotated page", text.Text);

        // Rotated 90° clockwise, PDF point (x, y) shows at (y / 792, x / 612).
        var r = text.GetCharBox(0);
        Assert.InRange(r.Left, 700 / 792f - 0.01f, 700 / 792f + 0.01f);
        Assert.InRange(r.Top, 72 / 612f - 0.01f, 72 / 612f + 0.01f);
        Assert.True(r.Height > r.Width * 0.5f, "Glyph should run down the page");
    }

    [Fact]
    public async Task Caret_word_and_line_geometry()
    {
        using var doc = await OpenSample();
        var text = await doc.GetTextAsync(0);
        var h = text.GetCharBox(0);

        Assert.Equal(0, text.CaretAt(h.Left + h.Width * 0.2, (h.Top + h.Bottom) / 2));
        Assert.Equal(1, text.CaretAt(h.Left + h.Width * 0.8, (h.Top + h.Bottom) / 2));
        Assert.True(text.IsOverText((h.Left + h.Right) / 2, (h.Top + h.Bottom) / 2));
        Assert.False(text.IsOverText(0.9, 0.9));

        int clean = text.Text.IndexOf("CleanPDF", StringComparison.Ordinal);
        Assert.Equal((clean, 8), text.WordAt(clean + 3));

        Assert.Single(text.GetRects(0, SamplePdf.Page1Title.Length));
        int jumps = text.Text.IndexOf("jumps", StringComparison.Ordinal);
        int lazy = text.Text.IndexOf("lazy", StringComparison.Ordinal);
        Assert.Equal(2, text.GetRects(jumps, lazy + 4 - jumps).Count);
    }

    [Theory]
    [InlineData("hello", 2)]       // case-insensitive, two pages
    [InlineData("the lazy", 1)]    // across a line break
    [InlineData("cafe", 1)]        // accent-insensitive
    [InlineData("  QUICK   brown ", 1)]
    [InlineData("zebra", 0)]
    [InlineData("   ", 0)]
    public async Task Search_finds_matches(string query, int expected)
    {
        using var doc = await OpenSample();
        var hits = new List<SearchHit>();
        await doc.SearchAsync(query, hits.AddRange);
        Assert.Equal(expected, hits.Count);
    }

    [Fact]
    public async Task Search_hits_map_back_to_page_text()
    {
        using var doc = await OpenSample();
        var hits = new List<SearchHit>();
        await doc.SearchAsync("the lazy", hits.AddRange);

        var hit = Assert.Single(hits);
        var text = await doc.GetTextAsync(hit.PageIndex);
        string matched = text.Text.Substring(hit.Start, hit.Length);
        Assert.StartsWith("the", matched);
        Assert.EndsWith("lazy", matched);
        Assert.Equal("the lazy", hit.Snippet.Substring(hit.SnippetMatchStart, hit.SnippetMatchLength));
        Assert.Equal(2, text.GetRects(hit.Start, hit.Length).Count);
    }

    [Fact]
    public async Task Reads_the_outline()
    {
        using var doc = await OpenSample();
        var outline = await doc.GetOutlineAsync();

        Assert.Collection(outline,
            intro =>
            {
                Assert.Equal("Introduction", intro.Title);
                Assert.Equal(0, intro.PageIndex);
                Assert.Equal(792, intro.Y);
            },
            rotated =>
            {
                Assert.Equal("Rotated", rotated.Title);
                Assert.Equal(1, rotated.PageIndex);
                var child = Assert.Single(rotated.Children);
                Assert.Equal(("Small page", 2), (child.Title, child.PageIndex));
            });

        var (_, top) = await doc.PageToNormalizedAsync(0, 0, 792);
        Assert.Equal(0, top, 3);
    }

    [Fact]
    public async Task Copies_a_range_across_pages()
    {
        using var doc = await OpenSample();
        var p0 = await doc.GetTextAsync(0);
        string text = await doc.GetTextRangeAsync(0, p0.Text.IndexOf("Caf", StringComparison.Ordinal), 1, 7);

        Assert.StartsWith("Café.", text);
        Assert.EndsWith("Rotated", text);
    }

    [Fact]
    public async Task Concurrent_requests_all_complete()
    {
        using var doc = await OpenSample();
        var tasks = Enumerable.Range(0, 60).Select(i => i % 3 == 0
            ? (Task)doc.RenderAsync(i % 3, 200, 260, i % 2 == 0 ? WorkPriority.High : WorkPriority.Low)
            : doc.GetTextAsync(i % 3)).ToArray();
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Cancelled_work_is_skipped()
    {
        using var doc = await OpenSample();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doc.RenderAsync(0, 100, 100, ct: cts.Token));
    }

    [Fact]
    public async Task Calls_after_dispose_fail()
    {
        var doc = await OpenSample();
        doc.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => doc.GetTextAsync(0));
    }

    private static int DarkPixels(RenderedBitmap bmp, PixelRect r)
    {
        int n = 0;
        for (int y = r.Y; y < r.Y + r.Height; y++)
            for (int x = r.X; x < r.X + r.Width; x++)
            {
                int i = (y * bmp.Width + x) * 4;
                if (bmp.Bgra[i] + bmp.Bgra[i + 1] + bmp.Bgra[i + 2] < 300) n++;
            }
        return n;
    }
}
