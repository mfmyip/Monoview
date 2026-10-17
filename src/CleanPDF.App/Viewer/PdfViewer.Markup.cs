using CleanPDF.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
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

    /// <summary>The chosen color: a palette name, or "#RRGGBB" for a color from the color picker.</summary>
    public static MarkupColor Current =>
        AppSettings.Current.MarkupColor is ['#', .. var hex] && hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int rgb)
            ? new MarkupColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : Colors.FirstOrDefault(c => c.Name == AppSettings.Current.MarkupColor, Colors[0]).Color;

    public static MarkupKind CurrentKind => Enum.TryParse<MarkupKind>(AppSettings.Current.MarkupKind, out var kind) ? kind : MarkupKind.Highlight;

    /// <summary>How <paramref name="color"/> is kept in settings: its palette name, or "#RRGGBB".</summary>
    public static string SettingValue(MarkupColor color) =>
        Colors.FirstOrDefault(c => c.Color == color).Name ?? $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Color dots for a flyout: the palette, then <paramref name="selected"/> if it isn't in the palette, then a
    /// button that swaps the flyout's content for a color picker. Picking a color hides the flyout first.
    /// </summary>
    public static StackPanel ColorRow(Flyout host, MarkupColor selected, Action<MarkupColor> choose)
    {
        void Choose(MarkupColor color)
        {
            host.Hide();
            choose(color);
        }

        var ring = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]);
        var entries = Colors.ToList();
        if (!entries.Any(e => e.Color == selected))
            entries.Add((SettingValue(selected), selected));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (name, color) in entries)
        {
            var dot = new Grid { Width = 24, Height = 24 };
            if (color == selected)
                dot.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Stroke = ring, StrokeThickness = 2 });
            dot.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, color.R, color.G, color.B)) });
            var button = new Button { Content = dot, Padding = new Thickness(2), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            ToolTipService.SetToolTip(button, name);
            AutomationProperties.SetName(button, name);
            button.Click += (s, e) => Choose(color);
            row.Children.Add(button);
        }

        var more = new Button
        {
            Content = new FontIcon { Glyph = "\uE710", FontSize = 12 },
            Width = 28, Height = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(more, "More colors");
        AutomationProperties.SetName(more, "More colors");
        more.Click += (s, e) =>
        {
            var picker = new ColorPicker
            {
                Color = Windows.UI.Color.FromArgb(255, selected.R, selected.G, selected.B),
                IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = false,
            };
            var use = new Button { Content = "Use color", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
            use.Click += (s2, e2) => Choose(new MarkupColor(picker.Color.R, picker.Color.G, picker.Color.B));
            host.Content = new StackPanel { Spacing = 10, Children = { picker, use } };
        };
        row.Children.Add(more);
        return row;
    }
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

    /// <summary>
    /// Shows <paramref name="doc"/> in place of the current document, which must have the same pages at the same
    /// sizes (undo, redo, save). Every page redraws, and its old drawing stays up until the new one is ready.
    /// </summary>
    public void ReplaceDocument(PdfDocument doc)
    {
        _doc = doc;
        _previews.Document = doc;
        _tiles.Clear();
        _texts.Clear();
        _links.Clear();
        _markups.Clear();
        _pressedMarkup = null;
        foreach (var view in _views.Values)
        {
            view.MarkTilesStale(_w[view.Index]);
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
            var doc = _doc;
            var markups = await doc.GetMarkupsAsync(index);
            if (doc == _doc) _markups.Add(index, markups);
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
        var menu = new Flyout();
        var delete = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new SymbolIcon(Symbol.Delete), new TextBlock { Text = "Delete" } } },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
        };
        delete.Click += (s, e) =>
        {
            menu.Hide();
            _ = RemoveAsync(markup);
        };
        menu.Content = new StackPanel
        {
            Spacing = 6,
            Children = { MarkupPalette.ColorRow(menu, markup.Color, color => _ = RecolorAsync(markup, color)), delete },
        };
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
