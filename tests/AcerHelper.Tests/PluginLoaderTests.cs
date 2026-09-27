using System.Text;
using AcerHelper.Infrastructure.Plugins;
using AcerHelper.Infrastructure.Plugins.Abi;
using AcerHelper.Infrastructure.Plugins.Abi.V1;
using AcerHelper.Infrastructure.Plugins.Distribution;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The loader's selection rules (docs/vendor-plugins.md §4.1, §4.1.1, §3.8.4). Every test drives
/// <see cref="VendorPluginLoader"/> with a <see cref="FakeNativePluginBinding"/> standing in for a Native AOT
/// module (which cannot be built here), so the version gate, the confidence threshold, the tie refusal and the
/// create-status mapping are all proven without a `.dll`.
///
/// THE THREE GATES OF §3.8.4 ARE THE SPINE: gate 2 is <c>ah_abi_version</c> vs the registry's supported majors
/// (<see cref="UnsupportedMajorIsDiscardedWithUnsupportedAbi"/>), gate 3 is <c>ah_create</c> returning -2
/// (<see cref="CreateAbiMismatchIsReportedAsAbiMismatch"/>); gate 1 (the manifest / matchHint) is the caller's
/// and is covered by <c>PluginCatalogTests</c>, not here.
/// </summary>
public class PluginLoaderTests
{
    private const int Threshold = 50;
    private const string LineManifestJson =
        """{"abi":"1.0","pluginId":"acer-nitro","vendor":"acer","vendorName":"Nitro AN18-61"}""";

    private static readonly MachineDescriptor Machine =
        new(Manufacturer: "Acer", Product: "Nitro AN18-61", Board: "Acer", BoardProduct: "AN18");

    /// <summary>A loader over the shipped single-major registry (major 1, the current adapter, §3.8.2), with the
    /// caller's candidate list and a factory that resolves each path to a configured fake.</summary>
    private static VendorPluginLoader Loader(
        IReadOnlyList<string> paths,
        Func<string, INativePluginBinding?> factory,
        PluginAbiRegistry? registry = null,
        int threshold = Threshold) =>
        new(paths, factory, registry ?? PluginAbiRegistry.CreateDefault(), Machine, "0.37.0", threshold);

    /// <summary>A fake that declares the current major, matches at the given confidence and creates a valid
    /// session returning the §3.4 manifest.</summary>
    private static FakeNativePluginBinding Winning(int confidence, string id = "acer-nitro")
    {
        var fake = new FakeNativePluginBinding
        {
            PluginIdValue = id,
            MatchResult = new PluginMatchResult(Matched: true, Confidence: confidence, Reason: "test"),
            CreateManifestUtf8 = Encoding.UTF8.GetBytes(LineManifestJson),
        };
        return fake;
    }

    // ---- Gate 2: the version check is authoritative -----------------------------------------------------

    /// <summary>§3.8.4 gate 2: a plugin whose <c>ah_abi_version</c> major is not in <c>SupportedMajors</c> is
    /// refused with <see cref="PluginLoadReason.UnsupportedAbi"/> — the "matched but incompatible" status-line
    /// case (§4.1.1), distinct from an ordinary no-match.</summary>
    [Fact]
    public void UnsupportedMajorIsDiscardedWithUnsupportedAbi()
    {
        var fake = Winning(confidence: 100);
        fake.AbiVersion = (9u << 16) | 0; // major 9: no adapter exists

        var result = Loader(["p.dll"], _ => fake).Load();

        Assert.Null(result.Session);
        Assert.Equal(PluginLoadReason.UnsupportedAbi, result.Reason);
        // The DMI gate was never reached — the version gate precedes the match, so ah_matches was not called.
        Assert.Empty(fake.MatchesDescriptors);
    }

