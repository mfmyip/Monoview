using System.Diagnostics;

namespace Monoview.App;

/// <summary>Prints "TRACE name=ms since process start" in benchmark mode, to see where startup time goes.</summary>
internal static class StartupTrace
{
    public static bool Enabled { get; set; }
    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime;

    public static void Mark(string name)
    {
        if (!Enabled) return;
        Console.Out.WriteLine($"TRACE {name}={(DateTime.Now - ProcessStart).TotalMilliseconds:F0}");
    }
}
