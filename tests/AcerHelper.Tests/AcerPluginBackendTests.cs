using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Acer;
using AcerHelper.Vendor.AcerNitro;

namespace AcerHelper.Tests;

/// <summary>
/// THE ACER-NITRO BACKEND GUARDS (docs/vendor-plugins.md §3.2, §3.4, §6 Phase 1 step 3). Three claim groups:
///
///   1. THE <c>ah_matches</c> CONFIDENCE MAPPING (§3.2) — driven through the plugin's own pure
///      <see cref="AcerMatch"/>, compiled into this test assembly by the test csproj's source-include.
///   2. THE MANIFEST the backend builds (§3.4) — driven through the pure <see cref="AcerManifestBuilder"/> with a
///      hand-built <see cref="AcerManifestInput"/>, so "declares exactly the capabilities wired, and nothing more"
///      is asserted without hardware. The same builder the session calls (it only gathers the input there).
///   3. THE NO-HOST-DEPENDENCY SOURCE GUARD — the plugin sources must not name <c>Loc</c>/<c>Localization</c>,
///      <c>GenericDevice</c>, <c>LaptopService</c>, <c>Composition</c> or <c>UI</c>. Read as TEXT (the plugin is a
///      separate AOT assembly this project must not reference), with comments stripped so the tree's own prose
///      that EXPLAINS the boundary does not redden the guard — the technique <c>PluginBoundaryGuardTests</c> uses.
/// </summary>
public class AcerPluginBackendTests
{
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    // ==========================================================================================================
    // 1. ah_matches — the §3.2 confidence mapping
    // ==========================================================================================================

    /// <summary>The four documented confidence levels (§3.2), on the current single-entry DB (AN18/Nitro 18):
    /// not-this-machine 0, Acer-only 50, product-substring 80, product+board 100. The line gate is the
    /// <c>acer-models.json</c> <c>Match</c> set (here "AN18"/"Nitro 18"), read through <see cref="AcerModels"/>.</summary>
    [Theory]
    [InlineData("Dell", "XPS 15", null, null, false, 0)]              // not this machine
    [InlineData("Acer", "Swift SF314", null, null, true, 50)]          // Acer, unlisted product
    [InlineData("Acer", "Nitro AN18-61", null, null, true, 80)]        // product names the line
    [InlineData("Acer", "Nitro AN18-61", "Acer", "AN18", true, 100)]   // product + board product agree
    [InlineData("Dell", "Nitro 18", null, null, false, 0)]             // substring, but not an Acer
    [InlineData("Acer", null, null, null, true, 50)]                   // Acer, no product: line unknown
    public void MatchesReturnsTheDocumentedConfidence(
        string? manufacturer, string? product, string? board, string? boardProduct, bool match, int confidence)
    {
        var (m, c, reason) = AcerMatch.Decide(manufacturer, product, board, boardProduct);
        Assert.Equal(match, m);
        Assert.Equal(confidence, c);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    /// <summary>A malformed or non-object descriptor is "not this machine" with confidence 0, never a throw —
    /// the host treats a failed <c>ah_matches</c> as a discard (§4.3).</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void MalformedDescriptorIsNotAMatch(string descriptor)
    {
        var (m, c, _) = AcerMatch.DecideFromJson(descriptor);
        Assert.False(m);
        Assert.Equal(0, c);
    }

    /// <summary>The camelCase descriptor the host builds (§3.2) is read field for field. The body is the
    /// documented <c>{"match":…,"confidence":…,"reason":"…"}</c> shape the host decodes.</summary>
    [Fact]
    public void MatchesReadsTheHostDescriptorAndBuildsTheDocumentedBody()
    {
        const string descriptor =
            """{"manufacturer":"Acer","product":"Nitro AN18-61","board":"Acer","boardProduct":"AN18"}""";
        var (match, confidence, reason) = AcerMatch.DecideFromJson(descriptor);
        Assert.True(match);
        Assert.Equal(100, confidence);

        var body = AcerMatch.BuildResponse(match, confidence, reason);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("match").GetBoolean());
        Assert.Equal(100, doc.RootElement.GetProperty("confidence").GetInt32());
        Assert.True(doc.RootElement.GetProperty("reason").ValueKind == JsonValueKind.String);
    }

    /// <summary>The handshake gate reads the host's API major from the <c>ah_create</c> body (§3.8.4 gate 3), and
    /// answers null when the body carries none (an older host is treated as compatible).</summary>
    [Fact]
    public void CreateDescriptorExposesTheHostApiMajor()
    {
        Assert.Equal(2, AcerMatch.HostMajor("""{"abi":{"major":2,"minor":0},"product":"Nitro AN18-61"}"""));
        Assert.Null(AcerMatch.HostMajor("""{"product":"Nitro AN18-61"}"""));
        Assert.Null(AcerMatch.HostMajor("garbage"));
        Assert.Equal("Nitro AN18-61", AcerMatch.Product("""{"product":"Nitro AN18-61"}"""));
    }

