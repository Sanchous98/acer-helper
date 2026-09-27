using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcerHelper.Infrastructure.Plugins.Distribution;

/// <summary>
/// The <c>vendor-plugins.json</c> distribution manifest (docs/vendor-plugins.md §5.2) — the release-attached
/// document that names every published plugin asset, per OS/arch, with the metadata and detached signature the
/// host needs to decide WHAT to download and whether to TRUST it (§5.3, §5.4).
///
/// IT IS NOT THE PROBE MANIFEST. <c>PluginManifest</c> (§3.4) is what a LOADED plugin says about this machine's
/// hardware; this is what the RELEASE says about the published files, before anything is downloaded. The two
/// share only the word "manifest"; keeping them in different types (and different files) is what stops a
/// download-time concern from leaking into the loaded-session model.
///
/// Every type is a plain camelCase wire DTO serialised through the source-generated
/// <see cref="PluginCatalogJsonContext"/> — the same AOT-safe rule as <c>PluginJsonContext</c> and
/// <c>AcerModelJsonContext</c>, and for the same reason: <c>JsonSerializer.Serialize(object)</c> is
/// <c>RequiresDynamicCode</c> and cannot be used in the Native AOT host (§2.2, §3.3). No reflection, ever.
///
/// The §5.2 sample is the authority for field names; the one addition is <see cref="PluginCatalogEntry.Signature"/>,
/// see its own note.
/// </summary>
internal sealed class PluginCatalog
{
    /// <summary>The manifest's own format version (§5.2), so a future shape change is detectable without
    /// guessing from the fields present. The §5.2 sample ships <c>1</c>.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>The published entries. Null and empty are both "no plugins" — the selector returns an empty
    /// result rather than throwing, so a release with no plugin attached degrades to the no-plugin fallback
    /// exactly like a missing manifest (§4.1).</summary>
    public List<PluginCatalogEntry>? Plugins { get; set; }
}

/// <summary>
/// One published plugin asset (§5.2). It is keyed for selection by <c>id + os + arch + api.major</c>
/// (§3.8.4 gate 1) and is the unit the signature covers (§5.4): the canonical string is built from the fields
/// of THIS type, so all of them except <see cref="Signature"/> itself are authenticated.
///
/// Fields the design doc shows but the selection path does not strictly need (<see cref="Vendor"/>,
/// <see cref="Asset"/>, <see cref="Sig"/>, <see cref="SigAlg"/>) are modelled anyway: they are part of the
/// published contract, the CI aggregate job writes them (§5.5), and dropping them from the DTO would make the
/// host silently ignore a field a human reading the release page can see. An unknown member is ignored by
/// source-gen JSON (§3.8.5), so keeping them costs nothing and documents the full shape.
/// </summary>
internal sealed class PluginCatalogEntry
{
    /// <summary>The MODEL-LINE id, e.g. "acer-nitro" (§1.4) — not the vendor. Must equal the plugin's
    /// <c>ah_plugin_id</c> and the cache folder name (§5.3.1); the host does not derive it from the file name.</summary>
    public string? Id { get; set; }

    /// <summary>The vendor token, e.g. "acer" (§5.2). Informational for the host; the line id above is the key.</summary>
    public string? Vendor { get; set; }

    /// <summary>"win" or "linux" (§5.2) — the OS token the host matches
    /// <c>RuntimeInformation.IsOSPlatform</c> facts against. A string, not an enum, so an unknown future token
    /// simply never matches rather than failing to deserialise.</summary>
    public string? Os { get; set; }

    /// <summary>"x64" today (§5.2); arm64 later. Same "unknown token never matches" rule as <see cref="Os"/>.</summary>
    public string? Arch { get; set; }

    /// <summary>The plugin's own build version (e.g. "1.2.0"), used for staleness (§5.3) and carried into the
    /// signature so it cannot be forged (§5.4). NOT the app version and NOT the API version.</summary>
    public string? Version { get; set; }

    /// <summary>The plugin API the ASSET was built against (§3.8.4 gate 1), e.g. <c>{"major":1,"minor":0}</c>.
    /// The major is the selection key; the minor is informational (§3.8.5).</summary>
    public PluginApiRange? Api { get; set; }

    /// <summary>The oldest app <c>&lt;Version&gt;</c> that can load the asset, e.g. "0.38.0" (§3.8.4 gate 1),
    /// compared against <c>AppInfo.Version</c>. A string here because it is a semver-ish app version, parsed by
    /// the selector rather than by the serialiser.</summary>
    public string? MinHost { get; set; }

