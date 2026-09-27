using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AcerHelper.Infrastructure.Plugins.Distribution;

namespace AcerHelper.Tests;

/// <summary>
/// THE CI SIGNER GUARD (docs/vendor-plugins.md §5.4 "Code signing and trust", §5.5 "CI"; task D3).
///
/// WHAT THIS PROVES, AND WHY IT IS NOT A RUNTIME TEST OF THE TOOL. The signer (<c>tools/PluginSign</c>) is a
/// separate project that cannot be referenced by the suite and whose whole job is to produce bytes the HOST
/// verifies. Two facts matter and both are checked from the source/build side, the same technique
/// <see cref="PluginSoakHostSourceTests"/> uses:
///
///   1. THE TOOL USES THE HOST'S CODE, NOT A PRIVATE COPY. The canonical string is the cross-version contract
///      (§5.4); if the tool re-implemented it, the two could drift and every published release would fail to
///      verify on an already-shipped client. So the csproj must SOURCE-INCLUDE
///      <c>PluginSignature.cs</c>/<c>PluginCatalog.cs</c>/<c>PluginPublicKeys.cs</c> (the §3.1/§4.4 source-share
///      pattern) and Program.cs must CALL <c>PluginSignature.Sign</c>/<c>Verify</c> rather than containing a
///      separator literal or its own join.
///   2. THE TOOL'S OUTPUT VERIFIES UNDER THE HOST VERIFIER. A synthetic entry is built EXACTLY as Program.cs
///      builds it, signed with the test key (<see cref="PluginSignatureTests"/>'s pair), serialised through the
///      same <see cref="PluginCatalogJsonContext"/>, parsed back, and required to pass
///      <see cref="PluginSignature.Verify"/>/<see cref="PluginSignature.VerifyBinary"/> — plus the tamper
///      rejection.
///
/// HONEST LIMIT: this does not execute the tool (no process spawn; the suite stays offline). It proves the tool
/// compiles against the host's contract and that the entry shape it emits verifies. The only thing a tagged CI
/// run adds is the real secret and the real AOT bytes.
/// </summary>
public class PluginSignerTests
{
    private const string Csproj = "tools/PluginSign/PluginSign.csproj";
    private const string Source = "tools/PluginSign/Program.cs";

    // The well-known TEST private key (identical to PluginSignatureTests's): published in-tree on purpose so the
    // guard can sign without the CI secret. The signer's own fail-loud test uses the PRODUCTION keyId, which the
    // test key cannot satisfy — see PluginReleaseWorkflowTests for the policy.
    private const string TestPrivateKeyPkcs8 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgaKZC5OZ+wR2KEjeKFbVxOKgpuR/D1UeMphPqgBKAtMWhRANCAARU+/uqGJLftH2B4qKvh8eJJJKzbcrLt8+wemlROmj0zsD3jMk/lKqHR+4MXBbtTrkK5zfpfNXG5ZBNzY/Yea24";

    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string FileText(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }

    /// <summary>THE SOURCE-INCLUDE IS THE ANTI-DRIFT MECHANISM (§5.4). The signer csproj must compile in the
    /// host's own PluginSignature/PluginCatalog/PluginPublicKeys files. A future "helpful" move to a local copy
    /// reddens this instead of silently splitting the signer from the verifier.</summary>
    [Fact]
    public void SignerCsprojSourceIncludesTheHostSignatureAndCatalogFiles()
    {
        var csproj = FileText(Csproj);

        foreach (var file in new[] { "PluginSignature.cs", "PluginCatalog.cs", "PluginPublicKeys.cs" })
            Assert.Contains(
                $"<Compile Include=\"..\\..\\Infrastructure\\Plugins\\Distribution\\{file}\"",
                csproj,
                StringComparison.Ordinal);

        // It must NOT reference the host project: a ProjectReference would drag Avalonia/UI into a build tool and
        // would not export the internal signer code anyway (the §2.1 published-assembly export rule).
        Assert.DoesNotContain("<ProjectReference", csproj, StringComparison.Ordinal);
        // Dependency-free: no NuGet (BCL crypto only), per the task brief.
        Assert.DoesNotContain("<PackageReference", csproj, StringComparison.Ordinal);
    }

