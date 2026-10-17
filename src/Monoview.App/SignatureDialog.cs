using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Monoview.App.Viewer;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;

namespace Monoview.App;

/// <summary>
/// Makes a signature, like Preview: draw it with the mouse or a pen, type your name in a handwriting font, or use a
/// picture of it. The result is saved with a transparent background. Returns it, or null when cancelled.
/// </summary>
internal static class SignatureDialog
{
    private static readonly SolidColorBrush Ink = new(Windows.UI.Color.FromArgb(255, 0x1B, 0x2A, 0x5C));

    public static async Task<StampPicture?> ShowAsync(XamlRoot root, IntPtr hwnd, string? typedName = null)
    {
        const double PadWidth = 480, PadHeight = 160;

        // Draw: strokes on a transparent canvas, so only the ink is kept.
        var strokes = new Canvas { Width = PadWidth, Height = PadHeight, Background = new SolidColorBrush(Colors.Transparent) };
        Polyline? stroke = null;
        strokes.PointerPressed += (_, e) =>
        {
            stroke = new Polyline
            {
                Stroke = Ink, StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            stroke.Points.Add(e.GetCurrentPoint(strokes).Position);
            strokes.Children.Add(stroke);
            strokes.CapturePointer(e.Pointer);
        };
        strokes.PointerMoved += (_, e) => stroke?.Points.Add(e.GetCurrentPoint(strokes).Position);
        strokes.PointerReleased += (_, e) =>
        {
            stroke = null;
            strokes.ReleasePointerCapture(e.Pointer);
        };

        // Type: the name in a handwriting font.
        var name = new TextBox { PlaceholderText = "Type your name", Text = typedName ?? "" };
        var typed = new TextBlock
        {
            FontFamily = new FontFamily("Segoe Script"), FontSize = 44, Foreground = Ink,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        typed.Text = name.Text;
        name.TextChanged += (_, _) => typed.Text = name.Text;

        // Picture: a photo or scan; its paper turns transparent.
        StampPicture? fromFile = null;
        var preview = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
        var choose = new Button { Content = "Choose a picture…" };

        var pad = new Border
        {
            Width = PadWidth, Height = PadHeight, Background = new SolidColorBrush(Colors.White),
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0, 0, 0)),
        };
        var hint = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var tabs = new SelectorBar();
        foreach (var label in new[] { "Draw", "Type", "Picture" })
            tabs.Items.Add(new SelectorBarItem { Text = label });
        var extra = new ContentPresenter();

        void Show(int tab)
        {
            pad.Child = tab switch { 0 => strokes, 1 => typed, _ => preview };
            extra.Content = tab switch { 1 => name, 2 => choose, _ => null };
            hint.Text = tab switch
            {
                0 => "Sign in the box with your mouse, pen or finger.",
                1 => "Your name appears in a handwriting font.",
                _ => "Use a photo or scan of your signature on white paper. The paper becomes transparent.",
            };
        }
        tabs.SelectionChanged += (_, _) => Show(tabs.Items.IndexOf(tabs.SelectedItem));
        tabs.SelectedItem = tabs.Items[typedName is null ? 0 : 1];
        Show(typedName is null ? 0 : 1);

        choose.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var ext in DocumentFiles.ImageExtensions) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            if (await picker.PickSingleFileAsync() is not { } file) return;
            fromFile = Signatures.Trim(PaperToTransparent(await Signatures.LoadAsync(file.Path)));
            if (fromFile is null) return;
            var bitmap = new WriteableBitmap(fromFile.Width, fromFile.Height);
            System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.CopyTo(Premultiply(fromFile.Bgra), bitmap.PixelBuffer);
            bitmap.Invalidate();
            preview.Source = bitmap;
        };

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "New signature",
            Content = new StackPanel { Spacing = 12, Children = { tabs, pad, extra, hint } },
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        dialog.SecondaryButtonClick += (_, e) =>
        {
            e.Cancel = true; // Clear empties the current tab and keeps the dialog open
            strokes.Children.Clear();
            name.Text = "";
            fromFile = null;
            preview.Source = null;
        };

        StampPicture? result = null;
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                int tab = tabs.Items.IndexOf(tabs.SelectedItem);
                result = tab == 2 ? fromFile : await RenderAsync(tab == 0 ? strokes : typed);
                if (result is null) e.Cancel = true; // nothing drawn, typed or chosen yet
            }
            finally
            {
                deferral.Complete();
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || result is null) return null;
        await Signatures.SaveAsync(result);
        return result;
    }

    /// <summary>The element's pixels, transparent where nothing is drawn, trimmed to the ink.</summary>
    private static async Task<StampPicture?> RenderAsync(FrameworkElement element)
    {
        if (element.ActualWidth < 1) return null;
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element);
        var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(await rtb.GetPixelsAsync());
        // The renderer gives premultiplied alpha; pictures are kept straight.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a is 0 or 255) continue;
            for (int c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Min(255, pixels[i + c] * 255 / a);
        }
        return Signatures.Trim(new StampPicture(rtb.PixelWidth, rtb.PixelHeight, pixels));
    }

    /// <summary>Light paper becomes transparent, fading in over the grey range so ink edges stay smooth.</summary>
    private static StampPicture PaperToTransparent(StampPicture picture)
    {
        var bgra = (byte[])picture.Bgra.Clone();
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int light = (bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3;
            int alpha = light >= 220 ? 0 : light <= 150 ? 255 : (220 - light) * 255 / 70;
            bgra[i + 3] = (byte)Math.Min(bgra[i + 3], alpha);
        }
        return picture with { Bgra = bgra };
    }

    private static byte[] Premultiply(byte[] straight)
    {
        var bgra = (byte[])straight.Clone();
        for (int i = 0; i < bgra.Length; i += 4)
            for (int c = 0; c < 3; c++) bgra[i + c] = (byte)(bgra[i + c] * bgra[i + 3] / 255);
        return bgra;
    }
}
