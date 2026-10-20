using System.Text.Json;
using Microsoft.Win32;
using System.Text.Json.Serialization;

namespace Monoview.App;

/// <summary>User preferences, kept in %LOCALAPPDATA%\Monoview\settings.json.</summary>
public sealed class AppSettings
{
    public const double DefaultAutoScrollSpeed = 1.3;
    public const double DefaultWheelZoomStep = 0.2;

    /// <summary>Middle-click autoscroll speed multiplier (1 = the original speed).</summary>
    public double AutoScrollSpeed { get; set; } = DefaultAutoScrollSpeed;

    /// <summary>Zoom change per Ctrl+wheel notch, e.g. 0.2 = 20%.</summary>
    public double WheelZoomStep { get; set; } = DefaultWheelZoomStep;

    /// <summary>Zoom for newly opened documents: "FitWidth", "FitPage" or "ActualSize".</summary>
    public string DefaultZoom { get; set; } = "FitWidth";

    /// <summary>"System" (follow Windows' app mode), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Raised when a setting that needs re-applying (the theme) changes.</summary>
    public static event EventHandler? Changed;

    public static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);

    /// <summary>Search options, set from the Aa button next to the search box.</summary>
    public bool SearchMatchCase { get; set; }
    public bool SearchWholeWord { get; set; }

    /// <summary>Markup style and color from the highlighter button: "Highlight", "Underline" or "StrikeOut"; a palette color name.</summary>
    public string MarkupKind { get; set; } = "Highlight";
    public string MarkupColor { get; set; } = "Yellow";

    /// <summary>Style for the markup bar's shapes, ink and text boxes; colors as palette names or "#RRGGBB", no fill when empty.</summary>
    public string ShapeStroke { get; set; } = "Red";
    public string ShapeFill { get; set; } = "";
    public float ShapeLineWidth { get; set; } = 2;
    public float ShapeFontSize { get; set; } = 14;

    /// <summary>Whether the markup bar, the second toolbar row, is shown.</summary>
    public bool MarkupBarVisible { get; set; }

    /// <summary>Reopen each file at the page where it was closed.</summary>
    public bool RememberLastPage { get; set; } = true;

    /// <summary>Check GitHub for a newer release at launch; null until the user sets it in Settings.</summary>
    public bool? CheckForUpdates { get; set; }

    public DateTime LastUpdateCheck { get; set; }

    /// <summary>The Settings choice if made, else the MSI's checkbox, else on, as in the zip build.</summary>
    [JsonIgnore]
    public bool UpdateChecksOn => CheckForUpdates
        ?? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Monoview\Installer", "CheckForUpdates", 1) is not 0;

    /// <summary>Sidebar width in DIPs, set by dragging its edge.</summary>
    public double SidebarWidth { get; set; } = 220;

    // The minimum fits the Pages, Outline and Search tabs without clipping.
    public static double ClampSidebarWidth(double width) => Math.Clamp(width, 216, 520);

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Monoview", "settings.json");

    private static AppSettings? _current;

    public static AppSettings Current => _current ??= Load();

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Current, SettingsJson.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void ResetToDefaults() => _current = new AppSettings();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(StorePath), SettingsJson.Default.AppSettings);
                if (loaded is not null)
                {
                    // Keep hand-edited values in a sane range.
                    loaded.AutoScrollSpeed = Math.Clamp(loaded.AutoScrollSpeed, 0.25, 4);
                    loaded.WheelZoomStep = Math.Clamp(loaded.WheelZoomStep, 0.05, 0.5);
                    loaded.SidebarWidth = ClampSidebarWidth(loaded.SidebarWidth);
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class SettingsJson : JsonSerializerContext;
