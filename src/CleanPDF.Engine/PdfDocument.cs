using System.Runtime.InteropServices;
using PDFiumCore;

namespace CleanPDF.Engine;

/// <summary>
/// Thin wrapper over a PDFium document. PDFium is not thread-safe, so every native
/// call goes through <see cref="PdfiumLock"/>. M1 replaces this with a dedicated
/// render thread and queue.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private const int BitmapFormatBgra = 4; // FPDFBitmap_BGRA
    private const int RenderFlagAnnot = 0x01; // FPDF_ANNOT
    private const int RenderFlagLcdText = 0x02; // FPDF_LCD_TEXT

    internal static readonly object PdfiumLock = new();
    private static bool _initialized;

    private FpdfDocumentT? _doc;

    public int PageCount { get; }

    private PdfDocument(FpdfDocumentT doc)
    {
        _doc = doc;
        PageCount = fpdfview.FPDF_GetPageCount(doc);
    }

    public static PdfDocument Open(string path, string? password = null)
    {
        lock (PdfiumLock)
        {
            EnsureInitialized();
            var doc = fpdfview.FPDF_LoadDocument(path, password);
            if (doc is null || doc.__Instance == IntPtr.Zero)
                throw new PdfException($"Could not open '{path}' (PDFium error {fpdfview.FPDF_GetLastError()}).");
            return new PdfDocument(doc);
        }
    }

    /// <summary>Page size in PDF points (1/72 inch).</summary>
    public (double Width, double Height) GetPageSize(int index)
    {
        lock (PdfiumLock)
        {
            var page = LoadPage(index);
            try
            {
                return (fpdfview.FPDF_GetPageWidthF(page), fpdfview.FPDF_GetPageHeightF(page));
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// Renders a page to premultiplied BGRA pixels (the format WinUI's WriteableBitmap expects).
    /// </summary>
    public RenderedPage RenderPage(int index, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "Size must be positive.");

        lock (PdfiumLock)
        {
            var page = LoadPage(index);
            var bitmap = fpdfview.FPDFBitmapCreateEx(pixelWidth, pixelHeight, BitmapFormatBgra, IntPtr.Zero, 0);
            try
            {
                fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, pixelWidth, pixelHeight, 0xFFFFFFFF);
                fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, pixelWidth, pixelHeight, 0,
                    RenderFlagAnnot | RenderFlagLcdText);

                int stride = fpdfview.FPDFBitmapGetStride(bitmap);
                var pixels = new byte[pixelWidth * 4 * pixelHeight];
                var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
                for (int y = 0; y < pixelHeight; y++)
                    Marshal.Copy(buffer + y * stride, pixels, y * pixelWidth * 4, pixelWidth * 4);

                return new RenderedPage(pixelWidth, pixelHeight, pixels);
            }
            finally
            {
                fpdfview.FPDFBitmapDestroy(bitmap);
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    public void Dispose()
    {
        lock (PdfiumLock)
        {
            if (_doc is null) return;
            fpdfview.FPDF_CloseDocument(_doc);
            _doc = null;
        }
    }

    private FpdfPageT LoadPage(int index)
    {
        ObjectDisposedException.ThrowIf(_doc is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, PageCount);

        var page = fpdfview.FPDF_LoadPage(_doc, index);
        if (page is null || page.__Instance == IntPtr.Zero)
            throw new PdfException($"Could not load page {index} (PDFium error {fpdfview.FPDF_GetLastError()}).");
        return page;
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        fpdfview.FPDF_InitLibrary();
        _initialized = true;
    }
}

public sealed record RenderedPage(int Width, int Height, byte[] Bgra);

public sealed class PdfException(string message) : Exception(message);
