using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Monoview.Engine;
using Windows.Foundation;
using PageRotation = Monoview.Engine.Rotation;

namespace Monoview.App.Viewer;

/// <summary>A picture to place on a page, such as a signature: straight-alpha BGRA pixels.</summary>
public sealed record StampPicture(int Width, int Height, byte[] Bgra);

/// <summary>Signatures and other stamps: placing one with a click, then selecting, moving, resizing and deleting it.</summary>
public sealed partial class PdfViewer
{
    private const double HandleSize = 10;

    private readonly LruCache<int, IReadOnlyList<Stamp>> _stamps = new(48);
    private readonly HashSet<int> _stampsLoading = new();
    private readonly InputCursor _placeCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    private readonly InputCursor _moveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private readonly InputCursor _resizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
    private Stamp? _selectedStamp;
    private StampDrag? _stampDrag;
    private int? _selectNewestStampOn;

    /// <summary>A stamp being dragged: where the pointer went down, the rect it started at and where it is now, as shown.</summary>
    private sealed record StampDrag(Stamp Stamp, bool Resize, (double X, double Y) Start, NormRect From)
    {
        public NormRect Now { get; set; } = From;
    }

    /// <summary>The picture the next click on a page places, such as a signature; null when not placing.</summary>
    public StampPicture? PendingStamp { get; set; }

    private void EnsureStamps(int index)
    {
        if (_stamps.TryGet(index, out _) || !_stampsLoading.Add(index)) return;
        _ = LoadStampsAsync(index);
    }

