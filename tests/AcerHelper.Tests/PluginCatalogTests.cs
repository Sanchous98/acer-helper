using System.Text.Json;
using AcerHelper.Infrastructure.Plugins.Distribution;

namespace AcerHelper.Tests;

/// <summary>
/// The distribution manifest and its selection gate (docs/vendor-plugins.md §5.2, §3.8.4 gate 1, Phase 0 task
/// T2). Two things are proven here: the §5.2 sample deserialises through the source-generated context (no
/// reflection, AOT-safe), and <see cref="PluginCatalogSelector"/> applies exactly the cheap pre-download checks
/// while never pretending to be the authority that <c>ah_matches</c> is (§3.2).
/// </summary>
public class PluginCatalogTests
{
    /// <summary>The §5.2 sample, pasted verbatim (camelCase, its own <c>//</c> comments and trailing commas), so
    /// the test proves a fixture can be copied from the doc into the tree unchanged. It carries two entries so
    /// the OS/arch filter has something to reject.</summary>
    private const string SampleCatalog = """
    {
      "schema": 1,
      "plugins": [
        {
          "id": "acer-nitro",
          "vendor": "acer",
          "os": "win",
          "arch": "x64",
          "version": "1.2.0",
          "api": { "major": 1, "minor": 0 },
          "minHost": "0.38.0",
          "matchHint": {
            "manufacturer": ["Acer"],
            "product": ["Nitro", "AN18"]
          },
          "asset": "AcerHelper.Vendor.acer-nitro-win-x64.dll",
          "url": "https://github.com/Sanchous98/acer-helper/releases/download/v0.38.0/AcerHelper.Vendor.acer-nitro-win-x64.dll",
          "sha256": "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
          "signature": "MEUCIQDexample==",
          "sig": "https://github.com/Sanchous98/acer-helper/releases/download/v0.38.0/AcerHelper.Vendor.acer-nitro-win-x64.dll.sig",
          "sigAlg": "ES256",
          "keyId": "test-p256-2026-09"
        },
        {
          "id": "dell-xps",
          "vendor": "dell",
          "os": "linux",
          "arch": "x64",
          "version": "0.1.0",
          "api": { "major": 1, "minor": 0 },
          "minHost": "0.40.0",
          "matchHint": { "manufacturer": ["Dell"], "product": ["XPS"] },
          "asset": "AcerHelper.Vendor.dell-xps-linux-x64.so",
          "url": "https://example.invalid/dell.so",
          "sha256": "aa",
          "signature": "MEUCIQDexample==",
          "sig": "https://example.invalid/dell.so.sig",
          "sigAlg": "ES256",
          "keyId": "test-p256-2026-09"
        }
      ]
    }
    """;

    private static PluginCatalog Load() =>
        JsonSerializer.Deserialize(SampleCatalog, PluginCatalogJsonContext.Default.PluginCatalog)!;

    /// <summary>Build a catalog with several entries that differ only in the axis under test, so each selector
    /// test isolates one gate.</summary>
    private static PluginCatalogEntry E(
        string id,
        string os = "win",
        string arch = "x64",
        int major = 1,
        int minor = 0,
        string minHost = "0.38.0",
        PluginMatchHint? hint = null) => new()
        {
            Id = id,
            Os = os,
            Arch = arch,
            Version = "1.0.0",
            Api = new PluginApiRange { Major = major, Minor = minor },
            MinHost = minHost,
            MatchHint = hint,
        };

    private static MachineDescriptor AcerNitro =>
        new("Acer", "Nitro AN18-61", "Acer", "AN18-61");

    /// <summary>The §5.2 sample deserialises through the generated context: every documented field, nested
    /// <c>api</c> and <c>matchHint</c> included, plus the inline <c>signature</c> the verifier consumes. If the
    /// context were missing a member, source-gen would simply leave it null and this would catch it.</summary>
    [Fact]
    public void TheSampleCatalogDeserialisesThroughTheGeneratedContext()
    {
        var catalog = Load();

        Assert.Equal(1, catalog.Schema);
        Assert.NotNull(catalog.Plugins);
        Assert.Equal(2, catalog.Plugins!.Count);

        var acer = catalog.Plugins[0];
        Assert.Equal("acer-nitro", acer.Id);
        Assert.Equal("acer", acer.Vendor);
        Assert.Equal("win", acer.Os);
        Assert.Equal("x64", acer.Arch);
        Assert.Equal("1.2.0", acer.Version);
        Assert.Equal(1, acer.Api!.Major);
        Assert.Equal(0, acer.Api.Minor);
        Assert.Equal("0.38.0", acer.MinHost);
        Assert.Equal(["Nitro", "AN18"], acer.MatchHint!.Product!);
        Assert.Equal("AcerHelper.Vendor.acer-nitro-win-x64.dll", acer.Asset);
        Assert.Contains("acer-helper/releases", acer.Url);
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", acer.Sha256);
        Assert.Equal("MEUCIQDexample==", acer.Signature);
        Assert.Equal("ES256", acer.SigAlg);
        Assert.Equal("test-p256-2026-09", acer.KeyId);
    }

    /// <summary>The OS/arch filter (§5.2): a Windows selection returns only the Windows entry and a Linux one
    /// only the Linux entry, from the same catalog.</summary>
    [Fact]
    public void SelectionMatchesOnlyTheHostsOsAndArch()
    {
        var catalog = Load();

        var win = PluginCatalogSelector.Select(catalog, "win", "x64", 1, 0, "0.38.0", AcerNitro);
        Assert.Equal(["acer-nitro"], win.Select(e => e.Id));

        var linux = PluginCatalogSelector.Select(catalog, "linux", "x64", 1, 0, "0.40.0",
            new MachineDescriptor("Dell Inc.", "XPS 15"));
        Assert.Equal(["dell-xps"], linux.Select(e => e.Id));
    }

