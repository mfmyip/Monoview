namespace CleanPDF.Engine.Tests;

public class ImageTests
{
    [Fact]
    public async Task Picture_opens_as_one_page_sized_from_its_dpi()
    {
        // 200 x 100 pixels: left half red, right half blue.
        const int width = 200, height = 100;
        var bgra = new byte[width * 4 * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool left = x < width / 2;
                bgra[i] = (byte)(left ? 0 : 255);
                bgra[i + 2] = (byte)(left ? 255 : 0);
                bgra[i + 3] = 255;
            }

        using var doc = await PdfDocument.FromImageAsync(@"C:\pictures\photo.png", width, height, bgra, 144, 144);
        Assert.Equal(1, doc.PageCount);
        Assert.Equal(100, doc.PageSizes[0].Width, 1);
        Assert.Equal(50, doc.PageSizes[0].Height, 1);
        Assert.Equal(@"C:\pictures\photo.png", doc.Path);
        Assert.False(doc.IsModified);

        var page = await doc.RenderAsync(0, width, height);
        int Pixel(int x, int y) => (y * page.Width + x) * 4;
        Assert.True(page.Bgra[Pixel(20, 50) + 2] > 200 && page.Bgra[Pixel(20, 50)] < 50, "left half is red");
        Assert.True(page.Bgra[Pixel(180, 50)] > 200 && page.Bgra[Pixel(180, 50) + 2] < 50, "right half is blue");
    }
}