    private async Task LoadStampsAsync(int index)
    {
        try
        {
            var doc = _doc;
            var stamps = await doc.GetStampsAsync(index);
            if (doc != _doc) return;
            _stamps.Add(index, stamps);
            if (_selectNewestStampOn == index && stamps.Count > 0)
            {
                _selectNewestStampOn = null;
                _selectedStamp = stamps[^1];
                if (_views.TryGetValue(index, out var view)) RefreshOverlay(view);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or PdfException)
        {
        }
        finally
        {
            _stampsLoading.Remove(index);
        }
    }

    // ---- Coordinates: stamps are kept unrotated; dragging works in the page as shown ----

    private (double X, double Y) ToShownCoords(int page, Point p) => ((p.X - _left[page]) / _w[page], (p.Y - _top[page]) / _h[page]);

    private NormRect FromShown(NormRect shown)
    {
        var (x1, y1) = PageRotation.FromView(shown.Left, shown.Top, _rotation);
        var (x2, y2) = PageRotation.FromView(shown.Right, shown.Bottom, _rotation);
        return new NormRect((float)Math.Min(x1, x2), (float)Math.Min(y1, y2), (float)Math.Max(x1, x2), (float)Math.Max(y1, y2));
    }

    private Stamp? StampAt(Point p)
    {
        if (!_laidOut || PageCount == 0) return null;
        int page = PageAtPoint(p);
        if (!_stamps.TryGet(page, out var stamps) || stamps.Count == 0) return null;
        var (x, y) = ToPageCoords(page, p);
        return stamps.LastOrDefault(s => s.Rect.Contains(x, y));
    }

    private bool OnResizeHandle(Stamp stamp, Point p)
    {
        var shown = PageRotation.ToView(stamp.Rect, _rotation);
        int page = stamp.PageIndex;
        double right = _left[page] + shown.Right * _w[page], bottom = _top[page] + shown.Bottom * _h[page];
        return Math.Abs(p.X - right) <= HandleSize && Math.Abs(p.Y - bottom) <= HandleSize;
    }

    // ---- Pointer hooks, called first by the viewer's pointer handlers; true when they took the event ----

    private bool HandleStampPress(PointerRoutedEventArgs e, Point p)
    {
        if (PendingStamp is { } picture)
        {
            PendingStamp = null;
            if (_laidOut && PageCount > 0)
            {
                int page = PageAtPoint(p);
                var (x, y) = ToShownCoords(page, p);
                _ = PlaceStampAsync(picture, page, x, y);
            }
            return true;
        }
        var hit = _selectedStamp is { } selected && OnResizeHandle(selected, p) ? selected : StampAt(p);
        if (hit is null)
        {
            if (_selectedStamp is null) return false;
            _selectedStamp = null;
            RefreshAllOverlays();
            return false;
        }
        _selectedStamp = hit;
        var shown = PageRotation.ToView(hit.Rect, _rotation);
        _stampDrag = new StampDrag(hit, OnResizeHandle(hit, p), ToShownCoords(hit.PageIndex, p), shown);
        _surface.CapturePointer(e.Pointer);
        _scroller.Focus(Microsoft.UI.Xaml.FocusState.Pointer);
        RefreshAllOverlays();
        return true;
    }

    private bool HandleStampMove(Point p)
    {
        if (_stampDrag is { } drag)
        {
            int page = drag.Stamp.PageIndex;
            var (x, y) = ToShownCoords(page, p);
            double dx = x - drag.Start.X, dy = y - drag.Start.Y;
            var from = drag.From;
            if (drag.Resize)
            {
                // Keep the picture's shape: the corner follows the pointer along the width.
                double width = Math.Clamp(from.Width + dx, 0.02, 1 - from.Left);
                double height = width * from.Height / from.Width;
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

        InputCursor? cursor = PendingStamp is not null ? _placeCursor
            : _selectedStamp is { } selected && OnResizeHandle(selected, p) ? _resizeCursor
            : StampAt(p) is not null ? _moveCursor
            : null;
        if (cursor is null) return false;
        ProtectedCursor = cursor;
        _textCursorShown = false;
        _overLink = true; // so leaving the stamp puts the usual cursor back
        return true;
    }

    private bool HandleStampRelease(PointerRoutedEventArgs e)
    {
        if (_stampDrag is not { } drag) return false;
        _stampDrag = null;
        _surface.ReleasePointerCapture(e.Pointer);
        if (drag.Now != drag.From)
            _ = MoveStampAsync(drag.Stamp, FromShown(drag.Now));
        return true;
    }

    // ---- Edits, each one undoable ----

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
        if (BeforeEdit is not null) await BeforeEdit();
        await _doc.AddStampAsync(page, rect, picture.Width, picture.Height, picture.Bgra);
        _selectNewestStampOn = page;
        AfterEdit([page]);
    }

    private async Task MoveStampAsync(Stamp stamp, NormRect rect)
    {
        if (BeforeEdit is not null) await BeforeEdit();
        await _doc.MoveStampAsync(stamp, rect);
        _selectedStamp = stamp with { Rect = rect };
        AfterEdit([stamp.PageIndex]);
    }

    /// <summary>Deletes the selected signature or stamp (the Delete key). False when none is selected.</summary>
    public bool DeleteSelectedStamp()
    {
        if (_selectedStamp is not { } stamp) return false;
        _selectedStamp = null;
        _ = DeleteStampAsync(stamp);
        return true;
    }

    private async Task DeleteStampAsync(Stamp stamp)
    {
        if (BeforeEdit is not null) await BeforeEdit();
        await _doc.RemoveStampAsync(stamp);
        AfterEdit([stamp.PageIndex]);
    }

    /// <summary>Esc: stop placing, or let go of the selected stamp. False when there was nothing to cancel.</summary>
    public bool CancelStamp()
    {
        if (PendingStamp is null && _selectedStamp is null) return false;
        PendingStamp = null;
        _selectedStamp = null;
        RefreshAllOverlays();
        return true;
    }

    /// <summary>Outlines the selected stamp, or where a dragged one would go, with a resize handle at its corner.</summary>
    private void DrawStampOverlay(PageView view)
    {
        NormRect shown;
        if (_stampDrag is { } drag && drag.Stamp.PageIndex == view.Index) shown = drag.Now;
        else if (_selectedStamp is { } stamp && stamp.PageIndex == view.Index) shown = PageRotation.ToView(stamp.Rect, _rotation);
        else return;

        double w = _w[view.Index], h = _h[view.Index];
        var accent = new SolidColorBrush(SelectionColor() with { A = 0xFF });
        var frame = new Rectangle
        {
            Stroke = accent, StrokeThickness = 1.5, StrokeDashArray = [4, 3],
            Width = Math.Max(1, shown.Width * w), Height = Math.Max(1, shown.Height * h),
        };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(frame, shown.Left * w);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(frame, shown.Top * h);
        view.Overlay.Children.Add(frame);
        var handle = new Rectangle { Fill = accent, Width = HandleSize, Height = HandleSize, RadiusX = 2, RadiusY = 2 };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(handle, shown.Right * w - HandleSize / 2);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(handle, shown.Bottom * h - HandleSize / 2);
        view.Overlay.Children.Add(handle);
    }
}
