using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace Monoview.App;

/// <summary>
/// Tab dragging like Chrome. Along the strip the tab follows the pointer and the others slide aside. Pulled up or down
/// off the strip, it moves into a window of its own that follows the pointer; let go over another window's tabs, it
/// joins them. Driven by the pointer, not system drag and drop, which lags and can't move a window.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>How far above or below the tab strip, in DIPs, the pointer pulls a tab off into its own window.</summary>
    private const double TearOffDistance = 32;

    private TabViewItem? _pressedTab;
    private Point _pressPoint;      // on the tab strip
    private Point _pressOnTab;      // on the pressed tab
    private bool _draggingTab;
    private readonly List<(TabViewItem Tab, double Left, double Width)> _dragSlots = [];
    private int _dragFrom, _dragTo;
    private double _dragDx;
    private double[] _dragShifts = [];  // where each other tab is sliding to

    private void AttachTabDrag(TabViewItem tab)
    {
        tab.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Tab_PointerPressed), true);
        tab.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Tab_PointerMoved), true);
        tab.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Tab_PointerReleased), true);
        tab.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndStripDrag(commit: true)), true);
    }

    private void Tab_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not TabViewItem tab || !e.GetCurrentPoint(tab).Properties.IsLeftButtonPressed) return;
        // The close button closes the tab; it doesn't drag it.
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != tab; element = VisualTreeHelper.GetParent(element))
            if (element is ButtonBase) return;
        _pressedTab = tab;
        _pressPoint = e.GetCurrentPoint(Tabs).Position;
        _pressOnTab = e.GetCurrentPoint(tab).Position;
        tab.CapturePointer(e.Pointer);
    }

    private void Tab_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedTab is not { } tab || !ReferenceEquals(sender, tab)) return;
        var p = e.GetCurrentPoint(Tabs).Position;
        if (!_draggingTab)
        {
            if (Math.Abs(p.X - _pressPoint.X) < 4 && Math.Abs(p.Y - _pressPoint.Y) < 4) return;
            BeginStripDrag(tab);
        }
        // A home tab has no document to take to another window, so it only moves along the strip.
        if (tab.Tag is not null && (p.Y < -TearOffDistance || p.Y > Tabs.ActualHeight + TearOffDistance))
            TearOff(tab);
        else
            MoveStripDrag(p.X - _pressPoint.X);
    }

    private void Tab_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedTab is not { } tab || !ReferenceEquals(sender, tab)) return;
        tab.ReleasePointerCapture(e.Pointer);
        EndStripDrag(commit: true);
    }

    /// <summary>Notes where every tab sits, so the dragged one can move over them without the layout changing.</summary>
    private void BeginStripDrag(TabViewItem tab)
    {
        _draggingTab = true;
        Tabs.SelectedItem = tab;
        _dragSlots.Clear();
        foreach (var t in Tabs.TabItems.OfType<TabViewItem>())
        {
            double left = ((UIElement)VisualTreeHelper.GetParent(t)).TransformToVisual(Tabs).TransformPoint(default).X + t.ActualOffset.X;
            _dragSlots.Add((t, left, t.ActualWidth));
            // Through the composition layer: the tab slide (SlideTabs) already uses it, which rules out UIElement.Translation.
            ElementCompositionPreview.SetIsTranslationEnabled(t, true);
            var visual = ElementCompositionPreview.GetElementVisual(t);
            visual.StopAnimation("Translation"); // a glide from the last drop
            visual.ImplicitAnimations = null;
        }
        _dragFrom = _dragTo = _dragSlots.FindIndex(s => ReferenceEquals(s.Tab, tab));
        _dragShifts = new double[_dragSlots.Count];
    }

    /// <summary>
    /// Moves the dragged tab with the pointer, kept within the strip. A tab slides aside once the dragged tab's
    /// leading edge passes its middle; the others stay put, so nothing flips back and forth.
    /// </summary>
    private void MoveStripDrag(double dx)
    {
        var (dragged, left, width) = _dragSlots[_dragFrom];
        dx = Math.Clamp(dx, _dragSlots[0].Left - left, _dragSlots[^1].Left + _dragSlots[^1].Width - left - width);
        _dragDx = dx;
        Shift(dragged, dx);
        double l = left + dx, r = l + width;
        _dragTo = _dragFrom;
        for (int i = _dragFrom - 1; i >= 0 && l < _dragSlots[i].Left + _dragSlots[i].Width / 2; i--) _dragTo = i;
        for (int i = _dragFrom + 1; i < _dragSlots.Count && r > _dragSlots[i].Left + _dragSlots[i].Width / 2; i++) _dragTo = i;
        // Tabs it passed take its place: one slot over, by its width and the gap after it.
        double pitch = _dragFrom + 1 < _dragSlots.Count ? _dragSlots[_dragFrom + 1].Left - left : width;
        for (int i = 0; i < _dragSlots.Count; i++)
        {
            if (i == _dragFrom) continue;
            double shift = i >= _dragTo && i < _dragFrom ? pitch : i <= _dragTo && i > _dragFrom ? -pitch : 0;
            if (shift == _dragShifts[i]) continue;
            _dragShifts[i] = shift;
            SlideTo(_dragSlots[i].Tab, shift);
        }
    }

    /// <summary>Puts the tabs in the order the drag showed (unless <paramref name="commit"/> is false) and back in their places.</summary>
    private void EndStripDrag(bool commit)
    {
        if (_pressedTab is not { } tab) return;
        _pressedTab = null;
        if (!_draggingTab) return;
        _draggingTab = false;
        // The dragged tab starts from where it was let go, seen from its new place, and glides in.
        var (_, fromLeft, fromWidth) = _dragSlots[_dragFrom];
        var (_, toLeft, toWidth) = _dragSlots[commit ? _dragTo : _dragFrom];
        double newLeft = _dragTo < _dragFrom || !commit ? toLeft : toLeft + toWidth - fromWidth;
        double settle = fromLeft + _dragDx - newLeft;
        _dragDx = 0;
        foreach (var (t, _, _) in _dragSlots)
        {
            ElementCompositionPreview.GetElementVisual(t).StopAnimation("Translation");
            Shift(t, ReferenceEquals(t, tab) ? settle : 0);
        }
        // Move the others past the dragged tab rather than the tab itself: taking out the selected tab would switch
        // the document shown for a moment.
        for (int moves = commit ? Math.Abs(_dragTo - _dragFrom) : 0; moves > 0 && Tabs.TabItems.Contains(tab); moves--)
        {
            int at = Tabs.TabItems.IndexOf(tab), other = _dragTo < _dragFrom ? at - 1 : at + 1;
            var item = Tabs.TabItems[other];
            Tabs.TabItems.RemoveAt(other);
            Tabs.TabItems.Insert(at, item);
        }
        _dragSlots.Clear();
        SlideTo(tab, 0);
        // Slides come back once the new order is laid out, so it doesn't play as a slide.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, SlideTabs);
    }

    private static void Shift(UIElement tab, double dx) =>
        ElementCompositionPreview.GetElementVisual(tab).Properties.InsertVector3("Translation", new Vector3((float)dx, 0, 0));

    /// <summary>Slides a tab from where it is drawn to <paramref name="dx"/> from its place.</summary>
    private static void SlideTo(UIElement tab, double dx)
    {
        var visual = ElementCompositionPreview.GetElementVisual(tab);
        var slide = visual.Compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1f, new Vector3((float)dx, 0, 0), EaseOut(visual.Compositor));
        slide.Duration = SlideTime;
        visual.StartAnimation("Translation", slide);
    }

    /// <summary>How long tabs take to slide aside, and the dropped tab to settle, as in Chrome.</summary>
    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(150);

    /// <summary>Fast at first, slowing into place.</summary>
    private static Microsoft.UI.Composition.CompositionEasingFunction EaseOut(Microsoft.UI.Composition.Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));

    // ---------------- Tearing a tab off into its own window ----------------

    /// <summary>
    /// The tab goes into a new window under the pointer, which then follows it. A window's only tab takes the
    /// whole window along instead.
    /// </summary>
    private void TearOff(TabViewItem tab)
    {
        var grab = _pressOnTab;
        double firstLeft = _dragSlots[0].Left;
        EndStripDrag(commit: false);
        // While this window holds the mouse, Windows keeps it in front of the new one.
        tab.ReleasePointerCaptures();
        if (Root.XamlRoot is not { } root || !GetCursorPos(out var cursor)) return;
        double scale = root.RasterizationScale;
        s_tabGrab = (grab.X, tab.ActualWidth);
        if (Tabs.TabItems.Count == 1)
        {
            DragWindow(this, tab, new PointInt32(cursor.X - AppWindow.Position.X, cursor.Y - AppWindow.Position.Y));
            return;
        }
        // In the new window the tab is the first one: where this window's first tab is, held where it was grabbed.
        var client = new CursorPoint();
        ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this), ref client);
        var strip = Tabs.TransformToVisual(Root).TransformPoint(new Point(firstLeft + grab.X, tab.TransformToVisual(Tabs).TransformPoint(default).Y + grab.Y));
        var offset = new PointInt32(client.X - AppWindow.Position.X + (int)(strip.X * scale), client.Y - AppWindow.Position.Y + (int)(strip.Y * scale));
        var window = App.Current.NewWindow();
        window.AppWindow.Resize(AppWindow.Size);
        window.AppWindow.Move(new PointInt32(cursor.X - offset.X, cursor.Y - offset.Y));
        window.Activate();
        MoveTab(tab, window);
        if (window.Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault() is { } moved)
            DragWindow(window, moved, offset);
    }

    /// <summary>
    /// Moves <paramref name="window"/> with the pointer, held at <paramref name="grab"/>, until the button is let go.
    /// Over another window's tabs it hides and that window shows where <paramref name="tab"/> would go; let go
    /// there, the tab joins them and the emptied window closes.
    /// </summary>
    private static void DragWindow(MainWindow window, TabViewItem tab, PointInt32 grab)
    {
        if (tab.Tag is not DocumentView view) return;
        KeepOnTop(window, true); // like Chrome, the dragged window stays above every other until it's let go
        var timer = window.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(8);
        MainWindow? target = null;
        timer.Tick += (_, _) =>
        {
            GetCursorPos(out var cursor);
            var over = App.Current.Windows.FirstOrDefault(w => w != window && w.TabStripAt(cursor) is not null);
            if (over != target) target?.TakeIncomingTab(null);
            target = over;
            if (GetAsyncKeyState(LeftButton) >= 0) // let go
            {
                timer.Stop();
                KeepOnTop(window, false);
                if (target is not null)
                {
                    window.MoveTab(tab, target); // at the placeholder; closes the emptied window
                    return;
                }
                window.AppWindow.Show();
                window.BringToFront();
                return;
            }
            window.AppWindow.Move(new PointInt32(cursor.X - grab.X, cursor.Y - grab.Y));
            if (target?.TabStripAt(cursor) is { } p)
            {
                target.ShowIncomingTab(view, target.TabIndexAt(p.X));
                if (window.AppWindow.IsVisible) window.AppWindow.Hide();
            }
            else if (!window.AppWindow.IsVisible)
            {
                window.AppWindow.Show();
            }
        };
        timer.Start();
    }

    /// <summary>The point on this window's tab strip under <paramref name="screen"/>, or null when it isn't over the strip.</summary>
    private Point? TabStripAt(CursorPoint screen)
    {
        if (Tabs.Visibility != Visibility.Visible || !AppWindow.IsVisible || Root.XamlRoot is not { } root) return null;
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized }) return null;
        ScreenToClient(WinRT.Interop.WindowNative.GetWindowHandle(this), ref screen);
        double scale = root.RasterizationScale;
        var p = Root.TransformToVisual(Tabs).TransformPoint(new Point(screen.X / scale, screen.Y / scale));
        return p.X >= 0 && p.X <= Tabs.ActualWidth && p.Y >= -8 && p.Y <= Tabs.ActualHeight + 8 ? p : null;
    }

    private static void KeepOnTop(MainWindow window, bool on)
    {
        const uint NoSize = 0x1, NoMove = 0x2, NoActivate = 0x10;
        SetWindowPos(WinRT.Interop.WindowNative.GetWindowHandle(window), on ? -1 : -2, 0, 0, 0, 0, NoSize | NoMove | NoActivate); // HWND_TOPMOST, HWND_NOTOPMOST
    }

    // ---------------- A tab from another window, held over this one ----------------

    /// <summary>Where the pointer grabbed the dragged tab, and its width, so a window can tell where the tab's edges are.</summary>
    private static (double Grab, double Width) s_tabGrab;

    /// <summary>
    /// A tab from another window held over this one: a greyed-out tab with its file name where it will land, and
    /// its view, built and drawn unseen behind the current one, so the drop shows the document at once.
    /// </summary>
    private (TabViewItem Placeholder, DocumentView View)? _incoming;

    /// <summary>Puts the placeholder for <paramref name="source"/>'s tab at <paramref name="index"/> among this window's tabs.</summary>
    private void ShowIncomingTab(DocumentView source, int index)
    {
        if (_incoming is not { } incoming)
        {
            var view = CreateDocumentView(source.Document);
            view.Viewer.InitialState = source.Viewer.CaptureState();
            view.SetSidebarVisible(source.IsSidebarVisible);
            view.Opacity = 0;
            view.IsHitTestVisible = false;
            DocumentHost.Children.Insert(0, view);
            var placeholder = new TabViewItem
            {
                Header = Path.GetFileName(source.Document.Path), IconSource = new SymbolIconSource { Symbol = Symbol.Document },
                IsClosable = false, IsHitTestVisible = false, Opacity = 0.45,
            };
            MakePill(placeholder);
            _incoming = incoming = (placeholder, view);
        }
        int at = Tabs.TabItems.IndexOf(incoming.Placeholder);
        if (at == index) return;
        if (at >= 0) Tabs.TabItems.RemoveAt(at);
        Tabs.TabItems.Insert(Math.Min(index, Tabs.TabItems.Count), incoming.Placeholder);
        SlideTabs();
        Tabs.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Removes the dragged tab's placeholder. Returns the view built for it, and the placeholder's place, when it shows
    /// <paramref name="doc"/>; otherwise closes the view and returns null.
    /// </summary>
    private (DocumentView View, int Index)? TakeIncomingTab(Monoview.Engine.PdfDocument? doc)
    {
        if (_incoming is not { } incoming) return null;
        _incoming = null;
        int index = Tabs.TabItems.IndexOf(incoming.Placeholder);
        Tabs.TabItems.Remove(incoming.Placeholder);
        DocumentHost.Children.Remove(incoming.View);
        if (!ReferenceEquals(incoming.View.Document, doc))
        {
            incoming.View.Close(closeDocument: false);
            Tabs.Visibility = !_fullScreen && Tabs.TabItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            return null;
        }
        incoming.View.Opacity = 1;
        incoming.View.IsHitTestVisible = true;
        return (incoming.View, index);
    }

    private double _incomingX;

    /// <summary>
    /// Where a tab dragged with the pointer at <paramref name="x"/> lands, counting the tabs without its placeholder.
    /// A tab gets out of the way once the dragged tab's leading edge is a quarter of the way into it. The placeholder
    /// only moves the way the pointer moves, so it can't flip back and forth over a tab.
    /// </summary>
    private int TabIndexAt(double x)
    {
        var tabs = Tabs.TabItems.OfType<TabViewItem>().ToList();
        int placeholder = _incoming is { } incoming ? tabs.IndexOf(incoming.Placeholder) : -1;
        double dx = x - _incomingX;
        _incomingX = x;
        if (placeholder >= 0 && dx == 0) return placeholder;
        // The dragged tab as wide as its placeholder here, held where the pointer grabbed it.
        double width = placeholder >= 0 && tabs[placeholder].ActualWidth > 0 ? tabs[placeholder].ActualWidth : s_tabGrab.Width;
        double left = x - s_tabGrab.Grab * width / Math.Max(1, s_tabGrab.Width), right = left + width;
        bool leftward = placeholder >= 0 && dx < 0;
        int index = 0;
        for (int i = 0; i < tabs.Count; i++)
        {
            if (i == placeholder) continue;
            // Where layout put the tab, not where its slide has got to.
            double start = ((UIElement)VisualTreeHelper.GetParent(tabs[i])).TransformToVisual(Tabs).TransformPoint(default).X + tabs[i].ActualOffset.X;
            if (leftward ? left > start + tabs[i].ActualWidth * 0.75 : right > start + tabs[i].ActualWidth * 0.25) index++;
        }
        return placeholder < 0 ? index : leftward ? Math.Min(placeholder, index) : Math.Max(placeholder, index);
    }

    // ---------------- The pointer, read from Windows: it outlives the tab and window it started in ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X, Y;
    }

    private const int LeftButton = 0x01; // VK_LBUTTON

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out CursorPoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(IntPtr window, ref CursorPoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr window, ref CursorPoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
