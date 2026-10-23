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
/// The markup bar, Preview's second toolbar row: drawing tools, then Style (line width), Border, Fill and Aa (text).
/// The style buttons edit the selected object, or else set the style of the next object the tool makes; a button
/// that doesn't apply is greyed out. Ctrl+Shift+A or the Markup button shows and hides the bar.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly MarkupColor Black = new(0, 0, 0);
    private static readonly float[] LineWidths = [1, 2, 3, 4, 6, 8, 12];

    private readonly Dictionary<MarkupTool, ToggleButton> _toolButtons = new();
    private DropDownButton? _styleButton, _borderButton, _fillButton, _textButton;
    private Rectangle? _borderIcon, _fillIcon, _textBar;
    private Microsoft.UI.Xaml.Shapes.Path? _borderSlash, _fillSlash;
    private TextBlock? _widthLabel, _borderLabel, _fillLabel, _contextLabel;

    /// <summary>Rectangles and ellipses share one style; every other kind has its own.</summary>
    private static ShapeKind StyleGroup(ShapeKind kind) => kind == ShapeKind.Ellipse ? ShapeKind.Rectangle : kind;

    /// <summary>The style of the next object of <paramref name="kind"/>.</summary>
    private static ShapeStyle ToolStyle(ShapeKind kind) =>
        AppSettings.Current.ShapeStyles.TryGetValue(StyleGroup(kind), out var style) ? style : StyleGroup(kind) switch
        {
            ShapeKind.TextBox => new ShapeStyle(null, null, 1, 14, Black),
            ShapeKind.Note => new ShapeStyle(null, new MarkupColor(255, 210, 0)),
            _ => new ShapeStyle(new MarkupColor(220, 40, 40)),
        };

    /// <summary>What the style buttons edit: the selected object, else the next one the tool makes. Null when neither.</summary>
    private (ShapeKind Kind, ShapeStyle Style, bool Selected)? StyleContext()
    {
        if (CurrentView?.Viewer is not { } viewer) return null;
        if (viewer.Selected is { } shape)
            return shape.Kind == ShapeKind.Stamp ? null : (shape.Kind, shape.Style ?? ToolStyle(shape.Kind), true);
        return PdfViewer.KindOf(viewer.Tool) is { } kind ? (kind, ToolStyle(kind), false) : null;
    }

    private static bool HasLine(ShapeKind kind) => kind is ShapeKind.Ink or ShapeKind.Rectangle or ShapeKind.Ellipse or ShapeKind.TextBox;
    private static bool HasFill(ShapeKind kind) => kind is ShapeKind.Rectangle or ShapeKind.Ellipse or ShapeKind.TextBox or ShapeKind.Note;

    private void ChangeStyle(Func<ShapeStyle, ShapeStyle> change)
    {
        if (StyleContext() is not { } context) return;
        var style = change(context.Style);
        if (context.Selected)
        {
            _ = CurrentView!.Viewer.RestyleSelectedAsync(style); // the bar updates when the new object is selected
            return;
        }
        AppSettings.Current.ShapeStyles[StyleGroup(context.Kind)] = style;
        AppSettings.Save();
        UpdateStyleControls();
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

        // Style: three lines of growing width, and the current width.
        var lines = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        foreach (double h in new[] { 1.0, 2, 3 })
        {
            var bar = new Rectangle { Width = 14, Height = h, RadiusX = 0.5, RadiusY = 0.5 };
            TakeButtonForeground(bar, stroke: false, fill: true);
            lines.Children.Add(bar);
        }
        _widthLabel = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var widthMenu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        widthMenu.Opening += (_, _) => FillWidthMenu(widthMenu);
        _styleButton = BarButton("Line width", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { lines, _widthLabel } }, widthMenu);

        // Border: a hollow square in the border color. Fill: a solid one. A red slash means none.
        (_borderIcon, _borderSlash) = (new Rectangle { Width = 13, Height = 13, RadiusX = 2.5, RadiusY = 2.5, StrokeThickness = 2.5 }, Slash());
        _borderLabel = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _borderButton = BarButton("Border color", IconAndLabel(_borderIcon, _borderSlash, _borderLabel),
            PanelFlyout(flyout => StyleContext() is { } c
                ? MarkupPalette.ColorPanel(flyout, c.Style.Stroke, hasNone: c.Kind != ShapeKind.Ink, color => ChangeStyle(s => s with { Stroke = color }))
                : new Grid()));

        (_fillIcon, _fillSlash) = (new Rectangle { Width = 14, Height = 14, RadiusX = 2.5, RadiusY = 2.5, StrokeThickness = 1 }, Slash());
        _fillIcon.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 138, 138, 138));
        _fillLabel = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _fillButton = BarButton("Fill color", IconAndLabel(_fillIcon, _fillSlash, _fillLabel),
            PanelFlyout(flyout => StyleContext() is { } c
                ? MarkupPalette.ColorPanel(flyout, c.Style.Fill, hasNone: c.Kind != ShapeKind.Note, color => ChangeStyle(s => s with { Fill = color }))
                : new Grid()));

        // Aa: font, size and text color.
        _textBar = new Rectangle { Width = 16, Height = 3, RadiusX = 1, RadiusY = 1 };
        var aa = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = "Aa", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center }, _textBar } };
        _textButton = BarButton("Font, size and text color", aa, PanelFlyout(TextPanel));

        foreach (var button in new[] { _styleButton, _borderButton, _fillButton, _textButton })
            MarkupBar.Children.Add(button);
        _contextLabel = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.7 };
        MarkupBar.Children.Add(_contextLabel);

        UpdateStyleControls();
        SetMarkupBarVisible(AppSettings.Current.MarkupBarVisible);
    }

    private static Microsoft.UI.Xaml.Shapes.Path Slash() => new()
    {
        Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), "M2,16 L16,2"),
        Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28)), StrokeThickness = 1.5, Width = 18, Height = 18,
    };

    private static StackPanel IconAndLabel(Rectangle icon, Microsoft.UI.Xaml.Shapes.Path slash, TextBlock label) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children = { new Grid { Width = 18, Height = 18, Children = { icon, slash } }, label },
    };

    private void FillWidthMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        if (StyleContext() is not { } context) return;
        bool none = context.Style.Stroke is null;
        if (none) menu.Items.Add(new MenuFlyoutItem { Text = "Pick a border color to set a width", IsEnabled = false });
        foreach (float width in LineWidths)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = $"{width:0} pt", GroupName = "Line width", IsEnabled = !none,
                IsChecked = !none && Math.Abs(context.Style.LineWidth - width) < 0.01,
                Icon = new PathIcon { Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), FormattableString.Invariant($"M0,{8 - width / 2} H16 V{8 + width / 2} H0 Z")) },
            };
            item.Click += (_, _) => ChangeStyle(s => s with { LineWidth = width });
            menu.Items.Add(item);
        }
    }

    /// <summary>The Aa flyout: font, size, and a text color button that swaps in the color panel.</summary>
    private FrameworkElement TextPanel(Flyout flyout)
    {
        if (StyleContext() is not { Kind: ShapeKind.TextBox } context) return new Grid();
        var style = context.Style;
        var fonts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (font, label, family) in new[] { (TextFont.Helvetica, "Sans", "Arial"), (TextFont.Times, "Serif", "Times New Roman"), (TextFont.Courier, "Mono", "Courier New") })
        {
            var button = new ToggleButton { Content = label, FontFamily = new FontFamily(family), IsChecked = style.Font == font, MinWidth = 64 };
            AutomationProperties.SetName(button, font.ToString());
            button.Click += (_, _) =>
            {
                foreach (var other in fonts.Children.OfType<ToggleButton>()) other.IsChecked = ReferenceEquals(other, button);
                ChangeStyle(s => s with { Font = font });
            };
            fonts.Children.Add(button);
        }
        var size = new NumberBox
        {
            Header = "Size (pt)", Value = style.FontSize, Minimum = 6, Maximum = 96, SmallChange = 1, LargeChange = 4,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 140, HorizontalAlignment = HorizontalAlignment.Left,
        };
        size.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue) && Math.Abs(e.NewValue - e.OldValue) > 0.01) ChangeStyle(s => s with { FontSize = (float)e.NewValue });
        };
        var swatch = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), Background = MarkupPalette.Brush(style.TextColor ?? Black) };
        var color = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { swatch, new TextBlock { Text = "Text color" } } } };
        color.Click += (_, _) => flyout.Content = MarkupPalette.ColorPanel(flyout, style.TextColor ?? Black, hasNone: false, c => ChangeStyle(s => s with { TextColor = c }));
        return new StackPanel { Spacing = 12, Width = 216, Children = { fonts, size, color } };
    }

    private static Flyout PanelFlyout(Func<Flyout, FrameworkElement> content)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) => flyout.Content = content(flyout);
        return flyout;
    }

    private static DropDownButton BarButton(string name, FrameworkElement content, FlyoutBase flyout)
    {
        var button = new DropDownButton
        {
            Content = content, Flyout = flyout, Height = 32, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
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
        TakeButtonForeground(path, stroke: true, fill: filled);
        return path;
    }

    /// <summary>
    /// Draws <paramref name="shape"/> in its button's text color, like the "T" tool, so it follows the window's theme,
    /// the checked state and the greyed-out state; Application.Current.Resources only knows the Windows theme.
    /// </summary>
    private static void TakeButtonForeground(Microsoft.UI.Xaml.Shapes.Shape shape, bool stroke, bool fill)
    {
        shape.Loaded += (_, _) =>
        {
            DependencyObject? presenter = shape;
            while (presenter is not null and not ContentPresenter) presenter = VisualTreeHelper.GetParent(presenter);
            if (presenter is null) return;
            var color = new Microsoft.UI.Xaml.Data.Binding { Source = presenter, Path = new PropertyPath("Foreground") };
            if (stroke) shape.SetBinding(Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, color);
            if (fill) shape.SetBinding(Microsoft.UI.Xaml.Shapes.Shape.FillProperty, color);
        };
    }

    private void UpdateStyleControls()
    {
        if (_styleButton is null) return;
        var context = StyleContext();
        var kind = context?.Kind;
        var style = context?.Style;
        bool line = kind is { } k1 && HasLine(k1), fill = kind is { } k2 && HasFill(k2), text = kind == ShapeKind.TextBox;
        _styleButton.IsEnabled = _borderButton!.IsEnabled = line;
        _fillButton!.IsEnabled = fill;
        _textButton!.IsEnabled = text;
        var off = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 160, 160, 160));
        _widthLabel!.Text = line && style!.Stroke is not null ? $"{style.LineWidth:0.#} pt" : "";
        _borderIcon!.Stroke = line && style!.Stroke is { } stroke ? MarkupPalette.Brush(stroke) : off;
        _borderSlash!.Visibility = line && style!.Stroke is null ? Visibility.Visible : Visibility.Collapsed;
        _borderLabel!.Text = kind == ShapeKind.Ink ? "Line" : "Border";
        _fillIcon!.Fill = fill && style!.Fill is { } f ? MarkupPalette.Brush(f) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _fillSlash!.Visibility = fill && style!.Fill is null ? Visibility.Visible : Visibility.Collapsed;
        _fillLabel!.Text = kind == ShapeKind.Note ? "Color" : "Fill";
        _textBar!.Fill = text ? MarkupPalette.Brush(style!.TextColor ?? Black) : off;
        string noun = kind switch
        {
            ShapeKind.Ink => "line", ShapeKind.Rectangle => "rectangle", ShapeKind.Ellipse => "ellipse",
            ShapeKind.TextBox => "text box", ShapeKind.Note => "note", _ => "",
        };
        _contextLabel!.Text = context is null ? "" : context.Value.Selected ? $"Selected {noun}" : $"New {noun}s";
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
        UpdateStyleControls();
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
