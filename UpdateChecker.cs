using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcerHelper;

/// <summary>Checks GitHub Releases for a newer version and returns it (version + page URL + downloadable
/// assets). It does NOT download or install — that's delegated to the platform self-updaters
/// (<see cref="WindowsUpdater"/> for the MSI, <see cref="AppImageUpdater"/> for the Linux AppImage), which
/// pick the matching asset off <see cref="UpdateInfo.Assets"/>; when neither applies the UI opens the page.
/// Any failure (offline, rate-limited, no releases yet, parse error) degrades silently to "no update". Runs
/// off the UI thread; a fresh <see cref="HttpClient"/> per check (called once at startup, so cost is
/// irrelevant).</summary>
public sealed class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Sanchous98/acer-helper/releases/latest";

    /// <summary>The newer release, or null if we're already current / couldn't check.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var current = Assembly.GetEntryAssembly()?.GetName().Version
                      ?? Assembly.GetExecutingAssembly().GetName().Version;
        if (current == null) return null;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AcerHelper-update-check");   // GitHub API requires a UA
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json = await http.GetStringAsync(LatestReleaseApi, ct);
            var release = JsonSerializer.Deserialize(json, GithubJsonContext.Default.GithubRelease);
            if (release?.TagName == null || string.IsNullOrEmpty(release.HtmlUrl)) return null;
            if (!TryParseTag(release.TagName, out var latest) || latest <= current) return null;

            var assets = (release.Assets ?? [])
                .Where(a => a.Name != null && a.BrowserDownloadUrl != null)
                .Select(a => new ReleaseAsset(a.Name!, a.BrowserDownloadUrl!))
                .ToList();
            return new UpdateInfo(latest.ToString(), release.HtmlUrl, assets);
        }
        catch { return null; }
    }

    // Tags look like "v0.15.0" or "0.15.0" (maybe with a "-beta" suffix) -> take the leading numeric part.
    private static bool TryParseTag(string tag, out Version version)
    {
        var s = tag.TrimStart('v', 'V');
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        return Version.TryParse(s[..end], out version!);
    }
}

/// <summary>A newer release: its version, the release page URL (fallback), and its downloadable assets
/// (used to self-replace the AppImage on Linux; see <see cref="AppImageUpdater"/>).</summary>
public sealed record UpdateInfo(string Version, string Url, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>One release asset: file name + direct download URL.</summary>
public sealed record ReleaseAsset(string Name, string Url);

/// <summary>The slice of the GitHub "latest release" JSON we read.</summary>
public sealed class GithubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("assets")] public List<GithubAsset>? Assets { get; set; }
}

public sealed class GithubAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
}

// Source-generated so it works under Native AOT (no reflection-based serialization).
[JsonSerializable(typeof(GithubRelease))]
internal partial class GithubJsonContext : JsonSerializerContext;
