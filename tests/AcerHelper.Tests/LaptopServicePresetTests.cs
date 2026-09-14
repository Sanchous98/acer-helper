using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>The per-mode preset bags of <see cref="Settings"/>, counted as ONE value so a test can assert the
/// whole graph around a call in a single line, and so a bag added to the graph later cannot be silently
/// left out of the "a read must not mutate" assertions.</summary>
internal static class PresetGraph
{
    internal static (int Fan, int GpuOc, int Co, int CpuPower, int Light) Counts(Settings s) =>
        (s.FanPresets.Count, s.GpuOcPresets.Count, s.CoPresets.Count, s.CpuPowerModes.Count, s.LightPresets.Count);

    internal static readonly (int, int, int, int, int) Empty = (0, 0, 0, 0, 0);
}

/// <summary>
/// The read/write asymmetry the whole per-mode preset graph rests on: a <c>Current*</c> accessor is a READER
/// that hands back a default when the mode has no preset and must leave the graph exactly as it found it,
/// while its <c>Set*</c> twin is a WRITER that creates the entry — "created on first write, the user is
/// configuring it" (LaptopService.cs:391-393 for fans, repeated at 490 for GPU offsets and 566 for the
/// Curve Optimizer).
///
/// Every assertion below is on the settings dictionaries' Count (and their contents) around the call, plus
/// <see cref="FakeSettingsStore.SaveCount"/>. Those are the two cheapest ways to catch a refactor that
/// silently turns a read into a write — a UI poll that quietly creates a preset for every mode the user
/// visits — or a write into a read, a setting the user changed that never reaches disk.
/// </summary>
public class LaptopServicePresetReadTests
{
    /// <summary>One preset bucket per <c>which</c> string, for the table-driven tests below.</summary>
    private sealed record Arranged(LaptopServiceFixture F,
                                   FakeFanControl Fan,
                                   FakeGpuOverclock Gpu,
                                   FakeCurveOptimizer Co,
                                   FakeCpuPower Cpu);

