using CleanPDF.Engine;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace CleanPDF.App.Viewer;

/// <summary>Clickable links, and Back/Forward history for jumps (links, outline, page box, thumbnails).</summary>
public sealed partial class PdfViewer
{
    private const int MaxHistory = 100;

    private readonly LruCache<int, IReadOnlyList<PageLink>> _links = new(48);
    private readonly HashSet<int> _linksLoading = new();
    private readonly InputCursor _handCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    private readonly List<ViewPosition> _back = new();
    private readonly List<ViewPosition> _forward = new();
    private PageLink? _pressedLink;

    /// <summary>Back or Forward became possible or impossible.</summary>
    public event EventHandler? HistoryChanged;

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>A jump the user can undo with Back: remembers where the view was, then goes to the page.</summary>
    public void NavigateTo(int page, double? normalizedY = null)
    {
        if (!_laidOut || PageCount == 0) return;
        Push(_back, CurrentPosition());
        _forward.Clear();
        GoToPage(page, normalizedY);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void GoBack() => Step(_back, _forward);
    public void GoForward() => Step(_forward, _back);

    private void Step(List<ViewPosition> from, List<ViewPosition> to)
    {
        if (from.Count == 0 || !_laidOut) return;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        Push(to, CurrentPosition());
        Restore(target);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Push(List<ViewPosition> stack, ViewPosition position)
    {
        stack.Add(position);
        if (stack.Count > MaxHistory) stack.RemoveAt(0);
    }

    /// <summary>The page at the top of the view and how far down it the view starts (0..1, as shown).</summary>
    private ViewPosition CurrentPosition()
    {
        float zf = _scroller.ZoomFactor;
        int page = CurrentPage;
        double top = _scroller.VerticalOffset / zf;
        return new ViewPosition(page, Math.Clamp((top - _top[page]) / _h[page], -0.1, 1));
    }

    private void Restore(ViewPosition position)
    {
        GoToPage(position.Page);
        int page = Math.Clamp(position.Page, 0, PageCount - 1);
        double y = _top[page] + position.Fraction * _h[page];
        _pendingOffset = null;
        _scroller.ChangeView(null, Math.Max(0, y) * _scroller.ZoomFactor, null, true);
    }

    // ---------------- Links ----------------

    private void EnsureLinks(int index)
    {
        if (_links.TryGet(index, out _) || !_linksLoading.Add(index)) return;
        _ = LoadLinksAsync(index);
    }

    private async Task LoadLinksAsync(int index)
    {
        try
        {
            _links.Add(index, await _doc.GetLinksAsync(index));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or PdfException)
        {
        }
        finally
        {
            _linksLoading.Remove(index);
        }
    }

    /// <summary>The link under a point on the surface, if its page's links are loaded.</summary>
    private PageLink? LinkAt(Point p)
    {
        if (!_laidOut || PageCount == 0) return null;
        int page = PageAtPoint(p);
        if (!_links.TryGet(page, out var links) || links.Count == 0) return null;
        var (x, y) = ToPageCoords(page, p);
        const double slop = 0.002;
        return links.FirstOrDefault(l => l.Rects.Any(r =>
            x >= r.Left - slop && x <= r.Right + slop && y >= r.Top - slop && y <= r.Bottom + slop));
    }

    /// <summary>Mouse side buttons go back and forward, as in browsers. Returns true if handled.</summary>
    private bool HandleHistoryButtons(PointerPointProperties properties)
    {
        if (properties.IsXButton1Pressed) { GoBack(); return true; }
        if (properties.IsXButton2Pressed) { GoForward(); return true; }
        return false;
    }

    private bool BeginLinkClick(Point p)
    {
        _pressedLink = LinkAt(p);
        return _pressedLink is not null;
    }

    /// <summary>Follows the link if the button is released over the same link it was pressed on.</summary>
    private bool EndLinkClick(Point p)
    {
        var pressed = _pressedLink;
        _pressedLink = null;
        if (pressed is null) return false;
        if (LinkAt(p) == pressed) _ = FollowAsync(pressed);
        return true;
    }

    private async Task FollowAsync(PageLink link)
    {
        if (link.TargetPage is { } page)
        {
            NavigateTo(page, link.TargetY);
            return;
        }
        // Only open web and mail addresses; other schemes in a PDF could launch programs.
        if (Uri.TryCreate(link.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private readonly record struct ViewPosition(int Page, double Fraction);
}
