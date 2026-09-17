using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The five per-mode axes as data (<see cref="ModeAxisTable"/>), checked against what the SERVICE actually
/// does — which is the only reason the table earns its place over the prose comments it replaced.
///
/// A TABLE ASSERTED AGAINST ITSELF IS A TAUTOLOGY. Every test here therefore drives the real
/// <see cref="LaptopService"/> through a fake port and reads the CALL LOG, then compares that observation with
/// the table's answer. If the two disagree the code has drifted from its own documentation, which is the
/// failure this file exists to catch — the policies were previously stated in five blocks of prose that
/// nothing could check.
///
/// WHAT IS NOT CHECKED HERE, and why. The lighting axis's policy (<c>InheritPrevious</c>) is enforced in
/// <c>UI/ViewModels/LightingViewModel.cs</c>, not in the service — it is a view-model rule about carrying the
/// previous mode's appearance into an unconfigured one, and the suite cannot construct a view-model (it needs
/// a desktop lifetime). Its table entry is therefore stated but not cross-checked, and that gap is named rather
/// than papered over. The CPU-power and fan axes are the ones whose enforcement IS reachable, and they are.
/// </summary>
public class ModeAxisTableTests
{
    private sealed record Arranged(LaptopServiceFixture F, FakeFanControl Fan, FakeGpuOverclock Gpu,
                                   FakeCurveOptimizer Co, FakeCpuPower Cpu);

    /// <summary>Every axis port attached, so one run can be observed on all of them. The device reports
    /// "balanced" as current, which is the key the presets are filed under.</summary>
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

    /// <summary>A settings graph with a preset for the CURRENT mode on every axis that can hold one, so no
    /// assertion below can pass merely because there was nothing to apply.</summary>
    private static Settings FullyConfigured()
    {
        var s = new Settings();
        s.FanPresets["balanced"] = new FanPreset { Mode = (int)FanMode.Max, Cpu = 42, Gpu = 84 };
        s.GpuOcPresets["balanced"] = new GpuOcPreset { Core = -150, Mem = 800 };
        s.CpuPowerModes["balanced"] = "best-performance";
        s.CoPresets["balanced"] = new CoPreset { AllCore = -20 };
        s.LightPresets["balanced"] = new LightPreset();
        return s;
    }

    // ---- the policies, each checked against the service rather than restated ----

    /// <summary><c>LeaveUntouched</c>: the EC's fan mode cannot be read back, so an absent preset is not a known
    /// state and nothing may be written. The store is non-empty (another mode is configured) so this exercises
    /// "this mode has none" rather than "the user has never used fans".</summary>
    [Fact]
    public void FansAreLeftUntouched_AndTheServiceAgrees()
    {
        var settings = new Settings();
        settings.FanPresets["quiet"] = new FanPreset { Mode = (int)FanMode.Max };
        var a = Setup(settings);

        Assert.Equal(EmptyAxisPolicy.LeaveUntouched, ModeAxisTable.Policy(ModeAxis.Fans));

        Assert.Null(a.F.Service.ApplyModeFan());
        Assert.Empty(a.Fan.ModeCalls);
        Assert.Empty(a.Fan.SpeedCalls);
    }

    /// <summary><c>ForceStock</c>: the driver zeroes the offsets, so "never configured" is a VALUE to write, not
    /// an absence — and writing it is what stops a mode inheriting the previous mode's overclock.</summary>
    [Fact]
    public void GpuOcIsForcedToStock_AndTheServiceAgrees()
    {
        var settings = new Settings();
        settings.GpuOcPresets["quiet"] = new GpuOcPreset { Core = 100, Mem = 200 };
        var a = Setup(settings);

        Assert.Equal(EmptyAxisPolicy.ForceStock, ModeAxisTable.Policy(ModeAxis.GpuOc));

        a.F.Service.ApplyModeGpuOc();
        Assert.Equal(new[] { (0, 0) }, a.Gpu.SetCalls);
    }

