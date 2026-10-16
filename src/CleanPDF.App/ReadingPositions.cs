using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanPDF.App;

/// <summary>The last page read in each file, kept in %LOCALAPPDATA%\CleanPDF\positions.json.</summary>
internal static class ReadingPositions
{
    private const int Max = 300;
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanPDF", "positions.json");

    private static Dictionary<string, PositionEntry>? _entries;

    /// <summary>0-based page, or null if the file has no saved position.</summary>
    public static int? Get(string path) =>
        (_entries ??= Load()).TryGetValue(Key(path), out var entry) ? entry.Page : null;

    public static void Set(string path, int page)
    {
        var entries = _entries ??= Load();
        entries[Key(path)] = new PositionEntry(page, DateTime.UtcNow);
        // Forget the oldest files once the list is long.
        if (entries.Count > Max)
            foreach (var old in entries.OrderBy(e => e.Value.Saved).Take(entries.Count - Max).ToList())
                entries.Remove(old.Key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(entries, PositionsJson.Default.DictionaryStringPositionEntry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Key(string path) => Path.GetFullPath(path).ToUpperInvariant();

    private static Dictionary<string, PositionEntry> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize(File.ReadAllText(StorePath), PositionsJson.Default.DictionaryStringPositionEntry) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return [];
    }
}

internal sealed record PositionEntry(int Page, DateTime Saved);

[JsonSerializable(typeof(Dictionary<string, PositionEntry>))]
internal sealed partial class PositionsJson : JsonSerializerContext;
