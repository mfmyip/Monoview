namespace CleanPDF.Engine.Tests;

public class SaveTests
{
    [Fact]
    public async Task Saved_copy_has_the_same_pages_text_links_and_outline()
    {
        string path = TempPath();
        try
        {
            using (var doc = await PdfDocument.OpenAsync(SamplePdf.Path))
                await doc.SaveCopyAsync(path);

            using var copy = await PdfDocument.OpenAsync(path);
            Assert.Equal(3, copy.PageCount);
            Assert.Equal(new PageSize(792, 612), copy.PageSizes[1]);
            Assert.StartsWith(SamplePdf.Page1Title, (await copy.GetTextAsync(0)).Text);
            Assert.Contains(await copy.GetLinksAsync(0), l => l.TargetPage == 2);
            Assert.Equal(2, (await copy.GetOutlineAsync()).Count);
            await copy.CloseAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Saving_keeps_password_protection()
    {
        string locked = EncryptedPdf.Write(TempPath());
        string path = TempPath();
        try
        {
            using (var doc = await PdfDocument.OpenAsync(locked, EncryptedPdf.Password))
            {
                await doc.SaveCopyAsync(path);
                await doc.CloseAsync();
            }

            await Assert.ThrowsAsync<PdfPasswordException>(() => PdfDocument.OpenAsync(path));
            using var copy = await PdfDocument.OpenAsync(path, EncryptedPdf.Password);
            Assert.Contains(EncryptedPdf.Text, (await copy.GetTextAsync(0)).Text);
            await copy.CloseAsync();
        }
        finally
        {
            File.Delete(locked);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CloseAsync_releases_the_file()
    {
        string path = TempPath();
        File.Copy(SamplePdf.Path, path);
        var doc = await PdfDocument.OpenAsync(path);
        await doc.GetTextAsync(0);
        await doc.CloseAsync();
        File.Delete(path); // throws if PDFium still has the file open
        Assert.False(File.Exists(path));
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"cleanpdf-save-{Guid.NewGuid():N}.pdf");
}
