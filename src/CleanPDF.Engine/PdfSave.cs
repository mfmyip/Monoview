using System.Runtime.InteropServices;

namespace CleanPDF.Engine;

public sealed partial class PdfDocument
{
    private const uint FPDF_NO_INCREMENTAL = 2;

    // PDFium writes through a callback; saving runs only on the PDFium thread, so one static target is safe.
    private static Stream? _saveTarget;
    private static readonly WriteBlockCallback WriteBlock = (_, data, size) =>
    {
        try
        {
            unsafe { _saveTarget!.Write(new ReadOnlySpan<byte>((void*)data, (int)size)); }
            return 1;
        }
        catch (IOException)
        {
            return 0;
        }
    };

    /// <summary>
    /// Writes the whole document, with any changes, to <paramref name="path"/>. Password protection is kept.
    /// The open document is not affected.
    /// </summary>
    public Task SaveCopyAsync(string path) => PdfiumThread.Run(() =>
    {
        var doc = Doc;
        using var file = File.Create(path);
        _saveTarget = file;
        try
        {
            var writer = new FileWrite { Version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(WriteBlock) };
            if (FPDF_SaveAsCopy(doc.__Instance, ref writer, FPDF_NO_INCREMENTAL) == 0)
                throw new PdfException("PDFium could not write the file.");
        }
        finally
        {
            _saveTarget = null;
        }
    });

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteBlockCallback(IntPtr self, IntPtr data, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileWrite
    {
        public int Version;
        public IntPtr WriteBlock;
    }

    [DllImport("pdfium")] private static extern int FPDF_SaveAsCopy(IntPtr doc, ref FileWrite writer, uint flags);
}
