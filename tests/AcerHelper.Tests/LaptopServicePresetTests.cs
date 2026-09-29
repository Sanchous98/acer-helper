using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>The per-mode preset bags of <see cref="Settings"/>, counted as ONE value so a test can assert the
/// whole graph around a call in a single line, and so a bag added to the graph later cannot be silently
/// left out of the "a read must not mutate" assertions.
///
/// That promise used to be prose: <see cref="Counts"/> listed the five bags by hand, so a sixth would have
/// been omitted in silence. It is now built from <see cref="ModeAxisTable.All"/> and the switch below is
/// exhaustive over <see cref="ModeAxis"/>, with a test that iterates every enum value — so a new axis throws
/// here instead of quietly dropping out of the assertions.</summary>
internal static class PresetGraph
{
    /// <summary>The bag one axis is stored in.</summary>
    internal static int CountFor(Settings s, ModeAxis axis) => axis switch
    {
        ModeAxis.Fans => s.FanPresets.Count,
        ModeAxis.GpuOc => s.GpuOcPresets.Count,
        ModeAxis.CpuPower => s.CpuPowerModes.Count,
        ModeAxis.Co => s.CoPresets.Count,
        ModeAxis.Lights => s.LightPresets.Count,
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "a new ModeAxis needs a bag here"),
    };

    internal static (int Fan, int GpuOc, int Co, int CpuPower, int Light) Counts(Settings s) =>
        (CountFor(s, ModeAxis.Fans), CountFor(s, ModeAxis.GpuOc), CountFor(s, ModeAxis.Co),
         CountFor(s, ModeAxis.CpuPower), CountFor(s, ModeAxis.Lights));

    internal static readonly (int, int, int, int, int) Empty = (0, 0, 0, 0, 0);
}

