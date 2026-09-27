using System.Security.Cryptography;
using System.Text;

namespace AcerHelper.Infrastructure.Plugins.Distribution;

/// <summary>
/// The signature primitive for the distribution manifest (docs/vendor-plugins.md §5.4): the canonical string,
/// the verifier the loaded host runs, and the signer CI (and the tests) run.
///
/// WHY THE MANIFEST ENTRY, NOT THE BARE BYTES. The design signs a canonical string of the entry's metadata —
/// <c>id</c>, <c>os</c>, <c>arch</c>, <c>version</c>, <c>api.major</c>, <c>api.minor</c>, <c>minHost</c>,
/// <c>sha256</c> — rather than the binary alone, "so the metadata cannot be tampered with: … the alternative —
/// a detached raw signature over the binary only — is simpler but leaves version/api forgeable; rejected"
/// (§5.4). The verifier then closes the chain signature → manifest hash → binary: the signature makes the
/// manifest's <c>sha256</c> (and the API major it selects on) authentic, and <see cref="VerifyBinary"/> makes
/// the downloaded bytes match that hash. Neither the bytes nor their metadata can be swapped.
///
/// PRIMITIVE: ECDSA P-256 (§5.4, the locked Phase 0 decision). It is on every .NET target, AOT-safe (no
/// <c>RequiresDynamicCode</c>), and uses the in-box SHA-256 — no third-party crypto dependency. Ed25519 is the
/// design's preference on the merits but is only acceptable "if and only if it proves available on the shipped
/// runtime" (<c>Ed25519.IsSupported</c>, §5.4); it is NOT wired here because the probe has not changed the
/// answer and P-256 is the default the host ships. If that probe ever flips, an Ed25519 verifier is a sibling
/// method selected by <see cref="PluginCatalogEntry.SigAlg"/> — the canonical-string contract below is
/// primitive-independent and would not change.
///
/// NO EXCEPTION ESCAPES. Every public method returns bool (or null) and catches: a malformed signature, an
/// unknown key, a wrong curve or a bad base64 are all expected adversarial inputs, and a verify path that can
/// throw is a crash an attacker chooses the timing of. Fail closed = return false.
/// </summary>
internal static class PluginSignature
{
    /// <summary>The canonical-string field separator. Every field is joined with this one byte, in the fixed
    /// order of <see cref="CanonicalString"/>, UTF-8, with NO trailing separator. A single <c>\n</c> is chosen
    /// because it is unambiguous for the values involved — none of <c>id</c>, <c>os</c>, <c>arch</c>,
    /// <c>version</c>, the API ints, <c>minHost</c> or a lowercase-hex <c>sha256</c> can contain a newline —
    /// while staying trivially reproducible from a shell, PowerShell or any CI signer without a JSON library.
    /// Newline rather than a single character like '|' also makes a captured canonical string readable when a
    /// verification is debugged.</summary>
    public const char Separator = '\n';

    /// <summary>The signature algorithm token this build verifies (§5.2, §5.4). Only ES256 is implemented, so
    /// any entry declaring another algorithm is refused fail-closed rather than silently treated as ES256.</summary>
    public const string Algorithm = "ES256";

