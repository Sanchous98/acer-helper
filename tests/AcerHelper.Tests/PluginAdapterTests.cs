using System.Runtime.CompilerServices;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Plugins;
using AcerHelper.Infrastructure.Plugins.Abi;
using AcerHelper.Infrastructure.Plugins.Adapters;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The managed capability adapters (docs/vendor-plugins.md §3.4, §3.5, §4.2): each is driven against a
/// <see cref="FakePluginSession"/> so the tests are about FORWARDING (the right (capability, op) pair, the right
/// body) and MAPPING (the wire body onto the host type) — never about the loader, the binding or a native library.
/// The session boundary is exactly where <see cref="IPluginSession"/>'s own note puts it: "a fake session returns
/// canned PluginCallResults exactly as the in-tree fake ports stand in for hardware".
///
/// What each test pins, in one line:
///   * the MANIFEST members (<c>All</c>, <c>Traits</c>, fan <c>Capability</c>, <c>MaxLevel</c>, MUX modes) are
///     read without calling the plugin (§4.2), asserted by an empty <see cref="FakePluginSession.InvokeCalls"/>;
///   * a <c>Refused</c> body becomes the port's <c>LastError</c> (§4.2);
///   * the <c>kind</c> string maps onto <see cref="ProfileKind"/> in the host (§3.4, §6);
///   * the device assembler fills only what the manifest declared (§1.1: an unlisted slot stays null).
/// </summary>
public class PluginAdapterTests
{
    // ---- fixtures ----------------------------------------------------------------------------------------

    /// <summary>A profile manifest with two profiles and a trait for each; no calls are needed to read it.</summary>
    private static PowerProfilesManifest PowerManifest() => new()
    {
        All =
        [
            new ProfileManifest { Id = "1", DisplayName = "profile.balanced" },
            new ProfileManifest { Id = "5", DisplayName = "profile.turbo" },
        ],
        Traits =
        [
            new ProfileTraitsManifest
            {
                Id = "1", Kind = "Balanced",
                Accent = [10, 20, 30], Flash = [40, 50, 60],
            },
            new ProfileTraitsManifest { Id = "5", Kind = "Turbo" },
        ],
    };

    /// <summary>A manifest with a single profile, used where the profile set is not the subject.</summary>
    private static PluginManifest ManifestWith(Func<CapabilitiesManifest> build, string? vendorName = null,
                                               string? status = null)
        => new()
        {
            PluginId = "acer-nitro", Abi = "1.0", VendorName = vendorName, StatusMessage = status,
            Capabilities = build(),
        };

    // ---- power profiles (§4.2) --------------------------------------------------------------------------

    /// <summary>§4.2: <c>All</c> and <c>Traits</c> come from the MANIFEST and never call the plugin. The session
    /// records zero invokes while both are read.</summary>
    [Fact]
    public void PowerAllAndTraitsComeFromTheManifestWithoutCallingTheSession()
    {
        var session = new FakePluginSession(ManifestWith(() => new CapabilitiesManifest
        {
            PowerProfiles = PowerManifest(),
        }));
        var port = new PluginPowerProfiles(session, session.Manifest.Capabilities!.PowerProfiles!);

        Assert.Equal(new[] { "1", "5" }, port.All.Select(p => p.Id));
        Assert.Equal("profile.turbo", port.All[1].DisplayName);

        var turbo = port.Traits(port.All[1]);
        Assert.Equal(ProfileKind.Turbo, turbo.Kind);
        Assert.Null(turbo.Accent);

        var balanced = port.Traits(port.All[0]);
        Assert.Equal(ProfileKind.Balanced, balanced.Kind);
        Assert.Equal(new AccentColor(10, 20, 30), balanced.Accent);
        Assert.Equal(new AccentColor(40, 50, 60), balanced.FlashColor);

        Assert.Empty(session.InvokeCalls);
    }

