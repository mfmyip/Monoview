using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Monoview.App.Viewer;
using Monoview.Engine;

namespace Monoview.App;

/// <summary>
/// The markup bar, Preview's second toolbar row: drawing tools, then line color, fill color, line width and
/// text size. Ctrl+Shift+A or the Markup button shows and hides it.
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<MarkupTool, ToggleButton> _toolButtons = new();
    private Border? _strokeSwatch, _fillSwatch;
    private TextBlock? _widthLabel, _sizeLabel;

    private static ShapeStyle CurrentShapeStyle()
    {
        var s = AppSettings.Current;
        return new ShapeStyle(MarkupPalette.Parse(s.ShapeStroke) ?? new MarkupColor(220, 40, 40), MarkupPalette.Parse(s.ShapeFill),
            s.ShapeLineWidth, s.ShapeFontSize);
    }

    private void BuildMarkupBar()
    {
        var tools = new (MarkupTool Tool, string Name, Func<FrameworkElement> Icon)[]
        {
            (MarkupTool.Select, "Select: select text, or select and move objects", () => PathIcon("M3,1 L3,15 L7,11 L10,17 L12,16 L9,10 L14,10 Z", filled: true)),
            (MarkupTool.Ink, "Draw: freehand strokes", () => PathIcon("M1,13 C4,3 6,3 7,9 C8,15 11,15 15,4")),
            (MarkupTool.Line, "Line", () => PathIcon("M2,15 L15,2")),
            (MarkupTool.Arrow, "Arrow", () => PathIcon("M2,15 L15,2 M8,2 L15,2 L15,9")),
            (MarkupTool.Rectangle, "Rectangle", () => PathIcon("M2,3 L15,3 L15,14 L2,14 Z")),
            (MarkupTool.Ellipse, "Ellipse", () => PathIcon("M1,8.5 A7.5,6 0 1 1 16,8.5 A7.5,6 0 1 1 1,8.5 Z")),
            (MarkupTool.TextBox, "Text box: drag or click, then type", () => new TextBlock { Text = "T", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center }),
            (MarkupTool.Note, "Note: click to add a sticky note", () => PathIcon("M2,2 L15,2 L15,11 L11,15 L2,15 Z M11,15 L11,11 L15,11")),
            (MarkupTool.Crop, "Crop: drag over the part of the page to keep", () => PathIcon("M4,1 L4,13 L16,13 M1,4 L13,4 L13,16")),
        };
        foreach (var (tool, name, icon) in tools)
        {
            var button = new ToggleButton
            {
                Content = icon(), Width = 34, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(button, name);
            AutomationProperties.SetName(button, name.Split(':')[0]);
            button.Click += (_, _) => SelectTool(tool);
            _toolButtons[tool] = button;
            MarkupBar.Children.Add(button);
        }
        MarkupBar.Children.Add(new Rectangle { Style = (Style)Root.Resources["ToolSeparator"] });

        _strokeSwatch = Swatch();
        MarkupBar.Children.Add(StyleButton("Line and text color", _strokeSwatch, flyout =>
            MarkupPalette.ColorRow(flyout, CurrentShapeStyle().Stroke, color => SetShapeStyle(s => s.ShapeStroke = MarkupPalette.SettingValue(color)))));

        _fillSwatch = Swatch();
        MarkupBar.Children.Add(StyleButton("Fill color", _fillSwatch, flyout =>
        {
            var none = new Button { Content = "No fill", HorizontalAlignment = HorizontalAlignment.Stretch };
            none.Click += (_, _) =>
            {
                flyout.Hide();
                SetShapeStyle(s => s.ShapeFill = "");
            };
            var row = MarkupPalette.ColorRow(flyout, CurrentShapeStyle().Fill ?? new MarkupColor(255, 255, 255),
                color => SetShapeStyle(s => s.ShapeFill = MarkupPalette.SettingValue(color)));
            return new StackPanel { Spacing = 8, Children = { none, row } };
        }));

        _widthLabel = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        MarkupBar.Children.Add(ChoiceButton("Line width", _widthLabel, [1, 2, 3, 5, 8], () => AppSettings.Current.ShapeLineWidth,
            value => SetShapeStyle(s => s.ShapeLineWidth = value)));
        _sizeLabel = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        MarkupBar.Children.Add(ChoiceButton("Text size", _sizeLabel, [10, 12, 14, 18, 24, 36], () => AppSettings.Current.ShapeFontSize,
            value => SetShapeStyle(s => s.ShapeFontSize = value)));

        UpdateStyleControls();
        SetMarkupBarVisible(AppSettings.Current.MarkupBarVisible);
    }

    private static Microsoft.UI.Xaml.Shapes.Path PathIcon(string data, bool filled = false)
    {
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data),
            StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 18, Height = 18, Stretch = Stretch.None,
        };
        // Take the button's text color, like the "T" tool does, so the icon follows the window's theme and
        // the checked state; Application.Current.Resources only knows the Windows theme.
        path.Loaded += (_, _) =>
        {
            DependencyObject? presenter = path;
            while (presenter is not null and not ContentPresenter) presenter = VisualTreeHelper.GetParent(presenter);
            if (presenter is null) return;
            var color = new Microsoft.UI.Xaml.Data.Binding { Source = presenter, Path = new PropertyPath("Foreground") };
            path.SetBinding(Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, color);
            if (filled) path.SetBinding(Microsoft.UI.Xaml.Shapes.Shape.FillProperty, color);
        };
        return path;
    }

    // Built from XAML so ThemeResource follows the window's theme setting.
    private static Border Swatch() => (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="16" Height="16" CornerRadius="8"
                BorderThickness="1" BorderBrush="{ThemeResource TextFillColorSecondaryBrush}" />
        """);

    /// <summary>A small drop-down whose flyout content is built each time it opens.</summary>
    private static DropDownButton StyleButton(string name, FrameworkElement icon, Func<Flyout, FrameworkElement> content)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) => flyout.Content = content(flyout);
        var button = new DropDownButton
        {
            Content = icon, Flyout = flyout, Height = 32, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static DropDownButton ChoiceButton(string name, TextBlock label, float[] values, Func<float> current, Action<float> choose)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            foreach (float value in values)
            {
                var item = new RadioMenuFlyoutItem { Text = $"{value:0} pt", GroupName = name, IsChecked = Math.Abs(current() - value) < 0.01 };
                item.Click += (_, _) => choose(value);
                menu.Items.Add(item);
            }
        };
        var button = new DropDownButton
        {
            Content = label, Flyout = menu, Height = 32, Padding = new Thickness(8, 0, 6, 0), CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }

    private void SetShapeStyle(Action<AppSettings> change)
    {
        change(AppSettings.Current);
        AppSettings.Save();
        var style = CurrentShapeStyle();
        foreach (var view in DocumentViews())
            view.Viewer.ToolStyle = style;
        UpdateStyleControls();
    }

    private void UpdateStyleControls()
    {
        var style = CurrentShapeStyle();
        static Brush Brush(MarkupColor c) => new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
        _strokeSwatch!.Background = Brush(style.Stroke);
        _fillSwatch!.Background = style.Fill is { } fill ? Brush(fill) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _fillSwatch.Child = style.Fill is null ? PathIcon("M3,13 L13,3") : null; // a slash for no fill
        _widthLabel!.Text = $"{style.LineWidth:0} pt line";
        _sizeLabel!.Text = $"{style.FontSize:0} pt text";
    }

    private void SelectTool(MarkupTool tool)
    {
        if (CurrentView is not { } view) return;
        SetHighlighterMode(false);
        // Clicking the active tool again goes back to Select.
        view.Viewer.Tool = view.Viewer.Tool == tool ? MarkupTool.Select : tool;
        UpdateToolButtons();
        if (view.Viewer.Tool is MarkupTool.Ink) ShowInfo("Draw with the mouse or a pen. Esc or Select to stop.");
    }

    private void UpdateToolButtons()
    {
        var tool = CurrentView?.Viewer.Tool ?? MarkupTool.Select;
        foreach (var (key, button) in _toolButtons)
            button.IsChecked = key == tool;
    }

    private void SetMarkupBarVisible(bool visible)
    {
        MarkupBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MarkupToggle.IsChecked = visible;
        AppSettings.Current.MarkupBarVisible = visible;
        if (!_benchmark) AppSettings.Save(); // dev runs leave your settings alone
        if (!visible && CurrentView is { } view) view.Viewer.Tool = MarkupTool.Select;
    }

    private void MarkupToggle_Click(object sender, RoutedEventArgs e) => SetMarkupBarVisible(MarkupToggle.IsChecked == true);
}
