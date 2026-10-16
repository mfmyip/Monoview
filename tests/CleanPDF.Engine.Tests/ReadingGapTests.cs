namespace CleanPDF.Engine.Tests;

public class ReadingGapTests
{
    private static Task<PdfDocument> OpenSample() => PdfDocument.OpenAsync(SamplePdf.Path);

    [Fact]
    public async Task Finds_page_links_and_web_links()
    {
        using var doc = await OpenSample();
        var links = await doc.GetLinksAsync(0);

        var jump = Assert.Single(links, l => l.TargetPage is not null);
        Assert.Equal(2, jump.TargetPage);
        // /XYZ 0 300 on the 400 pt tall page 3 is 100 pt from its top.
        Assert.Equal(0.25, jump.TargetY!.Value, 2);
        Assert.InRange(jump.Rects[0].Left, 72 / 612f - 0.01f, 72 / 612f + 0.01f);

        Assert.Contains(links, l => l.Uri == "https://cleanpdf.example/docs");
        var web = Assert.Single(links, l => l.Uri == "https://example.com/help");
        Assert.NotEmpty(web.Rects);
    }

    [Fact]
    public async Task Pages_without_links_have_none()
    {
        using var doc = await OpenSample();
        Assert.Empty(await doc.GetLinksAsync(2));
    }

    [Fact]
    public async Task Password_protected_pdf_needs_the_password()
    {
        string path = EncryptedPdf.Write(Path.Combine(Path.GetTempPath(), $"cleanpdf-locked-{Guid.NewGuid():N}.pdf"));
        try
        {
            var missing = await Assert.ThrowsAsync<PdfPasswordException>(() => PdfDocument.OpenAsync(path));
            Assert.Contains("password", missing.Message);
            await Assert.ThrowsAsync<PdfPasswordException>(() => PdfDocument.OpenAsync(path, "wrong"));

            using var doc = await PdfDocument.OpenAsync(path, EncryptedPdf.Password);
            Assert.Contains(EncryptedPdf.Text, (await doc.GetTextAsync(0)).Text);
            Assert.True((await doc.GetPropertiesAsync()).Encrypted);
        }
        finally
        {
            // Dispose closes the file on the PDFium thread without waiting, so the delete can race it.
            for (int i = 0; i < 50 && File.Exists(path); i++)
            {
                try { File.Delete(path); }
                catch (IOException) { await Task.Delay(20); }
            }
        }
    }

    [Theory]
    [InlineData("hello", false, false, 2)]
    [InlineData("hello", true, false, 1)]      // only "hello again" on page 3 is lower case
    [InlineData("Hello", true, false, 1)]
    [InlineData("cafe", true, false, 0)]       // match case also stops ignoring accents
    [InlineData("the", false, true, 3)]        // "The quick", "over the", "at the"
    [InlineData("the", true, true, 2)]
    [InlineData("Clean", false, true, 0)]      // part of "CleanPDF"
    [InlineData("CleanPDF", false, true, 1)]
    public async Task Search_options(string query, bool matchCase, bool wholeWord, int expected)
    {
        using var doc = await OpenSample();
        var hits = new List<SearchHit>();
        await doc.SearchAsync(query, hits.AddRange, options: new SearchOptions(matchCase, wholeWord));
        Assert.Equal(expected, hits.Count);
    }
}