    /// <summary>A balanced-profile service with every preset-bearing port attached, so an assertion that a
    /// port was NOT called is a claim about the service and not about a missing fake.</summary>
    private static Arranged Setup()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        var fan = new FakeFanControl();
        var gpu = new FakeGpuOverclock();
        var co = new FakeCurveOptimizer();
        var cpu = new FakeCpuPower { CurrentId = "best-efficiency" };
        f.Device.FanControl = fan;
        f.Device.GpuOverclock = gpu;
        f.Device.CurveOptimizer = co;
        f.Device.CpuPower = cpu;
        return new Arranged(f, fan, gpu, co, cpu);
    }

    private static int CountOf(Settings s, string which) => which switch
    {
        "fan"       => s.FanPresets.Count,
        "gpu-oc"    => s.GpuOcPresets.Count,
        "co"        => s.CoPresets.Count,
        "cpu-power" => s.CpuPowerModes.Count,
        _           => throw new ArgumentOutOfRangeException(nameof(which)),
    };

    private static IEnumerable<string> KeysOf(Settings s, string which) => which switch
    {
        "fan"       => s.FanPresets.Keys,
        "gpu-oc"    => s.GpuOcPresets.Keys,
        "co"        => s.CoPresets.Keys,
        "cpu-power" => s.CpuPowerModes.Keys,
        _           => throw new ArgumentOutOfRangeException(nameof(which)),
    };

    /// <summary>A write whose stored value is distinguishable from every default, so "the read fell back"
    /// and "the read found the other mode's preset" can never look the same.</summary>
    private static void Write(LaptopService svc, string which)
    {
        switch (which)
        {
            case "fan":       svc.SetFan(FanMode.Max, 42, 84); break;
            case "gpu-oc":    svc.SetGpuOc(150, 800); break;
            case "co":        svc.SetCo(-12); break;
            case "cpu-power": svc.SetCpuPower("best-performance"); break;
            default:          throw new ArgumentOutOfRangeException(nameof(which));
        }
    }

    // ---- the readers: a Current* accessor on a mode with no preset creates NOTHING ----

    /// <summary>The returned preset is the type's own default (Settings.cs:99-101: Auto, 70/70), and the
    /// graph is untouched — no entry, no Save.</summary>
    [Fact]
    public void CurrentFan_OnAnUnconfiguredMode_CreatesNoPreset()
    {
        var a = Setup();

        var fan = a.F.Service.CurrentFan();

        Assert.Equal((int)FanMode.Auto, fan.Mode);
        Assert.Equal(70, fan.Cpu);
        Assert.Equal(70, fan.Gpu);
        Assert.Empty(fan.CpuCurve);
        Assert.Empty(fan.GpuCurve);
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Equal(0, a.F.Store.SaveCount);
    }

    [Fact]
    public void CurrentGpuOc_OnAnUnconfiguredMode_CreatesNoPreset()
    {
        var a = Setup();

        var g = a.F.Service.CurrentGpuOc();

        Assert.Equal(0, g.Core);
        Assert.Equal(0, g.Mem);
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Equal(0, a.F.Store.SaveCount);
    }

    [Fact]
    public void CurrentCo_OnAnUnconfiguredMode_CreatesNoPreset()
    {
        var a = Setup();

        var c = a.F.Service.CurrentCo();

        Assert.Equal(0, c.AllCore);
        Assert.Empty(c.Domains);
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Equal(0, a.F.Store.SaveCount);
    }

    /// <summary>The CPU-power axis has no default of its own to hand back on an unconfigured mode, so it
    /// reflects the LIVE overlay instead (LaptopService.cs:531-536) — reality, not a forced value. It still
    /// must not record a choice the user never made.</summary>
    [Fact]
    public void CurrentCpuPower_OnAnUnconfiguredMode_ReportsTheLiveOverlay_AndCreatesNoEntry()
    {
        var a = Setup();

        Assert.Equal("best-efficiency", a.F.Service.CurrentCpuPower());
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Equal(0, a.F.Store.SaveCount);
        Assert.Empty(a.Cpu.SetCalls);                 // reporting it must not re-drive it either
    }

    [Fact]
    public void CurrentCpuPower_WithNoPort_IsNull_AndCreatesNoEntry()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        Assert.Null(f.Service.CurrentCpuPower());
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(f.Store.Settings));
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>A reader hands back a FRESH default every time (the <c>: new FanPreset()</c> arm of the
    /// ternary), never a shared scratch instance and never the stored one. So a view-model that mutates what
    /// it was handed cannot smuggle that change into the graph — only a <c>Set*</c> call can.</summary>
    [Theory]
    [InlineData("fan")]
    [InlineData("gpu-oc")]
    [InlineData("co")]
    public void ADefaultFromARead_IsAFreshInstance_SoMutatingItIsNeverRemembered(string which)
    {
        var a = Setup();

        switch (which)
        {
            case "fan":    { var v = a.F.Service.CurrentFan();    v.Mode = (int)FanMode.Custom; v.Cpu = 99; break; }
            case "gpu-oc": { var v = a.F.Service.CurrentGpuOc(); v.Core = 999;     v.Mem = 999;  break; }
            case "co":     { var v = a.F.Service.CurrentCo();    v.AllCore = -30; v.Domains["big"] = -30; break; }
        }

        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Equal(0, a.F.Store.SaveCount);
        Assert.Equal(70, a.F.Service.CurrentFan().Cpu);          // ...the next read is the default again
        Assert.Equal(0, a.F.Service.CurrentGpuOc().Core);
        Assert.Equal(0, a.F.Service.CurrentCo().AllCore);
    }

    // ---- the writers: a Set* creates exactly the one entry for the CURRENT mode ----

    [Fact]
    public void SetFan_InsertsThePassedValues_UnderTheCurrentModeKey()
    {
        var a = Setup();

        a.F.Service.SetFan(FanMode.Max, 42, 84);

        var stored = Assert.Single(a.F.Store.Settings.FanPresets);
        Assert.Equal("balanced", stored.Key);
        Assert.Equal((int)FanMode.Max, stored.Value.Mode);
        Assert.Equal(42, stored.Value.Cpu);
        Assert.Equal(84, stored.Value.Gpu);
        Assert.Equal(1, a.F.Store.SaveCount);
        Assert.Equal([FanMode.Max], a.Fan.ModeCalls);
        Assert.Empty(a.Fan.SpeedCalls);                          // Max is a mode switch, not a manual speed
    }

    /// <summary>SetFanCurve goes through the same <c>StoredFan()</c>, and must not disturb the fixed speeds
    /// the mode already carries (they are the fallback for a fan whose curve is off).</summary>
    [Fact]
    public void SetFanCurve_InsertsAPreset_KeepingTheFixedSpeeds()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);   // no fan port needed

        f.Service.SetFanCurve(gpu: false, use: true, points: [10, 20, 30]);

        var stored = Assert.Single(f.Store.Settings.FanPresets);
        Assert.Equal("balanced", stored.Key);
        Assert.True(stored.Value.CpuUseCurve);
        Assert.False(stored.Value.GpuUseCurve);
        Assert.Equal([10, 20, 30], stored.Value.CpuCurve);
        Assert.Empty(stored.Value.GpuCurve);
        Assert.Equal(70, stored.Value.Cpu);                      // the fixed speeds survive untouched
        Assert.Equal(70, stored.Value.Gpu);
        Assert.Equal(1, f.Store.SaveCount);
    }

    [Fact]
    public void SetGpuOc_InsertsAndAppliesTheOffsets()
    {
        var a = Setup();

        Assert.True(a.F.Service.SetGpuOc(150, 800));

        var stored = Assert.Single(a.F.Store.Settings.GpuOcPresets);
        Assert.Equal("balanced", stored.Key);
        Assert.Equal(150, stored.Value.Core);
        Assert.Equal(800, stored.Value.Mem);
        Assert.Equal(1, a.F.Store.SaveCount);
        Assert.Equal(new[] { (150, 800) }, a.Gpu.SetCalls);
    }

    [Fact]
    public void SetCpuPower_InsertsAndAppliesTheOverlayId()
    {
        var a = Setup();

        Assert.True(a.F.Service.SetCpuPower("best-performance"));

        var stored = Assert.Single(a.F.Store.Settings.CpuPowerModes);
        Assert.Equal("balanced", stored.Key);
        Assert.Equal("best-performance", stored.Value);
        Assert.Equal(1, a.F.Store.SaveCount);
        Assert.Equal(["best-performance"], a.Cpu.SetCalls);
    }

    /// <summary>NOTE the ordering, derived from the code and not from the doc prose: every <c>Set*</c>
    /// persists BEFORE it consults the port (LaptopService.cs:502-513, 539-550, 580-595), so a machine whose
    /// port is missing — the feature was probed away — still records the user's choice and reports the write
    /// as failed. Losing the setting as well would be the surprising outcome; this is the behaviour as
    /// shipped, and it is the exact opposite of <c>SetCoDomains</c>, which returns before persisting when the
    /// port is null (624-625). See LaptopServiceCoTests for that half of the asymmetry.</summary>
    [Theory]
    [InlineData("gpu-oc")]
    [InlineData("co")]
    [InlineData("cpu-power")]
    public void AWritWithNoPort_ReturnsFalse_ButStillPersistsThePreset(string which)
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);   // no ports at all

        var ok = which switch
        {
            "gpu-oc"    => f.Service.SetGpuOc(150, 800),
            "co"        => f.Service.SetCo(-12),
            "cpu-power" => f.Service.SetCpuPower("best-performance"),
            _           => throw new ArgumentOutOfRangeException(nameof(which)),
        };

        Assert.False(ok);
        Assert.Equal(1, CountOf(f.Store.Settings, which));
        Assert.Equal(["balanced"], KeysOf(f.Store.Settings, which));
        Assert.Equal(1, f.Store.SaveCount);
    }

    // ---- bucketing: the preset follows CurrentModeKey(), so modes cannot see each other's ----

    [Theory]
    [InlineData("fan")]
    [InlineData("gpu-oc")]
    [InlineData("co")]
    public void AWritInOneMode_IsNotVisibleInAnother(string which)
    {
        var a = Setup();

        Write(a.F.Service, which);
        Assert.Equal(1, CountOf(a.F.Store.Settings, which));
        Assert.Equal(["balanced"], KeysOf(a.F.Store.Settings, which).Order());

        a.F.Pp!.CurrentProfile = TestProfiles.Performance;       // the user switches profile

        switch (which)
        {
            case "fan":    Assert.Equal(70, a.F.Service.CurrentFan().Cpu); break;
            case "gpu-oc": Assert.Equal(0, a.F.Service.CurrentGpuOc().Core); break;
            case "co":     Assert.Equal(0, a.F.Service.CurrentCo().AllCore); break;
        }
        Assert.Equal(1, CountOf(a.F.Store.Settings, which));     // ...and the read created nothing

        Write(a.F.Service, which);
        Assert.Equal(2, CountOf(a.F.Store.Settings, which));
        Assert.Equal(["balanced", "performance"], KeysOf(a.F.Store.Settings, which).Order());
    }

    /// <summary>The CPU-power axis buckets the same way, but its fallback is the port's live reading — so on
    /// the unconfigured mode it must report what the OS is actually doing, not the other mode's stored id.
    /// (The port's current overlay is re-arranged after the write because the fake's Set moves it, exactly as
    /// the real overlay API would when the firmware switches profile.)</summary>
    [Fact]
    public void TheCpuPowerOverlay_IsPerMode_AndFallsBackToTheLiveOverlay()
    {
        var a = Setup();

        a.F.Service.SetCpuPower("best-performance");
        a.Cpu.CurrentId = "best-efficiency";

        a.F.Pp!.CurrentProfile = TestProfiles.Performance;

        Assert.Equal("best-efficiency", a.F.Service.CurrentCpuPower());
        Assert.Single(a.F.Store.Settings.CpuPowerModes);
        Assert.Equal(["balanced"], a.F.Store.Settings.CpuPowerModes.Keys.Order());
    }
}

