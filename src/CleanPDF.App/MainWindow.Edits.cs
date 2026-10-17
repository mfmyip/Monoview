using CleanPDF.App.Viewer;
using CleanPDF.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace CleanPDF.App;

/// <summary>Page operations and undo/redo: every edit is checkpointed in the tab's history first.</summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<TabViewItem, EditHistory> _histories = new();

    private EditHistory HistoryFor(TabViewItem tab) =>
        _histories.TryGetValue(tab, out var history) ? history : _histories[tab] = new EditHistory();

    private TabViewItem? TabOf(DocumentView view) =>
        Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => ReferenceEquals(t.Tag, view));

    /// <summary>File name, with a dot in front while there are unsaved changes.</summary>
    private static void UpdateTabHeader(TabViewItem tab)
    {
        if (tab.Tag is not DocumentView view) return;
        string name = Path.GetFileName(view.Document.Path);
        tab.Header = view.Document.IsModified ? $"• {name}" : name;
        ToolTipService.SetToolTip(tab, view.Document.Path);
    }

    /// <summary>
    /// Shows <paramref name="doc"/> in the tab in place of <paramref name="old"/>'s document, keeping zoom, rotation,
    /// scroll and sidebar. With the same pages (undo, redo, save, reload) the view stays and redraws in place;
    /// page operations change the pages, so the view is rebuilt.
    /// </summary>
    private void ReplaceView(TabViewItem tab, DocumentView old, PdfDocument doc, bool closeOldDocument,
        int? page = null, IReadOnlyList<int>? select = null)
    {
        var previous = old.Document;
        if (!ReferenceEquals(previous, doc) && page is null && select is null && previous.PageSizes.SequenceEqual(doc.PageSizes))
        {
            old.ReplaceDocument(doc);
            if (closeOldDocument) previous.Dispose();
            UpdateTabHeader(tab);
            OnCurrentDocumentChanged();
            return;
        }

        var view = CreateDocumentView(doc);
        var state = old.Viewer.CaptureState();
        // Same pages: keep the exact scroll position. A page operation lands on the page it names instead.
        view.Viewer.InitialState = page is null ? state
            : state with { Page = Math.Clamp(page.Value, 0, Math.Max(0, doc.PageCount - 1)), OffsetX = null, OffsetY = null };
        view.SetSidebarVisible(old.IsSidebarVisible);
        view.SelectAfterLoad = select;
        DocumentHost.Children.Remove(old);
        old.Close(closeOldDocument);
        DocumentHost.Children.Add(view);
        tab.Tag = view;
        UpdateTabHeader(tab);
        OnCurrentDocumentChanged();
    }

    private async Task RunPageRequestAsync(DocumentView view, PageRequest request)
    {
        if (TabOf(view) is not { } tab) return;
        var doc = view.Document;
        var pages = request.Pages;

        // Pickers and prompts first, so cancelling doesn't leave an empty undo step.
        string? file = request.FilePath;
        PdfDocument? source = null;
        if (request.Action == PageAction.InsertFromFile)
        {
            if (file is null && (file = await PickInsertFileAsync()) is null) return;
            if ((source = await OpenToInsertAsync(file)) is null) return;
        }
        if (request.Action == PageAction.ExportImages)
        {
            string? target = await PickImageTargetAsync(doc.Path, pages);
            if (target is null) return;
            try
            {
                var written = await ExportImagesAsync(doc, pages, target);
                ShowInfo(written.Count == 1 ? $"Saved {Path.GetFileName(written[0])}" : $"Saved {written.Count} pictures to {Path.GetDirectoryName(target)}");
            }
            catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
            {
                ShowError("Couldn't export the pages", ex.Message);
            }
            return; // exporting doesn't change this document
        }
        if (request.Action == PageAction.Extract)
        {
            string? target = await PickExtractTargetAsync(doc.Path, pages);
            if (target is null) return;
            try
            {
                await doc.ExtractPagesAsync(pages, target);
                ShowInfo($"Saved {pages.Count} page{(pages.Count == 1 ? "" : "s")} to {Path.GetFileName(target)}");
            }
            catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
            {
                ShowError("Couldn't extract the pages", ex.Message);
            }
            return; // extracting doesn't change this document
        }
        if (request.Action == PageAction.Delete && pages.Count >= doc.PageCount)
        {
            ShowError("Can't delete every page", "A PDF needs at least one page.");
            return;
        }

        var history = HistoryFor(tab);
        await history.CheckpointAsync(doc);
        try
        {
            switch (request.Action)
            {
                case PageAction.RotateLeft:
                case PageAction.RotateRight:
                    await doc.RotatePagesAsync(pages, request.Action == PageAction.RotateRight ? 1 : -1);
                    ReplaceView(tab, view, doc, closeOldDocument: false, select: pages);
                    break;
                case PageAction.Delete:
                    await doc.DeletePagesAsync(pages);
                    ReplaceView(tab, view, doc, closeOldDocument: false, page: pages[0]);
                    break;
                case PageAction.Move:
                    await doc.MovePagesAsync(pages, request.Destination);
                    var moved = Enumerable.Range(request.Destination, pages.Count).ToList();
                    ReplaceView(tab, view, doc, closeOldDocument: false, page: moved[0], select: moved);
                    break;
                case PageAction.InsertFromFile:
                    int at = Math.Clamp(request.Destination, 0, doc.PageCount);
                    int added = await doc.InsertPagesFromAsync(source!, at);
                    var inserted = Enumerable.Range(at, added).ToList();
                    ReplaceView(tab, view, doc, closeOldDocument: false, page: at, select: inserted);
                    ShowInfo($"Inserted {added} page{(added == 1 ? "" : "s")} from {Path.GetFileName(file)}");
                    break;
            }
        }
        catch (PdfException ex)
        {
            history.DropLast();
            ShowError("Couldn't change the pages", ex.Message);
        }
        finally
        {
            source?.Dispose();
        }
    }

    /// <summary>Opens a PDF or a picture to insert its pages, asking for the password if it has one. Null when cancelled or unreadable.</summary>
    private async Task<PdfDocument?> OpenToInsertAsync(string path)
    {
        try
        {
            return await DocumentFiles.OpenAsync(path);
        }
        catch (PdfPasswordException)
        {
            return await AskPasswordAndOpenAsync(path);
        }
        catch (PdfException ex)
        {
            ShowError($"Couldn't open {Path.GetFileName(path)}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Writes each page as a picture at 150 DPI, PNG or JPEG by <paramref name="target"/>'s extension. One page goes to
    /// <paramref name="target"/>; several get " page N" added to its name. Returns the files written.
    /// </summary>
    private static async Task<List<string>> ExportImagesAsync(PdfDocument doc, IReadOnlyList<int> pages, string target)
    {
        const double Dpi = 150, MaxPixels = 10000;
        bool jpeg = Path.GetExtension(target).ToLowerInvariant() is ".jpg" or ".jpeg";
        var written = new List<string>();
        foreach (int page in pages)
        {
            var size = doc.PageSizes[page];
            double scale = Math.Min(Dpi / 72, MaxPixels / Math.Max(size.Width, size.Height));
            int w = Math.Max(1, (int)Math.Round(size.Width * scale)), h = Math.Max(1, (int)Math.Round(size.Height * scale));
            var rendered = await doc.RenderAsync(page, w, h);

            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                jpeg ? Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId : Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                (uint)w, (uint)h, scale * 72, scale * 72, rendered.Bgra);
            await encoder.FlushAsync();
            stream.Seek(0);

            string path = pages.Count == 1 ? target
                : Path.Combine(Path.GetDirectoryName(target)!, $"{Path.GetFileNameWithoutExtension(target)} page {page + 1}{Path.GetExtension(target)}");
            using (var file = File.Create(path))
                await stream.AsStreamForRead().CopyToAsync(file);
            written.Add(path);
        }
        return written;
    }

    /// <summary>Undo (or redo): reopens the document as it was before (or after) the last edit.</summary>
    private async Task StepHistoryAsync(bool redo)
    {
        if (Tabs.SelectedItem is not TabViewItem tab || tab.Tag is not DocumentView view) return;
        var doc = view.Document;
        var history = HistoryFor(tab);
        if (!(redo ? history.CanRedo : history.CanUndo)) return;

        var current = new EditHistory.Snapshot(await doc.SaveToBytesAsync(), !doc.IsModified);
        if (history.Step(redo, current) is not { } target) return;
        var restored = await PdfDocument.OpenMemoryAsync(target.Bytes, doc.Path, doc.Password, modified: !target.MatchesFile);
        ReplaceView(tab, view, restored, closeOldDocument: true);
    }

    private async Task<string?> PickInsertFileAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (string extension in DocumentFiles.Extensions)
            picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private async Task<string?> PickImageTargetAsync(string source, IReadOnlyList<int> pages)
    {
        string name = Path.GetFileNameWithoutExtension(source);
        var picker = new FileSavePicker { SuggestedFileName = pages.Count == 1 ? $"{name} page {pages[0] + 1}" : name };
        picker.FileTypeChoices.Add("PNG picture", new List<string> { ".png" });
        picker.FileTypeChoices.Add("JPEG picture", new List<string> { ".jpg" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSaveFileAsync())?.Path;
    }

    private async Task<string?> PickExtractTargetAsync(string source, IReadOnlyList<int> pages)
    {
        string suffix = pages.Count == 1 ? $"page {pages[0] + 1}" : $"pages {pages[0] + 1} to {pages[^1] + 1}";
        var picker = new FileSavePicker { SuggestedFileName = $"{Path.GetFileNameWithoutExtension(source)} {suffix}" };
        picker.FileTypeChoices.Add("PDF document", new List<string> { ".pdf" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSaveFileAsync())?.Path;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => _ = StepHistoryAsync(redo: false);
    private void Redo_Click(object sender, RoutedEventArgs e) => _ = StepHistoryAsync(redo: true);

    /// <summary>Page editing mode, shared by all tabs and kept when a view is rebuilt; off at every launch.</summary>
    private bool _pageEditing;

    private void SetPageEditing(bool on)
    {
        _pageEditing = on;
        foreach (var view in DocumentViews())
            view.PageEditing = on;
        if (on && CurrentView is { } current)
        {
            current.SetSidebarVisible(true);
            current.SelectSidebarTab("pages");
        }
    }

    private void EditPagesMenu_Click(object sender, RoutedEventArgs e) => SetPageEditing(EditPagesMenuItem.IsChecked);

    private void PagesMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentView is not { } view || sender is not MenuFlyoutItem { Tag: string tag } || !Enum.TryParse<PageAction>(tag, out var action)) return;
        view.RequestPages(action, destination: view.SelectedPages.Max() + 1);
    }
}
