namespace CleanPDF.Engine.Tests;

public class QuickWinTests
{
    private static Task<PdfDocument> OpenSample() => PdfDocument.OpenAsync(SamplePdf.Path);

    [Fact]
    public async Task Reads_document_properties()
    {
        using var doc = await OpenSample();
        var props = await doc.GetPropertiesAsync();

        Assert.Equal("CleanPDF sample", props.Title);
        Assert.Equal("Engine tests", props.Author);
        Assert.Null(props.Subject);
        Assert.Equal("1.7", props.PdfVersion);
        Assert.False(props.Encrypted);
        Assert.Equal(3, props.PageCount);
        Assert.Equal(new PageSize(612, 792), props.FirstPageSize);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)), props.Created);
        Assert.True(props.FileSize > 0);
    }

    [Theory]
    [InlineData("D:2023", 2023, 1, 1, 0)]
    [InlineData("D:20230808142530Z", 2023, 8, 8, 0)]
    [InlineData("D:20230808142530-05'00'", 2023, 8, 8, -5)]
    [InlineData("20230808", 2023, 8, 8, 0)]
    public void Parses_pdf_dates(string value, int year, int month, int day, int offsetHours)
    {
        var date = PdfDocument.ParsePdfDate(value);
        Assert.NotNull(date);
        Assert.Equal((year, month, day), (date!.Value.Year, date.Value.Month, date.Value.Day));
        Assert.Equal(TimeSpan.FromHours(offsetHours), date.Value.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("D:20231399")]
    public void Bad_pdf_dates_are_null(string? value) => Assert.Null(PdfDocument.ParsePdfDate(value));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Rotation_round_trips(int rotation)
    {
        var (vx, vy) = Rotation.ToView(0.2, 0.7, rotation);
        var (x, y) = Rotation.FromView(vx, vy, rotation);
        Assert.Equal(0.2, x, 6);
        Assert.Equal(0.7, y, 6);
    }

    [Fact]
    public void Quarter_turn_moves_top_left_to_top_right() => Assert.Equal((1.0, 0.0), Rotation.ToView(0, 0, 1));

    [Fact]
    public async Task Renders_with_view_rotation()
    {
        using var doc = await OpenSample();
        // Rotated a quarter turn clockwise, page 1 (612 x 792) is 792 x 612 and PDF point (x, y) lands
        // at pixel (y, x): the title (baseline y = 700, x from 72) runs down from (700, 72), its
        // letters extending right of the baseline.
        var bmp = await doc.RenderAsync(0, 792, 612, new PixelRect(0, 0, 792, 612), rotation: 1);
        Assert.True(DarkPixels(bmp, new PixelRect(700, 72, 20, 180)) > 100);
        Assert.Equal(0, DarkPixels(bmp, new PixelRect(50, 50, 100, 100)));
    }

    [Fact]
    public async Task Prints_to_microsoft_print_to_pdf()
    {
        const string printer = "Microsoft Print to PDF";
        IntPtr hdc = PdfPrinter.CreatePrinterDC(printer);
        if (hdc == IntPtr.Zero) return; // printer not installed on this machine

        string output = Path.Combine(Path.GetTempPath(), $"cleanpdf-print-{Guid.NewGuid():N}.pdf");
        try
        {
            using var doc = await OpenSample();
            var printed = new List<int>();
            await PdfPrinter.PrintAsync(doc, hdc, "CleanPDF test", [0, 2], output, new Progress<int>(printed.Add));

            Assert.Equal([1, 2], printed);
            using var result = await OpenWhenReadyAsync(output);
            Assert.Equal(2, result.PageCount);

            // PDFium prints text as outlines, so check where the ink is rather than the text.
            // Page 1 prints at 100% on Letter: the title is near the top-left, as on screen.
            var p0 = await result.RenderAsync(0, 612, 792);
            Assert.True(DarkPixels(p0, new PixelRect(60, 60, 200, 40)) > 100);
            // The 300 x 400 pt page is centered: "hello again" sits ~100 pt below its top.
            var p1 = await result.RenderAsync(1, 612, 792);
            Assert.True(DarkPixels(p1, new PixelRect(180, 260, 160, 50)) > 50);
            Assert.Equal(0, DarkPixels(p1, new PixelRect(0, 0, 100, 100)));
        }
        finally
        {
            PdfPrinter.DeletePrinterDC(hdc);
            for (int i = 0; i < 50 && File.Exists(output); i++)
            {
                try { File.Delete(output); }
                catch (IOException) { await Task.Delay(100); } // the spooler can hold it briefly
            }
        }
    }

    private static async Task<PdfDocument> OpenWhenReadyAsync(string path)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                return await PdfDocument.OpenAsync(path);
            }
            catch (PdfException) when (i < 50)
            {
                await Task.Delay(100); // the spooler writes the file after the job is submitted
            }
        }
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