/// <summary>
/// The four <c>ApplyMode*</c> methods on a mode change — and the one thing they all have in common, which
/// is easy to get wrong from their names: NONE of them is a writer. They read the current mode's preset
/// (or fall back) and push it to the hardware; the graph is left exactly as it was, and nothing is saved.
///
/// That is deliberate rather than incidental: these run on every profile switch AND on the startup/resume
/// re-apply paths (LaptopService.cs:51-52, 60), so an inserting implementation would mint a preset for every
/// mode the machine ever passed through, and save on every boot. The asymmetry with the <c>Set*</c> methods
/// is the whole point of this file.
/// </summary>
public class LaptopServiceApplyModeGraphTests
{
    private sealed record Arranged(LaptopServiceFixture F,
                                   FakeFanControl Fan,
                                   FakeGpuOverclock Gpu,
                                   FakeCurveOptimizer Co,
                                   FakeCpuPower Cpu);

    private static Arranged Setup(PerformanceProfile? current = null)
    {
        var f = LaptopServiceFixture.WithProfiles(current: current ?? TestProfiles.Balanced);
        var fan = new FakeFanControl();
        var gpu = new FakeGpuOverclock();
        var co = new FakeCurveOptimizer();
        var cpu = new FakeCpuPower { CurrentId = "best-efficiency" };
        f.Device.FanControl = fan;
        f.Device.GpuOverclock = gpu;
        f.Device.CurveOptimizer = co;
        f.Device.CpuPower = cpu;
        return new Arranged(f, fan, gpu, co, cpu);
    }

