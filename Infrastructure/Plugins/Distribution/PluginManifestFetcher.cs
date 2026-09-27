using System.Net.Http;
using System.Text.Json;
using AcerHelper.Infrastructure;

namespace AcerHelper.Infrastructure.Plugins.Distribution;

/// <summary>
/// LOCATE + FETCH + PARSE <c>vendor-plugins.json</c> from a release's asset list (docs/vendor-plugins.md §5.3
/// step 1, §5.2). Task D2a: this is the "resolve the release's manifest" half of the runtime update flow, kept
/// SEPARATE from the decision/install half (<see cref="PluginUpdateService"/>) so the parsing rules and the
/// network seam can be tested without a single byte leaving the machine.
///
/// IT REUSES THE EXISTING UPDATE MACHINERY, IT DOES NOT ADD ONE (§5.3): the caller already has the release's
/// assets from <c>UpdateChecker.CheckAsync</c> / <c>UpdateInfo.Assets</c> (<c>UpdateChecker.cs:20-55</c>), so the
/// plugin path simply asks for <c>vendor-plugins.json</c> AMONG those assets instead of making a second GitHub
/// API call — the plugin and app update checks share one network path.
///
/// THE HTTP BOUNDARY IS AN INJECTED SEAM. <see cref="FetchAsync"/> takes a
/// <c>Func&lt;string, CancellationToken, Task&lt;byte[]?&gt;&gt;</c> rather than constructing an
/// <see cref="HttpClient"/> itself, so a test drives every path (found, absent, 404, malformed, thrown) over an
/// in-memory byte[] and NEVER touches the network. Production builds the real call with
/// <see cref="HttpDownloader"/>, in the <c>WindowsUpdater.DownloadAsync</c> / <c>AppImageUpdater.ReplaceAsync</c>
/// style (UA header, timeout, off the UI thread; <c>WindowsUpdater.cs:61-80</c>).
///
/// NO EXCEPTION ESCAPES. A missing asset, an empty body, a failed download and malformed JSON all degrade to a
/// <see cref="PluginManifestFetchReason"/> — never a throw — matching the fail-closed style of
/// <see cref="PluginSignature"/>/<see cref="PluginCache"/> (§5.4, §5.3.1) and the loader's "never a crash"
/// (§4.3). The parse goes through the source-generated <see cref="PluginCatalogJsonContext"/> only; no
/// reflection, AOT-safe (§2.2).
/// </summary>
internal static class PluginManifestFetcher
{
    /// <summary>The manifest's exact asset name (§5.2), matched case-insensitively because the release page and
    /// the host's parser must agree on the shape regardless of how a human capitalised it.</summary>
    public const string ManifestAssetName = "vendor-plugins.json";

    /// <summary>The injectable download seam: fetch <paramref name="url"/> to bytes, or null on any failure.
    /// The same seam fetches BOTH the manifest and (via <see cref="PluginUpdateService"/>) a plugin asset, so a
    /// test can dispatch by URL.</summary>
    public delegate Task<byte[]?> DownloadBytes(string url, CancellationToken ct);

    /// <summary>
    /// Find the <c>vendor-plugins.json</c> asset among a release's assets and return its download URL, or null
    /// when the release carries no manifest (§5.2; a release with no plugin attached is the ordinary "no
    /// plugins" case, §4.1). Name equality is ordinal-ignore-case; a null asset name never matches. The URL is
    /// returned verbatim — the host never assembles one from parts (§5.2).
    /// </summary>
    public static string? FindManifestUrl(IReadOnlyList<ReleaseAsset>? assets)
    {
        if (assets is null) return null;
        foreach (var asset in assets)
        {
            if (string.Equals(asset?.Name, ManifestAssetName, StringComparison.OrdinalIgnoreCase))
                return asset!.Url;
        }
        return null;
    }

