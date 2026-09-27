using System.Text;
using System.Text.Json;
using AcerHelper.Infrastructure.Plugins;
using AcerHelper.Infrastructure.Plugins.Abi;
using AcerHelper.Infrastructure.Plugins.Abi.V1;

namespace AcerHelper.Tests;

/// <summary>
/// The per-major adapter registry and the §3.8.4 gate-2 compatibility decision (docs/vendor-plugins.md §3.8.2,
/// §3.8.3, §3.8.5). Nothing here drives hardware or a native library: the point is that the "hold current +
/// deprecated major at once" mechanism is provable with ordinary objects, and that the CURRENT-major adapter is
/// the identity because the internal model IS the current major's model (§3.8.2).
///
/// The synthetic two-major registry is the load-bearing test: today the host ships a single major (§3.8.2 "Status
/// today"), so the deprecated path would otherwise never run. Building a fake <c>{5 current, 4 deprecated}</c>
/// registry exercises exactly the code path the first real bump will take, without waiting for that bump.
/// </summary>
public class PluginAbiVersioningTests
{
    // ---- Test doubles --------------------------------------------------------------------------------------
    // A minimal adapter whose identity is its (major, deprecated) pair; request/response pass through, which is
    // enough to prove the registry routes to the RIGHT instance. DecodeManifest is real (source-gen) so the type
    // is a faithful stand-in rather than a stub that cannot decode.

    private sealed class FakeAdapter(int major, bool deprecated = false) : IPluginAbiAdapter
    {
        public int Major { get; } = major;
        public bool Deprecated { get; } = deprecated;

        public PluginManifest DecodeManifest(ReadOnlySpan<byte> utf8Json) =>
            JsonSerializer.Deserialize(utf8Json, PluginJsonContext.Default.PluginManifest)!;

        public string EncodeRequest(uint capability, uint op, string internalRequestJson) => internalRequestJson;

        public string? DecodeResponse(uint capability, uint op, string? wireResponseJson) => wireResponseJson;
    }

    // ---- Default registry: single current major today -------------------------------------------------------

    /// <summary>§3.8.2 "Status today": the shipped host carries ONE adapter, for <c>PluginApi.Major</c>, and the
    /// deprecated slot is empty. The last two lines are the gate-2 lookup contract — the current major resolves,
    /// any other major (here the next one) resolves to null, which is the refusal.</summary>
    [Fact]
    public void DefaultRegistryHoldsOnlyTheCurrentMajorToday()
    {
        var registry = PluginAbiRegistry.CreateDefault();

        Assert.Equal(PluginApi.Major, registry.CurrentMajor);
        Assert.Equal([PluginApi.Major], registry.SupportedMajors.ToArray());

        var current = registry.ForMajor(PluginApi.Major);
        Assert.NotNull(current);
        Assert.False(current!.Deprecated);
        Assert.Null(registry.ForMajor(PluginApi.Major + 1));
    }

    /// <summary>THE BUMP GUARD. The V1 adapter is major 1 and the default registry's current major is the
    /// generated <c>PluginApi.Major</c>. If someone bumps <c>&lt;PluginApiVersion&gt;</c> without adding/repointing
    /// an adapter, <c>CreateDefault</c> throws (asserted below) and these two facts stop agreeing — so a forgotten
    /// adapter change reddens here rather than shipping a host that refuses its own plugins.</summary>
    [Fact]
    public void V1AdapterIsMajorOneAndTheDefaultRegistryMatchesPluginApi()
    {
        var v1 = new AbiV1Adapter();
        Assert.Equal(1, v1.Major);
        Assert.False(v1.Deprecated);

        Assert.Equal(PluginApi.Major, PluginAbiRegistry.CreateDefault().CurrentMajor);
        // CreateDefault's guard makes the invariant explicit rather than incidental.
        Assert.Equal(PluginApi.Major, v1.Major);
    }

    // ---- Synthetic two-major registry: the shape after the first bump ---------------------------------------

    /// <summary>§3.8.2/§3.8.3 after the first bump: <c>{ current, deprecated }</c>, each major resolving to its own
    /// adapter instance, the deprecated one flagged. This is the code path the shipped single-major host cannot
    /// reach until it bumps, proven here instead.</summary>
    [Fact]
    public void TwoMajorRegistryRoutesEachMajorToItsOwnAdapterAndFlagsTheDeprecatedOne()
    {
        var current = new FakeAdapter(major: 5, deprecated: false);
        var deprecated = new FakeAdapter(major: 4, deprecated: true);
        var registry = new PluginAbiRegistry([current, deprecated]);

        Assert.Equal(5, registry.CurrentMajor);
        Assert.Equal([4, 5], registry.SupportedMajors.ToArray()); // ascending, deterministic

        Assert.Same(current, registry.ForMajor(5));
        Assert.Same(deprecated, registry.ForMajor(4));
        Assert.True(registry.ForMajor(4)!.Deprecated);
        Assert.False(registry.ForMajor(5)!.Deprecated);
        Assert.Null(registry.ForMajor(6));
    }

