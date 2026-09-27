using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure;

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

    // The one GitHub call this type makes, behind a seam so a test can drive the parse and the version gate
    // over a JSON fixture without touching the network. Production is DefaultFetchAsync (a fresh HttpClient per
    // check, once at startup and once per UpdateSchedule tick, so the cost is irrelevant).
    private readonly Func<CancellationToken, Task<string?>> _fetchLatestReleaseJson;

    /// <summary>The production checker: one GitHub "latest release" call per check.</summary>
    public UpdateChecker() : this(DefaultFetchAsync) { }

    /// <summary>A checker over an INJECTED fetch, for the tests: it returns the release JSON body, or null on
    /// any failure. The seam is the HTTP boundary only — the parse, the asset projection and the version gate
    /// above it are the same code the shipped build runs, so a fixture exercises the real decision.</summary>
    internal UpdateChecker(Func<CancellationToken, Task<string?>> fetchLatestReleaseJson)
        => _fetchLatestReleaseJson = fetchLatestReleaseJson ?? throw new ArgumentNullException(nameof(fetchLatestReleaseJson));

    /// <summary>
    /// The release's assets (name + url), WHENEVER the latest-release call succeeds — WITHOUT the app-version
    /// comparison (docs/vendor-plugins.md §5.2/§5.3). <see cref="CheckAsync"/> returns null unless the app
    /// itself is out of date, but the runtime plugin check needs the release the app is already told about even
    /// when the app is current; this is that un-gated half. Null when the call fails, the body is not a
    /// release, or the release is malformed — the same silent degradation as <see cref="CheckAsync"/>.
    ///
    /// It is NOT a second network path: it shares <see cref="CheckDetailedAsync"/> with <see cref="CheckAsync"/>,
    /// so both read the release through one parse and one asset projection (§5.2). The app's own tick calls
    /// <see cref="CheckDetailedAsync"/> directly, so the app check and the plugin check are ONE GitHub call.
    /// </summary>
    public async Task<IReadOnlyList<ReleaseAsset>?> FetchLatestReleaseAsync(CancellationToken ct = default)
        => (await CheckDetailedAsync(ct).ConfigureAwait(false)).Assets;

    /// <summary>The newer release, or null if we're already current / couldn't check.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
        => (await CheckDetailedAsync(ct).ConfigureAwait(false)).Info;

    /// <summary>
    /// ONE update check, returning BOTH things a single tick needs (docs/vendor-plugins.md §5.2: the plugin path
    /// "simply asks for <c>vendor-plugins.json</c> among those assets instead of adding a second GitHub API
    /// call"): <see cref="ReleaseCheck.Info"/> is exactly what <see cref="CheckAsync"/> returns (the app's newer
    /// release, or null when current), and <see cref="ReleaseCheck.Assets"/> is exactly what
    /// <see cref="FetchLatestReleaseAsync"/> returns (the release's assets, un-gated). Both come from ONE fetch
    /// and ONE parse, so the app tick can drive both halves without a second network round-trip.
    ///
    /// The version comparison runs only to decide <see cref="ReleaseCheck.Info"/>; <see cref="ReleaseCheck.Assets"/>
    /// is returned on every successful fetch, which is the un-gated property the plugin check depends on.
    /// </summary>
    internal async Task<ReleaseCheck> CheckDetailedAsync(CancellationToken ct = default)
    {
        // ONE network path (§5.2): a single raw fetch, then the app-only version gate on top of it.
        var release = await FetchLatestReleaseRawAsync(ct).ConfigureAwait(false);
        if (release is null) return new ReleaseCheck(null, null);

        var assets = release.Assets;

        // The running version is a compile-time constant baked from the csproj <Version> (AppInfo.Version,
        // written by the GenerateAppVersion MSBuild target). We deliberately do NOT read it from
        // Assembly.GetName().Version: Native AOT (the shipped build) strips assembly-version reflection
        // metadata and returns 0.0.0.0, so every release compared as newer and the app kept offering an
        // "update" to the version already installed. Parse current and the tag through the SAME helper so the
        // component counts match (Version("0.20.0") has Revision -1) and equal versions compare equal.
        if (!TryParseVersion(AppInfo.Version, out var current, out var currentIsPre)) return new ReleaseCheck(null, assets);
        if (!TryParseVersion(release.TagName, out var latest, out var latestIsPre)) return new ReleaseCheck(null, assets);
        // Numerically newer wins outright; numerically EQUAL is an update only when it graduates a
        // prerelease to the final build ("0.21.0-beta" running, "v0.21.0" released) — the suffix is
        // dropped by the numeric parse, so without this a beta user would never see the matching final.
        if (!(latest > current || (latest == current && currentIsPre && !latestIsPre)))
            return new ReleaseCheck(null, assets);

        // Display the human-authored tag ("0.21.0", maybe "-beta"), not the 4-component normalized Version
        // ("0.21.0.0"); this string is only ever shown in the "Update available: v{0}" banner/tray text.
        // The body is the release's changelog, shown (unexpanded) in the notification's detail area.
        return new ReleaseCheck(
            new UpdateInfo(release.TagName.TrimStart('v', 'V'), release.HtmlUrl, assets, release.Body),
            assets);
    }

    /// <summary>
    /// The SHARED, un-gated fetch behind <see cref="CheckDetailedAsync"/> (§5.2): the latest release with its
    /// assets projected to <see cref="ReleaseAsset"/>, or null on any failure (offline, rate-limited, no
    /// releases, non-JSON, a malformed body). The version comparison is deliberately NOT here — it is app-update
    /// policy and lives in <see cref="CheckDetailedAsync"/>; the plugin path needs the release even when the app
    /// is current.
    /// </summary>
    private async Task<LatestRelease?> FetchLatestReleaseRawAsync(CancellationToken ct)
    {
        var json = await _fetchLatestReleaseJson(ct).ConfigureAwait(false);
        if (json is null) return null;

        try
        {
            var release = JsonSerializer.Deserialize(json, GithubJsonContext.Default.GithubRelease);
            if (release?.TagName == null || string.IsNullOrEmpty(release.HtmlUrl)) return null;

            var assets = (release.Assets ?? [])
                .Where(a => a.Name != null && a.BrowserDownloadUrl != null)
                .Select(a => new ReleaseAsset(a.Name!, a.BrowserDownloadUrl!))
                .ToList();
            return new LatestRelease(release.TagName, release.HtmlUrl, assets, release.Body);
        }
        catch { return null; }
    }

    /// <summary>The production fetch: a fresh <see cref="HttpClient"/> with a 10 s timeout and the UA header
    /// GitHub requires, folded to null on every failure so the caller sees "no release" not an exception.</summary>
    private static async Task<string?> DefaultFetchAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AcerHelper-update-check");   // GitHub API requires a UA
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return await http.GetStringAsync(LatestReleaseApi, ct).ConfigureAwait(false);
        }
        catch { return null; }
    }

    /// <summary>The parsed "latest release" before the app-version gate: the tag, page URL, projected assets and
    /// the changelog. Produced by <see cref="FetchLatestReleaseRawAsync"/> for <see cref="CheckDetailedAsync"/>.</summary>
    private sealed record LatestRelease(
        string TagName, string HtmlUrl, IReadOnlyList<ReleaseAsset> Assets, string? Body);

    /// <summary>Both halves of one update check (§5.2): the app's newer release (null when current/offline) and
    /// the release's assets (null only when the fetch itself failed). A record struct because it is a small
    /// transient result; <see cref="CheckDetailedAsync"/> is the one place the two are produced from one fetch.</summary>
    internal readonly record struct ReleaseCheck(UpdateInfo? Info, IReadOnlyList<ReleaseAsset>? Assets);

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

/// <summary>A newer release: its version, the release page URL (fallback), its downloadable assets and the
/// release notes/changelog shown in the expanded update notification. (<see cref="Changelog"/> is nullable: a
/// release can be published with an empty body.)</summary>
public sealed record UpdateInfo(string Version, string Url, IReadOnlyList<ReleaseAsset> Assets, string? Changelog = null);

/// <summary>One release asset: file name + direct download URL.</summary>
public sealed record ReleaseAsset(string Name, string Url);

/// <summary>The slice of the GitHub "latest release" JSON we read.</summary>
public sealed class GithubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    /// <summary>The release notes (Markdown on GitHub); rendered as plain wrapped text in the notification.</summary>
    [JsonPropertyName("body")] public string? Body { get; set; }
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
