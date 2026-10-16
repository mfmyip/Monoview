using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using CleanPDF.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace CleanPDF.App;

public sealed partial class MainWindow : Window
{
    private readonly bool _benchmark;
    private PdfDocument? _doc;
    private int _pageIndex;

    public MainWindow(string? path, bool benchmark)
    {
        _benchmark = benchmark;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 1300));
        Closed += (_, _) => _doc?.Dispose();

        if (path is not null)
            _ = OpenAsync(path);
    }

    private async Task OpenAsync(string path)
    {
        try
        {
            var doc = await Task.Run(() => PdfDocument.Open(path));
            _doc?.Dispose();
            _doc = doc;
            _pageIndex = 0;
            Title = $"{Path.GetFileName(path)} - CleanPDF";
            EmptyHint.Visibility = Visibility.Collapsed;
            await ShowPageAsync();
        }
        catch (PdfException ex)
        {
            EmptyHint.Text = ex.Message;
            EmptyHint.Visibility = Visibility.Visible;
        }
    }

    private async Task ShowPageAsync()
    {
        if (_doc is null) return;
        var doc = _doc;
        int index = _pageIndex;

        // Render at the window's DPI so text is sharp; points are 1/72 inch, DIPs are 1/96 inch.
        double scale = Content.XamlRoot?.RasterizationScale ?? 1.0;
        double dipsPerPoint = 96.0 / 72.0;
        var (wPt, hPt) = await Task.Run(() => doc.GetPageSize(index));
        double fitWidthDips = Math.Min(Scroller.ActualWidth > 0 ? Scroller.ActualWidth - 48 : 1000, wPt * dipsPerPoint * 1.5);
        double zoom = fitWidthDips / (wPt * dipsPerPoint);
        int pxW = (int)Math.Round(wPt * dipsPerPoint * zoom * scale);
        int pxH = (int)Math.Round(hPt * dipsPerPoint * zoom * scale);

        var rendered = await Task.Run(() => doc.RenderPage(index, pxW, pxH));
        if (doc != _doc || index != _pageIndex) return; // user moved on while rendering

        var bmp = new WriteableBitmap(rendered.Width, rendered.Height);
        using (var stream = bmp.PixelBuffer.AsStream())
            stream.Write(rendered.Bgra, 0, rendered.Bgra.Length);
        bmp.Invalidate();

        PageImage.Source = bmp;
        PageImage.Width = rendered.Width / scale;
        PageImage.Height = rendered.Height / scale;
        PageLabel.Text = $"Page {index + 1} of {doc.PageCount}";

        if (_benchmark)
            ReportFirstPageAndExit();
    }

    private void ReportFirstPageAndExit()
    {
        // Wait one frame so the page has actually been composed on screen.
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendered;

        void OnRendered(object? sender, object e)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendered;
            var sinceStart = DateTime.Now - Process.GetCurrentProcess().StartTime;
            Console.Out.WriteLine($"FIRST_PAGE_MS={sinceStart.TotalMilliseconds:F0}");
            Console.Out.Flush();
            Application.Current.Exit();
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            await OpenAsync(file.Path);
    }

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _pageIndex == 0) return;
        _pageIndex--;
        await ShowPageAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _pageIndex >= _doc.PageCount - 1) return;
        _pageIndex++;
        await ShowPageAsync();
    }
}
