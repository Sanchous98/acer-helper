using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// CHARACTERISATION of the startup row of the volatile-state re-apply schedule: which axes
/// <see cref="LaptopService.ApplyStartupState"/> drives, with what values, and — the point of this file — which
/// axis it deliberately does not touch.
///
/// WHY THIS IS NOT ALREADY COVERED. <c>LaptopServicePresetTests</c> pins each <c>ApplyMode*</c> on its own.
/// What no test pinned is the COMPOSITION at startup: the boot path drives the GPU offsets, the CPU power
/// overlay and the Curve Optimizer, and leaves the fans alone. That composition is written by hand in three
/// places — startup, mode change, resume — and startup is the only one of the three a test can reach.
/// <c>AppController.BackgroundPass</c> and <c>LightingCoordinator.OnResume</c> each need a desktop lifetime
/// and a live refresh loop, which this suite does not have (<c>CpuPrimeTests</c> records that); a test that
/// drove those two would be asserting its own copy of the schedule rather than the product's. They are
/// therefore NOT characterised here, and the title of this file is narrow on purpose.
///
/// THE NAMES SAY WHAT HAPPENS, NOT WHAT SHOULD. <c>StartupDoesNotTouchTheFans</c> is a fact about today, not a
/// requirement. Whether a boot should also push the mode's fan preset is an open question — the fan axis
/// documents itself as applying "on a mode change" only, and the EC latches the fan mode across a reboot — and
/// a test named as a requirement would quietly settle that question instead of recording it.
/// </summary>
public class ReconcileScheduleTests
{
    private sealed record Arranged(LaptopServiceFixture F,
                                   FakeFanControl Fan,
                                   FakeGpuOverclock Gpu,
                                   FakeCurveOptimizer Co,
                                   FakeCpuPower Cpu);

    /// <summary>Every volatile axis attached at once, so one startup run can be observed on all of them. The
    /// device reports "balanced" as current, which is the key the presets below are filed under.</summary>
    private static Arranged Setup(Settings settings)
    {
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);
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

    // ---- the axes the startup row DOES drive ----

    /// <summary>The GPU offsets are re-asserted from the current mode's preset, because the dGPU driver zeroes
    /// them on every boot and driver reload — so if the app does not put them back, the user's overclock is
    /// simply gone after a restart.</summary>
    [Fact]
    public void StartupReAppliesTheCurrentModesGpuOffsets()
    {
        var settings = new Settings();
        settings.GpuOcPresets["balanced"] = new GpuOcPreset { Core = -150, Mem = 800 };
        var a = Setup(settings);

        a.F.Service.ApplyStartupState();

        Assert.Equal(new[] { (-150, 800) }, a.Gpu.SetCalls);
    }

    /// <summary>...and a mode that was never configured is written as STOCK (0/0) rather than skipped: the
    /// driver zeroes the offsets anyway, so "definitely stock" is a value to apply, not an absence. This is the
    /// contract that differs from the fan and CPU-power axes below, and startup is where it matters most.</summary>
    [Fact]
    public void StartupWritesStockGpuOffsetsForAModeThatWasNeverConfigured()
    {
        var a = Setup(new Settings());

        a.F.Service.ApplyStartupState();

        Assert.Equal(new[] { (0, 0) }, a.Gpu.SetCalls);
    }

    /// <summary>The CPU power overlay IS driven when the current mode has one stored.</summary>
    [Fact]
    public void StartupAppliesTheStoredCpuPowerMode()
    {
        var settings = new Settings();
        settings.CpuPowerModes["balanced"] = "best-performance";
        var a = Setup(settings);

        a.F.Service.ApplyStartupState();

        Assert.Equal(["best-performance"], a.Cpu.SetCalls);
    }

    /// <summary>...and is left alone when it does not: an unconfigured profile does not get an OS power mode
    /// forced onto it, which is why this axis reads the live overlay back instead of writing one. The store is
    /// non-empty — another mode holds a preset — so this exercises "this mode has none" rather than "the user
    /// has never used the feature".</summary>
    [Fact]
    public void StartupLeavesTheCpuPowerOverlayAloneForAModeThatWasNeverConfigured()
    {
        var settings = new Settings();
        settings.CpuPowerModes["quiet"] = "best-efficiency";
        var a = Setup(settings);

        a.F.Service.ApplyStartupState();

        Assert.Empty(a.Cpu.SetCalls);
    }

