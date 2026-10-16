using System.Diagnostics;
using System.Runtime.InteropServices;
using CleanPDF.App.Viewer;
using CleanPDF.Engine;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace CleanPDF.App;

public sealed record RecentEntry(string Path, string Name, string Folder);

public sealed partial class MainWindow : Window
{
    private readonly bool _benchmark;
    private readonly DispatcherTimerLike _searchDebounce;

    public MainWindow(bool benchmark)
    {
        _benchmark = benchmark;
        StartupTrace.Mark("ctor_start");
        InitializeComponent();
        StartupTrace.Mark("xaml_loaded");

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1400));

        // Windows draws a light title bar for desktop apps unless asked; match the app's theme.
        Root.ActualThemeChanged += (_, _) => UseDarkTitleBar(Root.ActualTheme == ElementTheme.Dark);
        ApplyTheme();
        AppSettings.Changed += (_, _) => ApplyTheme();

        // Ctrl+= and Ctrl+- on the main keyboard (no named VirtualKey for these).
        AddAccelerator((VirtualKey)187, VirtualKeyModifiers.Control, Accel_ZoomIn);
        AddAccelerator((VirtualKey)189, VirtualKeyModifiers.Control, Accel_ZoomOut);
        AddAccelerator((VirtualKey)188, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; _ = OpenSettingsAsync(); }); // Ctrl+,
        AddAccelerator(VirtualKey.G, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; FocusPageBox(); });
        AddAccelerator(VirtualKey.F1, VirtualKeyModifiers.None, (sender, e) => { e.Handled = true; _ = RunDialogAsync(HelpDialogs.ShowShortcutsAsync); });

        _searchDebounce = new DispatcherTimerLike(TimeSpan.FromMilliseconds(250), () => _ = RunSearchAsync());
        Closed += (_, _) =>
        {
            foreach (var view in DocumentViews().ToList())
                view.Dispose();
        };

        UpdateToolbar();
    }

    /// <summary>Shows the start page (open button and recent files) when no document is open.</summary>
    public void ShowStartPage(bool show)
    {
        StartPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) RefreshStartPage();
    }

    private DocumentView? CurrentView => (Tabs.SelectedItem as TabViewItem)?.Tag as DocumentView;

    private IEnumerable<DocumentView> DocumentViews() =>
        Tabs.TabItems.OfType<TabViewItem>().Select(t => t.Tag).OfType<DocumentView>();

    // ---------------- Opening & tabs ----------------

    public void OpenFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            _ = OpenFileAsync(path);
    }

    /// <param name="opening">An open already started for this path (startup overlaps it with window creation).</param>
    public async Task OpenFileAsync(string path, Task<PdfDocument>? opening = null)
    {
        path = Path.GetFullPath(path);
        var existing = Tabs.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(t => t.Tag is DocumentView v && string.Equals(v.Document.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Tabs.SelectedItem = existing;
            return;
        }

        PdfDocument doc;
        try
        {
            doc = await (opening ?? PdfDocument.OpenAsync(path));
            StartupTrace.Mark("doc_opened");
        }
        catch (PdfException ex)
        {
            if (!File.Exists(path)) RecentFiles.Remove(path);
            ShowError($"Couldn't open {Path.GetFileName(path)}", ex.Message);
            ShowStartPage(Tabs.TabItems.Count == 0);
            return;
        }

        var view = new DocumentView(doc);
        view.Viewer.CurrentPageChanged += (s, _) => { if (ReferenceEquals(s, CurrentView?.Viewer)) UpdatePageLabel(); };
        view.Viewer.ZoomChanged += (s, _) => { if (ReferenceEquals(s, CurrentView?.Viewer)) UpdateZoomLabel(); };
        view.SearchChanged += (s, _) => { if (ReferenceEquals(s, CurrentView)) UpdateSearchCount(); };
        view.SidebarWidthChanged += (s, _) =>
        {
            foreach (var other in DocumentViews())
                if (!ReferenceEquals(other, s)) other.ApplySidebarWidth();
        };
        if (_benchmark)
            view.Viewer.FirstPageRendered += (_, _) => ReportBenchmarkAndExit();

        var tab = new TabViewItem
        {
            Header = Path.GetFileName(path),
            Tag = view,
            IconSource = new SymbolIconSource { Symbol = Symbol.Document },
        };
        ToolTipService.SetToolTip(tab, path);
        view.Visibility = Visibility.Collapsed;
        DocumentHost.Children.Add(view);
        // A collapsed TabView doesn't apply selection, so show it before adding the first tab.
        Tabs.Visibility = Visibility.Visible;
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
        OnCurrentDocumentChanged();
        StartupTrace.Mark("tab_added");

        if (!_benchmark)
            RecentFiles.Add(path);
        ErrorBar.IsOpen = false;
    }

    private async Task PickAndOpenAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pdf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files)
            await OpenFileAsync(file.Path);
    }

    private void CloseTab(TabViewItem tab)
    {
        if (tab.Tag is DocumentView view)
        {
            DocumentHost.Children.Remove(view);
            view.Dispose();
        }
        Tabs.TabItems.Remove(tab);
        if (Tabs.TabItems.Count == 0)
            OnCurrentDocumentChanged();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => OnCurrentDocumentChanged();

    private void OnCurrentDocumentChanged()
    {
        var current = CurrentView;
        foreach (var view in DocumentViews())
            view.Visibility = view == current ? Visibility.Visible : Visibility.Collapsed;
        ShowStartPage(current is null);
        Title = current is null ? "CleanPDF" : $"{Path.GetFileName(current.Document.Path)} - CleanPDF";
        UpdateToolbar();
    }

    private void Tabs_AddTabButtonClick(TabView sender, object args) => _ = PickAndOpenAsync();
    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) => CloseTab(args.Tab);

    private void Open_Click(object sender, RoutedEventArgs e) => _ = PickAndOpenAsync();

    private void RecentMenu_Opening(object? sender, object e)
    {
        RecentMenu.Items.Clear();
        foreach (var path in RecentFiles.Items)
        {
            var item = new MenuFlyoutItem { Text = Path.GetFileName(path), Tag = path };
            ToolTipService.SetToolTip(item, path);
            item.Click += (s, args) => _ = OpenFileAsync((string)((MenuFlyoutItem)s).Tag);
            RecentMenu.Items.Add(item);
        }
        if (RecentMenu.Items.Count == 0)
            RecentMenu.Items.Add(new MenuFlyoutItem { Text = "No recent files", IsEnabled = false });
    }

    private void RecentList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentEntry entry)
            _ = OpenFileAsync(entry.Path);
    }

    private void RefreshStartPage()
    {
        var entries = RecentFiles.Items
            .Select(p => new RecentEntry(p, Path.GetFileName(p), Path.GetDirectoryName(p) ?? ""))
            .ToList();
        RecentList.ItemsSource = entries;
        RecentHeader.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open";
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var file in items.OfType<StorageFile>())
            await OpenFileAsync(file.Path);
    }

    // ---------------- Toolbar ----------------

    private void UpdateToolbar()
    {
        // Document tools only appear when there's a document; the tab strip only when there are tabs.
        var docVisibility = CurrentView is not null ? Visibility.Visible : Visibility.Collapsed;
        SidebarButton.Visibility = SidebarSeparator.Visibility = DocTools.Visibility = SearchTools.Visibility = docVisibility;
        Tabs.Visibility = Tabs.TabItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Text = CurrentView?.SearchQuery ?? "";
        UpdatePageLabel();
        UpdateZoomLabel();
        UpdateSearchCount();
        bool registered = AppRegistration.IsRegistered();
        RegisterButton.Visibility = registered ? Visibility.Collapsed : Visibility.Visible;
        UnregisterButton.Visibility = registered ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePageLabel()
    {
        var viewer = CurrentView?.Viewer;
        if (!ReferenceEquals(FocusedElement(), PageBox))
            PageBox.Text = viewer is null ? "" : (viewer.CurrentPage + 1).ToString();
        PageCountLabel.Text = viewer is null ? "" : $"of {viewer.PageCount}";
    }

    private void UpdateZoomLabel() => ZoomLabel.Text = CurrentView is { } v ? $"{v.Viewer.Zoom * 100:0}%" : "";

    private void UpdateSearchCount()
    {
        var view = CurrentView;
        bool searched = view is not null && view.SearchQuery.Length > 0;
        SearchCount.Text = !searched ? ""
            : view!.HitCount == 0 ? "0"
            : $"{view.CurrentHitIndex + 1} of {view.HitCount}";
        // The match count and ↑/↓ only make sense once something has been searched.
        var visibility = searched ? Visibility.Visible : Visibility.Collapsed;
        SearchCount.Visibility = PrevHitButton.Visibility = NextHitButton.Visibility = visibility;
    }


    private void SidebarButton_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar() => CurrentView?.ToggleSidebar();

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => CurrentView?.Viewer.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => CurrentView?.Viewer.ZoomOut();
    private void FitWidth_Click(object sender, RoutedEventArgs e) => CurrentView?.Viewer.SetFitMode(FitMode.FitWidth);
    private void FitPage_Click(object sender, RoutedEventArgs e) => CurrentView?.Viewer.SetFitMode(FitMode.FitPage);
    private void ActualSize_Click(object sender, RoutedEventArgs e) => CurrentView?.Viewer.SetZoom(1);

    private void ZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag } && double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out double z))
            CurrentView?.Viewer.SetZoom(z);
    }

    private void PageBox_GotFocus(object sender, RoutedEventArgs e) => PageBox.SelectAll();

    private void PageBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            GoToTypedPage();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            UpdatePageLabel();
            e.Handled = true;
        }
    }

    private void PageBox_LostFocus(object sender, RoutedEventArgs e) => UpdatePageLabel();

    private void GoToTypedPage()
    {
        if (CurrentView is { } view && int.TryParse(PageBox.Text.Trim(), out int page))
            view.Viewer.GoToPage(page - 1);
        UpdatePageLabel();
    }

    // ---------------- Search ----------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CurrentView is { } view && SearchBox.Text.Trim() != view.SearchQuery)
            _searchDebounce.Restart();
    }

    private async Task RunSearchAsync()
    {
        if (CurrentView is { } view)
            await view.SearchAsync(SearchBox.Text);
    }

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (CurrentView is not { } view) return;
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (SearchBox.Text.Trim() != view.SearchQuery)
            {
                _searchDebounce.Stop();
                await view.SearchAsync(SearchBox.Text);
            }
            else if (IsShiftDown()) view.PreviousHit();
            else view.NextHit();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _searchDebounce.Stop();
            SearchBox.Text = "";
            view.ClearSearch();
        }
    }

    private void NextHit_Click(object sender, RoutedEventArgs e) => CurrentView?.NextHit();
    private void PrevHit_Click(object sender, RoutedEventArgs e) => CurrentView?.PreviousHit();

    private static bool IsShiftDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

    // ---------------- Keyboard shortcuts ----------------

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers,
        Windows.Foundation.TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accel = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accel.Invoked += handler;
        Root.KeyboardAccelerators.Add(accel);
    }

    private object? FocusedElement() => Root.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;

    private bool TextBoxHasFocus() => FocusedElement() is TextBox;

    private void Accel_Open(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; _ = PickAndOpenAsync(); }

    private void Accel_CloseTab(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (Tabs.SelectedItem is TabViewItem tab) CloseTab(tab);
        else Close();
    }

    private void Accel_NextTab(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; CycleTab(+1); }
    private void Accel_PreviousTab(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; CycleTab(-1); }

    private void CycleTab(int step)
    {
        int n = Tabs.TabItems.Count;
        if (n > 1) Tabs.SelectedIndex = ((Tabs.SelectedIndex + step) % n + n) % n;
    }

    private void Accel_Find(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        FocusSearch();
    }

    private void FocusSearch()
    {
        if (CurrentView is null) return;
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
    }

    private void Accel_FindNext(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; CurrentView?.NextHit(); }
    private void Accel_FindPrevious(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; CurrentView?.PreviousHit(); }
    private void Accel_ToggleSidebar(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; ToggleSidebar(); }

    private void Accel_Copy(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (TextBoxHasFocus() || CurrentView is not { } view || !view.Viewer.HasSelection) return;
        e.Handled = true;
        _ = view.Viewer.CopySelectionAsync();
    }

    private void Accel_SelectAll(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (TextBoxHasFocus() || CurrentView is not { } view) return;
        e.Handled = true;
        _ = view.Viewer.SelectAllAsync();
    }

    private void Accel_ZoomIn(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; CurrentView?.Viewer.ZoomIn(); }
    private void Accel_ZoomOut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; CurrentView?.Viewer.ZoomOut(); }
    private void Accel_ActualSize(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; CurrentView?.Viewer.SetZoom(1); }
    private void Accel_FitWidth(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; CurrentView?.Viewer.SetFitMode(FitMode.FitWidth); }
    private void Accel_FitPage(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; CurrentView?.Viewer.SetFitMode(FitMode.FitPage); }

    // ---------------- "Open with" registration ----------------

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Add CleanPDF to “Open with”?",
            Content = "CleanPDF will register itself for PDF files for your Windows account (no admin needed). " +
                      "To make it the default, choose it in Settings > Apps > Default apps afterwards.\n\n" +
                      $"Registered program: {Environment.ProcessPath}",
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        AppRegistration.Register();
        UpdateToolbar();
        var done = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "CleanPDF added",
            Content = "It now appears under “Open with” for PDF files. Open Default apps to make it the default?",
            PrimaryButtonText = "Open Default apps",
            CloseButtonText = "Not now",
        };
        if (await done.ShowAsync() == ContentDialogResult.Primary)
            await Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _ = OpenSettingsAsync();

    private bool _dialogOpen;

    private Task OpenSettingsAsync() => RunDialogAsync(SettingsDialog.ShowAsync);

    /// <summary>Shows one of our dialogs; WinUI allows only one ContentDialog at a time.</summary>
    private async Task RunDialogAsync(Func<XamlRoot, Task> show)
    {
        if (_dialogOpen || Root.XamlRoot is null) return;
        _dialogOpen = true;
        try
        {
            await show(Root.XamlRoot);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ---------------- ☰ menu ----------------

    private void MainMenu_Opening(object? sender, object e)
    {
        var view = CurrentView;
        bool hasDoc = view is not null;
        foreach (var item in new MenuFlyoutItemBase[] { CloseTabItem, ShowInExplorerItem, CopyPathItem, ViewMenu, FindMenu })
            item.IsEnabled = hasDoc;
        CopyItem.IsEnabled = view?.Viewer.HasSelection ?? false;
        SidebarItem.Text = view?.IsSidebarVisible == false ? "Show sidebar" : "Hide sidebar";

        RecentSubMenu.Items.Clear();
        foreach (var path in RecentFiles.Items)
        {
            var item = new MenuFlyoutItem { Text = Path.GetFileName(path), Tag = path };
            ToolTipService.SetToolTip(item, path);
            item.Click += (s, args) => _ = OpenFileAsync((string)((MenuFlyoutItem)s).Tag);
            RecentSubMenu.Items.Add(item);
        }
        if (RecentSubMenu.Items.Count == 0)
            RecentSubMenu.Items.Add(new MenuFlyoutItem { Text = "No recent files", IsEnabled = false });
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem is TabViewItem tab) CloseTab(tab);
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentView is { } view && File.Exists(view.Document.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{view.Document.Path}\"") { UseShellExecute = false });
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentView is not { } view) return;
        var package = new DataPackage();
        package.SetText(view.Document.Path);
        Clipboard.SetContent(package);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void GoToPage_Click(object sender, RoutedEventArgs e) => FocusPageBox();

    private void FocusPageBox()
    {
        if (CurrentView is null) return;
        PageBox.Focus(FocusState.Keyboard);
        PageBox.SelectAll();
    }

    private void NextTab_Click(object sender, RoutedEventArgs e) => CycleTab(+1);
    private void PreviousTab_Click(object sender, RoutedEventArgs e) => CycleTab(-1);
    private void Find_Click(object sender, RoutedEventArgs e) => FocusSearch();
    private void Copy_Click(object sender, RoutedEventArgs e) => _ = CurrentView?.Viewer.CopySelectionAsync();
    private void SelectAll_Click(object sender, RoutedEventArgs e) => _ = CurrentView?.Viewer.SelectAllAsync();
    private void Shortcuts_Click(object sender, RoutedEventArgs e) => _ = RunDialogAsync(HelpDialogs.ShowShortcutsAsync);
    private void About_Click(object sender, RoutedEventArgs e) => _ = RunDialogAsync(HelpDialogs.ShowAboutAsync);

    private void Unregister_Click(object sender, RoutedEventArgs e)
    {
        AppRegistration.Unregister();
        UpdateToolbar();
    }

    // ---------------- Misc ----------------

    private void ShowError(string title, string message)
    {
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    public void BringToFront()
    {
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    internal void ReportBenchmarkAndExit()
    {
        // Wait one frame so the page has actually been composed on screen.
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendered;

        async void OnRendered(object? sender, object e)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendered;
            var sinceStart = DateTime.Now - Process.GetCurrentProcess().StartTime;
            Console.Out.WriteLine($"FIRST_PAGE_MS={sinceStart.TotalMilliseconds:F0}");
            Console.Out.WriteLine($"WORKING_SET_MB={Environment.WorkingSet / 1024.0 / 1024.0:F0}");
            Console.Out.Flush();

            // Dev aid: --search=<text> and --screenshot=<file.png> to check the UI without clicking around.
            var args = Environment.GetCommandLineArgs();
            string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
            if (Arg("--search") is { } query && CurrentView is { } view)
            {
                SearchBox.Text = query;
                await view.SearchAsync(query);
                await Task.Delay(1500);
            }
            if (CurrentView is { } doc)
            {
                if (Arg("--zoom") is { } zoom) doc.Viewer.SetZoom(double.Parse(zoom, System.Globalization.CultureInfo.InvariantCulture));
                if (Arg("--page") is { } page) { await Task.Delay(300); doc.Viewer.GoToPage(int.Parse(page) - 1); }
                if (Arg("--sidebar") is { } tab)
                {
                    if (tab == "hidden") doc.SetSidebarVisible(false);
                    else doc.SelectSidebarTab(tab);
                }
                if (args.Contains("--select-all")) await doc.Viewer.SelectAllAsync();
                if (args.Contains("--settings-dialog")) { _ = OpenSettingsAsync(); await Task.Delay(800); }
                if (Arg("--dialog") is { } dialog)
                {
                    var shown = RunDialogAsync(dialog == "about" ? HelpDialogs.ShowAboutAsync : HelpDialogs.ShowShortcutsAsync);
                    await Task.WhenAny(shown, Task.Delay(800));
                    if (shown.IsFaulted) Console.Out.WriteLine($"DIALOG_ERROR {shown.Exception!.InnerException}");
                }
                if (args.Contains("--menu")) { MainMenu.ShowAt(MenuButton); await Task.Delay(500); }
                if (args.Contains("--autoscroll-test"))
                {
                    // Anchor mid-page with the pointer 150 px below it; the icon stays up for the screenshot.
                    double before = doc.Viewer.VerticalOffset;
                    doc.Viewer.StartAutoScroll(new Windows.Foundation.Point(500, 400));
                    doc.Viewer.UpdateAutoScrollPointer(new Windows.Foundation.Point(500, 550));
                    await Task.Delay(1000);
                    Console.Out.WriteLine($"AUTOSCROLL moved={doc.Viewer.VerticalOffset - before:F0}px in 1s, active={doc.Viewer.IsAutoScrolling}");
                }
                if (args.Contains("--wheel-test"))
                {
                    // Rapid Ctrl+wheel notches in, then out, at one point: the content under it must not drift.
                    var anchor = new Windows.Foundation.Point(300, 400);
                    var before = doc.Viewer.PageAtViewportPoint(anchor);
                    foreach (int delta in new[] { 120, 120, 120, 120, -120, 240, -360 })
                        doc.Viewer.WheelZoom(delta, anchor);
                    await Task.Delay(800);
                    var after = doc.Viewer.PageAtViewportPoint(anchor);
                    Console.Out.WriteLine($"WHEEL zoom={doc.Viewer.Zoom:F3} before=({before.Page},{before.Fx:F4},{before.Fy:F4}) after=({after.Page},{after.Fx:F4},{after.Fy:F4})");
                }
                await Task.Delay(1200);
            }
            if (Arg("--screenshot") is { } shot)
            {
                await Task.Delay(500);
                await SaveScreenshotAsync(shot);
            }
            // Keeps the window up so an outside tool can capture popups (dialogs aren't in Root's render).
            if (Arg("--hold") is { } hold) await Task.Delay(int.Parse(hold));
            Application.Current.Exit();
        }
    }

    private async Task SaveScreenshotAsync(string path)
    {
        await SaveElementAsync(Root, path);
        // Menus and dialogs live in popups outside Root; save each open one next to the main shot.
        int n = 0;
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
            if (popup.Child is { } child)
                await SaveElementAsync(child, Path.ChangeExtension(path, $".popup{n++}.png"));
    }

    private static async Task SaveElementAsync(UIElement element, string path)
    {
        var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await rtb.RenderAsync(element);
        var pixels = await rtb.GetPixelsAsync();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
        await encoder.FlushAsync();
        using var file = File.Create(path);
        await stream.AsStreamForRead().CopyToAsync(file);
    }

    /// <summary>Applies the Theme setting (or the --theme=light|dark dev flag) to the window and its title bar.</summary>
    private void ApplyTheme()
    {
        string? flag = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--theme=", StringComparison.Ordinal))?[8..];
        string theme = flag ?? AppSettings.Current.Theme;
        var requested = theme.ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        Root.RequestedTheme = requested;
        UseDarkTitleBar(requested == ElementTheme.Default
            ? Application.Current.RequestedTheme == ApplicationTheme.Dark
            : requested == ElementTheme.Dark);
    }

    private void UseDarkTitleBar(bool dark)
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>A restartable one-shot timer on the UI thread.</summary>
internal sealed class DispatcherTimerLike
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

    public DispatcherTimerLike(TimeSpan delay, Action action)
    {
        _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = delay;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => action();
    }

    public void Restart()
    {
        _timer.Stop();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}








