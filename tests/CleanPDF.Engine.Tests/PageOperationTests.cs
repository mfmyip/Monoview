namespace CleanPDF.Engine.Tests;

public class PageOperationTests
{
    // Sample pages: 0 "Hello CleanPDF" (612x792), 1 "Rotated page" (shown 792x612), 2 "hello again" (300x400).
    private static Task<PdfDocument> OpenSample() => PdfDocument.OpenAsync(SamplePdf.Path);

    private static async Task<string[]> FirstWords(PdfDocument doc)
    {
        var words = new string[doc.PageCount];
        for (int i = 0; i < doc.PageCount; i++)
            words[i] = (await doc.GetTextAsync(i)).Text.Split(' ')[0];
        return words;
    }

    [Fact]
    public async Task Deletes_pages()
    {
        using var doc = await OpenSample();
        await doc.DeletePagesAsync([1]);
        Assert.Equal(["Hello", "hello"], await FirstWords(doc));
        Assert.Equal(new PageSize(300, 400), doc.PageSizes[1]);
        Assert.True(doc.IsModified);
    }

    [Fact]
    public async Task Refuses_to_delete_every_page()
    {
        using var doc = await OpenSample();
        await Assert.ThrowsAsync<PdfException>(() => doc.DeletePagesAsync([0, 1, 2]));
        Assert.Equal(3, doc.PageCount);
    }

    [Theory]
    [InlineData(new[] { 2 }, 0, new[] { "hello", "Hello", "Rotated" })]
    [InlineData(new[] { 0 }, 2, new[] { "Rotated", "hello", "Hello" })]
    [InlineData(new[] { 0, 2 }, 1, new[] { "Rotated", "Hello", "hello" })]
    public async Task Moves_pages(int[] pages, int destination, string[] expected)
    {
        using var doc = await OpenSample();
        await doc.MovePagesAsync(pages, destination);
        Assert.Equal(expected, await FirstWords(doc));
    }

    [Fact]
    public async Task Rotation_is_saved_with_the_page()
    {
        string path = TempPath();
        try
        {
            using (var doc = await OpenSample())
            {
                await doc.RotatePagesAsync([0], 1);
                Assert.Equal(new PageSize(792, 612), doc.PageSizes[0]);
                await doc.SaveCopyAsync(path);
                await doc.CloseAsync();
            }
            using var saved = await PdfDocument.OpenAsync(path);
            Assert.Equal(new PageSize(792, 612), saved.PageSizes[0]);
            await saved.CloseAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Inserts_another_pdf_and_extracts_pages()
    {
        string extracted = TempPath();
        try
        {
            using var doc = await OpenSample();
            using (var source = await PdfDocument.OpenAsync(SamplePdf.Path))
                Assert.Equal(3, await doc.InsertPagesFromAsync(source, 1));
            Assert.Equal(["Hello", "Hello", "Rotated", "hello", "Rotated", "hello"], await FirstWords(doc));

            await doc.ExtractPagesAsync([5, 0], extracted);
            using var part = await PdfDocument.OpenAsync(extracted);
            Assert.Equal(["hello", "Hello"], await FirstWords(part));
            await part.CloseAsync();
        }
        finally
        {
            File.Delete(extracted);
        }
    }

    [Fact]
    public async Task Inserts_a_password_protected_pdf_opened_with_its_password()
    {
        string locked = EncryptedPdf.Write(TempPath());
        try
        {
            using var doc = await OpenSample();
            using (var source = await PdfDocument.OpenAsync(locked, EncryptedPdf.Password))
                Assert.Equal(1, await doc.InsertPagesFromAsync(source, 0));
            Assert.Equal(4, doc.PageCount);
            Assert.StartsWith(EncryptedPdf.Text, (await doc.GetTextAsync(0)).Text);
        }
        finally
        {
            File.Delete(locked);
        }
    }

    [Fact]
    public async Task Copies_chosen_pages_from_another_document_in_the_order_given()
    {
        using var doc = await OpenSample();
        using var other = await OpenSample();
        Assert.Equal(2, await doc.InsertPagesFromAsync(other, 3, [2, 0]));
        Assert.Equal(["Hello", "Rotated", "hello", "hello", "Hello"], await FirstWords(doc));
        Assert.Equal(3, other.PageCount);
    }

    [Fact]
    public async Task Inserts_a_picture_as_a_page()
    {
        using var doc = await OpenSample();
        var white = Enumerable.Repeat((byte)255, 40 * 4 * 20).ToArray();
        using (var picture = await PdfDocument.FromImageAsync(@"C:\pictures\note.png", 40, 20, white, 72, 72))
            Assert.Equal(1, await doc.InsertPagesFromAsync(picture, 3));
        Assert.Equal(4, doc.PageCount);
        Assert.Equal(40, doc.PageSizes[3].Width, 1);
    }

    [Fact]
    public async Task Snapshot_restores_an_earlier_state()
    {
        using var doc = await OpenSample();
        byte[] before = await doc.SaveToBytesAsync();
        await doc.DeletePagesAsync([0]);
        Assert.Equal(2, doc.PageCount);

        using var restored = await PdfDocument.OpenMemoryAsync(before, doc.Path, null, modified: false);
        Assert.Equal(3, restored.PageCount);
        Assert.Equal(["Hello", "Rotated", "hello"], await FirstWords(restored));
        Assert.Equal(doc.Path, restored.Path);
        Assert.False(restored.IsModified);
        await restored.CloseAsync();
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"cleanpdf-pages-{Guid.NewGuid():N}.pdf");
}