    /// <summary>§3.8.3/§3.8.4: a plugin at a DEPRECATED major still loads through its adapter (that is the whole
    /// point of the compatibility window). A synthetic <c>{5 current, 4 deprecated}</c> registry proves the path
    /// the single-major shipped host cannot reach yet.</summary>
    [Fact]
    public void DeprecatedMajorLoadsThroughItsAdapter()
    {
        var registry = new PluginAbiRegistry([new StubAdapter(5), new StubAdapter(4, deprecated: true)]);

        var fake = new FakeNativePluginBinding
        {
            AbiVersion = (4u << 16) | 0,
            MatchResult = new PluginMatchResult(true, 80, "deprecated"),
            CreateManifestUtf8 = Encoding.UTF8.GetBytes(LineManifestJson),
        };

        var result = Loader(["p.dll"], _ => fake, registry).Load();

        Assert.True(result.IsLoaded);
        Assert.Equal(80, result.Confidence);
    }

    // ---- The confidence threshold and the unique-maximum rule -------------------------------------------

    /// <summary>§3.2: a match below the threshold does not count — nothing above threshold is
    /// <see cref="PluginLoadReason.NoMatch"/>.</summary>
    [Fact]
    public void MatchBelowThresholdYieldsNoMatch()
    {
        var fake = Winning(confidence: 49); // one below the 50 threshold

        var result = Loader(["p.dll"], _ => fake).Load();

        Assert.Null(result.Session);
        Assert.Equal(PluginLoadReason.NoMatch, result.Reason);
    }

    /// <summary>The threshold is inclusive: a match exactly AT it counts.</summary>
    [Fact]
    public void MatchAtThresholdCounts()
    {
        var fake = Winning(confidence: Threshold);

        var result = Loader(["p.dll"], _ => fake).Load();

        Assert.True(result.IsLoaded);
        Assert.Equal(Threshold, result.Confidence);
    }

    /// <summary>§3.2: the unique maximum wins — two candidates at 80 and 50 select the 80, and the loser is
    /// disposed (its managed handle released; its module would stay mapped, §2.3).</summary>
    [Fact]
    public void HighestConfidenceWinsAndLosersAreDisposed()
    {
        var high = Winning(confidence: 80, id: "high");
        var low = Winning(confidence: 50, id: "low");

        var byPath = new Dictionary<string, FakeNativePluginBinding> { ["high.dll"] = high, ["low.dll"] = low };
        var result = Loader(["low.dll", "high.dll"], p => byPath[p]).Load();

        Assert.True(result.IsLoaded);
        Assert.Equal("high", result.PluginId);
        Assert.Equal(80, result.Confidence);
        Assert.Equal(0, high.DisposeCount);   // the winner's session owns it now
        Assert.Equal(1, low.DisposeCount);    // the non-winner was released
    }

    /// <summary>§3.2: "Ties are resolved by refusing to pick". Two candidates at the same top confidence yield
    /// <see cref="PluginLoadReason.AmbiguousMatch"/> and NO session, even though both individually match.</summary>
    [Fact]
    public void TieAtTopConfidenceIsRefused()
    {
        var a = Winning(confidence: 80, id: "a");
        var b = Winning(confidence: 80, id: "b");

        var byPath = new Dictionary<string, FakeNativePluginBinding> { ["a.dll"] = a, ["b.dll"] = b };
        var result = Loader(["a.dll", "b.dll"], p => byPath[p]).Load();

        Assert.Null(result.Session);
        Assert.Equal(PluginLoadReason.AmbiguousMatch, result.Reason);
        // Both were released — nothing wins on a tie, so neither is kept.
        Assert.Equal(1, a.DisposeCount);
        Assert.Equal(1, b.DisposeCount);
    }

    /// <summary>A confidence that ties only at a LOWER value does not refuse: the higher one still wins (80 vs
    /// two 50s is not a tie at the maximum).</summary>
    [Fact]
    public void TiesBelowTheMaximumDoNotRefuse()
    {
        var high = Winning(confidence: 80, id: "high");
        var low1 = Winning(confidence: 50, id: "low1");
        var low2 = Winning(confidence: 50, id: "low2");

        var byPath = new Dictionary<string, FakeNativePluginBinding>
        { ["high.dll"] = high, ["low1.dll"] = low1, ["low2.dll"] = low2 };
        var result = Loader(["low1.dll", "high.dll", "low2.dll"], p => byPath[p]).Load();

        Assert.True(result.IsLoaded);
        Assert.Equal("high", result.PluginId);
        Assert.Equal(1, low1.DisposeCount);
        Assert.Equal(1, low2.DisposeCount);
    }

