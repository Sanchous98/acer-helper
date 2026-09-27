using System.IO;
using System.Text.Json.Nodes;
using AcerHelper.Infrastructure;
using AcerHelper.Infrastructure.Plugins;

namespace AcerHelper.Infrastructure.Plugins.Distribution;

/// <summary>
/// THE RUNTIME PLUGIN UPDATE SERVICE (docs/vendor-plugins.md §5.3 steps 1-5, minus the notification/UI wiring
/// that task D2b adds). Given the host's facts (<see cref="PluginUpdateHost"/>) and a release's assets, it:
///
///   1. resolves + fetches + parses <c>vendor-plugins.json</c> (<see cref="PluginManifestFetcher"/>, §5.3 step 1);
///   2. plans the entries that are MISSING or OUTDATED for this machine after §3.8.4 gate 1 (§5.3 step 3);
///   3. for each planned entry, downloads it, verifies the §5.4 chain (signature → manifest hash → bytes BEFORE
///      installing), CONFIRMS the line with <c>ah_matches</c> through the injected binding, and only then installs
///      it into the <see cref="PluginCache"/> (§5.3 step 4);
///   4. returns a <see cref="PluginUpdateResult"/> saying what happened (§5.3 step 5).
///
/// IT DOES NO UI, NO SCHEDULING AND NO WIRING. Nothing calls it yet (D2b), so this slice changes no behaviour; its
/// whole reason to exist is that the decision + install logic is a UNIT-TESTABLE service with an INJECTED
/// downloader and an INJECTED binding factory (NO real network, NO real native plugin in tests). The cache root is
/// also injected (a temp dir in tests).
///
/// THE MACHINE-LINE DECISION (§5.3 step 2). On a machine that already has a candidate cached the answer is the
/// existing loader/scan (<c>DeviceFactory</c> + <c>ah_matches</c>). Here, at DOWNLOAD time, the candidate whose
/// cached copy is already current is not planned at all, so the only entry that reaches step 4 is one the host
/// does NOT already have; with nothing cached that means the manifest's ADVISORY <c>matchHint</c> chooses the
/// candidate — <see cref="PluginCatalogSelector.Select"/> applies it — and <c>ah_matches</c> then CONFIRMS it
/// against the real DMI AFTER download (step 4). The hint may only narrow, never confirm (§3.2).
///
/// FAIL-CLOSED, NEVER THROWS (§4.3 "never a crash", §5.4). Offline, 404, malformed manifest, bad signature,
/// tampered bytes, a non-matching line and a load failure all become a <see cref="PluginUpdateReason"/> and
/// leave the cache untouched. A failed verification, a non-match or a load failure DISCARDS and does NOT install.
///
/// AOT-SAFE: source-gen JSON only, BCL HTTP/crypto (<see cref="PluginSignature"/>, <see cref="PluginCache"/>).
/// </summary>
internal sealed class PluginUpdateService
{
    /// <summary>The minimum <c>ah_matches</c> confidence that confirms a line (§3.2 calibration: 50 =
    /// manufacturer only, 80 = product substring, 100 = product+board). Mirrors <c>DeviceFactory.ConfidenceThreshold</c>
    /// so the download-time confirmation and the startup load accept the same floor; the manifest <c>matchHint</c>
    /// only chose what to download, so this is where the plugin's own verdict is the authority.</summary>
    public const int ConfidenceThreshold = 50;

    private readonly PluginCache _cache;
    private readonly PluginManifestFetcher.DownloadBytes _download;
    private readonly Func<string, INativePluginBinding?> _bindingFactory;

