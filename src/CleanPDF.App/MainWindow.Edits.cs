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
    /// Shows <paramref name="doc"/> in the tab in place of <paramref name="old"/>, keeping zoom, rotation and
    /// sidebar. Page operations change the page count, so the view is rebuilt rather than updated.
    /// </summary>
    private void ReplaceView(TabViewItem tab, DocumentView old, PdfDocument doc, bool closeOldDocument,
        int? page = null, IReadOnlyList<int>? select = null)
    {
        var view = CreateDocumentView(doc);
        var state = old.Viewer.CaptureState();
        view.Viewer.InitialState = state with { Page = Math.Clamp(page ?? state.Page, 0, Math.Max(0, doc.PageCount - 1)) };
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

        // Pickers first, so cancelling doesn't leave an empty undo step.
        string? file = request.FilePath;
        if (request.Action == PageAction.InsertFromFile && file is null && (file = await PickPdfAsync()) is null) return;
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
                    int added = await doc.InsertPagesFromAsync(file!, null, at);
                    var inserted = Enumerable.Range(at, added).ToList();
                    ReplaceView(tab, view, doc, closeOldDocument: false, page: at, select: inserted);
                    ShowInfo($"Inserted {added} page{(added == 1 ? "" : "s")} from {Path.GetFileName(file)}");
                    break;
            }
        }
        catch (PdfException ex)
        {
            history.DropLast();
            ShowError("Couldn't change the pages", ex is PdfPasswordException
                ? "Password-protected PDFs can't be inserted yet. Open it, then Save As without changes, or remove the password first."
                : ex.Message);
        }
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

    private async Task<string?> PickPdfAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pdf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFileAsync())?.Path;
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
