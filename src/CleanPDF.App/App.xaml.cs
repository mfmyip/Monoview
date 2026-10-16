using Microsoft.UI.Xaml;

namespace CleanPDF.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Process args, not LaunchActivatedEventArgs.Arguments: unpackaged apps get "Open with" paths here.
        var cmd = Environment.GetCommandLineArgs().Skip(1).ToArray();
        string? path = cmd.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        bool benchmark = cmd.Contains("--benchmark");

        _window = new MainWindow(path, benchmark);
        _window.Activate();
    }
}
