using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using WixToolset.BootstrapperApplicationApi;

namespace Monoview.Setup;

/// <summary>
/// One window with four pages: Start (a summary and Install), Options, Progress, and a Message page for
/// done, maintenance, errors and newer installs. The footer buttons change meaning with the page.
/// </summary>
internal sealed partial class SetupWindow : Window
{
    private enum Page { Start, Options, Progress, Done, Maintenance, Closing }

    private const string LicenseUrl = "https://github.com/mfmyip/Monoview/blob/main/LICENSE";
    private static readonly string UserFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Monoview");
    // Setup runs as x86, where ProgramFiles points at Program Files (x86); ProgramW6432 is the 64-bit folder.
    private static readonly string MachineFolder = Path.Combine(
        Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Monoview");

    private readonly SetupApp _app;
    private readonly bool _interactive;
    private Page _page;
    private string _userFolder = UserFolder, _machineFolder = MachineFolder;

    /// <summary>The window handle, read from Burn's thread to parent the admin prompt.</summary>
    public IntPtr Handle { get; private set; }

    public SetupWindow(SetupApp app, bool interactive)
    {
        _app = app;
        _interactive = interactive;
        InitializeComponent();
        ApplyTheme();
        LoadPreviousChoices();
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            if (IsDark()) { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); }
        };
        Closing += OnClosing;
        ShowPage(Page.Progress, "Getting ready", "Checking what is installed…");
        PrimaryButton.Visibility = Visibility.Collapsed;
    }

    // ---- Pages, called by SetupApp on this window's thread ----

    public void ShowStart()
    {
        if (_app.Installed)
        {
            ShowPage(Page.Maintenance, "Monoview is installed", $"Version {_app.Version} is on this PC.");
            Message.Text = "Repair puts back missing or damaged files. Uninstall removes Monoview and its shortcuts; your documents and settings stay.";
            SetButtons(primary: "Uninstall", secondary: "Close", tertiary: "Repair");
            return;
        }
        string heading = _app.UpgradingFrom is null ? "Install Monoview" : "Update Monoview";
        string sub = _app.UpgradingFrom is null
            ? $"A fast, calm PDF viewer for Windows. Version {_app.Version}."
            : $"Version {_app.UpgradingFrom} is installed. This updates it to {_app.Version}.";
        ShowPage(Page.Start, heading, sub);
        UpdateSummary();
        SetButtons(primary: _app.UpgradingFrom is null ? "Install" : "Update", secondary: "Cancel");
    }

    public void ShowProgress(LaunchAction action)
    {
        string what = action switch
        {
            LaunchAction.Uninstall => "Removing Monoview",
            LaunchAction.Repair => "Repairing Monoview",
            _ => _app.UpgradingFrom is null ? "Installing Monoview" : "Updating Monoview",
        };
        ShowPage(Page.Progress, what, "This takes a few seconds.");
        Progress.Value = 0;
        ProgressText.Text = "Starting…";
        SetButtons(primary: null, secondary: "Cancel");
    }

    public void SetProgress(int percent)
    {
        Progress.Value = percent;
        if (SecondaryButton.IsEnabled) ProgressText.Text = $"{percent}%";
    }

    public void ShowDone(LaunchAction action)
    {
        DoneBadge.Visibility = Visibility.Visible;
        switch (action)
        {
            case LaunchAction.Uninstall:
                ShowPage(Page.Done, "Monoview was removed", "Thanks for trying it.");
                Message.Text = "Your documents and settings were not touched.";
                SetButtons(primary: "Close", secondary: null);
                break;
            case LaunchAction.Repair:
                ShowPage(Page.Done, "Monoview was repaired", "Everything is back in place.");
                Message.Text = "";
                SetButtons(primary: "Close", secondary: null);
                break;
            default:
                ShowPage(Page.Done, "Monoview is ready", "Find it in the Start menu, or open a PDF with it.");
                Message.Text = "";
                SetButtons(primary: "Open Monoview", secondary: "Close");
                if (MakeDefault.IsEnabled && MakeDefault.IsChecked == true)
                {
                    Message.Text = "Windows Settings is open on Monoview's page. Choose Set default there to finish.";
                    Open(DefaultAppsLink.ForRegisteredApp(machine: Everyone.IsChecked == true));
                }
                break;
        }
    }

    public void ShowCancelled()
    {
        ShowPage(Page.Closing, "Setup was cancelled", "Nothing was changed.");
        Message.Text = "";
        SetButtons(primary: "Close", secondary: null);
    }

    public void ShowError(int status)
    {
        ShowPage(Page.Closing, "Setup didn't finish", $"Error 0x{status:X8}.");
        Message.Text = "Nothing was changed. The setup log has the details; attach it if you report the problem.";
        LogLink.Visibility = Visibility.Visible;
        SetButtons(primary: "Close", secondary: null);
    }

    public void ShowNewerInstalled(string version)
    {
        ShowPage(Page.Closing, "A newer Monoview is installed", $"Version {version} is already on this PC.");
        Message.Text = "To go back to this older version, uninstall Monoview in Windows Settings > Apps first.";
        SetButtons(primary: "Close", secondary: null);
    }

    public Result AskFilesInUse() =>
        MessageBox.Show(this, "Monoview is running. Close it, then click Retry.", "Monoview Setup",
            MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK ? Result.Retry : Result.Cancel;

    // ---- Buttons ----

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        switch (_page)
        {
            case Page.Start or Page.Options:
                if (string.IsNullOrWhiteSpace(Folder.Text) || !Path.IsPathRooted(Folder.Text.Trim()))
                {
                    ShowPage(Page.Options, "Options", "Choose where and for whom to install Monoview.");
                    SetButtons(primary: "Install", secondary: "Back");
                    Folder.Focus();
                    return;
                }
                _app.Install(Everyone.IsChecked == true, Folder.Text.Trim(),
                    DesktopShortcut.IsChecked == true, OpenWith.IsChecked == true, CheckForUpdates.IsChecked == true);
                break;
            case Page.Maintenance:
                _app.Uninstall();
                break;
            case Page.Done when _app.PlannedAction == LaunchAction.Install:
                try { Process.Start(new ProcessStartInfo(Path.Combine(Folder.Text.Trim(), "Monoview.exe")) { UseShellExecute = true }); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException) { }
                Finish();
                break;
            default:
                Finish();
                break;
        }
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        switch (_page)
        {
            case Page.Options:
                ShowStart();
                break;
            case Page.Progress:
                _app.Cancel();
                SecondaryButton.IsEnabled = false;
                ProgressText.Text = "Cancelling…";
                break;
            default:
                Close();
                break;
        }
    }

    private void Tertiary_Click(object sender, RoutedEventArgs e) => _app.Repair();

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(Page.Options, "Options", "Choose where and for whom to install Monoview.");
        SetButtons(primary: _app.UpgradingFrom is null ? "Install" : "Update", secondary: "Back");
    }

    private void Scope_Checked(object sender, RoutedEventArgs e)
    {
        if (Folder is null) return; // during InitializeComponent
        // Swap the default folder with the scope, but keep a folder the user typed.
        if (Everyone.IsChecked == true && SameFolder(Folder.Text, _userFolder)) Folder.Text = _machineFolder;
        else if (JustMe.IsChecked == true && SameFolder(Folder.Text, _machineFolder)) Folder.Text = _userFolder;
        Shield.Visibility = Everyone.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        string? picked = FolderPicker.Pick(Handle, "Choose where to install Monoview", Folder.Text);
        if (picked is null) return;
        // Picking a parent like D:\Apps means D:\Apps\Monoview, as most installers do.
        Folder.Text = string.Equals(Path.GetFileName(picked), "Monoview", StringComparison.OrdinalIgnoreCase)
            ? picked : Path.Combine(picked, "Monoview");
    }

    private void License_Click(object sender, RoutedEventArgs e) => Open(LicenseUrl);

    private void Log_Click(object sender, RoutedEventArgs e) => Open(_app.LogPath);

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_app.Applying)
        {
            e.Cancel = true;
            if (MessageBox.Show(this, "Stop setup? Anything it did so far is undone.", "Monoview Setup",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Secondary_Click(this, new RoutedEventArgs());
            return;
        }
        // Leaving before anything ran counts as cancelled for whoever started setup.
        _app.Exit(cancelled: _page is Page.Start or Page.Options or Page.Maintenance);
    }

    private void Finish()
    {
        Closing -= OnClosing;
        Close();
        _app.Exit(cancelled: false);
    }

    // ---- Helpers ----

    private void ShowPage(Page page, string heading, string subheading)
    {
        _page = page;
        Heading.Text = heading;
        Subheading.Text = subheading;
        StartPage.Visibility = page == Page.Start ? Visibility.Visible : Visibility.Collapsed;
        OptionsPage.Visibility = page == Page.Options ? Visibility.Visible : Visibility.Collapsed;
        ProgressPage.Visibility = page == Page.Progress ? Visibility.Visible : Visibility.Collapsed;
        MessagePage.Visibility = page is Page.Done or Page.Maintenance or Page.Closing ? Visibility.Visible : Visibility.Collapsed;
        LicenseNote.Visibility = page is Page.Start or Page.Options ? Visibility.Visible : Visibility.Hidden;
        LogLink.Visibility = Visibility.Collapsed;
        if (page != Page.Done) DoneBadge.Visibility = Visibility.Collapsed;
    }

    private void SetButtons(string? primary, string? secondary, string? tertiary = null)
    {
        PrimaryButton.Visibility = primary is null || !_interactive ? Visibility.Collapsed : Visibility.Visible;
        PrimaryLabel.Text = primary ?? "";
        Shield.Visibility = primary is "Install" or "Update" && Everyone.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        SecondaryButton.Content = secondary ?? "";
        SecondaryButton.IsEnabled = true;
        TertiaryButton.Visibility = tertiary is null ? Visibility.Collapsed : Visibility.Visible;
        TertiaryButton.Content = tertiary ?? "";
    }

    private void UpdateSummary()
    {
        bool everyone = Everyone.IsChecked == true;
        SummaryScope.Text = everyone
            ? "Installs for everyone on this PC. Windows asks for admin approval."
            : "Installs just for you. No admin approval needed.";
        SummaryFolder.Text = Folder.Text;
        string extras = "Adds Monoview to the Start menu";
        if (DesktopShortcut.IsChecked == true) extras += " and the desktop";
        if (OpenWith.IsChecked == true) extras += ", and to Open with for PDF, PNG and JPG";
        extras += ".";
        if (CheckForUpdates.IsChecked == true) extras += " Checks GitHub for updates when it starts.";
        if (OpenWith.IsChecked == true && MakeDefault.IsChecked == true) extras += " Then opens Windows Settings to make it your default PDF app.";
        SummaryExtras.Text = extras;
    }

    /// <summary>Fills the options from the install being updated, so an update keeps the user's choices.</summary>
    private void LoadPreviousChoices()
    {
        using var user = Registry.CurrentUser.OpenSubKey(@"Software\Monoview\Installer");
        using var machineBase = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var machine = machineBase.OpenSubKey(@"Software\Monoview\Installer");
        if (user?.GetValue("InstallFolder") is string userFolder && userFolder.Length > 0) _userFolder = userFolder.TrimEnd('\\');
        if (machine?.GetValue("InstallFolder") is string machineFolder && machineFolder.Length > 0) _machineFolder = machineFolder.TrimEnd('\\');

        bool everyone = machine?.GetValue("InstallFolder") is not null && user?.GetValue("InstallFolder") is null;
        var choices = everyone ? machine : user;
        Everyone.IsChecked = everyone;
        JustMe.IsChecked = !everyone;
        Folder.Text = everyone ? _machineFolder : _userFolder;
        DesktopShortcut.IsChecked = choices?.GetValue("DesktopShortcut") as string == "on";
        OpenWith.IsChecked = choices?.GetValue("OpenWith") as string != "off";
        CheckForUpdates.IsChecked = choices?.GetValue("CheckForUpdates") is not int updates || updates != 0;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static bool IsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    /// <summary>Windows 11 colors: light by default, dark when apps use the dark theme.</summary>
    private void ApplyTheme()
    {
        if (!IsDark()) return;
        void Set(string key, string hex) => Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        Set("Bg", "#202020");
        Set("Card", "#2B2B2B");
        Set("Text", "#FFFFFF");
        Set("SubText", "#C5C5C5");
        Set("Line", "#3A3A3A");
        Set("Control", "#2D2D2D");
        Set("ControlHover", "#353535");
        Set("Accent", "#60CDFF");
        Set("AccentHover", "#7AD5FF");
        Set("OnAccent", "#000000");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