    /// <summary><c>LeaveUntouched</c>: no OS power mode is forced onto a profile the user has not configured,
    /// which is why this axis reads the live overlay back instead of writing one.</summary>
    [Fact]
    public void CpuPowerIsLeftUntouched_AndTheServiceAgrees()
    {
        var settings = new Settings();
        settings.CpuPowerModes["quiet"] = "best-efficiency";
        var a = Setup(settings);

        Assert.Equal(EmptyAxisPolicy.LeaveUntouched, ModeAxisTable.Policy(ModeAxis.CpuPower));

        a.F.Service.ApplyModeCpuPower();
        Assert.Empty(a.Cpu.SetCalls);
    }

    /// <summary><c>ForceStock</c> on the Curve Optimizer — with the fourth case that a three-valued enum would
    /// have lost, checked immediately below it.</summary>
    [Fact]
    public void CoIsForcedToStock_AndTheServiceAgrees()
    {
        var settings = new Settings();
        settings.CoPresets["quiet"] = new CoPreset { AllCore = -25 };
        var a = Setup(settings);

        Assert.Equal(EmptyAxisPolicy.ForceStock, ModeAxisTable.Policy(ModeAxis.Co));

        a.F.Service.ApplyModeCo();
        Assert.Equal([0], a.Co.SetCalls);
    }

    /// <summary>The CO axis's SECOND answer, and the reason <see cref="ModeAxisTable.PolicyWhenNoModeWasEverConfigured"/>
    /// exists separately: an install where no mode ever had a preset does not write the SMU mailbox at all,
    /// while a mode that merely lacks one gets stock written actively. Collapsing the two would send a message
    /// nobody asked for on an opcode this class of CPU does not confirm.</summary>
    [Fact]
    public void CoIsLeftUntouchedWhenNothingWasEverConfigured_AndTheServiceAgrees()
    {
        var a = Setup(new Settings());

        Assert.Equal(EmptyAxisPolicy.ForceStock, ModeAxisTable.Policy(ModeAxis.Co));                          // this mode
        Assert.Equal(EmptyAxisPolicy.LeaveUntouched, ModeAxisTable.PolicyWhenNoModeWasEverConfigured(ModeAxis.Co));

        a.F.Service.ApplyModeCo();
        Assert.Empty(a.Co.SetCalls);
        Assert.Empty(a.Co.SetDomainsCalls);
    }

    // ---- volatility: the set the platform forgets, checked against what startup re-applies ----

    /// <summary>
    /// The strongest cross-check in this file: the table's volatile set must equal the set of axes a STARTUP
    /// actually drives, on a graph where every axis has a preset.
    ///
    /// It ties two things that were documented separately and could drift apart — <see cref="ModeAxisTable.IsVolatile"/>
    /// (which axes the platform forgets) and <c>ApplyStartupState</c> (which axes the boot path puts back). The
    /// fans must NOT appear: the EC latches the fan mode across a reboot, so there is nothing to restore. The
    /// lights must not appear either, for a different reason — they are re-applied by the UI layer
    /// (<see cref="ModeAxisTable.Owner"/>), not by the boot path.
    ///
    /// WHAT CHANGED IN WAVE 2. The boot path no longer owns an axis list: it calls the reconciler with
    /// <c>ReapplyTrigger.Startup</c>, whose schedule IS <see cref="ModeAxisTable.IsVolatile"/> applied to
    /// <see cref="ModeAxisTable.All"/>. The site can therefore no longer disagree with the table — which makes
    /// this test a check of the DOMAIN's internal agreement (schedule vs volatility) rather than of the site, and
    /// the call log below is the third party that can disagree with both. It is kept, and it is kept unweakened,
    /// because that is exactly the drift it would still catch.
    /// </summary>
    [Fact]
    public void StartupDrivesExactlyTheAxesTheTableCallsVolatile()
    {
        var a = Setup(FullyConfigured());

        a.F.Service.ApplyStartupState();

        // The Curve Optimizer is dispatched asynchronously, so wait for it before reading the logs.
        Assert.True(Eventually.Until(() => a.Co.SetCalls.Count > 0 || a.Co.SetDomainsCalls.Count > 0),
            "startup never reached the SMU");

        var driven = new HashSet<ModeAxis>();
        if (a.Fan.ModeCalls.Count > 0 || a.Fan.SpeedCalls.Count > 0) driven.Add(ModeAxis.Fans);
        if (a.Gpu.SetCalls.Count > 0) driven.Add(ModeAxis.GpuOc);
        if (a.Cpu.SetCalls.Count > 0) driven.Add(ModeAxis.CpuPower);
        if (a.Co.SetCalls.Count > 0 || a.Co.SetDomainsCalls.Count > 0) driven.Add(ModeAxis.Co);

        var volatileAxes = ModeAxisTable.All.Where(ModeAxisTable.IsVolatile).ToHashSet();

        Assert.Equal(volatileAxes, driven);
        Assert.DoesNotContain(ModeAxis.Lights, driven);   // re-applied by the UI, not at startup
        Assert.NotEmpty(driven);                          // ...and the assertion above is not vacuous
    }

