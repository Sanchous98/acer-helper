using System.Net.Http;
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
        // The running version is a compile-time constant baked from the csproj <Version> (AppInfo.Version,
        // written by the GenerateAppVersion MSBuild target). We deliberately do NOT read it from
        // Assembly.GetName().Version: Native AOT (the shipped build) strips assembly-version reflection
        // metadata and returns 0.0.0.0, so every release compared as newer and the app kept offering an
        // "update" to the version already installed. Parse current and the tag through the SAME helper so the
        // component counts match (Version("0.20.0") has Revision -1) and equal versions compare equal.
        if (!TryParseVersion(AppInfo.Version, out var current, out var currentIsPre)) return null;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AcerHelper-update-check");   // GitHub API requires a UA
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json = await http.GetStringAsync(LatestReleaseApi, ct);
            var release = JsonSerializer.Deserialize(json, GithubJsonContext.Default.GithubRelease);
            if (release?.TagName == null || string.IsNullOrEmpty(release.HtmlUrl)) return null;
            if (!TryParseVersion(release.TagName, out var latest, out var latestIsPre)) return null;
            // Numerically newer wins outright; numerically EQUAL is an update only when it graduates a
            // prerelease to the final build ("0.21.0-beta" running, "v0.21.0" released) — the suffix is
            // dropped by the numeric parse, so without this a beta user would never see the matching final.
            if (!(latest > current || (latest == current && currentIsPre && !latestIsPre))) return null;

            var assets = (release.Assets ?? [])
                .Where(a => a.Name != null && a.BrowserDownloadUrl != null)
                .Select(a => new ReleaseAsset(a.Name!, a.BrowserDownloadUrl!))
                .ToList();
            // Display the human-authored tag ("0.21.0", maybe "-beta"), not the 4-component normalized Version
            // ("0.21.0.0"); this string is only ever shown in the "Update available: v{0}" banner/tray text.
            return new UpdateInfo(release.TagName.TrimStart('v', 'V'), release.HtmlUrl, assets);
        }
        catch { return null; }
    }

    // Accepts release tags ("v0.15.0") and the bare app version ("0.15.0"), maybe with a "-beta" suffix ->
    // take the leading numeric part, reporting whether a prerelease suffix followed it (semver orders
    // 0.21.0-beta BEFORE 0.21.0, so the caller must not collapse them into "equal"). Used for BOTH the
    // running version and the latest tag so they parse to the same shape.
    private static bool TryParseVersion(string tag, out Version version, out bool prerelease)
    {
        var s = tag.TrimStart('v', 'V');
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        prerelease = end < s.Length;   // anything after the numeric part ("-beta", "-rc1") marks a prerelease
        if (!Version.TryParse(s[..end], out var v)) { version = null!; return false; }
        // Normalize to 4 fully-specified components (unspecified -> 0) so the app version and the tag compare
        // purely on value regardless of how many parts each was written with: Version treats an unspecified
        // component as -1, so without this "0.20" and "0.20.0.0" would compare unequal to "0.20.0".
        version = new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build, v.Revision < 0 ? 0 : v.Revision);
        return true;
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