    /// <summary>A profile the manifest does not classify reads as <see cref="ProfileTraits.Unknown"/> — Other with
    /// no colours, the "nobody classifies it" answer, never a nearby mode.</summary>
    [Fact]
    public void PowerTraitsOfAnUnclassifiedProfileAreUnknown()
    {
        var session = new FakePluginSession(ManifestWith(() => new CapabilitiesManifest
        {
            PowerProfiles = PowerManifest(),
        }));
        var port = new PluginPowerProfiles(session, session.Manifest.Capabilities!.PowerProfiles!);

        var traits = port.Traits(new PerformanceProfile("99", "foreign"));

        Assert.Equal(ProfileKind.Other, traits.Kind);
        Assert.Null(traits.Accent);
        Assert.Null(traits.FlashColor);
    }

    /// <summary>§3.5: <c>Current</c> forwards <c>(Power, Op.Current)</c> and maps <c>{"id":"5"}</c> onto the
    /// manifest's profile. An id the manifest does not list is null, not invented.</summary>
    [Fact]
    public void PowerCurrentForwardsAndMapsTheId()
    {
        var session = new FakePluginSession(ManifestWith(() => new CapabilitiesManifest
        {
            PowerProfiles = PowerManifest(),
        }));
        session.SetResult(Capability.Power, Operation.Power.Current, AbiStatus.Ok, """{"id":"5"}""");
        var port = new PluginPowerProfiles(session, session.Manifest.Capabilities!.PowerProfiles!);

        var current = port.Current();

        Assert.Equal("5", current!.Id);
        Assert.Null(port.LastError);
        var call = Assert.Single(session.InvokeCalls);
        Assert.Equal((Capability.Power, Operation.Power.Current, "{}"), call);
    }

    /// <summary>§3.5: <c>Set</c> forwards <c>{"id":"1"}</c>; a refusal returns false and puts the plugin's own
    /// reason on <see cref="IPowerProfiles.LastError"/> — the reason belongs to THIS call (§4.2).</summary>
    [Fact]
    public void PowerSetForwardsAndRefusalSetsLastError()
    {
        var session = new FakePluginSession(ManifestWith(() => new CapabilitiesManifest
        {
            PowerProfiles = PowerManifest(),
        }));
        var port = new PluginPowerProfiles(session, session.Manifest.Capabilities!.PowerProfiles!);

        session.SetResult(Capability.Power, Operation.Power.Set, AbiStatus.Ok, "{}");
        Assert.True(port.Set(port.All[0]));
        Assert.Null(port.LastError);
        Assert.Equal((Capability.Power, Operation.Power.Set, """{"id":"1"}"""), session.InvokeCalls[^1]);

        session.SetResult(Capability.Power, Operation.Power.Set, AbiStatus.Refused, """{"error":"EC locked"}""");
        Assert.False(port.Set(port.All[0]));
        Assert.Equal("EC locked", port.LastError);
    }

    /// <summary>§3.5: <c>AvailableOn</c> forwards <c>{"onAc":true}</c> and filters the manifest's set by the
    /// returned ids, so an id the plugin names that it does not offer is dropped.</summary>
    [Fact]
    public void PowerAvailableOnForwardsAndFiltersByManifestSet()
    {
        var session = new FakePluginSession(ManifestWith(() => new CapabilitiesManifest
        {
            PowerProfiles = PowerManifest(),
        }));
        session.SetResult(Capability.Power, Operation.Power.AvailableOn, AbiStatus.Ok,
            """{"ids":["5","99"]}""");
        var port = new PluginPowerProfiles(session, session.Manifest.Capabilities!.PowerProfiles!);

        var onAc = port.AvailableOn(true);

        Assert.Equal(new[] { "5" }, onAc.Select(p => p.Id));
        Assert.Equal((Capability.Power, Operation.Power.AvailableOn, """{"onAc":true}"""), session.InvokeCalls[^1]);

        var onBattery = port.AvailableOn(false);
        Assert.Equal(new[] { "5" }, onBattery.Select(p => p.Id));
        Assert.Equal("""{"onAc":false}""", session.InvokeCalls[^1].RequestJson);
    }

