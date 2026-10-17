using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanPDF.App;

/// <summary>Most-recently-opened files, kept in %LOCALAPPDATA%\CleanPDF\recent.json.</summary>
internal static class RecentFiles
{
    private const int Max = 15;
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanPDF", "recent.json");

    private static List<string>? _items;

    public static IReadOnlyList<string> Items => _items ??= Load();

    public static void Add(string path)
    {
        var items = _items ??= Load();
        items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        items.Insert(0, path);
        if (items.Count > Max) items.RemoveRange(Max, items.Count - Max);
        Save(items);
    }

    public static void Remove(string path)
    {
        var items = _items ??= Load();
        if (items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
            Save(items);
    }

    public static void Clear()
    {
        _items = [];
        Save(_items);
    }

    private static List<string> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize(File.ReadAllText(StorePath), RecentJson.Default.ListString) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return [];
    }

    private static void Save(List<string> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(items, RecentJson.Default.ListString));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSerializable(typeof(List<string>))]
internal sealed partial class RecentJson : JsonSerializerContext;
