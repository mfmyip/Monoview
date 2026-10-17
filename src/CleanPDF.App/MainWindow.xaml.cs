using System.Diagnostics;
using System.Runtime.InteropServices;
using CleanPDF.App.Viewer;
using CleanPDF.Engine;
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
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "CleanPDF.ico"));

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
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; _ = SaveAsync(); });
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (sender, e) => { e.Handled = true; _ = SaveAsAsync(); });
        AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, (sender, e) => { e.Handled = true; _ = ReloadAsync(); });
        AddAccelerator(VirtualKey.D, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; ShowProperties(); });
        AddAccelerator(VirtualKey.R, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; CurrentView?.Viewer.Rotate(+1); });
        AddAccelerator(VirtualKey.L, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; CurrentView?.Viewer.Rotate(-1); });
        AddAccelerator(VirtualKey.Q, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; _ = ExitAsync(); });
        AddAccelerator(VirtualKey.F11, VirtualKeyModifiers.None, (sender, e) => { e.Handled = true; ToggleFullScreen(); });
        AddAccelerator(VirtualKey.W, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (sender, e) => { e.Handled = true; _ = CloseAllTabsAsync(); });
        // Esc leaves full screen; otherwise let it through (search box, autoscroll, selection).
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, (sender, e) => { if (_fullScreen) { e.Handled = true; SetFullScreen(false); } });
        AddAccelerator(VirtualKey.F1, VirtualKeyModifiers.None, (sender, e) => { e.Handled = true; _ = RunDialogAsync(HelpDialogs.ShowShortcutsAsync); });

        _searchDebounce = new DispatcherTimerLike(TimeSpan.FromMilliseconds(250), () => _ = RunSearchAsync());
        Closed += (_, _) =>
        {
            _infoTimer?.Stop();
            _searchDebounce.Stop();
            foreach (var view in DocumentViews().ToList())
            {
                SaveReadingPosition(view);
                view.Dispose();
            }
        };
        // The window's X: ask about unsaved changes first.
        AppWindow.Closing += async (_, e) =>
        {
            if (_exitConfirmed || !DocumentViews().Any(v => v.Document.IsModified)) return;
            e.Cancel = true;
            await ExitAsync();
        };
        AddAccelerator(VirtualKey.H, VirtualKeyModifiers.Control, (sender, e) => { e.Handled = true; HighlightCommand(); });
        // Undo/redo; in a text box they stay the text box's own.
        AddAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control, (sender, e) => { if (!TextBoxHasFocus()) { e.Handled = true; _ = StepHistoryAsync(redo: false); } });
        AddAccelerator(VirtualKey.Y, VirtualKeyModifiers.Control, (sender, e) => { if (!TextBoxHasFocus()) { e.Handled = true; _ = StepHistoryAsync(redo: true); } });
        AddAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (sender, e) => { if (!TextBoxHasFocus()) { e.Handled = true; _ = StepHistoryAsync(redo: true); } });
        AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, (sender, e) => { e.Handled = true; CurrentView?.Viewer.GoBack(); });
        AddAccelerator(VirtualKey.Right, VirtualKeyModifiers.Menu, (sender, e) => { e.Handled = true; CurrentView?.Viewer.GoForward(); });

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
        var existing = Tabs.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(t => t.Tag is DocumentView v && string.Equals(v.Document.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Tabs.SelectedItem = existing;
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
        // A dot before the file name marks unsaved changes, as in most editors.
        view.Viewer.PagesEdited += (_, _) =>
        {
            if (TabOf(view) is { } tab) UpdateTabHeader(tab);
        };
        view.Viewer.BeforeEdit = () => TabOf(view) is { } tab ? HistoryFor(tab).CheckpointAsync(view.Document) : Task.CompletedTask;
        view.PageRequested += (_, request) => _ = RunPageRequestAsync(view, request);
        view.PageEditing = _pageEditing;
        view.PageEditingChanged += (_, on) => SetPageEditing(on);
        return view;
    }

    // ---------------- Highlighter ----------------

    private bool _highlighterMode;
    private bool _syncingHighlighter;

    /// <summary>Ctrl+H and the highlighter button: mark the selection, or turn highlighter mode on or off.</summary>
    private void HighlightCommand()
    {
        if (CurrentView is not { } view) return;
        if (view.Viewer.HasSelection)
            _ = view.Viewer.MarkSelectionAsync(MarkupPalette.CurrentKind, MarkupPalette.Current);
        else
            SetHighlighterMode(!_highlighterMode);
    }

    private void SetHighlighterMode(bool on)
    {
        _highlighterMode = on;
        foreach (var view in DocumentViews())
            view.Viewer.HighlighterMode = on;
        _syncingHighlighter = true;
        HighlightButton.IsChecked = on;
        _syncingHighlighter = false;
        if (on) ShowInfo("Highlighter on: select text to mark it. Click the button again to stop.");
    }

    private void HighlightButton_IsCheckedChanged(ToggleSplitButton sender, ToggleSplitButtonIsCheckedChangedEventArgs args)
    {
        if (_syncingHighlighter) return;
        HighlightCommand();
        // With a selection the click marked it; keep the button showing the mode, not the click.
        _syncingHighlighter = true;
        sender.IsChecked = _highlighterMode;
        _syncingHighlighter = false;
    }

    /// <summary>Style and color picker: a row of three styles drawn in the current color, then a row of color dots.</summary>
    private void HighlightMenu_Opening(object? sender, object e)
    {
        var c = MarkupPalette.Current;
        var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
        var ring = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]);

        var styles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var kinds = new (string Text, MarkupKind Kind)[]
        {
            ("Highlight", MarkupKind.Highlight), ("Underline", MarkupKind.Underline), ("Strikethrough", MarkupKind.StrikeOut),
        };
        foreach (var (text, kind) in kinds)
        {
            var sample = new Grid { Width = 20, Height = 22 };
            if (kind == MarkupKind.Highlight)
                sample.Children.Add(new Border { Background = brush, Opacity = 0.55, CornerRadius = new CornerRadius(2) });
            sample.Children.Add(new TextBlock { Text = "A", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            if (kind != MarkupKind.Highlight)
                sample.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Fill = brush, Height = 2,
                    VerticalAlignment = kind == MarkupKind.Underline ? VerticalAlignment.Bottom : VerticalAlignment.Center,
                    Margin = kind == MarkupKind.Underline ? new Thickness(1, 0, 1, 1) : new Thickness(1, 1, 1, 0),
                });
            bool selected = MarkupPalette.CurrentKind == kind;
            var button = new Button
            {
                Content = sample, Width = 40, Height = 36, Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderBrush = selected ? ring : null, BorderThickness = new Thickness(selected ? 2 : 0),
            };
            ToolTipService.SetToolTip(button, text);
            AutomationProperties.SetName(button, text);
            button.Click += (s, args) =>
            {
                AppSettings.Current.MarkupKind = kind.ToString();
                AppSettings.Save();
                HighlightMenu.Hide();
            };
            styles.Children.Add(button);
        }

        var colors = MarkupPalette.ColorRow(HighlightMenu, c, color =>
        {
            AppSettings.Current.MarkupColor = MarkupPalette.SettingValue(color);
            AppSettings.Save();
            UpdateHighlightSwatch();
        });

        HighlightMenu.Content = new StackPanel { Spacing = 10, Children = { styles, colors } };
    }

    private void UpdateHighlightSwatch()
    {
        var c = MarkupPalette.Current;
        HighlightSwatch.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
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

    /// <summary>Closes the window after asking about unsaved changes (Exit, Ctrl+Q, the window's X).</summary>
    private async Task ExitAsync()
    {
        if (!await CloseAllTabsAsync()) return;
        _exitConfirmed = true;
        Close();
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
        Title = current is null ? "CleanPDF" : $"{Path.GetFileName(current.Document.Path)} - CleanPDF";
        UpdateToolbar();
    }

    private void Tabs_AddTabButtonClick(TabView sender, object args) => _ = PickAndOpenAsync();
    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) => _ = CloseTabAsync(args.Tab);

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
        foreach (var item in new MenuFlyoutItemBase[] { SaveAsItem, ReloadItem, PrintItem, PropertiesItem, CloseTabItem, CloseAllItem, ShowInExplorerItem, CopyPathItem, ViewMenu, FindMenu, SelectAllItem })
            item.IsEnabled = hasDoc;
        SaveItem.IsEnabled = view?.Document.IsModified ?? false;
        var history = Tabs.SelectedItem is TabViewItem selected && view is not null ? HistoryFor(selected) : null;
        UndoItem.IsEnabled = history?.CanUndo ?? false;
        RedoItem.IsEnabled = history?.CanRedo ?? false;
        PagesMenu.IsEnabled = view is not null;
        EditPagesMenuItem.IsChecked = _pageEditing;
        // Page actions need editing on; extracting is allowed since it doesn't change this file.
        foreach (var item in PagesMenu.Items.OfType<MenuFlyoutItem>().Where(i => i is not ToggleMenuFlyoutItem))
            item.IsEnabled = _pageEditing || (string?)item.Tag == nameof(PageAction.Extract);
        SinglePageItem.IsChecked = view?.Viewer.SinglePage ?? false;
        TwoPageItem.IsChecked = view?.Viewer.TwoPage ?? false;
        BackItem.IsEnabled = view?.Viewer.CanGoBack ?? false;
        ForwardItem.IsEnabled = view?.Viewer.CanGoForward ?? false;
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

    private void Exit_Click(object sender, RoutedEventArgs e) => _ = ExitAsync();

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
                    await StepHistoryAsync(redo: false);
                    await Task.Delay(1000);
                    double? undone = Offset();
                    await StepHistoryAsync(redo: true);
                    await Task.Delay(1000);
                    Console.Out.WriteLine($"UNDO_SCROLL before={before:F0} after_undo={undone:F0} after_redo={Offset():F0}");
                }
                if (args.Contains("--edit-pages")) { SetPageEditing(true); await Task.Delay(500); }
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








