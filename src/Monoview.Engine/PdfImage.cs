using System.Runtime.InteropServices;

namespace Monoview.Engine;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Opens a picture as a one-page PDF in memory, sized from its DPI. <paramref name="bgra"/> holds
    /// <paramref name="width"/> x <paramref name="height"/> straight-alpha BGRA pixels. <paramref name="path"/>
    /// is the picture's file; the document is unmodified, and Save must not write PDF bytes over it.
    /// </summary>
    public static async Task<PdfDocument> FromImageAsync(string path, int width, int height, byte[] bgra, double dpiX, double dpiY)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * 4 * height)
            throw new PdfException("The picture has no pixels.");
        byte[] pdf = await PdfiumThread.Run(() =>
        {
            EnsureLibrary();
            double w = width * 72.0 / (dpiX > 0 ? dpiX : 96), h = height * 72.0 / (dpiY > 0 ? dpiY : 96);
            IntPtr doc = FPDF_CreateNewDocument();
            var pixels = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            IntPtr bitmap = FPDFBitmap_CreateEx(width, height, BitmapFormatBgra, pixels.AddrOfPinnedObject(), width * 4);
            try
            {
                IntPtr page = FPDFPage_New(doc, 0, w, h);
                IntPtr image = FPDFPageObj_NewImageObj(doc);
                if (FPDFImageObj_SetBitmap(IntPtr.Zero, 0, image, bitmap) == 0)
                    throw new PdfException("PDFium could not read the picture.");
                FPDFImageObj_SetMatrix(image, w, 0, 0, h, 0, 0);
                FPDFPage_InsertObject(page, image);
                FPDFPage_GenerateContent(page);
                FPDF_ClosePage(page);
                using var memory = new MemoryStream();
                WriteTo(doc, memory);
                return memory.ToArray();
            }
            finally
            {
                FPDFBitmap_Destroy(bitmap);
                pixels.Free();
                FPDF_CloseDocument(doc);
            }
        });
        return await OpenMemoryAsync(pdf, path, null, modified: false);
    }

    [DllImport("pdfium")] private static extern IntPtr FPDFPage_New(IntPtr doc, int index, double width, double height);
    [DllImport("pdfium")] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport("pdfium")] private static extern IntPtr FPDFPageObj_NewImageObj(IntPtr doc);
    [DllImport("pdfium")] private static extern int FPDFImageObj_SetBitmap(IntPtr pages, int count, IntPtr image, IntPtr bitmap);
    [DllImport("pdfium")] private static extern int FPDFImageObj_SetMatrix(IntPtr image, double a, double b, double c, double d, double e, double f);
    [DllImport("pdfium")] private static extern void FPDFPage_InsertObject(IntPtr page, IntPtr obj);
    [DllImport("pdfium")] private static extern int FPDFPage_GenerateContent(IntPtr page);
    [DllImport("pdfium")] private static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr buffer, int stride);
    [DllImport("pdfium")] private static extern void FPDFBitmap_Destroy(IntPtr bitmap);
}
