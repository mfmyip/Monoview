using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using CleanPDF.Engine;

namespace CleanPDF.App.Viewer;

/// <summary>
/// One page on the viewer surface. Layers, bottom to top: a blurry whole-page preview,
/// stale tiles from the previous zoom (scaled to fit), sharp tiles, then highlights.
/// Views are recycled as pages scroll in and out.
/// </summary>
internal sealed class PageView : Grid
{
    private static readonly Brush PageBackground = new SolidColorBrush(Colors.White);
    private static readonly Brush FrameBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0, 0, 0));

    private Canvas _tiles = new();
    private Canvas? _staleTiles;
    private double _staleDipWidth;

    public int Index { get; private set; } = -1;
    /// <summary>Bumped whenever outstanding tile renders become irrelevant.</summary>
    public int Generation { get; private set; }
    public CancellationTokenSource Cts { get; private set; } = new();
    public bool PreviewRequested { get; set; }

    public Image Preview { get; } = new() { Stretch = Stretch.Fill };
    public Canvas Overlay { get; } = new() { IsHitTestVisible = false };
    public Dictionary<(int X, int Y), Image> TileImages { get; } = new();
    public HashSet<(int X, int Y)> PendingTiles { get; } = new();

    /// <summary>Full page width in pixels that the current tiles were rendered for.</summary>
    public int TileFullWidth { get; set; }
    /// <summary>Page width in DIPs when the current tiles were laid out.</summary>
    public double TileDipWidth { get; set; }

    public PageView()
    {
        Background = PageBackground;
        IsHitTestVisible = false; // pointer input is handled by the surface
        Children.Add(Preview);
        Children.Add(_tiles);
        Children.Add(Overlay);
        Children.Add(new Rectangle { Stroke = FrameBrush, StrokeThickness = 1 });
    }

    public void Bind(int index)
    {
        Reset();
        Index = index;
        Visibility = Visibility.Visible;
    }

    public void Unbind()
    {
        Reset();
        Visibility = Visibility.Collapsed;
    }

    public void AddTile((int X, int Y) key, WriteableBitmap bitmap, PixelRect rect, double rasterScale)
    {
        if (TileImages.ContainsKey(key)) return;
        var img = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Fill,
            Width = rect.Width / rasterScale,
            Height = rect.Height / rasterScale,
        };
        Canvas.SetLeft(img, rect.X / rasterScale);
        Canvas.SetTop(img, rect.Y / rasterScale);
        _tiles.Children.Add(img);
        TileImages[key] = img;
    }

    /// <summary>
    /// The page was resized (zoom). Keep the current tiles visible, stretched to the new size,
    /// until sharp tiles for the new size have all arrived.
    /// </summary>
    public void MarkTilesStale(double newDipWidth)
    {
        CancelPending();
        if (_tiles.Children.Count > 0)
        {
            if (_staleTiles is not null) Children.Remove(_staleTiles);
            _staleTiles = _tiles;
            _staleDipWidth = TileDipWidth;
            _tiles = new Canvas();
            Children.Insert(Children.IndexOf(_staleTiles) + 1, _tiles);
        }
        if (_staleTiles is not null && _staleDipWidth > 0)
        {
            double f = newDipWidth / _staleDipWidth;
            _staleTiles.RenderTransform = new ScaleTransform { ScaleX = f, ScaleY = f };
        }
        TileImages.Clear();
        TileFullWidth = 0;
    }

    public void DropStaleTilesIfComplete()
    {
        if (_staleTiles is not null && PendingTiles.Count == 0 && TileImages.Count > 0)
        {
            Children.Remove(_staleTiles);
            _staleTiles = null;
        }
    }

    private void CancelPending()
    {
        Generation++;
        Cts.Cancel();
        Cts.Dispose();
        Cts = new CancellationTokenSource();
        PendingTiles.Clear();
    }

    private void Reset()
    {
        CancelPending();
        Index = -1;
        PreviewRequested = false;
        Preview.Source = null;
        _tiles.Children.Clear();
        if (_staleTiles is not null)
        {
            Children.Remove(_staleTiles);
            _staleTiles = null;
        }
        TileImages.Clear();
        TileFullWidth = 0;
        Overlay.Children.Clear();
    }
}