    // ---- Skip and refusal paths -------------------------------------------------------------------------

    /// <summary>§4.1: a candidate whose binding factory returns null (absent / not a native image / a missing
    /// export) is skipped, and the scan continues to a usable candidate.</summary>
    [Fact]
    public void NullBindingFactoryResultIsSkipped()
    {
        var good = Winning(confidence: 80);

        var result = Loader(["missing.dll", "good.dll"], p => p == "good.dll" ? good : null).Load();

        Assert.True(result.IsLoaded);
        Assert.Equal(80, result.Confidence);
    }

    /// <summary>An empty candidate list is <see cref="PluginLoadReason.NoCandidates"/> — the silent
    /// <c>GenericDevice</c> fallback (§4.1.1), never an exception.</summary>
    [Fact]
    public void NoCandidatesIsReported()
    {
        var result = Loader([], _ => null).Load();

        Assert.Null(result.Session);
        Assert.Equal(PluginLoadReason.NoCandidates, result.Reason);
    }

    /// <summary>§3.8.4 gate 3 / §4.1.1: <c>ah_create</c> returning <c>-2 AbiMismatch</c> is surfaced as
    /// <see cref="PluginLoadReason.AbiMismatch"/> — the handshake refusal the earlier gates cannot see.</summary>
    [Fact]
    public void CreateAbiMismatchIsReportedAsAbiMismatch()
    {
        var fake = Winning(confidence: 80);
        fake.CreateStatus = AbiStatus.AbiMismatch;

        var result = Loader(["p.dll"], _ => fake).Load();

        Assert.Null(result.Session);
        Assert.Equal(PluginLoadReason.AbiMismatch, result.Reason);
        Assert.Equal(1, fake.DisposeCount); // the refused binding was released
    }

    /// <summary>Any other non-<c>Ok</c> create status is <see cref="PluginLoadReason.CreateRefused"/>, carrying
    /// the status in the detail for a log.</summary>
    [Fact]
    public void CreateRefusedForNonOkStatus()
    {
        var fake = Winning(confidence: 80);
        fake.CreateStatus = AbiStatus.Refused;

        var result = Loader(["p.dll"], _ => fake).Load();

        Assert.Null(result.Session);
        Assert.Equal(PluginLoadReason.CreateRefused, result.Reason);
        Assert.Contains("1", result.Detail); // the status is in the detail
        Assert.Equal(1, fake.DisposeCount);
    }

    /// <summary>A successful create but a manifest the winning adapter cannot decode is
    /// <see cref="PluginLoadReason.ManifestInvalid"/>, not a null-manifest session.</summary>
    [Fact]
    public void UndecodableManifestIsReportedAsManifestInvalid()
    {
        var fake = Winning(confidence: 80);
        fake.CreateManifestUtf8 = Encoding.UTF8.GetBytes("not json");

        var result = Loader(["p.dll"], _ => fake).Load();

        Assert.Null(result.Session);
        Assert.Equal(PluginLoadReason.ManifestInvalid, result.Reason);
        Assert.Equal(1, fake.DisposeCount);
    }

    // ---- The happy path ---------------------------------------------------------------------------------