    /// <summary>
    /// Construct over explicit seams. <paramref name="cache"/> is the per-user store (a temp root in tests);
    /// <paramref name="download"/> fetches BOTH the manifest and a plugin asset by URL (production
    /// <see cref="PluginManifestFetcher.HttpDownloader"/>, tests an in-memory map); <paramref name="bindingFactory"/>
    /// loads a downloaded file as a plugin for the pre-install <c>ah_matches</c> confirmation (production
    /// <c>NativePluginBinding.TryLoad</c>, tests a fake). No real network or native plugin is required to drive any
    /// path.
    /// </summary>
    public PluginUpdateService(
        PluginCache cache,
        PluginManifestFetcher.DownloadBytes download,
        Func<string, INativePluginBinding?> bindingFactory)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _download = download ?? throw new ArgumentNullException(nameof(download));
        _bindingFactory = bindingFactory ?? throw new ArgumentNullException(nameof(bindingFactory));
    }

    /// <summary>
    /// Run the §5.3 flow. Returns a reason for every outcome; never throws — a cancellation is
    /// <see cref="PluginUpdateReason.Cancelled"/>, a network fault is <see cref="PluginUpdateReason.DownloadFailed"/>,
    /// and so on. The caller (D2b) turns <see cref="PluginUpdateReason.Installed"/> into a "restart to enable"
    /// notification; this slice only returns it.
    /// </summary>
    public async Task<PluginUpdateResult> RunAsync(
        PluginUpdateHost host,
        IReadOnlyList<ReleaseAsset>? assets,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        try
        {
            // Step 1 — locate + fetch + parse the release manifest (§5.3 step 1). Its failures map onto the
            // service's own reasons so the caller needs one enum.
            var fetched = await PluginManifestFetcher.FetchAsync(assets, _download, ct).ConfigureAwait(false);
            if (!fetched.IsOk)
                return PluginUpdateResult.Failed(Map(fetched.Reason));

            // Step 3 — plan the missing/outdated entries after gate 1 (§5.3 step 3). The cache lookup is built
            // from the REAL cache and only counts a copy that passes the LOCAL at-rest verify, so a tampered or
            // truncated cached asset reads as "missing" and is re-downloaded (§5.3.1, §5.4).
            var plan = Plan(fetched.Catalog!, host, VerifiedCacheLookup);

            if (plan.Entries.Count == 0)
                return PluginUpdateResult.Failed(
                    plan.Incompatible ? PluginUpdateReason.Incompatible : PluginUpdateReason.NothingToDo,
                    checkedCount: plan.Checked);

            // Steps 4-5 — download, verify, confirm, install, for each planned entry (bounded; typically one).
            var failure = PluginUpdateReason.NothingToDo;
            foreach (var entry in plan.Entries)
            {
                ct.ThrowIfCancellationRequested();

                var outcome = await TryInstallAsync(entry, host, ct).ConfigureAwait(false);
                if (outcome.Reason == PluginUpdateReason.Installed)
                    return outcome with { Checked = plan.Checked };

                // Remember the first concrete failure but keep trying the remaining planned entries, so one bad
                // entry cannot block a good sibling.
                if (failure == PluginUpdateReason.NothingToDo) failure = outcome.Reason;
            }

            return PluginUpdateResult.Failed(failure, checkedCount: plan.Checked);
        }
        catch (OperationCanceledException)
        {
            return PluginUpdateResult.Failed(PluginUpdateReason.Cancelled);
        }
        catch
        {
            // Belt to the per-step braces: a plugin-shaped failure must never escape (§4.3). An unexpected host
            // fault is reported as a download failure it is safe to retry on the next tick (§5.3).
            return PluginUpdateResult.Failed(PluginUpdateReason.DownloadFailed);
        }
    }

    /// <summary>
    /// THE PLAN (§5.3 step 3), a PURE function over (catalog, host facts, cache): the entries that are MISSING or
    /// OUTDATED for this machine after §3.8.4 gate 1. It builds on <see cref="PluginCatalogSelector.Select"/>
    /// rather than duplicating gate 1 — the selector applies os/arch, <c>api.major ∈ SupportedMajors</c>,
    /// <c>minHost ≤ host version</c> and the advisory <c>matchHint</c>, and returns a deterministic order — and
    /// then keeps only what the cache does not already hold at the manifest's version.
    ///
    /// <paramref name="cacheLookup"/> returns the TRUSTWORTHY installed record for an id + API major, or null.
    /// Passing it in (rather than reading <see cref="PluginCache"/> here) is what keeps this callable with no I/O:
    /// a test supplies a dictionary. The service supplies one backed by the real cache and its at-rest verify.
    ///
    /// An entry with no <c>id</c> or no <c>api</c> is skipped: the cache key is id + major (§5.3.1), so without
    /// both it cannot be installed or later found. "Outdated" = no usable cached copy, a cached copy older than
    /// the manifest's <c>version</c>, or a <c>version</c> that does not parse (fail closed → re-download).
    /// </summary>
    public static PluginUpdatePlan Plan(
        PluginCatalog catalog,
        PluginUpdateHost host,
        Func<string, int, PluginInstallRecord?> cacheLookup)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(cacheLookup);

        var selected = PluginCatalogSelector.Select(
            catalog, host.Os, host.Arch, host.HostApiMajor, host.HostApiMinor, host.HostAppVersion,
            host.Machine, host.SupportedMajors);

        var planned = new List<PluginCatalogEntry>();
        foreach (var entry in selected)
        {
            if (entry.Id is null || entry.Api is null) continue; // unusable key: cannot install/find it (§5.3.1)

            var installed = cacheLookup(entry.Id, entry.Api.Major);
            if (IsOutdated(installed, entry)) planned.Add(entry);
        }

        // "Incompatible" is the §5.2 status-line case: nothing worth downloading, but the release DID carry a
        // build for this OS/arch whose api.major this host cannot adapt. It is kept distinct from the ordinary
        // NothingToDo so D2b can surface status.plugin_incompatible instead of silence.
        var incompatible = selected.Count == 0 && HasIncompatibleCandidate(catalog, host);

        return new PluginUpdatePlan(planned, selected.Count, incompatible);
    }

    /// <summary>True when the cache has no usable copy of the entry, or a copy older than the manifest's version.
    /// A record older than the wanted version (or an unparseable either side) means re-download; equal or newer
    /// means already current and is NOT planned (§5.3 step 3).</summary>
    private static bool IsOutdated(PluginInstallRecord? installed, PluginCatalogEntry entry)
    {
        if (installed is null) return true;
        if (!PluginCatalogSelector.TryParseAppVersion(entry.Version, out var wanted)) return true;
        if (!PluginCatalogSelector.TryParseAppVersion(installed.Version, out var have)) return true;
        return have < wanted;
    }

    /// <summary>
    /// The §5.2 "expected but not adapt-able" check: at least one entry matches this OS/arch yet declares an
    /// <c>api.major</c> outside <see cref="PluginUpdateHost.SupportedMajors"/>. This is deliberately ONLY the
    /// major test — the hint/minHost reasons for excluding an entry are "not this machine"/"host too old", which
    /// are ordinary NothingToDo, whereas a wrong major is the broken-install case §5.2 says to surface as
    /// <c>status.plugin_incompatible</c>. It reuses the selector's own os/arch predicate shape without repeating
    /// the whole gate.
    /// </summary>
    private static bool HasIncompatibleCandidate(PluginCatalog catalog, PluginUpdateHost host)
    {
        foreach (var entry in catalog.Plugins ?? [])
        {
            if (!string.Equals(entry.Os, host.Os, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(entry.Arch, host.Arch, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Api is { } api && !host.SupportedMajors.Contains(api.Major)) return true;
        }
        return false;
    }

    /// <summary>The cache lookup the plan uses: the installed record for id + major ONLY when it passes the local
    /// at-rest hash check, so a tampered/truncated cached asset reads as "missing" and is re-downloaded
    /// (§5.3.1, §5.4).</summary>
    private PluginInstallRecord? VerifiedCacheLookup(string id, int apiMajor)
    {
        var record = _cache.FindInstalled(id, apiMajor);
        return record is not null && _cache.VerifyInstalled(record) ? record : null;
    }

    /// <summary>
    /// §5.3 step 4 for ONE entry: download the asset bytes, verify the §5.4 chain, CONFIRM the line with
    /// <c>ah_matches</c> through the injected binding, and install atomically only when all three pass. Every
    /// failure returns a reason and installs nothing. The downloaded bytes are written to a temp file OUTSIDE the
    /// cache (the loader scans the cache recursively for <c>*.dll</c>/<c>*.so</c>, so a probe file must not live
    /// there) and always deleted.
    /// </summary>
    private async Task<PluginUpdateResult> TryInstallAsync(
        PluginCatalogEntry entry, PluginUpdateHost host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.Url))
            return PluginUpdateResult.Failed(PluginUpdateReason.DownloadFailed);

        byte[]? bytes;
        try { bytes = await _download(entry.Url!, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { bytes = null; }
        if (bytes is null || bytes.Length == 0)
            return PluginUpdateResult.Failed(PluginUpdateReason.DownloadFailed);

        // VERIFY BEFORE INSTALLING (§5.4): signature → manifest hash → bytes. A bad signature, a wrong hash or
        // tampered bytes are refused here, so nothing is written (PluginCache.Install re-verifies anyway, but we
        // want the distinct VerificationFailed reason rather than an opaque install failure).
        if (!PluginSignature.VerifyBinary(entry, bytes))
            return PluginUpdateResult.Failed(PluginUpdateReason.VerificationFailed);

        // CONFIRM THE LINE (§5.3 step 2/4): load the downloaded bytes as a plugin and ask ah_matches. A load
        // failure or a non-match discards the bytes and installs nothing; the descriptor is the §3.2 four-field
        // JSON.
        if (!ConfirmLine(entry, bytes, host))
            return PluginUpdateResult.Failed(PluginUpdateReason.NoMatch);

        // INSTALL (§5.3.1): atomic, and only after the full chain passed. A false here is a write/verify fault.
        if (!_cache.Install(entry, bytes))
            return PluginUpdateResult.Failed(PluginUpdateReason.VerificationFailed);

        return PluginUpdateResult.Installed(entry.Id, entry.Version);
    }

    /// <summary>
    /// Write the downloaded bytes to a temp file, load it through the injected binding factory and call
    /// <c>ah_matches</c> (§3.2). Returns true only on a real match at or above <see cref="ConfidenceThreshold"/>.
    /// A null binding (absent/not-a-native-image/missing-export), a thrown factory and a thrown <c>Matches</c> all
    /// return false — a discard, never a crash (§4.3). The binding is disposed and the temp file deleted in a
    /// <c>finally</c> on every path.
    /// </summary>
    private bool ConfirmLine(PluginCatalogEntry entry, byte[] bytes, PluginUpdateHost host)
    {
        var ext = Path.GetExtension(entry.Asset ?? entry.Url ?? ".dll");
        var temp = Path.Combine(Path.GetTempPath(), $"acer-helper-plugin-probe-{Guid.NewGuid():N}{ext}");
        INativePluginBinding? binding = null;
        try
        {
            File.WriteAllBytes(temp, bytes);
            binding = _bindingFactory(temp);
            if (binding is null) return false;

            var decision = binding.Matches(BuildDescriptorJson(host.Machine));
            return decision.Matched && decision.Confidence >= ConfidenceThreshold;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { binding?.Dispose(); } catch { /* the vendor's own handles; best effort (§4.3) */ }
            try { File.Delete(temp); } catch { /* temp; best effort */ }
        }
    }

    /// <summary>The §3.2 four-field machine descriptor as JSON, built with <see cref="JsonObject"/> (AOT-safe, no
    /// reflection) and with the wire names spelled literally so they cannot drift. All four fields are always
    /// emitted; a null is read by the plugin as "unknown", exactly the absent case. This mirrors the loader's own
    /// private builder without reaching into it.</summary>
    private static string BuildDescriptorJson(MachineDescriptor m) =>
        new JsonObject
        {
            ["manufacturer"] = m.Manufacturer,
            ["product"] = m.Product,
            ["board"] = m.Board,
            ["boardProduct"] = m.BoardProduct,
        }.ToJsonString();

    /// <summary>Map a manifest-fetch failure onto the service's reason enum so the caller has one enum to switch
    /// on. A malformed manifest is surfaced as <see cref="PluginUpdateReason.Malformed"/>; a missing asset is
    /// <see cref="PluginUpdateReason.NoManifest"/>; an empty/thrown download is
    /// <see cref="PluginUpdateReason.DownloadFailed"/>.</summary>
    private static PluginUpdateReason Map(PluginManifestFetchReason reason) => reason switch
    {
        PluginManifestFetchReason.NoManifest => PluginUpdateReason.NoManifest,
        PluginManifestFetchReason.Malformed => PluginUpdateReason.Malformed,
        _ => PluginUpdateReason.DownloadFailed,
    };
}

/// <summary>
/// The host's facts for one update check (docs/vendor-plugins.md §5.3 steps 2-3): this machine's OS/arch tokens,
/// the host's generated plugin API major/minor (<c>PluginApi</c>, §3.8.1), the app <c>&lt;Version&gt;</c> the
/// <c>minHost</c> gate compares, the four-field DMI descriptor (§3.2) and the API majors the host can adapt
/// (<c>PluginAbiRegistry.SupportedMajors</c>, §3.8.2/§3.8.4 gate 1). A record so production builds one from the
/// same sources <c>DeviceFactory</c> uses and a test builds one from literals.
/// </summary>
internal sealed record PluginUpdateHost(
    string Os,
    string Arch,
    int HostApiMajor,
    int HostApiMinor,
    string HostAppVersion,
    MachineDescriptor Machine,
    IReadOnlySet<int> SupportedMajors);

/// <summary>
/// The plan's output (§5.3 step 3): the entries to download/install, in the selector's deterministic order, the
/// number of candidates gate 1 examined (<see cref="Checked"/>, for the result), and whether an INCOMPATIBLE
/// candidate was seen (<see cref="Incompatible"/>, the §5.2 status-line case). A record struct because it is a
/// small transient result.
/// </summary>
internal readonly record struct PluginUpdatePlan(
    IReadOnlyList<PluginCatalogEntry> Entries,
    int Checked,
    bool Incompatible);

/// <summary>
/// Why a plugin update ended the way it did (docs/vendor-plugins.md §5.3). Exhaustive so D2b switches and the
/// compiler flags a new reason with no branch (its "Installed → restart notification" mapping in particular).
/// </summary>
internal enum PluginUpdateReason
{
    /// <summary>A plugin was downloaded, verified, confirmed against this machine and installed (§5.3 step 4).
    /// D2b turns this into the "restart to enable" notification; this slice only returns it.</summary>
    Installed = 0,

    /// <summary>The release carries no <c>vendor-plugins.json</c> — no plugin is published with it (§5.2). The
    /// ordinary "no plugins" case.</summary>
    NoManifest = 1,

    /// <summary>The manifest parsed and gate 1 selected candidates, but every one is already current for this
    /// machine, or nothing matched after the advisory <c>matchHint</c> (§5.3 step 3).</summary>
    NothingToDo = 2,

    /// <summary>The candidate's asset could not be downloaded (offline, 404, timeout, empty body). Safe to retry
    /// on the next schedule tick.</summary>
    DownloadFailed = 3,

    /// <summary>The downloaded bytes failed the §5.4 chain — a bad signature, a wrong hash or tampered bytes.
    /// Nothing was installed. This is the integrity refusal.</summary>
    VerificationFailed = 4,

    /// <summary>The bytes verified but the plugin did not load or <c>ah_matches</c> did not confirm this machine
    /// (§5.3 step 4). Discarded; nothing installed.</summary>
    NoMatch = 5,

    /// <summary>The release carried a build for this OS/arch whose <c>api.major</c> this host cannot adapt
    /// (§5.2/§3.8.4 gate 1) and nothing else was planned. The caller may surface
    /// <c>status.plugin_incompatible</c> rather than staying silent.</summary>
    Incompatible = 6,

    /// <summary>The downloaded manifest was not valid <c>vendor-plugins.json</c> JSON (malformed/truncated). The
    /// update is refused rather than guessed at.</summary>
    Malformed = 7,

    /// <summary>The check was cancelled via the <see cref="CancellationToken"/>. No install happened.</summary>
    Cancelled = 8,
}

/// <summary>
/// The service's verdict (§5.3 step 5): a <see cref="Reason"/> always, the installed
/// <see cref="PluginId"/>/<see cref="Version"/> on success, the number of gate-1 candidates examined
/// (<see cref="Checked"/>) and an optional human-readable <see cref="Detail"/> for a log. The
/// installed⇔reason invariant holds by construction through the factories.
/// </summary>
internal sealed record PluginUpdateResult(
    PluginUpdateReason Reason,
    string? PluginId,
    string? Version,
    int Checked,
    string? Detail)
{
    /// <summary>Convenience: something was installed this run (the D2b notification trigger).</summary>
    public bool IsInstalled => Reason == PluginUpdateReason.Installed;

    /// <summary>A successful install carrying the id + version the signature authenticated.</summary>
    public static PluginUpdateResult Installed(string? pluginId, string? version, int checkedCount = 0) =>
        new(PluginUpdateReason.Installed, pluginId, version, checkedCount, Detail: null);

    /// <summary>A non-install outcome. <see cref="PluginId"/>/<see cref="Version"/> are informational (the entry
    /// a failure was about), null when no single entry was involved.</summary>
    public static PluginUpdateResult Failed(
        PluginUpdateReason reason, string? detail = null, int checkedCount = 0,
        string? pluginId = null, string? version = null) =>
        new(reason, pluginId, version, checkedCount, detail);
}
