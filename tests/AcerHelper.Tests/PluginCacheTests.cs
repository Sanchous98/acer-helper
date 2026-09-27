using System.Security.Cryptography;
using System.Text;
using AcerHelper.Infrastructure.Plugins.Distribution;

namespace AcerHelper.Tests;

/// <summary>
/// The local per-user plugin cache (docs/vendor-plugins.md §5.3, §5.3.1, §5.4, Phase D1). It is a pure, network-free
/// store — no download, no GitHub, no startup wiring — so these tests are the whole proof of the layer: install a
/// signed asset with a verified hash, find it, re-verify it locally, and prove every tamper/failure degrades to
/// false/null WITHOUT touching a previously good copy.
///
/// EVERY TEST USES A TEMP ROOT (<see cref="TempCacheRoot"/>) AND NEVER <c>%AppData%</c>. The cache writes real
/// files and moves them over each other, so a test pointed at the real location would destroy a user's cached
/// plugins and could be observed by a running app. The explicit-root constructor is the seam that makes this
/// possible; the real-root factory is tested for shape only.
/// </summary>
public class PluginCacheTests
{
    // The well-known TEST keypair whose public half is embedded in PluginPublicKeys (PluginPublicKeys.TestKeyId).
    // Deliberately the SAME value as PluginSignatureTests' private key: this suite reuses the one in-tree test key
    // rather than generating a fresh keypair, so the assets it signs verify against the embedded public constant
    // exactly as a real signed release would. That also makes a mis-regenerated test key fail here as well as in
    // PluginSignatureTests.
    private const string TestPrivateKeyPkcs8 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgaKZC5OZ+wR2KEjeKFbVxOKgpuR/D1UeMphPqgBKAtMWhRANCAARU+/uqGJLftH2B4qKvh8eJJJKzbcrLt8+wemlROmj0zsD3jMk/lKqHR+4MXBbtTrkK5zfpfNXG5ZBNzY/Yea24";

    /// <summary>The asset bytes for a plugin. A fixed non-empty blob; the tests that need tampering clone it.</summary>
    private static byte[] Asset(string marker = "a real plugin binary, more or less") =>
        Encoding.UTF8.GetBytes(marker);

    /// <summary>
    /// Build a COMPLETE, SIGNED entry over <paramref name="bytes"/>, exactly as CI would: fill the metadata fields
    /// the §5.4 canonical string covers, set <c>sha256</c> to the bytes' digest, then sign. This is the only way
    /// <see cref="PluginCache.Install"/> may accept an asset, so it is the shared positive fixture.
    /// </summary>
    private static PluginCatalogEntry SignedEntry(
        byte[] bytes,
        string id = "acer-nitro",
        string os = "win",
        string arch = "x64",
        string version = "1.2.0",
        int apiMajor = 1,
        int apiMinor = 0,
        string? asset = null)
    {
        var entry = new PluginCatalogEntry
        {
            Id = id,
            Os = os,
            Arch = arch,
            Version = version,
            Api = new PluginApiRange { Major = apiMajor, Minor = apiMinor },
            MinHost = "0.38.0",
            Asset = asset ?? $"AcerHelper.Vendor.{id}-{os}-{arch}.dll",
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            KeyId = PluginPublicKeys.TestKeyId,
            SigAlg = "ES256",
        };
        entry.Signature = PluginSignature.Sign(entry, TestPrivateKeyPkcs8);
        return entry;
    }

