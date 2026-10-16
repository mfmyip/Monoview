using CleanPDF.App.Viewer;
using CleanPDF.Engine;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;
using Windows.Graphics.Printing.OptionDetails;

namespace CleanPDF.App;

/// <summary>
/// Prints through Windows' modern print dialog, which shows a live preview and the printer's own
/// options (orientation, copies, pages, paper, duplex, color). Pages are rendered by PDFium as
/// high-resolution images sized to the paper's printable area.
/// </summary>
internal sealed class PrintService
{
    /// <summary>Upper bound on rendered pixels held for one job, so long documents don't run out of memory.</summary>
    private const double PixelBudget = 250_000_000; // ≈ 1 GB of BGRA
    private const double MaxPrintDpi = 300, MinPrintDpi = 150, PreviewDpi = 96;

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly PrintManager _manager;

    private PdfDocument? _doc;
    private PrintDocument? _printDoc;
    private IPrintDocumentSource? _source;
    private PrintPageDescription _page;
    private string _title = "";

    /// <summary>Raised on the UI thread when a job finishes: (succeeded, message).</summary>
    public event Action<bool, string>? Completed;

    private PrintService(IntPtr hwnd, PrintManager manager)
    {
        _hwnd = hwnd;
        _manager = manager;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _manager.PrintTaskRequested += OnPrintTaskRequested;
    }

    /// <summary>Hooks the window up to the print system; null if this Windows doesn't support it.</summary>
    public static PrintService? TryCreate(Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            return new PrintService(hwnd, PrintManagerInterop.GetForWindow(hwnd));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or TypeLoadException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Shows the print dialog for <paramref name="doc"/>. Returns false if the dialog couldn't be shown.</summary>
    public async Task<bool> ShowAsync(PdfDocument doc)
    {
        _doc = doc;
        _title = Path.GetFileName(doc.Path);
        _printDoc = new PrintDocument();
        _source = _printDoc.DocumentSource;
        _printDoc.Paginate += OnPaginate;
        _printDoc.GetPreviewPage += OnGetPreviewPage;
        _printDoc.AddPages += OnAddPages;
        try
        {
            return await PrintManagerInterop.ShowPrintUIForWindowAsync(_hwnd);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    // Raised by Windows (possibly off the UI thread) when the dialog opens.
    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        if (_source is not { } source) return;
        var task = args.Request.CreatePrintTask(_title, request => request.SetSource(source));

        var shown = task.Options.DisplayedOptions;
        shown.Clear();
        foreach (var option in new[]
        {
            StandardPrintTaskOptions.Copies, StandardPrintTaskOptions.CustomPageRanges, StandardPrintTaskOptions.Orientation,
            StandardPrintTaskOptions.MediaSize, StandardPrintTaskOptions.Duplex, StandardPrintTaskOptions.ColorMode,
        })
            shown.Add(option);
        task.Options.PageRangeOptions.AllowAllPages = true;
        task.Options.PageRangeOptions.AllowCustomSetOfPages = true;

        task.Completed += (_, e) => _dispatcher.TryEnqueue(() =>
        {
            Completed?.Invoke(e.Completion == PrintTaskCompletion.Submitted,
                e.Completion switch
                {
                    PrintTaskCompletion.Submitted => $"Sent {_title} to the printer",
                    PrintTaskCompletion.Failed => "Printing failed",
                    _ => "",
                });
            Cleanup();
        });
    }

    private void OnPaginate(object sender, PaginateEventArgs e)
    {
        // Paper size, printable area and orientation change as the user picks options; re-lay out each time.
        _page = e.PrintTaskOptions.GetPageDescription(0);
        _printDoc!.SetPreviewPageCount(_doc!.PageCount, PreviewPageCountType.Final);
    }

    private void OnGetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        // The dialog snapshots the element when it's set, so the image must already be rendered.
        // At preview resolution that's a few tens of milliseconds per page.
        var element = RenderPage(e.PageNumber - 1, PreviewDpi);
        _printDoc!.SetPreviewPage(e.PageNumber, element);
    }

    private FrameworkElement RenderPage(int index, double dpi)
    {
        var (element, image, w, h, rotation) = BuildPage(index, dpi);
        try
        {
            // PDFium runs on its own thread, so waiting here can't deadlock the UI thread.
            var rendered = _doc!.RenderAsync(index, w, h, new PixelRect(0, 0, w, h), rotation, WorkPriority.High)
                .GetAwaiter().GetResult();
            image.Source = Bitmaps.ToBitmap(rendered);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or PdfException or OperationCanceledException)
        {
        }
        return element;
    }

    private void OnAddPages(object sender, AddPagesEventArgs e)
    {
        var pages = SelectedPages(e.PrintTaskOptions);
        double paperPixels = _page.ImageableRect.Width * _page.ImageableRect.Height / (96.0 * 96.0); // per DPI²
        double dpi = Math.Clamp(Math.Sqrt(PixelBudget / Math.Max(1, pages.Count * paperPixels)), MinPrintDpi, MaxPrintDpi);

        // AddPages is synchronous and each page must be complete when added.
        foreach (int index in pages)
            _printDoc!.AddPage(RenderPage(index, dpi));
        _printDoc!.AddPagesComplete();
    }

    /// <summary>
    /// One printed page: a canvas the size of the paper with the PDF page centered in the printable
    /// area, shrunk to fit (never enlarged) and turned sideways when that fits the paper better.
    /// </summary>
    private (FrameworkElement Element, Image Image, int PixelW, int PixelH, int Rotation) BuildPage(int index, double dpi)
    {
        var size = _doc!.PageSizes[index];
        var area = _page.ImageableRect;
        bool sideways = size.Width > size.Height != area.Width > area.Height;
        double pw = (sideways ? size.Height : size.Width) * 96 / 72;   // DIPs at 100%
        double ph = (sideways ? size.Width : size.Height) * 96 / 72;
        double scale = Math.Min(1, Math.Min(area.Width / pw, area.Height / ph));
        double w = pw * scale, h = ph * scale;

        var image = new Image { Width = w, Height = h, Stretch = Stretch.Fill };
        Canvas.SetLeft(image, area.X + (area.Width - w) / 2);
        Canvas.SetTop(image, area.Y + (area.Height - h) / 2);
        var canvas = new Canvas { Width = _page.PageSize.Width, Height = _page.PageSize.Height };
        canvas.Children.Add(image);

        int pixelW = Math.Max(1, (int)Math.Round(w * dpi / 96)), pixelH = Math.Max(1, (int)Math.Round(h * dpi / 96));
        return (canvas, image, pixelW, pixelH, sideways ? 1 : 0);
    }

    private List<int> SelectedPages(PrintTaskOptions options)
    {
        var pages = new List<int>();
        var seen = new HashSet<int>();
        foreach (var range in options.CustomPageRanges)
            for (int p = Math.Max(1, range.FirstPageNumber); p <= Math.Min(_doc!.PageCount, range.LastPageNumber); p++)
                if (seen.Add(p - 1)) pages.Add(p - 1);
        return pages.Count > 0 ? pages : Enumerable.Range(0, _doc!.PageCount).ToList();
    }

    private void Cleanup()
    {
        if (_printDoc is not null)
        {
            _printDoc.Paginate -= OnPaginate;
            _printDoc.GetPreviewPage -= OnGetPreviewPage;
            _printDoc.AddPages -= OnAddPages;
        }
        _printDoc = null;
        _source = null;
        _doc = null;
    }
}
