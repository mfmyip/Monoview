using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Monoview.App.Viewer;

/// <summary>
/// Middle-click autoscroll, as in browsers and Acrobat. Click the wheel and release: an anchor
/// icon appears and the page scrolls toward the mouse, faster the further away it is; any click,
/// the wheel or Esc stops it. Press the wheel and drag: scrolls while held, stops on release.
/// </summary>
public sealed partial class PdfViewer
{
    private const double AutoScrollDeadZone = 10;

    private readonly Canvas _overlayLayer = new() { IsHitTestVisible = false };
    private readonly InputCursor _scrollCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private FrameworkElement? _autoScrollIcon;
    private AutoScrollState? _autoScroll;

    public bool IsAutoScrolling => _autoScroll is not null;
    public double VerticalOffset => _scroller.VerticalOffset;

    private void InitAutoScroll()
    {
        KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape && _autoScroll is not null)
            {
                StopAutoScroll();
                e.Handled = true;
            }
        };
        _scroller.LostFocus += (_, _) => StopAutoScroll();
    }

    /// <summary>Returns true if the press was consumed by autoscroll (starting or stopping it).</summary>
    private bool HandleAutoScrollPress(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_scroller);
        if (_autoScroll is not null)
        {
            StopAutoScroll(); // any click ends autoscroll, and does nothing else
            e.Handled = true;
            return true;
        }
        if (!point.Properties.IsMiddleButtonPressed || !_laidOut) return false;

        StartAutoScroll(point.Position);
        _autoScroll!.Held = true;
        if (_surface.CapturePointer(e.Pointer)) _autoScroll.CapturedPointer = e.Pointer;
        e.Handled = true;
        return true;
    }

    private bool HandleAutoScrollMove(PointerRoutedEventArgs e)
    {
        if (_autoScroll is null) return false;
        UpdateAutoScrollPointer(e.GetCurrentPoint(_scroller).Position);
        return true;
    }

    private bool HandleAutoScrollRelease(PointerRoutedEventArgs e)
    {
        if (_autoScroll is not { Held: true } state) return false;
        state.Held = false;
        if (state.CapturedPointer is not null)
        {
            _surface.ReleasePointerCapture(state.CapturedPointer);
            state.CapturedPointer = null;
        }
        // Press-drag-release scrolls only while held; a plain click keeps scrolling until the next click.
        if (state.Dragged) StopAutoScroll();
        e.Handled = true;
        return true;
    }

    private void OnAutoScrollCaptureLost()
    {
        if (_autoScroll is { Held: true, CapturedPointer: not null })
            StopAutoScroll();
    }

    /// <summary>Starts autoscroll anchored at a viewport point (also used by the --autoscroll-test dev check).</summary>
    public void StartAutoScroll(Point anchor)
    {
        StopAutoScroll();
        _autoScroll = new AutoScrollState
        {
            Anchor = anchor,
            Pointer = anchor,
            H = _scroller.HorizontalOffset,
            V = _scroller.VerticalOffset,
            LastTicks = Stopwatch.GetTimestamp(),
        };

        if (_autoScrollIcon is null)
        {
            _autoScrollIcon = CreateAutoScrollIcon();
            _overlayLayer.Children.Add(_autoScrollIcon);
        }
        Canvas.SetLeft(_autoScrollIcon, anchor.X - 16);
        Canvas.SetTop(_autoScrollIcon, anchor.Y - 16);
        _autoScrollIcon.Visibility = Visibility.Visible;
        ProtectedCursor = _scrollCursor;
        _scroller.Focus(FocusState.Pointer);
        CompositionTarget.Rendering += OnAutoScrollFrame;
    }

    public void UpdateAutoScrollPointer(Point pointer)
    {
        if (_autoScroll is not { } state) return;
        state.Pointer = pointer;
        double dx = pointer.X - state.Anchor.X, dy = pointer.Y - state.Anchor.Y;
        if (state.Held && dx * dx + dy * dy > AutoScrollDeadZone * AutoScrollDeadZone)
            state.Dragged = true;
    }

    public void StopAutoScroll()
    {
        if (_autoScroll is null) return;
        CompositionTarget.Rendering -= OnAutoScrollFrame;
        if (_autoScroll.CapturedPointer is not null)
            _surface.ReleasePointerCapture(_autoScroll.CapturedPointer);
        _autoScroll = null;
        if (_autoScrollIcon is not null) _autoScrollIcon.Visibility = Visibility.Collapsed;
        _textCursorShown = false;
        ProtectedCursor = _arrowCursor;
        UpdateViewport(requestTiles: true);
    }

    private void OnAutoScrollFrame(object? sender, object e)
    {
        if (_autoScroll is not { } state) return;
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Min(0.05, (now - state.LastTicks) / (double)Stopwatch.Frequency);
        state.LastTicks = now;

        double vx = _scroller.ScrollableWidth > 0 ? Speed(state.Pointer.X - state.Anchor.X) : 0;
        double vy = Speed(state.Pointer.Y - state.Anchor.Y);
        if (vx == 0 && vy == 0) return;

        // Track the position ourselves so sub-pixel steps at slow speeds still add up.
        state.H = Math.Clamp(state.H + vx * dt, 0, _scroller.ScrollableWidth);
        state.V = Math.Clamp(state.V + vy * dt, 0, _scroller.ScrollableHeight);
        _scroller.ChangeView(state.H, state.V, null, true);
    }

    /// <summary>Scroll speed in DIPs per second for a pointer offset from the anchor.</summary>
    private static double Speed(double offset)
    {
        double d = Math.Abs(offset) - AutoScrollDeadZone;
        if (d <= 0) return 0;
        // Gentle near the anchor, quick further out: at speed 1, 50 px ≈ 375 px/s and 200 px ≈ 2,200 px/s.
        return Math.Sign(offset) * (d * 6 + d * d * 0.03) * AppSettings.Current.AutoScrollSpeed;
    }

    private static FrameworkElement CreateAutoScrollIcon()
    {
        const string xaml = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="32" Height="32" Visibility="Collapsed">
                <Ellipse Fill="#E6FFFFFF" Stroke="#80000000" StrokeThickness="1" />
                <Path Fill="#CC000000" Data="M16,5 L20,11 L12,11 Z M16,27 L20,21 L12,21 Z M5,16 L11,12 L11,20 Z M27,16 L21,12 L21,20 Z" />
                <Ellipse Width="4" Height="4" Fill="#CC000000" />
            </Grid>
            """;
        return (FrameworkElement)XamlReader.Load(xaml);
    }

    private sealed class AutoScrollState
    {
        public Point Anchor;
        public Point Pointer;
        public double H, V;
        public long LastTicks;
        public bool Held;
        public bool Dragged;
        public Pointer? CapturedPointer;
    }
}
