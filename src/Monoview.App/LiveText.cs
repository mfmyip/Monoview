using System.Runtime.InteropServices.WindowsRuntime;
using Monoview.Engine;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Monoview.App;

/// <summary>Windows' built-in OCR as the engine's text recognizer, so scans and photos get selectable text.</summary>
public static class LiveText
{
    // Created on first use, so startup doesn't pay for it; null when Windows has no OCR language installed.
    private static readonly Lazy<OcrEngine?> Engine = new(OcrEngine.TryCreateFromUserProfileLanguages);
    private static readonly SemaphoreSlim OneAtATime = new(1);

    public static void Enable() =>
        PdfDocument.Recognizer = new TextRecognizer((int)OcrEngine.MaxImageDimension, bitmap => Task.Run(() => RecognizeAsync(bitmap)));

    private static async Task<IReadOnlyList<IReadOnlyList<RecognizedWord>>> RecognizeAsync(RenderedBitmap bitmap)
    {
        if (Engine.Value is not { } engine) return [];
        using var image = SoftwareBitmap.CreateCopyFromBuffer(bitmap.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8,
            bitmap.Width, bitmap.Height, BitmapAlphaMode.Premultiplied);
        await OneAtATime.WaitAsync();
        try
        {
            var result = await engine.RecognizeAsync(image);
            return result.Lines
                .Select(line => (IReadOnlyList<RecognizedWord>)line.Words
                    .Select(w => new RecognizedWord(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))
                    .ToList())
                .ToList();
        }
        finally
        {
            OneAtATime.Release();
        }
    }
}