    /// <summary>Construction rules are fail-fast: a malformed adapter set is a build configuration bug. Each case
    /// is stated so the reason it is illegal is the assertion's message, not folklore — two current adapters are
    /// illegal because the internal model must be ONE major's model (§3.8.2), and two deprecated ones exceed the
    /// current+previous window (§3.8.2).</summary>
    [Fact]
    public void MalformedAdapterSetsAreRejected()
    {
        // No current (only a deprecated) — the internal model has no home.
        Assert.Throws<ArgumentException>(() => new PluginAbiRegistry([new FakeAdapter(4, deprecated: true)]));
        // Two current adapters — ambiguous internal model.
        Assert.Throws<ArgumentException>(() => new PluginAbiRegistry([new FakeAdapter(4), new FakeAdapter(5)]));
        // Two deprecated adapters — wider than the current+previous window.
        Assert.Throws<ArgumentException>(
            () => new PluginAbiRegistry([new FakeAdapter(5), new FakeAdapter(4, true), new FakeAdapter(3, true)]));
        // Duplicate major.
        Assert.Throws<ArgumentException>(() => new PluginAbiRegistry([new FakeAdapter(5), new FakeAdapter(5, true)]));
        // Major 0 is not a shipped API.
        Assert.Throws<ArgumentException>(() => new PluginAbiRegistry([new FakeAdapter(0)]));
    }

    // ---- Gate-2 compatibility decision ----------------------------------------------------------------------

    /// <summary>A plugin at the host's current major is compatible via the CURRENT adapter (§3.8.4 gate 2).</summary>
    [Fact]
    public void PluginAtCurrentMajorIsCompatibleViaTheCurrentAdapter()
    {
        var verdict = AbiCompatibility.Decide(PluginAbiRegistry.CreateDefault(), PluginApi.Major);

        Assert.True(verdict.IsCompatible);
        Assert.False(verdict.IsDeprecated);
        Assert.False(verdict.IsIncompatible);
        Assert.Equal(AbiCompatibilityReason.SupportedCurrent, verdict.Reason);
    }

    /// <summary>A plugin one major BEHIND the host, with a deprecated adapter present, still loads — but the
    /// verdict says so, which is what lets the caller log/telemeter "running a deprecated plugin" (§3.8.3).</summary>
    [Fact]
    public void PluginOneMajorBehindWithDeprecatedAdapterIsCompatibleButDeprecated()
    {
        var registry = new PluginAbiRegistry([new FakeAdapter(5, deprecated: false), new FakeAdapter(4, deprecated: true)]);

        var verdict = AbiCompatibility.Decide(registry, pluginMajor: 4);

        Assert.True(verdict.IsCompatible);
        Assert.True(verdict.IsDeprecated);
        Assert.Equal(AbiCompatibilityReason.SupportedDeprecated, verdict.Reason);
    }

    /// <summary>A major NEWER than the host has no adapter — refused (the §3.8.4 gate-2 case a stale manifest
    /// cannot see). It must be incompatible AND not deprecated.</summary>
    [Fact]
    public void PluginAtUnknownNewerMajorIsIncompatible()
    {
        var verdict = AbiCompatibility.Decide(PluginAbiRegistry.CreateDefault(), PluginApi.Major + 1);

        Assert.False(verdict.IsCompatible);
        Assert.True(verdict.IsIncompatible);
        Assert.False(verdict.IsDeprecated);
        Assert.Equal(AbiCompatibilityReason.UnsupportedMajor, verdict.Reason);
    }

    /// <summary>The §3.8.3 removal case: a major one behind the host with NO deprecated adapter (e.g. host at 3
    /// after major 1 was removed) is unsupported exactly like a newer one — the reason carries it.</summary>
    [Fact]
    public void PluginAtRemovedMajorWithoutDeprecatedAdapterIsIncompatible()
    {
        var registry = new PluginAbiRegistry([new FakeAdapter(3, deprecated: false)]);

        var verdict = AbiCompatibility.Decide(registry, pluginMajor: 1);

        Assert.False(verdict.IsCompatible);
        Assert.Equal(AbiCompatibilityReason.UnsupportedMajor, verdict.Reason);
    }

