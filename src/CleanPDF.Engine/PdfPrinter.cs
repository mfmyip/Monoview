using System.Runtime.InteropServices;
using PDFiumCore;

namespace CleanPDF.Engine;

/// <summary>
/// Prints pages to a Windows printer device context. PDFium draws each page straight into the
/// printer's DC (vector output at the printer's resolution), so nothing is rasterized up front.
/// </summary>
public static partial class PdfPrinter
{
    private const int RenderFlagAnnot = 0x01;     // FPDF_ANNOT
    private const int RenderFlagPrinting = 0x800; // FPDF_PRINTING
    private const int HORZRES = 8, VERTRES = 10, LOGPIXELSX = 88, LOGPIXELSY = 90;

    /// <summary>
    /// Prints <paramref name="pages"/> (0-based) as one job. Each page is shrunk to fit the printable
    /// area if needed (never enlarged), centered, and turned sideways when that fits the paper better.
    /// <paramref name="outputFile"/> sends the job to a file (e.g. with "Microsoft Print to PDF").
    /// </summary>
    public static Task PrintAsync(PdfDocument document, IntPtr hdc, string jobName, IReadOnlyList<int> pages,
        string? outputFile = null, IProgress<int>? progress = null, CancellationToken ct = default) =>
        PdfiumThread.Run(() =>
        {
            var docInfo = new DOCINFOW
            {
                cbSize = Marshal.SizeOf<DOCINFOW>(),
                lpszDocName = jobName,
                lpszOutput = outputFile,
            };
            if (StartDocW(hdc, ref docInfo) <= 0)
                throw new PdfException($"The printer didn't accept the job (error {Marshal.GetLastPInvokeError()}).");

            try
            {
                int areaW = GetDeviceCaps(hdc, HORZRES), areaH = GetDeviceCaps(hdc, VERTRES);
                double dpiX = GetDeviceCaps(hdc, LOGPIXELSX), dpiY = GetDeviceCaps(hdc, LOGPIXELSY);

                for (int i = 0; i < pages.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    int index = pages[i];
                    var size = document.PageSizes[index];

                    // Turn landscape pages sideways on portrait paper (and vice versa) when that fits better.
                    bool sideways = size.Width > size.Height != areaW > areaH;
                    double pw = (sideways ? size.Height : size.Width) / 72.0 * dpiX;
                    double ph = (sideways ? size.Width : size.Height) / 72.0 * dpiY;
                    double scale = Math.Min(1, Math.Min(areaW / pw, areaH / ph));
                    int w = (int)Math.Round(pw * scale), h = (int)Math.Round(ph * scale);
                    int x = (areaW - w) / 2, y = (areaH - h) / 2;

                    if (StartPage(hdc) <= 0) throw new PdfException("The printer stopped accepting pages.");
                    var page = document.GetPageForPrinting(index);
                    FPDF_RenderPage(hdc, page.__Instance, x, y, w, h, sideways ? 1 : 0, RenderFlagAnnot | RenderFlagPrinting);
                    if (EndPage(hdc) <= 0) throw new PdfException("The printer stopped accepting pages.");
                    progress?.Report(i + 1);
                }
                EndDoc(hdc);
            }
            catch
            {
                AbortDoc(hdc);
                throw;
            }
        }, WorkPriority.High, ct);

    /// <summary>Creates a DC for a printer by name (for printing without the dialog, e.g. in tests).</summary>
    public static IntPtr CreatePrinterDC(string printerName) => CreateDCW("WINSPOOL", printerName, null, IntPtr.Zero);

    public static void DeletePrinterDC(IntPtr hdc)
    {
        if (hdc != IntPtr.Zero) DeleteDC(hdc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOCINFOW
    {
        public int cbSize;
        public string? lpszDocName;
        public string? lpszOutput;
        public string? lpszDatatype;
        public int fwType;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocW(IntPtr hdc, ref DOCINFOW docInfo);

    [LibraryImport("gdi32.dll")] private static partial int StartPage(IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial int EndPage(IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial int EndDoc(IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial int AbortDoc(IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial int GetDeviceCaps(IntPtr hdc, int index);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateDCW(string driver, string device, string? output, IntPtr devMode);

    // Windows-only PDFium entry point (not in the cross-platform bindings): draws a page into an HDC.
    [LibraryImport("pdfium")]
    private static partial void FPDF_RenderPage(IntPtr hdc, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
}
