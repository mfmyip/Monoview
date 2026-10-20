using System;
using System.Windows.Interop;
using System.Windows.Threading;
using WixToolset.BootstrapperApplicationApi;

namespace Monoview.Setup;

/// <summary>
/// Talks to Burn: detects what is installed, plans and applies the install, uninstall or repair, and reports
/// progress to <see cref="SetupWindow"/>. With /quiet or /passive it runs the requested action without asking.
/// </summary>
internal sealed class SetupApp : BootstrapperApplication
{
    // The MsiPackage Id in Bundle.wxs.
    private const string MsiId = "MonoviewMsi";
    private const int ErrorCancelled = 1602;

    private IBootstrapperCommand _command = null!;
    private Dispatcher _dispatcher = null!;
    private SetupWindow? _window;
    // Burn's Apply needs a parent window even in quiet mode, where nothing is shown.
    private HwndSource? _hiddenWindow;
    private volatile bool _cancel;
    private int _result;

    public LaunchAction PlannedAction { get; private set; }
    public bool Installed { get; private set; }
    public string? UpgradingFrom { get; private set; }
    public string? NewerInstalled { get; private set; }
    public string Version => engine.GetVariableVersion("WixBundleVersion");
    public string LogPath => engine.GetVariableString("WixBundleLog");
    public bool Applying { get; private set; }

    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);
        _command = args.Command;
    }

    protected override void Run()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        // Name=value pairs on the command line set the variables Bundle.wxs marks bal:Overridable.
        _command.ParseCommandLine().SetOverridableVariables(new BootstrapperApplicationData().Bundle.OverridableVariables, engine);
        if (_command.Display is Display.Full or Display.Passive)
        {
            _window = new SetupWindow(this, interactive: _command.Display == Display.Full);
            _window.Show();
        }
        else
        {
            _hiddenWindow = new HwndSource(new HwndSourceParameters("Monoview Setup") { WindowStyle = 0 });
        }
        engine.Detect();
        Dispatcher.Run();
        // Report a plain Win32 code, not an HRESULT, so callers see 1602 for "cancelled" and so on.
        engine.Quit((_result & 0xFFFF0000) == unchecked((int)0x80070000) ? _result & 0xFFFF : _result);
    }

    /// <summary>Starts an install with the user's choices, which reach the MSI through bundle variables.</summary>
    public void Install(bool everyone, string folder, bool desktopShortcut, bool openWith, bool checkForUpdates)
    {
        engine.SetVariableString("InstallFolder", folder, false);
        engine.SetVariableString("DesktopShortcut", desktopShortcut ? "1" : "0", false);
        engine.SetVariableString("OpenWith", openWith ? "1" : "0", false);
        engine.SetVariableString("CheckForUpdates", checkForUpdates ? "1" : "0", false);
        Plan(LaunchAction.Install, everyone ? BundleScope.PerMachine : BundleScope.PerUser);
    }

    public void Repair() => Plan(LaunchAction.Repair, BundleScope.Default);

    public void Uninstall() => Plan(LaunchAction.Uninstall, BundleScope.Default);

    /// <summary>Stops a running install at the next progress report; Burn rolls back what it did.</summary>
    public void Cancel() => _cancel = true;

    /// <summary>Ends setup; <paramref name="cancelled"/> reports "user cancelled" to whoever started it.</summary>
    public void Exit(bool cancelled)
    {
        if (cancelled) _result = ErrorCancelled;
        _dispatcher.InvokeShutdown();
    }

    private void Plan(LaunchAction action, BundleScope scope)
    {
        PlannedAction = action;
        _cancel = false;
        engine.Plan(action, scope);
    }

    protected override void OnDetectRelatedBundle(DetectRelatedBundleEventArgs args)
    {
        if (args.RelationType != RelationType.Upgrade) return;
        if (engine.CompareVersions(Version, args.Version) < 0) NewerInstalled = args.Version;
        else UpgradingFrom = args.Version;
    }

    protected override void OnDetectPackageComplete(DetectPackageCompleteEventArgs args)
    {
        if (args.PackageId == MsiId && args.State == PackageState.Present) Installed = true;
    }

    // Engine calls such as Plan stay on Burn's callback thread; only window updates go to the window's thread.
    // Calling the engine from the window's thread while a callback waits on it fails.
    protected override void OnDetectComplete(DetectCompleteEventArgs args)
    {
        if (args.Status < 0) { _dispatcher.Invoke(() => Finish(args.Status)); return; }
        if (NewerInstalled is not null && _command.Action == LaunchAction.Install)
        {
            if (_window is null) Exit(cancelled: false);
            else _dispatcher.Invoke(() => _window.ShowNewerInstalled(NewerInstalled));
            return;
        }
        // Uninstall from Settings, and every quiet or passive run, go straight to work.
        if (_command.Action == LaunchAction.Uninstall) Uninstall();
        else if (_command.Display != Display.Full) Plan(_command.Action, ScopeFromCommandLine());
        else _dispatcher.Invoke(() => _window!.ShowStart());
    }

    /// <summary>A quiet install is per-user unless the command line says AllUsers=1, like the MSI.</summary>
    private BundleScope ScopeFromCommandLine() =>
        _command.Action != LaunchAction.Install ? BundleScope.Default
        : Variable("AllUsers") == "1" ? BundleScope.PerMachine : BundleScope.PerUser;

    /// <summary>A bundle variable's value, or null when nobody set it (Burn throws for those).</summary>
    private string? Variable(string name) => engine.ContainsVariable(name) ? engine.GetVariableString(name) : null;

    protected override void OnPlanComplete(PlanCompleteEventArgs args)
    {
        if (args.Status < 0) { _dispatcher.Invoke(() => Finish(args.Status)); return; }
        Applying = true;
        _dispatcher.Invoke(() => _window?.ShowProgress(PlannedAction));
        engine.Apply(_window?.Handle ?? _hiddenWindow!.Handle);
    }

    protected override void OnProgress(ProgressEventArgs args)
    {
        _dispatcher.InvokeAsync(() => _window?.SetProgress(args.OverallPercentage));
        args.Cancel = _cancel;
    }

    protected override void OnExecuteProgress(ExecuteProgressEventArgs args) => args.Cancel = _cancel;

    protected override void OnCacheAcquireProgress(CacheAcquireProgressEventArgs args) => args.Cancel = _cancel;

    protected override void OnExecuteFilesInUse(ExecuteFilesInUseEventArgs args)
    {
        if (_window is null || _command.Display != Display.Full) return;
        args.Result = _dispatcher.Invoke(() => _window.AskFilesInUse());
    }

    protected override void OnApplyComplete(ApplyCompleteEventArgs args)
    {
        Applying = false;
        _dispatcher.Invoke(() => Finish(args.Status));
    }

    private void Finish(int status)
    {
        _result = status;
        if (_window is null || _command.Display != Display.Full) { _dispatcher.InvokeShutdown(); return; }
        if (status >= 0) _window.ShowDone(PlannedAction);
        else if (status == unchecked((int)0x800704C7)) _window.ShowCancelled(); // HRESULT_FROM_WIN32(ERROR_CANCELLED)
        else _window.ShowError(status);
    }
}
