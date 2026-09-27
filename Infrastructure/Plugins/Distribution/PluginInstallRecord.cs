using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcerHelper.Infrastructure.Plugins.Distribution;

/// <summary>
/// The <c>installed.json</c> sidecar of one cached plugin (docs/vendor-plugins.md §5.3.1): the record the host
/// writes next to a downloaded asset so that STARTUP verification is a LOCAL hash check with no network and no
/// manifest, and so a host that later moves API majors knows which build it has (§5.3.1, §5.3 step 5).
///
/// WHY A SIDECAR AT ALL. The trust chain is signature → manifest <c>sha256</c> → binary (§5.4). The binary is
/// the only thing that survives in the cache byte-for-byte; <c>installed.json</c> preserves the manifest facts
/// the signature authenticated (<c>sha256</c>, <c>keyId</c>, the API major) so an at-rest re-hash can prove the
/// cached bytes still match what was verified at install time without re-downloading <c>vendor-plugins.json</c>.
/// The record is written ONLY after <see cref="PluginSignature.VerifyBinary"/> succeeded (see
/// <see cref="PluginCache.Install"/>), so a record existing at all means "the bytes this names passed the chain".
///
/// WHAT IT DELIBERATELY DOES NOT CARRY: the signature itself, and the canonical fields (<c>os</c>, <c>arch</c>,
/// <c>minHost</c>) the signature covers. Those live in the fetched manifest entry, which is not cached here. That
/// is why <see cref="PluginCache.VerifyInstalled(PluginInstallRecord)"/> owns the LOCAL hash check, and the
/// authenticity (signature) half is re-run by the caller that still holds a <see cref="PluginCatalogEntry"/> —
/// see <see cref="PluginCache.VerifyInstalled(PluginInstallRecord, PluginCatalogEntry)"/>.
///
/// Reuses <see cref="PluginApiRange"/> from <c>PluginCatalog.cs</c> rather than declaring a second
/// <c>api: { major, minor }</c> shape, so the wire object and its deserialisation stay one type. Serialised with
/// the source-generated <see cref="PluginInstallRecordJsonContext"/> — the same AOT-safe rule as
/// <c>PluginCatalogJsonContext</c> and <c>AcerModelJsonContext</c> (§2.2): <c>JsonSerializer.Serialize(object)</c>
/// is <c>RequiresDynamicCode</c> and cannot run in the Native AOT host. No reflection, ever.
/// </summary>
internal sealed class PluginInstallRecord
{
    /// <summary>The MODEL-LINE id (§1.4), e.g. "acer-nitro" — the same token as <c>ah_plugin_id</c>, the manifest
    /// entry's <c>id</c> and the first cache folder level (§5.3.1). Never derived from a file name.</summary>
    public string? Id { get; set; }

    /// <summary>The cached asset's BARE file name (e.g. <c>AcerHelper.Vendor.acer-nitro-win-x64.dll</c>). It is
    /// the name from the manifest entry, sanitised to a single path segment at install time; the directory is
    /// derived from <see cref="Id"/> and <see cref="Api"/> instead, so a manifest cannot point the cache outside
    /// its own tree (§5.3.1).</summary>
    public string? Asset { get; set; }

    /// <summary>The plugin's own build version (§5.2, e.g. "1.2.0") — carried so staleness can be decided
    /// locally (§5.3 step 3) and for support. NOT the app version and NOT the API version.</summary>
    public string? Version { get; set; }

    /// <summary>The plugin API the cached asset was built against (§3.8.4): the MAJOR is the cache key segment,
    /// the minor is informational (§3.8.5). Reuses <see cref="PluginApiRange"/> so the wire object has one type.</summary>
    public PluginApiRange? Api { get; set; }

    /// <summary>The asset's SHA-256, lowercase hex — the value verified against the downloaded bytes at install
    /// (§5.4) and re-checked against the cached bytes by <see cref="PluginCache.VerifyInstalled(PluginInstallRecord)"/>.
    /// This is the local-integrity anchor: a tampered or truncated byte changes it and the verify returns false.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Which embedded public key signed the entry (§5.4), e.g. "2026-09-release" — kept for diagnostics
    /// and so a future rotation can see what is cached. It is NOT re-used for verification here because the record
    /// carries no signature; the caller with the manifest entry re-runs <see cref="PluginSignature.Verify"/>.</summary>
    public string? KeyId { get; set; }

    /// <summary>When the asset was installed, ISO-8601 UTC (<c>DateTime.UtcNow.ToString("O")</c>). A string, not a
    /// <c>DateTime</c>, so the serialised form is exactly the documented ISO-8601 timestamp and cannot drift with a
    /// serializer's local-offset policy.</summary>
    public string? InstalledAt { get; set; }
}

/// <summary>
/// The AOT-safe serialiser for <c>installed.json</c> (§5.3.1) — a <c>[JsonSerializable]</c> context for the one
/// root type, mirroring <see cref="PluginCatalogJsonContext"/>. camelCase + the tolerant reader options
/// (comments, trailing commas, case-insensitive names) so a hand-inspected or fixture file parses identically to
/// the writer's output. The nested <see cref="PluginApiRange"/> is generated with the root type.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(PluginInstallRecord))]
internal partial class PluginInstallRecordJsonContext : JsonSerializerContext;
