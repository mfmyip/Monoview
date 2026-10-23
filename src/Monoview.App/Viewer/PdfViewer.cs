using Monoview.Engine;
// UIElement has its own Rotation property, which hides the engine's Rotation class in here.
using PageRotation = Monoview.Engine.Rotation;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace Monoview.App.Viewer;

public enum FitMode { FitWidth, FitPage, Custom }

/// <summary>What Reload carries over to the reopened document.</summary>
/// <summary>How a document is shown. With an offset, restoring returns to that exact scroll position, not the page top.</summary>
public sealed record ViewState(int Page, double Zoom, FitMode FitMode, int Rotation, bool SinglePage, bool TwoPage = false,
    double? OffsetX = null, double? OffsetY = null);

/// <summary>
/// Continuous-scroll page view. Pages are laid out in a column on a canvas inside a
/// ScrollViewer; only pages near the viewport get a view, and only tiles inside the viewport
/// are rendered sharp. Pinch / Ctrl+wheel zoom uses the ScrollViewer's own (bitmap) zoom while
/// the gesture runs, then "bakes" it: re-lays out at the new size and re-renders sharp.
/// </summary>
public sealed partial class PdfViewer : UserControl
{
    public const double MinZoom = 0.1, MaxZoom = 8;
    private const double PageMargin = 24, PageGap = 16;
    private const int TileSize = 512;
    private static readonly double[] ZoomSteps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6, 8];

    private PdfDocument _doc;
    private readonly PreviewCache _previews;
    private readonly ScrollViewer _scroller;
    private readonly Canvas _surface;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _settleTimer;
    private readonly Dictionary<int, PageView> _views = new();
    private readonly Stack<PageView> _pool = new();
    private readonly LruCache<TileKey, WriteableBitmap> _tiles = new(64);
    private readonly LruCache<int, PageText> _texts = new(48);
    private readonly HashSet<int> _textsLoading = new();
    private readonly InputCursor _arrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private readonly InputCursor _textCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);

    private readonly double[] _top, _left, _w, _h;
    private double _zoom = 1;
    private Point? _pendingOffset;
    private bool _laidOut;
    private bool _firstPageReported;
    private bool _holdZoomWhileResizing;
    private int _rotation;
    private bool _singlePage;
    private bool _twoPage;

    private TextPos? _anchor, _focus;
    private bool _selecting;
    private bool _textCursorShown;
    private bool _overLink;

    private Dictionary<int, List<SearchHit>> _hits = new();
    private SearchHit? _currentHit;

    private Brush? _selectionBrush;
    private static readonly Brush HitBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x70, 0xFF, 0xD4, 0x00));
    private static readonly Brush CurrentHitBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xA0, 0xFF, 0x8C, 0x00));

    public event EventHandler? CurrentPageChanged;
    public event EventHandler? ZoomChanged;
    /// <summary>Raised once, when the first visible page is fully sharp. Used by the startup benchmark.</summary>
    public event EventHandler? FirstPageRendered;

    public PdfViewer(PdfDocument doc, PreviewCache previews)
    {
        _doc = doc;
        _previews = previews;
        int n = doc.PageCount;
        _top = new double[n];
        _left = new double[n];
        _w = new double[n];
        _h = new double[n];

        _surface = new Canvas { Background = new SolidColorBrush(Colors.Transparent) };
        _scroller = new ScrollViewer
        {
            Content = _surface,
            ZoomMode = ZoomMode.Enabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsTabStop = true,
        };
        // The overlay layer sits above the scrolling content (autoscroll anchor icon).
        Content = new Grid { Children = { _scroller, _overlayLayer } };
        InitAutoScroll();

        _scroller.ViewChanged += OnViewChanged;
        _scroller.SizeChanged += OnSizeChanged;
        _surface.PointerWheelChanged += OnPointerWheelChanged;
        _surface.PointerPressed += OnPointerPressed;
        _surface.PointerMoved += OnPointerMoved;
        _surface.PointerReleased += OnPointerReleased;
        _surface.PointerExited += (_, _) => HideCommentTip();
        _surface.PointerCaptureLost += (_, _) =>
        {
            _selecting = false;
            OnAutoScrollCaptureLost();
        };
        _surface.DoubleTapped += OnDoubleTapped;
        PreviewKeyDown += OnPreviewKeyDown;
        CharacterReceived += OnCharacterReceived;
        AttachForm(doc);
        _textMenu = BuildContextMenu();
        _surface.ContextRequested += OnContextRequested;

        _settleTimer = DispatcherQueue.CreateTimer();
        _settleTimer.Interval = TimeSpan.FromMilliseconds(120);
        _settleTimer.IsRepeating = false;
        _settleTimer.Tick += (_, _) => UpdateViewport(requestTiles: true);

        Loaded += (_, _) =>
        {
            if (XamlRoot is not null)
                XamlRoot.Changed += (_, _) => { if (_laidOut) ApplyZoom(_zoom, null, requestTiles: true); };
        };
    }

    public PdfDocument Document => _doc;
    public int PageCount => _doc.PageCount;
    public int CurrentPage { get; private set; }
    /// <summary>Starts as the "Zoom for newly opened PDFs" setting; Actual size is a custom zoom of 1.</summary>
    public FitMode FitMode { get; private set; } = AppSettings.Current.DefaultZoom switch
    {
        "FitPage" => FitMode.FitPage,
        "ActualSize" => FitMode.Custom,
        _ => FitMode.FitWidth,
    };
    /// <summary>Effective zoom; 1 = actual size (72 points per inch shown as 96 DIPs per inch).</summary>
    public double Zoom => _zoom * _scroller.ZoomFactor;
    public bool HasSelection => _anchor is { } a && _focus is { } f && a != f;
    /// <summary>View rotation in quarter turns clockwise (not saved to the file).</summary>
    public int ViewRotation => _rotation;
    /// <summary>One page at a time instead of a continuous column.</summary>
    public bool SinglePage => _singlePage;

    /// <summary>State to restore on first layout (Reload keeps page, zoom, rotation and layout).</summary>
    public ViewState? InitialState { get; set; }

    public ViewState CaptureState() => new(CurrentPage, Zoom, FitMode, _rotation, _singlePage, _twoPage, _scroller.HorizontalOffset, _scroller.VerticalOffset);

    /// <summary>Pages side by side in pairs, 1 and 2, then 3 and 4, scrolling continuously.</summary>
    public bool TwoPage => _twoPage;

    public void SetTwoPage(bool twoPage)
    {
        if (twoPage == _twoPage) return;
        int page = CurrentPage;
        _twoPage = twoPage;
        if (twoPage) _singlePage = false;
        ResetViews();
        ApplyZoom(FitMode == FitMode.Custom ? _zoom : ComputeFitZoom(), null, requestTiles: true);
        GoToPage(page);
        LayoutModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private double RasterScale => XamlRoot?.RasterizationScale ?? 1.0;

    /// <summary>Page size in points as shown, with the view rotation applied.</summary>
    private PageSize ShownSize(int index) => PageRotation.Apply(_doc.PageSizes[index], _rotation);

    // ---------------- Rotation, single page, interactive resize ----------------

    public void Rotate(int quarterTurns)
    {
        int page = CurrentPage;
        _rotation = PageRotation.Normalize(_rotation + quarterTurns);
        _previews.Rotation = _rotation;
        ResetViews();
        ApplyZoom(FitMode == FitMode.Custom ? _zoom : ComputeFitZoom(), null, requestTiles: true);
        GoToPage(page);
        RotationChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSinglePage(bool single)
    {
        if (single == _singlePage) return;
        int page = CurrentPage;
        _singlePage = single;
        if (single) _twoPage = false;
        ResetViews();
        ApplyZoom(FitMode == FitMode.Custom ? _zoom : ComputeFitZoom(), null, requestTiles: true);
        GoToPage(page);
        LayoutModeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// While the sidebar is being dragged, keep the zoom and only re-center pages; re-fit once at
    /// the end. Re-fitting on every pixel of the drag made fit-width feel choppy.
    /// </summary>
    public void BeginInteractiveResize() => _holdZoomWhileResizing = true;

    public void EndInteractiveResize()
    {
        if (!_holdZoomWhileResizing) return;
        _holdZoomWhileResizing = false;
        if (_laidOut && FitMode != FitMode.Custom)
            ApplyZoom(ComputeFitZoom(), TopCenterAnchor(), requestTiles: true);
    }

    public event EventHandler? RotationChanged;
    public event EventHandler? LayoutModeChanged;

    private void ResetViews()
    {
        foreach (var view in _views.Values)
        {
            view.Unbind();
            _pool.Push(view);
        }
        _views.Clear();
        _tiles.Clear();
    }

    // ---------------- Zoom & navigation ----------------

    public void SetFitMode(FitMode mode)
    {
        FitMode = mode;
        if (mode != FitMode.Custom && _laidOut)
            ApplyZoom(ComputeFitZoom(), TopCenterAnchor(), requestTiles: true);
    }

    public void SetZoom(double zoom)
    {
        FitMode = FitMode.Custom;
        ApplyZoom(zoom, null, requestTiles: true);
    }

    public void ZoomIn() => SetZoom(ZoomSteps.FirstOrDefault(z => z > Zoom * 1.01, MaxZoom));
    public void ZoomOut() => SetZoom(ZoomSteps.LastOrDefault(z => z < Zoom / 1.01, MinZoom));

    /// <param name="normalizedY">Point on the page to bring near the top (unrotated page coordinates, 0..1).</param>
    public void GoToPage(int index, double? normalizedY = null, bool scrollToBottom = false)
    {
        if (!_laidOut || PageCount == 0) return;
        index = Math.Clamp(index, 0, PageCount - 1);
        if (_singlePage && index != CurrentPage)
        {
            CurrentPage = index;
            Layout();
            _scroller.UpdateLayout();
            UpdateViewport(requestTiles: true);
            CurrentPageChanged?.Invoke(this, EventArgs.Empty);
        }
        double y = normalizedY is { } ny
            ? _top[index] + PageRotation.ToView(0.5, ny, _rotation).Y * _h[index] - 8
            : _top[index] - PageMargin / 2;
        if (scrollToBottom) y = _surface.Height;
        _pendingOffset = null;
        _scroller.ChangeView(null, Math.Max(0, y) * _scroller.ZoomFactor, null, true);
    }

    private void FlipPage(int step)
    {
        int target = CurrentPage + step;
        if (target < 0 || target >= PageCount) return;
        // Going back lands at the bottom of the previous page, so reading backwards is continuous.
        GoToPage(target, scrollToBottom: step < 0);
    }

    private double ComputeFitZoom()
    {
        const double k = 96.0 / 72.0;
        double vw = Math.Max(50, _scroller.ActualWidth - 2 * PageMargin);
        double vh = Math.Max(50, _scroller.ActualHeight - 2 * PageMargin);
        if (_twoPage && PageCount > 0)
        {
            // Fit a pair: the gap between the two pages stays fixed in DIPs.
            double rowWidth = Enumerable.Range(0, (PageCount + 1) / 2).Max(r => RowWidthPoints(r * 2));
            double fitW = (vw - PageGap) / (rowWidth * k);
            if (FitMode == FitMode.FitWidth) return Math.Clamp(fitW, MinZoom, MaxZoom);
            int first = CurrentPage - CurrentPage % 2;
            double rowHeight = Math.Max(ShownSize(first).Height, first + 1 < PageCount ? ShownSize(first + 1).Height : 0);
            return Math.Clamp(Math.Min((vw - PageGap) / (RowWidthPoints(first) * k), vh / (rowHeight * k)), MinZoom, MaxZoom);
        }

        double maxWidth = PageCount == 0 ? 612
            : _singlePage ? ShownSize(CurrentPage).Width
            : Enumerable.Range(0, PageCount).Max(i => ShownSize(i).Width);
        if (FitMode == FitMode.FitWidth)
            return Math.Clamp(vw / (maxWidth * k), MinZoom, MaxZoom);

        var size = PageCount > 0 ? ShownSize(CurrentPage) : new PageSize(612, 792);
        return Math.Clamp(Math.Min(vw / (size.Width * k), vh / (size.Height * k)), MinZoom, MaxZoom);
    }

    /// <summary>Width in points of the pair starting at <paramref name="first"/> (without the gap).</summary>
    private double RowWidthPoints(int first) =>
        ShownSize(first).Width + (first + 1 < PageCount ? ShownSize(first + 1).Width : 0);

    private Point TopCenterAnchor() => new(_scroller.ViewportWidth / 2, 0);

    /// <summary>Re-lays out at <paramref name="zoom"/>, keeping the content under the anchor (viewport coordinates) in place.</summary>
    private void ApplyZoom(double zoom, Point? anchor, bool requestTiles)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        float zf = _scroller.ZoomFactor;
        var a = anchor ?? new Point(_scroller.ViewportWidth / 2, _scroller.ViewportHeight / 2);

        bool hadLayout = _laidOut && PageCount > 0;
        int page = 0;
        double fx = 0.5, fy = 0;
        // ChangeView applies on the next layout tick, so rapid wheel notches use the offset we
        // last asked for rather than the one the ScrollViewer still reports.
        double h = _pendingOffset?.X ?? _scroller.HorizontalOffset;
        double v = _pendingOffset?.Y ?? _scroller.VerticalOffset;
        if (hadLayout)
            (page, fx, fy) = ContentToPage((h + a.X) / zf, (v + a.Y) / zf);

        _zoom = zoom;
        Layout();
        _laidOut = true;

        _scroller.UpdateLayout();
        if (hadLayout)
        {
            double cx = _left[page] + fx * _w[page];
            double cy = _top[page] + fy * _h[page];
            var target = new Point(
                Math.Clamp(cx - a.X, 0, Math.Max(0, _surface.Width - _scroller.ViewportWidth)),
                Math.Clamp(cy - a.Y, 0, Math.Max(0, _surface.Height - _scroller.ViewportHeight)));
            _pendingOffset = target;
            _scroller.ChangeView(target.X, target.Y, 1f, true);
        }
        else
        {
            _scroller.ChangeView(0, 0, 1f, true);
        }

        UpdateViewport(requestTiles);
        if (!requestTiles) RestartSettleTimer();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Layout()
    {
        double k = _zoom * 96.0 / 72.0;
        double s = RasterScale;
        int n = PageCount;
        double maxW = 0;
        for (int i = 0; i < n; i++)
        {
            var size = ShownSize(i);
            _w[i] = size.Width * k;
            _h[i] = size.Height * k;
            if (!_singlePage || i == CurrentPage) maxW = Math.Max(maxW, _w[i]);
        }

        // A lone page shorter than the window sits in the middle, like Preview.
        double y = n == 1 ? Math.Max(PageMargin, (_scroller.ActualHeight - _h[0]) / 2) : PageMargin;
        double contentW;
        if (_twoPage)
        {
            // Pairs side by side, top-aligned: both pages of a row share one top.
            double maxRow = 0;
            for (int i = 0; i < n; i += 2)
                maxRow = Math.Max(maxRow, _w[i] + (i + 1 < n ? PageGap + _w[i + 1] : 0));
            contentW = Math.Max(_scroller.ActualWidth, maxRow + 2 * PageMargin);
            for (int i = 0; i < n; i += 2)
            {
                bool pair = i + 1 < n;
                double rowW = _w[i] + (pair ? PageGap + _w[i + 1] : 0);
                double left = (contentW - rowW) / 2;
                double top = Math.Round(y * s) / s;
                _left[i] = Math.Round(left * s) / s;
                _top[i] = top;
                if (pair)
                {
                    _left[i + 1] = Math.Round((left + _w[i] + PageGap) * s) / s;
                    _top[i + 1] = top;
                }
                y += Math.Max(_h[i], pair ? _h[i + 1] : 0) + PageGap;
            }
        }
        else
        {
            contentW = Math.Max(_scroller.ActualWidth, maxW + 2 * PageMargin);
            for (int i = 0; i < n; i++)
            {
                // Snap to device pixels so tiles line up without seams.
                _left[i] = Math.Round((contentW - _w[i]) / 2 * s) / s;
                _top[i] = Math.Round(y * s) / s;
                // Single page: every page sits in the same slot; only the current one is shown.
                if (!_singlePage) y += _h[i] + PageGap;
            }
        }
        double contentH = _singlePage && n > 0
            ? Math.Max(_h[CurrentPage] + 2 * PageMargin, _scroller.ActualHeight)
            : Math.Max(y - PageGap + PageMargin, _scroller.ActualHeight);

        _surface.Width = contentW;
        _surface.Height = contentH;
        // ScrollViewer rejects factors below 0.1; a pinch that hits the limit just bakes and can continue.
        _scroller.MinZoomFactor = (float)Math.Clamp(MinZoom / _zoom, 0.1, 1);
        _scroller.MaxZoomFactor = (float)Math.Max(1, MaxZoom / _zoom);

        foreach (var view in _views.Values)
            Place(view);
    }

    private void Place(PageView view)
    {
        int i = view.Index;
        Canvas.SetLeft(view, _left[i]);
        Canvas.SetTop(view, _top[i]);
        if (Math.Abs(view.Width - _w[i]) > 0.01 || double.IsNaN(view.Width))
        {
            view.MarkTilesStale(_w[i]);
            view.Width = _w[i];
            view.Height = _h[i];
            RefreshOverlay(view);
        }
    }

    private (int Page, double Fx, double Fy) ContentToPage(double cx, double cy)
    {
        int p = PageAtPoint(new Point(cx, cy));
        return (p, (cx - _left[p]) / _w[p], (cy - _top[p]) / _h[p]);
    }

    /// <summary>
    /// First page of the last row whose top is at or above <paramref name="y"/> (content DIPs).
    /// In two-page view that's the left page of the row.
    /// </summary>
    private int PageAtY(double y)
    {
        if (_singlePage) return CurrentPage;
        int lo = 0, hi = PageCount - 1, result = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_top[mid] <= y) { result = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        while (result > 0 && _top[result - 1] == _top[result]) result--;
        return result;
    }

    /// <summary>The page under a point; in two-page view the right page when the point is right of the gap's middle.</summary>
    private int PageAtPoint(Point p)
    {
        int page = PageAtY(p.Y);
        if (_twoPage && page + 1 < PageCount && _top[page + 1] == _top[page] && p.X >= _left[page + 1] - PageGap / 2)
            page++;
        return page;
    }

    /// <summary>First page of the next row, or PageCount.</summary>
    private int NextRowStart(int page)
    {
        int next = page + 1;
        while (next < PageCount && _top[next] == _top[page]) next++;
        return next;
    }

    // ---------------- Viewport & rendering ----------------

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_scroller.ActualWidth <= 0 || PageCount == 0) return;
        if (!_laidOut)
        {
            StartupTrace.Mark("first_layout");
            double zoom = 1;
            if (InitialState is { } state)
            {
                _rotation = PageRotation.Normalize(state.Rotation);
                _previews.Rotation = _rotation;
                _singlePage = state.SinglePage;
                _twoPage = state.TwoPage && !state.SinglePage;
                FitMode = state.FitMode;
                zoom = state.Zoom;
                CurrentPage = Math.Clamp(state.Page, 0, PageCount - 1);
            }
            else if (PageCount == 1)
            {
                // Like Preview: a single page, such as a picture, shows at actual size, shrunk only to fit the window.
                FitMode = FitMode.FitPage;
                if (ComputeFitZoom() >= 1)
                    FitMode = FitMode.Custom;
            }
            // With a position to restore, render nothing sharp at the top first: that would draw page 1, report
            // it as the first page, and only then jump. Tiles come once the scroll below lands.
            ApplyZoom(FitMode == FitMode.Custom ? zoom : ComputeFitZoom(), null, requestTiles: InitialState is null);
            if (InitialState is { } restored)
            {
                // The ScrollViewer ignores ChangeView during its first layout pass; scroll on the next tick.
                int page = Math.Clamp(restored.Page, 0, PageCount - 1);
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (restored.OffsetY is { } y)
                    {
                        _pendingOffset = null;
                        _scroller.ChangeView(restored.OffsetX, y, null, true);
                    }
                    else
                        GoToPage(page);
                    RestartSettleTimer(); // draws wherever the scroll landed, even if it didn't move

                });
            }
            InitialState = null;
        }
        else if (_holdZoomWhileResizing)
        {
            // Dragging the sidebar: just re-center at the same zoom (cheap); re-fit when the drag ends.
            Layout();
            UpdateViewport(requestTiles: false);
            RestartSettleTimer();
        }
        else if (FitMode != FitMode.Custom)
            ApplyZoom(ComputeFitZoom(), TopCenterAnchor(), requestTiles: false);
        else
        {
            Layout();
            UpdateViewport(requestTiles: false);
            RestartSettleTimer();
        }
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        HideCommentTip(); // the hover outline is drawn over the view, so it would stay put while the pages move
        if (!e.IsIntermediate) _pendingOffset = null;
        float zf = _scroller.ZoomFactor;
        if (!e.IsIntermediate && Math.Abs(zf - 1) > 0.001)
        {
            // A pinch or Ctrl+wheel zoom finished: bake it into the layout and render sharp.
            FitMode = FitMode.Custom;
            ApplyZoom(_zoom * zf, null, requestTiles: true);
            return;
        }
        bool zooming = Math.Abs(zf - 1) > 0.001;
        UpdateViewport(requestTiles: !zooming);
        if (zooming) ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ctrl+wheel zooms like Acrobat: each notch is an immediate step (20% by default, see Settings) around the mouse
    /// pointer, with no animation. Smooth-scrolling touchpads send smaller deltas and get
    /// proportionally smaller steps. Touchpad pinch still goes through the ScrollViewer.
    /// </summary>
    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        StopAutoScroll(); // the wheel takes over from autoscroll
        if (!_laidOut) return;
        var point = e.GetCurrentPoint(_scroller);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0 || point.Properties.IsHorizontalMouseWheel) return;

        if ((e.KeyModifiers & VirtualKeyModifiers.Control) == 0)
        {
            // Single page: scrolling past the bottom or top flips to the next or previous page.
            if (_singlePage && (delta < 0 ? AtBottom : AtTop))
            {
                FlipPage(delta < 0 ? +1 : -1);
                e.Handled = true;
            }
            return;
        }
        e.Handled = true; // stop the ScrollViewer's own animated zoom
        WheelZoom(delta, point.Position);
    }

    /// <summary>One Ctrl+wheel zoom step of <paramref name="delta"/> (120 per notch) around a viewport point.</summary>
    public void WheelZoom(int delta, Point anchor)
    {
        FitMode = FitMode.Custom;
        double zoom = Zoom * Math.Pow(1 + AppSettings.Current.WheelZoomStep, delta / 120.0);
        // Make 100% easy to land on, as Acrobat's zoom steps do.
        if (Math.Abs(zoom - 1) < 0.04) zoom = 1;
        ApplyZoom(zoom, anchor, requestTiles: false);
    }

    /// <summary>Dev check: page coordinates (page, x, y fractions) under a viewport point.</summary>
    public (int Page, double Fx, double Fy) PageAtViewportPoint(Point p)
    {
        float zf = _scroller.ZoomFactor;
        return ContentToPage((_scroller.HorizontalOffset + p.X) / zf, (_scroller.VerticalOffset + p.Y) / zf);
    }

    private bool AtTop => _scroller.VerticalOffset <= 0.5;
    private bool AtBottom => _scroller.VerticalOffset >= _scroller.ScrollableHeight - 0.5;

    /// <summary>Single-page keys: ←/→ flip; Page Down/Space/↓ at the bottom (or Up keys at the top) flip too.</summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandleFormKey(e))
        {
            e.Handled = true;
            return;
        }
        if (_textEditor is not null)
        {
            // Typing in a text box: its keys are its own; Esc ends the edit and keeps the text.
            if (e.Key == VirtualKey.Escape)
            {
                CommitTextEditor();
                e.Handled = true;
            }
            return;
        }
        if (SelectedMarkup is { } markup && e.Key is VirtualKey.Delete or VirtualKey.Escape)
        {
            if (e.Key == VirtualKey.Delete) _ = RemoveAsync(markup);
            else SelectedMarkup = null;
            e.Handled = true;
            return;
        }
        if ((e.Key == VirtualKey.Delete && DeleteSelectedShape()) || (e.Key == VirtualKey.Escape && CancelShapeEditing()))
        {
            e.Handled = true;
            return;
        }
        if (!_singlePage || !_laidOut) return;
        int step = e.Key switch
        {
            VirtualKey.Right => +1,
            VirtualKey.Left => -1,
            VirtualKey.PageDown or VirtualKey.Space or VirtualKey.Down when AtBottom => +1,
            VirtualKey.PageUp or VirtualKey.Up when AtTop => -1,
            VirtualKey.Home => -CurrentPage,
            VirtualKey.End => PageCount - 1 - CurrentPage,
            _ => 0,
        };
        if (step == 0) return;
        if (e.Key is VirtualKey.Home or VirtualKey.End) GoToPage(CurrentPage + step);
        else FlipPage(step);
        e.Handled = true;
    }

    private void RestartSettleTimer()
    {
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private void UpdateViewport(bool requestTiles)
    {
        if (!_laidOut || PageCount == 0) return;
        float zf = _scroller.ZoomFactor;
        var viewport = new Rect(_scroller.HorizontalOffset / zf, _scroller.VerticalOffset / zf,
            _scroller.ViewportWidth / zf, _scroller.ViewportHeight / zf);
        double bufferTop = viewport.Top - viewport.Height, bufferBottom = viewport.Bottom + viewport.Height;
        var prefetch = new Rect(viewport.X, viewport.Y - viewport.Height / 2, viewport.Width, viewport.Height * 2);

        var needed = new HashSet<int>();
        if (_singlePage)
            needed.Add(CurrentPage);
        else
            for (int i = PageAtY(bufferTop); i < PageCount && _top[i] <= bufferBottom; i++)
                if (_top[i] + _h[i] >= bufferTop)
                    needed.Add(i);

        foreach (var (index, view) in _views.Where(kv => !needed.Contains(kv.Key)).ToList())
        {
            view.Unbind();
            _views.Remove(index);
            _pool.Push(view);
        }

        foreach (int i in needed)
        {
            var view = GetView(i);
            bool visible = _top[i] < viewport.Bottom && _top[i] + _h[i] > viewport.Top;
            // Sharp tiles for what's on screen go first; the blurry preview only matters if they're slow.
            if (requestTiles && visible) EnsureTiles(view, viewport, WorkPriority.High);
            EnsurePreview(view, visible && !requestTiles ? WorkPriority.High : WorkPriority.Low);
            if (requestTiles) EnsureTiles(view, prefetch, WorkPriority.Low);
            if (visible)
            {
                EnsureText(i);
                EnsureLinks(i);
                EnsureMarkups(i);
                EnsureShapes(i);
                EnsureFormFields(i);
            }
        }

        if (_singlePage) return; // the current page only changes by flipping
        int current = PageAtY(viewport.Top + viewport.Height * 0.3);
        if (_scroller.VerticalOffset >= _scroller.ScrollableHeight - 1 && _scroller.ScrollableHeight > 0)
            current = PageCount - 1;
        if (current != CurrentPage)
        {
            CurrentPage = current;
            CurrentPageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private PageView GetView(int index)
    {
        if (_views.TryGetValue(index, out var view)) return view;
        if (!_pool.TryPop(out view))
        {
            view = new PageView();
            _surface.Children.Add(view);
        }
        view.Bind(index);
        view.Width = double.NaN;
        _views[index] = view;
        Place(view);
        return view;
    }

    private void EnsurePreview(PageView view, WorkPriority priority)
    {
        if (view.Preview.Source is not null || view.PreviewRequested) return;
        int index = view.Index;
        if (_previews.TryGet(index, out var cached))
        {
            view.Preview.Source = cached;
            return;
        }
        view.PreviewRequested = true;
        _ = LoadPreviewAsync(view, index, priority);
    }

    private async Task LoadPreviewAsync(PageView view, int index, WorkPriority priority)
    {
        var bmp = await _previews.GetAsync(index, priority);
        if (bmp is not null && view.Index == index)
        {
            view.Preview.Source = bmp;
            if (index == 0) StartupTrace.Mark("preview0");
        }
    }

    private void EnsureTiles(PageView view, Rect region, WorkPriority priority)
    {
        int i = view.Index;
        double s = RasterScale;
        int fullW = Math.Max(1, (int)Math.Round(_w[i] * s));
        int fullH = Math.Max(1, (int)Math.Round(_h[i] * s));
        if (view.TileFullWidth != fullW)
        {
            if (view.TileFullWidth != 0) view.MarkTilesStale(_w[i]);
            view.TileFullWidth = fullW;
            view.TileDipWidth = _w[i];
        }

        double x0 = Math.Clamp((region.Left - _left[i]) * s, 0, fullW);
        double x1 = Math.Clamp((region.Right - _left[i]) * s, 0, fullW);
        double y0 = Math.Clamp((region.Top - _top[i]) * s, 0, fullH);
        double y1 = Math.Clamp((region.Bottom - _top[i]) * s, 0, fullH);
        if (x1 <= x0 || y1 <= y0) return;

        for (int ty = (int)(y0 / TileSize); ty * TileSize < y1; ty++)
        {
            for (int tx = (int)(x0 / TileSize); tx * TileSize < x1; tx++)
            {
                var key = (tx, ty);
                if (view.TileImages.ContainsKey(key) || view.PendingTiles.Contains(key)) continue;
                var rect = new PixelRect(tx * TileSize, ty * TileSize,
                    Math.Min(TileSize, fullW - tx * TileSize), Math.Min(TileSize, fullH - ty * TileSize));
                var cacheKey = new TileKey(i, fullW, tx, ty, _rotation);
                if (_tiles.TryGet(cacheKey, out var bmp))
                {
                    view.AddTile(key, bmp, rect, s);
                    continue;
                }
                view.PendingTiles.Add(key);
                _ = RenderTileAsync(view, view.Generation, cacheKey, rect, fullW, fullH, s, priority, view.Cts.Token);
            }
        }
        view.DropStaleTilesIfComplete();
        ReportFirstPageIfReady(view);
    }

    private async Task RenderTileAsync(PageView view, int generation, TileKey key, PixelRect rect,
        int fullW, int fullH, double scale, WorkPriority priority, CancellationToken ct)
    {
        RenderedBitmap rendered;
        var doc = _doc;
        try
        {
            rendered = await doc.RenderAsync(key.Page, fullW, fullH, rect, key.Rotation, priority, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or PdfException)
        {
            return;
        }
        if (doc != _doc) return; // the document was replaced while this rendered

        var bmp = Bitmaps.ToBitmap(rendered);
        if (!_firstPageReported) StartupTrace.Mark($"tile_p{key.Page}_{key.X}_{key.Y}");
        _tiles.Add(key, bmp);
        if (view.Generation != generation || view.Index != key.Page) return;

        view.PendingTiles.Remove((key.X, key.Y));
        view.AddTile((key.X, key.Y), bmp, rect, scale);
        view.DropStaleTilesIfComplete();
        TilesDrawn(view);
        ReportFirstPageIfReady(view);
    }

    private void ReportFirstPageIfReady(PageView view)
    {
        if (_firstPageReported || view.PendingTiles.Count > 0 || view.TileImages.Count == 0) return;
        _firstPageReported = true;
        FirstPageRendered?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- Text, selection & highlights ----------------

    private void EnsureText(int index)
    {
        if (_texts.TryGet(index, out _) || !_textsLoading.Add(index)) return;
        _ = LoadTextAsync(index);
    }

    private async Task LoadTextAsync(int index)
    {
        try
        {
            var doc = _doc;
            var text = await doc.GetTextAsync(index, WorkPriority.Low);
            if (doc != _doc) return;
            _texts.Add(index, text);
            if (_views.TryGetValue(index, out var view))
                RefreshOverlay(view);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or PdfException)
        {
        }
        finally
        {
            _textsLoading.Remove(index);
        }
    }

    private async Task<PageText?> GetTextAsync(int index)
    {
        if (_texts.TryGet(index, out var text)) return text;
        try
        {
            text = await _doc.GetTextAsync(index);
            _texts.Add(index, text);
            return text;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or PdfException)
        {
            return null;
        }
    }

    /// <summary>Text position under a point on the surface, or null if that page's text isn't loaded yet.</summary>
    private TextPos? HitTest(Point p, out PageText? text)
    {
        text = null;
        if (!_laidOut || PageCount == 0) return null;
        int page = PageAtPoint(p);
        // In the gap below a row, snap to the nearer row.
        int next = NextRowStart(page);
        if (!_singlePage && p.Y > _top[page] + _h[page] && next < PageCount &&
            p.Y - (_top[page] + _h[page]) > _top[next] - p.Y)
            page = PageAtPoint(new Point(p.X, _top[next]));
        if (!_texts.TryGet(page, out text))
        {
            EnsureText(page);
            return null;
        }
        var (nx, ny) = ToPageCoords(page, p);
        return new TextPos(page, text.CaretAt(nx, ny));
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) return;
        if (HandleAutoScrollPress(e)) return;
        var point = e.GetCurrentPoint(_surface);
        if (HandleHistoryButtons(point.Properties))
        {
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        _scroller.Focus(FocusState.Pointer);
        if (HandleFormPress(e, point.Position) || HandleShapePress(e, point.Position))
        {
            e.Handled = true;
            return;
        }
        bool shiftDown = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        if (!shiftDown && BeginLinkClick(point.Position))
        {
            e.Handled = true; // a click on a link follows it instead of starting a selection
            return;
        }
        _pressedMarkup = shiftDown ? null : MarkupAt(point.Position);
        var pos = HitTest(point.Position, out _);
        if (pos is null)
        {
            ClearSelection();
            return;
        }
        bool shift = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        if (!shift || _anchor is null) _anchor = pos;
        _focus = pos;
        _selecting = _surface.CapturePointer(e.Pointer);
        RefreshAllOverlays();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (HandleAutoScrollMove(e)) return;
        var point = e.GetCurrentPoint(_surface);
        if (_selecting)
        {
            if (HitTest(point.Position, out _) is { } pos && pos != _focus)
            {
                _focus = pos;
                RefreshAllOverlays();
            }
            AutoScrollWhileSelecting(e.GetCurrentPoint(_scroller).Position);
            return;
        }
        if (HandleFormMove(point.Position) || HandleShapeMove(point.Position, e)) return;
        if (UpdateMarkupHover(point.Position, e.GetCurrentPoint(this).Position) || LinkAt(point.Position) is not null)
        {
            ProtectedCursor = _handCursor;
            _textCursorShown = false;
            _overLink = true;
            return;
        }
        bool overText = false;
        if (_laidOut && PageCount > 0)
        {
            int page = PageAtPoint(point.Position);
            if (_texts.TryGet(page, out var text))
            {
                var (nx, ny) = ToPageCoords(page, point.Position);
                overText = nx is >= 0 and <= 1 && ny is >= 0 and <= 1 && text.IsOverText(nx, ny);
            }
        }
        if (overText != _textCursorShown || _overLink)
        {
            _textCursorShown = overText;
            _overLink = false;
            ProtectedCursor = overText ? _textCursor : _arrowCursor;
        }
    }

    private void AutoScrollWhileSelecting(Point inViewport)
    {
        const double edge = 24, step = 24;
        if (inViewport.Y < edge)
            _scroller.ChangeView(null, Math.Max(0, _scroller.VerticalOffset - step), null, true);
        else if (inViewport.Y > _scroller.ViewportHeight - edge)
            _scroller.ChangeView(null, _scroller.VerticalOffset + step, null, true);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (HandleAutoScrollRelease(e)) return;
        if (EndLinkClick(e.GetCurrentPoint(_surface).Position)) return;
        if (HandleFormRelease(e, e.GetCurrentPoint(_surface).Position) || HandleShapeRelease(e, e.GetCurrentPoint(_surface).Position)) return;
        if (!_selecting) return;
        _selecting = false;
        _surface.ReleasePointerCapture(e.Pointer);
        var pressedMarkup = _pressedMarkup;
        _pressedMarkup = null;
        if (_anchor == _focus)
        {
            // A click on a markup selects it; a click anywhere else lets it go.
            ClearSelection();
            var at = e.GetCurrentPoint(_surface).Position;
            SelectedMarkup = pressedMarkup is not null && MarkupAt(at) is { } under && Same(pressedMarkup, under) ? under : null;
        }
        else
        {
            SelectedMarkup = null;
            if (HighlighterMode) _ = MarkSelectionAsync(MarkupPalette.CurrentKind, MarkupPalette.Current);
        }
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var p = e.GetPosition(_surface);
        if (HandleFormDoubleTap(p) || HandleShapeDoubleTap(p)) return;
        // A double-click on a markup opens its comment, as in Acrobat.
        if (MarkupAt(p) is { } markup)
        {
            ClearSelection();
            SelectedMarkup = markup;
            EditComment(markup);
            return;
        }
        if (HitTest(p, out var text) is not { } pos || text is null) return;
        int page = pos.Page;
        var (cx, cy) = ToPageCoords(page, p);
        int ch = text.NearestChar(cx, cy);
        if (ch < 0) return;
        var (start, length) = text.WordAt(ch);
        _anchor = new TextPos(page, start);
        _focus = new TextPos(page, start + length);
        RefreshAllOverlays();
    }

    public void ClearSelection()
    {
        if (_anchor is null && _focus is null) return;
        _anchor = _focus = null;
        RefreshAllOverlays();
    }

    public async Task SelectAllAsync()
    {
        if (PageCount == 0) return;
        var last = await GetTextAsync(PageCount - 1);
        _anchor = new TextPos(0, 0);
        _focus = new TextPos(PageCount - 1, last?.Length ?? 0);
        RefreshAllOverlays();
    }

    public async Task<bool> CopySelectionAsync()
    {
        if (!HasSelection) return false;
        var (start, end) = OrderedSelection();
        string text = await _doc.GetTextRangeAsync(start.Page, start.Caret, end.Page, end.Caret);
        if (text.Length == 0) return false;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        return true;
    }

    private (TextPos Start, TextPos End) OrderedSelection()
    {
        var a = _anchor!.Value;
        var f = _focus!.Value;
        return a.CompareTo(f) <= 0 ? (a, f) : (f, a);
    }

    private MenuFlyout BuildContextMenu()
    {
        // Marking the selection comes first, the comment at the top, as in Acrobat.
        var menu = new MenuFlyout();
        AddMarkupItems(menu, () => HasSelection);
        menu.Items.Add(new MenuFlyoutSeparator());
        var copy = new MenuFlyoutItem { Text = "Copy", Icon = new SymbolIcon(Symbol.Copy) };
        copy.Click += async (_, _) => await CopySelectionAsync();
        var selectAll = new MenuFlyoutItem { Text = "Select All", Icon = new SymbolIcon(Symbol.SelectAll) };
        selectAll.Click += async (_, _) => await SelectAllAsync();
        menu.Items.Add(copy);
        menu.Items.Add(selectAll);
        menu.Opening += (_, _) => copy.IsEnabled = HasSelection;
        return menu;
    }

    public void SetSearchHits(IEnumerable<SearchHit> hits, SearchHit? current)
    {
        _hits = hits.GroupBy(h => h.PageIndex).ToDictionary(g => g.Key, g => g.ToList());
        _currentHit = current;
        RefreshAllOverlays();
    }

    /// <summary>Makes <paramref name="hit"/> the current match and scrolls it into view.</summary>
    public async Task ShowHitAsync(SearchHit hit)
    {
        _currentHit = hit;
        if (!_hits.ContainsKey(hit.PageIndex))
            _hits[hit.PageIndex] = [hit];
        var text = await GetTextAsync(hit.PageIndex);
        RefreshAllOverlays();
        if (text is null || !_laidOut) return;

        var rects = text.GetRects(hit.Start, hit.Length);
        if (rects.Count == 0)
        {
            GoToPage(hit.PageIndex);
            return;
        }
        if (_singlePage && hit.PageIndex != CurrentPage) GoToPage(hit.PageIndex);
        float zf = _scroller.ZoomFactor;
        int p = hit.PageIndex;
        var r = PageRotation.ToView(rects[0], _rotation);
        double y = _top[p] + r.Top * _h[p];
        double x = _left[p] + r.Left * _w[p];
        double vw = _scroller.ViewportWidth / zf, vh = _scroller.ViewportHeight / zf;
        double vx = _scroller.HorizontalOffset / zf, vy = _scroller.VerticalOffset / zf;

        double? newY = y < vy + 16 || y > vy + vh - 48 ? Math.Max(0, y - vh / 3) : null;
        double? newX = x < vx || x > vx + vw - 32 ? Math.Max(0, x - vw / 4) : null;
        if (newX is not null || newY is not null)
            _scroller.ChangeView(newX * zf, newY * zf, null, true);
    }

    private void RefreshAllOverlays()
    {
        foreach (var view in _views.Values)
            RefreshOverlay(view);
    }

    private void RefreshOverlay(PageView view)
    {
        view.Overlay.Children.Clear();
        int page = view.Index;
        if (page < 0) return;
        DrawShapeOverlay(view);
        DrawCommentMarkers(view);
        DrawMarkupSelection(view);

        bool hasHits = _hits.TryGetValue(page, out var hits);
        (int Start, int End)? selection = SelectionOnPage(page);
        if (!hasHits && selection is null) return;
        if (!_texts.TryGet(page, out var text))
        {
            EnsureText(page);
            return;
        }

        double w = _w[page], h = _h[page];
        void Draw(NormRect r, Brush brush)
        {
            var rect = new Rectangle
            {
                Fill = brush,
                Width = Math.Max(1, r.Width * w + 2),
                Height = Math.Max(1, r.Height * h + 2),
                RadiusX = 2,
                RadiusY = 2,
            };
            Canvas.SetLeft(rect, r.Left * w - 1);
            Canvas.SetTop(rect, r.Top * h - 1);
            view.Overlay.Children.Add(rect);
        }

        if (hasHits)
            foreach (var hit in hits!)
                foreach (var r in ViewRects(text, hit.Start, hit.Length))
                    Draw(r, hit == _currentHit ? CurrentHitBrush : HitBrush);

        if (selection is { } sel)
        {
            _selectionBrush ??= new SolidColorBrush(SelectionColor());
            foreach (var r in ViewRects(text, sel.Start, sel.End - sel.Start))
                Draw(r, _selectionBrush);
        }
    }

    private (int Start, int End)? SelectionOnPage(int page)
    {
        if (!HasSelection) return null;
        var (start, end) = OrderedSelection();
        if (page < start.Page || page > end.Page) return null;
        int from = page == start.Page ? start.Caret : 0;
        int to = page == end.Page ? end.Caret : int.MaxValue;
        return (from, to);
    }

    private static Windows.UI.Color SelectionColor()
    {
        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var c) && c is Windows.UI.Color color
            ? color
            : Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
        return Windows.UI.Color.FromArgb(0x60, accent.R, accent.G, accent.B);
    }

    /// <summary>A surface point as unrotated normalized page coordinates (what <see cref="PageText"/> uses).</summary>
    private (double X, double Y) ToPageCoords(int page, Point p) =>
        PageRotation.FromView((p.X - _left[page]) / _w[page], (p.Y - _top[page]) / _h[page], _rotation);

    private IEnumerable<NormRect> ViewRects(PageText text, int start, int count) =>
        text.GetRects(start, count).Select(r => PageRotation.ToView(r, _rotation));

    private readonly record struct TileKey(int Page, int FullWidth, int X, int Y, int Rotation);

    private readonly record struct TextPos(int Page, int Caret) : IComparable<TextPos>
    {
        public int CompareTo(TextPos other) => Page != other.Page ? Page.CompareTo(other.Page) : Caret.CompareTo(other.Caret);
    }
}