    /// <summary>
    /// The EXACT byte string the CI signer and the host verifier must agree on (§5.4) — the cross-version
    /// contract. Field order is fixed and documented; values are written verbatim (no trimming, no
    /// normalisation beyond the caller having produced a valid entry) and joined by <see cref="Separator"/>:
    ///
    /// <code>
    /// id \n os \n arch \n version \n api.major \n api.minor \n minHost \n sha256
    /// </code>
    ///
    /// e.g. <c>acer-nitro\nwin\nx64\n1.2.0\n1\n0\n0.38.0\n2cf24d…</c> (one line in the real thing; shown with
    /// visual newlines). There is NO trailing separator. <c>api.major</c>/<c>api.minor</c> are invariant-culture
    /// integers; <c>sha256</c> is the lowercase hex the manifest carries. A null field is written as the empty
    /// string (a malformed entry can therefore still produce a stable string, so signing and verifying it
    /// disagree consistently rather than crashing). A field value that itself contained a newline would make the
    /// encoding ambiguous; the field domains above exclude that, and a producer that violated it would produce
    /// a string both sides still hash identically — the ambiguity only matters if two distinct entries could
    /// collide, which the constrained domains prevent.
    /// </summary>
    public static string CanonicalString(PluginCatalogEntry entry) => string.Join(
        Separator,
        entry.Id ?? "",
        entry.Os ?? "",
        entry.Arch ?? "",
        entry.Version ?? "",
        (entry.Api?.Major ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        (entry.Api?.Minor ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        entry.MinHost ?? "",
        entry.Sha256 ?? "");

    /// <summary>The canonical string as the bytes that are actually hashed/signed — UTF-8, no BOM.</summary>
    public static byte[] CanonicalBytes(PluginCatalogEntry entry) =>
        Encoding.UTF8.GetBytes(CanonicalString(entry));

    /// <summary>
    /// Verify the detached signature over <see cref="CanonicalString"/>, using the embedded public key selected
    /// by the entry's <c>keyId</c> (§5.4). Returns false — never throws — on an unknown <c>keyId</c>, a
    /// malformed/empty signature, a missing entry field the canonical form needs, a non-ES256 <c>sigAlg</c>, or a
    /// signature that simply does not match.
    ///
    /// This is the manifest-integrity half of the chain; <see cref="VerifyBinary"/> is the bytes half. A caller
    /// that wants the full §5.4 check calls both (or <see cref="VerifyBinary(PluginCatalogEntry, byte[])"/>).
    /// </summary>
    public static bool Verify(PluginCatalogEntry entry)
    {
        try
        {
            // Only the P-256 primitive this build implements is accepted. A null sigAlg is treated as ES256
            // because that is the only thing this build can mean; anything else is refused rather than guessed.
            if (entry.SigAlg is not null &&
                !string.Equals(entry.SigAlg, Algorithm, StringComparison.OrdinalIgnoreCase))
                return false;

            var keySpki = PluginPublicKeys.TryGet(entry.KeyId);
            if (keySpki is null) return false;

            var signature = TryDecodeSignature(entry.Signature);
            if (signature is null || signature.Length == 0) return false;

            using var ecdsa = ECDsa.Create();
            // SPKI names its own curve, so a key that is not P-256 either fails to import or fails to verify —
            // no separate curve check is needed, and none is trusted to be cheaper than the import itself.
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(keySpki), out _);
            return ecdsa.VerifyData(CanonicalBytes(entry), signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The full §5.4 chain: the manifest signature is valid AND the given binary's SHA-256 equals the entry's
    /// <c>sha256</c>. This is what the host runs on the bytes it downloaded before installing them (§5.3 step 4)
    /// and again on the cached bytes at startup (§5.3 step 5).
    ///
    /// The hash is compared as lowercase hex, case-insensitively, because the manifest's spelling is a producer
    /// detail while the bytes are the fact; <paramref name="bytes"/> null or empty hashes to the empty-string
    /// digest and simply will not match a real entry, so no separate guard is needed.
    /// </summary>
    public static bool VerifyBinary(PluginCatalogEntry entry, byte[] bytes)
    {
        try
        {
            if (!Verify(entry)) return false;
            if (string.IsNullOrWhiteSpace(entry.Sha256)) return false;

            var actual = Convert.ToHexString(SHA256.HashData(bytes ?? [])).ToLowerInvariant();
            return string.Equals(actual, entry.Sha256.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sign <see cref="CanonicalString"/> with a P-256 private key, producing the base64 detached signature the
    /// manifest's <c>signature</c> field carries. INTERNAL on purpose: CI (via the same code, §5.5) and the
    /// tests are the intended callers; the shipping host only ever verifies and must never need the capability
    /// to sign. <paramref name="privateKeyPkcs8Base64"/> is the CI secret's PKCS#8 DER, base64 (§7.3 item 2);
    /// the method throws on an unreadable key because that is a producer-side configuration error, not an
    /// adversarial input — the fail-closed rule above applies to verification, not to signing.
    /// </summary>
    internal static string Sign(PluginCatalogEntry entry, string privateKeyPkcs8Base64)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out _);
        var signature = ecdsa.SignData(CanonicalBytes(entry), HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature);
    }

    /// <summary>
    /// Decode the detached signature. Base64 is the canonical encoding (§5.2's <c>.sig</c> is base64 text, and
    /// <see cref="Sign"/> emits base64); hex is accepted as a best-effort fallback because the same document
    /// already spells hashes as hex and a producer could reasonably do the same for the signature. Returns null
    /// on anything that decodes as neither, which the caller folds into the same "false" as a mismatch.
    /// </summary>
    private static byte[]? TryDecodeSignature(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return null;
        var text = signature.Trim();
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            // Not base64 — try hex. Convert.FromHexString throws on odd length or a non-hex character, which
            // the outer catch in Verify turns into the documented "false".
            try { return Convert.FromHexString(text); }
            catch (FormatException) { return null; }
        }
    }
}