/// <summary>
/// The read/write asymmetry the whole per-mode preset graph rests on: a <c>Current*</c> accessor is a READER
/// that hands back a default when the mode has no preset and must leave the graph exactly as it found it,
/// while its <c>Set*</c> twin is a WRITER that creates the entry — "created on first write, the user is
/// configuring it" (`LaptopService.Fans.cs` `StoredFan` for fans, repeated in `LaptopService.Tuning.cs` `StoredGpuOc` for GPU offsets and `LaptopService.Tuning.cs` `StoredCo` for the
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
                                   FakeGpuPowerEnvelope Env,
                                   FakeCurveOptimizer Co,
                                   FakeCpuPower Cpu);

    /// <summary>A balanced-profile service with every preset-bearing port attached, so an assertion that a
    /// port was NOT called is a claim about the service and not about a missing fake.</summary>
    private static Arranged Setup()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        var fan = new FakeFanControl();
        var gpu = new FakeGpuOverclock();
        var env = new FakeGpuPowerEnvelope();
        var co = new FakeCurveOptimizer();
        var cpu = new FakeCpuPower { CurrentId = "best-efficiency" };
        f.Device.FanControl = fan;
        f.Device.GpuOverclock = gpu;
        f.Device.GpuPowerEnvelope = env;
        f.Device.CurveOptimizer = co;
        f.Device.CpuPower = cpu;
        return new Arranged(f, fan, gpu, env, co, cpu);
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
    private static void Write(LaptopServiceFixture f, string which)
    {
        switch (which)
        {
            case "fan":       f.ApplyFanSelection.Run(FanMode.Max, 42, 84); break;
            case "gpu-oc":    f.ApplyGpuOffsets.Run(new GpuAxisState(150, 800)); break;
            case "co":        f.Service.SetCo(-12); break;
            case "cpu-power": f.ApplyCpuPower.Run("best-performance"); break;
            default:          throw new ArgumentOutOfRangeException(nameof(which));
        }
    }

    // ---- the readers: a Current* accessor on a mode with no preset creates NOTHING ----

    /// <summary>The returned state is the type's own default (<c>FanAxisState</c>: Auto, 70/70, the built-in
    /// ramp in each half), and the graph is untouched — no entry, no Save. Reached through the
    /// <see cref="ReadFanState"/> use case, which is what the UI reads.
    ///
    /// THE CURVES ARE THE RAMP AND NOT EMPTY, which is the value this asserts that changed: an empty curve was
    /// the stored spelling of "use the built-in ramp", and it is a shape the model cannot hold
    /// (<see cref="FanSettings"/>), so a default preset carries the ramp itself. The user-visible behaviour is
    /// identical — the fans follow the same five duties either way — and the assertion is tightened rather than
    /// loosened.</summary>
    [Fact]
    public void CurrentFan_OnAnUnconfiguredMode_CreatesNoPreset()
    {
        var a = Setup();

        var fan = a.F.FanState.Run();

        Assert.Equal(FanMode.Auto, fan.Mode);
        Assert.Equal(70, fan.Cpu.FixedDuty);
        Assert.Equal(70, fan.Gpu.FixedDuty);
        Assert.Equal(Fan.DefaultDuties(), fan.Cpu.Curve);
        Assert.Equal(Fan.DefaultDuties(), fan.Gpu.Curve);
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Equal(0, a.F.Store.SaveCount);
    }

    [Fact]
    public void CurrentGpuOc_OnAnUnconfiguredMode_CreatesNoPreset()
    {
        var a = Setup();

        var g = a.F.GpuOcState.Run();

        Assert.Equal(0, g.Core);
        Assert.Equal(0, g.Mem);
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Equal(0, a.F.Store.SaveCount);
    }

    [Fact]
    public void CurrentCo_OnAnUnconfiguredMode_CreatesNoPreset()
    {
        var a = Setup();

        var c = a.F.CoDomains.Run();

        Assert.Equal(new[] { 0 }, c);                          // one all-core row on a domainless CPU
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Equal(0, a.F.Store.SaveCount);
    }

    /// <summary>The CPU-power axis has no default of its own to hand back on an unconfigured mode, so it
    /// reflects the LIVE overlay instead (<see cref="ReadCpuPower"/>) — reality, not a forced value. It still
    /// must not record a choice the user never made.</summary>
    [Fact]
    public void CurrentCpuPower_OnAnUnconfiguredMode_ReportsTheLiveOverlay_AndCreatesNoEntry()
    {
        var a = Setup();

        Assert.Equal("best-efficiency", a.F.CpuPower.Run());
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Equal(0, a.F.Store.SaveCount);
        Assert.Empty(a.Cpu.SetCalls);                 // reporting it must not re-drive it either
    }

    [Fact]
    public void CurrentCpuPower_WithNoPort_IsNull_AndCreatesNoEntry()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        Assert.Null(f.CpuPower.Run());
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(f.Store.Settings));
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>A reader hands back a FRESH value every time (the default arm of the read), never a shared
    /// scratch instance and never the stored one. The domain value is IMMUTABLE — a <see cref="FanAxisState"/>
    /// or <see cref="GpuAxisState"/> cannot be edited in place — so the only mutable thing a read hands over is
    /// an ARRAY, and this asserts that writing through it cannot reach the graph. The GPU axis has no array, so
    /// its row is the plain "the next read is the default again" half.</summary>
    [Theory]
    [InlineData("fan")]
    [InlineData("gpu-oc")]
    [InlineData("co")]
    public void ADefaultFromARead_SharesNoArrayWithTheGraph_SoWritingThroughItIsNeverRemembered(string which)
    {
        var a = Setup();

        switch (which)
        {
            case "fan":    { var v = a.F.FanState.Run(); v.Cpu.Curve[0] = 99; break; }
            case "gpu-oc": { var v = a.F.GpuOcState.Run(); v = v with { Core = 999, Mem = 999 }; break; }
            case "co":     { var v = a.F.CoDomains.Run(); v[0] = -30; break; }
        }

        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Equal(0, a.F.Store.SaveCount);
        Assert.Equal(70, a.F.FanState.Run().Cpu.FixedDuty);      // ...the next read is the default again
        Assert.Equal(0, a.F.GpuOcState.Run().Core);
        Assert.Equal(new[] { 0 }, a.F.CoDomains.Run());
    }

    // ---- the writers: a Set* creates exactly the one entry for the CURRENT mode ----

    [Fact]
    public void ApplyFanSelection_InsertsThePassedValues_UnderTheCurrentModeKey()
    {
        var a = Setup();

        a.F.ApplyFanSelection.Run(FanMode.Max, 42, 84);

        var stored = Assert.Single(a.F.Store.Settings.FanPresets);
        Assert.Equal("balanced", stored.Key);
        Assert.Equal((int)FanMode.Max, stored.Value.Mode);
        Assert.Equal(42, stored.Value.Cpu);
        Assert.Equal(84, stored.Value.Gpu);
        Assert.Equal(1, a.F.Store.SaveCount);
        Assert.Equal([FanMode.Max], a.Fan.ModeCalls);
        Assert.Empty(a.Fan.SpeedCalls);                          // Max is a mode switch, not a manual speed
    }

    /// <summary>The curve edit goes through the same <c>StoredFan()</c>, and must not disturb the fixed speeds
    /// the mode already carries (they are the fallback for a fan whose curve is off).</summary>
    [Fact]
    public void ApplyFanCurve_InsertsAPreset_KeepingTheFixedSpeeds()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);   // no fan port needed

        f.ApplyFanCurve.Run(gpu: false, use: true, points: [10, 20, 30, 40, 50]);

        var stored = Assert.Single(f.Store.Settings.FanPresets);
        Assert.Equal("balanced", stored.Key);
        Assert.True(stored.Value.CpuUseCurve);
        Assert.False(stored.Value.GpuUseCurve);
        Assert.Equal([10, 20, 30, 40, 50], stored.Value.CpuCurve);
        // The untouched half keeps its OWN curve — the ramp a fresh preset starts with — rather than becoming
        // empty: a curve is one duty% per anchor, and the half this edit did not name must survive it as a curve.
        Assert.Equal(Fan.DefaultDuties(), stored.Value.GpuCurve);
        Assert.Equal(70, stored.Value.Cpu);                      // the fixed speeds survive untouched
        Assert.Equal(70, stored.Value.Gpu);
        Assert.Equal(1, f.Store.SaveCount);
    }

    [Fact]
    public void ApplyGpuOffsets_InsertsAndAppliesTheOffsets()
    {
        var a = Setup();

        Assert.True(a.F.ApplyGpuOffsets.Run(new GpuAxisState(150, 800)));

        var stored = Assert.Single(a.F.Store.Settings.GpuOcPresets);
        Assert.Equal("balanced", stored.Key);
        Assert.Equal(150, stored.Value.Core);
        Assert.Equal(800, stored.Value.Mem);
        Assert.Equal(1, a.F.Store.SaveCount);
        Assert.Equal(new[] { (150, 800) }, a.Gpu.SetCalls);
    }

    /// <summary>The GPU power level is remembered per mode BESIDE the clock offsets and applied to the EC — the
    /// same shape as the offsets, on the same preset. A pick stores the persisted int and writes the level; a
    /// pick of "follow the profile" (null) stores no override and writes NOTHING to the EC (the envelope is the
    /// profile's own row then). Both arms in one test so neither can pass by the other's accident.</summary>
    [Fact]
    public void ApplyGpuPower_InsertsTheLevel_AppliesIt_AndClearsTheOverrideOnFollowTheProfile()
    {
        var a = Setup();

        Assert.True(a.F.ApplyGpuPower.Run(GpuPowerLevel.Turbo));
        var stored = Assert.Single(a.F.Store.Settings.GpuOcPresets);
        Assert.Equal("balanced", stored.Key);
        Assert.Equal((int)GpuPowerLevel.Turbo, stored.Value.Power);
        Assert.Equal([GpuPowerLevel.Turbo], a.Env.SetCalls);

        // Follow the profile: the override is cleared, and nothing is written to the EC.
        a.Env.SetCalls.Clear();
        Assert.True(a.F.ApplyGpuPower.Run(null));

        Assert.Null(Assert.Single(a.F.Store.Settings.GpuOcPresets).Value.Power);
        Assert.Empty(a.Env.SetCalls);
    }

    /// <summary>The offsets and the power level share one preset entry, so editing one must not clear the other,
    /// and each edit persists exactly once. The two are separate controls on one mode; a writer that rebuilt the
    /// preset instead of updating it would silently drop whichever half it did not name.</summary>
    [Fact]
    public void TheOffsetsAndThePowerLevel_ShareOnePresetWithoutClobberingEachOther()
    {
        var a = Setup();

        a.F.ApplyGpuOffsets.Run(new GpuAxisState(150, 800));
        a.F.ApplyGpuPower.Run(GpuPowerLevel.Quiet);

        var stored = Assert.Single(a.F.Store.Settings.GpuOcPresets).Value;
        Assert.Equal(150, stored.Core);
        Assert.Equal(800, stored.Mem);
        Assert.Equal((int)GpuPowerLevel.Quiet, stored.Power);
    }

    /// <summary>The power edit follows the offsets' "write with no port still persists the preset" asymmetry:
    /// the choice is recorded even when the machine has no EC envelope channel, so it comes back if the channel
    /// does. The write reports false (a capability fact), never a throw.</summary>
    [Fact]
    public void ApplyGpuPower_WithNoEnvelopePort_ReturnsFalse_ButStillPersistsTheChoice()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);   // no ports at all

        Assert.False(f.ApplyGpuPower.Run(GpuPowerLevel.Balanced));

        Assert.Equal((int)GpuPowerLevel.Balanced, Assert.Single(f.Store.Settings.GpuOcPresets).Value.Power);
    }

    [Fact]
    public void ApplyCpuPower_InsertsAndAppliesTheOverlayId()
    {
        var a = Setup();

        Assert.True(a.F.ApplyCpuPower.Run("best-performance"));

        var stored = Assert.Single(a.F.Store.Settings.CpuPowerModes);
        Assert.Equal("balanced", stored.Key);
        Assert.Equal("best-performance", stored.Value);
        Assert.Equal(1, a.F.Store.SaveCount);
        Assert.Equal(["best-performance"], a.Cpu.SetCalls);
    }

    /// <summary>NOTE the ordering, derived from the code and not from the doc prose: every <c>Set*</c>
    /// persists BEFORE it consults the port (`LaptopService.Tuning.cs` `IGpuOffsetsTarget.Store`/
    /// `ICpuPowerOverlayTarget.Store`, reached through `ApplyGpuOffsets`/`ApplyCpuPowerOverlay`, and `SetCo`),
    /// so a machine whose
    /// port is missing — the feature was probed away — still records the user's choice and returns false for the
    /// write. Losing the setting as well would be the surprising outcome; this is the behaviour as
    /// shipped, and it is the exact opposite of <c>SetCoDomains</c>, which returns before persisting when the
    /// port is null (`LaptopService.Tuning.cs` `SetCoDomains` — the port guard). See LaptopServiceCoTests for that half of the asymmetry.</summary>
    [Theory]
    [InlineData("gpu-oc")]
    [InlineData("co")]
    [InlineData("cpu-power")]
    public void AWritWithNoPort_ReturnsFalse_ButStillPersistsThePreset(string which)
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);   // no ports at all

        var (ok, error) = which switch
        {
            "gpu-oc"    => (f.ApplyGpuOffsets.Run(new GpuAxisState(150, 800)), (string?)null),
            "co"        => f.Service.SetCo(-12),
            "cpu-power" => (f.ApplyCpuPower.Run("best-performance"), (string?)null),
            _           => throw new ArgumentOutOfRangeException(nameof(which)),
        };

        Assert.False(ok);
        Assert.Null(error);                       // no port -> nothing was attempted, so there is no reason
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

        Write(a.F, which);
        Assert.Equal(1, CountOf(a.F.Store.Settings, which));
        Assert.Equal(["balanced"], KeysOf(a.F.Store.Settings, which).Order());

        a.F.Pp!.CurrentProfile = TestProfiles.Performance;       // the user switches profile

        switch (which)
        {
            case "fan":    Assert.Equal(70, a.F.FanState.Run().Cpu.FixedDuty); break;
            case "gpu-oc": Assert.Equal(0, a.F.GpuOcState.Run().Core); break;
            case "co":     Assert.Equal(0, a.F.CoDomains.Run()[0]); break;
        }
        Assert.Equal(1, CountOf(a.F.Store.Settings, which));     // ...and the read created nothing

        Write(a.F, which);
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

        a.F.ApplyCpuPower.Run("best-performance");
        a.Cpu.CurrentId = "best-efficiency";

        a.F.Pp!.CurrentProfile = TestProfiles.Performance;

        Assert.Equal("best-efficiency", a.F.CpuPower.Run());
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
/// re-apply paths (`LaptopService.cs` `ApplyStartupState` — the re-apply calls), so an inserting implementation would mint a preset for every
/// mode the machine ever passed through, and save on every boot. The asymmetry with the <c>Set*</c> methods
/// is the whole point of this file.
/// </summary>
public class LaptopServiceApplyModeGraphTests
{
    private sealed record Arranged(LaptopServiceFixture F,
                                   FakeFanControl Fan,
                                   FakeGpuOverclock Gpu,
                                   FakeGpuPowerEnvelope Env,
                                   FakeCurveOptimizer Co,
                                   FakeCpuPower Cpu);

