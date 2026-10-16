using System.Runtime.InteropServices;

namespace CleanPDF.App;

/// <summary>
/// The standard Windows print dialog (printer, page range, copies). Copies and collation are
/// handled by the printer driver through the DEVMODE the dialog returns.
/// </summary>
internal static class PrintDialog
{
    public sealed record Result(IntPtr Hdc, string PrinterName, IReadOnlyList<int> Pages);

    private const uint PD_NOSELECTION = 0x4, PD_PAGENUMS = 0x2, PD_RETURNDC = 0x100;
    private const uint PD_USEDEVMODECOPIESANDCOLLATE = 0x40000, PD_CURRENTPAGE = 0x400000;
    private const uint START_PAGE_GENERAL = 0xFFFFFFFF, PD_RESULT_PRINT = 1;
    private const int MaxRanges = 32;

    /// <summary>Shows the dialog; returns null if cancelled. The caller deletes <see cref="Result.Hdc"/>.</summary>
    public static Result? Show(IntPtr owner, int pageCount, int currentPage)
    {
        IntPtr ranges = Marshal.AllocHGlobal(Marshal.SizeOf<PRINTPAGERANGE>() * MaxRanges);
        var pd = new PRINTDLGEXW
        {
            lStructSize = Marshal.SizeOf<PRINTDLGEXW>(),
            hwndOwner = owner,
            Flags = PD_RETURNDC | PD_USEDEVMODECOPIESANDCOLLATE | PD_NOSELECTION,
            nMaxPageRanges = MaxRanges,
            lpPageRanges = ranges,
            nMinPage = 1,
            nMaxPage = (uint)pageCount,
            nCopies = 1,
            nStartPage = START_PAGE_GENERAL,
        };
        try
        {
            int hr = PrintDlgExW(ref pd);
            if (hr != 0 || pd.dwResultAction != PD_RESULT_PRINT || pd.hDC == IntPtr.Zero)
            {
                if (pd.hDC != IntPtr.Zero) DeleteDC(pd.hDC);
                return null;
            }

            List<int> pages;
            if ((pd.Flags & PD_CURRENTPAGE) != 0)
            {
                pages = [currentPage];
            }
            else if ((pd.Flags & PD_PAGENUMS) != 0)
            {
                pages = [];
                var seen = new HashSet<int>();
                for (int i = 0; i < pd.nPageRanges; i++)
                {
                    var range = Marshal.PtrToStructure<PRINTPAGERANGE>(ranges + i * Marshal.SizeOf<PRINTPAGERANGE>());
                    int from = (int)Math.Min(range.nFromPage, range.nToPage), to = (int)Math.Max(range.nFromPage, range.nToPage);
                    for (int p = Math.Max(1, from); p <= Math.Min(pageCount, to); p++)
                        if (seen.Add(p - 1)) pages.Add(p - 1);
                }
            }
            else
            {
                pages = Enumerable.Range(0, pageCount).ToList();
            }
            return new Result(pd.hDC, PrinterName(pd.hDevNames), pages);
        }
        finally
        {
            if (pd.hDevMode != IntPtr.Zero) GlobalFree(pd.hDevMode);
            if (pd.hDevNames != IntPtr.Zero) GlobalFree(pd.hDevNames);
            Marshal.FreeHGlobal(ranges);
        }
    }

    private static string PrinterName(IntPtr hDevNames)
    {
        if (hDevNames == IntPtr.Zero) return "the printer";
        IntPtr p = GlobalLock(hDevNames);
        try
        {
            // DEVNAMES: four WORD offsets (in characters) to the driver, device and port names.
            int deviceOffset = Marshal.ReadInt16(p, 2);
            return Marshal.PtrToStringUni(p + deviceOffset * 2) ?? "the printer";
        }
        finally
        {
            GlobalUnlock(hDevNames);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTPAGERANGE
    {
        public uint nFromPage;
        public uint nToPage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTDLGEXW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hDevMode;
        public IntPtr hDevNames;
        public IntPtr hDC;
        public uint Flags;
        public uint Flags2;
        public uint ExclusionFlags;
        public uint nPageRanges;
        public uint nMaxPageRanges;
        public IntPtr lpPageRanges;
        public uint nMinPage;
        public uint nMaxPage;
        public uint nCopies;
        public IntPtr hInstance;
        public IntPtr lpPrintTemplateName;
        public IntPtr lpCallback;
        public uint nPropertyPages;
        public IntPtr lphPropertyPages;
        public uint nStartPage;
        public uint dwResultAction;
    }

    [DllImport("comdlg32.dll")] private static extern int PrintDlgExW(ref PRINTDLGEXW pd);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr h);
}
