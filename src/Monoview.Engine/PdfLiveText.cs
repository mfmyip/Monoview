using System.Text;

namespace Monoview.Engine;

/// <summary>A word found by text recognition, with its box in pixels of the bitmap it was found in.</summary>
public sealed record RecognizedWord(string Text, double X, double Y, double Width, double Height);

/// <summary>
/// Reads text out of a rendered page. The app supplies it (Windows OCR); the engine has no OS dependency.
/// <paramref name="MaxPixels"/> is the longest side the recognizer accepts.
/// </summary>
public sealed record TextRecognizer(int MaxPixels, Func<RenderedBitmap, Task<IReadOnlyList<IReadOnlyList<RecognizedWord>>>> Recognize);

public sealed partial class PdfDocument
{
    // Pages are rendered at this resolution for recognition, unless that passes MaxPixels.
    private const double RecognitionDpi = 300;

    /// <summary>
    /// When set, pages with no text (scans, photos) get their text from this, like Live Text in Preview.
    /// The result is kept in memory only; the file is not changed.
    /// </summary>
    public static TextRecognizer? Recognizer { get; set; }

    // Recognition per page, finished or running. Cleared when pages change.
    private readonly Dictionary<int, Task<PageText>> _recognized = new();

    private static bool HasText(PageText text) => text.Text.Any(c => !char.IsWhiteSpace(c) && c != '\0');

    public async Task<PageText> GetTextAsync(int pageIndex, WorkPriority priority = WorkPriority.High, CancellationToken ct = default)
    {
        var text = await PdfiumThread.Run(() => GetText(pageIndex), priority, ct);
        if (HasText(text) || Recognizer is not { } recognizer) return text;

        Task<PageText> task;
        lock (_recognized)
        {
            if (!_recognized.TryGetValue(pageIndex, out task!) || task.IsFaulted || task.IsCanceled)
                _recognized[pageIndex] = task = RecognizeAsync(pageIndex, recognizer, priority);
        }
        return await task.WaitAsync(ct);
    }

    private async Task<PageText> RecognizeAsync(int pageIndex, TextRecognizer recognizer, WorkPriority priority)
    {
        var size = _pageSizes[pageIndex];
        double scale = Math.Min(RecognitionDpi / 72, recognizer.MaxPixels / Math.Max(size.Width, size.Height));
        int width = Math.Max(1, (int)(size.Width * scale));
        int height = Math.Max(1, (int)(size.Height * scale));
        var bitmap = await RenderAsync(pageIndex, width, height, priority).ConfigureAwait(false);
        var lines = await recognizer.Recognize(bitmap).ConfigureAwait(false);
        return BuildText(pageIndex, lines, width, height);
    }

    /// <summary>
    /// Lays recognized words out like PDFium text: words split by spaces, lines by "\r\n", and one box per
    /// character (the word's box split evenly), so selection, copy and search work unchanged.
    /// </summary>
    internal static PageText BuildText(int pageIndex, IReadOnlyList<IReadOnlyList<RecognizedWord>> lines, int width, int height)
    {
        var sb = new StringBuilder();
        var boxes = new List<NormRect>();
        foreach (var line in lines)
        {
            if (line.Count == 0) continue;
            if (sb.Length > 0)
            {
                sb.Append("\r\n");
                boxes.Add(default);
                boxes.Add(default);
            }
            for (int w = 0; w < line.Count; w++)
            {
                var word = line[w];
                if (w > 0)
                {
                    sb.Append(' ');
                    boxes.Add(default);
                }
                double step = word.Width / Math.Max(1, word.Text.Length);
                for (int i = 0; i < word.Text.Length; i++)
                {
                    sb.Append(word.Text[i]);
                    double left = word.X + step * i;
                    boxes.Add(new NormRect((float)(left / width), (float)(word.Y / height),
                        (float)((left + step) / width), (float)((word.Y + word.Height) / height)));
                }
            }
        }
        return new PageText(pageIndex, sb.ToString(), boxes.ToArray());
    }

    private void ForgetRecognizedText()
    {
        lock (_recognized) _recognized.Clear();
    }
}
