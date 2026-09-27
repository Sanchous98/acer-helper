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

    // TODO(owner): embed the REAL production public key here before the first plugin is published, under its own
    // stable keyId (e.g. "2026-09-release"). It is the SPKI DER, base64, of the P-256 key whose PRIVATE half is
    // the GitHub Actions signing secret (§5.4, §5.5) — generate it once, commit only the public half to the host
    // (never the private half, and never a key that signed anything in a repo), and keep the private half only
    // in the CI secret store / KMS (§7.3 item 2). Adding it is one entry below; do NOT repurpose TestKeyId, and
    // do NOT read this map lazily from a file — the compile-time constant is the whole trust property (§5.4).

    /// <summary>The trusted keys by <c>keyId</c> → SPKI DER base64. Add production keys here as they are
    /// created; never edit or remove an entry a shipped release was signed with, because an old client accepts
    /// only the keys it was built with and cannot learn a replacement over the network (§7.3 item 2).</summary>
    public static IReadOnlyDictionary<string, string> All { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TestKeyId] = TestPublicKeySpki,
            // TODO(owner): [ "2026-09-release" ] = "<real production P-256 SPKI, base64>" — see the note above.
        };

    /// <summary>Look up a trusted key's SPKI DER by its <c>keyId</c>, or null when unknown. The comparison is
    /// ordinal (keyIds are exact tokens, not user text), and an unknown id returns null so the caller's verify
    /// path fails closed instead of falling back to some other key.</summary>
    public static string? TryGet(string? keyId) =>
        keyId is not null && All.TryGetValue(keyId, out var key) ? key : null;
}
