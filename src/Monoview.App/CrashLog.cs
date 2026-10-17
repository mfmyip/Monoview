namespace Monoview.App;

/// <summary>Appends unhandled exceptions to %LOCALAPPDATA%\Monoview\crash.log (and stderr).</summary>
internal static class CrashLog
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Monoview", "crash.log");

    public static void Write(Exception ex)
    {
        string entry = $"[{DateTime.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}";
        try
        {
            Console.Error.Write(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, entry);
        }
        catch (IOException)
        {
        }
    }
}
