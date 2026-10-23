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

    private bool _isSelected;

    /// <summary>Selected in the page list: the picture gets an accent ring.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ring)));
        }
    }

    public Microsoft.UI.Xaml.Media.Brush Ring => _isSelected
        ? (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"]
        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

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

/// <summary>A comment in the comments pane: a markup's comment or a sticky note's text, and where it is.</summary>
public sealed class CommentItem(int pageIndex, double top, string kind, MarkupColor color, string text)
{
    public int PageIndex { get; } = pageIndex;
    /// <summary>The comment's top on its page, unrotated and normalized, to scroll to.</summary>
    public double Top { get; } = top;
    public string Label { get; } = $"Page {pageIndex + 1} · {kind}";
    public Microsoft.UI.Xaml.Media.Brush Color { get; } = Viewer.MarkupPalette.Brush(color);
    public string Text { get; } = text;
}

public sealed class SearchResultItem(SearchHit hit)
{
    public SearchHit Hit { get; } = hit;
    public string PageLabel { get; } = $"Page {hit.PageIndex + 1}";
    public string Before { get; } = hit.Snippet[..hit.SnippetMatchStart];
    public string Match { get; } = hit.Snippet.Substring(hit.SnippetMatchStart, hit.SnippetMatchLength);
    public string After { get; } = hit.Snippet[(hit.SnippetMatchStart + hit.SnippetMatchLength)..];
}
