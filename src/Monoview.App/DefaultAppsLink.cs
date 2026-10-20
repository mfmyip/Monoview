using System;

namespace Monoview;

/// <summary>
/// Links to Monoview's own page in Settings > Apps > Default apps, where the user makes it the default PDF app;
/// Windows 11 shows a Set default button there, Windows 10 the general list. Windows doesn't let an app set
/// itself. The app and setup.exe (installer\Setup links this file) both open it.
/// </summary>
internal static class DefaultAppsLink
{
    // "Monoview" is the value name under Software\RegisteredApplications that the MSI and AppRegistration write.
    public static string ForRegisteredApp(bool machine) =>
        $"ms-settings:defaultapps?registeredApp{(machine ? "Machine" : "User")}=Monoview";

    /// <summary>The page for the Store build, found by its app user model ID.</summary>
    public static string ForPackage(string appUserModelId) =>
        $"ms-settings:defaultapps?registeredAUMID={Uri.EscapeDataString(appUserModelId)}";
}