    /// <summary>The ADVISORY coarse pre-filter (§3.2, §5.2) so the host does not load every unrelated plugin.
    /// It narrows what is worth downloading/loading; <c>ah_matches</c> remains the authority. Null means "no
    /// hint": such an entry is never filtered out (the design's explicit no-hint rule, §3.2).</summary>
    public PluginMatchHint? MatchHint { get; set; }

    /// <summary>The exact release asset file name (§5.2), e.g.
    /// <c>AcerHelper.Vendor.acer-nitro-win-x64.dll</c>. The host downloads by <see cref="Url"/>; this is the
    /// human-facing name and the cache file name, and it is what the CI consistency check re-hashes (§6).</summary>
    public string? Asset { get; set; }

    /// <summary>The browser_download_url of the asset (§5.2) — the exact URL the host fetches. The host never
    /// assembles a URL from parts; a renamed or mis-uploaded asset is therefore a manifest error, caught here.</summary>
    public string? Url { get; set; }

    /// <summary>The asset's SHA-256 as lowercase hex (§5.2). The signature covers this value (§5.4), and the
    /// host re-hashes the downloaded bytes against it, chaining signature → manifest hash → binary so neither
    /// the binary nor its metadata (its API major included) can be swapped.</summary>
    public string? Sha256 { get; set; }

    /// <summary>The detached signature over the CANONICAL STRING of this entry (§5.4), base64-encoded.
    ///
    /// §5.2's sample carries only <see cref="Sig"/> (the URL of the standalone <c>.sig</c> release asset); this
    /// inline field is what task T2's verifier consumes, so the host can verify without a second download. The
    /// two are complementary: <see cref="Sig"/> is where the same signature is published for external tooling
    /// and CI (§5.5), and this is the copy the running host reads in the manifest it already fetched (§5.3).
    /// Base64 is the canonical encoding; <c>PluginSignature</c> also accepts hex as a best-effort fallback.</summary>
    public string? Signature { get; set; }

    /// <summary>The URL of the standalone <c>.sig</c> asset (§5.2). Published for CI/other verifiers; the
    /// host itself verifies <see cref="Signature"/> and does not fetch this.</summary>
    public string? Sig { get; set; }

    /// <summary>The signature algorithm token (§5.2): "ES256" for the P-256 primitive that ships (§5.4), or
    /// "Ed25519" only if the runtime probe makes it available. A value other than "ES256" is refused by the
    /// P-256 verifier (fail-closed); null is treated as "ES256", the only primitive this build implements.</summary>
    public string? SigAlg { get; set; }

    /// <summary>Which embedded public key signed it (§5.4, §7.3 item 2), e.g. "2026-09-release". It indexes
    /// <c>PluginPublicKeys</c>; rotation adds keys there without breaking a client that only knows the old ones.</summary>
    public string? KeyId { get; set; }
}

/// <summary>The <c>api</c> object of an entry (§5.2): the plugin API major.minor the asset was built against,
/// mirrored from <c>ah_abi_version()</c> at build time (§3.8.1). The MAJOR is the gate (§3.8.4); the minor is
/// informational (§3.8.5). Both are ints, so a malformed value never deserialises.</summary>
internal sealed class PluginApiRange
{
    public int Major { get; set; }
    public int Minor { get; set; }
}

/// <summary>
/// The advisory match hint (§3.2, §5.2). Each list is a set of case-insensitive substrings tested against the
/// same-named machine-descriptor field. It is deliberately COARSE: its only job is to keep an unrelated plugin
/// from being downloaded and loaded (a loaded AOT module cannot be unloaded, §2.3), so the cost of a false
/// positive is a wasted probe while the cost of a false negative is a line backend that never appears.
///
/// That asymmetry is why a null/empty group imposes NO constraint and a null <see cref="PluginCatalogEntry.MatchHint"/>
/// keeps the entry: the hint may only ever EXCLUDE on a definite mismatch, never confirm a match. The design
/// states the rule directly — "a plugin with no hint is still evaluated if it is already cached" (§3.2).
/// </summary>
internal sealed class PluginMatchHint
{
    public List<string>? Manufacturer { get; set; }
    public List<string>? Product { get; set; }
    public List<string>? Board { get; set; }
    public List<string>? BoardProduct { get; set; }
}

/// <summary>
/// The AOT-safe serialiser for <c>vendor-plugins.json</c> (§5.2) — a <c>[JsonSerializable]</c> context for the
/// one root type, mirroring <c>PluginJsonContext</c> and <c>AcerModelJsonContext</c>. camelCase + the tolerant
/// reader options (comments, trailing commas) so a fixture can paste the §5.2 sample verbatim, which is exactly
/// the precedent the existing contexts set.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(PluginCatalog))]
internal partial class PluginCatalogJsonContext : JsonSerializerContext;
