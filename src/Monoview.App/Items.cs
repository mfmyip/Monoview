using System.ComponentModel;
using Monoview.Engine;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Monoview.App;

public sealed partial class ThumbnailItem(int index, double width, double height) : INotifyPropertyChanged
{
    private WriteableBitmap? _image;

    public int Index { get; } = index;
    public string Label { get; } = (index + 1).ToString();
    public double DisplayWidth { get; } = width;
    public double DisplayHeight { get; } = height;

    public WriteableBitmap? Image
    {
        get => _image;
        set
        {
            if (_image == value) return;
            _image = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class SearchResultItem(SearchHit hit)
{
    public SearchHit Hit { get; } = hit;
    public string PageLabel { get; } = $"Page {hit.PageIndex + 1}";
    public string Before { get; } = hit.Snippet[..hit.SnippetMatchStart];
    public string Match { get; } = hit.Snippet.Substring(hit.SnippetMatchStart, hit.SnippetMatchLength);
    public string After { get; } = hit.Snippet[(hit.SnippetMatchStart + hit.SnippetMatchLength)..];
}