    /// <summary>§4.1 step 5: a winner's <c>ah_create</c> Ok returns a session carrying the DECODED manifest and
    /// the winning pluginId/confidence, and the binding stays alive (owned by the session, disposed later).</summary>
    [Fact]
    public void CreateOkReturnsSessionWithDecodedManifest()
    {
        var fake = Winning(confidence: 100);
        fake.CreateStatus = AbiStatus.Ok;

        var result = Loader(["p.dll"], _ => fake).Load();

        Assert.True(result.IsLoaded);
        Assert.Equal("acer-nitro", result.PluginId);
        Assert.Equal(100, result.Confidence);
        Assert.Equal(0, fake.DisposeCount); // owned by the session now

        var session = result.Session!;
        Assert.Equal("acer-nitro", session.Manifest.PluginId);
        Assert.Equal("Nitro AN18-61", session.Manifest.VendorName);

        // The descriptor fed to ah_matches is the four-field §3.2 JSON with the machine's values.
        var desc = Assert.Single(fake.MatchesDescriptors);
        Assert.Contains("\"manufacturer\":\"Acer\"", desc);
        Assert.Contains("\"product\":\"Nitro AN18-61\"", desc);

        // The ah_create request carries the descriptor PLUS the host's generated API version (§3.4, gate 3).
        var create = Assert.Single(fake.CreateDescriptors);
        Assert.Contains($"\"major\":{PluginApi.Major}", create);
        Assert.Contains($"\"minor\":{PluginApi.Minor}", create);
    }

    /// <summary>The loader is deterministic on the same inputs: two runs over the same candidate set give the
    /// same winner and confidence.</summary>
    [Fact]
    public void SelectionIsDeterministic()
    {
        static PluginLoadResult Run()
        {
            var high = Winning(confidence: 80, id: "high");
            var low = Winning(confidence: 50, id: "low");
            var byPath = new Dictionary<string, FakeNativePluginBinding> { ["high.dll"] = high, ["low.dll"] = low };
            return Loader(["low.dll", "high.dll"], p => byPath[p]).Load();
        }

        var first = Run();
        var second = Run();

        Assert.Equal(first.PluginId, second.PluginId);
        Assert.Equal(first.Confidence, second.Confidence);
    }

    /// <summary>A module that cannot answer <c>ah_abi_version</c> at all is skipped (§4.1) rather than aborting
    /// the scan.</summary>
    [Fact]
    public void ModuleThatCannotAnswerAbiVersionIsSkipped()
    {
        var broken = Winning(confidence: 80);
        broken.AbiVersionFails = true;
        var good = Winning(confidence: 60, id: "good");

        var byPath = new Dictionary<string, FakeNativePluginBinding> { ["broken.dll"] = broken, ["good.dll"] = good };
        var result = Loader(["broken.dll", "good.dll"], p => byPath[p]).Load();

        Assert.True(result.IsLoaded);
        Assert.Equal("good", result.PluginId);
        Assert.Empty(broken.MatchesDescriptors);
    }

    /// <summary>§4.1.1: the search-path enumeration is the dev override first, then the per-user cache, with
    /// empty entries dropped. Pure, so it is pinned here without touching the real machine.</summary>
    [Fact]
    public void SearchPathsPutTheOverrideFirstAndDropEmpties()
    {
        Assert.Equal([@"C:\dev", @"C:\cache"], LoaderPaths(@"C:\dev", @"C:\cache"));
        Assert.Equal([@"C:\cache"], LoaderPaths(null, @"C:\cache"));
        Assert.Equal([@"C:\dev"], LoaderPaths(@"C:\dev", ""));
        Assert.Empty(LoaderPaths(null, null));

        static IReadOnlyList<string> LoaderPaths(string? ov, string? cache) =>
            VendorPluginLoader.EnumerateSearchPaths(ov, cache);
    }

    /// <summary>A pass-through adapter used only by the deprecated-major test to build a two-major registry; the
    /// real <see cref="AbiV1Adapter"/> cannot be deprecated, and this test needs the registry shape, not the
    /// translation.</summary>
    private sealed class StubAdapter(int major, bool deprecated = false) : IPluginAbiAdapter
    {
        public int Major { get; } = major;
        public bool Deprecated { get; } = deprecated;

        public PluginManifest DecodeManifest(ReadOnlySpan<byte> utf8Json) =>
            System.Text.Json.JsonSerializer.Deserialize(utf8Json, PluginJsonContext.Default.PluginManifest)!;

        public string EncodeRequest(uint capability, uint op, string internalRequestJson) => internalRequestJson;
        public string? DecodeResponse(uint capability, uint op, string? wireResponseJson) => wireResponseJson;
    }
}
