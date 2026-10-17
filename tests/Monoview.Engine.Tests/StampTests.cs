namespace Monoview.Engine.Tests;

public class StampTests
{
    // 40 x 20 pixels: top half red, bottom half blue, left quarter fully transparent.
    private const int W = 40, H = 20;
    private static byte[] Picture()
    {
        var bgra = new byte[W * 4 * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                bool top = y < H / 2;
                bgra[i] = (byte)(top ? 0 : 255);
                bgra[i + 2] = (byte)(top ? 255 : 0);
                bgra[i + 3] = (byte)(x < W / 4 ? 0 : 255);
            }
        return bgra;
    }

    private static async Task<(byte B, byte G, byte R)> PixelAt(PdfDocument doc, int page, double nx, double ny)
    {
        var size = doc.PageSizes[page];
        int w = (int)size.Width, h = (int)size.Height;
        var bmp = await doc.RenderAsync(page, w, h);
        int i = ((int)(ny * h) * bmp.Width + (int)(nx * w)) * 4;
        return (bmp.Bgra[i], bmp.Bgra[i + 1], bmp.Bgra[i + 2]);
    }

    private static bool IsRed((byte B, byte G, byte R) p) => p.R > 200 && p.B < 80 && p.G < 80;
    private static bool IsBlue((byte B, byte G, byte R) p) => p.B > 200 && p.R < 80 && p.G < 80;
    private static bool IsWhite((byte B, byte G, byte R) p) => p.R > 240 && p.G > 240 && p.B > 240;

    [Fact]
    public async Task Places_a_picture_upright_with_transparency()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddStampAsync(0, new NormRect(0.2f, 0.4f, 0.6f, 0.5f), W, H, Picture());
        Assert.True(doc.IsModified);
        var stamp = Assert.Single(await doc.GetStampsAsync(0));
        Assert.InRange(stamp.Rect.Left, 0.19f, 0.21f);
        Assert.InRange(stamp.Rect.Bottom, 0.49f, 0.51f);

        Assert.True(IsRed(await PixelAt(doc, 0, 0.45, 0.42)), "top half red");
        Assert.True(IsBlue(await PixelAt(doc, 0, 0.45, 0.48)), "bottom half blue");
        Assert.True(IsWhite(await PixelAt(doc, 0, 0.22, 0.42)), "transparent quarter shows the page");
    }

    [Fact]
    public async Task Stays_upright_on_a_rotated_page()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddStampAsync(1, new NormRect(0.2f, 0.4f, 0.6f, 0.5f), W, H, Picture());
        Assert.True(IsRed(await PixelAt(doc, 1, 0.45, 0.42)), "red stays on top");
        Assert.True(IsBlue(await PixelAt(doc, 1, 0.45, 0.48)), "blue stays below");
    }

    [Fact]
    public async Task Grows_in_place()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        await doc.AddStampAsync(0, new NormRect(0.4f, 0.4f, 0.6f, 0.45f), W, H, Picture());
        await doc.RenderAsync(0, 612, 792);
        await doc.MoveStampAsync(Assert.Single(await doc.GetStampsAsync(0)), new NormRect(0.2f, 0.3f, 0.8f, 0.6f));
        Assert.True(IsRed(await PixelAt(doc, 0, 0.7, 0.35)), "top half fills the larger rect");
        Assert.True(IsBlue(await PixelAt(doc, 0, 0.7, 0.55)), "bottom half fills the larger rect");
    }

    [Fact]
    public async Task Moves_and_resizes_and_survives_save()
    {
        string path = Path.Combine(Path.GetTempPath(), $"monoview-stamp-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = await PdfDocument.OpenAsync(SamplePdf.Path))
            {
                await doc.AddStampAsync(0, new NormRect(0.2f, 0.4f, 0.6f, 0.5f), W, H, Picture());
                await doc.RenderAsync(0, 612, 792); // builds the appearance before the move
                await doc.MoveStampAsync(Assert.Single(await doc.GetStampsAsync(0)), new NormRect(0.3f, 0.7f, 0.9f, 0.85f));
                Assert.True(IsWhite(await PixelAt(doc, 0, 0.45, 0.42)), "old place is empty");
                Assert.True(IsRed(await PixelAt(doc, 0, 0.7, 0.73)), "new place, top half");
                Assert.True(IsBlue(await PixelAt(doc, 0, 0.7, 0.82)), "new place, bottom half");
                await doc.SaveCopyAsync(path);
                await doc.CloseAsync();
            }

            using var saved = await PdfDocument.OpenAsync(path);
            var stamp = Assert.Single(await saved.GetStampsAsync(0));
            Assert.InRange(stamp.Rect.Top, 0.69f, 0.71f);
            Assert.True(IsRed(await PixelAt(saved, 0, 0.7, 0.73)), "saved file shows the picture");
            await saved.RemoveStampAsync(stamp);
            Assert.Empty(await saved.GetStampsAsync(0));
            await saved.CloseAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
