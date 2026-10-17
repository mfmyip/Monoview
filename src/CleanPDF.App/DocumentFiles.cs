using CleanPDF.Engine;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace CleanPDF.App;

/// <summary>The file types CleanPDF opens, and the one way to open any of them.</summary>
public static class DocumentFiles
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];
    public static readonly string[] Extensions = [".pdf", .. ImageExtensions];

    public static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool CanOpen(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens a PDF, or a picture as a one-page PDF. Throws <see cref="PdfException"/> when it can't.</summary>
    public static async Task<PdfDocument> OpenAsync(string path, string? password = null)
    {
        if (!IsImage(path))
            return await PdfDocument.OpenAsync(path, password);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            return await PdfDocument.FromImageAsync(path, (int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight,
                pixels.DetachPixelData(), decoder.DpiX, decoder.DpiY);
        }
        catch (Exception ex) when (ex is not PdfException)
        {
            throw new PdfException($"Couldn't read {Path.GetFileName(path)} as a picture: {ex.Message}");
        }
    }
}
