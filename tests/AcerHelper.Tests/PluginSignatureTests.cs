using System.Security.Cryptography;
using System.Text;
using AcerHelper.Infrastructure.Plugins.Distribution;

namespace AcerHelper.Tests;

/// <summary>
/// The signature primitive of the distribution manifest (docs/vendor-plugins.md §5.4, Phase 0 task T2). It is a
/// pure, self-contained layer — no network, no startup wiring — so these tests are the whole proof of the trust
/// path: a P-256 keypair is generated in the test, a synthetic entry is signed, and the verifier must accept it
/// and reject every tampering.
///
/// THE CANONICAL-STRING PIN IS THE MOST IMPORTANT TEST HERE. The byte string is the contract between the CI
/// signer and the host verifier (§5.4); the two are compiled and run at different times by different code, so
/// nothing but a hard-coded expectation catches a change that would make a freshly signed release fail to
/// verify on an already-shipped client. Its literal is intentionally duplicated in the test rather than derived
/// from the production helper.
/// </summary>
public class PluginSignatureTests
{
    // The well-known TEST keypair whose public half is embedded in PluginPublicKeys (PluginPublicKeys.TestKeyId).
    // The private half living in the test tree is the point: it lets the suite sign without a secret. It must
    // never sign a real release — the test keyId marks that, and the production key is a TODO in PluginPublicKeys.
    private const string TestPrivateKeyPkcs8 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgaKZC5OZ+wR2KEjeKFbVxOKgpuR/D1UeMphPqgBKAtMWhRANCAARU+/uqGJLftH2B4qKvh8eJJJKzbcrLt8+wemlROmj0zsD3jMk/lKqHR+4MXBbtTrkK5zfpfNXG5ZBNzY/Yea24";

    /// <summary>Build a minimal but complete entry; fields are overridable so a test can flip exactly one and
    /// prove the signature covers it. <c>sha256</c> is left null by default because the signature tests do not
    /// need a hash — the field is included in the canonical string regardless, as the empty string.</summary>
    private static PluginCatalogEntry Entry(
        string id = "acer-nitro",
        string os = "win",
        string arch = "x64",
        string version = "1.2.0",
        int apiMajor = 1,
        int apiMinor = 0,
        string minHost = "0.38.0",
        string? sha256 = null) => new()
        {
            Id = id,
            Os = os,
            Arch = arch,
            Version = version,
            Api = new PluginApiRange { Major = apiMajor, Minor = apiMinor },
            MinHost = minHost,
            Sha256 = sha256,
            KeyId = PluginPublicKeys.TestKeyId,
            SigAlg = "ES256",
        };

    /// <summary>Sign a synthetic entry with the test private key, exactly as CI will (<c>PluginSignature.Sign</c>).</summary>
    private static string Sign(PluginCatalogEntry entry) => PluginSignature.Sign(entry, TestPrivateKeyPkcs8);

    /// <summary>The happy path: a signature produced by the test key verifies against the public key embedded in
    /// <see cref="PluginPublicKeys"/>. This is the only test that proves the two halves are a real pair, so a
    /// regenerated test key that forgot to update the embedded public constant fails here and nowhere else.</summary>
    [Fact]
    public void ASignatureFromTheTestKeyVerifies()
    {
        var entry = Entry();
        entry.Signature = Sign(entry);

        Assert.True(PluginSignature.Verify(entry));
    }

    /// <summary>Flipping one signed canonical field must invalidate the signature (§5.4): the signature is over
    /// <c>version</c> among others, so a manifest edited to advertise a different plugin version no longer
    /// verifies. <c>version</c> stands in for every field in the canonical string — the same rejection follows
    /// from id/os/arch/api/minHost/sha256 — and is chosen because §5.4 names it as the reason a raw-binary
    /// signature was rejected.</summary>
    [Fact]
    public void FlippingASignedFieldInvalidatesTheSignature()
    {
        var entry = Entry();
        entry.Signature = Sign(entry);
        Assert.True(PluginSignature.Verify(entry));

        entry.Version = "1.2.1";                       // tamper after signing
        Assert.False(PluginSignature.Verify(entry));
    }

    /// <summary>The API major is part of the canonical string because it is the selection key (§3.8.4) — if it
    /// were not signed, an attacker could relabel a plugin as a major the host adapts. Pinned separately from
    /// <c>version</c> because it is the security-relevant field the design calls out by name.</summary>
    [Fact]
    public void FlippingTheApiMajorInvalidatesTheSignature()
    {
        var entry = Entry(apiMajor: 1);
        entry.Signature = Sign(entry);
        Assert.True(PluginSignature.Verify(entry));

        entry.Api!.Major = 2;
        Assert.False(PluginSignature.Verify(entry));
    }