    // ==========================================================================================================
    // 2. the manifest — declares exactly what was wired, never more
    // ==========================================================================================================

    private static JsonElement ManifestOf(AcerManifestInput input)
        => JsonDocument.Parse(AcerManifestBuilder.Build(input)).RootElement.Clone();

    /// <summary>A maximal input: every capability wired. Asserts the exact manifest the host reads — the profile
    /// table's ids + traits + per-source availability (derived from <c>AcerProfiles</c>, not restated here), the
    /// fan capability, the presence-only markers, the battery flags, the RGB zones with positional effect handles,
    /// the MUX modes and the declared settings.</summary>
    [Fact]
    public void ManifestDeclaresEveryWiredCapability()
    {
        var input = new AcerManifestInput(
            PluginId: "acer-nitro", Abi: "1.0", Vendor: "acer", ModelName: "Acer Nitro 18",
            StatusMessage: "status.acer_wmi_unavailable",
            Profiles: AcerProfiles.All, CurrentProfileId: "1",
            Fan: new FanCapability(HasMax: true, HasCustom: true, HasGpuFan: true),
            Sensors: true, ChargeLimit: true, Calibration: true, PowerSource: true,
            RgbZones:
            [
                new AcerRgbZone("Keyboard", 4, CanFollowProfile: false, ReadBrightness: true,
                    RgbEffects.Keyboard.Select(e => e.ToModeInfo()).ToList()),
                new AcerRgbZone("Lightbar", 1, CanFollowProfile: true, ReadBrightness: false,
                    RgbEffects.Lightbar.Select(e => e.ToModeInfo()).ToList()),
            ],
            RgbProfileFollowKey: "acer.lightbarFollowsProfile",
            GpuMux: true, GpuMuxSupported: true,
            GpuMuxModes: [new("1", "gpu.mode_hybrid"), new("2", "gpu.mode_discrete"), new("3", "gpu.mode_auto")],
            Settings:
            [
                new AcerSettingDeclaration("lcd_override", "flag", null, ReadbackVerifiesWrite: false),
                new AcerSettingDeclaration("usb_charging", "choice",
                    [new("0", "level.off"), new("10", "10%")], ReadbackVerifiesWrite: true),
            ]);

        var root = ManifestOf(input);
        Assert.Equal("acer-nitro", root.GetProperty("pluginId").GetString());
        Assert.Equal("acer", root.GetProperty("vendor").GetString());
        Assert.Equal("Acer Nitro 18", root.GetProperty("vendorName").GetString());
        Assert.Equal("status.acer_wmi_unavailable", root.GetProperty("statusMessage").GetString());
        Assert.Equal("1.0", root.GetProperty("abi").GetString());

        var caps = root.GetProperty("capabilities");

        var power = caps.GetProperty("powerProfiles");
        Assert.Equal(["6", "0", "1", "4", "5"], power.GetProperty("all").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal("1", power.GetProperty("currentId").GetString());
        // AcerProfiles.IsAvailable — the same table policy the host's AcerMappedProfiles declares.
        Assert.Equal(["0", "1", "4", "5"], power.GetProperty("availableOnAc").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["6", "1"], power.GetProperty("availableOnBattery").EnumerateArray().Select(x => x.GetString()));
        var turbo = power.GetProperty("traits").EnumerateArray().First(t => t.GetProperty("id").GetString() == "5");
        Assert.Equal("Turbo", turbo.GetProperty("kind").GetString());
        Assert.True(turbo.GetProperty("accent").GetArrayLength() == 3);

        var fan = caps.GetProperty("fan").GetProperty("capability");
        Assert.True(fan.GetProperty("hasMax").GetBoolean());
        Assert.True(fan.GetProperty("hasCustom").GetBoolean());
        Assert.True(fan.GetProperty("hasGpuFan").GetBoolean());

        Assert.True(caps.TryGetProperty("sensors", out _));   // presence is the declaration

        var battery = caps.GetProperty("battery");
        Assert.True(battery.GetProperty("chargeLimit").GetBoolean());
        Assert.True(battery.GetProperty("calibration").GetBoolean());
        Assert.False(battery.GetProperty("chargeMode").GetBoolean());
        Assert.True(battery.GetProperty("powerSource").GetBoolean());

        var rgb = caps.GetProperty("rgb");
        Assert.Equal("acer.lightbarFollowsProfile", rgb.GetProperty("profileFollowKey").GetString());
        var zones = rgb.GetProperty("zones").EnumerateArray().ToArray();
        Assert.Equal(2, zones.Length);
        Assert.Equal("Keyboard", zones[0].GetProperty("name").GetString());
        Assert.Equal(4, zones[0].GetProperty("subZones").GetInt32());
        Assert.True(zones[0].GetProperty("readBrightness").GetBoolean());
        Assert.True(zones[1].GetProperty("canFollowProfile").GetBoolean());
        Assert.False(zones[1].GetProperty("readBrightness").GetBoolean());
        // Positional effect handles: the host's RgbModeInfo.Handle indexes back into this same list.
        var effects = zones[0].GetProperty("effects").EnumerateArray().ToArray();
        Assert.Equal(effects.Length, RgbEffects.Keyboard.Length);
        for (var i = 0; i < effects.Length; i++)
            Assert.Equal(i, effects[i].GetProperty("handle").GetInt32());

        var mux = caps.GetProperty("gpuMux");
        Assert.True(mux.GetProperty("supported").GetBoolean());
        Assert.Equal(3, mux.GetProperty("modes").GetArrayLength());

        var settings = caps.GetProperty("declaredSettings").EnumerateArray().ToArray();
        Assert.Equal(2, settings.Length);
        Assert.Equal("lcd_override", settings[0].GetProperty("key").GetString());
        Assert.Equal("flag", settings[0].GetProperty("shape").GetString());
        Assert.False(settings[0].GetProperty("readbackVerifiesWrite").GetBoolean());
        Assert.Equal("usb_charging", settings[1].GetProperty("key").GetString());
        Assert.Equal("choice", settings[1].GetProperty("shape").GetString());
        Assert.Equal(2, settings[1].GetProperty("options").GetArrayLength());
    }

    /// <summary>THE HONEST-MINIMUM CASE, and the core of the slice's hard rule: an input with NOTHING wired
    /// declares NO capability. Every optional sub-object is absent (not null-valued, absent), which is how the host
    /// hides a capability (§4.2). A plugin that could not probe its hardware must not claim a capability it cannot
    /// answer.</summary>
    [Fact]
    public void ManifestOmitsEveryCapabilityThatWasNotWired()
    {
        var input = new AcerManifestInput(
            PluginId: "acer-nitro", Abi: "1.0", Vendor: "acer", ModelName: "Acer",
            StatusMessage: "status.acer_wmi_unavailable",
            Profiles: null, CurrentProfileId: null, Fan: null, Sensors: false,
            ChargeLimit: false, Calibration: false, PowerSource: false,
            RgbZones: null, RgbProfileFollowKey: null,
            GpuMux: false, GpuMuxSupported: false, GpuMuxModes: null,
            Settings: null);

        var caps = ManifestOf(input).GetProperty("capabilities");
        foreach (var absent in new[] { "powerProfiles", "fan", "sensors", "battery", "rgb", "gpuMux", "declaredSettings" })
            Assert.True(IsAbsent(caps, absent), $"'{absent}' must be absent (or null) when nothing was wired");
    }

    /// <summary>"Absent" for the host is "missing OR null": <c>PluginVendorDevice</c> tests each capability with
    /// <c>is { } x</c>, so a null sub-object and a missing key are the same statement — "this machine does not have
    /// it" (§4.2). The serializer emits nulls (the context sets no WhenWritingNull), so both are asserted.</summary>
    private static bool IsAbsent(JsonElement parent, string name)
        => !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    /// <summary>A wired-but-UNSUPPORTED MUX is still declared, with <c>supported:false</c> and no modes — the host
    /// attaches the port either way so its shared card can state the refusal rather than the section vanish
    /// (AcerDevice.cs:26-29). And a partial battery declares only the flags that were wired.</summary>
    [Fact]
    public void UnsupportedMuxAndPartialBatteryAreDeclaredAsSuch()
    {
        var input = new AcerManifestInput(
            PluginId: "acer-nitro", Abi: "1.0", Vendor: "acer", ModelName: "Acer", StatusMessage: null,
            Profiles: null, CurrentProfileId: null, Fan: null, Sensors: false,
            ChargeLimit: true, Calibration: false, PowerSource: false,
            RgbZones: null, RgbProfileFollowKey: null,
            GpuMux: true, GpuMuxSupported: false, GpuMuxModes: null, Settings: null);

        var caps = ManifestOf(input).GetProperty("capabilities");

        var mux = caps.GetProperty("gpuMux");
        Assert.False(mux.GetProperty("supported").GetBoolean());
        Assert.True(IsAbsent(mux, "modes"), "an unsupported mux declares no modes");

        var battery = caps.GetProperty("battery");
        Assert.True(battery.GetProperty("chargeLimit").GetBoolean());
        Assert.False(battery.GetProperty("calibration").GetBoolean());
        Assert.False(battery.GetProperty("powerSource").GetBoolean());

        Assert.True(IsAbsent(caps, "rgb"), "no zones -> no rgb capability");
    }

    // ==========================================================================================================
    // 3. the no-host-dependency source guard
    // ==========================================================================================================

    private static List<string> PluginSources()
    {
        var dir = Path.Combine(Root(), "plugins", "AcerHelper.Vendor.acer-nitro");
        Assert.True(Directory.Exists(dir), "the plugin tree is gone — this guard is looking at nothing");
        var sep = Path.DirectorySeparatorChar;
        // EXCLUDE THE REFERENCE-ONLY WIRING FILES. P4 moved the whole Acer tree into Acer/ (so Infrastructure/
        // Vendors/Acer/ is empty), but the csproj deliberately does NOT compile the host-coupled ones: AcerDevice.*
        // extend the host GenericDevice, AcerBattery.Windows.cs is superseded by the ported AcerBatteryWmi, and
        // AcerProfilePorts.cs uses Loc.T. They are kept in-tree only as the diffable reference the AcerSession.*
        // port cites ("cites the host line it came from"), so they must not be read as part of the plugin's compiled
        // surface — the guard is about what the plugin SHIPS, and the csproj proves they are not shipped.
        string[] referenceOnly = ["AcerDevice.cs", "AcerDevice.Windows.cs", "AcerDevice.Linux.cs",
                                  "AcerBattery.Windows.cs", "AcerProfilePorts.cs"];
        return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{sep}obj{sep}") && !p.Contains($"{sep}bin{sep}"))
            .Where(p => !referenceOnly.Contains(Path.GetFileName(p), StringComparer.Ordinal))
            .ToList();
    }

    /// <summary>Strip <c>//</c> and <c>/* */</c> comments before searching: the plugin's own prose deliberately
    /// NAMES the host types it must not use ("never GenericDevice, LaptopService, Localization"), and a name in a
    /// comment is documentation, not a dependency — the same narrower reading <c>PluginBoundaryGuardTests</c> takes.</summary>
    private static string StripComments(string text)
    {
        var noBlock = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\r\n]*", "");
    }