    /// <summary>§3.4, §6: the manifest's <c>kind</c> string is parsed in the HOST. Every <see cref="ProfileKind"/>
    /// name maps, casing-insensitively; anything else is Other.</summary>
    [Theory]
    [InlineData("Quiet", ProfileKind.Quiet)]
    [InlineData("Eco", ProfileKind.Eco)]
    [InlineData("Balanced", ProfileKind.Balanced)]
    [InlineData("Performance", ProfileKind.Performance)]
    [InlineData("Turbo", ProfileKind.Turbo)]
    [InlineData("Other", ProfileKind.Other)]
    [InlineData("turbo", ProfileKind.Turbo)]
    [InlineData("  Eco ", ProfileKind.Eco)]
    [InlineData("Unknown", ProfileKind.Other)]
    [InlineData(null, ProfileKind.Other)]
    public void KindStringMapsToProfileKind(string? kind, ProfileKind expected)
        => Assert.Equal(expected, PluginPowerProfiles.ParseKind(kind));

    // ---- fan (§4.2) --------------------------------------------------------------------------------------

    /// <summary>§4.2: <see cref="IFanControl.Capability"/> comes from the manifest (no call); <c>SetMode</c> and
    /// <c>SetCustomSpeeds</c> forward their §3.5 bodies, and a refusal sets <c>LastError</c>.</summary>
    [Fact]
    public void FanCapabilityIsFromTheManifestAndWritesForward()
    {
        var session = new FakePluginSession(ManifestWith(() => new CapabilitiesManifest
        {
            Fan = new FanManifest
            {
                Capability = new FanCapabilityManifest { HasMax = true, HasCustom = true, HasGpuFan = false },
            },
        }));
        var port = new PluginFanControl(session, session.Manifest.Capabilities!.Fan!);

        Assert.Equal(new FanCapability(true, true, false), port.Capability);
        Assert.Empty(session.InvokeCalls); // Capability needed no call

        Assert.True(port.SetMode(FanMode.Custom));
        Assert.Equal((Capability.Fan, Operation.Fan.SetMode, """{"mode":"Custom"}"""), session.InvokeCalls[^1]);
        Assert.Null(port.LastError);

        Assert.True(port.SetCustomSpeeds(70, 55));
        Assert.Equal((Capability.Fan, Operation.Fan.SetCustomSpeeds, """{"cpu":70,"gpu":55}"""),
                     session.InvokeCalls[^1]);

        session.SetResult(Capability.Fan, Operation.Fan.SetMode, AbiStatus.Refused, """{"error":"fan busy"}""");
        Assert.False(port.SetMode(FanMode.Auto));
        Assert.Equal("fan busy", port.LastError);
    }

    // ---- sensors (§4.2) ----------------------------------------------------------------------------------

    /// <summary>§3.5: <c>Read</c> forwards <c>(Sensors, Op.Read)</c> and marshals the snapshot, fans included.
    /// A non-Ok result degrades to the default "-1 everywhere" snapshot rather than throwing.</summary>
    [Fact]
    public void SensorsReadMapsTheSnapshotAndDegradesOnRefusal()
    {
        var session = new FakePluginSession(ManifestWith(() => new CapabilitiesManifest
        {
            Sensors = new SensorsManifest(),
        }));
        session.SetResult(Capability.Sensors, Operation.Sensors.Read, AbiStatus.Ok,
            """{"cpuTempC":41,"gpuTempC":40,"fans":[{"label":"CPU","rpm":2100},{"label":"GPU","rpm":0}]}""");
        var port = new PluginSensors(session);

        var snapshot = port.Read();

        Assert.Equal(41, snapshot.CpuTempC);
        Assert.Equal(40, snapshot.GpuTempC);
        Assert.Equal(new FanReading[] { new("CPU", 2100), new("GPU", 0) }, snapshot.Fans);
        Assert.Equal((Capability.Sensors, Operation.Sensors.Read, "{}"), Assert.Single(session.InvokeCalls));

        session.SetResult(Capability.Sensors, Operation.Sensors.Read, AbiStatus.Refused, """{"error":"x"}""");
        var degraded = port.Read();
        Assert.Equal(-1, degraded.CpuTempC);
        Assert.Empty(degraded.Fans);
    }

