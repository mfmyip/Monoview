using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Monoview.Engine;
using Windows.Foundation;
using PageRotation = Monoview.Engine.Rotation;
using Shape = Monoview.Engine.Shape;

namespace Monoview.App.Viewer;

/// <summary>A picture to place on a page, such as a signature: straight-alpha BGRA pixels.</summary>
public sealed record StampPicture(int Width, int Height, byte[] Bgra);

/// <summary>The markup bar's drawing tools. Select is the usual pointer: select text, or select and move objects.</summary>
public enum MarkupTool { Select, Ink, Line, Arrow, Rectangle, Ellipse, TextBox, Note, Crop }

/// <summary>
/// Objects on pages: drawing them with the markup tools, placing signatures, and selecting, moving, resizing,
/// editing and deleting them. Every change is one undo step.
/// </summary>
public sealed partial class PdfViewer
{
    private const double HandleSize = 10;

    private readonly LruCache<int, IReadOnlyList<Shape>> _shapes = new(48);
    private readonly HashSet<int> _shapesLoading = new();
    private readonly InputCursor _placeCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    private readonly InputCursor _moveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private readonly InputCursor _resizeNwSe = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
    private readonly InputCursor _resizeNeSw = InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest);
    private readonly InputCursor _resizeWe = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private readonly InputCursor _resizeNs = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    private ShapeDrag? _drag;
    private Drawing? _drawing;
    private int? _selectNewestOn;
    private TextBox? _textEditor;
    private MarkupTool _tool;

    /// <summary>A shape being dragged: where the pointer went down, the rect it started at and where it is now, as shown.</summary>
    private sealed record ShapeDrag(Shape Shape, Handle? Resize, (double X, double Y) Start, NormRect From)
    {
        public NormRect Now { get; set; } = From;
    }

    /// <summary>A shape being drawn with a tool, in the page as shown (0 to 1).</summary>
    private sealed class Drawing(MarkupTool tool, int page, (double X, double Y) start)
    {
        public MarkupTool Tool { get; } = tool;
        public int Page { get; } = page;
        public List<(double X, double Y)> Points { get; } = [start];
        public (double X, double Y) Start => Points[0];
        public (double X, double Y) End => Points[^1];
    }

    /// <summary>The tool the pointer uses on pages. After a shape, text box or note it goes back to Select; freehand stays on.</summary>
    public MarkupTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            _tool = value;
            _drawing = null;
            if (value != MarkupTool.Select)
            {
                // A drawing tool styles the next object, so let go of the selected one, as Preview does.
                PendingStamp = null;
                Selected = null;
                RefreshAllOverlays();
            }
            ToolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ToolChanged;

    /// <summary>The crop tool drew an area: the page and the area to keep, in unrotated normalized page coordinates.</summary>
    public event EventHandler<(int Page, NormRect Area)>? CropRequested;

    /// <summary>The style of the next object of each kind.</summary>
    public Func<ShapeKind, ShapeStyle> StyleFor { get; set; } = _ => new(new MarkupColor(220, 40, 40));

    /// <summary>The kind of object <paramref name="tool"/> makes; null for Select and Crop.</summary>
    public static ShapeKind? KindOf(MarkupTool tool) => tool switch
    {
        MarkupTool.Ink or MarkupTool.Line or MarkupTool.Arrow => ShapeKind.Ink,
        MarkupTool.Rectangle => ShapeKind.Rectangle,
        MarkupTool.Ellipse => ShapeKind.Ellipse,
        MarkupTool.TextBox => ShapeKind.TextBox,
        MarkupTool.Note => ShapeKind.Note,
        _ => null,
    };

    /// <summary>The selected object, if any.</summary>
    public Shape? Selected
    {
        get => _selected;
        private set
        {
            if (_selected == value) return;
            _selected = value;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Shape? _selected;

    public event EventHandler? SelectionChanged;

    /// <summary>The picture the next click on a page places, such as a signature; null when not placing.</summary>
    public StampPicture? PendingStamp { get; set; }

    private void EnsureShapes(int index)
    {
        if (_shapes.TryGet(index, out _) || !_shapesLoading.Add(index)) return;
        _ = LoadShapesAsync(index);
    }

    private async Task LoadShapesAsync(int index)
    {
        try
        {
            var doc = _doc;
            var shapes = await doc.GetShapesAsync(index);
            if (doc != _doc) return;
            _shapes.Add(index, shapes);
            if (_selectNewestOn == index && shapes.Count > 0)
            {
                _selectNewestOn = null;
                Selected = shapes[^1];
                if (_views.TryGetValue(index, out var view)) RefreshOverlay(view);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or PdfException)
        {
        }
        finally
        {
            _shapesLoading.Remove(index);
        }
    }

    // ---- Coordinates: shapes are kept unrotated; drawing and dragging work in the page as shown ----

    private (double X, double Y) ToShownCoords(int page, Point p) =>
        (Math.Clamp((p.X - _left[page]) / _w[page], 0, 1), Math.Clamp((p.Y - _top[page]) / _h[page], 0, 1));

    private (double X, double Y) Unrotate((double X, double Y) shown) => PageRotation.FromView(shown.X, shown.Y, _rotation);

    private NormRect FromShown(NormRect shown)
    {
        var (x1, y1) = Unrotate((shown.Left, shown.Top));
        var (x2, y2) = Unrotate((shown.Right, shown.Bottom));
        return new NormRect((float)Math.Min(x1, x2), (float)Math.Min(y1, y2), (float)Math.Max(x1, x2), (float)Math.Max(y1, y2));
    }

    private static NormRect Between((double X, double Y) a, (double X, double Y) b) =>
        new((float)Math.Min(a.X, b.X), (float)Math.Min(a.Y, b.Y), (float)Math.Max(a.X, b.X), (float)Math.Max(a.Y, b.Y));

    private Shape? ShapeAt(Point p)
    {
        if (!_laidOut || PageCount == 0) return null;
        int page = PageAtPoint(p);
        if (!_shapes.TryGet(page, out var shapes) || shapes.Count == 0) return null;
        var (x, y) = ToPageCoords(page, p);
        return shapes.LastOrDefault(s => s.Rect.Contains(x, y));
    }

    /// <summary>A resize handle, by the edges it moves: -1 for left or top, 1 for right or bottom, 0 for neither.</summary>
    private readonly record struct Handle(int X, int Y);

    private static readonly Handle[] Corners = [new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)];
    private static readonly Handle[] AllHandles = [.. Corners, new(0, -1), new(1, 0), new(0, 1), new(-1, 0)];

    /// <summary>Preview's handles: the corners and edge middles. Pictures keep their shape, so corners only; notes keep their size.</summary>
    private static Handle[] HandlesFor(ShapeKind kind) => kind switch
    {
        ShapeKind.Note => [],
        ShapeKind.Stamp => Corners,
        _ => AllHandles,
    };

    /// <summary>Where <paramref name="handle"/> sits on <paramref name="shown"/>, in pixels from the page's corner.</summary>
    private static Point HandlePoint(NormRect shown, Handle handle, double w, double h) => new(
        (handle.X < 0 ? shown.Left : handle.X > 0 ? shown.Right : (shown.Left + shown.Right) / 2) * w,
        (handle.Y < 0 ? shown.Top : handle.Y > 0 ? shown.Bottom : (shown.Top + shown.Bottom) / 2) * h);

    private Handle? HandleAt(Shape shape, Point p)
    {
        var shown = PageRotation.ToView(shape.Rect, _rotation);
        int page = shape.PageIndex;
        foreach (var handle in HandlesFor(shape.Kind))
        {
            var at = HandlePoint(shown, handle, _w[page], _h[page]);
            if (Math.Abs(p.X - _left[page] - at.X) <= HandleSize && Math.Abs(p.Y - _top[page] - at.Y) <= HandleSize) return handle;
        }
        return null;
    }

    private InputCursor CursorFor(Handle handle) =>
        handle.X == 0 ? _resizeNs : handle.Y == 0 ? _resizeWe : handle.X == handle.Y ? _resizeNwSe : _resizeNeSw;

    // ---- Pointer hooks, called first by the viewer's pointer handlers; true when they took the event ----

    private bool HandleShapePress(PointerRoutedEventArgs e, Point p)
    {
        CommitTextEditor();
        if (!_laidOut || PageCount == 0) return false;
        int page = PageAtPoint(p);
        if (PendingStamp is { } picture)
        {
            PendingStamp = null;
            var (x, y) = ToShownCoords(page, p);
            _ = PlaceStampAsync(picture, page, x, y);
            return true;
        }
        if (_tool == MarkupTool.Note)
        {
            var (nx, ny) = Unrotate(ToShownCoords(page, p));
            Tool = MarkupTool.Select;
            _ = AddNoteAsync(page, nx, ny, p);
            return true;
        }
        if (_tool != MarkupTool.Select)
        {
            _drawing = new Drawing(_tool, page, ToShownCoords(page, p));
            _surface.CapturePointer(e.Pointer);
            return true;
        }

        var handle = Selected is { } selected ? HandleAt(selected, p) : null;
        var hit = handle is null ? ShapeAt(p) : Selected;
        if (hit is null)
        {
            if (Selected is null) return false;
            Selected = null;
            RefreshAllOverlays();
            return false;
        }
        Selected = hit;
        var shown = PageRotation.ToView(hit.Rect, _rotation);
        _drag = new ShapeDrag(hit, handle, ToShownCoords(hit.PageIndex, p), shown);
        _surface.CapturePointer(e.Pointer);
        _scroller.Focus(FocusState.Pointer);
        RefreshAllOverlays();
        return true;
    }

    private bool HandleShapeMove(Point p, PointerRoutedEventArgs e)
    {
        if (_drawing is { } drawing)
        {
            var point = ToShownCoords(drawing.Page, p);
            // Freehand takes every sample the pen or mouse gave since the last event, not just the newest.
            if (drawing.Tool == MarkupTool.Ink)
                foreach (var sample in e.GetIntermediatePoints(_surface).OrderBy(s => s.Timestamp))
                    drawing.Points.Add(ToShownCoords(drawing.Page, sample.Position));
            else if (drawing.Points.Count == 1) drawing.Points.Add(point);
            else drawing.Points[^1] = point;
            if (_views.TryGetValue(drawing.Page, out var view)) RefreshOverlay(view);
            return true;
        }
        if (_drag is { } drag)
        {
            int page = drag.Shape.PageIndex;
            var (x, y) = ToShownCoords(page, p);
            double dx = x - drag.Start.X, dy = y - drag.Start.Y;
            var from = drag.From;
            if (drag.Resize is { } edges)
            {
                // The handle's edges follow the pointer; the opposite edges stay put.
                const double min = 0.005;
                double left = from.Left, top = from.Top, right = from.Right, bottom = from.Bottom;
                if (edges.X < 0) left = Math.Clamp(left + dx, 0, right - min);
                if (edges.X > 0) right = Math.Clamp(right + dx, left + min, 1);
                if (edges.Y < 0) top = Math.Clamp(top + dy, 0, bottom - min);
                if (edges.Y > 0) bottom = Math.Clamp(bottom + dy, top + min, 1);
                if (drag.Shape.Kind == ShapeKind.Stamp) // pictures keep their shape: the height follows the width
                {
                    double height = (right - left) * from.Height / from.Width;
                    if (edges.Y < 0) top = Math.Max(0, bottom - height);
                    else bottom = Math.Min(1, top + height);
                }
                drag.Now = new NormRect((float)left, (float)top, (float)right, (float)bottom);
            }
            else
            {
                double left = Math.Clamp(from.Left + dx, 0, 1 - from.Width), top = Math.Clamp(from.Top + dy, 0, 1 - from.Height);
                drag.Now = new NormRect((float)left, (float)top, (float)(left + from.Width), (float)(top + from.Height));
            }
            if (_views.TryGetValue(page, out var view)) RefreshOverlay(view);
            return true;
        }

        InputCursor? cursor = PendingStamp is not null || _tool != MarkupTool.Select ? _placeCursor
            : Selected is { } selected && HandleAt(selected, p) is { } handle ? CursorFor(handle)
            : ShapeAt(p) is not null ? _moveCursor
            : null;
        if (cursor is null) return false;
        ProtectedCursor = cursor;
        _textCursorShown = false;
        _overLink = true; // so leaving the object puts the usual cursor back
        return true;
    }

    private bool HandleShapeRelease(PointerRoutedEventArgs e, Point p)
    {
        if (_drawing is { } drawing)
        {
            _drawing = null;
            _surface.ReleasePointerCapture(e.Pointer);
            _ = DrawAsync(drawing.Tool, drawing.Page, drawing.Points);
            return true;
        }
        if (_drag is not { } drag) return false;
        _drag = null;
        _surface.ReleasePointerCapture(e.Pointer);
        if (drag.Now != drag.From)
            _ = MoveShapeAsync(drag.Shape, FromShown(drag.Now));
        else if (drag.Shape.Kind == ShapeKind.Note)
            EditNote(drag.Shape, p); // a click on a note opens it
        return true;
    }

    /// <summary>A double-click on a text box edits its text.</summary>
    private bool HandleShapeDoubleTap(Point p)
    {
        if (ShapeAt(p) is not { Kind: ShapeKind.TextBox } box) return false;
        OpenTextEditor(box.PageIndex, PageRotation.ToView(box.Rect, _rotation), box);
        return true;
    }

    // ---- Creating ----

    /// <summary>
    /// Adds what <paramref name="tool"/> drew through <paramref name="points"/> (the page as shown, 0 to 1): all the
    /// points for freehand, the first and last for the others. A text box opens for typing instead.
    /// </summary>
    public async Task DrawAsync(MarkupTool tool, int page, IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count == 0) return;
        var (start, end) = (points[0], points[^1]);
        var box = Between(start, end);
        bool tiny = box.Width < 0.01 && box.Height < 0.01;
        switch (tool)
        {
            case MarkupTool.Ink:
                if (points.Count < 2) break;
                await EditAsync(page, () => _doc.AddInkAsync(page, [points.Select(Unrotate).ToList()], StyleFor(ShapeKind.Ink)), select: false);
                break;
            case MarkupTool.Line or MarkupTool.Arrow:
                if (tiny) break;
                var strokes = new List<IReadOnlyList<(double X, double Y)>> { new[] { start, end }.Select(Unrotate).ToList() };
                if (tool == MarkupTool.Arrow) strokes.Add(ArrowHead(page, start, end).Select(Unrotate).ToList());
                Tool = MarkupTool.Select;
                await EditAsync(page, () => _doc.AddInkAsync(page, strokes, StyleFor(ShapeKind.Ink)));
                break;
            case MarkupTool.Rectangle or MarkupTool.Ellipse:
                if (tiny) break;
                var kind = tool == MarkupTool.Rectangle ? ShapeKind.Rectangle : ShapeKind.Ellipse;
                Tool = MarkupTool.Select;
                await EditAsync(page, () => _doc.AddBoxAsync(page, kind, FromShown(box), StyleFor(kind)));
                break;
            case MarkupTool.Crop:
                if (box.Width < 0.02 || box.Height < 0.02) break;
                Tool = MarkupTool.Select;
                CropRequested?.Invoke(this, (page, FromShown(box)));
                break;
            case MarkupTool.TextBox:
                // A click gives a box of a useful size; a drag sets the width.
                if (box.Width < 0.05) box = new NormRect(box.Left, box.Top, (float)Math.Min(1, box.Left + 0.3), box.Bottom);
                Tool = MarkupTool.Select;
                OpenTextEditor(page, box, null);
                break;
        }
    }

    /// <summary>The two short strokes of an arrowhead at <paramref name="to"/>, drawn as one stroke through the tip.</summary>
    private IReadOnlyList<(double X, double Y)> ArrowHead(int page, (double X, double Y) from, (double X, double Y) to)
    {
        // Angles are right in pixels, not in the page's 0-to-1 units, so work in pixels.
        double w = _w[page], h = _h[page];
        double angle = Math.Atan2((to.Y - from.Y) * h, (to.X - from.X) * w);
        double length = Math.Max(10, StyleFor(ShapeKind.Ink).LineWidth * 4) * _zoom * 96 / 72;
        (double, double) Wing(double turn) =>
            (to.X - Math.Cos(angle + turn) * length / w, to.Y - Math.Sin(angle + turn) * length / h);
        return [Wing(0.45), to, Wing(-0.45)];
    }

    /// <summary>
    /// Places <paramref name="picture"/> on <paramref name="page"/>, centered on (<paramref name="x"/>, <paramref name="y"/>)
    /// in the page as shown (0 to 1), about a fifth of the page wide. It is selected once placed.
    /// </summary>
    public async Task PlaceStampAsync(StampPicture picture, int page, double x, double y)
    {
        double width = 0.22, height = width * _w[page] / _h[page] * picture.Height / picture.Width;
        if (height > 0.12) (width, height) = (width * 0.12 / height, 0.12);
        double left = Math.Clamp(x - width / 2, 0, 1 - width), top = Math.Clamp(y - height / 2, 0, 1 - height);
        var rect = FromShown(new NormRect((float)left, (float)top, (float)(left + width), (float)(top + height)));
        await EditAsync(page, () => _doc.AddStampAsync(page, rect, picture.Width, picture.Height, picture.Bgra));
    }

    private async Task AddNoteAsync(int page, double x, double y, Point at)
    {
        await EditAsync(page, () => _doc.AddNoteAsync(page, x, y, "", StyleFor(ShapeKind.Note).Fill ?? new MarkupColor(255, 210, 0)));
        // Open the new note so it can be typed into; it's the newest on its page.
        if (await _doc.GetShapesAsync(page) is { Count: > 0 } shapes) EditNote(shapes[^1], at, isNew: true);
    }

    /// <summary>Runs one change to a page as one undo step, then redraws the page; new objects come up selected.</summary>
    private async Task EditAsync(int page, Func<Task> change, bool select = true)
    {
        if (BeforeEdit is not null) await BeforeEdit();
        await change();
        if (select) _selectNewestOn = page;
        AfterEdit([page]);
    }

    // ---- Changing ----

    private async Task MoveShapeAsync(Shape shape, NormRect rect)
    {
        await EditAsync(shape.PageIndex, () => _doc.MoveShapeAsync(shape, rect), select: false);
        Selected = shape with { Rect = rect };
    }

    /// <summary>Gives the selected object <paramref name="style"/>; it comes back selected, in front of the others.</summary>
    public async Task RestyleSelectedAsync(ShapeStyle style)
    {
        if (Selected is not { Kind: not ShapeKind.Stamp } shape) return;
        Selected = null; // its place in the page's list changes
        await EditAsync(shape.PageIndex, () => _doc.RestyleShapeAsync(shape, style));
    }

    /// <summary>Deletes the selected object (the Delete key). False when none is selected.</summary>
    public bool DeleteSelectedShape()
    {
        if (Selected is not { } shape || _textEditor is not null) return false;
        Selected = null;
        _ = EditAsync(shape.PageIndex, () => _doc.RemoveShapeAsync(shape), select: false);
        return true;
    }

    /// <summary>Esc: stop placing or drawing, go back to Select, or let go of the selected object. False when there was nothing to cancel.</summary>
    public bool CancelShapeEditing()
    {
        if (PendingStamp is null && Selected is null && _tool == MarkupTool.Select && _textEditor is null) return false;
        if (_textEditor is not null) CloseTextEditor();
        PendingStamp = null;
        Tool = MarkupTool.Select;
        Selected = null;
        RefreshAllOverlays();
        return true;
    }

    // ---- Text: text boxes edit in place, notes in a small popup ----

    private (int Page, NormRect Shown, Shape? Existing)? _editing;

    /// <summary>
    /// Arial loaded from its file. With Arial Narrow installed, a TextBox asked for "Arial" by name draws the narrow
    /// face, as it ignores FontStretch, so typing would wrap differently from the drawn box.
    /// </summary>
    private static readonly string ArialFile =
        "ms-appx:///" + System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf").Replace('\\', '/') + "#Arial, Arial";

    /// <summary>Puts a text field over <paramref name="shown"/> on <paramref name="page"/>; it's saved when it loses focus.</summary>
    private void OpenTextEditor(int page, NormRect shown, Shape? existing)
    {
        CommitTextEditor();
        double k = _zoom * 96 / 72;
        var style = existing?.Style ?? StyleFor(ShapeKind.TextBox);
        static Brush Brush(MarkupColor c) => new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
        // Laid out as the engine draws the box (border, padding, font and size), and opaque so the old drawing doesn't
        // show through. A box with no border gets a thin accent line while typing, so its edges can be seen.
        var (borderBrush, border) = style.Stroke is { } stroke
            ? (Brush(stroke), style.LineWidth * k)
            : ((Brush)new SolidColorBrush(SelectionColor() with { A = 0xFF }), 1.0);
        var background = Brush(style.Fill ?? new MarkupColor(255, 255, 255));
        var foreground = Brush(style.TextColor ?? new MarkupColor(0, 0, 0));
        _textEditor = new TextBox
        {
            Text = existing?.Text ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = Math.Max(1, style.FontSize * k),
            FontFamily = new FontFamily(style.Font switch { TextFont.Times => "Times New Roman", TextFont.Courier => "Courier New", _ => ArialFile }),
            Width = Math.Max(40, shown.Width * _w[page]),
            MinWidth = 0,
            MinHeight = Math.Max(24, shown.Height * _h[page]),
            BorderThickness = new Thickness(border),
            Padding = new Thickness(Math.Max(0, 2 * k - (style.Stroke is null ? border : 0))),
            CornerRadius = new CornerRadius(0),
        };
        // The theme's hover and focused looks would replace these, so set those too.
        foreach (string state in new[] { "", "PointerOver", "Focused" })
        {
            _textEditor.Resources["TextControlBackground" + state] = background;
            _textEditor.Resources["TextControlForeground" + state] = foreground;
            _textEditor.Resources["TextControlBorderBrush" + state] = borderBrush;
        }
        _textEditor.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(border);
        Canvas.SetLeft(_textEditor, _left[page] + shown.Left * _w[page]);
        Canvas.SetTop(_textEditor, _top[page] + shown.Top * _h[page]);
        _editing = (page, shown, existing);
        _textEditor.LostFocus += (_, _) => CommitTextEditor();
        _textEditor.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Escape) return;
            e.Handled = true;
            CommitTextEditor();
        };
        _surface.Children.Add(_textEditor);
        _textEditor.Focus(FocusState.Programmatic);
        _textEditor.SelectAll();
    }

    private void CloseTextEditor()
    {
        if (_textEditor is null) return;
        _surface.Children.Remove(_textEditor);
        _textEditor = null;
        _editing = null;
    }

    /// <summary>Saves the text field: a new box is added, an existing one changes, and an emptied one is deleted.</summary>
    private void CommitTextEditor()
    {
        if (_textEditor is not { } editor || _editing is not { } editing) return;
        string text = editor.Text.TrimEnd();
        double height = editor.ActualHeight / _h[editing.Page];
        CloseTextEditor();
        var shown = editing.Shown;
        var rect = FromShown(new NormRect(shown.Left, shown.Top, shown.Right, (float)Math.Min(1, shown.Top + Math.Max(shown.Height, height))));
        int page = editing.Page;
        if (editing.Existing is { } box)
        {
            if (text == box.Text) return;
            _ = EditAsync(page, () => text.Length == 0 ? _doc.RemoveShapeAsync(box) : _doc.SetShapeTextAsync(box, text), select: false);
        }
        else if (text.Length > 0)
        {
            _ = EditAsync(page, () => _doc.AddTextBoxAsync(page, rect, text, StyleFor(ShapeKind.TextBox)));
        }
    }

    /// <summary>Opens a note's text in a popup next to it; the text is saved when the popup closes. An empty new note is removed.</summary>
    private void EditNote(Shape note, Point at, bool isNew = false)
    {
        var box = new TextBox { Text = note.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = 260, MinHeight = 96, PlaceholderText = "Note" };
        var delete = new Button { Content = "Delete note", HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { box, delete } } };
        bool deleted = false;
        delete.Click += (_, _) =>
        {
            deleted = true;
            flyout.Hide();
        };
        flyout.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        flyout.Closed += (_, _) =>
        {
            string text = box.Text.TrimEnd();
            if (deleted || (isNew && text.Length == 0))
                _ = EditAsync(note.PageIndex, () => _doc.RemoveShapeAsync(note), select: false);
            else if (text != note.Text)
                _ = EditAsync(note.PageIndex, () => _doc.SetShapeTextAsync(note, text), select: false);
        };
        flyout.ShowAt(_surface, new FlyoutShowOptions { Position = at, Placement = FlyoutPlacementMode.RightEdgeAlignedTop });
    }

    // ---- Drawing the selection and the shape being drawn ----

    private void DrawShapeOverlay(PageView view)
    {
        double w = _w[view.Index], h = _h[view.Index];
        if (_drawing is { } drawing && drawing.Page == view.Index && drawing.Points.Count > 1)
            view.Overlay.Children.Add(Preview(drawing, w, h));

        NormRect shown;
        ShapeKind kind;
        if (_drag is { } drag && drag.Shape.PageIndex == view.Index) (shown, kind) = (drag.Now, drag.Shape.Kind);
        else if (Selected is { } shape && shape.PageIndex == view.Index) (shown, kind) = (PageRotation.ToView(shape.Rect, _rotation), shape.Kind);
        else return;

        // As in Preview: dots at the corners and edge middles, and no frame. A note has no handles, so it gets a frame.
        var accent = new SolidColorBrush(SelectionColor() with { A = 0xFF });
        if (_drag is { } moving && moving.Shape.PageIndex == view.Index && moving.Now != moving.From)
            view.Overlay.Children.Add(Ghost(moving, w, h, accent));
        if (kind == ShapeKind.Note)
        {
            var frame = new Rectangle
            {
                Stroke = accent, StrokeThickness = 1.5, StrokeDashArray = [4, 3],
                Width = Math.Max(1, shown.Width * w), Height = Math.Max(1, shown.Height * h),
            };
            Canvas.SetLeft(frame, shown.Left * w);
            Canvas.SetTop(frame, shown.Top * h);
            view.Overlay.Children.Add(frame);
        }
        foreach (var handle in HandlesFor(kind))
        {
            var at = HandlePoint(shown, handle, w, h);
            var dot = new Ellipse { Width = 9, Height = 9, Fill = accent, Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), StrokeThickness = 1.5 };
            Canvas.SetLeft(dot, at.X - 4.5);
            Canvas.SetTop(dot, at.Y - 4.5);
            view.Overlay.Children.Add(dot);
        }
    }

    /// <summary>
    /// The object being moved or resized, drawn at its new place and size so it follows the pointer, as in Preview.
    /// The page keeps drawing the object where it was until the pointer lets go.
    /// </summary>
    private FrameworkElement Ghost(ShapeDrag drag, double w, double h, Brush accent)
    {
        var (shape, from, now) = (drag.Shape, drag.From, drag.Now);
        var s = shape.Style ?? StyleFor(shape.Kind);
        double k = _zoom * 96 / 72;
        static Brush? Paint(MarkupColor? c) => c is { } v ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, v.R, v.G, v.B)) : null;
        var black = new MarkupColor(0, 0, 0);
        FrameworkElement element;
        switch (shape.Kind)
        {
            case ShapeKind.Rectangle or ShapeKind.Ellipse:
                Microsoft.UI.Xaml.Shapes.Shape box = shape.Kind == ShapeKind.Ellipse ? new Ellipse() : new Rectangle();
                box.Stroke = Paint(s.Stroke);
                box.StrokeThickness = s.Stroke is null ? 0 : Math.Max(1, s.LineWidth * k);
                box.Fill = Paint(s.Fill);
                element = box;
                break;
            case ShapeKind.Ink when shape.Ink is { } strokes:
                // Each point keeps its place within the box, as the engine scales ink on a resize.
                (double X, double Y) Map((double X, double Y) p)
                {
                    var (x, y) = PageRotation.ToView(p.X, p.Y, _rotation);
                    return ((x - from.Left) / from.Width * now.Width * w, (y - from.Top) / from.Height * now.Height * h);
                }
                var geometry = new PathGeometry();
                foreach (var points in strokes.Where(p => p.Count > 0).Select(p => p.Select(Map).ToList()))
                {
                    var figure = new PathFigure { StartPoint = new Point(points[0].X, points[0].Y) };
                    foreach (var (c1, c2, end) in InkCurve.Segments(points))
                        figure.Segments.Add(new BezierSegment { Point1 = new(c1.X, c1.Y), Point2 = new(c2.X, c2.Y), Point3 = new(end.X, end.Y) });
                    geometry.Figures.Add(figure);
                }
                element = new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = geometry, Stroke = Paint(s.Stroke ?? black), StrokeThickness = Math.Max(1, s.LineWidth * k),
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                break;
            case ShapeKind.TextBox:
                element = new Border
                {
                    Background = Paint(s.Fill), BorderBrush = Paint(s.Stroke),
                    BorderThickness = new Thickness(s.Stroke is null ? 0 : s.LineWidth * k), Padding = new Thickness(2 * k),
                    Child = new TextBlock
                    {
                        Text = shape.Text, TextWrapping = TextWrapping.Wrap, FontSize = Math.Max(1, s.FontSize * k),
                        FontFamily = new FontFamily(s.Font switch { TextFont.Times => "Times New Roman", TextFont.Courier => "Courier New", _ => "Arial" }),
                        FontStretch = Windows.UI.Text.FontStretch.Normal, Foreground = Paint(s.TextColor ?? black),
                    },
                };
                break;
            default: // pictures and notes: an outline where they go
                element = new Rectangle { Stroke = accent, StrokeThickness = 1.5, StrokeDashArray = [4, 3] };
                break;
        }
        element.Width = Math.Max(1, now.Width * w);
        element.Height = Math.Max(1, now.Height * h);
        Canvas.SetLeft(element, now.Left * w);
        Canvas.SetTop(element, now.Top * h);
        return element;
    }

    /// <summary>What the tool would draw, in the current style, while the pointer is still down.</summary>
    private Microsoft.UI.Xaml.Shapes.Shape Preview(Drawing drawing, double w, double h)
    {
        double k = _zoom * 96 / 72;
        var s = KindOf(drawing.Tool) is { } kind ? StyleFor(kind) : new ShapeStyle(null);
        Brush? stroke = s.Stroke is { } c ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B)) : null;
        Brush? fill = s.Fill is { } f ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, f.R, f.G, f.B)) : null;
        double thickness = s.Stroke is null ? 0 : Math.Max(1, s.LineWidth * k);
        Point Px((double X, double Y) p) => new(p.X * w, p.Y * h);

        switch (drawing.Tool)
        {
            case MarkupTool.Rectangle or MarkupTool.Ellipse or MarkupTool.TextBox or MarkupTool.Crop:
                var box = Between(drawing.Start, drawing.End);
                Microsoft.UI.Xaml.Shapes.Shape shape = drawing.Tool == MarkupTool.Ellipse ? new Ellipse() : new Rectangle();
                bool outline = drawing.Tool is MarkupTool.TextBox or MarkupTool.Crop; // an area, not a shape
                shape.Stroke = outline ? new SolidColorBrush(SelectionColor() with { A = 0xFF }) : stroke;
                shape.StrokeThickness = outline ? 1.5 : thickness;
                shape.Fill = outline ? null : fill;
                if (outline) shape.StrokeDashArray = [4, 3];
                shape.Width = Math.Max(1, box.Width * w);
                shape.Height = Math.Max(1, box.Height * h);
                Canvas.SetLeft(shape, box.Left * w);
                Canvas.SetTop(shape, box.Top * h);
                return shape;
            case MarkupTool.Ink:
                // The same curve the page gets once the pen lifts.
                var figure = new PathFigure { StartPoint = Px(drawing.Start) };
                foreach (var (c1, c2, end) in InkCurve.Segments(drawing.Points))
                    figure.Segments.Add(new BezierSegment { Point1 = Px(c1), Point2 = Px(c2), Point3 = Px(end) });
                return new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = new PathGeometry { Figures = { figure } }, Stroke = stroke, StrokeThickness = thickness,
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
            default:
                var line = new Polyline { Stroke = stroke, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                IEnumerable<(double X, double Y)> points = drawing.Tool == MarkupTool.Ink ? drawing.Points : [drawing.Start, drawing.End];
                foreach (var p in points) line.Points.Add(Px(p));
                if (drawing.Tool == MarkupTool.Arrow)
                    foreach (var p in ArrowHead(drawing.Page, drawing.Start, drawing.End).Prepend(drawing.End)) line.Points.Add(Px(p));
                return line;
        }
    }
}
