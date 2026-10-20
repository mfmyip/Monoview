using Monoview.Engine;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Monoview.App;

public partial class App : Application
{
    private readonly string[] _args;
    private readonly bool _benchmark;
    private readonly List<MainWindow> _windows = new();
    private MainWindow? _lastActive;

    public static new App Current => (App)Application.Current;

    /// <summary>Open windows, oldest first. The app exits when the last one closes.</summary>
    public IReadOnlyList<MainWindow> Windows => _windows;

    /// <summary>A new, empty window; the caller fills and activates it.</summary>
    public MainWindow NewWindow()
    {
        var window = new MainWindow(_benchmark);
        _windows.Add(window);
        _lastActive ??= window;
        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) _lastActive = window;
        };
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            if (_lastActive == window) _lastActive = _windows.LastOrDefault();
        };
        return window;
    }

    /// <summary>Closes every window, asking about unsaved changes in each; stops at the first Cancel (Exit, Ctrl+Q).</summary>
    public async Task QuitAsync()
    {
        foreach (var window in _windows.ToList())
            if (!await window.CloseWindowAsync())
                return;
    }

    public App(string[] args, bool benchmark)
    {
        _args = args;
        _benchmark = benchmark;
        InitializeComponent();
        UnhandledException += (_, e) => CrashLog.Write(e.Exception);
        LiveText.Enable();
        // Dev aid: --trace-exceptions prints every exception as it's thrown (finds crashes in dispatcher callbacks).
        if (args.Contains("--trace-exceptions"))
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
                Console.Error.WriteLine($"FIRST_CHANCE {e.Exception.GetType().Name}: {e.Exception.Message}\n{Environment.StackTrace}");
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
        var doc = await DocumentFiles.OpenAsync(path).ConfigureAwait(false);
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
        var window = NewWindow();
        StartupTrace.Mark("window_created");
        window.Activate();
        foreach (var (path, task) in opening)
            _ = window.OpenFileAsync(path, task);
        if (!_benchmark) _ = window.CheckForUpdatesAsync();
        if (opening.Count == 0)
        {
            window.ShowStartPage(true);
            if (_benchmark) window.ReportBenchmarkAndExit(); // lets --screenshot capture the start page
        }

        if (!_benchmark)
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            AppInstance.GetCurrent().Activated += (_, e) =>
            {
                // The MSIX build gets file activations from its file associations; the plain .exe gets a command line.
                string[] files = e.Data switch
                {
                    Windows.ApplicationModel.Activation.IFileActivatedEventArgs file => file.Files.Select(f => f.Path).Where(File.Exists).ToArray(),
                    Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch => Program.FilesFromCommandLine(launch.Arguments),
                    _ => [],
                };
                // Files from Explorer go to the window used last, like a browser.
                dispatcher.TryEnqueue(() =>
                {
                    var target = _lastActive ?? NewWindow();
                    target.BringToFront();
                    target.OpenFiles(files);
                });
            };
        }
    }
}


