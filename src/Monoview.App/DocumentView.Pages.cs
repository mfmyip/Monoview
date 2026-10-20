using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace Monoview.App;

public enum PageAction { RotateLeft, RotateRight, Delete, Move, InsertFromFile, MoveFrom, Crop, Extract, ExportImages, Copy, Paste }

public static class PageActions
{
    /// <summary>Extracting, exporting and copying leave this document alone, so they work without Edit pages.</summary>
    public static bool NeedsEditing(string? tag) =>
        tag is not (nameof(PageAction.Extract) or nameof(PageAction.ExportImages) or nameof(PageAction.Copy));
}

/// <summary>
/// A page operation asked for in the sidebar; the window runs it, so it can be undone. For MoveFrom,
/// <see cref="Pages"/> are pages of <see cref="From"/>, another open document, which loses them.
/// </summary>
public sealed record PageRequest(PageAction Action, IReadOnlyList<int> Pages, int Destination = 0, string? FilePath = null,
    DocumentView? From = null, Monoview.Engine.NormRect? Area = null);

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

    /// <summary>Takes <paramref name="pages"/> out of this document, after another document took them in.</summary>
    public void RemovePages(IReadOnlyList<int> pages) => PageRequested?.Invoke(this, new PageRequest(PageAction.Delete, pages));

    /// <summary>Keyboard focus is in the page list, so Ctrl+C and Ctrl+V copy and paste pages.</summary>
    public bool PagesHaveFocus
    {
        get
        {
            for (var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
                if (ReferenceEquals(element, Thumbnails)) return true;
            return false;
        }
    }

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

    /// <summary>Pages being dragged out of a sidebar, so this or another document's sidebar can take them.</summary>
    private static (DocumentView From, IReadOnlyList<int> Pages)? s_pageDrag;

    private void Thumbnails_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        s_pageDrag = (this, e.Items.OfType<ThumbnailItem>().Select(t => t.Index).Order().ToList());
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void Thumbnails_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        s_pageDrag = null;
        // Dropped here, the gap stays until the moved pages are drawn in place.
        if (!_movePending) HideDropGap(animate: true);
    }

    private void Thumbnails_DragOver(object sender, DragEventArgs e)
    {
        if (!_pageEditing) return; // not editing: the window opens a dropped file in a new tab instead
        if (s_pageDrag is { } drag)
        {
            ShowDropGap(e, drag);
            e.AcceptedOperation = DataPackageOperation.Move;
            if (!ReferenceEquals(drag.From, this))
                e.DragUIOverride.Caption = drag.Pages.Count == 1 ? "Move page here" : $"Move {drag.Pages.Count} pages here";
        }
        else if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            ShowDropGap(e, null);
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Insert pages here";
        }
        else return;
        e.Handled = true;
    }

    private void Thumbnails_DragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(Thumbnails);
        if (p.X < 0 || p.Y < 0 || p.X >= Thumbnails.ActualWidth || p.Y >= Thumbnails.ActualHeight) HideDropGap(animate: true);
    }

    private async void Thumbnails_Drop(object sender, DragEventArgs e)
    {
        if (!_pageEditing) return;
        if (s_pageDrag is { } drag)
        {
            e.Handled = true;
            int at = ShowDropGap(e, drag);
            if (!ReferenceEquals(drag.From, this))
            {
                HideDropGap(animate: false);
                PageRequested?.Invoke(this, new PageRequest(PageAction.MoveFrom, drag.Pages, at, From: drag.From));
            }
            else if (drag.Pages.Select((page, i) => page == at + i).All(same => same))
                HideDropGap(animate: true); // back where they were
            else
            {
                _movePending = true;
                PageRequested?.Invoke(this, new PageRequest(PageAction.Move, drag.Pages, at));
            }
            return;
        }
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true; // don't also open the file in a new tab
        int insertAt = ShowDropGap(e, null);
        HideDropGap(animate: false);
        var deferral = e.GetDeferral();
        try
        {
            foreach (var file in (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>()
                         .Where(f => DocumentFiles.CanOpen(f.Path)).Reverse())
                PageRequested?.Invoke(this, new PageRequest(PageAction.InsertFromFile, [], insertAt, file.Path));
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---- The drop gap: pages slide aside as soon as the drag is a quarter of the way into them ----

    private bool _movePending;

    /// <summary>
    /// Slides the thumbnails to show where a drop at the pointer puts the pages: <paramref name="drag"/>'s pages move
    /// into the gap, dimmed, when they come from this list; otherwise the gap is as tall as the incoming pages.
    /// Returns where the first dropped page lands, counted after the drop.
    /// </summary>
    private int ShowDropGap(DragEventArgs e, (DocumentView From, IReadOnlyList<int> Pages)? drag)
    {
        if (Thumbnails.ItemsPanelRoot is not { } panel) return _thumbs.Count;
        double y = e.GetPosition(panel).Y;
        IReadOnlyList<int> own = drag is { } d && ReferenceEquals(d.From, this) ? d.Pages : [];
        var dragged = own.ToHashSet();
        var containers = Enumerable.Range(0, _thumbs.Count).Select(i => Thumbnails.ContainerFromIndex(i) as FrameworkElement).ToList();
        var shown = containers.Select((c, i) => (Container: c, Index: i)).Where(x => x.Container is not null).ToList();
        if (shown.Count == 0) return _thumbs.Count;
        // The space around a thumbnail's picture, to size pages that aren't on screen.
        double chrome = shown[0].Container!.ActualHeight - _thumbs[shown[0].Index].DisplayHeight;
        double Height(int i) => containers[i]?.ActualHeight ?? _thumbs[i].DisplayHeight + chrome;
        double gap = own.Count > 0 ? own.Sum(Height) : (drag?.Pages.Count ?? 1) * shown.Average(x => x.Container!.ActualHeight);

        // How many other pages stay above the gap. A page below the dragged ones moves up once the pointer is a
        // quarter into it; one above them moves down once the pointer is a quarter into it from below.
        int origin = own.Count > 0 ? own[0] : -1;
        int above = 0;
        bool onScreen = false;
        for (int i = 0; i < _thumbs.Count; i++)
        {
            if (dragged.Contains(i)) continue;
            if (containers[i] is not { } c)
            {
                if (!onScreen) above++; // scrolled off the top
                continue;
            }
            onScreen = true;
            double threshold = origin < 0 ? 0.5 : i > origin ? 0.25 : 0.75;
            if (y > c.ActualOffset.Y + Height(i) * threshold) above++;
        }

        // Slide the others; dragged pages that were above a thumbnail no longer take space there.
        double draggedAbove = 0, gapTop = double.NaN, previousBottom = double.NaN;
        int rank = 0;
        for (int i = 0; i < _thumbs.Count; i++)
        {
            if (dragged.Contains(i))
            {
                draggedAbove += Height(i);
                continue;
            }
            double top = (containers[i]?.ActualOffset.Y ?? double.NaN) - draggedAbove;
            if (rank == above) gapTop = double.IsNaN(top) ? previousBottom : top;
            Slide(containers[i], (rank >= above ? gap : 0) - draggedAbove);
            previousBottom = top + Height(i);
            rank++;
        }
        if (rank == above) gapTop = previousBottom;

        // Dragged pages from this list fill the gap, dimmed.
        double next = gapTop;
        foreach (int page in own)
        {
            if (containers[page] is { } c && !double.IsNaN(next))
            {
                Slide(c, next - c.ActualOffset.Y);
                c.Opacity = 0.4;
            }
            next += Height(page);
        }
        return above;
    }

    private static void Slide(UIElement? container, double dy)
    {
        if (container is null) return;
        container.TranslationTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) };
        container.Translation = new Vector3(0, (float)dy, 0);
    }

    /// <summary>Puts every thumbnail back in its place; at once when the pages were just redrawn in their new order.</summary>
    public void HideDropGap(bool animate)
    {
        _movePending = false;
        for (int i = 0; i < _thumbs.Count; i++)
            if (Thumbnails.ContainerFromIndex(i) is UIElement container)
                ResetSlide(container, animate);
    }

    internal static void ResetSlide(UIElement container, bool animate)
    {
        if (!animate) container.TranslationTransition = null;
        container.Translation = Vector3.Zero;
        container.Opacity = 1;
    }
}
