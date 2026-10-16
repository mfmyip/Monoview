using CleanPDF.Engine;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace CleanPDF.App.Viewer;

/// <summary>The markup colors offered in the toolbar, the selection menu and a markup's own menu.</summary>
public static class MarkupPalette
{
    public static readonly (string Name, MarkupColor Color)[] Colors =
    [
        ("Yellow", new MarkupColor(255, 230, 0)),
        ("Green", new MarkupColor(110, 220, 90)),
        ("Blue", new MarkupColor(100, 190, 255)),
        ("Pink", new MarkupColor(255, 130, 210)),
        ("Red", new MarkupColor(235, 60, 60)),
    ];

    public static MarkupColor Current => Colors.FirstOrDefault(c => c.Name == AppSettings.Current.MarkupColor, Colors[0]).Color;

    public static MarkupKind CurrentKind => Enum.TryParse<MarkupKind>(AppSettings.Current.MarkupKind, out var kind) ? kind : MarkupKind.Highlight;
}

/// <summary>Highlights, underlines and strikeouts: making them from the selection, and recoloring or deleting them.</summary>
public sealed partial class PdfViewer
{
    private readonly LruCache<int, IReadOnlyList<Markup>> _markups = new(48);
    private readonly HashSet<int> _markupsLoading = new();
    private Markup? _pressedMarkup;

    /// <summary>When on, every text selection becomes a markup as soon as the mouse is released.</summary>
    public bool HighlighterMode { get; set; }

    /// <summary>Called before each markup change, so the window can keep an undo step.</summary>
    public Func<Task>? BeforeEdit { get; set; }

    /// <summary>Pages changed (markup added, recolored or removed). The document now has unsaved changes.</summary>
    public event EventHandler<IReadOnlyList<int>>? PagesEdited;

    /// <summary>Marks the selected text with <paramref name="kind"/> in <paramref name="color"/>, page by page.</summary>
    public async Task MarkSelectionAsync(MarkupKind kind, MarkupColor color)
    {
        if (!HasSelection) return;
        var (start, end) = OrderedSelection();
        if (BeforeEdit is not null) await BeforeEdit();
        var edited = new List<int>();
        for (int page = start.Page; page <= end.Page; page++)
        {
            var text = await GetTextAsync(page);
            if (text is null) continue;
            int from = page == start.Page ? start.Caret : 0;
            int to = page == end.Page ? end.Caret : text.Length;
            var rects = text.GetRects(from, to - from);
            if (rects.Count == 0) continue;
            await _doc.AddMarkupAsync(page, kind, color, rects);
            edited.Add(page);
        }
        ClearSelection();
        AfterEdit(edited);
    }

    private async Task RecolorAsync(Markup markup, MarkupColor color)
    {
        if (BeforeEdit is not null) await BeforeEdit();
        await _doc.SetMarkupColorAsync(markup, color);
        AfterEdit([markup.PageIndex]);
    }

    private async Task RemoveAsync(Markup markup)
    {
        if (BeforeEdit is not null) await BeforeEdit();
        await _doc.RemoveMarkupAsync(markup);
        AfterEdit([markup.PageIndex]);
    }

    /// <summary>Stops timers and autoscroll before the view is thrown away.</summary>
    public void Close()
    {
        _settleTimer.Stop();
        StopAutoScroll();
    }

    private void AfterEdit(IReadOnlyList<int> pages)
    {
        if (pages.Count == 0) return;
        foreach (int page in pages)
            RefreshPage(page);
        PagesEdited?.Invoke(this, pages);
    }

    /// <summary>Re-renders a changed page. Its old tiles stay up until the new ones arrive, so it doesn't flicker.</summary>
    private void RefreshPage(int page)
    {
        _tiles.RemoveWhere(k => k.Page == page);
        _previews.Invalidate(page);
        _markups.Remove(page);
        if (_views.TryGetValue(page, out var view))
        {
            view.MarkTilesStale(_w[page]);
            view.PreviewRequested = false;
        }
        UpdateViewport(requestTiles: true);
    }

    private void EnsureMarkups(int index)
    {
        if (_markups.TryGet(index, out _) || !_markupsLoading.Add(index)) return;
        _ = LoadMarkupsAsync(index);
    }

    private async Task LoadMarkupsAsync(int index)
    {
        try
        {
            _markups.Add(index, await _doc.GetMarkupsAsync(index));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or PdfException)
        {
        }
        finally
        {
            _markupsLoading.Remove(index);
        }
    }

    private Markup? MarkupAt(Point p)
    {
        if (!_laidOut || PageCount == 0) return null;
        int page = PageAtPoint(p);
        if (!_markups.TryGet(page, out var markups) || markups.Count == 0) return null;
        var (x, y) = ToPageCoords(page, p);
        // Search from the top: later annotations are drawn over earlier ones.
        return markups.LastOrDefault(m => m.Rects.Any(r => r.Contains(x, y)));
    }

    /// <summary>Clicking a markup (without dragging) opens its menu: colors and Delete.</summary>
    private void ShowMarkupMenu(Markup markup, Point at)
    {
        var menu = new MenuFlyout();
        foreach (var (name, color) in MarkupPalette.Colors)
        {
            var item = new RadioMenuFlyoutItem { Text = name, GroupName = "color", IsChecked = color == markup.Color };
            item.Click += (s, e) => _ = RecolorAsync(markup, color);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = "Delete", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += (s, e) => _ = RemoveAsync(markup);
        menu.Items.Add(delete);
        menu.ShowAt(_surface, new FlyoutShowOptions { Position = at });
    }

    private void AddMarkupItems(MenuFlyout menu, Func<bool> enabled)
    {
        var items = new (string Text, MarkupKind Kind)[]
        {
            ("Highlight", MarkupKind.Highlight), ("Underline", MarkupKind.Underline), ("Strikethrough", MarkupKind.StrikeOut),
        };
        var added = new List<MenuFlyoutItem>();
        foreach (var (text, kind) in items)
        {
            var item = new MenuFlyoutItem { Text = text };
            if (kind == MarkupKind.Highlight) item.KeyboardAcceleratorTextOverride = "Ctrl+H";
            item.Click += (s, e) => _ = MarkSelectionAsync(kind, MarkupPalette.Current);
            menu.Items.Add(item);
            added.Add(item);
        }
        menu.Opening += (s, e) => { foreach (var item in added) item.IsEnabled = enabled(); };
    }
}
