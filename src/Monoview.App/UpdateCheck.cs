using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monoview.App;

/// <summary>
/// Asks GitHub, at most once a day, whether a newer release is out. The Store build skips this:
/// the Store updates it.
/// </summary>
internal static class UpdateCheck
{
    private const string LatestRelease = "https://api.github.com/repos/mfmyip/Monoview/releases/latest";

    public static async Task<(string Version, string Url)?> FindNewerAsync()
    {
        var settings = AppSettings.Current;
        if (AppRegistration.IsPackaged || !settings.UpdateChecksOn || DateTime.UtcNow - settings.LastUpdateCheck < TimeSpan.FromDays(1))
            return null;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Monoview"); // GitHub rejects requests without one
            var release = JsonSerializer.Deserialize(await http.GetStringAsync(LatestRelease), ReleaseJson.Default.Release);
            settings.LastUpdateCheck = DateTime.UtcNow;
            AppSettings.Save();

            string current = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "";
            if (release is not null && Version.TryParse(release.TagName.TrimStart('v'), out var latest)
                && Version.TryParse(current, out var running) && latest > running)
                return (latest.ToString(), release.HtmlUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
        }
        return null;
    }

    internal sealed record Release(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl);
}

[JsonSerializable(typeof(UpdateCheck.Release))]
internal sealed partial class ReleaseJson : JsonSerializerContext;
