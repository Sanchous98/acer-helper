using System.Text.Json;
using AcerHelper.Infrastructure.Plugins;
using AcerHelper.Infrastructure.Plugins.Abi;

namespace AcerHelper.Tests;

/// <summary>
/// The shared ABI foundation (docs/vendor-plugins.md §3, Phase 0 task T1). Nothing here drives hardware or the
/// loader: these tests pin the LITERAL WIRE VALUES — the version encoding, the status/op/name constants and the
/// manifest round-trip — because those are the things a rename or a renumber silently breaks for every plugin
/// at once, and they are exactly the surfaces the compiler cannot check across a source-shared boundary.
///
/// The export-NAME assertions are the strongest example: the host resolves them with
/// <c>NativeLibrary.GetExport</c> and a plugin declares them in its own <c>[UnmanagedCallersOnly]</c>, so a
/// typo on either side is a load-time miss with no compile-time error. Pinning the strings here makes the
/// rename a test failure instead (§3.2, §6).
/// </summary>
public class PluginAbiFoundationTests
{
    /// <summary>The generated <c>PluginApi</c> const must be the <c>(major &lt;&lt; 16) | minor</c> encoding the
    /// C ABI defines (§3.2), and the shipped API is 1.0 (§3.8.2). If the generator target ever wrote the two
    /// halves in the wrong order or shifted the wrong one, the handshake gate would compare two different
    /// numbers and refuse every plugin — so this checks the arithmetic as well as the value.</summary>
    [Fact]
    public void PluginApiEncodesMajorAndMinorAsTheAbiVersionDoes()
    {
        Assert.Equal(1, PluginApi.Major);
        Assert.Equal(0, PluginApi.Minor);
        Assert.Equal((PluginApi.Major << 16) | PluginApi.Minor, PluginApi.Encoded);
        Assert.Equal(0x00010000, PluginApi.Encoded);
    }

    /// <summary>The status codes are wire values the host branches on without parsing the body (§3.2), so their
    /// numbers are part of the contract. <c>Internal</c> and <c>AbiMismatch</c> are negatives BY DESIGN, which
    /// the second half pins so neither can drift into the positive space a documented status occupies.</summary>
    [Fact]
    public void StatusConstantsHaveTheirDocumentedValues()
    {
        Assert.Equal(0, AbiStatus.Ok);
        Assert.Equal(1, AbiStatus.Refused);
        Assert.Equal(2, AbiStatus.NotSupported);
        Assert.Equal(3, AbiStatus.InvalidArg);
        Assert.Equal(-1, AbiStatus.Internal);
        Assert.Equal(-2, AbiStatus.AbiMismatch);

        // The two failure codes that are neither a hardware answer nor a host bug are negative, so the host's
        // "is this a plugin fault?" reading is a sign check and not a lookup that could miss a new code.
        Assert.True(AbiStatus.Internal < 0);
        Assert.True(AbiStatus.AbiMismatch < 0);
    }

    /// <summary>The capability codes name the vendor-overridable slots (§3.5); a renumber would route a call to
    /// the wrong capability. Spot-checked across the range rather than exhaustively, because the point is the
    /// numbering scheme, not the enumeration.</summary>
    [Fact]
    public void CapabilityConstantsHaveTheirDocumentedValues()
    {
        Assert.Equal(1u, Capability.Power);
        Assert.Equal(4u, Capability.Battery);
        Assert.Equal(6u, Capability.Rgb);
        Assert.Equal(9u, Capability.Settings);
    }

    /// <summary>The per-capability op numbers are what <c>ah_invoke</c> routes on (§3.5). The two named in the
    /// task are the ones that make the "same integer, different capability" rule concrete: 3 is Power.Set and
    /// 6 is Battery.ReadPowerSource, and they are only distinguishable by the capability the host passes
    /// alongside.</summary>
    [Fact]
    public void OperationConstantsHaveTheirDocumentedValues()
    {
        Assert.Equal(1u, Operation.Power.Selectable);
        Assert.Equal(3u, Operation.Power.Set);
        Assert.Equal(5u, Operation.Power.Traits);
        Assert.Equal(1u, Operation.Fan.SetMode);
        Assert.Equal(2u, Operation.Fan.SetCustomSpeeds);
        Assert.Equal(6u, Operation.Battery.ReadPowerSource);
        Assert.Equal(5u, Operation.Rgb.ReadBrightness);
        Assert.Equal(2u, Operation.Settings.Set);
    }

