using System.Diagnostics;
using System.Runtime.InteropServices;
using Monoview.App.Viewer;
using Monoview.Engine;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace Monoview.App;

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
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Monoview.ico"));

        // Windows draws a light title bar for desktop apps unless asked; match the app's theme.
        Root.ActualThemeChanged += (_, _) => UseDarkTitleBar(Root.ActualTheme == ElementTheme.Dark);
        ApplyTheme();
        EventHandler themeChanged = (_, _) => ApplyTheme();
        AppSettings.Changed += themeChanged;
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
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; _ = SaveAsync(); });
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (sender, e) => { e.Handled = true; _ = SaveAsAsync(); });
        AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, (sender, e) => { e.Handled = true; _ = ReloadAsync(); });
        AddAccelerator(VirtualKey.D, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; ShowProperties(); });
        AddAccelerator(VirtualKey.R, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; CurrentView?.Viewer.Rotate(+1); });
        AddAccelerator(VirtualKey.L, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; CurrentView?.Viewer.Rotate(-1); });
        AddAccelerator(VirtualKey.Q, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; _ = App.Current.QuitAsync(); });
        // Browser-style tab and window keys.
        AddAccelerator(VirtualKey.N, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; OpenNewWindow(); });
        AddAccelerator(VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (sender, e) => { e.Handled = true; ReopenClosedTab(); });
        AddAccelerator(VirtualKey.PageDown, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; CycleTab(+1); });
        AddAccelerator(VirtualKey.PageUp, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; CycleTab(-1); });
        AddAccelerator(VirtualKey.F11, VirtualKeyModifiers.None, (sender, e) => { e.Handled = true; ToggleFullScreen(); });
        AddAccelerator(VirtualKey.W, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (sender, e) => { e.Handled = true; _ = CloseAllTabsAsync(); });
        // Esc leaves full screen; otherwise let it through (search box, autoscroll, selection).
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, (sender, e) =>
        {
            if (CurrentView?.Viewer.CancelShapeEditing() == true) e.Handled = true; // stop placing a signature first
            else if (_fullScreen) { e.Handled = true; SetFullScreen(false); }
        });
        AddAccelerator(VirtualKey.F1, VirtualKeyModifiers.None, (sender, e) => { e.Handled = true; _ = RunDialogAsync(HelpDialogs.ShowShortcutsAsync); });

        _searchDebounce = new DispatcherTimerLike(TimeSpan.FromMilliseconds(250), () => _ = RunSearchAsync());
        _hoverSwitch = new DispatcherTimerLike(TimeSpan.FromMilliseconds(500), () =>
        {
            if (_hoverTab is { } tab && Tabs.TabItems.Contains(tab)) Tabs.SelectedItem = tab;
        });
        Closed += (_, _) =>
        {
            AppSettings.Changed -= themeChanged;
            _infoTimer?.Stop();
            _searchDebounce.Stop();
            _hoverSwitch.Stop();
            foreach (var view in DocumentViews().ToList())
            {
                SaveReadingPosition(view);
                RememberClosed(view);
                view.Dispose();
            }
        };
        // The window's X: ask about unsaved changes first.
        AppWindow.Closing += async (_, e) =>
        {
            if (_exitConfirmed || !DocumentViews().Any(v => v.Document.IsModified)) return;
            e.Cancel = true;
            await CloseWindowAsync();
        };
        AddAccelerator(VirtualKey.H, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; MarkCommand(MarkupKind.Highlight); });
        // Undo/redo; in a text box they stay the text box's own.
        AddAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control, (sender, e) => { if (!TextBoxHasFocus()) { e.Handled = true; _ = StepHistoryAsync(redo: false); } });
        AddAccelerator(VirtualKey.Y, VirtualKeyModifiers.Control, (sender, e) => { if (!TextBoxHasFocus()) { e.Handled = true; _ = StepHistoryAsync(redo: true); } });
        AddAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (sender, e) => { if (!TextBoxHasFocus()) { e.Handled = true; _ = StepHistoryAsync(redo: true); } });
        AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, (sender, e) => { e.Handled = true; CurrentView?.Viewer.GoBack(); });
        AddAccelerator(VirtualKey.Right, VirtualKeyModifiers.Menu, (sender, e) => { e.Handled = true; CurrentView?.Viewer.GoForward(); });

        BuildMarkupBar();
        AddAccelerator(VirtualKey.A, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (sender, e) => { e.Handled = true; SetMarkupBarVisible(MarkupBar.Visibility != Visibility.Visible); });
        UpdateToolbar();
        UpdateHighlightSwatch();
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
        // Already open in any window: bring that tab forward instead of opening it twice.
        foreach (var window in App.Current.Windows)
        {
            var existing = window.Tabs.TabItems.OfType<TabViewItem>()
                .FirstOrDefault(t => t.Tag is DocumentView v && string.Equals(v.Document.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is null) continue;
            window.Tabs.SelectedItem = existing;
            if (window != this) window.BringToFront();
            return;
        }

        PdfDocument? doc;
        try
        {
            doc = await (opening ?? DocumentFiles.OpenAsync(path));
            StartupTrace.Mark("doc_opened");
        }
        catch (PdfPasswordException)
        {
            doc = await AskPasswordAndOpenAsync(path);
        }
        catch (PdfException ex)
        {
            if (!File.Exists(path)) RecentFiles.Remove(path);
            ShowError($"Couldn't open {Path.GetFileName(path)}", ex.Message);
            doc = null;
        }
        if (doc is null)
        {
            ShowStartPage(Tabs.TabItems.Count == 0);
            return;
        }

        var view = CreateDocumentView(doc);
        if (_benchmark)
            view.Viewer.FirstPageRendered += (_, _) => ReportBenchmarkAndExit();
        else if (AppSettings.Current.RememberLastPage && ReadingPositions.Get(path) is int lastPage and > 0 && lastPage < doc.PageCount)
            view.Viewer.InitialState = new ViewState(lastPage, 1, view.Viewer.FitMode, 0, false);

        AddTab(view);
        StartupTrace.Mark("tab_added");

        if (!_benchmark)
            RecentFiles.Add(path);
        ErrorBar.IsOpen = false;
    }

    /// <summary>Adds a tab for <paramref name="view"/> at <paramref name="index"/> (-1: the end) and selects it.</summary>
    private TabViewItem AddTab(DocumentView view, int index = -1)
    {
        var tab = new TabViewItem { Tag = view, IconSource = new SymbolIconSource { Symbol = Symbol.Document } };
        UpdateTabHeader(tab);
        tab.ContextFlyout = BuildTabMenu(tab);
        // Middle-click closes, like a browser.
        tab.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(tab).Properties.IsMiddleButtonPressed) return;
            e.Handled = true;
            _ = CloseTabAsync(tab);
        };
        // Holding a dragged page or file over a tab brings that tab forward, so it can be dropped there.
        tab.AllowDrop = true;
        tab.DragEnter += (_, _) =>
        {
            if (s_tabDrag is not null || Tabs.SelectedItem == tab) return;
            _hoverTab = tab;
            _hoverSwitch.Restart();
        };
        tab.DragLeave += (_, _) =>
        {
            if (_hoverTab == tab) _hoverSwitch.Stop();
        };
        view.Visibility = Visibility.Collapsed;
        DocumentHost.Children.Add(view);
        // A collapsed TabView doesn't apply selection, so show it before adding the first tab.
        Tabs.Visibility = Visibility.Visible;
        if (index < 0 || index > Tabs.TabItems.Count) Tabs.TabItems.Add(tab);
        else Tabs.TabItems.Insert(index, tab);
        Tabs.SelectedItem = tab;
        OnCurrentDocumentChanged();
        return tab;
    }

    private readonly DispatcherTimerLike _hoverSwitch;
    private TabViewItem? _hoverTab;

    // ---------------- Windows and moving tabs ----------------

    /// <summary>A new empty window a little below and right of this one (Ctrl+N).</summary>
    private void OpenNewWindow()
    {
        var window = App.Current.NewWindow();
        window.AppWindow.Resize(AppWindow.Size);
        window.AppWindow.Move(new Windows.Graphics.PointInt32(AppWindow.Position.X + 32, AppWindow.Position.Y + 32));
        window.ShowStartPage(true);
        window.Activate();
    }

    /// <summary>
    /// Moves a tab, with its open document, view and undo history, into <paramref name="target"/> at
    /// <paramref name="index"/> (-1: the end). A window left without tabs closes, like a browser.
    /// </summary>
    private void MoveTab(TabViewItem tab, MainWindow target, int index = -1)
    {
        if (tab.Tag is not DocumentView view || target == this) return;
        var state = view.Viewer.CaptureState();
        bool sidebar = view.IsSidebarVisible;
        var history = HistoryFor(tab);
        _histories.Remove(tab);
        DocumentHost.Children.Remove(view);
        view.Close(closeDocument: false);
        Tabs.TabItems.Remove(tab);
        OnCurrentDocumentChanged();

        var moved = target.CreateDocumentView(view.Document);
        moved.Viewer.InitialState = state;
        moved.SetSidebarVisible(sidebar);
        target._histories[target.AddTab(moved, index)] = history;
        target.BringToFront();
        if (Tabs.TabItems.Count == 0)
        {
            _exitConfirmed = true;
            Close();
        }
    }

    /// <summary>Puts a tab in a window of its own at <paramref name="at"/>, or a little below and right of this one.</summary>
    private void MoveToNewWindow(TabViewItem tab, Windows.Graphics.PointInt32? at = null)
    {
        var window = App.Current.NewWindow();
        window.AppWindow.Resize(AppWindow.Size);
        window.AppWindow.Move(at ?? new Windows.Graphics.PointInt32(AppWindow.Position.X + 32, AppWindow.Position.Y + 32));
        window.Activate();
        MoveTab(tab, window);
    }

    /// <summary>The tab being dragged, and its window, so another window can take it.</summary>
    private static (MainWindow Window, TabViewItem Tab)? s_tabDrag;
    private const string TabDragFormat = "Monoview.Tab";

    private bool IsTabFromOtherWindow(DragEventArgs e) =>
        s_tabDrag is { } drag && drag.Window != this && e.DataView.Contains(TabDragFormat);

    private void Tabs_TabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        s_tabDrag = (this, args.Tab);
        args.Data.SetData(TabDragFormat, "tab");
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void Tabs_TabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args) => s_tabDrag = null;

    private void Tabs_TabStripDragOver(object sender, DragEventArgs e)
    {
        if (!IsTabFromOtherWindow(e)) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = "Move tab here";
    }

    private void Tabs_TabStripDrop(object sender, DragEventArgs e)
    {
        if (!IsTabFromOtherWindow(e) || s_tabDrag is not { } drag) return;
        drag.Window.MoveTab(drag.Tab, this, TabIndexAt(e.GetPosition(Tabs).X));
    }

    /// <summary>Dropped away from every window: the tab gets a window of its own where it was let go.</summary>
    private void Tabs_TabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
    {
        GetCursorPos(out var cursor);
        var at = new Windows.Graphics.PointInt32(cursor.X - 160, cursor.Y - 20);
        if (Tabs.TabItems.Count == 1) AppWindow.Move(at); // the only tab: just move its window
        else MoveToNewWindow(args.Tab, at);
    }

    /// <summary>Insert before the first tab whose middle is right of <paramref name="x"/>; at the end otherwise.</summary>
    private int TabIndexAt(double x)
    {
        for (int i = 0; i < Tabs.TabItems.Count; i++)
        {
            if (Tabs.ContainerFromIndex(i) is not FrameworkElement container) continue;
            if (x < container.TransformToVisual(Tabs).TransformPoint(default).X + container.ActualWidth / 2) return i;
        }
        return Tabs.TabItems.Count;
    }

    /// <summary>Closed files, newest last, for Ctrl+Shift+T.</summary>
    private static readonly List<string> s_closedPaths = new();

    private void RememberClosed(DocumentView view)
    {
        string path = view.Document.Path;
        if (!File.Exists(path)) return;
        s_closedPaths.Remove(path);
        s_closedPaths.Add(path);
        if (s_closedPaths.Count > 20) s_closedPaths.RemoveAt(0);
    }

    private void ReopenClosedTab()
    {
        while (s_closedPaths.Count > 0)
        {
            string path = s_closedPaths[^1];
            s_closedPaths.RemoveAt(s_closedPaths.Count - 1);
            if (!File.Exists(path)) continue;
            _ = OpenFileAsync(path);
            return;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out CursorPoint point);

    /// <summary>Asks for the password until it's right or the user cancels; null on cancel.</summary>
    private async Task<PdfDocument?> AskPasswordAndOpenAsync(string path)
    {
        if (_dialogOpen || Root.XamlRoot is null) return null;
        _dialogOpen = true;
        try
        {
            string? error = null;
            while (true)
            {
                var box = new PasswordBox { PlaceholderText = "Password", Width = 320 };
                var content = new StackPanel { Spacing = 8 };
                content.Children.Add(new TextBlock { Text = $"{Path.GetFileName(path)} is protected with a password.", TextWrapping = TextWrapping.Wrap });
                content.Children.Add(box);
                if (error is not null)
                    content.Children.Add(new TextBlock { Text = error, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] });

                var dialog = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot,
                    Title = "Enter password",
                    Content = content,
                    PrimaryButtonText = "Open",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    RequestedTheme = Root.ActualTheme,
                };
                dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

                try
                {
                    return await PdfDocument.OpenAsync(path, box.Password);
                }
                catch (PdfPasswordException ex)
                {
                    error = ex.Message;
                }
                catch (PdfException ex)
                {
                    ShowError($"Couldn't open {Path.GetFileName(path)}", ex.Message);
                    return null;
                }
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>Remembers the page so the file reopens there.</summary>
    private void SaveReadingPosition(DocumentView view)
    {
        if (!_benchmark && AppSettings.Current.RememberLastPage)
            ReadingPositions.Set(view.Document.Path, view.Viewer.CurrentPage);
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
        view.Viewer.HighlighterMode = _highlighterMode;
        view.Viewer.ToolStyle = CurrentShapeStyle();
        view.Viewer.ToolChanged += (s, _) => { if (ReferenceEquals(s, CurrentView?.Viewer)) UpdateToolButtons(); };
        // A dot before the file name marks unsaved changes, as in most editors.
        view.Viewer.PagesEdited += (_, _) =>
        {
            if (TabOf(view) is { } tab) UpdateTabHeader(tab);
        };
        view.Viewer.BeforeEdit = () => TabOf(view) is { } tab ? HistoryFor(tab).CheckpointAsync(view.Document) : Task.CompletedTask;
        view.Viewer.CancelEdit = () => { if (TabOf(view) is { } tab) HistoryFor(tab).DropLast(); };
        view.PageRequested += (_, request) => _ = RunPageRequestAsync(view, request);
        view.PageEditing = _pageEditing;
        view.PageEditingChanged += (_, on) => SetPageEditing(on);
        return view;
    }

    // ---------------- Highlighter ----------------

    private bool _highlighterMode;
    private bool _syncingHighlighter;

    /// <summary>
    /// The highlight, underline and strikethrough buttons, and Ctrl+H: mark the selection that way, or with no
    /// selection turn that marking mode on, or off if it was already on.
    /// </summary>
    private void MarkCommand(MarkupKind kind)
    {
        if (CurrentView is not { } view) return;
        bool sameKind = MarkupPalette.CurrentKind == kind;
        AppSettings.Current.MarkupKind = kind.ToString();
        AppSettings.Save();
        if (view.Viewer.HasSelection)
            _ = view.Viewer.MarkSelectionAsync(kind, MarkupPalette.Current);
        else
            SetHighlighterMode(!(_highlighterMode && sameKind));
        UpdateMarkButtons();
    }

    private void SetHighlighterMode(bool on)
    {
        _highlighterMode = on;
        foreach (var view in DocumentViews())
            view.Viewer.HighlighterMode = on;
        UpdateMarkButtons();
        if (on)
        {
            string verb = MarkupPalette.CurrentKind switch
            {
                MarkupKind.Underline => "Underlining",
                MarkupKind.StrikeOut => "Striking through",
                _ => "Highlighting",
            };
            ShowInfo($"{verb} on: select text to mark it. Click the button again to stop.");
        }
    }

    /// <summary>The button of the marking mode that's on shows pressed; the others don't.</summary>
    private void UpdateMarkButtons()
    {
        var kind = MarkupPalette.CurrentKind;
        _syncingHighlighter = true;
        HighlightButton.IsChecked = _highlighterMode && kind == MarkupKind.Highlight;
        _syncingHighlighter = false;
        UnderlineButton.IsChecked = _highlighterMode && kind == MarkupKind.Underline;
        StrikeButton.IsChecked = _highlighterMode && kind == MarkupKind.StrikeOut;
    }

    private void HighlightButton_IsCheckedChanged(ToggleSplitButton sender, ToggleSplitButtonIsCheckedChangedEventArgs args)
    {
        if (_syncingHighlighter) return;
        MarkCommand(MarkupKind.Highlight);
    }

    private void UnderlineButton_Click(object sender, RoutedEventArgs e) => MarkCommand(MarkupKind.Underline);
    private void StrikeButton_Click(object sender, RoutedEventArgs e) => MarkCommand(MarkupKind.StrikeOut);

    /// <summary>The color panel under the highlighter's arrow; the color is shared by all three marking buttons.</summary>
    private void HighlightMenu_Opening(object? sender, object e)
    {
        var c = MarkupPalette.Current;
        var colors = MarkupPalette.ColorRow(HighlightMenu, c, color =>
        {
            AppSettings.Current.MarkupColor = MarkupPalette.SettingValue(color);
            AppSettings.Save();
            UpdateHighlightSwatch();
        });

        HighlightMenu.Content = colors;
    }

    // ---------------- Signatures ----------------

    /// <summary>The Sign menu: saved signatures to place, each with a delete button, then New signature.</summary>
    private void SignMenu_Opening(object? sender, object e)
    {
        var list = new StackPanel { Spacing = 6, MinWidth = 260 };
        list.Children.Add(new TextBlock { Text = "Signatures", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        var files = Signatures.Files;
        if (files.Count == 0)
            list.Children.Add(new TextBlock { Text = "No saved signatures yet.", Opacity = 0.7 });
        foreach (string file in files)
        {
            var image = new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(file)), Height = 48, MaxWidth = 220, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
            // White behind the ink, so dark signatures show in the dark theme too.
            var place = new Button
            {
                Content = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.White), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4), Child = image },
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(2),
            };
            ToolTipService.SetToolTip(place, "Place this signature: click where it goes");
            place.Click += async (_, _) =>
            {
                SignMenu.Hide();
                await BeginPlacingAsync(await Signatures.LoadAsync(file));
            };
            var delete = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 }, VerticalAlignment = VerticalAlignment.Stretch };
            ToolTipService.SetToolTip(delete, "Delete this saved signature");
            AutomationProperties.SetName(delete, "Delete signature");
            delete.Click += (_, _) =>
            {
                Signatures.Delete(file);
                SignMenu_Opening(null, EventArgs.Empty);
            };
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(delete, 1);
            row.Children.Add(place);
            row.Children.Add(delete);
            list.Children.Add(row);
        }
        var create = new Button { Content = "New signature…", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch };
        create.Click += async (_, _) =>
        {
            SignMenu.Hide();
            await NewSignatureAsync();
        };
        list.Children.Add(create);
        SignMenu.Content = list;
    }

    /// <summary>Makes a signature in the dialog, then starts placing it.</summary>
    private async Task NewSignatureAsync(string? typedName = null)
    {
        StampPicture? made = null;
        await RunDialogAsync(async root => made = await SignatureDialog.ShowAsync(root, WinRT.Interop.WindowNative.GetWindowHandle(this), typedName));
        if (made is not null) await BeginPlacingAsync(made);
    }

    private Task BeginPlacingAsync(StampPicture picture)
    {
        if (CurrentView is not { } view) return Task.CompletedTask;
        SetHighlighterMode(false);
        view.Viewer.Tool = MarkupTool.Select;
        view.Viewer.PendingStamp = picture;
        ShowInfo("Click where the signature goes. Drag it to move, drag its corner to resize, Delete removes it. Esc cancels.");
        return Task.CompletedTask;
    }

    private void UpdateHighlightSwatch()
    {
        var c = MarkupPalette.Current;
        var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
        HighlightSwatch.Fill = UnderlineSwatch.Fill = StrikeSwatch.Fill = brush;
    }

    /// <summary>Reopens the current file from disk (e.g. after another app changed it), keeping page, zoom and layout.</summary>
    private async Task ReloadAsync()
    {
        if (Tabs.SelectedItem is not TabViewItem tab || tab.Tag is not DocumentView old) return;
        if (old.Document.IsModified && !await ConfirmAsync("Discard changes and reload?",
                "The file is reopened from disk and your unsaved changes are lost.", "Reload"))
            return;
        if (await ReopenAsync(tab, old, old.Document.Path))
            ShowInfo($"Reloaded {Path.GetFileName(old.Document.Path)}");
    }

    /// <summary>Opens <paramref name="path"/> in place of <paramref name="old"/>, keeping page, zoom, rotation and sidebar.</summary>
    private async Task<bool> ReopenAsync(TabViewItem tab, DocumentView old, string path)
    {
        PdfDocument doc;
        try
        {
            doc = await DocumentFiles.OpenAsync(path, old.Document.Password);
        }
        catch (PdfException ex)
        {
            ShowError($"Couldn't open {Path.GetFileName(path)}", ex.Message);
            return false;
        }

        ReplaceView(tab, old, doc, closeOldDocument: true);
        return true;
    }

    /// <summary>
    /// Saves the current document to <paramref name="target"/>, or to its own file when null. Writes a
    /// temp file next to the target first and swaps it in, so a failed save never damages the original.
    /// </summary>
    private async Task SaveAsync(string? target = null)
    {
        if (Tabs.SelectedItem is not TabViewItem tab || tab.Tag is not DocumentView view) return;
        if (target is null && !view.Document.IsModified) return; // nothing to save
        if (target is null && DocumentFiles.IsImage(view.Document.Path))
        {
            await SaveAsAsync(); // never write PDF bytes into a picture file
            return;
        }
        string source = view.Document.Path;
        target = Path.GetFullPath(target ?? source);
        string temp = Path.Combine(Path.GetDirectoryName(target)!, $"~{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await view.Document.SaveCopyAsync(temp);
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            File.Delete(temp);
            ShowError($"Couldn't save {Path.GetFileName(target)}", ex.Message);
            return;
        }

        // PDFium keeps the open file locked, so release it before replacing it.
        if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
            await view.Document.CloseAsync();
        try
        {
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Delete(temp);
            ShowError($"Couldn't save {Path.GetFileName(target)}", ex.Message);
            target = source;
        }

        bool reopened = await ReopenAsync(tab, view, target);
        if (reopened) HistoryFor(tab).MarkAllUnsaved(); // the file on disk now matches only the current state
        if (reopened && target != source)
        {
            if (!_benchmark) RecentFiles.Add(target);
            ShowInfo($"Saved as {Path.GetFileName(target)}");
        }
        else if (target == source)
        {
            ShowInfo($"Saved {Path.GetFileName(target)}");
        }
    }

    private async Task SaveAsAsync()
    {
        if (CurrentView is not { } view) return;
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(view.Document.Path) };
        picker.FileTypeChoices.Add("PDF document", new List<string> { ".pdf" });
        try
        {
            // Start in the file's own folder. A picture's own file would suggest its picture extension.
            if (!DocumentFiles.IsImage(view.Document.Path))
                picker.SuggestedSaveFile = await StorageFile.GetFileFromPathAsync(view.Document.Path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or ArgumentException)
        {
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        await WaitForModifierKeysUpAsync();
        if (await picker.PickSaveFileAsync() is { } file)
            await SaveAsync(file.Path);
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

    /// <summary>Closes the tabs one by one, asking about unsaved changes; stops at the first Cancel.</summary>
    private async Task<bool> CloseTabsAsync(IEnumerable<TabViewItem> tabs)
    {
        foreach (var tab in tabs.ToList())
            if (!await CloseTabAsync(tab))
                return false;
        return true;
    }

    private Task<bool> CloseAllTabsAsync() => CloseTabsAsync(Tabs.TabItems.OfType<TabViewItem>());

    /// <summary>Closes this window after asking about unsaved changes (the window's X). False when cancelled.</summary>
    public async Task<bool> CloseWindowAsync()
    {
        if (!await CloseAllTabsAsync()) return false;
        _exitConfirmed = true;
        Close();
        return true;
    }

    private bool _exitConfirmed;

    /// <summary>For a tab with unsaved changes: Save, Don't save or Cancel. Returns false on Cancel.</summary>
    private async Task<bool> ConfirmDiscardAsync(TabViewItem tab)
    {
        if (tab.Tag is not DocumentView view || !view.Document.IsModified) return true;
        if (_dialogOpen || Root.XamlRoot is null) return false;
        Tabs.SelectedItem = tab;
        string name = Path.GetFileName(view.Document.Path);
        _dialogOpen = true;
        ContentDialogResult answer;
        try
        {
            answer = await new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = $"Save changes to {name}?",
                Content = "Your changes will be lost if you don't save them.",
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Don't save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = Root.ActualTheme,
            }.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
        if (answer == ContentDialogResult.None) return false;
        if (answer == ContentDialogResult.Secondary) return true;
        await SaveAsync();
        // Save replaces the tab's document; if it failed, the changes are still there.
        return tab.Tag is DocumentView saved && !saved.Document.IsModified;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        if (_dialogOpen || Root.XamlRoot is null) return false;
        _dialogOpen = true;
        try
        {
            return await new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = title,
                Content = message,
                PrimaryButtonText = action,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = Root.ActualTheme,
            }.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task<bool> CloseTabAsync(TabViewItem tab)
    {
        if (!await ConfirmDiscardAsync(tab)) return false;
        CloseTab(tab);
        return true;
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
        foreach (string extension in DocumentFiles.Extensions)
            picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        await WaitForModifierKeysUpAsync();
        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files)
            await OpenFileAsync(file.Path);
    }

    private void CloseTab(TabViewItem tab)
    {
        if (tab.Tag is DocumentView view)
        {
            SaveReadingPosition(view);
            RememberClosed(view);
            DocumentHost.Children.Remove(view);
            view.Dispose();
        }
        Tabs.TabItems.Remove(tab);
        _histories.Remove(tab);
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
        Title = current is null ? "Monoview" : $"{Path.GetFileName(current.Document.Path)} - Monoview";
        UpdateToolbar();
    }

    private void Tabs_AddTabButtonClick(TabView sender, object args) => _ = PickAndOpenAsync();
    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) => _ = CloseTabAsync(args.Tab);

    private void Open_Click(object sender, RoutedEventArgs e) => _ = PickAndOpenAsync();

    private void RecentMenu_Opening(object? sender, object e)
    {
        FillRecentItems(RecentMenu.Items);
    }

    /// <summary>The recent files as menu items, then "Clear recent files" (the toolbar's list and the ☰ submenu).</summary>
    private void FillRecentItems(IList<MenuFlyoutItemBase> items)
    {
        items.Clear();
        foreach (var path in RecentFiles.Items)
        {
            var item = new MenuFlyoutItem { Text = Path.GetFileName(path) };
            ToolTipService.SetToolTip(item, path);
            item.Click += (s, args) => _ = OpenFileAsync(path);
            items.Add(item);
        }
        if (items.Count == 0)
        {
            items.Add(new MenuFlyoutItem { Text = "No recent files", IsEnabled = false });
            return;
        }
        items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "Clear recent files", Icon = new FontIcon { Glyph = "\uE74D" } };
        clear.Click += (s, args) => ClearRecentFiles();
        items.Add(clear);
    }

    private static void ClearRecentFiles()
    {
        RecentFiles.Clear();
        foreach (var window in App.Current.Windows)
            window.RefreshStartPage();
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e) => ClearRecentFiles();

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
        RecentHeader.Visibility = ClearRecentButton.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (IsTabFromOtherWindow(e))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = "Move tab here";
            return;
        }
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open";
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (IsTabFromOtherWindow(e) && s_tabDrag is { } drag)
        {
            drag.Window.MoveTab(drag.Tab, this);
            return;
        }
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
        MarkupBar.Visibility = CurrentView is not null && AppSettings.Current.MarkupBarVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateToolButtons();
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
            view.Viewer.NavigateTo(page - 1);
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
        if (Tabs.SelectedItem is TabViewItem tab) _ = CloseTabAsync(tab);
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
        if (TextBoxHasFocus() || CurrentView is not { } view) return;
        if (view.Viewer.FormHasFocus)
        {
            e.Handled = true;
            _ = view.Viewer.CopyFromFieldAsync();
            return;
        }
        if (!view.Viewer.HasSelection) return;
        e.Handled = true;
        _ = view.Viewer.CopySelectionAsync();
    }

    private void Accel_SelectAll(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (TextBoxHasFocus() || CurrentView is not { } view) return;
        e.Handled = true;
        _ = view.Viewer.FormHasFocus ? view.Viewer.SelectAllInFieldAsync() : view.Viewer.SelectAllAsync();
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
            Title = "Add Monoview to “Open with”?",
            Content = "Monoview will register itself for PDF files for your Windows account (no admin needed). " +
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
            Title = "Monoview added",
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
        foreach (var item in new MenuFlyoutItemBase[] { SaveAsItem, ReloadItem, PrintItem, PropertiesItem, CloseTabItem, CloseAllItem, ShowInExplorerItem, CopyPathItem, ViewMenu, FindMenu, SelectAllItem })
            item.IsEnabled = hasDoc;
        SaveItem.IsEnabled = view?.Document.IsModified ?? false;
        var history = Tabs.SelectedItem is TabViewItem selected && view is not null ? HistoryFor(selected) : null;
        UndoItem.IsEnabled = history?.CanUndo ?? false;
        RedoItem.IsEnabled = history?.CanRedo ?? false;
        PagesMenu.IsEnabled = view is not null;
        EditPagesMenuItem.IsChecked = _pageEditing;
        foreach (var item in PagesMenu.Items.OfType<MenuFlyoutItem>().Where(i => i is not ToggleMenuFlyoutItem))
            item.IsEnabled = _pageEditing || !PageActions.NeedsEditing((string?)item.Tag);
        SinglePageItem.IsChecked = view?.Viewer.SinglePage ?? false;
        TwoPageItem.IsChecked = view?.Viewer.TwoPage ?? false;
        BackItem.IsEnabled = view?.Viewer.CanGoBack ?? false;
        ForwardItem.IsEnabled = view?.Viewer.CanGoForward ?? false;
        FullScreenItem.IsChecked = _fullScreen;
        CopyItem.IsEnabled = view?.Viewer.HasSelection ?? false;
        SidebarItem.Text = view?.IsSidebarVisible == false ? "Show sidebar" : "Hide sidebar";

        FillRecentItems(RecentSubMenu.Items);
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync();
    private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveAsync();
    private void SaveAs_Click(object sender, RoutedEventArgs e) => _ = SaveAsAsync();
    private void Print_Click(object sender, RoutedEventArgs e) => _ = PrintAsync();
    private void Properties_Click(object sender, RoutedEventArgs e) => ShowProperties();
    private void CloseAll_Click(object sender, RoutedEventArgs e) => _ = CloseAllTabsAsync();
    private void RotateRight_Click(object sender, RoutedEventArgs e) => CurrentView?.Viewer.Rotate(+1);
    private void RotateLeft_Click(object sender, RoutedEventArgs e) => CurrentView?.Viewer.Rotate(-1);
    private void SinglePage_Click(object sender, RoutedEventArgs e) => CurrentView?.Viewer.SetSinglePage(SinglePageItem.IsChecked);
    private void TwoPage_Click(object sender, RoutedEventArgs e) => CurrentView?.Viewer.SetTwoPage(TwoPageItem.IsChecked);
    private void Back_Click(object sender, RoutedEventArgs e) => CurrentView?.Viewer.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => CurrentView?.Viewer.GoForward();

    private void SearchOptions_Opening(object? sender, object e)
    {
        MatchCaseItem.IsChecked = AppSettings.Current.SearchMatchCase;
        WholeWordItem.IsChecked = AppSettings.Current.SearchWholeWord;
    }

    private void SearchOption_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.SearchMatchCase = MatchCaseItem.IsChecked;
        AppSettings.Current.SearchWholeWord = WholeWordItem.IsChecked;
        AppSettings.Save();
        if (CurrentView is { SearchQuery.Length: > 0 } view)
            _ = view.SearchAsync(view.SearchQuery, force: true);
    }
    private void FullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(FullScreenItem.IsChecked);

    private void ShowProperties()
    {
        if (CurrentView is { } view)
            _ = RunDialogAsync(root => PropertiesDialog.ShowAsync(root, view.Document));
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem is TabViewItem tab) _ = CloseTabAsync(tab);
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

        Add("Close tab", () => _ = CloseTabAsync(tab), "Ctrl+W");
        var closeOthers = Add("Close other tabs", () =>
        {
            _ = CloseTabsAsync(Tabs.TabItems.OfType<TabViewItem>().Where(t => t != tab));
        });
        var closeRight = Add("Close tabs to the right", () =>
        {
            int index = Tabs.TabItems.IndexOf(tab);
            _ = CloseTabsAsync(Tabs.TabItems.OfType<TabViewItem>().Skip(index + 1));
        });
        Add("Close all tabs", () => _ = CloseAllTabsAsync(), "Ctrl+Shift+W");
        Add("Reopen closed tab", ReopenClosedTab, "Ctrl+Shift+T");
        var moveOut = Add("Move to new window", () => MoveToNewWindow(tab), glyph: "\uE8A7");
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
            closeOthers.IsEnabled = moveOut.IsEnabled = Tabs.TabItems.Count > 1;
            closeRight.IsEnabled = Tabs.TabItems.IndexOf(tab) < Tabs.TabItems.Count - 1;
        };
        return menu;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _ = App.Current.QuitAsync();
    private void NewWindow_Click(object sender, RoutedEventArgs e) => OpenNewWindow();

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

    private static bool s_benchmarkStarted;

    internal void ReportBenchmarkAndExit()
    {
        // Once: files the dev checks open fire this again.
        if (s_benchmarkStarted) return;
        s_benchmarkStarted = true;
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
            if (args.Contains("--match-case")) AppSettings.Current.SearchMatchCase = true;
            if (args.Contains("--whole-word")) AppSettings.Current.SearchWholeWord = true;
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
                if (args.Contains("--two-page")) { doc.Viewer.SetTwoPage(true); await Task.Delay(800); }
                if (args.Contains("--live-text-test"))
                {
                    // Scans and photos: the text Windows OCR finds, as selection, copy and search see it.
                    var watch = Stopwatch.StartNew();
                    var text = await doc.Document.GetTextAsync(doc.Viewer.CurrentPage);
                    Console.Out.WriteLine($"LIVETEXT ms={watch.ElapsedMilliseconds} chars={text.Length} text={text.Text.ReplaceLineEndings(" | ")}");
                }
                if (args.Contains("--highlight-test"))
                {
                    // Select the first words of page 1 and highlight them, as a user would with Ctrl+H.
                    await doc.Viewer.SelectAllAsync();
                    await doc.Viewer.MarkSelectionAsync(MarkupKind.Highlight, MarkupPalette.Current);
                    await Task.Delay(1500);
                    Console.Out.WriteLine($"HIGHLIGHT modified={doc.Document.IsModified} tab={(Tabs.SelectedItem as TabViewItem)?.Header}");
                }
                if (args.Contains("--undo-test"))
                {
                    double? Offset() => CurrentView?.Viewer.CaptureState().OffsetY;
                    CurrentView?.Viewer.GoToPage(CurrentView.Viewer.CurrentPage, normalizedY: 0.4); // part way down a page, not at its top
                    await Task.Delay(500);
                    double? before = Offset();
                    CurrentView?.SelectSidebarTab("pages");
                    await StepHistoryAsync(redo: false);
                    await Task.Delay(1000);
                    double? undone = Offset();
                    await StepHistoryAsync(redo: true);
                    await Task.Delay(1000);
                    Console.Out.WriteLine($"UNDO_SCROLL before={before:F0} after_undo={undone:F0} after_redo={Offset():F0} views={DocumentHost.Children.OfType<DocumentView>().Count()}");
                }
                if (args.Contains("--edit-pages")) { SetPageEditing(true); await Task.Delay(500); }
                if (Arg("--demo") is { } demoFolder)
                {
                    // A paced walk through recent features, for a screen recording: <folder> holds out.pdf and test-image.png.
                    var size = new Windows.Graphics.SizeInt32(1400, 1300);
                    AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(100, 100, size.Width, size.Height));
                    async Task Pause(int ms = 1600) => await Task.Delay(ms);
                    var manual = CurrentView!;
                    await Pause(2000);
                    HighlightMenu.ShowAt(HighlightButton);
                    await Pause(2000);
                    HighlightMenu.Hide();
                    await manual.Viewer.SelectAllAsync();
                    await manual.Viewer.MarkSelectionAsync(MarkupKind.Highlight, new MarkupColor(110, 220, 90));
                    await Pause(2000);
                    await StepHistoryAsync(redo: false);
                    await Pause();
                    await StepHistoryAsync(redo: true);
                    await Pause();
                    await OpenFileAsync(Path.Combine(demoFolder, "test-image.png"));
                    await Pause(2200);
                    await OpenFileAsync(Path.Combine(demoFolder, "out.pdf"));
                    SetPageEditing(true);
                    await Pause();
                    await RunPageRequestAsync(CurrentView!, new PageRequest(PageAction.CopyFrom, [0, 1], 0, Source: manual.Document));
                    await Pause(2200);
                    MoveToNewWindow((TabViewItem)Tabs.SelectedItem, new Windows.Graphics.PointInt32(100 + size.Width + 20, 100));
                    await Pause(2500);
                    var other = App.Current.Windows.First(w => w != this);
                    other.MoveTab((TabViewItem)other.Tabs.SelectedItem, this);
                    await Pause(2000);
                    var picture = Tabs.TabItems.OfType<TabViewItem>().First(t => t.Tag is DocumentView { Document.Path: var p } && p.EndsWith(".png"));
                    await CloseTabAsync(picture);
                    await Pause();
                    ReopenClosedTab();
                    await Pause(2200);
                    SetPageEditing(false);
                    foreach (var modified in Tabs.TabItems.OfType<TabViewItem>().Where(t => t.Tag is DocumentView { Document.IsModified: true }).ToList()) CloseTab(modified);
                    await Pause(800);
                }
                if (Arg("--tabs-test") is { } secondFile)
                {
                    string Windows_() => string.Join(" | ", App.Current.Windows.Select(w => string.Join(",", w.Tabs.TabItems.OfType<TabViewItem>()
                        .Select(t => $"{t.Header}:{(t.Tag as DocumentView)?.Document.PageCount}p"))));
                    var first = CurrentView!;
                    await OpenFileAsync(secondFile);
                    await Task.Delay(800);
                    Console.Out.WriteLine($"TABS opened second: {Windows_()}");
                    await RunPageRequestAsync(CurrentView!, new PageRequest(PageAction.CopyFrom, [0, 1], 0, Source: first.Document));
                    await Task.Delay(800);
                    Console.Out.WriteLine($"TABS copied 2 pages in: {Windows_()} undo={HistoryFor((TabViewItem)Tabs.SelectedItem).CanUndo}");
                    MoveToNewWindow((TabViewItem)Tabs.SelectedItem);
                    await Task.Delay(1000);
                    Console.Out.WriteLine($"TABS moved to new window: {Windows_()}");
                    var other = App.Current.Windows.First(w => w != this);
                    Console.Out.WriteLine($"TABS undo kept in new window: {other.HistoryFor((TabViewItem)other.Tabs.SelectedItem).CanUndo}");
                    other.MoveTab((TabViewItem)other.Tabs.SelectedItem, this);
                    await Task.Delay(1000);
                    Console.Out.WriteLine($"TABS moved back, empty window closed: {Windows_()} windows={App.Current.Windows.Count}");
                    var firstTab = Tabs.TabItems.OfType<TabViewItem>().First(t => t.Tag == first);
                    await CloseTabAsync(firstTab);
                    ReopenClosedTab();
                    await Task.Delay(1000);
                    Console.Out.WriteLine($"TABS closed and reopened first: {Windows_()}");
                    foreach (var modified in Tabs.TabItems.OfType<TabViewItem>().Where(t => t.Tag is DocumentView { Document.IsModified: true }).ToList()) CloseTab(modified);
                }
                if (Arg("--insert-test") is { } insertPath)
                {
                    await RunPageRequestAsync(CurrentView!, new PageRequest(PageAction.InsertFromFile, [], 0, insertPath));
                    await Task.Delay(800);
                    Console.Out.WriteLine($"INSERTED count={CurrentView?.Document.PageCount} size1={CurrentView?.Document.PageSizes[0]} error={(ErrorBar.Severity == InfoBarSeverity.Error && ErrorBar.IsOpen ? ErrorBar.Message : "none")}");
                }
                if (Arg("--export-test") is { } exportTarget)
                {
                    var written = await ExportImagesAsync(CurrentView!.Document, [0, 1], exportTarget);
                    Console.Out.WriteLine($"EXPORTED {string.Join(" | ", written.Select(f => $"{Path.GetFileName(f)} {new FileInfo(f).Length} bytes"))}");
                }
                if (Arg("--pages-test") is { } insertFile)
                {
                    async Task Step(string name, Func<Task> action)
                    {
                        await action();
                        await Task.Delay(700);
                        var v = CurrentView!;
                        var h = HistoryFor((TabViewItem)Tabs.SelectedItem);
                        Console.Out.WriteLine($"PAGES {name}: count={v.Document.PageCount} modified={v.Document.IsModified} undo={h.CanUndo} redo={h.CanRedo} size1={v.Document.PageSizes[0]}");
                    }
                    await Step("delete page 2", () => RunPageRequestAsync(CurrentView!, new PageRequest(PageAction.Delete, [1])));
                    await Step("move page 1 to end", () => RunPageRequestAsync(CurrentView!, new PageRequest(PageAction.Move, [0], CurrentView!.Document.PageCount - 1)));
                    await Step("rotate page 1", () => RunPageRequestAsync(CurrentView!, new PageRequest(PageAction.RotateRight, [0])));
                    await Step("insert file at 0", () => RunPageRequestAsync(CurrentView!, new PageRequest(PageAction.InsertFromFile, [], 0, insertFile)));
                    await Step("undo", () => StepHistoryAsync(redo: false));
                    await Step("undo", () => StepHistoryAsync(redo: false));
                    await Step("redo", () => StepHistoryAsync(redo: true));
                }
                if (args.Contains("--save")) { await SaveAsync(); await Task.Delay(800); }
                if (Arg("--save-to") is { } saveTo) { await SaveAsync(saveTo); await Task.Delay(800); }
                if (args.Contains("--save") || Arg("--save-to") is not null)
                    Console.Out.WriteLine($"SAVED tab={(Tabs.SelectedItem as TabViewItem)?.Header} path={CurrentView?.Document.Path} pages={CurrentView?.Document.PageCount} page={CurrentView?.Viewer.CurrentPage + 1} error={(ErrorBar.Severity == InfoBarSeverity.Error && ErrorBar.IsOpen ? ErrorBar.Message : "none")}");
                if (Arg("--password-prompt") is { } locked) { _ = AskPasswordAndOpenAsync(locked); await Task.Delay(800); }
                if (args.Contains("--history-test"))
                {
                    // Jump to page 6, then Back and Forward: should read 6, start page, 6.
                    int start = doc.Viewer.CurrentPage + 1;
                    doc.Viewer.NavigateTo(5);
                    await Task.Delay(500);
                    int jumped = doc.Viewer.CurrentPage + 1;
                    doc.Viewer.GoBack();
                    await Task.Delay(500);
                    int back = doc.Viewer.CurrentPage + 1;
                    doc.Viewer.GoForward();
                    await Task.Delay(500);
                    Console.Out.WriteLine($"HISTORY start={start} jumped={jumped} back={back} forward={doc.Viewer.CurrentPage + 1} canBack={doc.Viewer.CanGoBack}");
                }
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
                if (Arg("--sign-test") is { } signaturePng)
                {
                    var picture = await Signatures.LoadAsync(signaturePng);
                    await CurrentView!.Viewer.PlaceStampAsync(picture, 0, 0.7, 0.42);
                    await Task.Delay(1200);
                    Console.Out.WriteLine($"SIGNED stamps={(await CurrentView.Document.GetShapesAsync(0)).Count} modified={CurrentView.Document.IsModified} undo={HistoryFor((TabViewItem)Tabs.SelectedItem).CanUndo}");
                }
                if (args.Contains("--shapes-test"))
                {
                    // One of each drawing tool on page 1, in the page as shown, then the markup bar for the screenshot.
                    // Laid out on the first page of the Alice sample so it reads like review notes.
                    SetMarkupBarVisible(true);
                    var viewer = CurrentView!.Viewer;
                    var red = new MarkupColor(214, 48, 49);
                    var blue = new MarkupColor(30, 100, 200);
                    await CurrentView.Document.AddMarkupAsync(0, MarkupKind.Highlight, new MarkupColor(255, 226, 80),
                        [new NormRect(0.580f, 0.340f, 0.766f, 0.360f), new NormRect(0.117f, 0.362f, 0.355f, 0.382f)]);
                    await CurrentView.Document.AddMarkupAsync(0, MarkupKind.Underline, blue, [new NormRect(0.458f, 0.418f, 0.693f, 0.438f)]);
                    viewer.ToolStyle = new ShapeStyle(red, null, 2.5f);
                    await viewer.DrawAsync(MarkupTool.Ellipse, 0, [(0.362, 0.153), (0.540, 0.191)]);
                    var bracket = Enumerable.Range(0, 30).Select(i => (0.088 + 0.004 * Math.Sin(i / 2.5), 0.398 + i * 0.0064)).ToList();
                    await viewer.DrawAsync(MarkupTool.Ink, 0, bracket);
                    await viewer.DrawAsync(MarkupTool.Line, 0, [(0.117, 0.722), (0.482, 0.722)]);
                    viewer.ToolStyle = new ShapeStyle(blue, null, 2);
                    await viewer.DrawAsync(MarkupTool.Rectangle, 0, [(0.103, 0.194), (0.731, 0.286)]);
                    await viewer.DrawAsync(MarkupTool.Arrow, 0, [(0.79, 0.43), (0.70, 0.428)]);
                    await CurrentView.Document.AddNoteAsync(0, 0.76, 0.20, "Great opening line.", new MarkupColor(255, 210, 0));
                    await CurrentView.Document.AddTextBoxAsync(0, new NormRect(0.795f, 0.408f, 0.985f, 0.452f), "Use this quote", new ShapeStyle(blue, new MarkupColor(232, 241, 252), 1, 13));
                    viewer.ReplaceDocument(CurrentView.Document); // redraw after the direct engine calls
                    await Task.Delay(1500);
                    var kinds = (await CurrentView.Document.GetShapesAsync(0)).Select(s => s.Kind);
                    Console.Out.WriteLine($"SHAPES {string.Join(",", kinds)} undo={HistoryFor((TabViewItem)Tabs.SelectedItem).CanUndo} tool={viewer.Tool}");
                }
                if (args.Contains("--form-test"))
                {
                    // Types into the first text field and checks the first check box, through the document's form engine.
                    var form = CurrentView!.Document;
                    var fields = await form.GetFormFieldsAsync(0);
                    Console.Out.WriteLine($"FORM has={form.HasForm} fields={string.Join(",", fields.Select(f => $"{f.Name}:{f.Kind}"))}");
                    if (fields.FirstOrDefault(f => f.Kind == FormFieldKind.Text) is { } text)
                    {
                        var (x, y) = ((text.Rect.Left + text.Rect.Right) / 2, (text.Rect.Top + text.Rect.Bottom) / 2);
                        await form.FormPointerAsync(0, FormPointer.Down, x, y);
                        await form.FormPointerAsync(0, FormPointer.Up, x, y);
                        foreach (char c in "Grace Hopper") await form.FormCharAsync(c);
                    }
                    if (fields.FirstOrDefault(f => f.Kind == FormFieldKind.CheckBox) is { } box)
                    {
                        var (x, y) = ((box.Rect.Left + box.Rect.Right) / 2, (box.Rect.Top + box.Rect.Bottom) / 2);
                        await form.FormPointerAsync(0, FormPointer.Down, x, y);
                        await form.FormPointerAsync(0, FormPointer.Up, x, y);
                    }
                    await Task.Delay(1000);
                    var after = await form.GetFormFieldsAsync(0);
                    Console.Out.WriteLine($"FORM values={string.Join(",", after.Select(f => $"{f.Name}={f.Value}{(f.IsChecked ? "[x]" : "")}"))} modified={form.IsModified}");
                }
                if (args.Contains("--crop-test"))
                {
                    var before = CurrentView!.Document.PageSizes[0];
                    await CurrentView.Viewer.DrawAsync(MarkupTool.Crop, 0, [(0.1, 0.1), (0.9, 0.5)]);
                    await Task.Delay(1200);
                    var cropped = CurrentView!.Document.PageSizes[0];
                    await StepHistoryAsync(redo: false);
                    await Task.Delay(1000);
                    Console.Out.WriteLine($"CROP before={before} cropped={cropped} after_undo={CurrentView!.Document.PageSizes[0]}");
                }
                if (args.Contains("--sign-menu")) { SignMenu.ShouldConstrainToRootBounds = true; SignMenu.ShowAt(SignButton); await Task.Delay(1000); }
                if (Arg("--sign-dialog") is { } typedName) { _ = NewSignatureAsync(typedName.Length > 0 ? typedName : null); await Task.Delay(1200); }
                if (args.Contains("--highlight-menu")) { HighlightMenu.ShouldConstrainToRootBounds = true; HighlightMenu.ShowAt(HighlightButton); await Task.Delay(1000); }
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
            // Close like a user would, so views stop their timers first; exiting with timers live can crash.
            _exitConfirmed = true;
            Close();
        }
    }

    private async Task SaveScreenshotAsync(string path)
    {
        await SaveElementAsync(Root, path);
        // Menus and dialogs live in popups outside Root; save each open one next to the main shot.
        int n = 0;
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
            if (popup.Child is { } child)
            {
                string file = Path.ChangeExtension(path, $".popup{n++}.png");
                // A flyout in its own window renders empty as a whole; its content still renders.
                if (!await SaveElementAsync(child, file) && child is ContentControl { Content: UIElement content })
                    await SaveElementAsync(content, file);
            }
    }

    private static async Task<bool> SaveElementAsync(UIElement element, string path)
    {
        var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await rtb.RenderAsync(element);
        if (rtb.PixelWidth == 0) return false;
        var pixels = await rtb.GetPixelsAsync();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
        await encoder.FlushAsync();
        using var file = File.Create(path);
        await stream.AsStreamForRead().CopyToAsync(file);
        return true;
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

    /// <summary>
    /// A file picker opened from a shortcut while Shift or Ctrl is still down never sees that key go up,
    /// so it types as if the key were held. Wait, up to a second and a half, until the keys are released.
    /// </summary>
    private static async Task WaitForModifierKeysUpAsync()
    {
        const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        for (int i = 0; i < 100 && new[] { VK_SHIFT, VK_CONTROL, VK_MENU }.Any(k => GetAsyncKeyState(k) < 0); i++)
            await Task.Delay(15);
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
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








