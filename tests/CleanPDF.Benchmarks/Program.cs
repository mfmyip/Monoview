// Startup benchmark: launches CleanPDF (and optionally Acrobat) on a PDF several times
// and reports time-to-first-page.
//
// Usage: CleanPDF.Benchmarks <CleanPDF.exe> <file.pdf> [--runs N] [--acrobat <Acrobat.exe>]
using System.Diagnostics;
using System.Text.RegularExpressions;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: CleanPDF.Benchmarks <CleanPDF.exe> <file.pdf> [--runs N] [--acrobat <Acrobat.exe>]");
    return 2;
}

string appExe = Path.GetFullPath(args[0]);
string pdf = Path.GetFullPath(args[1]);
int runs = OptionValue("--runs") is { } r ? int.Parse(r) : 10;
string? acrobatExe = OptionValue("--acrobat");

Console.WriteLine($"PDF: {Path.GetFileName(pdf)} ({new FileInfo(pdf).Length / 1024.0 / 1024.0:F1} MB), {runs} runs\n");

var cleanPdf = new List<double>();
for (int i = 0; i < runs; i++)
    cleanPdf.Add(RunCleanPdf());
Report("CleanPDF  time to first page", cleanPdf);

if (acrobatExe is not null)
{
    if (Process.GetProcessesByName("Acrobat").Length > 0)
    {
        Console.WriteLine("\nAcrobat is already running; skipping so the comparison isn't skewed (and your open files aren't closed).");
    }
    else
    {
        var acrobat = new List<double>();
        for (int i = 0; i < Math.Min(runs, 3); i++)
            acrobat.Add(RunAcrobat(acrobatExe));
        Report("Acrobat   time to document window", acrobat);
    }
}

return 0;

double RunCleanPdf()
{
    var psi = new ProcessStartInfo(appExe, $"\"{pdf}\" --benchmark")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
    };
    using var p = Process.Start(psi)!;
    string output = p.StandardOutput.ReadToEnd();
    if (!p.WaitForExit(30_000))
    {
        p.Kill();
        throw new TimeoutException("CleanPDF did not exit within 30 s.");
    }
    var m = Regex.Match(output, @"FIRST_PAGE_MS=(\d+)");
    if (!m.Success)
        throw new InvalidOperationException($"CleanPDF exited with {p.ExitCode} and no timing output.");
    return double.Parse(m.Groups[1].Value);
}

// Acrobat can't report when the page is painted, so this measures until its main window
// title shows the file name. That flatters Acrobat slightly (the page paints after that).
double RunAcrobat(string exe)
{
    var sw = Stopwatch.StartNew();
    using var p = Process.Start(new ProcessStartInfo(exe, $"\"{pdf}\"") { UseShellExecute = false })!;
    string name = Path.GetFileNameWithoutExtension(pdf);
    while (sw.Elapsed < TimeSpan.FromSeconds(60))
    {
        p.Refresh();
        if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Contains(name, StringComparison.OrdinalIgnoreCase))
            break;
        Thread.Sleep(10);
    }
    double ms = sw.Elapsed.TotalMilliseconds;
    foreach (var proc in Process.GetProcessesByName("Acrobat"))
    {
        proc.Kill(entireProcessTree: true);
        proc.WaitForExit(10_000);
    }
    Thread.Sleep(2000); // let Acrobat's helper processes wind down
    return ms;
}

static void Report(string label, List<double> ms)
{
    var sorted = ms.Order().ToList();
    double median = sorted[sorted.Count / 2];
    Console.WriteLine($"{label}: first {ms[0]:F0} ms, median {median:F0} ms, min {sorted[0]:F0} ms, max {sorted[^1]:F0} ms");
}

string? OptionValue(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
