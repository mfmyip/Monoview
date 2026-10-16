using System.Collections.ObjectModel;
using CleanPDF.App.Viewer;
using CleanPDF.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanPDF.App;

/// <summary>One open document: the sidebar (page thumbnails, outline, search results) and the page viewer.</summary>
public sealed partial class DocumentView : UserControl, IDisposable
{
    private const double ThumbMaxWidth = 140, ThumbMaxHeight = 180;

    private readonly PreviewCache _previews;
    private readonly ObservableCollection<ThumbnailItem> _thumbs = new();
    private readonly ObservableCollection<SearchResultItem> _results = new();
    private CancellationTokenSource? _searchCts;
    private bool _syncingThumbs;
    private bool _outlineLoaded;

    public PdfDocument Document { get; }
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

        for (int i = 0; i < document.PageCount; i++)
        {
            var size = document.PageSizes[i];
            double scale = Math.Min(ThumbMaxWidth / size.Width, ThumbMaxHeight / size.Height);
            _thumbs.Add(new ThumbnailItem(i, Math.Round(size.Width * scale), Math.Round(size.Height * scale)));
        }
        // Fill the thumbnail list once the first page is sharp (or after a second, if rendering
        // fails), so it doesn't slow the first paint.
        Viewer.FirstPageRendered += (_, _) => ShowThumbnails();
        var fallback = DispatcherQueue.CreateTimer();
        fallback.Interval = TimeSpan.FromSeconds(1);
        fallback.IsRepeating = false;
        fallback.Tick += (_, _) => ShowThumbnails();
        fallback.Start();

        Viewer.CurrentPageChanged += (_, _) => SyncThumbnailSelection();
        Loaded += (_, _) => SyncThumbnailSelection();
    }

    public void ToggleSidebar() => SetSidebarVisible(!IsSidebarVisible);

    public void SetSidebarVisible(bool visible)
    {
        Sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = visible ? new GridLength(220) : new GridLength(0);
    }

    // ---------------- Thumbnails ----------------

    private void ShowThumbnails()
    {
        if (Thumbnails.ItemsSource is not null) return;
        Thumbnails.ItemsSource = _thumbs;
        SyncThumbnailSelection();
    }

    /// <summary>Dev aid for screenshots: "pages", "outline" or "search".</summary>
    public void SelectSidebarTab(string name) => SidebarTabs.SelectedItem = name switch
    {
        "outline" => OutlineTab,
        "search" => SearchTab,
        _ => ThumbnailsTab,
    };

    private void SyncThumbnailSelection()
    {
        int page = Viewer.CurrentPage;
        if (page < 0 || page >= _thumbs.Count || Thumbnails.SelectedIndex == page) return;
        _syncingThumbs = true;
        Thumbnails.SelectedIndex = page;
        _syncingThumbs = false;
        if (IsSidebarVisible && Thumbnails.Visibility == Visibility.Visible)
            Thumbnails.ScrollIntoView(_thumbs[page]);
    }

    private void Thumbnails_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingThumbs || Thumbnails.SelectedIndex < 0) return;
        Viewer.GoToPage(Thumbnails.SelectedIndex);
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
        var bmp = await _previews.GetAsync(item.Index, WorkPriority.Low);
        if (bmp is not null) item.Image = bmp;
    }

    // ---------------- Sidebar tabs & outline ----------------

    private void SidebarTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowTab(sender.SelectedItem);

    private void ShowTab(SelectorBarItem? tab)
    {
        // The outline and search panels are x:Load="False": built the first time they're shown.
        if (tab == OutlineTab && OutlinePanel is null) FindName(nameof(OutlinePanel));
        if (tab == SearchTab) EnsureSearchPanel();

        Thumbnails.Visibility = tab == ThumbnailsTab ? Visibility.Visible : Visibility.Collapsed;
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
        Viewer.GoToPage(item.PageIndex, ny);
    }

    // ---------------- Search ----------------

    public async Task SearchAsync(string query)
    {
        query = query.Trim();
        if (query == SearchQuery) return;
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
            }, cts.Token);
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

    public void Dispose()
    {
        _searchCts?.Cancel();
        Document.Dispose();
    }
}