    /// <summary>Program.cs CALLS the host's signer/verifier and does NOT carry its own canonical string. The
    /// separator literal and the field-join belong to PluginSignature only; if either appears in the tool, the
    /// cross-version contract has been duplicated.</summary>
    [Fact]
    public void SignerCallsTheHostCryptoAndDoesNotReImplementTheCanonicalString()
    {
        var source = FileText(Source);

        Assert.Contains("PluginSignature.Sign(", source, StringComparison.Ordinal);
        Assert.Contains("PluginSignature.Verify(", source, StringComparison.Ordinal);
        Assert.Contains("PluginSignature.VerifyBinary(", source, StringComparison.Ordinal);
        Assert.Contains("PluginCatalogJsonContext.Default.PluginCatalog", source, StringComparison.Ordinal);

        // The tell-tale of a private copy: the canonical-string API or its separator field. Neither may appear.
        // (A bare `string.Join(` is NOT checked — the tool legitimately joins a log line — so the guard pins the
        // contract's names, which are what a re-implementation would have to reproduce.)
        Assert.DoesNotContain("CanonicalString", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CanonicalBytes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Separator", source, StringComparison.Ordinal);

        // The signing key is read from the environment only (never argv), so it cannot leak into a process list.
        Assert.Contains("PLUGIN_SIGNING_KEY", source, StringComparison.Ordinal);
        Assert.Contains("Environment.GetEnvironmentVariable", source, StringComparison.Ordinal);
    }

    /// <summary>Build a minimal but complete entry the way <c>Program.Sign</c> does, using the test key. Kept as a
    /// helper because the two behavioural tests below must sign the SAME shape the tool emits.</summary>
    private static PluginCatalogEntry Entry(string? sha256)
    {
        var entry = new PluginCatalogEntry
        {
            Id = "acer-nitro",
            Vendor = "acer",
            Os = "win",
            Arch = "x64",
            Version = "1.0.0",
            Api = new PluginApiRange { Major = 1, Minor = 0 },
            MinHost = "0.38.0",
            MatchHint = new PluginMatchHint { Manufacturer = ["Acer"], Product = ["Nitro", "AN18"] },
            Asset = "AcerHelper.Vendor.acer-nitro-win-x64.dll",
            Sha256 = sha256,
            KeyId = PluginPublicKeys.TestKeyId, // the test key, so the guard needs no CI secret
            SigAlg = "ES256",
        };
        entry.Signature = PluginSignature.Sign(entry, TestPrivateKeyPkcs8);
        return entry;
    }

    /// <summary>THE BEHAVIOURAL PROOF (§5.4 chain): the entry the tool emits — signed over the host's canonical
    /// string, serialised through the host's source-generated context — verifies under the host's verifier, and
    /// the binary half verifies against the exact bytes hashed into it.</summary>
    [Fact]
    public void SignerShapedOutputVerifiesUnderTheHostVerifier()
    {
        var bytes = Encoding.UTF8.GetBytes("a real plugin binary, more or less");
        var entry = Entry(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

        // Serialise + re-parse through the same context the manifest is written/read with (§5.2), so the guard
        // covers the wire round-trip and not just the in-memory object.
        var fragment = new PluginCatalog { Schema = 1, Plugins = [entry] };
        var json = JsonSerializer.Serialize(fragment, PluginCatalogJsonContext.Default.PluginCatalog);
        var parsed = JsonSerializer.Deserialize(json, PluginCatalogJsonContext.Default.PluginCatalog)!;

        var reparsed = parsed.Plugins![0];
        Assert.True(PluginSignature.Verify(reparsed), "the signer-shaped entry must verify under the host verifier");
        Assert.True(PluginSignature.VerifyBinary(reparsed, bytes), "the signed sha256 must match the asset bytes");
    }

    /// <summary>A tampered signed field must invalidate the entry — the §5.4 reason the signature is over the
    /// entry's metadata and not the bare bytes. <c>version</c> stands in for every canonical field; the API major
    /// (the selection key, §3.8.4) is checked beside it.</summary>
    [Fact]
    public void ATamperedSignerEntryFailsTheHostVerifier()
    {
        var bytes = Encoding.UTF8.GetBytes("bytes");
        var entry = Entry(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

        var tamperedVersion = Entry(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        tamperedVersion.Version = "9.9.9";
        Assert.False(PluginSignature.Verify(tamperedVersion));

        var tamperedMajor = Entry(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        tamperedMajor.Api!.Major = 2;
        Assert.False(PluginSignature.Verify(tamperedMajor));

        // ...and changing the bytes under an untouched signature fails the binary half of the chain.
        var movedBytes = (byte[])bytes.Clone();
        movedBytes[0] ^= 0x01;
        Assert.False(PluginSignature.VerifyBinary(entry, movedBytes));
    }

    /// <summary>The signer's release keyId must be the host's PRODUCTION id (§5.4/§7.3 item 2): a release signed
    /// under any other id would be verifiable only against the wrong embedded key. Pinned as a constant so a
    /// rename of the dated token reddens here.</summary>
    [Fact]
    public void TheReleaseKeyIdIsTheHostsProductionKey()
    {
        Assert.Equal("2026-09-release", PluginPublicKeys.ReleaseKeyId);
        var source = FileText(Source);
        Assert.Contains("PluginPublicKeys.ReleaseKeyId", source, StringComparison.Ordinal);
    }
}
