using Monoview.App.Viewer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Monoview.App;

/// <summary>Saved signatures, one transparent PNG each in %LOCALAPPDATA%\Monoview\Signatures, newest first.</summary>
internal static class Signatures
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Monoview", "Signatures");

    public static IReadOnlyList<string> Files =>
        Directory.Exists(Folder)
            ? Directory.GetFiles(Folder, "*.png").OrderByDescending(File.GetCreationTimeUtc).ToList()
            : [];

    public static async Task SaveAsync(StampPicture picture)
    {
        Directory.CreateDirectory(Folder);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)picture.Width, (uint)picture.Height, 96, 96, picture.Bgra);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var file = File.Create(Path.Combine(Folder, $"signature-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png"));
        await stream.AsStreamForRead().CopyToAsync(file);
    }

    public static async Task<StampPicture> LoadAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        return new StampPicture((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight, pixels.DetachPixelData());
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The smallest picture that holds every visible pixel, with a small margin; null when nothing is visible.</summary>
    public static StampPicture? Trim(StampPicture picture)
    {
        int w = picture.Width, h = picture.Height, minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (picture.Bgra[(y * w + x) * 4 + 3] > 8)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        if (maxX < 0) return null;
        const int margin = 4;
        minX = Math.Max(0, minX - margin); minY = Math.Max(0, minY - margin);
        maxX = Math.Min(w - 1, maxX + margin); maxY = Math.Min(h - 1, maxY + margin);
        int tw = maxX - minX + 1, th = maxY - minY + 1;
        var trimmed = new byte[tw * th * 4];
        for (int y = 0; y < th; y++)
            Array.Copy(picture.Bgra, ((minY + y) * w + minX) * 4, trimmed, y * tw * 4, tw * 4);
        return new StampPicture(tw, th, trimmed);
    }
}