    /// <summary>A store with one preset for ANOTHER mode ("quiet"), so every bag is non-empty and the
    /// Curve Optimizer takes its "the install is configured, this mode just isn't" branch instead of the
    /// empty-store early return. Any insertion into the graph then shows up as a count change.</summary>
    private static Settings StoreWithAnotherModeConfigured()
    {
        var s = new Settings();
        s.FanPresets["quiet"] = new FanPreset { Mode = (int)FanMode.Max, Cpu = 10, Gpu = 20 };
        s.GpuOcPresets["quiet"] = new GpuOcPreset { Core = 100, Mem = 200 };
        s.CpuPowerModes["quiet"] = "best-efficiency";
        s.CoPresets["quiet"] = new CoPreset { AllCore = -20 };
        return s;
    }

    /// <summary>The headline verdict, one row per Apply*: on a mode with no preset of its own, none of them
    /// inserts one and none of them saves. The port calls they DO make are pinned individually below.</summary>
    [Theory]
    [InlineData("fan")]
    [InlineData("gpu-oc")]
    [InlineData("cpu-power")]
    [InlineData("co")]
    public void NoApplyModeMethodEverInsertsAPreset_OrSaves(string which)
    {
        var settings = StoreWithAnotherModeConfigured();
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);
        f.Device.FanControl = new FakeFanControl();
        f.Device.GpuOverclock = new FakeGpuOverclock();
        f.Device.CurveOptimizer = new FakeCurveOptimizer();
        f.Device.CpuPower = new FakeCpuPower();

