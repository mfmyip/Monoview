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
        // The tab strip draws its own line under the tabs, open under the active one; the document's top line would close it.
        Tabs.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            DocumentHost.BorderThickness = new Thickness(0, Tabs.Visibility == Visibility.Visible ? 0 : 1, 0, 0);
            TabOutline.Visibility = Tabs.Visibility;
        });
        Tabs.LayoutUpdated += (_, _) => UpdateTabOutline();

        // Ctrl+= and Ctrl+- on the main keyboard (no named VirtualKey for these).
        AddAccelerator((VirtualKey)187, VirtualKeyModifiers.Control, Accel_ZoomIn);
        AddAccelerator((VirtualKey)189, VirtualKeyModifiers.Control, Accel_ZoomOut);
        AddAccelerator((VirtualKey)188, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; _ = OpenSettingsAsync(); }); // Ctrl+,
        AddAccelerator(VirtualKey.G, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; FocusPageBox(); });
        AddAccelerator(VirtualKey.P, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; _ = PrintAsync(); });
        AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, (sender, e) => { e.Handled = true; _ = ReloadAsync(); });
        AddAccelerator(VirtualKey.D, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; ShowProperties(); });
        AddAccelerator(VirtualKey.R, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; CurrentView?.Viewer.Rotate(+1); });
        AddAccelerator(VirtualKey.L, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; CurrentView?.Viewer.Rotate(-1); });
        AddAccelerator(VirtualKey.Q, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; Close(); });
        AddAccelerator(VirtualKey.F11, VirtualKeyModifiers.None, (sender, e) => { e.Handled = true; ToggleFullScreen(); });
        AddAccelerator(VirtualKey.W, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (sender, e) => { e.Handled = true; CloseAllTabs(); });
        // Esc leaves full screen; otherwise let it through (search box, autoscroll, selection).
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, (sender, e) => { if (_fullScreen) { e.Handled = true; SetFullScreen(false); } });
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

        var view = CreateDocumentView(doc);
        if (_benchmark)
            view.Viewer.FirstPageRendered += (_, _) => ReportBenchmarkAndExit();

        var tab = new TabViewItem
        {
            Header = Path.GetFileName(path),
            Tag = view,
            IconSource = new SymbolIconSource { Symbol = Symbol.Document },
        };
        ToolTipService.SetToolTip(tab, path);
        tab.ContextFlyout = BuildTabMenu(tab);
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

    private string? _tabOutlineData;

    // Redrawn on every layout pass (select, reorder, resize, close), so it only rebuilds when the shape moved.
    private void UpdateTabOutline()
    {
        if (Tabs.Visibility != Visibility.Visible) return;
        double w = Tabs.ActualWidth, b = Tabs.ActualHeight - 0.5;
        string data = FormattableString.Invariant($"M 0,{b} H {w}");
        if (Tabs.SelectedItem is TabViewItem tab && FindByName(tab, "TabContainer") is FrameworkElement box && box.ActualWidth > 0)
        {
            var r = box.TransformToVisual(Tabs).TransformBounds(new Windows.Foundation.Rect(0, 0, box.ActualWidth, box.ActualHeight));
            double x0 = r.Left + 0.5, x1 = r.Right - 0.5, t = r.Top + 0.5, flare = 8, top = 8;
            data = FormattableString.Invariant($"M 0,{b} H {x0 - flare} A {flare},{flare} 0 0 0 {x0},{b - flare} V {t + top} A {top},{top} 0 0 1 {x0 + top},{t} ")
                + FormattableString.Invariant($"H {x1 - top} A {top},{top} 0 0 1 {x1},{t + top} V {b - flare} A {flare},{flare} 0 0 0 {x1 + flare},{b} H {w}");
        }
        if (data == _tabOutlineData) return;
        _tabOutlineData = data;
        TabOutline.Data = (Microsoft.UI.Xaml.Media.Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(
            typeof(Microsoft.UI.Xaml.Media.Geometry), data);
    }

    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (FindByName(child, name) is { } found) return found;
        }
        return null;
    }

    private DocumentView CreateDocumentView(PdfDocument doc)
    {
        var view = new DocumentView(doc);
        view.Viewer.CurrentPageChanged += (s, _) => { if (ReferenceEquals(s, CurrentView?.Viewer)) UpdatePageLabel(); };
        view.Viewer.ZoomChanged += (s, _) => { if (ReferenceEquals(s, CurrentView?.Viewer)) UpdateZoomLabel(); };
        view.SearchChanged += (s, _) => { if (ReferenceEquals(s, CurrentView)) UpdateSearchCount(); };
        view.SidebarWidthChanged += (s, _) =>
        {
            foreach (var other in DocumentViews())
                if (!ReferenceEquals(other, s)) other.ApplySidebarWidth();
        };
        return view;
    }

    /// <summary>Reopens the current file from disk (e.g. after another app changed it), keeping page, zoom and layout.</summary>
    private async Task ReloadAsync()
    {
        if (Tabs.SelectedItem is not TabViewItem tab || tab.Tag is not DocumentView old) return;
        string path = old.Document.Path;
        PdfDocument doc;
        try
        {
            doc = await PdfDocument.OpenAsync(path);
        }
        catch (PdfException ex)
        {
            ShowError($"Couldn't reload {Path.GetFileName(path)}", ex.Message);
            return;
        }

        var view = CreateDocumentView(doc);
        view.Viewer.InitialState = old.Viewer.CaptureState();
        view.SetSidebarVisible(old.IsSidebarVisible);
        DocumentHost.Children.Remove(old);
        old.Dispose();
        DocumentHost.Children.Add(view);
        tab.Tag = view;
        OnCurrentDocumentChanged();
        ShowInfo($"Reloaded {Path.GetFileName(path)}");
    }

    private PrintService? _printService;
    private bool _printServiceTried;

    /// <summary>Prints with Windows' modern dialog (with preview); falls back to the classic dialog if that's unavailable.</summary>
    private async Task PrintAsync()
    {
        if (CurrentView is not { } view) return;
        if (!_printServiceTried)
        {
            _printServiceTried = true;
            _printService = PrintService.TryCreate(this);
            if (_printService is not null)
                _printService.Completed += (ok, message) =>
                {
                    if (ok) ShowInfo(message);
                    else if (message.Length > 0) ShowError(message, "The printer reported an error.");
                };
        }
        if (_printService is not null && await _printService.ShowAsync(view.Document))
        {
            StartupTrace.Mark("print_modern_dialog_shown");
            return;
        }
        StartupTrace.Mark($"print_fallback_classic service={(_printService is null ? "unavailable" : "failed")}");
        await PrintWithClassicDialogAsync(view);
    }

    private async Task PrintWithClassicDialogAsync(DocumentView view)
    {
        var result = PrintDialog.Show(WinRT.Interop.WindowNative.GetWindowHandle(this), view.Document.PageCount, view.Viewer.CurrentPage);
        if (result is null) return;
        try
        {
            int total = result.Pages.Count;
            ShowInfo($"Printing page 1 of {total}…", sticky: true);
            var progress = new Progress<int>(done => ShowInfo(done < total
                ? $"Printing page {done + 1} of {total}…"
                : $"Sent {total} page{(total == 1 ? "" : "s")} to {result.PrinterName}", sticky: done < total));
            await PdfPrinter.PrintAsync(view.Document, result.Hdc, Path.GetFileName(view.Document.Path), result.Pages, progress: progress);
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
        {
            ShowError("Printing failed", ex.Message);
        }
        finally
        {
            PdfPrinter.DeletePrinterDC(result.Hdc);
        }
    }

    private void CloseAllTabs()
    {
        foreach (var tab in Tabs.TabItems.OfType<TabViewItem>().ToList())
            CloseTab(tab);
    }

    // ---------------- Full screen ----------------

    private bool _fullScreen;

    private void ToggleFullScreen() => SetFullScreen(!_fullScreen);

    /// <summary>Full screen hides the toolbar and tabs for reading; Esc or F11 leaves it.</summary>
    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;
        _fullScreen = on;
        AppWindow.SetPresenter(on ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                                  : Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
        Toolbar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        Tabs.Visibility = !on && Tabs.TabItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (on) ShowInfo("Full screen — press Esc or F11 to exit");
        else ErrorBar.IsOpen = false;
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
        Tabs.Visibility = !_fullScreen && Tabs.TabItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        foreach (var item in new MenuFlyoutItemBase[] { ReloadItem, PrintItem, PropertiesItem, CloseTabItem, CloseAllItem, ShowInExplorerItem, CopyPathItem, ViewMenu, FindMenu, SelectAllItem })
            item.IsEnabled = hasDoc;
        SinglePageItem.IsChecked = view?.Viewer.SinglePage ?? false;
        FullScreenItem.IsChecked = _fullScreen;
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

    private void Reload_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync();
    private void Print_Click(object sender, RoutedEventArgs e) => _ = PrintAsync();
    private void Properties_Click(object sender, RoutedEventArgs e) => ShowProperties();
    private void CloseAll_Click(object sender, RoutedEventArgs e) => CloseAllTabs();
    private void RotateRight_Click(object sender, RoutedEventArgs e) => CurrentView?.Viewer.Rotate(+1);
    private void RotateLeft_Click(object sender, RoutedEventArgs e) => CurrentView?.Viewer.Rotate(-1);
    private void SinglePage_Click(object sender, RoutedEventArgs e) => CurrentView?.Viewer.SetSinglePage(SinglePageItem.IsChecked);
    private void FullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(FullScreenItem.IsChecked);

    private void ShowProperties()
    {
        if (CurrentView is { } view)
            _ = RunDialogAsync(root => PropertiesDialog.ShowAsync(root, view.Document));
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem is TabViewItem tab) CloseTab(tab);
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentView is { } view) ShowInExplorer(view.Document.Path);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentView is { } view) CopyText(view.Document.Path);
    }

    private static void ShowInExplorer(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    /// <summary>Windows' "Open with" picker for the file (to open it in another app).</summary>
    private void OpenWithAnotherApp(string path)
    {
        var info = new OPENASINFO { pcszFile = path, oaifInFlags = 0x4 /* OAIF_EXEC */ };
        SHOpenWithDialog(WinRT.Interop.WindowNative.GetWindowHandle(this), ref info);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        public string pcszFile;
        public string? pcszClass;
        public int oaifInFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hwnd, ref OPENASINFO info);

    // ---------------- Tab right-click menu ----------------

    private MenuFlyout BuildTabMenu(TabViewItem tab)
    {
        var menu = new MenuFlyout();
        string Path_() => (tab.Tag as DocumentView)?.Document.Path ?? "";
        void Select() => Tabs.SelectedItem = tab;

        MenuFlyoutItem Add(string text, Action action, string? keys = null, string? glyph = null)
        {
            var item = new MenuFlyoutItem { Text = text };
            if (keys is not null) item.KeyboardAcceleratorTextOverride = keys;
            if (glyph is not null) item.Icon = new FontIcon { Glyph = glyph };
            item.Click += (s, args) => action();
            menu.Items.Add(item);
            return item;
        }

        Add("Close tab", () => CloseTab(tab), "Ctrl+W");
        var closeOthers = Add("Close other tabs", () =>
        {
            foreach (var other in Tabs.TabItems.OfType<TabViewItem>().Where(t => t != tab).ToList()) CloseTab(other);
        });
        var closeRight = Add("Close tabs to the right", () =>
        {
            int index = Tabs.TabItems.IndexOf(tab);
            foreach (var other in Tabs.TabItems.OfType<TabViewItem>().Skip(index + 1).ToList()) CloseTab(other);
        });
        Add("Close all tabs", CloseAllTabs, "Ctrl+Shift+W");
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Copy file name", () => CopyText(Path.GetFileName(Path_())), glyph: "");
        Add("Copy file path", () => CopyText(Path_()));
        Add("Show in File Explorer", () => ShowInExplorer(Path_()), glyph: "");
        Add("Open with another app…", () => OpenWithAnotherApp(Path_()), glyph: "");
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Reload", () => { Select(); _ = ReloadAsync(); }, "F5", "");
        Add("Print…", () => { Select(); _ = PrintAsync(); }, "Ctrl+P", "");
        Add("Document properties…", () => { Select(); ShowProperties(); }, "Ctrl+D", "");

        menu.Opening += (s, args) =>
        {
            closeOthers.IsEnabled = Tabs.TabItems.Count > 1;
            closeRight.IsEnabled = Tabs.TabItems.IndexOf(tab) < Tabs.TabItems.Count - 1;
        };
        return menu;
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
        _infoTimer?.Stop();
        ErrorBar.Severity = InfoBarSeverity.Error;
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _infoTimer;

    /// <summary>A short status message in the info bar; it hides itself after a few seconds unless <paramref name="sticky"/>.</summary>
    private void ShowInfo(string message, bool sticky = false)
    {
        ErrorBar.Severity = InfoBarSeverity.Informational;
        ErrorBar.Title = message;
        ErrorBar.Message = "";
        ErrorBar.IsOpen = true;
        _infoTimer ??= CreateInfoTimer();
        _infoTimer.Stop();
        if (!sticky) _infoTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateInfoTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { if (ErrorBar.Severity == InfoBarSeverity.Informational) ErrorBar.IsOpen = false; };
        return timer;
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
                if (Arg("--page") is { } page) { await Task.Delay(300); doc.Viewer.GoToPage(int.Parse(page) - 1); await Task.Delay(400); }
                if (Arg("--sidebar") is { } tab)
                {
                    if (tab == "hidden") doc.SetSidebarVisible(false);
                    else doc.SelectSidebarTab(tab);
                }
                if (args.Contains("--select-all")) await doc.Viewer.SelectAllAsync();
                if (Arg("--rotate") is { } turns) { doc.Viewer.Rotate(int.Parse(turns)); await Task.Delay(800); }
                if (args.Contains("--single-page")) { doc.Viewer.SetSinglePage(true); await Task.Delay(300); }
                if (args.Contains("--fullscreen")) { SetFullScreen(true); await Task.Delay(800); }
                if (args.Contains("--reload"))
                {
                    Console.Out.WriteLine($"BEFORE_RELOAD page={CurrentView?.Viewer.CurrentPage + 1}");
                    await ReloadAsync();
                    await Task.Delay(1200);
                    Console.Out.WriteLine($"RELOADED page={CurrentView?.Viewer.CurrentPage + 1} zoom={CurrentView?.Viewer.Zoom:F2} rotation={CurrentView?.Viewer.ViewRotation}");
                }
                if (args.Contains("--properties")) { ShowProperties(); await Task.Delay(1000); }
                if (args.Contains("--print-dialog")) { _ = PrintAsync(); await Task.Delay(6000); }
                if (args.Contains("--settings-dialog")) { _ = OpenSettingsAsync(); await Task.Delay(800); }
                if (Arg("--dialog") is { } dialog)
                {
                    var shown = RunDialogAsync(dialog == "about" ? HelpDialogs.ShowAboutAsync : HelpDialogs.ShowShortcutsAsync);
                    await Task.WhenAny(shown, Task.Delay(800));
                    if (shown.IsFaulted) Console.Out.WriteLine($"DIALOG_ERROR {shown.Exception!.InnerException}");
                }
                if (args.Contains("--menu")) { MainMenu.ShowAt(MenuButton); await Task.Delay(500); }
                if (args.Contains("--tab-menu") && Tabs.SelectedItem is TabViewItem selectedTab)
                {
                    selectedTab.ContextFlyout.ShowAt(selectedTab);
                    await Task.Delay(500);
                }
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