    /// <summary>A scratch cache root per test, under the system temp folder. Non-negotiable rather than tidy: the
    /// cache writes real files and atomically renames over them, so a test pointed at the real per-user root would
    /// destroy the user's cached plugins (the same reason <c>JsonSettingsStore</c>'s TempDir exists).</summary>
    private sealed class TempCacheRoot : IDisposable
    {
        public TempCacheRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "acer-helper-plugin-cache-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public PluginCache NewCache() => new(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir; best effort */ }
        }
    }

    /// <summary>Round-trip: a valid signed asset installs, is found by id+major, the local hash check passes, and
    /// the resolved path points at the exact bytes — the whole happy path of §5.3 step 4→5.</summary>
    [Fact]
    public void InstallThenFindThenVerifyRoundTrips()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var bytes = Asset();
        var entry = SignedEntry(bytes);

        Assert.True(cache.Install(entry, bytes));

        var record = cache.FindInstalled("acer-nitro", 1);
        Assert.NotNull(record);
        Assert.Equal("acer-nitro", record!.Id);
        Assert.Equal(1, record.Api!.Major);
        Assert.Equal("1.2.0", record.Version);
        Assert.Equal(entry.Sha256, record.Sha256);
        Assert.Equal(PluginPublicKeys.TestKeyId, record.KeyId);
        Assert.NotNull(record.InstalledAt);

        Assert.True(cache.VerifyInstalled(record));

        var path = cache.ResolveInstalledPath(record);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(bytes, File.ReadAllBytes(path!));
    }

    /// <summary>The record is written under the documented layout (<c>&lt;id&gt;/v&lt;major&gt;/</c>) with the
    /// asset and sidecar side by side, and the asset name is the manifest's — the shape §5.3.1 names.</summary>
    [Fact]
    public void InstallWritesTheDocumentedLayout()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var bytes = Asset();
        var entry = SignedEntry(bytes);

        Assert.True(cache.Install(entry, bytes));

        var dir = Path.Combine(temp.Root, "acer-nitro", "v1");
        Assert.True(File.Exists(Path.Combine(dir, "AcerHelper.Vendor.acer-nitro-win-x64.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "installed.json")));
    }

    /// <summary>Tamper: flipping one byte of the STORED asset makes the local hash check false (the at-rest
    /// integrity half of §5.4/§5.3 step 5), with no exception. The record itself is untouched, so this isolates
    /// "bytes changed" from "metadata changed".</summary>
    [Fact]
    public void TamperingTheStoredAssetFailsLocalVerification()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var bytes = Asset();
        var entry = SignedEntry(bytes);
        Assert.True(cache.Install(entry, bytes));

        var record = cache.FindInstalled("acer-nitro", 1)!;
        var path = cache.ResolveInstalledPath(record)!;

        var stored = File.ReadAllBytes(path);
        stored[0] ^= 0x01;
        File.WriteAllBytes(path, stored);

        Assert.False(cache.VerifyInstalled(record));
        Assert.False(PluginSignature.VerifyBinary(entry, File.ReadAllBytes(path)));
    }

    /// <summary>Atomic failure, the load-bearing property: an install with a BAD SIGNATURE is refused AND a
    /// previously installed good copy survives byte-for-byte. The refusal happens before any write, so there is no
    /// window in which the good asset is gone.</summary>
    [Fact]
    public void InstallWithBadSignatureIsRefusedAndLeavesTheGoodCopyIntact()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var goodBytes = Asset("the good original");
        var goodEntry = SignedEntry(goodBytes);
        Assert.True(cache.Install(goodEntry, goodBytes));
        var recordPath = cache.RecordPath("acer-nitro", 1)!;
        var recordBefore = File.ReadAllBytes(recordPath);

        // A DIFFERENT asset, correctly signed, then its signature corrupted after signing: the sha256 is honest
        // but the detached signature is not, so the §5.4 chain fails.
        var badBytes = Asset("a newer, forged build");
        var badEntry = SignedEntry(badBytes, version: "9.9.9");
        badEntry.Signature = Convert.ToBase64String(new byte[64]); // not a signature over the canonical string

        Assert.False(cache.Install(badEntry, badBytes));

        // The old good copy and its record are exactly as before; the forged bytes were never written.
        var recordAfter = cache.FindInstalled("acer-nitro", 1);
        Assert.NotNull(recordAfter);
        Assert.Equal("1.2.0", recordAfter!.Version);
        Assert.True(cache.VerifyInstalled(recordAfter));
        Assert.Equal(recordBefore, File.ReadAllBytes(recordPath));
        Assert.Equal(goodBytes, File.ReadAllBytes(cache.ResolveInstalledPath(recordAfter)!));
    }

    /// <summary>The same atomic refusal for a WRONG HASH: a validly signed entry whose <c>sha256</c> does not match
    /// the bytes is refused by <c>VerifyBinary</c> even though the signature itself is valid. This is the bytes
    /// half of the chain, isolated from the signature half.</summary>
    [Fact]
    public void InstallWithMismatchedHashIsRefusedAndLeavesTheGoodCopyIntact()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var goodBytes = Asset("the good original");
        Assert.True(cache.Install(SignedEntry(goodBytes), goodBytes));

        // Sign an entry that CLAIMS the digest of different bytes, then hand it yet different bytes.
        var claimed = Asset("the bytes the entry claims");
        var entry = SignedEntry(claimed, version: "9.9.9");
        var actual = Asset("the bytes actually offered");

        Assert.False(cache.Install(entry, actual));

        var record = cache.FindInstalled("acer-nitro", 1);
        Assert.NotNull(record);
        Assert.Equal("1.2.0", record!.Version);
        Assert.True(cache.VerifyInstalled(record));
    }

    /// <summary>A missing <c>installed.json</c> yields null and a false verify, with no throw: a machine with no
    /// cached plugin is the ordinary state, not an error (§5.3.1).</summary>
    [Fact]
    public void MissingRecordYieldsNullAndFalseWithoutThrowing()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();

        Assert.Null(cache.FindInstalled("acer-nitro", 1));
        Assert.False(cache.VerifyInstalled(cache.FindInstalled("acer-nitro", 1)));
        Assert.Null(cache.ResolveInstalledPath(null));
        Assert.Null(cache.ResolveInstalledPath(cache.FindInstalled("acer-nitro", 1)));
    }

    /// <summary>A corrupt <c>installed.json</c> (not JSON at all) yields null, never a deserialisation exception:
    /// a truncated or hand-edited sidecar is an adversarial input on the startup path, so it must fail closed the
    /// way <see cref="PluginSignature"/> catches.</summary>
    [Fact]
    public void CorruptRecordYieldsNullWithoutThrowing()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var dir = Path.Combine(temp.Root, "acer-nitro", "v1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "installed.json"), "{ this is not json ");

        var record = cache.FindInstalled("acer-nitro", 1);
        Assert.Null(record);
        Assert.False(cache.VerifyInstalled(record));
    }

    /// <summary>A record with the right metadata but no asset file on disk resolves to null (missing) and verifies
    /// false; a truncated asset likewise. Both are the "not installed/not verified" degradation §5.3.1 requires.</summary>
    [Fact]
    public void MetadataWithoutAssetResolvesNullAndVerifiesFalse()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var bytes = Asset();
        var entry = SignedEntry(bytes);
        Assert.True(cache.Install(entry, bytes));

        var record = cache.FindInstalled("acer-nitro", 1)!;
        File.Delete(cache.ResolveInstalledPath(record)!);

        Assert.Null(cache.ResolveInstalledPath(record));
        Assert.False(cache.VerifyInstalled(record));
    }

    /// <summary>Two API majors coexist independently (§5.3 step 3, §3.8.3): installing major 1 then major 2 leaves
    /// BOTH findable and verifiable, addressed by the major key. This is the deprecation-window requirement.</summary>
    [Fact]
    public void TwoApiMajorsCoexistIndependently()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var bytes1 = Asset("api major one build");
        var bytes2 = Asset("api major two build");
        var entry1 = SignedEntry(bytes1, apiMajor: 1, asset: "plugin-v1.dll");
        var entry2 = SignedEntry(bytes2, apiMajor: 2, asset: "plugin-v2.dll");

        Assert.True(cache.Install(entry1, bytes1));
        Assert.True(cache.Install(entry2, bytes2));

        var record1 = cache.FindInstalled("acer-nitro", 1);
        var record2 = cache.FindInstalled("acer-nitro", 2);
        Assert.NotNull(record1);
        Assert.NotNull(record2);
        Assert.Equal(1, record1!.Api!.Major);
        Assert.Equal(2, record2!.Api!.Major);

        Assert.True(cache.VerifyInstalled(record1));
        Assert.True(cache.VerifyInstalled(record2));

        // The two assets are distinct files under distinct major folders and hold distinct bytes.
        var path1 = cache.ResolveInstalledPath(record1)!;
        var path2 = cache.ResolveInstalledPath(record2)!;
        Assert.NotEqual(path1, path2);
        Assert.Equal(bytes1, File.ReadAllBytes(path1));
        Assert.Equal(bytes2, File.ReadAllBytes(path2));
    }

    /// <summary>The full §5.4 chain at rest: a cached good copy verifies against the ORIGINAL manifest entry
    /// (signature + hash over the stored bytes), and the chain refuses when the entry does not describe the cached
    /// artefact — here the entry advertises a different version, so record↔entry binding fails before the
    /// signature is even consulted.</summary>
    [Fact]
    public void FullSignatureChainVerifiesAgainstTheManifestEntry()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var bytes = Asset();
        var entry = SignedEntry(bytes);
        Assert.True(cache.Install(entry, bytes));

        var record = cache.FindInstalled("acer-nitro", 1)!;
        Assert.True(cache.VerifyInstalled(record, entry));

        // An entry that is validly signed but for a DIFFERENT asset must not verify against this record.
        var otherEntry = SignedEntry(Asset("a different build"), version: "2.0.0");
        Assert.False(cache.VerifyInstalled(record, otherEntry));
        Assert.False(cache.VerifyInstalled(record, null));
    }

    /// <summary>An unsafe plugin id (a path separator or <c>..</c>) is rejected rather than rewritten: the cache
    /// must not be able to write outside its root, and silently sanitising would make the folder disagree with the
    /// id the loader reads from <c>ah_plugin_id</c> (§4.1.1).</summary>
    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("")]
    public void UnsafePluginIdIsRefusedWithoutWriting(string id)
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var bytes = Asset();
        var entry = SignedEntry(bytes, id: id);

        Assert.False(cache.Install(entry, bytes));
        Assert.Null(cache.FindInstalled(id, 1));
        // Nothing escaped the root: it is still empty (or contains only nothing).
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Root));
    }

    /// <summary>The explicit-root constructor honours the given root, and the real-root factory returns a
    /// non-empty absolute path (the shape check only — it never writes there).</summary>
    [Fact]
    public void RootIsHonouredAndTheDefaultRootIsAnAbsolutePath()
    {
        using var temp = new TempCacheRoot();
        Assert.Equal(temp.Root, temp.NewCache().Root);

        var real = PluginCache.DefaultRoot();
        // ApplicationData is always defined on the platforms this ships to; if it ever is not, null is the
        // documented "no cache" and the caller treats it as such.
        Assert.NotNull(real);
        Assert.False(string.IsNullOrWhiteSpace(real));
        Assert.True(Path.IsPathRooted(real!));
        Assert.Contains("AcerHelper", real!);
    }

    /// <summary>Installing from a downloaded temp file path (D2's downloader shape) behaves identically to the
    /// byte-array overload, and a missing temp file is refused without throwing.</summary>
    [Fact]
    public void InstallFromTempFilePathMatchesTheByteArrayOverload()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var bytes = Asset();
        var entry = SignedEntry(bytes);

        var downloaded = Path.Combine(temp.Root, "download.tmp");
        File.WriteAllBytes(downloaded, bytes);

        Assert.True(cache.Install(entry, downloaded));
        Assert.True(cache.VerifyInstalled(cache.FindInstalled("acer-nitro", 1)!));
        Assert.False(cache.Install(entry, Path.Combine(temp.Root, "does-not-exist.tmp")));
    }

    /// <summary>A second, newer install of the same id+major replaces the first atomically: the record and bytes
    /// both move to the new version, so staleness resolution (§5.3 step 3) can overwrite safely.</summary>
    [Fact]
    public void ReinstallingTheSameKeyReplacesTheRecordAndAsset()
    {
        using var temp = new TempCacheRoot();
        var cache = temp.NewCache();
        var first = Asset("version one");
        var second = Asset("version two");

        Assert.True(cache.Install(SignedEntry(first, version: "1.0.0"), first));
        Assert.True(cache.Install(SignedEntry(second, version: "2.0.0"), second));

        var record = cache.FindInstalled("acer-nitro", 1)!;
        Assert.Equal("2.0.0", record.Version);
        Assert.True(cache.VerifyInstalled(record));
        Assert.Equal(second, File.ReadAllBytes(cache.ResolveInstalledPath(record)!));
    }
}