    private static Arranged Setup(PerformanceProfile? current = null)
    {
        var f = LaptopServiceFixture.WithProfiles(current: current ?? TestProfiles.Balanced);
        var fan = new FakeFanControl();
        var gpu = new FakeGpuOverclock();
        var env = new FakeGpuPowerEnvelope();
        var co = new FakeCurveOptimizer();
        var cpu = new FakeCpuPower { CurrentId = "best-efficiency" };
        f.Device.FanControl = fan;
        f.Device.GpuOverclock = gpu;
        f.Device.GpuPowerEnvelope = env;
        f.Device.CurveOptimizer = co;
        f.Device.CpuPower = cpu;
        return new Arranged(f, fan, gpu, env, co, cpu);
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

        var before = PresetGraph.Counts(f.Store.Settings);

        switch (which)
        {
            case "fan":       f.ApplyModeFan.Run(); break;
            case "gpu-oc":    f.ApplyModeGpuOc.Run(); break;
            case "cpu-power": f.ApplyModeCpuPower.Run(); break;
            case "co":        f.ApplyModeCo.Run(); break;
        }

        // Read off the MODEL rather than off the instance this test seeded: the model took a copy of the
        // persisted half at construction (Settings' constructor), so "the graph" is the model's.
        Assert.Equal(before, PresetGraph.Counts(f.Store.Settings));
        Assert.Equal(["quiet"], f.Store.Settings.FanPresets.Keys);
        Assert.Equal(["quiet"], f.Store.Settings.GpuOcPresets.Keys);
        Assert.Equal(["quiet"], f.Store.Settings.CpuPowerModes.Keys);
        Assert.Equal(["quiet"], f.Store.Settings.CoPresets.Keys);
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>A mode the user never configured has no fan preset at all, and the fans are deliberately
    /// left alone rather than forced to a default — the hardware mode cannot be read back to seed one
    /// (<c>Settings.FanPresets</c>). So the port must not be touched either.</summary>
    [Fact]
    public void ApplyModeFan_OnAnUnconfiguredMode_ReturnsNull_AndLeavesTheFansAlone()
    {
        var a = Setup();

        Assert.Null(a.F.ApplyModeFan.Run());

        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Empty(a.Fan.ModeCalls);
        Assert.Empty(a.Fan.SpeedCalls);
        Assert.Equal(0, a.F.Store.SaveCount);
    }

    /// <summary>It hands back the stored preset in the DOMAIN's vocabulary — the caller sees the user's setting
    /// (not a default), carried as a <see cref="FanAxisState"/> value that cannot reach into the graph.</summary>
    [Fact]
    public void ApplyModeFan_WithAPreset_ReappliesIt_AndReturnsTheState()
    {
        var a = Setup();
        a.F.ApplyFanSelection.Run(FanMode.Max, 42, 84);
        a.Fan.ModeCalls.Clear();

        var applied = a.F.ApplyModeFan.Run();

        var stored = a.F.Store.Settings.FanPresets["balanced"];
        Assert.Equal((FanMode)stored.Mode, applied!.Value.Mode);   // ...and it says the same thing
        Assert.Equal(stored.Cpu, applied.Value.Cpu.FixedDuty);
        Assert.Equal([FanMode.Max], a.Fan.ModeCalls);
        Assert.Single(a.F.Store.Settings.FanPresets);            // still the one entry the writer made
        Assert.Equal(1, a.F.Store.SaveCount);                    // ...and still the one save
    }

    /// <summary>Custom is the exception to the "push it now" rule: the manual speeds are driven from the
    /// temperature curve on every refresh, so the mode CHANGE only switches behaviour — driving a fixed
    /// speed here would fight the curve (`LaptopService.Fans.cs` `ApplyModeFan`, `ApplyCustom`).</summary>
    [Fact]
    public void ApplyModeFan_WithACustomPreset_DefersToTheSensorLoop()
    {
        var settings = new Settings();
        settings.FanPresets["balanced"] = new FanPreset { Mode = (int)FanMode.Custom, Cpu = 30, Gpu = 40 };
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);
        var fan = new FakeFanControl();
        f.Device.FanControl = fan;

        var applied = f.ApplyModeFan.Run();

        // As above: the stored preset is the MODEL's, and the value carries its mode in the domain's vocabulary.
        Assert.Equal((FanMode)f.Store.Settings.FanPresets["balanced"].Mode, applied!.Value.Mode);
        Assert.Empty(fan.ModeCalls);
        Assert.Empty(fan.SpeedCalls);
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>A STORED <c>Mode</c> THAT NAMES NO <see cref="FanMode"/> IS READ AS AUTO, and the port is asked
    /// for a mode the enum names. The value is an <c>int</c> in the file, so 0, 7 and 300 are all writable by
    /// hand — and the cast this replaced was legal for every one of them, which is how a stored 7 became the
    /// behaviour byte the WMI argument carries into the EC (<c>AcerDevice.Windows.cs</c> <c>SetFanMode</c>) while
    /// the Linux backend's switch quietly treated it as Custom and wrote nothing. Neither was a decision.
    ///
    /// 300 is the row that proves the check is on the INT and not on the widened byte: <see cref="FanMode"/>'s
    /// underlying type is <c>byte</c>, so <c>(FanMode)300</c> wraps to 44 and a check performed after the cast
    /// would be asked about a different number than the file holds.
    ///
    /// MUTATION THAT REDDENS IT: replacing <c>FanModeOf</c> with <c>(FanMode)f.Mode</c> — the port then records
    /// a mode the enum does not name (and <c>AxisStateOf</c> returns one).</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(300)]
    [InlineData(-1)]
    public void AStoredModeThatNamesNoFanMode_IsReadAsAuto_AtEveryDoor(int stored)
    {
        var settings = new Settings();
        settings.FanPresets["balanced"] = new FanPreset { Mode = stored, Cpu = 40, Gpu = 50 };
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);
        var fan = new FakeFanControl();
        f.Device.FanControl = fan;

        // the read the whole UI goes through, and the one the re-apply outcome is built from
        Assert.Equal(FanMode.Auto, LaptopService.AxisStateOf(f.Store.Settings.FanPresets["balanced"]).Mode);

        // ...and the mode switch, which is where the EC is asked
        f.ApplyModeFan.Run();

        Assert.Equal([FanMode.Auto], fan.ModeCalls);
        Assert.Empty(fan.SpeedCalls);          // Auto is a behaviour, not a manual speed
    }