    /// <summary>The eight export names ARE the ABI's linker contract (§3.2). The host resolves them by string
    /// and a plugin exports them by string, so this is the guard that a rename cannot silently break every
    /// plugin at load time — the failure the §6 export smoke test would only catch on a built binary.</summary>
    [Fact]
    public void EntryPointNameConstantsMatchTheAbiContract()
    {
        Assert.Equal("ah_abi_version", VendorAbi.AbiVersion);
        Assert.Equal("ah_plugin_id", VendorAbi.PluginId);
        Assert.Equal("ah_matches", VendorAbi.Matches);
        Assert.Equal("ah_create", VendorAbi.Create);
        Assert.Equal("ah_invoke", VendorAbi.Invoke);
        Assert.Equal("ah_free", VendorAbi.Free);
        Assert.Equal("ah_dispose", VendorAbi.Dispose);
        Assert.Equal("ah_set_event_sink", VendorAbi.SetEventSink);
    }

    /// <summary>The event-sink kinds are the two §3.5 names; the adapter's marshalling switches on them.</summary>
    [Fact]
    public void EventKindConstantsHaveTheirDocumentedValues()
    {
        Assert.Equal(1u, VendorAbi.EventKind.Pressed);
        Assert.Equal(2u, VendorAbi.EventKind.InputActivity);
    }

    /// <summary>The manifest is the one wire type the foundation ships, so a round-trip through the generated
    /// context proves three things at once: the context handles the manifest at all (no reflection fallback),
    /// camelCase serialises every nested member as §3.4 spells it, and the deserializer accepts the exact
    /// sample shape — comments, trailing commas and nulls included. If any of that were wrong, every adapter
    /// built on this context (T5) would fail before it reached hardware.</summary>
    [Fact]
    public void PluginManifestRoundTripsThroughTheGeneratedContext()
    {
        // The §3.4 sample, pasted as-is (camelCase + its own // comments + trailing commas), because that is
        // how a plugin author will copy it into a fixture.
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
            "keyboardBrightness": {"maxLevel":2},
            "rgb": {
              "profileFollowKey": "acer.lightbarFollowsProfile",
              "zones": [{"name":"Keyboard","subZones":4,"canFollowProfile":false,
                         "effects":[{"name":"Static","hasColor":true,"hasSpeed":false,"handle":1}],
                         "readBrightness":true}]
            },
            "hotkeys": {},
            "gpuMux": {"supported":true,
                       "modes":[{"id":"1","displayName":"gpu.mode_hybrid"}],
                       "current":"1","pending":null},
            "battery": {"chargeLimit":true,"calibration":true,"chargeMode":false,"powerSource":true},
            "curveOptimizerOverride": null,
            "declaredSettings": [
              {"key":"lcd_override","shape":"flag","readbackVerifiesWrite":false},
              {"key":"usb_charging","shape":"choice","options":[{"id":"0","displayName":"level.off"}]}
            ]
          }
        }
        """;

        var manifest = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest);

        Assert.NotNull(manifest);
        Assert.Equal("1.0", manifest!.Abi);
        Assert.Equal("acer-nitro", manifest.PluginId);
        Assert.Equal("status.linux_mainline_full", manifest.StatusMessage);

        var caps = manifest.Capabilities;
        Assert.NotNull(caps);
        var power = caps!.PowerProfiles;
        Assert.NotNull(power);
        Assert.Equal("1", power!.CurrentId);
        Assert.Equal(["0", "1", "4", "5"], power.AvailableOnAc);
        var traits = power.Traits!;
        Assert.Equal("Turbo", traits[0].Kind);
        Assert.Equal([156, 39, 176], traits[0].Accent!);

        // A presence marker really is present-and-empty, not null — the host distinguishes the two.
        Assert.NotNull(caps.Sensors);
        Assert.NotNull(caps.Hotkeys);
        // ...and a null override is null, which is "keep the generic implementation".
        Assert.Null(caps.CurveOptimizerOverride);

        Assert.Equal(2, caps.KeyboardBrightness!.MaxLevel);
        Assert.True(caps.Fan!.Capability!.HasGpuFan);
        Assert.Equal(4, caps.Rgb!.Zones![0].SubZones);
        Assert.True(caps.Rgb.Zones[0].ReadBrightness);

        var battery = caps.Battery;
        Assert.NotNull(battery);
        Assert.True(battery!.ChargeLimit);
        Assert.False(battery.ChargeMode);

        Assert.Equal("lcd_override", caps.DeclaredSettings![0].Key);
        Assert.False(caps.DeclaredSettings[0].ReadbackVerifiesWrite);
        Assert.Equal("choice", caps.DeclaredSettings[1].Shape);
        Assert.Equal("level.off", caps.DeclaredSettings[1].Options![0].DisplayName);

        // And the round-trip: re-serialising produces camelCase keys the plugin side can read.
        var reserialized = JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest);
        Assert.Contains("\"pluginId\":\"acer-nitro\"", reserialized);
        Assert.Contains("\"readbackVerifiesWrite\":false", reserialized);
    }
}
