using Monoview.Engine;
using PageRotation = Monoview.Engine.Rotation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Monoview.App.Viewer;

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

    /// <summary>The chosen markup color.</summary>
    public static MarkupColor Current => Parse(AppSettings.Current.MarkupColor) ?? Colors[0].Color;

    /// <summary>A color kept in settings: a palette name, or "#RRGGBB" for one from the color picker. Null when empty or unknown.</summary>
    public static MarkupColor? Parse(string? value) =>
        value is ['#', .. var hex] && hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int rgb)
            ? new MarkupColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : Colors.Where(c => c.Name == value).Select(c => (MarkupColor?)c.Color).FirstOrDefault();

    public static MarkupKind CurrentKind => Enum.TryParse<MarkupKind>(AppSettings.Current.MarkupKind, out var kind) ? kind : MarkupKind.Highlight;

    /// <summary>How <paramref name="color"/> is kept in settings: its palette name, or "#RRGGBB".</summary>
    public static string SettingValue(MarkupColor color) =>
        Colors.FirstOrDefault(c => c.Color == color).Name ?? $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Brush Brush(MarkupColor c) => new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));

    // ---- The color panel, as in Preview: a row of brights, a grid of shades with None first; the color in use is ringed.
    // The highlighter, a markup's menu and the markup bar all pick colors with it.

    private static readonly (string Name, MarkupColor Color)[] Brights =
    [
        ("Red", new(255, 59, 48)), ("Orange", new(255, 149, 0)), ("Yellow", new(255, 204, 0)), ("Green", new(52, 199, 89)),
        ("Cyan", new(90, 200, 250)), ("Blue", new(10, 100, 220)), ("Magenta", new(255, 45, 206)), ("Purple", new(175, 82, 222)),
        ("Tan", new(198, 156, 109)), ("White", new(255, 255, 255)), ("Grey", new(138, 138, 138)), ("Black", new(0, 0, 0)),
    ];

    private static readonly MarkupColor[] Shades = BuildShades();

    /// <summary>A row of greys, then 8 rows of 12 hues from dark to light.</summary>
    private static MarkupColor[] BuildShades()
    {
        var shades = new List<MarkupColor>();
        for (int i = 0; i < 12; i++)
        {
            byte v = (byte)Math.Round(255 - i * 255 / 11.0);
            shades.Add(new MarkupColor(v, v, v));
        }
        int[] hues = [200, 215, 235, 265, 290, 340, 5, 20, 35, 48, 60, 90];
        foreach (double light in new[] { 0.22, 0.32, 0.42, 0.52, 0.62, 0.72, 0.82, 0.91 })
            foreach (int hue in hues)
            {
                double a = 0.8 * Math.Min(light, 1 - light);
                byte Channel(int n)
                {
                    double k = (n + hue / 30.0) % 12;
                    return (byte)Math.Round(255 * (light - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1))));
                }
                shades.Add(new MarkupColor(Channel(0), Channel(8), Channel(4)));
            }
        return shades.ToArray();
    }

    /// <summary>
    /// Colors to pick from in <paramref name="flyout"/>, with None first among the greys when <paramref name="hasNone"/>,
    /// and More colors, which swaps in a color picker. Picking a color hides the flyout first.
    /// </summary>
    public static FrameworkElement ColorPanel(Flyout flyout, MarkupColor? current, bool hasNone, Action<MarkupColor?> choose)
    {
        void Choose(MarkupColor? color)
        {
            flyout.Hide();
            choose(color);
        }
        var accent = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]);
        Button Cell(MarkupColor? color, string name, double size, bool selected)
        {
            FrameworkElement face = color is { } c
                ? new Border { Background = Brush(c), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 0, 0)), BorderThickness = new Thickness(c is { R: > 240, G: > 240, B: > 240 } ? 1 : 0) }
                : new Grid { Background = Brush(new MarkupColor(255, 255, 255)), Children = { new Microsoft.UI.Xaml.Shapes.Path
                    {
                        Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), "M0,10 L10,0"), Stretch = Stretch.Fill, Margin = new Thickness(1), // fits the cell, ringed or not
                        Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28)), StrokeThickness = 1.5,
                    } } };
            var button = new Button
            {
                Content = face, Width = size, Height = size, MinWidth = 0, MinHeight = 0, Padding = new Thickness(selected ? 2 : 0),
                CornerRadius = new CornerRadius(selected ? 3 : 0),
                BorderThickness = new Thickness(selected ? 2 : 0), BorderBrush = accent, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            ToolTipService.SetToolTip(button, name);
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => Choose(color);
            return button;
        }
        static Grid Grid12(double cell)
        {
            var grid = new Grid();
            for (int i = 0; i < 12; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cell) });
            return grid;
        }
        void Place(Grid grid, int index, UIElement child)
        {
            while (grid.RowDefinitions.Count <= index / 12) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
            Grid.SetRow((FrameworkElement)child, index / 12);
            Grid.SetColumn((FrameworkElement)child, index % 12);
            grid.Children.Add(child);
        }

        var panel = new StackPanel { Spacing = 10, Width = 216 };

        // Ring only the first cell in the color in use: black and white are in both grids.
        bool ringed = false;
        bool Ring(MarkupColor color) => !ringed && color == current && (ringed = true);

        var brights = Grid12(18);
        for (int i = 0; i < Brights.Length; i++)
            Place(brights, i, Cell(Brights[i].Color, Brights[i].Name, 18, Ring(Brights[i].Color)));
        panel.Children.Add(brights);

        var shades = Grid12(18);
        for (int i = 0; i < Shades.Length; i++)
            Place(shades, i, i == 0 && hasNone
                ? Cell(null, "None", 18, current is null)
                : Cell(Shades[i], SettingValue(Shades[i]), 18, Ring(Shades[i])));
        panel.Children.Add(shades);

        var more = new Button { Content = "More colors\u2026", HorizontalAlignment = HorizontalAlignment.Center };
        more.Click += (_, _) =>
        {
            var start = current ?? new MarkupColor(0, 0, 0);
            var picker = new ColorPicker
            {
                Color = Windows.UI.Color.FromArgb(255, start.R, start.G, start.B),
                IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = false,
            };
            var use = new Button { Content = "Use color", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
            use.Click += (_, _) => Choose(new MarkupColor(picker.Color.R, picker.Color.G, picker.Color.B));
            flyout.Content = new StackPanel { Spacing = 10, Children = { picker, use } };
        };
        panel.Children.Add(more);
        return panel;
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
    public async Task MarkSelectionAsync(MarkupKind kind, MarkupColor color, string comment = "")
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
            await _doc.AddMarkupAsync(page, kind, color, rects, comment);
            edited.Add(page);
        }
        ClearSelection();
        AfterEdit(edited);
    }

    /// <summary>
    /// The markup a click selected, as one object like a shape: outlined, with its comment under it if it has one.
    /// The toolbar's color recolors it, Delete removes it, Esc or a click elsewhere lets it go.
    /// </summary>
    public Markup? SelectedMarkup
    {
        get => _selectedMarkup;
        private set
        {
            if (_selectedMarkup == value) return;
            var old = _selectedMarkup;
            _selectedMarkup = value;
            if (value is not null) Selected = null; // one selected object at a time
            foreach (int page in new[] { old?.PageIndex, value?.PageIndex }.OfType<int>().Distinct())
                if (_views.TryGetValue(page, out var view)) RefreshOverlay(view);
            SelectedMarkupChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Markup? _selectedMarkup;

    public event EventHandler? SelectedMarkupChanged;

    /// <summary>The same annotation, even as another record read after a change.</summary>
    private static bool Same(Markup a, Markup? b) => b is not null && a.PageIndex == b.PageIndex && a.AnnotIndex == b.AnnotIndex;

    public Task RecolorSelectedMarkupAsync(MarkupColor color) =>
        SelectedMarkup is { } markup ? RecolorAsync(markup, color) : Task.CompletedTask;

    private async Task RecolorAsync(Markup markup, MarkupColor color)
    {
        if (BeforeEdit is not null) await BeforeEdit();
        await _doc.SetMarkupColorAsync(markup, color);
        if (Same(markup, SelectedMarkup)) SelectedMarkup = markup with { Color = color };
        AfterEdit([markup.PageIndex]);
    }

    private async Task SetCommentAsync(Markup markup, string comment)
    {
        if (comment == markup.Comment) return;
        if (BeforeEdit is not null) await BeforeEdit();
        await _doc.SetMarkupCommentAsync(markup, comment);
        if (Same(markup, SelectedMarkup)) SelectedMarkup = markup with { Comment = comment };
        AfterEdit([markup.PageIndex]);
    }

    private async Task RemoveAsync(Markup markup)
    {
        if (BeforeEdit is not null) await BeforeEdit();
        await _doc.RemoveMarkupAsync(markup);
        SelectedMarkup = null; // the page's other annotations move up in its list
        AfterEdit([markup.PageIndex]);
    }

    /// <summary>The selected markup's outline, and its comment card under it, in the page's overlay.</summary>
    private void DrawMarkupSelection(PageView view)
    {
        if (SelectedMarkup is not { } markup || markup.PageIndex != view.Index) return;
        double w = _w[view.Index], h = _h[view.Index];
        var accent = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]);
        double left = double.MaxValue, bottom = 0;
        foreach (var rect in markup.Rects)
        {
            var r = PageRotation.ToView(rect, _rotation);
            var outline = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = (r.Right - r.Left) * w + 6, Height = (r.Bottom - r.Top) * h + 6,
                Stroke = accent, StrokeThickness = 2, RadiusX = 3, RadiusY = 3,
            };
            Canvas.SetLeft(outline, r.Left * w - 3);
            Canvas.SetTop(outline, r.Top * h - 3);
            view.Overlay.Children.Add(outline);
            left = Math.Min(left, r.Left * w);
            bottom = Math.Max(bottom, r.Bottom * h);
        }
        if (markup.Comment.Length == 0) return;
        var card = CommentCard(markup.Comment);
        Canvas.SetLeft(card, left);
        Canvas.SetTop(card, bottom + 8);
        view.Overlay.Children.Add(card);
    }

    /// <summary>Stops timers and autoscroll before the view is thrown away.</summary>
    public void Close()
    {
        DetachForm(_doc);
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
        _shapes.Remove(page);
        if (_views.TryGetValue(page, out var view))
        {
            view.MarkTilesStale(_w[page]);
            view.PreviewRequested = false;
        }
        UpdateViewport(requestTiles: true);
    }

    /// <summary>
    /// Shows <paramref name="doc"/> in place of the current document, which must have as many pages. Without
    /// <paramref name="carry"/> the pages are the same (undo, redo, save): each redraws, and its old drawing stays up
    /// until the new one is ready. With it, page i shows old page <paramref name="carry"/>[i] (moved), or is new
    /// when that is -1 (rotated, cropped): moved pages keep their preview, and nothing shows a page that left.
    /// </summary>
    public void ReplaceDocument(PdfDocument doc, IReadOnlyList<int>? carry = null)
    {
        SelectedMarkup = null;
        DetachForm(_doc);
        _doc = doc;
        AttachForm(doc);
        _previews.Replace(doc, carry);
        _tiles.Clear();
        _texts.Clear();
        _links.Clear();
        _markups.Clear();
        _shapes.Clear();
        _pressedMarkup = null;
        Selected = null;
        _drag = null;
        _settling = null;
        _drawing = null;
        foreach (var view in _views.Values)
        {
            if (carry is null || carry[view.Index] == view.Index)
            {
                view.MarkTilesStale(_w[view.Index]);
                view.PreviewRequested = false;
            }
            else
            {
                view.Bind(view.Index);
                RefreshOverlay(view);
            }
        }
        // Rotated or moved pages may change size.
        if (carry is not null)
            ApplyZoom(FitMode == FitMode.Custom ? _zoom : ComputeFitZoom(), TopCenterAnchor(), requestTiles: true);
        else
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
            if (doc != _doc) return;
            _markups.Add(index, markups);
            if (markups.Any(m => m.Comment.Length > 0) && _views.TryGetValue(index, out var view)) RefreshOverlay(view);
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

    /// <summary>A comment as a note card: dark text on white, in either theme, so it stands apart from the menu around it.</summary>
    private static Border CommentCard(string comment) => new()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.White),
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 208, 208)), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), MaxWidth = 280,
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Comment", FontSize = 11, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 110, 110, 110)) },
                new TextBlock { Text = comment, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 26, 26)) },
            },
        },
    };

    /// <summary>
    /// As in Acrobat, a markup with a comment gets a dot in its own color, ringed in white, at the start of its first
    /// line. Drawn in the page's overlay, so it moves with the page.
    /// </summary>
    private void DrawCommentMarkers(PageView view)
    {
        if (!_markups.TryGet(view.Index, out var markups)) return;
        double w = _w[view.Index], h = _h[view.Index];
        foreach (var markup in markups.Where(m => m.Comment.Length > 0 && m.Rects.Count > 0))
        {
            var r = PageRotation.ToView(markup.Rects[0], _rotation);
            // A thin dark edge outside the white ring, so a light color still reads on a white page.
            var edge = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 0, 0, 0)) };
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 14, Height = 14, Fill = MarkupPalette.Brush(markup.Color),
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), StrokeThickness = 2.5,
            };
            Canvas.SetLeft(edge, r.Left * w - 9);
            Canvas.SetTop(edge, r.Top * h - 9);
            Canvas.SetLeft(dot, r.Left * w - 8);
            Canvas.SetTop(dot, r.Top * h - 8);
            view.Overlay.Children.Add(edge);
            view.Overlay.Children.Add(dot);
        }
    }

    /// <summary>Just under the left end of the markup's last line, in surface coordinates: where its comment editor opens.</summary>
    private Point BelowMarkup(Markup markup)
    {
        var r = PageRotation.ToView(markup.Rects[^1], _rotation);
        int page = markup.PageIndex;
        return new Point(_left[page] + r.Left * _w[page], _top[page] + r.Bottom * _h[page] + 4);
    }

    /// <summary>Clicking a markup opens its comment to add or edit, under the markup; hovering shows it.</summary>
    private void EditComment(Markup markup) =>
        ShowCommentEditor(BelowMarkup(markup), markup.Comment, text => SetCommentAsync(markup, text));

    /// <summary>Right-clicking a markup opens its menu: its comment if it has one, Add or Edit comment, colors and Delete.</summary>
    private void ShowMarkupMenu(Markup markup, Point at)
    {
        var menu = new Flyout();
        Button Row(string glyph, string text, Action click)
        {
            var button = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = glyph, FontSize = 16 }, new TextBlock { Text = text } } },
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            };
            button.Click += (_, _) =>
            {
                menu.Hide();
                click();
            };
            return button;
        }
        var panel = new StackPanel { Spacing = 6 };
        if (markup.Comment.Length > 0)
            panel.Children.Add(CommentCard(markup.Comment));
        panel.Children.Add(Row("", markup.Comment.Length > 0 ? "Edit comment" : "Add comment", () => EditComment(markup)));
        panel.Children.Add(MarkupPalette.ColorPanel(menu, markup.Color, hasNone: false, color => _ = RecolorAsync(markup, color!.Value)));
        panel.Children.Add(Row("", "Delete", () => _ = RemoveAsync(markup)));
        menu.Content = panel;
        menu.ShowAt(_surface, new FlyoutShowOptions { Position = at });
    }

    /// <summary>A box to type a comment in at <paramref name="at"/>; Save or Ctrl+Enter hands the text to <paramref name="save"/>.</summary>
    private void ShowCommentEditor(Point at, string text, Func<string, Task> save)
    {
        var flyout = new Flyout();
        var box = new TextBox { Text = text, PlaceholderText = "Comment", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = 260, Height = 100 };
        var ok = new Button { Content = "Save", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
        ok.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Enter, Modifiers = Windows.System.VirtualKeyModifiers.Control });
        ToolTipService.SetToolTip(ok, "Save (Ctrl+Enter)");
        ok.Click += (_, _) =>
        {
            flyout.Hide();
            _ = save(box.Text.Trim());
        };
        flyout.Content = new StackPanel { Spacing = 8, Children = { box, ok } };
        flyout.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectionStart = box.Text.Length;
        };
        HideCommentTip();
        flyout.ShowAt(_surface, new FlyoutShowOptions { Position = at, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    /// <summary>
    /// As in Acrobat, hovering a markup shows it as one object: a hand cursor and an outline around all its lines, ready
    /// for a click or right-click. A markup with a comment also shows its card beside the pointer. Both are drawn in the
    /// overlay layer above the pages. <paramref name="onSurface"/> finds the markup; <paramref name="inView"/> places the card.
    /// Returns whether a markup is under the pointer.
    /// </summary>
    private bool UpdateMarkupHover(Point onSurface, Point inView)
    {
        var markup = MarkupAt(onSurface);
        if (markup is null)
        {
            HideCommentTip();
            return false;
        }
        if (markup != _hovered)
        {
            HideCommentTip();
            _hovered = markup;
            var accent = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]);
            var toView = _surface.TransformToVisual(this);
            int page = markup.PageIndex;
            foreach (var rect in markup.Rects)
            {
                var r = PageRotation.ToView(rect, _rotation);
                var topLeft = toView.TransformPoint(new Point(_left[page] + r.Left * _w[page], _top[page] + r.Top * _h[page]));
                var outline = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = (r.Right - r.Left) * _w[page] + 4, Height = (r.Bottom - r.Top) * _h[page] + 4,
                    Stroke = accent, StrokeThickness = 1.5, RadiusX = 2, RadiusY = 2,
                };
                Canvas.SetLeft(outline, topLeft.X - 2);
                Canvas.SetTop(outline, topLeft.Y - 2);
                _hoverOutline.Add(outline);
                _overlayLayer.Children.Add(outline);
            }
            if (markup.Comment.Length > 0 && !Same(markup, SelectedMarkup)) // a selected one shows its card already
            {
                _commentTip = CommentCard(markup.Comment);
                _overlayLayer.Children.Add(_commentTip);
                _commentTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }
        }
        if (_commentTip is not null)
        {
            // Below and right of the pointer, kept inside the view.
            var size = _commentTip.DesiredSize;
            Canvas.SetLeft(_commentTip, Math.Max(0, Math.Min(inView.X + 14, ActualWidth - size.Width - 8)));
            Canvas.SetTop(_commentTip, inView.Y + 20 + size.Height > ActualHeight ? inView.Y - size.Height - 8 : inView.Y + 20);
        }
        return true;
    }

    /// <summary>Clears the hover outline and comment card, when the pointer leaves the markup, the view scrolls or a menu opens.</summary>
    private void HideCommentTip()
    {
        foreach (var outline in _hoverOutline) _overlayLayer.Children.Remove(outline);
        _hoverOutline.Clear();
        if (_commentTip is not null) _overlayLayer.Children.Remove(_commentTip);
        _commentTip = null;
        _hovered = null;
    }

    private readonly MenuFlyout _textMenu;
    private Markup? _hovered;
    private readonly List<Microsoft.UI.Xaml.Shapes.Rectangle> _hoverOutline = new();
    private Border? _commentTip;
    private Point _contextAt;

    /// <summary>Right-click on a markup opens the markup's menu instead of the text menu.</summary>
    /// <remarks>
    /// The text menu is shown from here, not set as the surface's ContextFlyout: WinUI opens a ContextFlyout itself and
    /// handles the event first, so the markup menu never got its turn and the text menu came up greyed out.
    /// </remarks>
    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        _contextAt = e.TryGetPosition(_surface, out var p) ? p
            : new Point(_scroller.HorizontalOffset + _scroller.ViewportWidth / 2, _scroller.VerticalOffset + _scroller.ViewportHeight / 2);
        HideCommentTip();
        if (!HasSelection && MarkupAt(_contextAt) is { } markup)
            ShowMarkupMenu(markup, _contextAt);
        else
            _textMenu.ShowAt(_surface, new FlyoutShowOptions { Position = _contextAt });
    }

    private void AddMarkupItems(MenuFlyout menu, Func<bool> enabled)
    {
        var items = new (string Text, MarkupKind Kind)[]
        {
            ("Highlight", MarkupKind.Highlight), ("Underline", MarkupKind.Underline), ("Strikethrough", MarkupKind.StrikeOut),
        };
        var added = new List<MenuFlyoutItem>();
        // First, as Acrobat's "Add Note to Text": a highlight that carries a comment.
        var commented = new MenuFlyoutItem { Text = "Highlight with comment…", Icon = new FontIcon { Glyph = "" } };
        commented.Click += (_, _) => ShowCommentEditor(_contextAt, "", text => MarkSelectionAsync(MarkupKind.Highlight, MarkupPalette.Current, text));
        menu.Items.Add(commented);
        added.Add(commented);
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