    /// <summary>An undefined stored mode is NOT Custom, so the sensor loop leaves the fans alone — the other
    /// half of the same rule, and the one whose failure would be invisible: a value that fell into the Custom
    /// branch would start driving the fans from the curves for a mode the user never configured.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(300)]
    public void AStoredModeThatNamesNoFanMode_DoesNotStartDrivingTheCurves(int stored)
    {
        var settings = new Settings();
        settings.FanPresets["balanced"] = new FanPreset
        {
            Mode = stored, Cpu = 40, Gpu = 50, CpuUseCurve = true, GpuUseCurve = true,
        };
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);
        var fan = new FakeFanControl();
        f.Device.FanControl = fan;

        f.ApplyCustom.Run(new SensorSnapshot { CpuTempC = 70, GpuTempC = 70 });

        Assert.Empty(fan.ModeCalls);
        Assert.Empty(fan.SpeedCalls);
    }

    /// <summary>A FIXED SPEED OUTSIDE THE DUTY RANGE COMES BACK INSIDE IT when the preset is written, because
    /// the state the write is built from was constructed by <see cref="FanSettings"/>, which clamps. The file is
    /// hand-editable, so 300 is what a user can put there; what they must not be able to do is leave a value in
    /// the graph that only some of its readers clamp.
    ///
    /// WHERE THIS IS AND IS NOT OBSERVABLE, measured rather than assumed: the <c>(byte)</c> cast in
    /// <c>IFanAxisTarget.ReplaceSelection</c> and its sibling in <c>ApplyModeFan</c> feed
    /// <c>ApplyFan</c>, which DISCARDS the two speeds unless the mode is Custom — and the Custom case goes
    /// through <c>ApplyCustom</c>, whose <c>Fan.Duty</c> clamps independently. So this value never reached the EC
    /// out of range even before the fix: the defect was a bypass that the clamp happened to cover one call
    /// further down, and what changes here is that the FILE now agrees with the hardware instead of holding a
    /// number nothing can act on.
    ///
    /// MUTATION THAT REDDENS IT: removing the <c>Math.Clamp</c> from <c>FanSettings</c>'s constructor — the
    /// stored preset then keeps 300 and -5.</summary>
    [Fact]
    public void AFixedSpeedOutsideTheDutyRange_IsBroughtInsideIt_WhenThePresetIsWritten()
    {
        var settings = new Settings();
        settings.FanPresets["balanced"] = new FanPreset
        {
            Mode = (int)FanMode.Max, Cpu = 300, Gpu = -5,
        };
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);
        f.Device.FanControl = new FakeFanControl();

        f.ApplyFanCurve.Run(gpu: false, use: false, points: [10, 20, 30, 40, 50]);

        var stored = f.Store.Settings.FanPresets["balanced"];
        Assert.Equal(100, stored.Cpu);   // 300 as a byte would be 44; as a duty% it is the ceiling
        Assert.Equal(0, stored.Gpu);
    }

    /// <summary>The DEPTH of the copy the READ makes, which is the part that is easy to get wrong: the state
    /// the UI reads carries curve ARRAYS, and a copy that took the fields but kept the arrays would still let a
    /// caller rewrite the user's fan curve in place, with no Set method and no lock — the widest remaining door
    /// of exactly the kind this closes. The read crosses as <see cref="FanAxisState"/> (a value), whose curve
    /// came through <see cref="FanSettings"/>'s copying constructor, so the array the caller gets is not the
    /// stored one.</summary>
    [Fact]
    public void AMutatedSnapshotNeverReachesTheStoredPreset_NotEvenThroughItsArrays()
    {
        var a = Setup();
        a.F.ApplyFanCurve.Run(gpu: false, use: true, points: [10, 20, 30, 40, 50]);

        var snapshot = a.F.FanState.Run();
        snapshot.Cpu.Curve[0] = 99;

        var stored = a.F.Store.Settings.FanPresets["balanced"];
        Assert.Equal(10, stored.CpuCurve[0]);                    // the array is a copy...
        Assert.True(stored.CpuUseCurve);
    }

    /// <summary>The same claim for the Curve Optimizer, whose read hands back an int ARRAY rather than the
    /// preset's dictionary. The read is a fresh array each call, so writing through it cannot reach the store.
    /// The preset is seeded directly so it carries a non-default value.</summary>
    [Fact]
    public void AMutatedCoSnapshotNeverReachesTheStoredPreset()
    {
        var settings = new Settings();
        settings.CoPresets["balanced"] = new CoPreset { AllCore = -15, Domains = { ["big"] = -10 } };
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);
        f.Device.CurveOptimizer = new FakeCurveOptimizer().WithDomains(new VoltageDomain("Zen 5", "big"));

        var snapshot = f.CoDomains.Run();
        snapshot[0] = 0;

        var stored = settings.CoPresets["balanced"];
        Assert.Equal(-10, stored.Domains["big"]);                // the array is a copy...
        Assert.Equal(-15, stored.AllCore);                       // ...and the scalar is the stored one
    }

    /// <summary>GPU offsets follow the OPPOSITE contract to fans (<c>Settings.GpuOcPresets</c>): an unconfigured mode
    /// is definitely stock, because the driver zeroes the offsets on every boot — so switching to it MUST
    /// clear whatever the previous mode applied. Hence the port still gets a (0, 0) write, while the graph
    /// still gains nothing.</summary>
    [Fact]
    public void ApplyModeGpuOc_OnAnUnconfiguredMode_WritesStock_ButInsertsNothing()
    {
        var a = Setup();

        var g = a.F.ApplyModeGpuOc.Run();

        Assert.Equal(0, g.Core);
        Assert.Equal(0, g.Mem);
        Assert.Equal(new[] { (0, 0) }, a.Gpu.SetCalls);
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Equal(0, a.F.Store.SaveCount);
    }

    [Fact]
    public void ApplyModeGpuOc_WithAPreset_WritesTheStoredOffsets_AndReturnsTheState()
    {
        var a = Setup();
        a.F.ApplyGpuOffsets.Run(new GpuAxisState(150, 800));
        a.Gpu.SetCalls.Clear();

        var applied = a.F.ApplyModeGpuOc.Run();

        var stored = a.F.Store.Settings.GpuOcPresets["balanced"];
        Assert.Equal(stored.Core, applied.Core);
        Assert.Equal(stored.Mem, applied.Mem);
        Assert.Equal(new[] { (150, 800) }, a.Gpu.SetCalls);
        Assert.Single(a.F.Store.Settings.GpuOcPresets);
        Assert.Equal(1, a.F.Store.SaveCount);
    }

    [Fact]
    public void ApplyModeGpuOc_WithNoPort_StillReturnsThePreset_AndInsertsNothing()
    {
        var settings = new Settings();
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);

        var g = f.ApplyModeGpuOc.Run();

        Assert.Equal(0, g.Core);
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(f.Store.Settings));
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>THE DIFFERENTIAL THE OWNER ASKED FOR, both arms on the same mode-switch path:
    ///
    /// * a mode that PINNED a power level writes that level to the EC (<c>Env.SetCalls</c>) and reports it in
    ///   the state the UI reflects — the envelope is decoupled from the profile;
    /// * a mode on the DEFAULT ("follow the profile") writes NOTHING to the EC — the profile switch already
    ///   drove the class-derived row, and forcing one here would re-introduce the coupling this axis removes.
    ///
    /// Both are asserted together so "the override is applied" and "the default is left alone" cannot both pass
    /// by the same accident (e.g. a target that always writes, or never does).</summary>
    [Fact]
    public void ApplyModeGpuOc_AppliesAPinnedPowerLevel_ButWritesNothingWhenItFollowsTheProfile()
    {
        var a = Setup();

        // Default: follow the profile — nothing on the EC, state reports no override.
        var follow = a.F.ApplyModeGpuOc.Run();
        Assert.Null(follow.Power);
        Assert.Empty(a.Env.SetCalls);

        // Pin a level for this mode (through the edit use case), then switch to it again.
        a.F.ApplyGpuPower.Run(GpuPowerLevel.Performance);
        a.Env.SetCalls.Clear();

        var pinned = a.F.ApplyModeGpuOc.Run();

        Assert.Equal(GpuPowerLevel.Performance, pinned.Power);
        Assert.Equal([GpuPowerLevel.Performance], a.Env.SetCalls);
    }

    /// <summary>The mode switch writes the level ONCE, and an unconfigured mode writes none — the same claim as
    /// the fan/offset axes, but the load-bearing case here is that the "follow the profile" arm does not
    /// accidentally inherit the PREVIOUS mode's level. Two modes, each with its own graph entry, switched
    /// between: the second's default must clear the first's override rather than carry it.</summary>
    [Fact]
    public void AModeSwitch_DoesNotCarryAnotherModesPowerLevelIntoOneThatFollowsTheProfile()
    {
        var a = Setup();
        // Seed Quiet's pinned level directly on the model's graph (the shape a file would supply).
        a.F.Store.Settings.GpuOcPresets["quiet"] = new GpuOcPreset { Power = (int)GpuPowerLevel.Balanced };

        // The live mode is Balanced — no entry -> follow the profile -> no EC write.
        a.F.ApplyModeGpuOc.Run();
        Assert.Empty(a.Env.SetCalls);

        // Now make Quiet live: its pinned Balanced level is written.
        a.F.Pp!.CurrentProfile = TestProfiles.Quiet;
        var applied = a.F.ApplyModeGpuOc.Run();

        Assert.Equal(GpuPowerLevel.Balanced, applied.Power);
        Assert.Equal([GpuPowerLevel.Balanced], a.Env.SetCalls);
    }

    /// <summary>CPU power follows the FAN contract, not the GPU one (<c>Settings.CpuPowerModes</c>): an unconfigured
    /// mode is left untouched, because forcing an OS power mode on a profile the user never configured would
    /// be a change nobody asked for. So the port is not written — only read, to report reality.</summary>
    [Fact]
    public void ApplyModeCpuPower_OnAnUnconfiguredMode_WritesNothing_AndInsertsNothing()
    {
        var a = Setup();

        Assert.Equal("best-efficiency", a.F.ApplyModeCpuPower.Run());

        Assert.Empty(a.Cpu.SetCalls);
        Assert.Equal(PresetGraph.Empty, PresetGraph.Counts(a.F.Store.Settings));
        Assert.Equal(0, a.F.Store.SaveCount);
    }

    [Fact]
    public void ApplyModeCpuPower_WithAnEntry_AppliesTheStoredOverlay_AndInsertsNothing()
    {
        var a = Setup();
        a.F.ApplyCpuPower.Run("best-performance");
        a.Cpu.SetCalls.Clear();

        Assert.Equal("best-performance", a.F.ApplyModeCpuPower.Run());

        Assert.Equal(["best-performance"], a.Cpu.SetCalls);
        Assert.Single(a.F.Store.Settings.CpuPowerModes);
        Assert.Equal(1, a.F.Store.SaveCount);
    }

    /// <summary>NOTE: the port is checked BEFORE the store (`LaptopService.Tuning.cs` `ApplyModeCpuPower` — the port check first), so on a machine whose
    /// overlay API does not answer, a mode the user DID configure reports as nothing at all rather than as
    /// its stored id. Pinned as shipped; the stored choice is still on disk and still reappears if the port
    /// comes back.</summary>
    [Fact]
    public void ApplyModeCpuPower_WithNoPort_IsNull_EvenWhenAModeIsConfigured()
    {
        var settings = new Settings();
        settings.CpuPowerModes["balanced"] = "best-performance";
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);

        Assert.Null(f.ApplyModeCpuPower.Run());
        Assert.Equal(0, f.Store.SaveCount);
        Assert.Equal(["balanced"], f.Store.Settings.CpuPowerModes.Keys);   // read off the MODEL — see above
    }
}