    /// <summary>
    /// Locate the manifest in <paramref name="assets"/>, download it through <paramref name="download"/> and parse
    /// it (§5.3 step 1). Never throws: a missing asset is <see cref="PluginManifestFetchReason.NoManifest"/>, a
    /// null/thrown/empty download is <see cref="PluginManifestFetchReason.DownloadFailed"/>, and bytes that are
    /// not a <see cref="PluginCatalog"/> are <see cref="PluginManifestFetchReason.Malformed"/>.
    /// </summary>
    public static async Task<PluginManifestFetchResult> FetchAsync(
        IReadOnlyList<ReleaseAsset>? assets,
        DownloadBytes? download,
        CancellationToken ct = default)
    {
        if (download is null) return new PluginManifestFetchResult(null, PluginManifestFetchReason.DownloadFailed);

        var url = FindManifestUrl(assets);
        if (url is null) return new PluginManifestFetchResult(null, PluginManifestFetchReason.NoManifest);

        byte[]? bytes;
        try { bytes = await download(url, ct).ConfigureAwait(false); }
        catch { bytes = null; } // offline/404/timeout: a reason, never a throw (§5.3, §5.4)

        return Parse(bytes);
    }

    /// <summary>
    /// Parse raw manifest bytes through the source-generated context. Null/empty bytes are
    /// <see cref="PluginManifestFetchReason.DownloadFailed"/> (the fetch produced nothing); bytes that do not
    /// deserialise to a <see cref="PluginCatalog"/> are <see cref="PluginManifestFetchReason.Malformed"/>. A
    /// syntactically valid document with no <c>plugins</c> array is a valid, empty catalog — "no plugins" is an
    /// ordinary state, not a parse error (§4.1). Never throws.
    /// </summary>
    public static PluginManifestFetchResult Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return new PluginManifestFetchResult(null, PluginManifestFetchReason.DownloadFailed);

        try
        {
            var catalog = JsonSerializer.Deserialize(bytes, PluginCatalogJsonContext.Default.PluginCatalog);
            return catalog is null
                ? new PluginManifestFetchResult(null, PluginManifestFetchReason.Malformed)
                : new PluginManifestFetchResult(catalog, PluginManifestFetchReason.Ok);
        }
        catch
        {
            // Malformed JSON, an ill-typed member, a truncated body — all adversarial inputs on the update path,
            // so they degrade to a reason rather than a crash (§5.4's fail-closed posture).
            return new PluginManifestFetchResult(null, PluginManifestFetchReason.Malformed);
        }
    }

    /// <summary>
    /// The production <see cref="DownloadBytes"/>: a one-shot <see cref="HttpClient"/> with the same shape as
    /// <c>WindowsUpdater.DownloadAsync</c> — a UA header (some GitHub endpoints 403 UA-less), a bounded timeout,
    /// and every failure folded to null so the caller sees a reason not an exception
    /// (<c>WindowsUpdater.cs:61-80</c>). Blocking network I/O: callers invoke it off the UI thread (§5.3).
    /// </summary>
    public static DownloadBytes HttpDownloader() => async (url, ct) =>
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AcerHelper-plugin");
            return await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    };
}

/// <summary>
/// Why a manifest fetch ended the way it did (docs/vendor-plugins.md §5.3 step 1, §5.2). Exhaustive so a caller
/// switches and the compiler flags a new reason that has no branch; the reason maps onto
/// <see cref="PluginUpdateReason.NoManifest"/>/<see cref="PluginUpdateReason.DownloadFailed"/> at the service.
/// </summary>
internal enum PluginManifestFetchReason
{
    /// <summary>The manifest was found, downloaded and parsed. <see cref="PluginManifestFetchResult.Catalog"/> is
    /// non-null.</summary>
    Ok = 0,

    /// <summary>The release carries no <c>vendor-plugins.json</c> asset — a release with no plugin attached
    /// (§5.2/§4.1). Ordinary, not an error.</summary>
    NoManifest = 1,

    /// <summary>The manifest asset exists but the download returned nothing (offline, 404, timeout, an injected
    /// seam that threw).</summary>
    DownloadFailed = 2,

    /// <summary>The downloaded bytes are not a <see cref="PluginCatalog"/> (malformed or truncated JSON). The
    /// update is refused rather than guessed at.</summary>
    Malformed = 3,
}

/// <summary>
/// The fetcher's verdict: a non-null <see cref="Catalog"/> exactly when <see cref="Reason"/> is
/// <see cref="PluginManifestFetchReason.Ok"/>. A record struct because it is a small, transient result — the same
/// shape the tree uses for <c>PluginCallResult</c> and <c>AbiCompatibilityResult</c>.
/// </summary>
internal readonly record struct PluginManifestFetchResult(PluginCatalog? Catalog, PluginManifestFetchReason Reason)
{
    /// <summary>Convenience: the parse succeeded and a catalog is available.</summary>
    public bool IsOk => Reason == PluginManifestFetchReason.Ok && Catalog is not null;
}
