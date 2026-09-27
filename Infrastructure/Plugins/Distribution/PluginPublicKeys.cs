namespace AcerHelper.Infrastructure.Plugins.Distribution;

/// <summary>
/// The embedded trusted public keys, keyed by <c>keyId</c> (§5.4, §7.3 item 2).
///
/// WHY EMBEDDED, NOT FETCHED. The public key is a compile-time constant in the host binary, exactly like
/// <c>AppInfo.Version</c> and <c>PluginApi</c> (<c>AcerHelper.csproj:220-237</c>): the design requires
/// verification with "no second download and no trust-on-first-use" (§5.4). If the key arrived over the same
/// network channel as the binary it verifies, an attacker who controls the channel would control both. Embedding
/// it is what makes the signature an actual trust anchor rather than a checksum of the attacker's own choosing.
///
/// ROTATION SHAPE. The map (not a single field) is deliberate: a client accepts exactly the keys it was built
/// with, so a rotated signing key reaches old clients only after an app update that adds the new key (§7.3
/// item 2). A new <c>keyId</c> is therefore a new map entry, never an edit to an existing one — old clients keep
/// verifying old releases, new clients verify both. The map is <see cref="IReadOnlyDictionary{TKey,TValue}"/>
/// over the interface so no caller can mutate it at runtime.
///
/// CRYPTO SHAPE. Values are SubjectPublicKeyInfo (SPKI) DER, base64, imported by <c>PluginSignature</c> with
/// <c>ECDsa.ImportSubjectPublicKeyInfo</c> — the format <c>ExportSubjectPublicKeyInfo</c> produces and the one
/// that names its own curve, so a P-256 key cannot be silently read as some other curve. Keys are P-256
/// (§5.4, the locked Phase 0 decision).
/// </summary>
internal static class PluginPublicKeys
{
    /// <summary>The <c>keyId</c> of the TEST key. It is embedded in this tree, so anything it signs is trusted
    /// by the tests and by a developer build that has not yet been switched to the production key. It MUST NOT
    /// sign production releases; the <c>keyId</c> is the visible marker of that.</summary>
    public const string TestKeyId = "test-p256-2026-09";

    /// <summary>The TEST public key, SPKI DER base64 (P-256). The private half is the well-known value in
    /// <c>PluginSignatureTests</c>, published in-tree on purpose: this key protects nothing, so its private half
    /// being public is the point — it lets the tests and CI fixtures sign without a secret. A production release
    /// signed with this key is a release any user can forge, which is why the real key below must land before
    /// the first real plugin ships.</summary>
    private const string TestPublicKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEVPv7qhiS37R9geKir4fHiSSSs23Ky7fPsHppUTpo9M7A94zJP5Sqh0fuDFwW7U65Cuc36XzVxuWQTc2P2HmtuA==";

    /// <summary>The <c>keyId</c> of the PRODUCTION signing key (§5.4, §5.5). CI signs the plugin assets and the
    /// manifest with its private half (the GitHub Actions secret <c>PLUGIN_SIGNING_KEY</c>, PKCS#8 DER base64),
    /// and every shipped host verifies with the public half below. The id is a stable, dated token so rotation is
    /// "add a new dated entry", never "edit this one".</summary>
    public const string ReleaseKeyId = "2026-09-release";

    /// <summary>The PRODUCTION public key, SPKI DER base64 (P-256). Only the public half is committed; the
    /// private half lives solely in the CI secret store (§5.4, §7.3 item 2 — never in the repo and never in a
    /// build host beyond the signing step). Verification imports it with <c>ImportSubjectPublicKeyInfo</c>, which
    /// names its own curve, so this cannot be silently read as another curve. REPLACING it is a new map entry
    /// under a NEW <c>keyId</c>, never an edit: an old client accepts only the keys it was built with and cannot
    /// learn a replacement over the network.</summary>
    private const string ReleasePublicKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAExZlJEy2ht9XSAZVaBANi6AiyE8+Es+aH0iRRtE9WbMwgdHoXV+tzcigbVHtZas32k/wU3o3aZ9L/miIQ+ledTA==";

    /// <summary>The trusted keys by <c>keyId</c> → SPKI DER base64. Add production keys here as they are
    /// created; never edit or remove an entry a shipped release was signed with, because an old client accepts
    /// only the keys it was built with and cannot learn a replacement over the network (§7.3 item 2). The TEST
    /// key stays so fixtures can sign without the secret; a production release is signed with
    /// <see cref="ReleaseKeyId"/>.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TestKeyId] = TestPublicKeySpki,
            [ReleaseKeyId] = ReleasePublicKeySpki,
        };

    /// <summary>Look up a trusted key's SPKI DER by its <c>keyId</c>, or null when unknown. The comparison is
    /// ordinal (keyIds are exact tokens, not user text), and an unknown id returns null so the caller's verify
    /// path fails closed instead of falling back to some other key.</summary>
    public static string? TryGet(string? keyId) =>
        keyId is not null && All.TryGetValue(keyId, out var key) ? key : null;
}
