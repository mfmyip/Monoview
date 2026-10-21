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
    private readonly InputCursor _resizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
    private ShapeDrag? _drag;
    private Drawing? _drawing;
    private int? _selectNewestOn;
    private TextBox? _textEditor;
    private MarkupTool _tool;

    /// <summary>A shape being dragged: where the pointer went down, the rect it started at and where it is now, as shown.</summary>
    private sealed record ShapeDrag(Shape Shape, bool Resize, (double X, double Y) Start, NormRect From)
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

    private bool OnResizeHandle(Shape shape, Point p)
    {
        if (shape.Kind == ShapeKind.Note) return false; // notes keep their icon size
        var shown = PageRotation.ToView(shape.Rect, _rotation);
        int page = shape.PageIndex;
        double right = _left[page] + shown.Right * _w[page], bottom = _top[page] + shown.Bottom * _h[page];
        return Math.Abs(p.X - right) <= HandleSize && Math.Abs(p.Y - bottom) <= HandleSize;
    }

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

        var hit = Selected is { } selected && OnResizeHandle(selected, p) ? selected : ShapeAt(p);
        if (hit is null)
        {
            if (Selected is null) return false;
            Selected = null;
            RefreshAllOverlays();
            return false;
        }
        Selected = hit;
        var shown = PageRotation.ToView(hit.Rect, _rotation);
        _drag = new ShapeDrag(hit, OnResizeHandle(hit, p), ToShownCoords(hit.PageIndex, p), shown);
        _surface.CapturePointer(e.Pointer);
        _scroller.Focus(FocusState.Pointer);
        RefreshAllOverlays();
        return true;
    }

    private bool HandleShapeMove(Point p)
    {
        if (_drawing is { } drawing)
        {
            var point = ToShownCoords(drawing.Page, p);
            if (drawing.Tool == MarkupTool.Ink) drawing.Points.Add(point);
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
            if (drag.Resize)
            {
                double width = Math.Clamp(from.Width + dx, 0.01, 1 - from.Left);
                // Pictures keep their shape; the corner of anything else follows the pointer freely.
                double height = drag.Shape.Kind == ShapeKind.Stamp ? width * from.Height / from.Width : Math.Clamp(from.Height + dy, 0.005, 1 - from.Top);
                drag.Now = new NormRect(from.Left, from.Top, (float)(from.Left + width), (float)Math.Min(1, from.Top + height));
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
            : Selected is { } selected && OnResizeHandle(selected, p) ? _resizeCursor
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

    /// <summary>Puts a text field over <paramref name="shown"/> on <paramref name="page"/>; it's saved when it loses focus.</summary>
    private void OpenTextEditor(int page, NormRect shown, Shape? existing)
    {
        CommitTextEditor();
        double k = _zoom * 96 / 72;
        var style = existing?.Style ?? StyleFor(ShapeKind.TextBox);
        static Brush Brush(MarkupColor c) => new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
        double border = style.Stroke is null ? 0 : style.LineWidth * k;
        _textEditor = new TextBox
        {
            Text = existing?.Text ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = Math.Max(8, style.FontSize * k),
            FontFamily = new FontFamily(style.Font switch { TextFont.Times => "Times New Roman", TextFont.Courier => "Courier New", _ => "Arial" }),
            Foreground = Brush(style.TextColor ?? new MarkupColor(0, 0, 0)),
            Background = style.Fill is { } fill ? Brush(fill) : new SolidColorBrush(Windows.UI.Color.FromArgb(0x20, 0, 120, 215)),
            BorderBrush = style.Stroke is { } stroke ? Brush(stroke) : null,
            BorderThickness = new Thickness(border),
            Width = Math.Max(40, shown.Width * _w[page]),
            MinHeight = Math.Max(24, shown.Height * _h[page]),
            Padding = new Thickness(2 * k),
        };
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
        bool resizable;
        if (_drag is { } drag && drag.Shape.PageIndex == view.Index) (shown, resizable) = (drag.Now, drag.Shape.Kind != ShapeKind.Note);
        else if (Selected is { } shape && shape.PageIndex == view.Index) (shown, resizable) = (PageRotation.ToView(shape.Rect, _rotation), shape.Kind != ShapeKind.Note);
        else return;

        var accent = new SolidColorBrush(SelectionColor() with { A = 0xFF });
        var frame = new Rectangle
        {
            Stroke = accent, StrokeThickness = 1.5, StrokeDashArray = [4, 3],
            Width = Math.Max(1, shown.Width * w), Height = Math.Max(1, shown.Height * h),
        };
        Canvas.SetLeft(frame, shown.Left * w);
        Canvas.SetTop(frame, shown.Top * h);
        view.Overlay.Children.Add(frame);
        if (!resizable) return;
        var handle = new Rectangle { Fill = accent, Width = HandleSize, Height = HandleSize, RadiusX = 2, RadiusY = 2 };
        Canvas.SetLeft(handle, shown.Right * w - HandleSize / 2);
        Canvas.SetTop(handle, shown.Bottom * h - HandleSize / 2);
        view.Overlay.Children.Add(handle);
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