/// <summary>
/// <c>GetOrAdd</c> (`LaptopService.cs` `GetOrAdd`) — the "look up, create and INSERT when absent, otherwise hand
/// back the instance already there" idiom every per-mode preset shares — plus the two reads on the lighting path
/// that DO insert: <see cref="LaptopService.LightsForCurrentMode()"/>, which creates the mode's preset, and
/// <see cref="ReadLightZone"/>, which creates the zone's entry inside it. Those two are the documented exception
/// to the rule the previous classes pin, and they are worth their own assertions precisely because they look
/// like the readers around them.
///
/// THE LIGHTING ACCESSORS CHANGED SHAPE, AND THAT IS THE OWNER'S RULING rather than this file's convenience:
/// the mode's lighting used to leave the service as the stored zone DICTIONARY, which the UI held and edited in
/// place, and the guards here pinned the aliasing with <c>Assert.Same</c>. What leaves now is a door
/// (<see cref="ILightZoneMode"/>) that reads and writes VALUES, so those guards are gone. Every rule they were
/// protecting is still pinned below, against the door instead of against the dictionary.
/// </summary>
public class LaptopServicePresetGetOrAddTests
{
    /// <summary>The read creates the mode's preset and NOTHING inside it, and it saves nothing: a mode that has
    /// merely been looked at has been configured by nobody, which is why the app can restore a mode's lighting
    /// without inventing state for the modes it never touched.
    ///
    /// MUTATION THAT REDDENS IT: dropping the bucket creation (`LightZoneMode`'s field initializer) leaves
    /// <c>LightPresets</c> empty; calling <c>Save()</c> on the read path makes <c>SaveCount</c> one.</summary>
    [Fact]
    public void LightsForCurrentMode_InsertsOneEmptyPreset_ButSavesNothing()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        var mode = f.Service.LightsForCurrentMode();