    /// <summary>The Curve Optimizer offset is re-asserted too — it lives in SMU state that a power cycle
    /// restores to stock, so the app is the source of truth across a reboot. It is the one axis the startup row
    /// dispatches ASYNCHRONOUSLY (a mailbox transaction can wait seconds on the machine-wide PCI lock), hence
    /// the poll rather than a direct assertion.</summary>
    [Fact]
    public void StartupReAppliesTheCurrentModesCurveOptimizerOffset()
    {
        var settings = new Settings();
        settings.CoPresets["balanced"] = new CoPreset { AllCore = -20 };
        var a = Setup(settings);

        a.F.Service.ApplyStartupState();

        Assert.True(Eventually.Until(() => a.Co.SetCalls.Count > 0), "startup never reached the SMU");
        Assert.Equal([-20], a.Co.SetCalls);   // the fake exposes no domains, so the all-core path is the one that runs
    }

    /// <summary>The fourth nuance of "no preset", and the one a table of three policies would lose: an install
    /// where NO mode ever had a Curve-Optimizer preset does not talk to the SMU at all, while a mode that merely
    /// lacks one gets stock written actively. An empty store means the user never opted into undervolting, so the
    /// mailbox message would be traffic nobody asked for on an opcode this CPU does not confirm.
    ///
    /// Called directly rather than through startup: the rule belongs to the axis, and a negative assertion about
    /// a fire-and-forget task would be a race rather than a test.</summary>
    [Fact]
    public void ApplyModeCo_LeavesTheSmuAloneWhenNoModeEverHadAPreset()
    {
        var a = Setup(new Settings());

        a.F.Service.ApplyModeCo();

        Assert.Empty(a.Co.SetCalls);
        Assert.Empty(a.Co.SetDomainsCalls);
    }

    /// <summary>...while a store holding a preset for ANOTHER mode puts this mode to stock actively. That
    /// difference is why "unconfigured" means two things on this one axis, and it is what stops a stale
    /// undervolt from being inherited by a profile the user never configured.</summary>
    [Fact]
    public void ApplyModeCo_WritesStockWhenOnlyAnotherModeHasAPreset()
    {
        var settings = new Settings();
        settings.CoPresets["quiet"] = new CoPreset { AllCore = -25 };
        var a = Setup(settings);

        a.F.Service.ApplyModeCo();

        Assert.Equal([0], a.Co.SetCalls);
    }

    // ---- the axis the startup row does NOT drive ----

    /// <summary>THE POINT OF THIS FILE. The startup row drives the three volatile axes and does not touch the
    /// fans, even though the current mode HAS a fan preset to apply. The two volatile assertions are controls
    /// proving the run actually happened (otherwise this would pass against a startup that did nothing at all),
    /// and the fan preset is read back to prove the claim is not vacuous: there IS something to apply, and it is
    /// still not applied.
    ///
    /// The fan axis sits outside the volatile set on purpose — its mode is latched by the EC and survives a
    /// reboot, so there is nothing to re-assert, and <c>LaptopService.Fans.cs</c> documents <c>ApplyModeFan</c>
    /// as running "on a mode change" only. Whether that is right is the owner's call; this records it, and is
    /// named so it cannot be mistaken for the requirement.</summary>
    [Fact]
    public void StartupDoesNotTouchTheFans()
    {
        var settings = new Settings();
        settings.FanPresets["balanced"] = new FanPreset { Mode = (int)FanMode.Max, Cpu = 42, Gpu = 84 };
        settings.GpuOcPresets["balanced"] = new GpuOcPreset { Core = -150, Mem = 800 };
        settings.CpuPowerModes["balanced"] = "best-performance";
        var a = Setup(settings);

        a.F.Service.ApplyStartupState();

        // Controls: the run happened, and it drove the axes it is supposed to.
        Assert.Equal(new[] { (-150, 800) }, a.Gpu.SetCalls);
        Assert.Equal(["best-performance"], a.Cpu.SetCalls);
        Assert.NotNull(settings.FanPresets["balanced"]);   // there IS a preset for this mode...

        Assert.Empty(a.Fan.ModeCalls);                     // ...and it is still not applied
        Assert.Empty(a.Fan.SpeedCalls);
    }

    /// <summary>Startup does not mint presets for a mode that has none — the read/write asymmetry the whole
    /// per-mode graph rests on, checked here on the one path that runs on every boot. A startup that created an
    /// entry would grow the file with a preset for every mode the machine ever passed through.</summary>
    [Fact]
    public void StartupInsertsNothingIntoThePresetGraph()
    {
        var settings = new Settings();
        settings.GpuOcPresets["quiet"] = new GpuOcPreset { Core = 100, Mem = 200 };
        var a = Setup(settings);
        var before = PresetGraph.Counts(settings);

        a.F.Service.ApplyStartupState();

        Assert.Equal(before, PresetGraph.Counts(settings));
        Assert.Equal(0, a.F.Store.SaveCount);
    }
}