        var before = PresetGraph.Counts(settings);

        switch (which)
        {
            case "fan":       f.Service.ApplyModeFan(); break;
            case "gpu-oc":    f.Service.ApplyModeGpuOc(); break;
            case "cpu-power": f.Service.ApplyModeCpuPower(); break;
            case "co":        f.Service.ApplyModeCo(); break;
        }

        Assert.Equal(before, PresetGraph.Counts(settings));
        Assert.Equal(["quiet"], settings.FanPresets.Keys);
        Assert.Equal(["quiet"], settings.GpuOcPresets.Keys);
        Assert.Equal(["quiet"], settings.CpuPowerModes.Keys);
        Assert.Equal(["quiet"], settings.CoPresets.Keys);
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>A mode the user never configured has no fan preset at all, and the fans are deliberately
    /// left alone rather than forced to a default — the hardware mode cannot be read back to seed one
    /// (Settings.cs:23-26). So the port must not be touched either.</summary>
    [Fact]
    public void ApplyModeFan_OnAnUnconfiguredMode_ReturnsNull_AndLeavesTheFansAlone()
    {
        var a = Setup();

        Assert.Null(a.F.Service.ApplyModeFan());

        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Empty(a.Fan.ModeCalls);
        Assert.Empty(a.Fan.SpeedCalls);
        Assert.Equal(0, a.F.Store.SaveCount);
    }

    /// <summary>It hands back the STORED instance (not a copy), which is what the UI reflects — so the
    /// caller sees the user's setting, not a default.</summary>
    [Fact]
    public void ApplyModeFan_WithAPreset_ReappliesIt_AndReturnsTheStoredInstance()
    {
        var a = Setup();
        a.F.Service.SetFan(FanMode.Max, 42, 84);
        a.Fan.ModeCalls.Clear();

        var applied = a.F.Service.ApplyModeFan();

        Assert.Same(a.F.Store.Settings.FanPresets["balanced"], applied);
        Assert.Equal([FanMode.Max], a.Fan.ModeCalls);
        Assert.Single(a.F.Store.Settings.FanPresets);            // still the one entry the writer made
        Assert.Equal(1, a.F.Store.SaveCount);                    // ...and still the one save
    }