    /// <summary>The two triggers whose schedule is the volatile set are the same SET as the volatility fact, and
    /// stated in <see cref="ModeAxisTable.All"/>'s order — a boot and a wake do not get to disagree about which
    /// axes the platform forgets. Internal agreement rather than an observation: the observation is the test
    /// above (a boot) and <c>HardwareReconcilerTests</c> (a wake), which read a call log.
    ///
    /// The schedule is <see cref="HardwareReconciler.Schedule"/> — Application, not the table — so this is a
    /// cross-layer agreement test by construction: the domain states which axes are volatile, the layer that
    /// drives them states the order, and the two are compared here rather than in either one's own file.</summary>
    [Fact]
    public void ABootAndAWakeBothScheduleTheVolatileAxesInAllOrder()
    {
        var volatileAxes = ModeAxisTable.All.Where(ModeAxisTable.IsVolatile).ToArray();

        Assert.Equal(volatileAxes, HardwareReconciler.Schedule(ReapplyTrigger.Startup));
        // A wake drives the same set and adds the lighting, which the firmware drops over suspend and the UI puts
        // back — first, because that repaint happens before the hardware re-assert rather than after it.
        Assert.Equal([ModeAxis.Lights, .. volatileAxes], HardwareReconciler.Schedule(ReapplyTrigger.Resume));
    }

    /// <summary>Which layer owns each axis's re-apply. Lighting is the one that is not a hardware concern.</summary>
    [Fact]
    public void LightingIsTheOnlyAxisTheUiReApplies()
    {
        Assert.Equal(ReassertOwner.Ui, ModeAxisTable.Owner(ModeAxis.Lights));
        Assert.All(ModeAxisTable.All.Where(x => x != ModeAxis.Lights),
            axis => Assert.Equal(ReassertOwner.Hardware, ModeAxisTable.Owner(axis)));
    }

    // ---- completeness: a new axis cannot be forgotten ----

    /// <summary>The promise the hand-written tuple in <see cref="PresetGraph"/> could not keep: every axis the
    /// table knows about is one the preset-graph assertions can see. Adding a sixth axis without a bag makes
    /// this red instead of silently dropping the new bag out of "a read must not mutate".</summary>
    [Fact]
    public void EveryAxisHasABagThePresetGraphCanSee()
    {
        var counted = Enum.GetValues<ModeAxis>()
            .Select(axis => PresetGraph.CountFor(new Settings(), axis))
            .ToArray();

        Assert.Equal(Enum.GetValues<ModeAxis>().Length, counted.Length);
        Assert.All(counted, c => Assert.Equal(0, c));                    // a fresh graph is empty on every axis
        Assert.Equal(ModeAxisTable.All.Count, counted.Length);           // the table and the enum agree
        Assert.Equal(ModeAxisTable.All.Distinct().Count(), counted.Length);
    }
}
