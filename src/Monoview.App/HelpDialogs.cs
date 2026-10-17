using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Monoview.App;

/// <summary>Help > Keyboard shortcuts (F1) and Help > About.</summary>
internal static class HelpDialogs
{
    private static readonly (string Group, (string Keys, string Action)[] Items)[] Shortcuts =
    [
        ("Files", [
            ("Ctrl+O", "Open a PDF"),
            ("Ctrl+S / Ctrl+Shift+S", "Save / save as"),
            ("Ctrl+Z / Ctrl+Y", "Undo / redo"),
            ("F5", "Reload from disk"),
            ("Ctrl+P", "Print"),
            ("Ctrl+D", "Document properties"),
            ("Ctrl+W", "Close tab"),
            ("Ctrl+Shift+W", "Close all tabs"),
            ("Ctrl+Tab / Ctrl+Shift+Tab", "Next / previous tab; Ctrl+PgDn / Ctrl+PgUp too"),
            ("Ctrl+Shift+T", "Reopen the last closed tab"),
            ("Ctrl+N", "New window"),
            ("Drag a tab", "Reorder; drop on another window to move it there, or outside to give it its own window"),
            ("Middle-click a tab", "Close it"),
            ("Ctrl+,", "Settings"),
        ]),
        ("View", [
            ("Ctrl+B", "Show or hide the sidebar"),
            ("Ctrl++ / Ctrl+-", "Zoom in / out"),
            ("Ctrl+scroll", "Zoom around the mouse"),
            ("Ctrl+0", "Actual size"),
            ("Ctrl+8 / Ctrl+9", "Fit width / fit page"),
            ("Ctrl+G", "Go to page"),
            ("Alt+← / Alt+→", "Back / forward after a jump; mouse side buttons too"),
            ("Ctrl+R / Ctrl+L", "Rotate clockwise / counter-clockwise"),
            ("F11", "Full screen (Esc to leave)"),
            ("← / →", "Previous / next page (single page view)"),
            ("Middle-click", "Autoscroll (click again or Esc to stop)"),
        ]),
        ("Signatures", [
            ("Sign button", "Place a saved signature, or make one: draw, type or use a picture"),
            ("Drag / drag the corner", "Move / resize a placed signature"),
            ("Delete / Esc", "Delete the selected signature / stop placing"),
        ]),
        ("Pages, in the sidebar", [
            ("Ctrl+click / Shift+click", "Select several pages"),
            ("Drag", "Reorder pages; drop a PDF or picture from Explorer to insert it"),
            ("Drag to another document", "Copy the pages into it; hold over a tab to switch to it first"),
            ("Delete", "Delete the selected pages"),
            ("Right-click", "Rotate, insert from a file, extract to a new PDF"),
        ]),
        ("Find and select", [
            ("Ctrl+F", "Search"),
            ("Enter / F3", "Next match"),
            ("Shift+Enter / Shift+F3", "Previous match"),
            ("Esc", "Clear the search"),
            ("Double-click", "Select a word"),
            ("Ctrl+A / Ctrl+C", "Select all / copy text"),
            ("Ctrl+H", "Highlight the selection; with nothing selected, turn highlighter mode on or off"),
        ]),
    ];

    public static async Task ShowShortcutsAsync(XamlRoot root)
    {
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 6, MinWidth = 420 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int row = 0;
        foreach (var (group, items) in Shortcuts)
        {
            var header = new TextBlock
            {
                Text = group,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 2),
            };
            AddRow(grid, row++, header, null);
            foreach (var (keys, action) in items)
                AddRow(grid, row++, new TextBlock { Text = keys, Opacity = 0.75 }, new TextBlock { Text = action, TextWrapping = TextWrapping.Wrap });
        }

        await new ContentDialog
        {
            XamlRoot = root,
            Title = "Keyboard shortcuts",
            Content = new ScrollViewer { Content = grid, MaxHeight = 520 },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        }.ShowAsync();
    }

    public static async Task ShowAboutAsync(XamlRoot root)
    {
        string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "dev";
        string pdfium = Monoview.Engine.PdfDocument.PdfiumVersion;
        string dataFolder = Path.GetDirectoryName(CrashLog.LogPath)!;

        var content = new StackPanel { Spacing = 8, MinWidth = 380 };
        content.Children.Add(new TextBlock { Text = $"Version {version}" });
        content.Children.Add(new TextBlock { Text = "A fast, simple PDF viewer for Windows.", Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Text = $"PDF rendering by PDFium {pdfium} (BSD license).",
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Settings, recent files and crash logs: {dataFolder}",
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Monoview",
            Content = content,
            PrimaryButtonText = "Open data folder",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Directory.CreateDirectory(dataFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dataFolder}\"") { UseShellExecute = false });
        }
    }

    private static void AddRow(Grid grid, int row, FrameworkElement left, FrameworkElement? right)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(left, row);
        if (right is null) Grid.SetColumnSpan(left, 2);
        grid.Children.Add(left);
        if (right is not null)
        {
            Grid.SetRow(right, row);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
    }
}