    /// <summary>§3.8.5: the minor is informational and must NEVER gate. The same major with the lowest and the
    /// highest 16-bit minor decodes to the SAME verdict (there is deliberately no minor parameter to misuse), and
    /// the encoded split recovers major/minor from the raw <c>ah_abi_version</c> (§3.2).</summary>
    [Fact]
    public void MinorIsInformationalAndTheEncodedVersionSplitsCorrectly()
    {
        Assert.Equal(2, AbiCompatibility.MajorOf((2 << 16) | 7));
        Assert.Equal(7, AbiCompatibility.MinorOf((2 << 16) | 7));

        var registry = new PluginAbiRegistry([new FakeAdapter(2, deprecated: false)]);
        // A plugin built for a much newer minor of the current major is accepted (additive, §3.8.5).
        var newerMinor = AbiCompatibility.DecideFromEncoded(registry, (2 << 16) | 0xFFFF);
        var olderMinor = AbiCompatibility.DecideFromEncoded(registry, (2 << 16) | 0);
        Assert.True(newerMinor.IsCompatible);
        Assert.True(olderMinor.IsCompatible);
        Assert.Equal(AbiCompatibilityReason.SupportedCurrent, newerMinor.Reason);
        Assert.Equal(newerMinor.Reason, olderMinor.Reason);
    }

    // ---- The current adapter is the identity (§3.8.2) -------------------------------------------------------

    /// <summary>The V1 adapter decodes the §3.4 sample, because it IS the internal model's shape. A round-trip of
    /// the sample proves the adapter reaches the generated context (no reflection, §2.2/§3.3) and that a future
    /// adapter must preserve this baseline.</summary>
    [Fact]
    public void V1AdapterDecodesTheSection34Manifest()
    {
        const string json = """
        {
          "abi": "1.0",
          "pluginId": "acer-nitro",
          "vendor": "acer",
          "vendorName": "Nitro AN18-61",
          "statusMessage": "status.linux_mainline_full",
          "capabilities": {
            "powerProfiles": {
              "all": [{"id":"6","displayName":"profile.eco"}],
              "currentId": "1",
              "availableOnAc": ["0","1","4","5"],
              "availableOnBattery": ["2","1"],
              "traits": [{"id":"5","kind":"Turbo","accent":[156,39,176],"flash":[255,0,199]}]
            },
            "fan": {"capability":{"hasMax":true,"hasCustom":true,"hasGpuFan":true}},
            "sensors": {},
            "hotkeys": {},
            "battery": {"chargeLimit":true,"calibration":true,"chargeMode":false,"powerSource":true},
            "declaredSettings": [{"key":"lcd_override","shape":"flag","readbackVerifiesWrite":false}]
          }
        }
        """;

        var manifest = new AbiV1Adapter().DecodeManifest(Encoding.UTF8.GetBytes(json));

        Assert.Equal("1.0", manifest.Abi);
        Assert.Equal("acer-nitro", manifest.PluginId);
        Assert.Equal("Nitro AN18-61", manifest.VendorName);

        var caps = manifest.Capabilities;
        Assert.NotNull(caps);
        Assert.Equal("1", caps!.PowerProfiles!.CurrentId);
        Assert.Equal([156, 39, 176], caps.PowerProfiles.Traits![0].Accent!);
        Assert.NotNull(caps.Sensors);
        Assert.NotNull(caps.Hotkeys);
        Assert.True(caps.Battery!.ChargeLimit);
        Assert.Equal("lcd_override", caps.DeclaredSettings![0].Key);
    }

    /// <summary>The current adapter is a PASS-THROUGH on the invoke path: its request/response encoders return
    /// the internal-model JSON unchanged (including the no-body <c>null</c>). The (capability, op) pair is ignored
    /// by V1 precisely because V1 needs no remapping; a future adapter's own test asserts its translation
    /// instead.</summary>
    [Fact]
    public void V1AdapterPassesRequestsAndResponsesThroughUnchanged()
    {
        var v1 = new AbiV1Adapter();

        const string request = """{"id":"4"}""";
        Assert.Same(request, v1.EncodeRequest(Capability.Power, Operation.Power.Set, request));

        const string response = """{"id":"1"}""";
        Assert.Same(response, v1.DecodeResponse(Capability.Power, Operation.Power.Current, response));

        // A no-body result stays no-body rather than becoming the string "null".
        Assert.Null(v1.DecodeResponse(Capability.Fan, Operation.Fan.SetMode, null));
    }
}
