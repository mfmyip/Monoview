namespace Monoview.Engine.Tests;

// PdfDocument.Recognizer is static, so these run apart from every other test.
[CollectionDefinition(nameof(LiveTextTests), DisableParallelization = true)]
public class LiveTextCollection;

[Collection(nameof(LiveTextTests))]
public sealed class LiveTextTests : IDisposable
{
    private int _calls;

    public LiveTextTests()
    {
        // Two lines, placed as fractions of whatever size the page was rendered at.
        PdfDocument.Recognizer = new TextRecognizer(4000, bitmap =>
        {
            Interlocked.Increment(ref _calls);
            double w = bitmap.Width, h = bitmap.Height;
            IReadOnlyList<IReadOnlyList<RecognizedWord>> lines =
            [
                [new("Hello", w * 0.1, h * 0.1, w * 0.2, h * 0.1), new("world", w * 0.4, h * 0.1, w * 0.2, h * 0.1)],
                [new("Second", w * 0.1, h * 0.5, w * 0.3, h * 0.1)],
            ];
            return Task.FromResult(lines);
        });
    }

    public void Dispose() => PdfDocument.Recognizer = null;

    private static Task<PdfDocument> OpenPicture()
    {
        const int width = 400, height = 200;
        var white = Enumerable.Repeat((byte)255, width * 4 * height).ToArray();
        return PdfDocument.FromImageAsync(@"C:\pictures\scan.png", width, height, white, 96, 96);
    }

    [Fact]
    public async Task Page_without_text_gets_recognized_text_laid_out_like_pdf_text()
    {
        using var doc = await OpenPicture();
        var text = await doc.GetTextAsync(0);

        Assert.Equal("Hello world\r\nSecond", text.Text);
        var world = Assert.Single(text.GetRects(6, 5));
        Assert.Equal(0.4f, world.Left, 3);
        Assert.Equal(0.6f, world.Right, 3);
        Assert.Equal(0.1f, world.Top, 3);
        Assert.Equal(0.2f, world.Bottom, 3);
        Assert.Equal((6, 5), text.WordAt(8));
    }

    [Fact]
    public async Task Search_and_copy_find_recognized_text()
    {
        using var doc = await OpenPicture();
        var hits = new List<SearchHit>();
        await doc.SearchAsync("world second", hits.AddRange);

        var hit = Assert.Single(hits);
        Assert.Equal(6, hit.Start);
        Assert.Equal("world\r\nSecond", await doc.GetTextRangeAsync(0, hit.Start, 0, hit.Start + hit.Length));
    }

    [Fact]
    public async Task Each_page_is_recognized_once()
    {
        using var doc = await OpenPicture();
        await Task.WhenAll(doc.GetTextAsync(0), doc.GetTextAsync(0));
        await doc.GetTextAsync(0);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task Page_with_real_text_is_not_recognized()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        var text = await doc.GetTextAsync(0);
        Assert.StartsWith(SamplePdf.Page1Title, text.Text);
        Assert.Equal(0, _calls);
    }
}