    /// <summary>THE HARD BOUNDARY (§6 Phase 1 constraint 1): the plugin must not depend on the host. No
    /// <c>Loc.</c>/<c>Localization</c>, no <c>GenericDevice</c>, no <c>LaptopService</c>, no <c>Composition</c> and
    /// no <c>UI</c> may appear in CODE (comments excluded). The localization boundary is the sharpest one: settings
    /// and errors travel as KEYS, never as sentences looked up through <c>Loc.T</c> (§3.4, constraint 3).</summary>
    [Fact]
    public void PluginSourcesNameNoHostType()
    {
        string[] forbidden =
        [
            "AcerHelper.Localization", "Loc.",
            "GenericDevice", "LaptopService",
            "AcerHelper.Infrastructure.Composition", "AcerHelper.UI",
        ];

        var offenders = new List<string>();
        foreach (var path in PluginSources())
        {
            var code = StripComments(File.ReadAllText(path));
            offenders.AddRange(forbidden
                .Where(token => code.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetFileName(path)}: {token}"));
        }

        Assert.True(offenders.Count == 0,
            "the acer-nitro plugin must not depend on the host (GenericDevice/LaptopService/Localization/Composition/UI) "
            + "— settings and errors travel as keys, not sentences (§3.4, §6 Phase 1):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>The plugin csproj's explicit Acer include list carries the ENE codec but still NOT the wiring
    /// files or the host-coupled policy file — <c>AcerProfilePorts.cs</c> (Loc.T) and the <c>AcerDevice.*</c> /
    /// <c>AcerBattery.Windows.cs</c> partials of <c>GenericDevice</c>. Re-asserted here because P4 moved the Acer
    /// tree into the plugin and changed the include list to bare in-tree <c>Acer\*.cs</c> paths.</summary>
    [Fact]
    public void PluginStillExcludesHostCoupledAcerFiles()
    {
        var csproj = File.ReadAllText(Path.Combine(Root(), "plugins", "AcerHelper.Vendor.acer-nitro",
                                                   "AcerHelper.Vendor.acer-nitro.csproj"));
        foreach (var excluded in new[]
                 { "AcerDevice.cs", "AcerDevice.Windows.cs", "AcerDevice.Linux.cs", "AcerBattery.Windows.cs", "AcerProfilePorts.cs" })
            Assert.DoesNotContain($"Compile Include=\"Acer\\{excluded}\"", csproj);

        // ...and DOES include the ENE codec, which the RGB capability now uses.
        Assert.Contains("Acer\\EneHidController.cs", csproj);
        Assert.Contains("Acer\\EneHidController.Windows.cs", csproj);
        Assert.Contains("Acer\\EneHidController.Linux.cs", csproj);
    }
}
