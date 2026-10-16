using CleanPDF.Engine;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace CleanPDF.App;

public partial class App : Application
{
    private readonly string[] _args;
    private readonly bool _benchmark;
    private MainWindow? _window;

    public App(string[] args, bool benchmark)
    {
        _args = args;
        _benchmark = benchmark;
        InitializeComponent();
        UnhandledException += (_, e) => CrashLog.Write(e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Launch();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            throw;
        }
    }

    private static async Task<PdfDocument> OpenAndPrefetchAsync(string path)
    {
        var doc = await PdfDocument.OpenAsync(path).ConfigureAwait(false);
        if (doc.PageCount > 0)
            _ = doc.PrefetchPageAsync(0);
        return doc;
    }

    private void Launch()
    {
        var files = _args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a))
            .Select(Path.GetFullPath).ToArray();

        // Open the documents and parse their first page on the PDFium thread while the UI
        // thread builds the window.
        var opening = files.Select(f => (Path: f, Task: OpenAndPrefetchAsync(f))).ToList();

        StartupTrace.Mark("launched");
        _window = new MainWindow(_benchmark);
        StartupTrace.Mark("window_created");
        _window.Activate();
        foreach (var (path, task) in opening)
            _ = _window.OpenFileAsync(path, task);
        if (opening.Count == 0)
        {
            _window.ShowStartPage(true);
            if (_benchmark) _window.ReportBenchmarkAndExit(); // lets --screenshot capture the start page
        }

        if (!_benchmark)
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            AppInstance.GetCurrent().Activated += (_, e) =>
            {
                string commandLine = e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ? launch.Arguments : "";
                dispatcher.TryEnqueue(() =>
                {
                    _window.BringToFront();
                    _window.OpenFiles(Program.FilesFromCommandLine(commandLine));
                });
            };
        }
    }
}


