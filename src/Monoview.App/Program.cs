using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Monoview.App;

/// <summary>
/// Custom entry point so a second launch (e.g. double-clicking another PDF in Explorer) hands
/// its files to the running window as new tabs instead of starting another process.
/// </summary>
public static partial class Program
{
    private const string InstanceKey = "Monoview.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        bool benchmark = args.Contains("--benchmark");
        StartupTrace.Enabled = benchmark;
        StartupTrace.Mark("main");
        if (!benchmark && RedirectToRunningInstance())
            return 0;

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(args, benchmark);
        });
        return 0;
    }

    private static bool RedirectToRunningInstance()
    {
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent)
            return false;

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        AllowSetForegroundWindow(main.ProcessId);
        // Redirect off the STA thread so the call can't deadlock on it.
        Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait();
        return true;
    }

    /// <summary>Splits a raw command line the way Windows does and returns the arguments that are files.</summary>
    public static string[] FilesFromCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero) return [];
        try
        {
            var files = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string? arg = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
                if (string.IsNullOrEmpty(arg) || arg.StartsWith("--", StringComparison.Ordinal)) continue;
                if (arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(arg)) files.Add(Path.GetFullPath(arg));
            }
            return files.ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgvW(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);
}