    /// <summary>An entry whose API major is not in the supported set is not selected (§3.8.4 gate 1). The set is
    /// an explicit parameter (T3 owns current-vs-deprecated), and widening it is the only way a different major
    /// is admitted.</summary>
    [Fact]
    public void SelectionExcludesAnUnsupportedApiMajor()
    {
        var catalog = new PluginCatalog { Plugins = [E("current"), E("future", major: 2)] };

        var withCurrentOnly = PluginCatalogSelector.Select(catalog, "win", "x64", 1, 0, "1.0.0", AcerNitro);
        Assert.Equal(["current"], withCurrentOnly.Select(e => e.Id));

        // T3 widens the set to include the deprecated major; this selector must honour it without a change.
        var withDeprecated = PluginCatalogSelector.Select(catalog, "win", "x64", 1, 0, "1.0.0", AcerNitro,
            supportedMajors: new HashSet<int> { 1, 2 });
        Assert.Equal(["current", "future"], withDeprecated.Select(e => e.Id));
    }

    /// <summary><c>minHost</c> above the host version excludes the entry; equal or below includes it (§3.8.4
    /// gate 1). "0.38" proves the version comparison is numeric and not string-based.</summary>
    [Theory]
    [InlineData("0.38.0", "0.37.0", false)] // host too old
    [InlineData("0.38.0", "0.38.0", true)]  // exactly the minimum
    [InlineData("0.38.0", "0.39.1", true)]  // host newer
    [InlineData("0.38", "0.38.0.0", true)]  // normalised, not string-compared
    public void SelectionRespectsMinHost(string minHost, string hostVersion, bool expected)
    {
        var catalog = new PluginCatalog { Plugins = [E("p", minHost: minHost)] };

        var result = PluginCatalogSelector.Select(catalog, "win", "x64", 1, 0, hostVersion, AcerNitro);
        Assert.Equal(expected, result.Count == 1);
    }

    /// <summary>The advisory hint filters a definite mismatch but never confirms — and an entry with NO hint is
    /// always returned (§3.2). These are the two halves of the "advisory only" rule, tested together so the
    /// asymmetry is explicit: an Acer hint passes on an Acer, fails on a Dell, and a hintless entry passes on
    /// both.</summary>
    [Fact]
    public void MatchHintFiltersDefiniteMismatchesAndLeavesHintlessEntriesAlone()
    {
        var acerHint = new PluginMatchHint
        {
            Manufacturer = ["Acer"],
            Product = ["Nitro", "AN18"],
        };
        var catalog = new PluginCatalog
        {
            Plugins = [E("acer-nitro", hint: acerHint), E("mystery")], // "mystery" carries no hint
        };

        var onAcer = PluginCatalogSelector.Select(catalog, "win", "x64", 1, 0, "1.0.0", AcerNitro);
        Assert.Equal(["acer-nitro", "mystery"], onAcer.Select(e => e.Id));

        var onDell = PluginCatalogSelector.Select(catalog, "win", "x64", 1, 0, "1.0.0",
            new MachineDescriptor("Dell Inc.", "XPS 15"));
        Assert.Equal(["mystery"], onDell.Select(e => e.Id)); // the Acer hint was excluded, the hintless one stayed
    }

    /// <summary>A hint group with no entries imposes no constraint, and comparison is case-insensitive; a null
    /// descriptor field cannot satisfy a present group and therefore excludes.</summary>
    [Fact]
    public void HintGroupsWithNoEntriesImposeNoConstraintAndMatchingIsCaseInsensitive()
    {
        var emptyManufacturer = new PluginMatchHint { Manufacturer = [], Product = ["nitro"] };
        var catalog = new PluginCatalog { Plugins = [E("p", hint: emptyManufacturer)] };

        // "nitro" vs "Nitro AN18-61" — case-insensitive substring.
        var hit = PluginCatalogSelector.Select(catalog, "win", "x64", 1, 0, "1.0.0", AcerNitro);
        Assert.Single(hit);

        // A descriptor with no product cannot satisfy the product group, so the entry is excluded.
        var miss = PluginCatalogSelector.Select(catalog, "win", "x64", 1, 0, "1.0.0", new MachineDescriptor("Acer"));
        Assert.Empty(miss);
    }

    /// <summary>The result order is deterministic and pinned: sorted by id (§5.4-style tiebreak documented on the
    /// selector), independent of the manifest's array order. The input here is deliberately reverse-alphabetical
    /// so a pass proves the sort rather than the input order.</summary>
    [Fact]
    public void SelectionOrderIsDeterministicById()
    {
        var catalog = new PluginCatalog { Plugins = [E("zeta"), E("alpha"), E("mike")] };

        var result = PluginCatalogSelector.Select(catalog, "win", "x64", 1, 0, "1.0.0", AcerNitro);
        Assert.Equal(["alpha", "mike", "zeta"], result.Select(e => e.Id));
    }

    /// <summary>A null or empty plugin list, or an unparseable host version, degrades to no candidates rather
    /// than throwing — the same "no plugin" fallback the loader gives a missing directory (§4.1).</summary>
    [Fact]
    public void DegenerateInputsYieldNoCandidatesWithoutThrowing()
    {
        Assert.Empty(PluginCatalogSelector.Select(new PluginCatalog(), "win", "x64", 1, 0, "1.0.0", AcerNitro));
        Assert.Empty(PluginCatalogSelector.Select(
            new PluginCatalog { Plugins = [E("p")] }, "win", "x64", 1, 0, "not-a-version", AcerNitro));
    }
}