    // ---- battery (§4.2) ----------------------------------------------------------------------------------

    /// <summary>§4.2: the host builds the <see cref="Battery"/> and assigns ONLY the properties the manifest
    /// listed. An unlisted property is null — "the manifest did not list it" is the removal semantics.</summary>
    [Fact]
    public void BatteryOnlyDeclaredPropertiesAreWired()
    {
        var session = new FakePluginSession(ManifestWith(() => new CapabilitiesManifest
        {
            Battery = new BatteryManifest { ChargeLimit = true, ChargeMode = true, PowerSource = true },
        }));
        var battery = new Battery();
        PluginBattery.Fill(battery, session, session.Manifest.Capabilities!.Battery);

        Assert.NotNull(battery.ChargeLimit);
        Assert.Null(battery.Calibration);          // not listed
        Assert.NotNull(battery.ChargeMode);
        Assert.NotNull(battery.PowerSource);
        Assert.Null(battery.Telemetry);            // telemetry is host-side, never wired by a plugin (§1.1)
    }

    /// <summary>§3.5: the charge-limit toggle forwards <c>ReadToggle</c>/<c>WriteToggle</c> by property name and
    /// reports the write's own <c>(ok, error)</c> half.</summary>
    [Fact]
    public void BatteryTogglesForwardByPropertyName()
    {
        var session = new FakePluginSession(ManifestWith(() => new CapabilitiesManifest
        {
            Battery = new BatteryManifest { ChargeLimit = true },
        }));
        var battery = new Battery();
        PluginBattery.Fill(battery, session, session.Manifest.Capabilities!.Battery);

        session.SetResult(Capability.Battery, Operation.Battery.ReadToggle, AbiStatus.Ok, """{"value":true}""");
        Assert.True(battery.ChargeLimit!.Read());
        Assert.Equal((Capability.Battery, Operation.Battery.ReadToggle, """{"property":"chargeLimit"}"""),
                     session.InvokeCalls[^1]);

        session.SetResult(Capability.Battery, Operation.Battery.WriteToggle, AbiStatus.Refused,
            """{"error":"limit locked"}""");
        var (ok, error) = battery.ChargeLimit.Write(true);
        Assert.False(ok);
        Assert.Equal("limit locked", error);
        Assert.Equal((Capability.Battery, Operation.Battery.WriteToggle,
                      """{"property":"chargeLimit","value":true}"""), session.InvokeCalls[^1]);
    }

    /// <summary>§3.5: <c>ReadPowerSource</c> maps the enum's own name onto <see cref="PowerSource"/>; an unknown
    /// string is <see cref="PowerSource.Unknown"/> (the UI hides the row rather than guessing).</summary>
    [Theory]
    [InlineData("Battery", PowerSource.Battery)]
    [InlineData("Barrel", PowerSource.Barrel)]
    [InlineData("UsbC", PowerSource.UsbC)]
    [InlineData("Nonsense", PowerSource.Unknown)]
    public void BatteryPowerSourceMaps(string wire, PowerSource expected)
    {
        var session = new FakePluginSession(ManifestWith(() => new CapabilitiesManifest
        {
            Battery = new BatteryManifest { PowerSource = true },
        }));
        session.SetResult(Capability.Battery, Operation.Battery.ReadPowerSource, AbiStatus.Ok,
            $$"""{"source":"{{wire}}"}""");
        var battery = new Battery();
        PluginBattery.Fill(battery, session, session.Manifest.Capabilities!.Battery);

        Assert.Equal(expected, battery.PowerSource!());
        Assert.Equal((Capability.Battery, Operation.Battery.ReadPowerSource, "{}"), session.InvokeCalls[^1]);
    }

    // ---- declared settings (§3.4, §4.2) ------------------------------------------------------------------

