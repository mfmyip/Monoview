using System.Runtime.InteropServices.WindowsRuntime;
using CleanPDF.Engine;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CleanPDF.App.Viewer;

internal static class Bitmaps
{
    /// <summary>Wraps rendered pixels in a bitmap XAML can show. Must run on the UI thread.</summary>
    public static WriteableBitmap ToBitmap(RenderedBitmap rendered)
    {
        var bmp = new WriteableBitmap(rendered.Width, rendered.Height);
        rendered.Bgra.CopyTo(bmp.PixelBuffer);
        bmp.Invalidate();
        return bmp;
    }
}

/// <summary>
/// Low-resolution renders of whole pages, shared by the viewer (shown while sharp tiles load)
/// and the thumbnail sidebar. UI thread only.
/// </summary>
public sealed class PreviewCache(PdfDocument doc)
{
    public const int PixelWidth = 280;

    private readonly LruCache<int, WriteableBitmap> _cache = new(200);
    private readonly Dictionary<int, Task<WriteableBitmap?>> _inFlight = new();

    public bool TryGet(int page, out WriteableBitmap bitmap) => _cache.TryGet(page, out bitmap);

    public Task<WriteableBitmap?> GetAsync(int page, WorkPriority priority)
    {
        if (_cache.TryGet(page, out var cached))
            return Task.FromResult<WriteableBitmap?>(cached);
        if (_inFlight.TryGetValue(page, out var pending))
            return pending;

        var task = LoadAsync(page, priority);
        _inFlight[page] = task;
        return task;
    }

    private async Task<WriteableBitmap?> LoadAsync(int page, WorkPriority priority)
    {
        try
        {
            var size = doc.PageSizes[page];
            double scale = PixelWidth / Math.Max(size.Width, size.Height * 0.75);
            int w = Math.Max(1, (int)Math.Round(size.Width * scale));
            int h = Math.Max(1, (int)Math.Round(size.Height * scale));
            var rendered = await doc.RenderAsync(page, w, h, priority);
            var bmp = Bitmaps.ToBitmap(rendered);
            _cache.Add(page, bmp);
            return bmp;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or PdfException)
        {
            return null;
        }
        finally
        {
            _inFlight.Remove(page);
        }
    }
}
