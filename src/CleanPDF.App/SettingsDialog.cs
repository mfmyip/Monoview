using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanPDF.App;

/// <summary>The Settings dialog (⋯ > Settings, Ctrl+,). Changes apply immediately and are saved on close.</summary>
internal static class SettingsDialog
{
    private static readonly (string Key, string Label)[] ZoomChoices =
    [
        ("FitWidth", "Fit width"),
        ("FitPage", "Fit page"),
        ("ActualSize", "Actual size (100%)"),
    ];

    private static readonly (string Key, string Label)[] ThemeChoices =
    [
        ("System", "Use Windows setting"),
        ("Light", "Light"),
        ("Dark", "Dark"),
    ];

    private static ElementTheme ToElementTheme(string theme) => theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static async Task ShowAsync(XamlRoot root)
    {
        var settings = AppSettings.Current;

        var speed = new Slider { Minimum = 0.25, Maximum = 3, StepFrequency = 0.05, Value = settings.AutoScrollSpeed };
        var speedValue = ValueText();
        void ShowSpeed() => speedValue.Text = $"{speed.Value:0.00}×";
        speed.ValueChanged += (_, _) => { settings.AutoScrollSpeed = speed.Value; ShowSpeed(); };
        ShowSpeed();

        var step = new Slider { Minimum = 5, Maximum = 50, StepFrequency = 5, Value = settings.WheelZoomStep * 100 };
        var stepValue = ValueText();
        void ShowStep() => stepValue.Text = $"{step.Value:0}% per notch";
        step.ValueChanged += (_, _) => { settings.WheelZoomStep = step.Value / 100; ShowStep(); };
        ShowStep();

        var defaultZoom = new RadioButtons();
        foreach (var (_, label) in ZoomChoices) defaultZoom.Items.Add(label);
        defaultZoom.SelectedIndex = Math.Max(0, Array.FindIndex(ZoomChoices, c => c.Key == settings.DefaultZoom));
        defaultZoom.SelectionChanged += (_, _) =>
        {
            if (defaultZoom.SelectedIndex >= 0) settings.DefaultZoom = ZoomChoices[defaultZoom.SelectedIndex].Key;
        };

        var theme = new RadioButtons();
        foreach (var (_, label) in ThemeChoices) theme.Items.Add(label);
        theme.SelectedIndex = Math.Max(0, Array.FindIndex(ThemeChoices, c => c.Key == settings.Theme));

        var content = new StackPanel { Spacing = 20, MinWidth = 380 };
        content.Children.Add(Section("Theme", null, null, theme));
        content.Children.Add(Section("Middle-click scroll speed",
            "How fast the page moves when you click the mouse wheel and move the mouse.", speedValue, speed));
        content.Children.Add(Section("Ctrl + scroll zoom step",
            "How much each notch of the wheel zooms while holding Ctrl.", stepValue, step));
        content.Children.Add(Section("Zoom for newly opened PDFs", null, null, defaultZoom));

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Settings",
            Content = content,
            PrimaryButtonText = "Done",
            SecondaryButtonText = "Reset to defaults",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ToElementTheme(settings.Theme),
        };
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedIndex < 0) return;
            settings.Theme = ThemeChoices[theme.SelectedIndex].Key;
            dialog.RequestedTheme = ToElementTheme(settings.Theme); // the dialog switches along with the window
            AppSettings.RaiseChanged();
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true; // stay open so the reset values are visible
            speed.Value = AppSettings.DefaultAutoScrollSpeed;
            step.Value = AppSettings.DefaultWheelZoomStep * 100;
            defaultZoom.SelectedIndex = 0;
            theme.SelectedIndex = 0;
        };

        await dialog.ShowAsync();
        AppSettings.Save();
    }

    private static TextBlock ValueText() => new() { Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right };

    private static StackPanel Section(string title, string? description, TextBlock? value, UIElement control)
    {
        var header = new Grid();
        header.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        if (value is not null) header.Children.Add(value);

        var section = new StackPanel { Spacing = 4 };
        section.Children.Add(header);
        if (description is not null)
            section.Children.Add(new TextBlock
            {
                Text = description,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
        section.Children.Add(control);
        return section;
    }
}
