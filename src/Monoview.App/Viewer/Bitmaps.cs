using System.Runtime.InteropServices.WindowsRuntime;
using Monoview.Engine;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Monoview.App.Viewer;

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

    private PdfDocument _doc = doc;

    /// <summary>The document previews come from; setting it drops them all and redraws each one on request.</summary>
    public PdfDocument Document
    {
        get => _doc;
        set
        {
            _doc = value;
            _cache.Clear();
            _inFlight.Clear();
            DocumentReplaced?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? DocumentReplaced;

    private readonly LruCache<int, WriteableBitmap> _cache = new(200);
    private readonly Dictionary<int, Task<WriteableBitmap?>> _inFlight = new();
    private int _rotation;

    /// <summary>View rotation (quarter turns clockwise); changing it drops cached previews.</summary>
    public int Rotation
    {
        get => _rotation;
        set
        {
            value = Monoview.Engine.Rotation.Normalize(value);
            if (value == _rotation) return;
            _rotation = value;
            _cache.Clear();
            _inFlight.Clear();
            RotationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? RotationChanged;

    /// <summary>The page changed; render its preview again next time.</summary>
    public void Invalidate(int page)
    {
        _cache.Remove(page);
        _inFlight.Remove(page);
        PageInvalidated?.Invoke(this, page);
    }

    public event EventHandler<int>? PageInvalidated;

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
        int rotation = _rotation;
        var source = _doc;
        try
        {
            var size = Monoview.Engine.Rotation.Apply(source.PageSizes[page], rotation);
            double scale = PixelWidth / Math.Max(size.Width, size.Height * 0.75);
            int w = Math.Max(1, (int)Math.Round(size.Width * scale));
            int h = Math.Max(1, (int)Math.Round(size.Height * scale));
            var rendered = await source.RenderAsync(page, w, h, new PixelRect(0, 0, w, h), rotation, priority);
            var bmp = Bitmaps.ToBitmap(rendered);
            if (rotation != _rotation || source != _doc) return null; // rotated or replaced while rendering
            _cache.Add(page, bmp);
            return bmp;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or PdfException)
        {
            return null;
        }
        finally
        {
            if (rotation == _rotation && source == _doc) _inFlight.Remove(page);
        }
    }
}
