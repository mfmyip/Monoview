using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Monoview.App;

/// <summary>
/// Registers Monoview for .pdf files for the current user (HKCU only, no admin), so it shows up
/// in "Open with" and in Settings > Default apps. Windows doesn't let apps make themselves the
/// default; the user picks it there. The MSIX package will declare this instead (M4).
/// </summary>
internal static partial class AppRegistration
{
    private const string ProgId = "Monoview.Document";
    private const string Classes = @"Software\Classes";
    private const string Capabilities = @"Software\Monoview\Capabilities";

    /// <summary>True in the MSIX build, which declares .pdf in its manifest and can't write these keys for the shell.</summary>
    public static bool IsPackaged { get; } = HasPackage();

    private static bool HasPackage()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) != 15700 /* APPMODEL_ERROR_NO_PACKAGE */;
    }

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{Classes}\{ProgId}\shell\open\command");
        return key?.GetValue(null) is string cmd && cmd.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase);
    }

    public static void Register()
    {
        string exe = Environment.ProcessPath!;
        string open = $"\"{exe}\" \"%1\"";

        using (var progId = Registry.CurrentUser.CreateSubKey($@"{Classes}\{ProgId}"))
        {
            progId.SetValue(null, "PDF Document");
            using (var icon = progId.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
            using (var cmd = progId.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, open);
        }
        using (var withProgIds = Registry.CurrentUser.CreateSubKey($@"{Classes}\.pdf\OpenWithProgids"))
            withProgIds.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);

        using (var app = Registry.CurrentUser.CreateSubKey($@"{Classes}\Applications\{Path.GetFileName(exe)}"))
        {
            app.SetValue("FriendlyAppName", "Monoview");
            using (var types = app.CreateSubKey("SupportedTypes")) types.SetValue(".pdf", "");
            using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, open);
        }

        using (var caps = Registry.CurrentUser.CreateSubKey(Capabilities))
        {
            caps.SetValue("ApplicationName", "Monoview");
            caps.SetValue("ApplicationDescription", "A fast, simple PDF viewer.");
            using (var assoc = caps.CreateSubKey("FileAssociations")) assoc.SetValue(".pdf", ProgId);
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue("Monoview", Capabilities);

        NotifyShell();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\Applications\{Path.GetFileName(Environment.ProcessPath!)}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(Capabilities, throwOnMissingSubKey: false);
        using (var withProgIds = Registry.CurrentUser.OpenSubKey($@"{Classes}\.pdf\OpenWithProgids", writable: true))
            withProgIds?.DeleteValue(ProgId, throwOnMissingValue: false);
        using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            registered?.DeleteValue("Monoview", throwOnMissingValue: false);
        NotifyShell();
    }

    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint length, IntPtr name);
}