    /// <summary>Custom is the exception to the "push it now" rule: the manual speeds are driven from the
    /// temperature curve on every refresh, so the mode CHANGE only switches behaviour — driving a fixed
    /// speed here would fight the curve (LaptopService.cs:432-434, 448-461).</summary>
    [Fact]
    public void ApplyModeFan_WithACustomPreset_DefersToTheSensorLoop()
    {
        var settings = new Settings();
        settings.FanPresets["balanced"] = new FanPreset { Mode = (int)FanMode.Custom, Cpu = 30, Gpu = 40 };
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);
        var fan = new FakeFanControl();
        f.Device.FanControl = fan;

        var applied = f.Service.ApplyModeFan();

        Assert.Same(settings.FanPresets["balanced"], applied);
        Assert.Empty(fan.ModeCalls);
        Assert.Empty(fan.SpeedCalls);
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>GPU offsets follow the OPPOSITE contract to fans (Settings.cs:42-47): an unconfigured mode
    /// is definitely stock, because the driver zeroes the offsets on every boot — so switching to it MUST
    /// clear whatever the previous mode applied. Hence the port still gets a (0, 0) write, while the graph
    /// still gains nothing.</summary>
    [Fact]
    public void ApplyModeGpuOc_OnAnUnconfiguredMode_WritesStock_ButInsertsNothing()
    {
        var a = Setup();

        var g = a.F.Service.ApplyModeGpuOc();

        Assert.Equal(0, g.Core);
        Assert.Equal(0, g.Mem);
        Assert.Equal(new[] { (0, 0) }, a.Gpu.SetCalls);
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Equal(0, a.F.Store.SaveCount);
    }

    [Fact]
    public void ApplyModeGpuOc_WithAPreset_WritesTheStoredOffsets_AndReturnsTheStoredInstance()
    {
        var a = Setup();
        a.F.Service.SetGpuOc(150, 800);
        a.Gpu.SetCalls.Clear();

        var applied = a.F.Service.ApplyModeGpuOc();

        Assert.Same(a.F.Store.Settings.GpuOcPresets["balanced"], applied);
        Assert.Equal(new[] { (150, 800) }, a.Gpu.SetCalls);
        Assert.Single(a.F.Store.Settings.GpuOcPresets);
        Assert.Equal(1, a.F.Store.SaveCount);
    }

    [Fact]
    public void ApplyModeGpuOc_WithNoPort_StillReturnsThePreset_AndInsertsNothing()
    {
        var settings = new Settings();
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);

        var g = f.Service.ApplyModeGpuOc();

        Assert.Equal(0, g.Core);
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(settings));
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>CPU power follows the FAN contract, not the GPU one (Settings.cs:57-61): an unconfigured
    /// mode is left untouched, because forcing an OS power mode on a profile the user never configured would
    /// be a change nobody asked for. So the port is not written — only read, to report reality.</summary>
    [Fact]
    public void ApplyModeCpuPower_OnAnUnconfiguredMode_WritesNothing_AndInsertsNothing()
    {
        var a = Setup();

        Assert.Equal("best-efficiency", a.F.Service.ApplyModeCpuPower());

        Assert.Empty(a.Cpu.SetCalls);
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Equal(0, a.F.Store.SaveCount);
    }

    [Fact]
    public void ApplyModeCpuPower_WithAnEntry_AppliesTheStoredOverlay_AndInsertsNothing()
    {
        var a = Setup();
        a.F.Service.SetCpuPower("best-performance");
        a.Cpu.SetCalls.Clear();

        Assert.Equal("best-performance", a.F.Service.ApplyModeCpuPower());

        Assert.Equal(["best-performance"], a.Cpu.SetCalls);
        Assert.Single(a.F.Store.Settings.CpuPowerModes);
        Assert.Equal(1, a.F.Store.SaveCount);
    }

    /// <summary>NOTE: the port is checked BEFORE the store (LaptopService.cs:557-558), so on a machine whose
    /// overlay API does not answer, a mode the user DID configure reports as nothing at all rather than as
    /// its stored id. Pinned as shipped; the stored choice is still on disk and still reappears if the port
    /// comes back.</summary>
    [Fact]
    public void ApplyModeCpuPower_WithNoPort_IsNull_EvenWhenAModeIsConfigured()
    {
        var settings = new Settings();
        settings.CpuPowerModes["balanced"] = "best-performance";
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);

        Assert.Null(f.Service.ApplyModeCpuPower());
        Assert.Equal(0, f.Store.SaveCount);
        Assert.Equal(["balanced"], settings.CpuPowerModes.Keys);
    }
}