    /// <summary>§3.4, §4.2: a declared FLAG forwards <c>Settings.Read</c>/<c>Settings.Set</c> by key; the write's
    /// refusal becomes the port's <c>LastError</c>, which the host-side <c>FlagSetting.Write</c> reads only after a
    /// returned false.</summary>
    [Fact]
    public void DeclaredFlagForwardsReadAndSetByKey()
    {
        var session = new FakePluginSession(new PluginManifest());
        var setting = Assert.IsType<FlagSetting>(PluginDeclaredSetting.Build(session, new DeclaredSettingManifest
        {
            Key = "lcd_override", Shape = "flag", ReadbackVerifiesWrite = false,
        }));

        Assert.Equal("lcd_override", setting.Key);
        Assert.False(setting.ReadbackVerifiesWrite);

        session.SetResult(Capability.Settings, Operation.Settings.Read, AbiStatus.Ok, """{"value":"1"}""");
        Assert.True(setting.Read());
        Assert.Equal((Capability.Settings, Operation.Settings.Read, """{"key":"lcd_override"}"""),
                     session.InvokeCalls[^1]);

        session.SetResult(Capability.Settings, Operation.Settings.Set, AbiStatus.Refused,
            """{"error":"not in setup mode"}""");
        var refused = Assert.Throws<SettingNotAppliedException>(() => setting.Apply("0"));
        Assert.Equal("not in setup mode", refused.Reason);
        Assert.Equal("not in setup mode", setting.Port.LastError);
        Assert.Equal((Capability.Settings, Operation.Settings.Set, """{"key":"lcd_override","value":"0"}"""),
                     session.InvokeCalls[^1]);
    }

    /// <summary>§3.4, §3.5: a declared CHOICE carries its manifest option set, and reads/writes the current id by
    /// key. The option ids are the vendor's stable keys; this layer does not interpret them.</summary>
    [Fact]
    public void DeclaredChoiceForwardsOptionsAndReadWriteByKey()
    {
        var session = new FakePluginSession(new PluginManifest());
        var setting = Assert.IsType<ChoiceSetting>(PluginDeclaredSetting.Build(session, new DeclaredSettingManifest
        {
            Key = "usb_charging", Shape = "choice",
            Options =
            [
                new ChoiceOptionManifest { Id = "0", DisplayName = "level.off" },
                new ChoiceOptionManifest { Id = "20", DisplayName = "20%" },
            ],
        }));

        Assert.Equal(new[] { "0", "20" }, setting.Options.Select(o => o.Id));
        Assert.Equal(1, setting.IndexOf("20"));
        Assert.Equal(0, setting.IndexOf("99")); // an id the device does not offer reads as index 0

        session.SetResult(Capability.Settings, Operation.Settings.Read, AbiStatus.Ok, """{"value":"20"}""");
        Assert.Equal("20", setting.Read());
        Assert.Equal((Capability.Settings, Operation.Settings.Read, """{"key":"usb_charging"}"""),
                     session.InvokeCalls[^1]);

        Assert.True(setting.Port.Set("0"));
        Assert.Equal((Capability.Settings, Operation.Settings.Set, """{"key":"usb_charging","value":"0"}"""),
                     session.InvokeCalls[^1]);
    }

    /// <summary>A declaration with no key or an unknown shape builds nothing rather than inventing a setting.</summary>
    [Fact]
    public void MalformedDeclarationBuildsNothing()
    {
        var session = new FakePluginSession(new PluginManifest());

        Assert.Null(PluginDeclaredSetting.Build(session, new DeclaredSettingManifest { Key = "", Shape = "flag" }));
        Assert.Null(PluginDeclaredSetting.Build(session, new DeclaredSettingManifest { Key = "x", Shape = "mystery" }));
    }

    // ---- device assembly (§4.1, §4.2) --------------------------------------------------------------------

