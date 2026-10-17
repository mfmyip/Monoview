using System.Collections.ObjectModel;
using Monoview.App.Viewer;
using Monoview.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Monoview.App;

/// <summary>One open document: the sidebar (page thumbnails, outline, search results) and the page viewer.</summary>
public sealed partial class DocumentView : UserControl, IDisposable
{
    private const double ThumbMaxWidth = 140, ThumbMaxHeight = 180;

    private readonly PreviewCache _previews;
    private readonly ObservableCollection<ThumbnailItem> _thumbs = new();
    private readonly ObservableCollection<SearchResultItem> _results = new();
    private CancellationTokenSource? _searchCts;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _thumbnailFallback;
    private bool _syncingThumbs;
    private bool _outlineLoaded;

    public PdfDocument Document { get; private set; }
    public PdfViewer Viewer { get; }
    public string SearchQuery { get; private set; } = "";
    public int CurrentHitIndex { get; private set; } = -1;
    public int HitCount => _results.Count;
    public bool IsSidebarVisible => Sidebar.Visibility == Visibility.Visible;

    /// <summary>Search results or the current match changed.</summary>
    public event EventHandler? SearchChanged;

    public DocumentView(PdfDocument document)
    {
        InitializeComponent();
        Document = document;
        _previews = new PreviewCache(document);
        Viewer = new PdfViewer(document, _previews);
        ViewerHost.Child = Viewer;

        BuildThumbnails();
        // A page with new markup gets a fresh thumbnail.
        // A changed page gets a fresh thumbnail; the old one stays up until it's drawn.
        _previews.PageInvalidated += (_, page) =>
        {
            if (page < _thumbs.Count && _thumbs[page].Image is not null)
                _ = LoadThumbnailAsync(_thumbs[page]);
        };
        _previews.DocumentReplaced += (_, _) =>
        {
            foreach (var thumb in _thumbs.Where(t => t.Image is not null))
                _ = LoadThumbnailAsync(thumb);
        };
        // Rotating the view rotates the thumbnails too.
        _previews.RotationChanged += (_, _) =>
        {
            BuildThumbnails();
            SyncThumbnailSelection();
        };
        // Fill the thumbnail list once the first page is sharp (or after a second, if rendering
        // fails), so it doesn't slow the first paint.
        Viewer.FirstPageRendered += (_, _) => ShowThumbnails();
        _thumbnailFallback = DispatcherQueue.CreateTimer();
        _thumbnailFallback.Interval = TimeSpan.FromSeconds(1);
        _thumbnailFallback.IsRepeating = false;
        _thumbnailFallback.Tick += (_, _) => ShowThumbnails();
        _thumbnailFallback.Start();

        Viewer.CurrentPageChanged += (_, _) => SyncThumbnailSelection();
        Loaded += (_, _) => SyncThumbnailSelection();
        // Like Preview: a single page, such as a picture, needs no page list.
        SetSidebarVisible(document.PageCount > 1);
    }

    public void ToggleSidebar() => SetSidebarVisible(!IsSidebarVisible);

    public void SetSidebarVisible(bool visible)
    {
        Sidebar.Visibility = SidebarSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        // Reopening uses the width the user last dragged it to.
        SidebarColumn.Width = new GridLength(visible ? AppSettings.Current.SidebarWidth : 0);
    }

    // ---------------- Sidebar resizing ----------------

    private static readonly Microsoft.UI.Input.InputCursor ResizeCursor =
        Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
    private double? _dragStartX, _dragStartWidth;

    private void SidebarSplitter_PointerEntered(object sender, PointerRoutedEventArgs e) => ProtectedCursor = ResizeCursor;