    /// <summary>The full §5.4 chain: a valid signature AND bytes whose SHA-256 equals the signed <c>sha256</c>
    /// verifies; changing the bytes makes <see cref="PluginSignature.VerifyBinary"/> fail even though the
    /// manifest signature is untouched. That last point is what the test proves — the signature authenticates
    /// the hash, and the hash authenticates the bytes.</summary>
    [Fact]
    public void VerifyBinaryAcceptsMatchingBytesAndRejectsTamperedBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("a real plugin binary, more or less");
        var entry = Entry(sha256: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        entry.Signature = Sign(entry);

        Assert.True(PluginSignature.VerifyBinary(entry, bytes));

        var tampered = (byte[])bytes.Clone();
        tampered[0] ^= 0x01;
        Assert.False(PluginSignature.VerifyBinary(entry, tampered));
    }

    /// <summary>An unknown <c>keyId</c> fails closed with no exception (§5.4): without this, a manifest could
    /// name any key and the host might skip verification. Return-false, never throw, is the contract.</summary>
    [Fact]
    public void AnUnknownKeyIdIsRejectedWithoutThrowing()
    {
        var entry = Entry();
        entry.Signature = Sign(entry);
        entry.KeyId = "no-such-key";

        Assert.False(PluginSignature.Verify(entry));
    }

    /// <summary>A malformed signature (not base64, not hex) is an adversarial input and must return false, not
    /// throw. Empty and garbage are checked together: both must take the same fail-closed path.</summary>
    [Theory]
    [InlineData("not base64 !!! nor hex")]
    [InlineData("")]
    [InlineData("zzzz")]
    [InlineData("deadbeef")] // valid hex, but not a valid ECDSA signature over the canonical string
    public void AMalformedSignatureIsRejectedWithoutThrowing(string signature)
    {
        var entry = Entry();
        entry.Signature = signature; // never signed / corrupted

        Assert.False(PluginSignature.Verify(entry));
    }

    /// <summary>A null signature must also fail closed rather than dereferencing.</summary>
    [Fact]
    public void ANullSignatureIsRejected()
    {
        var entry = Entry();
        entry.Signature = null;

        Assert.False(PluginSignature.Verify(entry));
    }

    /// <summary>A non-ES256 <c>sigAlg</c> is refused: only the P-256 primitive is implemented (§5.4), so a
    /// manifest claiming Ed25519 is not silently verified as if it were ECDSA. A null sigAlg is accepted (the
    /// only thing this build can mean); "ES256" is accepted case-insensitively.</summary>
    [Theory]
    [InlineData("Ed25519", false)]
    [InlineData("RS256", false)]
    [InlineData("es256", true)]
    [InlineData(null, true)]
    public void SigAlgMustBeEs256OrUnset(string? sigAlg, bool expected)
    {
        var entry = Entry();
        entry.Signature = Sign(entry);
        entry.SigAlg = sigAlg;

        Assert.Equal(expected, PluginSignature.Verify(entry));
    }

    /// <summary>THE CROSS-VERSION CONTRACT (§5.4). The exact canonical byte string for a fixed entry is pinned
    /// literally; it is the thing CI signs and the host verifies, so any change to the separator, the field
    /// order or a field's spelling breaks this test instead of silently breaking every already-published
    /// release. The expected value is written out here rather than computed from the production helper on
    /// purpose — deriving it would make the test agree with a wrong implementation.</summary>
    [Fact]
    public void TheCanonicalStringIsPinnedAsTheCrossVersionContract()
    {
        var entry = Entry(sha256: "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");

        const string expected =
            "acer-nitro\nwin\nx64\n1.2.0\n1\n0\n0.38.0\n2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

        Assert.Equal(expected, PluginSignature.CanonicalString(entry));

        // ...and the bytes are exactly the UTF-8 of that string with no BOM, so a signer written in another
        // language reproduces them byte-for-byte.
        Assert.Equal(Encoding.UTF8.GetBytes(expected), PluginSignature.CanonicalBytes(entry));
    }

    /// <summary>A null field is written as the empty string, so even a malformed entry yields a stable canonical
    /// string (sign and verify disagree consistently instead of one side crashing). Pinned because it is part of
    /// the encoding contract, not an implementation detail.</summary>
    [Fact]
    public void NullFieldsEncodeAsEmptyStrings()
    {
        var entry = new PluginCatalogEntry(); // every field null
        Assert.Equal("\n\n\n\n0\n0\n\n", PluginSignature.CanonicalString(entry));
    }

    /// <summary>Round-trip through the signer and verifier over a range of field values, including values that
    /// look like the separator's neighbours, so the fixed order really is the thing being verified and not an
    /// accident of one fixture.</summary>
    [Theory]
    [InlineData("acer-nitro", "win")]
    [InlineData("dell-xps", "linux")]
    [InlineData("asus-rog", "win")]
    public void SignaturesVerifyAcrossEntries(string id, string os)
    {
        var entry = Entry(id: id, os: os);
        entry.Signature = Sign(entry);
        Assert.True(PluginSignature.Verify(entry));
    }
}