    /// <summary>§1.1, §4.2: a manifest that declares a subset leaves the other slots null, exactly like a backend
    /// that probed a feature absent. VendorName and StatusMessage (a Loc KEY) are set from the manifest.</summary>
    [Fact]
    public void DeviceAssemblyFillsOnlyDeclaredSlots()
    {
        var session = new FakePluginSession(ManifestWith(() => new CapabilitiesManifest
        {
            PowerProfiles = PowerManifest(),
            Fan = new FanManifest { Capability = new FanCapabilityManifest { HasMax = true } },
        }, vendorName: "Nitro AN18-61", status: "status.linux_mainline_full"));

        var device = new PluginVendorDevice(session);

        Assert.Equal("Nitro AN18-61", device.VendorName);
        Assert.Equal("status.linux_mainline_full", device.StatusMessage);
        Assert.NotNull(device.PowerProfiles);
        Assert.NotNull(device.FanControl);

        // Everything the manifest did not declare stays null / empty.
        Assert.Null(device.Sensors);
        Assert.Null(device.KeyboardBrightness);
        Assert.Null(device.Lighting);
        Assert.Null(device.Hotkeys);
        Assert.Null(device.GpuMux);
        Assert.Null(device.Battery.ChargeLimit);
        Assert.Empty(device.DeclaredSettings);
    }

    /// <summary>§4.2: a manifest declaring declared settings wires them onto the device through <c>Declare</c>,
    /// in order.</summary>
    [Fact]
    public void DeviceAssemblyDeclaresSettingsFromTheManifest()
    {
        var session = new FakePluginSession(ManifestWith(() => new CapabilitiesManifest
        {
            DeclaredSettings =
            [
                new DeclaredSettingManifest { Key = "lcd_override", Shape = "flag" },
                new DeclaredSettingManifest
                {
                    Key = "usb_charging", Shape = "choice",
                    Options = [new ChoiceOptionManifest { Id = "0", DisplayName = "off" }],
                },
            ],
        }));

        var device = new PluginVendorDevice(session);

        Assert.Equal(new[] { "lcd_override", "usb_charging" }, device.DeclaredSettings.Select(s => s.Key));
    }

    /// <summary>§4.3: <see cref="PluginVendorDevice.Dispose"/> disposes the SESSION (which runs <c>ah_dispose</c>)
    /// exactly once, even if called twice. <c>FinalizeComposition</c> is a documented no-op.</summary>
    [Fact]
    public void DeviceDisposeDisposesTheSessionExactlyOnce()
    {
        var session = new FakePluginSession(ManifestWith(() => new CapabilitiesManifest()));
        var device = new PluginVendorDevice(session);

        device.FinalizeComposition(); // no-op today, must not throw

        device.Dispose();
        device.Dispose();

        Assert.Equal(1, session.DisposeCount);
    }

    /// <summary>A hotkeys declaration attaches a non-null, disposable adapter (the native sink is not wired yet,
    /// so the source never fires — see <see cref="IPluginEventSource"/>). Disposing the device disposes it too.</summary>
    [Fact]
    public void HotkeysDeclarationAttachesADisposableAdapter()
    {
        var session = new FakePluginSession(ManifestWith(() => new CapabilitiesManifest
        {
            Hotkeys = new HotkeysManifest(),
        }));

        var device = new PluginVendorDevice(session);
        var hotkeys = Assert.IsAssignableFrom<IHotkeys>(device.Hotkeys);
        Assert.NotNull(hotkeys);

        device.Dispose();
        Assert.Equal(1, session.DisposeCount);
    }

    // ---- the "not wired" guard (§6) ----------------------------------------------------------------------

    /// <summary>The repository root, from the compiler's path (the test host runs in its output folder).</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>
    /// §6: Phase 0 builds the adapter path but does NOT compose with it. The invariant is enforced at the source
    /// level — the repo's idiom (ArchitectureMapTests, DomainNeutralityTests) — by asserting the composition root
    /// never names <see cref="PluginVendorDevice"/>. The mutation that reddens it is the one line this task must
    /// not write: a <c>PluginVendorDevice.Create(...)</c> branch in <c>DeviceFactory.Create</c>.
    /// </summary>
    [Fact]
    public void DeviceFactoryDoesNotReferencePluginVendorDevice()
    {
        var path = Path.Combine(Root(), "Infrastructure", "Composition", "DeviceFactory.cs");
        Assert.True(File.Exists(path), $"the tree no longer has '{path}' — this guard is looking at nothing");

        var source = File.ReadAllText(path);

        Assert.DoesNotContain("PluginVendorDevice", source);
        Assert.DoesNotContain("AcerHelper.Infrastructure.Plugins", source);
    }
}