    private void SidebarSplitter_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStartX is null) ProtectedCursor = null;
    }

    private void SidebarSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragStartX = e.GetCurrentPoint(this).Position.X;
        _dragStartWidth = SidebarColumn.ActualWidth;
        Viewer.BeginInteractiveResize();
        SidebarSplitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void SidebarSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStartX is not { } startX || _dragStartWidth is not { } startWidth) return;
        double width = AppSettings.ClampSidebarWidth(startWidth + e.GetCurrentPoint(this).Position.X - startX);
        SidebarColumn.Width = new GridLength(width);
        e.Handled = true;
    }

    private void SidebarSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStartX is null) return;
        SidebarSplitter.ReleasePointerCapture(e.Pointer);
        EndSidebarDrag();
    }

    private void SidebarSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndSidebarDrag();

    private void EndSidebarDrag()
    {
        if (_dragStartX is null) return;
        _dragStartX = _dragStartWidth = null;
        ProtectedCursor = null;
        Viewer.EndInteractiveResize();
        AppSettings.Current.SidebarWidth = SidebarColumn.ActualWidth;
        AppSettings.Save();
        SidebarWidthChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The user dragged the sidebar to a new width (other open documents follow).</summary>
    public event EventHandler? SidebarWidthChanged;

    public void ApplySidebarWidth()
    {
        if (IsSidebarVisible) SidebarColumn.Width = new GridLength(AppSettings.Current.SidebarWidth);
    }

    // ---------------- Thumbnails ----------------

    private void BuildThumbnails()
    {
        _thumbs.Clear();
        for (int i = 0; i < Document.PageCount; i++)
        {
            var size = Monoview.Engine.Rotation.Apply(Document.PageSizes[i], _previews.Rotation);
            double scale = Math.Min(ThumbMaxWidth / size.Width, ThumbMaxHeight / size.Height);
            _thumbs.Add(new ThumbnailItem(i, Math.Round(size.Width * scale), Math.Round(size.Height * scale)));
        }
    }

    private void ShowThumbnails()
    {
        if (Thumbnails.ItemsSource is not null) return;
        // Filling the list selects an item; that isn't a click, so it mustn't scroll the page.
        _syncingThumbs = true;
        Thumbnails.ItemsSource = _thumbs;
        _syncingThumbs = false;
        if (SelectAfterLoad is { Count: > 0 } pages)
        {
            // After a page operation, keep the moved or rotated pages selected.
            _syncingThumbs = true;
            foreach (int page in pages.Where(p => p < _thumbs.Count))
                Thumbnails.SelectedItems.Add(_thumbs[page]);
            _syncingThumbs = false;
            Thumbnails.ScrollIntoView(_thumbs[Math.Min(pages[0], _thumbs.Count - 1)]);
        }
        else
        {
            SyncThumbnailSelection();
        }
    }

    /// <summary>Shows <paramref name="doc"/>, which has the same pages, in this view (undo, redo, save). The caller closes the old one.</summary>
    public void ReplaceDocument(PdfDocument doc)
    {
        Document = doc;
        Viewer.ReplaceDocument(doc);
    }

    /// <summary>Pages to select in the sidebar once it's filled (set by the window after a page operation).</summary>
    public IReadOnlyList<int>? SelectAfterLoad { get; set; }

    /// <summary>Shows a sidebar tab: "pages", "outline" or "search".</summary>
    public void SelectSidebarTab(string name) => SidebarTabs.SelectedItem = name switch
    {
        "outline" => OutlineTab,
        "search" => SearchTab,
        _ => ThumbnailsTab,
    };

    private void SyncThumbnailSelection()
    {
        int page = Viewer.CurrentPage;
        // Several selected pages are the user's choice for a page operation; scrolling shouldn't undo it.
        if (Thumbnails.SelectedItems.Count > 1) return;
        if (page < 0 || page >= _thumbs.Count || Thumbnails.SelectedIndex == page) return;
        _syncingThumbs = true;
        Thumbnails.SelectedIndex = page;
        _syncingThumbs = false;
        if (IsSidebarVisible && PagesPanel.Visibility == Visibility.Visible)
            Thumbnails.ScrollIntoView(_thumbs[page]);
    }

    private void Thumbnails_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only a single selection jumps; Ctrl or Shift-clicking more pages just selects them.
        if (_syncingThumbs || Thumbnails.SelectedItems.Count != 1 || Thumbnails.SelectedItem is not ThumbnailItem item) return;
        Viewer.NavigateTo(item.Index);
    }

    private void Thumbnails_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ThumbnailItem item) return;
        if (args.InRecycleQueue)
        {
            item.Image = null; // keep memory flat on long documents
            return;
        }
        if (item.Image is null)
            _ = LoadThumbnailAsync(item);
    }

    private async Task LoadThumbnailAsync(ThumbnailItem item)
    {
        var doc = Document;
        var bmp = await _previews.GetAsync(item.Index, WorkPriority.Low);
        if (bmp is not null) item.Image = bmp;
        else if (doc != Document) _ = LoadThumbnailAsync(item); // replaced while loading: load from the new one
    }

    // ---------------- Sidebar tabs & outline ----------------

    private void SidebarTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowTab(sender.SelectedItem);

    private void ShowTab(SelectorBarItem? tab)
    {
        // The outline and search panels are x:Load="False": built the first time they're shown.
        if (tab == OutlineTab && OutlinePanel is null) FindName(nameof(OutlinePanel));
        if (tab == SearchTab) EnsureSearchPanel();

        PagesPanel.Visibility = tab == ThumbnailsTab ? Visibility.Visible : Visibility.Collapsed;
        if (OutlinePanel is not null) OutlinePanel.Visibility = tab == OutlineTab ? Visibility.Visible : Visibility.Collapsed;
        if (SearchPanel is not null) SearchPanel.Visibility = tab == SearchTab ? Visibility.Visible : Visibility.Collapsed;
        if (tab == OutlineTab && !_outlineLoaded)
            _ = LoadOutlineAsync();
        if (tab == ThumbnailsTab)
            SyncThumbnailSelection();
    }

    private void EnsureSearchPanel()
    {
        if (SearchPanel is not null) return;
        FindName(nameof(SearchPanel));
        SearchResults.ItemsSource = _results;
    }

    private async Task LoadOutlineAsync()
    {
        _outlineLoaded = true;
        try
        {
            var outline = await Document.GetOutlineAsync();
            Outline.ItemsSource = outline;
            NoOutline.Visibility = outline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async void Outline_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not OutlineItem item || item.PageIndex < 0) return;
        double? ny = null;
        if (item.Y is { } y)
        {
            var (_, normY) = await Document.PageToNormalizedAsync(item.PageIndex, item.X ?? 0, y);
            ny = Math.Clamp(normY, 0, 1);
        }
        Viewer.NavigateTo(item.PageIndex, ny);
    }

    // ---------------- Search ----------------

    /// <param name="force">Search again even if the query is the same, e.g. after the search options change.</param>
    public async Task SearchAsync(string query, bool force = false)
    {
        query = query.Trim();
        if (query == SearchQuery && !force) return;
        EnsureSearchPanel();

        _searchCts?.Cancel();
        _results.Clear();
        CurrentHitIndex = -1;
        SearchQuery = query;
        Viewer.SetSearchHits([], null);
        SearchChanged?.Invoke(this, EventArgs.Empty);
        if (query.Length == 0)
        {
            SearchStatus.Text = "Press Ctrl+F to search.";
            return;
        }

        SetSidebarVisible(true);
        SidebarTabs.SelectedItem = SearchTab;
        SearchStatus.Text = "Searching…";

        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Document.SearchAsync(query, hits =>
            {
                if (cts.IsCancellationRequested) return;
                foreach (var hit in hits)
                    _results.Add(new SearchResultItem(hit));
                Viewer.SetSearchHits(_results.Select(r => r.Hit), CurrentHitIndex >= 0 ? _results[CurrentHitIndex].Hit : null);
                SearchStatus.Text = $"{_results.Count} matches so far…";
                if (CurrentHitIndex < 0)
                    _ = GoToHitAsync(0);
                SearchChanged?.Invoke(this, EventArgs.Empty);
            }, cts.Token, new SearchOptions(AppSettings.Current.SearchMatchCase, AppSettings.Current.SearchWholeWord));
            SearchStatus.Text = _results.Count switch
            {
                0 => "No matches.",
                1 => "1 match",
                var n => $"{n} matches",
            };
            SearchChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public void NextHit() => _ = GoToHitAsync(CurrentHitIndex + 1);
    public void PreviousHit() => _ = GoToHitAsync(CurrentHitIndex - 1);

    public void ClearSearch() => _ = SearchAsync("");

    private async Task GoToHitAsync(int index)
    {
        if (_results.Count == 0) return;
        index = ((index % _results.Count) + _results.Count) % _results.Count;
        CurrentHitIndex = index;
        SearchResults.SelectedIndex = index;
        SearchResults.ScrollIntoView(_results[index]);
        SearchChanged?.Invoke(this, EventArgs.Empty);
        await Viewer.ShowHitAsync(_results[index].Hit);
    }

    private void SearchResults_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResultItem item)
            _ = GoToHitAsync(_results.IndexOf(item));
    }

    public void Dispose() => Close(closeDocument: true);

    /// <param name="closeDocument">False when the document lives on in a new view (after a page operation).</param>
    public void Close(bool closeDocument)
    {
        // A timer firing on a closed view during shutdown crashes the app, so stop them all.
        _thumbnailFallback.Stop();
        Viewer.Close();
        _searchCts?.Cancel();
        if (closeDocument) Document.Dispose();
    }
}