/// <summary>
/// <c>GetOrAdd</c> (LaptopService.cs:736-743) — the "look up, create and INSERT when absent, otherwise hand
/// back the instance already there" idiom every per-mode preset shares — plus its one reader that DOES
/// insert: <see cref="LaptopService.LightsForCurrentMode"/>. That one is the documented exception to the
/// rule the previous classes pin, and it is worth its own assertions precisely because it looks like the
/// readers around it.
/// </summary>
public class LaptopServicePresetGetOrAddTests
{
    [Fact]
    public void LightsForCurrentMode_InsertsOneEmptyPreset_ButSavesNothing()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        var zones = f.Service.LightsForCurrentMode();

        Assert.Empty(zones);
        Assert.Equal(["balanced"], f.Store.Settings.LightPresets.Keys);
        // NOTE: this read DOES mutate the graph (it is the one exception) but it does NOT persist — the
        // lighting view-models mutate the live Zones dict and call PersistLighting() once they are done
        // (LaptopService.cs:465-473, 718).
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>Present key -> the SAME instance, not a fresh one. Otherwise a view-model's edits would
    /// land on an object nobody ever saves.</summary>
    [Fact]
    public void LightsForCurrentMode_ReturnsTheSameDictionaryInstance_OnEveryCall()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        var first = f.Service.LightsForCurrentMode();
        var second = f.Service.LightsForCurrentMode();

