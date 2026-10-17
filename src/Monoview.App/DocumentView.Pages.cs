using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace Monoview.App;

public enum PageAction { RotateLeft, RotateRight, Delete, Move, InsertFromFile, CopyFrom, Extract, ExportImages }

public static class PageActions
{
    /// <summary>Extracting and exporting write new files and leave this one alone, so they work without Edit pages.</summary>
    public static bool NeedsEditing(string? tag) => tag is not (nameof(PageAction.Extract) or nameof(PageAction.ExportImages));
}

/// <summary>
/// A page operation asked for in the sidebar; the window runs it, so it can be undone. For CopyFrom,
/// <see cref="Pages"/> are pages of <see cref="Source"/>, another open document.
/// </summary>
public sealed record PageRequest(PageAction Action, IReadOnlyList<int> Pages, int Destination = 0, string? FilePath = null,
    Monoview.Engine.PdfDocument? Source = null);

/// <summary>The page sidebar as an editor: drag to reorder, Delete, right-click actions, drop a PDF to insert.</summary>
public sealed partial class DocumentView
{
    public event EventHandler<PageRequest>? PageRequested;

    /// <summary>The user turned page editing on or off here; the window applies it to every tab.</summary>
    public event EventHandler<bool>? PageEditingChanged;

    private bool _pageEditing;

    /// <summary>
    /// Off (the default): the sidebar only navigates, so a stray drag or Delete can't change pages.
    /// On: multi-select, drag to reorder, Delete and dropping a PDF all edit the document.
    /// </summary>
    public bool PageEditing
    {
        get => _pageEditing;
        set
        {
            _pageEditing = value;
            Thumbnails.SelectionMode = value ? ListViewSelectionMode.Extended : ListViewSelectionMode.Single;
            Thumbnails.CanDragItems = value;
            Thumbnails.CanReorderItems = value;
            EditPagesButton.IsChecked = value;
            EditPagesIcon.Glyph = value ? "" : ""; // checkmark while editing, pencil otherwise
            EditPagesLabel.Text = value ? "Done" : "Edit pages";
            if (!value) SyncThumbnailSelection();
        }
    }

    private void SetPageEditing(bool on)
    {
        PageEditing = on;
        PageEditingChanged?.Invoke(this, on);
    }

    private void EditPagesButton_Click(object sender, RoutedEventArgs e) => SetPageEditing(EditPagesButton.IsChecked == true);

    private void EditPagesMenuItem_Click(object sender, RoutedEventArgs e) => SetPageEditing(EditPagesMenuItem.IsChecked);

    /// <summary>Page actions are greyed out until editing is on; the toggle at the top turns it on.</summary>
    private void PageMenu_Opening(object? sender, object e)
    {
        EditPagesMenuItem.IsChecked = _pageEditing;
        foreach (var item in PageMenu.Items.OfType<MenuFlyoutItem>().Where(i => i is not ToggleMenuFlyoutItem))
            item.IsEnabled = _pageEditing || !PageActions.NeedsEditing((string?)item.Tag);
    }

    /// <summary>Selected pages in page order, or the current page when none are selected.</summary>
    public IReadOnlyList<int> SelectedPages
    {
        get
        {
            var pages = Thumbnails.SelectedItems.OfType<ThumbnailItem>().Select(t => t.Index).Order().ToList();
            return pages.Count > 0 ? pages : [Viewer.CurrentPage];
        }
    }

    public void RequestPages(PageAction action, int destination = 0, string? filePath = null) =>
        PageRequested?.Invoke(this, new PageRequest(action, SelectedPages, destination, filePath));

    private void Thumbnails_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete || !_pageEditing) return;
        e.Handled = true;
        RequestPages(PageAction.Delete);
    }

    /// <summary>Right-clicking a page that isn't selected selects it, so the menu acts on what was clicked.</summary>
    private void Thumbnails_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ThumbnailItem item && !Thumbnails.SelectedItems.Contains(item))
            Thumbnails.SelectedItem = item;
    }

    private void PageMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag } || !Enum.TryParse<PageAction>(tag, out var action)) return;
        int after = SelectedPages.Max() + 1;
        RequestPages(action, destination: after);
    }

    /// <summary>Pages being dragged out of a sidebar, so another document's sidebar can copy them.</summary>
    private static (DocumentView From, IReadOnlyList<int> Pages)? s_pageDrag;

    private void Thumbnails_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        s_pageDrag = (this, e.Items.OfType<ThumbnailItem>().Select(t => t.Index).Order().ToList());
        // Move reorders within this list; Copy lets another document take the pages.
        e.Data.RequestedOperation = DataPackageOperation.Move | DataPackageOperation.Copy;
    }

    private (DocumentView From, IReadOnlyList<int> Pages)? PagesFromAnotherDocument() =>
        s_pageDrag is { } drag && drag.From != this ? drag : null;

    /// <summary>A drag inside the list reordered pages: tell the window which pages moved and where to.</summary>
    private void Thumbnails_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        s_pageDrag = null;
        if (args.DropResult != DataPackageOperation.Move) return;
        var moved = args.Items.OfType<ThumbnailItem>().OrderBy(_thumbs.IndexOf).ToList();
        if (moved.Count == 0) return;
        int destination = _thumbs.IndexOf(moved[0]);
        // The list already shows the new order; the document follows.
        if (moved.Select((t, i) => t.Index == destination + i).All(same => same)) return;
        PageRequested?.Invoke(this, new PageRequest(PageAction.Move, moved.Select(t => t.Index).ToList(), destination));
    }

    // ---- Dropping a PDF file from Explorer inserts its pages where it lands ----

    private void Thumbnails_DragOver(object sender, DragEventArgs e)
    {
        if (PagesFromAnotherDocument() is { } drag)
        {
            if (!_pageEditing) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = drag.Pages.Count == 1 ? "Copy page here" : $"Copy {drag.Pages.Count} pages here";
            e.Handled = true;
            return;
        }
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return; // a page reorder, handled by the list
        if (!_pageEditing) return; // not editing: the window opens the file in a new tab instead
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Insert pages here";
        e.Handled = true;
    }

    private async void Thumbnails_Drop(object sender, DragEventArgs e)
    {
        if (PagesFromAnotherDocument() is { } drag)
        {
            if (!_pageEditing) return;
            e.Handled = true;
            PageRequested?.Invoke(this, new PageRequest(PageAction.CopyFrom, drag.Pages, InsertionIndexAt(e.GetPosition(Thumbnails).Y),
                Source: drag.From.Document));
            return;
        }
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || !_pageEditing) return;
        e.Handled = true; // don't also open the file in a new tab
        var deferral = e.GetDeferral();
        try
        {
            int at = InsertionIndexAt(e.GetPosition(Thumbnails).Y);
            foreach (var file in (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>()
                         .Where(f => DocumentFiles.CanOpen(f.Path)).Reverse())
                PageRequested?.Invoke(this, new PageRequest(PageAction.InsertFromFile, [], at, file.Path));
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Insert before the first thumbnail whose middle is below <paramref name="y"/>; at the end otherwise.</summary>
    private int InsertionIndexAt(double y)
    {
        for (int i = 0; i < _thumbs.Count; i++)
        {
            if (Thumbnails.ContainerFromIndex(i) is not FrameworkElement container) continue;
            var top = container.TransformToVisual(Thumbnails).TransformPoint(default).Y;
            if (y < top + container.ActualHeight / 2) return i;
        }
        return _thumbs.Count;
    }
}
