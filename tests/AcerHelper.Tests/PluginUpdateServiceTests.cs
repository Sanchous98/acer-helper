using System.Security.Cryptography;
using System.Text;
using AcerHelper.Infrastructure;
using AcerHelper.Infrastructure.Plugins;
using AcerHelper.Infrastructure.Plugins.Distribution;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The runtime plugin update service (docs/vendor-plugins.md §5.3 steps 1-5, §5.3.1, §5.4, §3.8.4 gate 1, task
/// D2a). Every test drives the service with an INJECTED downloader and an INJECTED binding factory over a TEMP
/// cache root — no real network, no real native plugin — so the whole flow (fetch → plan → download → verify →
/// confirm → install) is proven without touching a machine.
///
/// THE DOWNLOADER SEAM is <see cref="PluginManifestFetcher.DownloadBytes"/>: one delegate fetches BOTH the
/// <c>vendor-plugins.json</c> manifest and the plugin asset, dispatched by URL, so a release is modelled entirely
/// in memory. THE BINDING SEAM is <c>Func&lt;string, INativePluginBinding?&gt;</c>, standing in for
/// <c>NativePluginBinding.TryLoad</c> exactly as <c>PluginLoaderTests</c> does.
/// </summary>
public class PluginUpdateServiceTests
{
    // The well-known TEST keypair whose public half is embedded in PluginPublicKeys, reused verbatim from
    // PluginCacheTests/PluginSignatureTests so signed fixtures verify against the embedded constant.
    private const string TestPrivateKeyPkcs8 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgaKZC5OZ+wR2KEjeKFbVxOKgpuR/D1UeMphPqgBKAtMWhRANCAARU+/uqGJLftH2B4qKvh8eJJJKzbcrLt8+wemlROmj0zsD3jMk/lKqHR+4MXBbtTrkK5zfpfNXG5ZBNzY/Yea24";

    private static readonly MachineDescriptor AcerNitro =
        new("Acer", "Nitro AN18-61", "Acer", "AN18-61");

    /// <summary>A scratch cache root per test — the cache writes real files, so a test must never touch the real
    /// per-user root (the same rule as <c>PluginCacheTests</c>).</summary>
    private sealed class TempCacheRoot : IDisposable
    {
        public TempCacheRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "acer-helper-plugin-update-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }
        public PluginCache NewCache() => new(Root);
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp; best effort */ }
        }
    }

    private static byte[] Asset(string marker = "a real plugin binary, more or less") =>
        Encoding.UTF8.GetBytes(marker);

    /// <summary>Build a COMPLETE, SIGNED entry, exactly as CI would (§5.4): fill the canonical fields, set
    /// <c>sha256</c> to the bytes' digest, then sign. The only entry the cache/verify path accepts.</summary>
    private static PluginCatalogEntry SignedEntry(
        byte[] bytes,
        string id = "acer-nitro",
        string os = "win",
        string arch = "x64",
        string version = "1.2.0",
        int apiMajor = 1,
        int apiMinor = 0,
        string minHost = "0.38.0",
        string url = "https://example.invalid/acer.dll",
        PluginMatchHint? hint = null)
    {
        var entry = new PluginCatalogEntry
        {
            Id = id,
            Vendor = "acer",
            Os = os,
            Arch = arch,
            Version = version,
            Api = new PluginApiRange { Major = apiMajor, Minor = apiMinor },
            MinHost = minHost,
            Asset = $"AcerHelper.Vendor.{id}-{os}-{arch}.dll",
            Url = url,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            KeyId = PluginPublicKeys.TestKeyId,
            SigAlg = "ES256",
            MatchHint = hint,
        };
        entry.Signature = PluginSignature.Sign(entry, TestPrivateKeyPkcs8);
        return entry;
    }

    /// <summary>A gate-1-clean, but UNSIGNED, entry for the PURE plan tests (the plan never verifies).</summary>
    private static PluginCatalogEntry E(
        string id,
        string os = "win",
        string arch = "x64",
        int major = 1,
        int minor = 0,
        string minHost = "0.38.0",
        string version = "1.0.0",
        PluginMatchHint? hint = null) => new()
        {
            Id = id,
            Os = os,
            Arch = arch,
            Version = version,
            Api = new PluginApiRange { Major = major, Minor = minor },
            MinHost = minHost,
            MatchHint = hint,
        };

    /// <summary>The host facts for the plan/flow: Windows x64, API 1.0, app 0.39, an Acer Nitro descriptor, and
    /// exactly one supported major.</summary>
    private static PluginUpdateHost Host(string os = "win", string arch = "x64", string appVersion = "0.39.0",
                                         IReadOnlySet<int>? majors = null) =>
        new(os, arch, 1, 0, appVersion, AcerNitro, majors ?? new HashSet<int> { 1 });

    /// <summary>An in-memory downloader: URLs map to bytes; anything else (and any configured throw) is null.</summary>
    private sealed class FakeDownloader
    {
        private readonly Dictionary<string, byte[]> _byUrl = new(StringComparer.Ordinal);
        public bool Throw { get; set; }
        public int Calls { get; private set; }

        public void Set(string url, byte[] bytes) => _byUrl[url] = bytes;

        public PluginManifestFetcher.DownloadBytes AsDelegate() => (url, ct) =>
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("network down");
            return Task.FromResult(_byUrl.TryGetValue(url, out var b) ? b : (byte[]?)null);
        };
    }

    /// <summary>Serialise a catalog to the bytes a release's manifest asset would carry.</summary>
    private static byte[] ManifestBytes(PluginCatalog catalog) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            catalog, PluginCatalogJsonContext.Default.PluginCatalog);

    private const string ManifestUrl = "https://example.invalid/vendor-plugins.json";

    /// <summary>The release's asset list: the manifest plus one plugin asset.</summary>
    private static List<ReleaseAsset> Release(PluginCatalogEntry entry) =>
        [new ReleaseAsset(PluginManifestFetcher.ManifestAssetName, ManifestUrl),
         new ReleaseAsset(entry.Asset!, entry.Url!)];

    // ======================================================================================================
    // PLAN — a PURE function over (catalog, host, cache) reusing PluginCatalogSelector (§5.3 step 3, §3.8.4)
    // ======================================================================================================

    /// <summary>A cache with nothing in it plans a matching entry: it is missing and must be downloaded (§5.3
    /// step 3).</summary>
    [Fact]
    public void PlanPlansAMatchingEntryWhenNothingIsCached()
    {
        var catalog = new PluginCatalog { Plugins = [E("acer-nitro")] };

        var plan = PluginUpdateService.Plan(catalog, Host(), (_, _) => null);

        Assert.Equal(["acer-nitro"], plan.Entries.Select(e => e.Id));
        Assert.Equal(1, plan.Checked);
        Assert.False(plan.Incompatible);
    }

    /// <summary>An entry for the wrong OS/arch is excluded by gate 1 (§5.2): a Linux build is not this Windows
    /// machine's.</summary>
    [Fact]
    public void PlanExcludesAWrongOsOrArchEntry()
    {
        var catalog = new PluginCatalog { Plugins = [E("linux-build", os: "linux"), E("this-machine")] };

        var plan = PluginUpdateService.Plan(catalog, Host(), (_, _) => null);

        Assert.Equal(["this-machine"], plan.Entries.Select(e => e.Id));
    }

    /// <summary>An <c>api.major</c> outside <c>SupportedMajors</c> is excluded by gate 1 (§3.8.4), and — since
    /// only that incompatible candidate exists for this OS/arch — the plan flags the §5.2 status-line case.</summary>
    [Fact]
    public void PlanExcludesAnUnsupportedApiMajorAndFlagsIncompatible()
    {
        var catalog = new PluginCatalog { Plugins = [E("future", major: 2)] };

        var plan = PluginUpdateService.Plan(catalog, Host(), (_, _) => null);

        Assert.Empty(plan.Entries);
        Assert.True(plan.Incompatible);
    }

    /// <summary>A supported, OS/arch-matching major does NOT flag Incompatible even when the hint excludes it —
    /// hint exclusion is "not this machine", the ordinary NothingToDo case.</summary>
    [Fact]
    public void PlanDoesNotFlagIncompatibleWhenTheMajorIsSupported()
    {
        // Manufacturer hint "Dell" cannot match the Acer descriptor, so the entry is excluded by the hint, not
        // the major; that is not the §5.2 status-line case.
        var hint = new PluginMatchHint { Manufacturer = ["Dell"] };
        var catalog = new PluginCatalog { Plugins = [E("acer-nitro", hint: hint)] };

        var plan = PluginUpdateService.Plan(catalog, Host(), (_, _) => null);

        Assert.Empty(plan.Entries);
        Assert.False(plan.Incompatible);
        Assert.Equal(0, plan.Checked); // the hint excluded it in the selector, so it was never examined
    }

    /// <summary><c>minHost</c> above the host version excludes the entry (§3.8.4 gate 1): the host is too old to
    /// load it.</summary>
    [Fact]
    public void PlanExcludesAnEntryWhoseMinHostIsAboveTheHost()
    {
        var catalog = new PluginCatalog { Plugins = [E("too-new", minHost: "0.99.0")] };

        var plan = PluginUpdateService.Plan(catalog, Host(appVersion: "0.39.0"), (_, _) => null);

        Assert.Empty(plan.Entries);
    }

    /// <summary>A cached copy at the SAME version is already current and is NOT planned (§5.3 step 3).</summary>
    [Fact]
    public void PlanDoesNotPlanAnEntryAlreadyAtTheManifestVersion()
    {
        var catalog = new PluginCatalog { Plugins = [E("acer-nitro", version: "1.2.0")] };
        var installed = new PluginInstallRecord { Id = "acer-nitro", Version = "1.2.0", Api = new PluginApiRange { Major = 1 } };

        var plan = PluginUpdateService.Plan(catalog, Host(), (_, _) => installed);

        Assert.Empty(plan.Entries);
    }

    /// <summary>A cached copy OLDER than the manifest version is outdated and IS planned (§5.3 step 3).</summary>
    [Fact]
    public void PlanPlansAnEntryWhoseCachedCopyIsOlder()
    {
        var catalog = new PluginCatalog { Plugins = [E("acer-nitro", version: "1.2.0")] };
        var installed = new PluginInstallRecord { Id = "acer-nitro", Version = "1.0.0", Api = new PluginApiRange { Major = 1 } };

        var plan = PluginUpdateService.Plan(catalog, Host(), (_, _) => installed);

        Assert.Equal(["acer-nitro"], plan.Entries.Select(e => e.Id));
    }

    // ======================================================================================================
    // FLOW — fetch → plan → download → verify → confirm → install (§5.3 steps 1-4)
    // ======================================================================================================

    /// <summary>The happy path: a signed asset downloads, verifies, confirms through a matching fake binding and
    /// installs; the result names the id/version and the cache then verifies (§5.3 steps 3-5).</summary>
    [Fact]
    public async Task MatchingBindingInstallsAndTheCacheVerifies()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var bytes = Asset();
        var entry = SignedEntry(bytes);

        var downloader = new FakeDownloader();
        downloader.Set(ManifestUrl, ManifestBytes(new PluginCatalog { Plugins = [entry] }));
        downloader.Set(entry.Url!, bytes);

        var binding = new FakeNativePluginBinding
        {
            MatchResult = new PluginMatchResult(Matched: true, Confidence: 80, Reason: "test"),
        };

        var service = new PluginUpdateService(cache, downloader.AsDelegate(), _ => binding);
        var result = await service.RunAsync(Host(), Release(entry));

        Assert.Equal(PluginUpdateReason.Installed, result.Reason);
        Assert.True(result.IsInstalled);
        Assert.Equal("acer-nitro", result.PluginId);
        Assert.Equal("1.2.0", result.Version);

        // The cache now holds and verifies the asset (§5.3.1).
        var record = cache.FindInstalled("acer-nitro", 1);
        Assert.NotNull(record);
        Assert.True(cache.VerifyInstalled(record));
        Assert.Equal(bytes, File.ReadAllBytes(cache.ResolveInstalledPath(record!)!));

        // The line was confirmed with the §3.2 four-field descriptor.
        var desc = Assert.Single(binding.MatchesDescriptors);
        Assert.Contains("\"manufacturer\":\"Acer\"", desc);
        Assert.Contains("\"product\":\"Nitro AN18-61\"", desc);
    }

    /// <summary>A fake binding that does NOT match discards the bytes: reason NoMatch, nothing installed
    /// (§5.3 step 4).</summary>
    [Fact]
    public async Task NonMatchingBindingIsDiscardedAndNothingIsInstalled()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var bytes = Asset();
        var entry = SignedEntry(bytes);

        var downloader = new FakeDownloader();
        downloader.Set(ManifestUrl, ManifestBytes(new PluginCatalog { Plugins = [entry] }));
        downloader.Set(entry.Url!, bytes);

        var binding = new FakeNativePluginBinding
        {
            MatchResult = new PluginMatchResult(Matched: false, Confidence: 0, Reason: "not this machine"),
        };

        var service = new PluginUpdateService(cache, downloader.AsDelegate(), _ => binding);
        var result = await service.RunAsync(Host(), Release(entry));

        Assert.Equal(PluginUpdateReason.NoMatch, result.Reason);
        Assert.False(result.IsInstalled);
        Assert.Null(cache.FindInstalled("acer-nitro", 1));
        Assert.Equal(1, binding.DisposeCount); // the probe binding was released
    }

    /// <summary>A match BELOW the confidence threshold does not confirm the line — the same 50 floor the loader
    /// uses (§3.2).</summary>
    [Fact]
    public async Task MatchBelowThresholdIsDiscarded()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var bytes = Asset();
        var entry = SignedEntry(bytes);

        var downloader = new FakeDownloader();
        downloader.Set(ManifestUrl, ManifestBytes(new PluginCatalog { Plugins = [entry] }));
        downloader.Set(entry.Url!, bytes);

        var binding = new FakeNativePluginBinding
        {
            MatchResult = new PluginMatchResult(Matched: true, Confidence: PluginUpdateService.ConfidenceThreshold - 1, Reason: "coarse"),
        };

        var service = new PluginUpdateService(cache, downloader.AsDelegate(), _ => binding);
        var result = await service.RunAsync(Host(), Release(entry));

        Assert.Equal(PluginUpdateReason.NoMatch, result.Reason);
        Assert.Null(cache.FindInstalled("acer-nitro", 1));
    }

    /// <summary>A tampered asset (bytes that do not match the signed hash) is rejected before anything is
    /// installed: reason VerificationFailed, cache untouched (§5.4).</summary>
    [Fact]
    public async Task TamperedAssetIsRejectedAsVerificationFailed()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var signed = Asset("the bytes the entry claims");
        var tampered = Asset("the bytes actually offered");
        var entry = SignedEntry(signed);

        var downloader = new FakeDownloader();
        downloader.Set(ManifestUrl, ManifestBytes(new PluginCatalog { Plugins = [entry] }));
        downloader.Set(entry.Url!, tampered); // same URL, different bytes than sha256/signature cover

        var binding = new FakeNativePluginBinding
        {
            MatchResult = new PluginMatchResult(Matched: true, Confidence: 100, Reason: "never reached"),
        };

        var service = new PluginUpdateService(cache, downloader.AsDelegate(), _ => binding);
        var result = await service.RunAsync(Host(), Release(entry));

        Assert.Equal(PluginUpdateReason.VerificationFailed, result.Reason);
        Assert.Null(cache.FindInstalled("acer-nitro", 1));
        // Verification precedes the load: ah_matches was never called for a tampered asset.
        Assert.Empty(binding.MatchesDescriptors);
    }

    /// <summary>A downloader that returns null (404/empty) yields DownloadFailed with no throw and no install
    /// (§5.3).</summary>
    [Fact]
    public async Task NullDownloadYieldsDownloadFailedWithoutThrowing()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var entry = SignedEntry(Asset());

        var downloader = new FakeDownloader();
        downloader.Set(ManifestUrl, ManifestBytes(new PluginCatalog { Plugins = [entry] }));
        // the plugin URL is NOT registered -> the seam returns null

        var service = new PluginUpdateService(cache, downloader.AsDelegate(), _ => new FakeNativePluginBinding());
        var result = await service.RunAsync(Host(), Release(entry));

        Assert.Equal(PluginUpdateReason.DownloadFailed, result.Reason);
        Assert.Null(cache.FindInstalled("acer-nitro", 1));
    }

    /// <summary>A downloader that THROWS yields DownloadFailed, never a throw (§4.3 "never a crash").</summary>
    [Fact]
    public async Task ThrowingDownloaderYieldsDownloadFailedWithoutThrowing()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var entry = SignedEntry(Asset());

        var downloader = new FakeDownloader { Throw = true };
        var service = new PluginUpdateService(cache, downloader.AsDelegate(), _ => new FakeNativePluginBinding());

        var result = await service.RunAsync(Host(), Release(entry));

        Assert.Equal(PluginUpdateReason.DownloadFailed, result.Reason);
        Assert.Null(cache.FindInstalled("acer-nitro", 1));
    }

    /// <summary>A release with NO manifest asset is NoManifest — the ordinary "no plugins published" case
    /// (§5.2), detected before any download of the manifest. The plugin asset is present but irrelevant.</summary>
    [Fact]
    public async Task ReleaseWithoutAManifestIsNoManifest()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var entry = SignedEntry(Asset());
        var downloader = new FakeDownloader();
        downloader.Set(ManifestUrl, ManifestBytes(new PluginCatalog { Plugins = [entry] }));

        var service = new PluginUpdateService(cache, downloader.AsDelegate(), _ => new FakeNativePluginBinding());

        // The release carries only the plugin asset, not vendor-plugins.json.
        var assets = new List<ReleaseAsset> { new(entry.Asset!, entry.Url!) };
        var result = await service.RunAsync(Host(), assets);

        Assert.Equal(PluginUpdateReason.NoManifest, result.Reason);
        Assert.Equal(0, downloader.Calls); // no manifest URL -> nothing was even fetched
    }

    /// <summary>A release whose manifest is already current for this machine yields NothingToDo, and no plugin
    /// asset is downloaded (§5.3 step 3).</summary>
    [Fact]
    public async Task AlreadyCurrentManifestYieldsNothingToDo()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var bytes = Asset();
        var entry = SignedEntry(bytes, version: "1.2.0");
        Assert.True(cache.Install(entry, bytes)); // a good, verified cached copy at the same version

        var downloader = new FakeDownloader();
        downloader.Set(ManifestUrl, ManifestBytes(new PluginCatalog { Plugins = [entry] }));
        downloader.Set(entry.Url!, bytes);

        var service = new PluginUpdateService(cache, downloader.AsDelegate(), _ => new FakeNativePluginBinding());
        var result = await service.RunAsync(Host(), Release(entry));

        Assert.Equal(PluginUpdateReason.NothingToDo, result.Reason);
        Assert.Equal(1, result.Checked);
        Assert.False(result.IsInstalled);
    }

    /// <summary>A malformed manifest body is Malformed, never a throw (§5.2/§5.4 fail-closed).</summary>
    [Fact]
    public async Task MalformedManifestYieldsMalformedWithoutThrowing()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var entry = SignedEntry(Asset());

        var downloader = new FakeDownloader();
        downloader.Set(ManifestUrl, Encoding.UTF8.GetBytes("{ this is not a catalog "));
        downloader.Set(entry.Url!, Asset());

        var service = new PluginUpdateService(cache, downloader.AsDelegate(), _ => new FakeNativePluginBinding());
        var result = await service.RunAsync(Host(), Release(entry));

        Assert.Equal(PluginUpdateReason.Malformed, result.Reason);
        Assert.Null(cache.FindInstalled("acer-nitro", 1));
    }

    /// <summary>A cancellation is surfaced as Cancelled with no throw and no install (§5.3: the caller exposes a
    /// token; D2b owns the UI-side guard).</summary>
    [Fact]
    public async Task CancellationIsSurfacedWithoutThrowing()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var entry = SignedEntry(Asset());

        var downloader = new FakeDownloader();
        downloader.Set(ManifestUrl, ManifestBytes(new PluginCatalog { Plugins = [entry] }));
        downloader.Set(entry.Url!, Asset());

        var service = new PluginUpdateService(cache, downloader.AsDelegate(), _ => new FakeNativePluginBinding());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await service.RunAsync(Host(), Release(entry), cts.Token);

        Assert.Equal(PluginUpdateReason.Cancelled, result.Reason);
        Assert.Null(cache.FindInstalled("acer-nitro", 1));
    }

    // ======================================================================================================
    // MANIFEST FETCHER — locate + parse over the injected seam (§5.3 step 1, §5.2)
    // ======================================================================================================

    /// <summary>The fetcher finds <c>vendor-plugins.json</c> among a release's assets (case-insensitively) and
    /// returns its URL verbatim; a release without it returns null.</summary>
    [Fact]
    public void FetcherFindsTheManifestAssetByName()
    {
        var assets = new List<ReleaseAsset>
        {
            new("AcerHelper.Vendor.acer-nitro-win-x64.dll", "https://example.invalid/plugin.dll"),
            new("Vendor-Plugins.JSON", "https://example.invalid/m.json"),
        };

        Assert.Equal("https://example.invalid/m.json", PluginManifestFetcher.FindManifestUrl(assets));
        Assert.Null(PluginManifestFetcher.FindManifestUrl(
            [new ReleaseAsset("AcerHelper.Vendor.acer-nitro-win-x64.dll", "https://example.invalid/plugin.dll")]));
        Assert.Null(PluginManifestFetcher.FindManifestUrl(null));
    }

    /// <summary>The parse half is driven over a <c>byte[]</c> directly: valid bytes produce a catalog; malformed
    /// bytes produce <see cref="PluginManifestFetchReason.Malformed"/>; null/empty produce
    /// <see cref="PluginManifestFetchReason.DownloadFailed"/> — never a throw.</summary>
    [Fact]
    public void FetcherParsesBytesAndNeverThrows()
    {
        var catalog = new PluginCatalog { Plugins = [E("acer-nitro")] };
        var ok = PluginManifestFetcher.Parse(ManifestBytes(catalog));
        Assert.True(ok.IsOk);
        Assert.Equal("acer-nitro", Assert.Single(ok.Catalog!.Plugins!).Id);

        Assert.Equal(PluginManifestFetchReason.Malformed,
            PluginManifestFetcher.Parse(Encoding.UTF8.GetBytes("{ not json")).Reason);
        Assert.Equal(PluginManifestFetchReason.DownloadFailed, PluginManifestFetcher.Parse(null).Reason);
        Assert.Equal(PluginManifestFetchReason.DownloadFailed, PluginManifestFetcher.Parse([]).Reason);
    }

    /// <summary>End to end through the seam: a release without the manifest asset is NoManifest; a seam that
    /// returns nothing is DownloadFailed; a malformed body is Malformed — all without a throw.</summary>
    [Fact]
    public async Task FetcherAsyncReportsNoManifestDownloadFailureAndMalformed()
    {
        var noManifest = await PluginManifestFetcher.FetchAsync(
            [new ReleaseAsset("plugin.dll", "https://example.invalid/p.dll")],
            (_, _) => Task.FromResult<byte[]?>(null));
        Assert.Equal(PluginManifestFetchReason.NoManifest, noManifest.Reason);

        var noBytes = await PluginManifestFetcher.FetchAsync(
            [new ReleaseAsset(PluginManifestFetcher.ManifestAssetName, ManifestUrl)],
            (_, _) => Task.FromResult<byte[]?>(null));
        Assert.Equal(PluginManifestFetchReason.DownloadFailed, noBytes.Reason);

        var malformed = await PluginManifestFetcher.FetchAsync(
            [new ReleaseAsset(PluginManifestFetcher.ManifestAssetName, ManifestUrl)],
            (_, _) => Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes("}{")));
        Assert.Equal(PluginManifestFetchReason.Malformed, malformed.Reason);

        var good = await PluginManifestFetcher.FetchAsync(
            [new ReleaseAsset(PluginManifestFetcher.ManifestAssetName, ManifestUrl)],
            (_, _) => Task.FromResult<byte[]?>(ManifestBytes(new PluginCatalog { Plugins = [E("acer-nitro")] })));
        Assert.True(good.IsOk);
    }

    /// <summary>A fetcher whose seam THROWS surfaces DownloadFailed, not an exception (§5.4 fail-closed).</summary>
    [Fact]
    public async Task FetcherAsyncSurfacesAThrowingSeamAsDownloadFailed()
    {
        var result = await PluginManifestFetcher.FetchAsync(
            [new ReleaseAsset(PluginManifestFetcher.ManifestAssetName, ManifestUrl)],
            (_, _) => throw new InvalidOperationException("network down"));

        Assert.Equal(PluginManifestFetchReason.DownloadFailed, result.Reason);
        Assert.False(result.IsOk);
    }
}