        Assert.Empty(mode.Stored());
        Assert.Equal(["balanced"], f.Store.Settings.LightPresets.Keys);
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>Both calls reach ONE mode, and the guard that says so is no longer an aliasing assertion. The
    /// door used to BE the stored dictionary, so <c>Assert.Same</c> was the whole proof; the doors are now
    /// deliberately different objects, and the rule underneath — that the app has one bucket per mode and not a
    /// fresh one per call — is pinned by writing through one door and reading through the other.
    ///
    /// MUTATION THAT REDDENS IT: a fresh key per call (the write lands in a bucket the second door never sees).</summary>
    [Fact]
    public void LightsForCurrentMode_ReachesTheSameModeOnEveryCall()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        var first = f.Service.LightsForCurrentMode();
        var second = f.Service.LightsForCurrentMode();
        first.Write("keyboard", Zone(brightness: 37));

        Assert.Equal(37, second.Stored()["keyboard"].Brightness);
        Assert.Single(f.Store.Settings.LightPresets);             // absent inserted exactly one, present none
    }

    /// <summary>Each mode keeps its own zones, and the door is the only way to see them, so the assertion is that
    /// a write through one mode's door is INVISIBLE through another's — which is what "the zones do not bleed
    /// across modes" meant when the caller could reach into the dictionary directly.
    ///
    /// MUTATION THAT REDDENS IT: keying every door off one bucket (the write would show up in both).</summary>
    [Fact]
    public void LightsForCurrentMode_IsPerMode()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        var balanced = f.Service.LightsForCurrentMode();
        f.Pp!.CurrentProfile = TestProfiles.Performance;
        var performance = f.Service.LightsForCurrentMode();
        balanced.Write("keyboard", Zone(brightness: 42));

        Assert.Equal(["balanced", "performance"], f.Store.Settings.LightPresets.Keys.Order());
        Assert.Empty(performance.Stored());                       // the zones do not bleed across modes
        Assert.Equal(42, balanced.Stored()["keyboard"].Brightness);
    }

    /// <summary>The read's insertion rule, which used to be <c>EnsureLightZone</c>'s and is now
    /// <see cref="ReadLightZone"/>'s: a zone this mode has none for is CREATED, once, holding the defaults — and
    /// the creation is not persisted.
    ///
    /// MUTATION THAT REDDENS IT: answering with the defaults without writing (the entry would not exist), or
    /// writing on every read (a second read would overwrite what is stored, caught by the sibling below).</summary>
    [Fact]
    public void ReadLightZone_InsertsTheZoneOnFirstSight_WithTheDefaults()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced, declare: Keyboard());
        var mode = f.Service.LightsForCurrentMode();

        // Control: the machine really does advertise this zone — without it, "nothing was created" would hold
        // for a zone the door refuses to know about, which is the sibling branch's subject and not this one.
        Assert.True(mode.Advertises("keyboard"));

        var first = new ReadLightZone(mode).Run("keyboard");
        var second = new ReadLightZone(mode).Run("keyboard");

        Assert.Equal(LightZoneState.Default.Brightness, first.Brightness);
        Assert.False(first.Configured);                          // looked at is not configured
        Assert.Equal(first.Brightness, second.Brightness);
        Assert.Equal(["keyboard"], f.Store.Settings.LightPresets["balanced"].Zones.Keys);
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>GetOrAdd must return what is THERE, not overwrite it with a default — an entry the user has
    /// already configured surviving a second read is the whole reason the creation exists at all.
    ///
    /// MUTATION THAT REDDENS IT: writing the defaults unconditionally in <c>ReadLightZone</c>.</summary>
    [Fact]
    public void ReadLightZone_KeepsAnExistingEntrysState()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced, declare: Keyboard());
        var mode = f.Service.LightsForCurrentMode();
        mode.Write("keyboard", Zone(brightness: 37));

        var zone = new ReadLightZone(mode).Run("keyboard");

        Assert.True(zone.Configured);
        Assert.Equal(37, zone.Brightness);
        Assert.Single(mode.Stored());
    }

    /// <summary>A zone per name, and no more: two names are two entries.
    ///
    /// MUTATION THAT REDDENS IT: keying the insertion off something other than the zone name.</summary>
    [Fact]
    public void ReadLightZone_WithDifferentNames_AddsOneEntryEach()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced,
                                                  declare: Keyboard("keyboard", "lightbar"));
        var mode = f.Service.LightsForCurrentMode();

        new ReadLightZone(mode).Run("keyboard");
        new ReadLightZone(mode).Run("lightbar");

        Assert.Equal(["keyboard", "lightbar"], f.Store.Settings.LightPresets["balanced"].Zones.Keys.Order());
    }

    /// <summary>What the door hands out is a COPY, down to the zone-colour array — the rule the six closed
    /// accessors state for the presets, and here the difference is measurable because the graph is reachable:
    /// rewriting the array a caller was given must not reach the stored one.
    ///
    /// MUTATION THAT REDDENS IT: returning the stored array itself in <c>LightZoneMode.ToDomain</c>.</summary>
    [Fact]
    public void TheStateTheDoorHandsOut_SharesNoArrayWithTheGraph()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced, declare: Keyboard());
        var mode = f.Service.LightsForCurrentMode();
        mode.Write("keyboard", new LightZoneState(true, 0, 50, 5, 1, 0x00FF00, [0x111111, 0x222222]));

        var handed = mode.Stored()["keyboard"];
        handed.ZoneColors[0] = 0x999999;

        Assert.Equal(0x111111, f.Store.Settings.LightPresets["balanced"].Zones["keyboard"].ZoneColors[0]);
    }

    /// <summary>A configured zone as the value the contract carries; the fields this file does not vary are the
    /// schema's own defaults, so the value reads as a zone the user has set rather than as a bare struct.</summary>
    private static LightZoneState Zone(int brightness)
        => new(Configured: true, EffectIndex: 0, Brightness: brightness, Speed: 5, Direction: 1, Color: 0xFF0000,
               ZoneColors: []);

    /// <summary>Declare RGB zones on the fake device BEFORE the service exists — the advertised list is what
    /// <c>ReadLightZone</c> and <c>ApplyLightZone</c> ask about, and a declaration that lands after construction
    /// cannot reach the model (see <see cref="LaptopServiceFixture"/>).</summary>
    private static Action<FakeDevice> Keyboard(params string[] zones) => d =>
    {
        var names = zones.Length > 0 ? zones : ["keyboard"];
        d.Lighting = new FakeRgbDevice { Zones = [.. names.Select(n => FakeRgbController.Zone(n))] };
    };

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
            case "fan":    f.ApplyFanSelection.Run(FanMode.Auto, 10, 20); break;
            case "gpu-oc": f.ApplyGpuOffsets.Run(new GpuAxisState(10, 20)); break;
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
            case "fan":    f.ApplyFanSelection.Run(FanMode.Max, 30, 40); break;
            case "gpu-oc": f.ApplyGpuOffsets.Run(new GpuAxisState(30, 40)); break;
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
    /// and lighting apply while Turbo sits over it (`LaptopService.Profiles.cs` `CurrentModeKey` — both overloads).</summary>
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
            case "fan":       f.ApplyFanSelection.Run(FanMode.Max, 42, 84); break;
            case "gpu-oc":    f.ApplyGpuOffsets.Run(new GpuAxisState(150, 800)); break;
            case "co":        f.Service.SetCo(-12); break;
            case "cpu-power": f.ApplyCpuPower.Run("best-performance"); break;
        }

        // Read off the MODEL rather than off the instance this test seeded: the model took its own copy of the
        // persisted half at construction (Settings' constructor), so "the bucket the write landed in" is the
        // model's — and reading it here does not depend on how deep that copy goes.
        IEnumerable<string> keys = which switch
        {
            "fan"       => f.Store.Settings.FanPresets.Keys,
            "gpu-oc"    => f.Store.Settings.GpuOcPresets.Keys,
            "co"        => f.Store.Settings.CoPresets.Keys,
            "cpu-power" => f.Store.Settings.CpuPowerModes.Keys,
            _           => throw new ArgumentOutOfRangeException(nameof(which)),
        };

        Assert.Equal(["balanced"], keys);
        Assert.Equal("balanced", f.Service.CurrentModeKey());     // the key that did it
    }
}
