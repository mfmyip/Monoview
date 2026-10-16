using System.Globalization;
using CleanPDF.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanPDF.App;

/// <summary>File > Document properties (Ctrl+D).</summary>
internal static class PropertiesDialog
{
    public static async Task ShowAsync(XamlRoot root, PdfDocument document)
    {
        DocumentProperties props;
        try
        {
            props = await document.GetPropertiesAsync();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
            return;
        }

        var rows = new List<(string Label, string? Value)>
        {
            ("File", Path.GetFileName(document.Path)),
            ("Location", Path.GetDirectoryName(document.Path)),
            ("Size", FormatBytes(props.FileSize)),
            ("Pages", props.PageCount.ToString(CultureInfo.CurrentCulture)),
            ("Page size", FormatPageSize(props.FirstPageSize)),
            ("PDF version", props.PdfVersion),
            ("Security", props.Encrypted ? "Encrypted" : "None"),
            ("Title", props.Title),
            ("Author", props.Author),
            ("Subject", props.Subject),
            ("Keywords", props.Keywords),
            ("Created", FormatDate(props.Created)),
            ("Modified", FormatDate(props.Modified)),
            ("Application", props.Creator),
            ("PDF producer", props.Producer),
        };

        var grid = new Grid { ColumnSpacing = 20, RowSpacing = 6, MinWidth = 440 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        foreach (var (label, value) in rows)
        {
            if (string.IsNullOrWhiteSpace(value)) continue; // skip metadata the file doesn't have
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Opacity = 0.7 };
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            grid.Children.Add(name);
            grid.Children.Add(text);
            row++;
        }

        await new ContentDialog
        {
            XamlRoot = root,
            Title = "Document properties",
            Content = new ScrollViewer { Content = grid, MaxHeight = 520 },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        }.ShowAsync();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB ({bytes:N0} bytes)",
    };

    private static string FormatPageSize(PageSize size)
    {
        double wIn = size.Width / 72, hIn = size.Height / 72;
        string named = (Math.Round(size.Width), Math.Round(size.Height)) switch
        {
            (612, 792) or (792, 612) => " (Letter)",
            (612, 1008) or (1008, 612) => " (Legal)",
            (595, 842) or (842, 595) => " (A4)",
            (420, 595) or (595, 420) => " (A5)",
            _ => "",
        };
        return $"{wIn:0.##} × {hIn:0.##} in · {wIn * 25.4:0} × {hIn * 25.4:0} mm{named}";
    }

    private static string? FormatDate(DateTimeOffset? date) =>
        date?.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
}