        Assert.Same(first, second);
        Assert.Same(f.Store.Settings.LightPresets["balanced"].Zones, first);
        Assert.Single(f.Store.Settings.LightPresets);             // absent inserted exactly one, present none
    }

    [Fact]
    public void LightsForCurrentMode_IsPerMode()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        var balanced = f.Service.LightsForCurrentMode();
        f.Pp!.CurrentProfile = TestProfiles.Performance;
        var performance = f.Service.LightsForCurrentMode();

        Assert.NotSame(balanced, performance);
        Assert.Equal(["balanced", "performance"], f.Store.Settings.LightPresets.Keys.Order());

        balanced["keyboard"] = new LightSettings();               // the zones do not bleed across modes
        Assert.Empty(performance);
    }

    [Fact]
    public void EnsureLightZone_InsertsOnce_AndReturnsTheStoredInstance()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        var zones = f.Service.LightsForCurrentMode();

        var first = f.Service.EnsureLightZone(zones, "keyboard");
        var second = f.Service.EnsureLightZone(zones, "keyboard");

        Assert.Same(first, second);
        Assert.Same(first, zones["keyboard"]);
        Assert.Single(zones);
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>GetOrAdd must return what is THERE, not overwrite it with a default — an entry the user has
    /// already configured surviving a second EnsureLightZone is the whole reason the method exists.</summary>
    [Fact]
    public void EnsureLightZone_KeepsAnExistingEntrysState()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        var zones = f.Service.LightsForCurrentMode();
        zones["keyboard"] = new LightSettings { Configured = true, Brightness = 37 };

        var zone = f.Service.EnsureLightZone(zones, "keyboard");

        Assert.True(zone.Configured);
        Assert.Equal(37, zone.Brightness);
        Assert.Single(zones);
    }

    [Fact]
    public void EnsureLightZone_WithDifferentNames_AddsOneEntryEach()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        var zones = f.Service.LightsForCurrentMode();

        var a = f.Service.EnsureLightZone(zones, "keyboard");
        var b = f.Service.EnsureLightZone(zones, "lightbar");

        Assert.NotSame(a, b);
        Assert.Equal(["keyboard", "lightbar"], zones.Keys.Order());
    }

    /// <summary>The writers' half of GetOrAdd: a SECOND write to the same mode must reuse the instance the
    /// first one stored (a replaced instance would lose the fields this call does not set — the per-fan
    /// curves, for a fan preset).</summary>
    [Theory]
    [InlineData("fan")]
    [InlineData("gpu-oc")]
    [InlineData("co")]
    public void ASecondWrite_ReusesTheStoredPreset_InsteadOfReplacingIt(string which)
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        switch (which)
        {
            case "fan":    f.Service.SetFan(FanMode.Auto, 10, 20); break;
            case "gpu-oc": f.Service.SetGpuOc(10, 20); break;
            case "co":     f.Service.SetCo(-10); break;
        }

        var first = which switch
        {
            "fan"    => (object)f.Store.Settings.FanPresets["balanced"],
            "gpu-oc" => f.Store.Settings.GpuOcPresets["balanced"],
            "co"     => f.Store.Settings.CoPresets["balanced"],
            _        => throw new ArgumentOutOfRangeException(nameof(which)),
        };

        switch (which)
        {
            case "fan":    f.Service.SetFan(FanMode.Max, 30, 40); break;
            case "gpu-oc": f.Service.SetGpuOc(30, 40); break;
            case "co":     f.Service.SetCo(-20); break;
        }

        var second = which switch
        {
            "fan"    => (object)f.Store.Settings.FanPresets["balanced"],
            "gpu-oc" => f.Store.Settings.GpuOcPresets["balanced"],
            "co"     => f.Store.Settings.CoPresets["balanced"],
            _        => throw new ArgumentOutOfRangeException(nameof(which)),
        };

        Assert.Same(first, second);
        Assert.Equal(1, which switch
        {
            "fan"    => f.Store.Settings.FanPresets.Count,
            "gpu-oc" => f.Store.Settings.GpuOcPresets.Count,
            _        => f.Store.Settings.CoPresets.Count,
        });
        Assert.Equal(2, f.Store.SaveCount);                       // both writes persisted, both to one entry
    }

    /// <summary>The key handed to GetOrAdd is <c>CurrentModeKey()</c>, read at call time — so with Turbo
    /// used as a switch the preset lands in the BASE profile's bucket, which is what makes the base's fans
    /// and lighting apply while Turbo sits over it (LaptopService.cs:130-144).</summary>
    [Theory]
    [InlineData("fan")]
    [InlineData("gpu-oc")]
    [InlineData("co")]
    [InlineData("cpu-power")]
    public void WithTurboAsASwitch_PresetsLandInTheBaseModesBucket(string which)
    {
        var settings = new Settings
        {
            TurboToggles = true,
            OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
        };
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Turbo);

        switch (which)
        {
            case "fan":       f.Service.SetFan(FanMode.Max, 42, 84); break;
            case "gpu-oc":    f.Service.SetGpuOc(150, 800); break;
            case "co":        f.Service.SetCo(-12); break;
            case "cpu-power": f.Service.SetCpuPower("best-performance"); break;
        }

        IEnumerable<string> keys = which switch
        {
            "fan"       => settings.FanPresets.Keys,
            "gpu-oc"    => settings.GpuOcPresets.Keys,
            "co"        => settings.CoPresets.Keys,
            "cpu-power" => settings.CpuPowerModes.Keys,
            _           => throw new ArgumentOutOfRangeException(nameof(which)),
        };

        Assert.Equal(["balanced"], keys);
        Assert.Equal("balanced", f.Service.CurrentModeKey());     // the key that did it
    }
}
