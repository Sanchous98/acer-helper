using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The fan curve as the MODEL it now belongs to (<see cref="Fan"/>), driven DIRECTLY — one fan at a time, the
/// data that fan was given and a temperature in, the duty% it should be at out, plus the memory of what was
/// applied to it. Before the curve moved there was nothing to ask one fan about: the interpolation was a static
/// helper and the duty memory was two loose ints on the controller, so the only reachable question was the
/// pair's.
///
/// WHY DIRECT DRIVING IS THE POINT AND NOT A SHORTCUT (the shape <c>CoAxisTests.cs</c> argued for the Co axis):
/// the existing suite pins this path through the SERVICE, and every one of those assertions still passes if the
/// curve is written out at the site instead of asked of this type. The cross-checks at the end tie each answer
/// back to the pair the port actually received, so the model cannot quietly stop being the one the production
/// path uses.
///
/// WHICH FAN IS WHICH is no longer the model's business — <see cref="Fan"/> has no CPU/GPU flag and takes no
/// half-of-a-preset selector — so <c>FansOf</c> below restates the mapping the application performs
/// (LaptopService.Fans.FanSettingsOf) in one place, which is what lets these tests build a fan at all. That
/// restatement is not what pins production's mapping: the cross-checks at the end compare against the literal
/// pair the port received, so swapping the halves in the service reddens them rather than this file.
///
/// WHAT IS NOT CHECKED HERE. Whether a write happens at all: the deadband is decided for the PAIR of fans, and
/// its boundary and pairing are pinned as today's behaviour in <c>FanCurveEngineStepTests.cs</c> — this file
/// re-states the pairing once, because moving the memories into two objects is exactly the change that could
/// have lost it. Nor is the machines this repo cannot run: how many fans exist, what they are labelled, and a
/// one-fan backend discarding the GPU duty are Infrastructure's and are unreachable from here.
/// </summary>
public class FanTests
{
    // A curve whose duties are all distinct from the built-in default ramp, so an implementation that reads
    // the wrong half of the preset — or falls back to the default ramp — fails instead of coincidentally
    // passing. The GPU's ramps DOWN, so the two are never the same number by accident.
    private static readonly int[] Ramp    = [10, 20, 30, 40, 50];
    private static readonly int[] GpuRamp = [90, 80, 70, 60, 50];

    /// <summary>One stored preset split into the two fans' settings, the way the application layer does it
    /// (LaptopService.Fans.FanSettingsOf). The pairing of halves to fans is the CALLER's decision now; the
    /// model is handed one fan's own data and nothing about its position.</summary>
    private static (FanSettings cpu, FanSettings gpu) FansOf(FanPreset p)
        => (new FanSettings(p.CpuUseCurve, p.CpuCurve, p.Cpu),
            new FanSettings(p.GpuUseCurve, p.GpuCurve, p.Gpu));

    private static Fan CpuFan(FanPreset p) => new(FansOf(p).cpu);
    private static Fan GpuFan(FanPreset p) => new(FansOf(p).gpu);

    /// <summary>A fan built only to watch its memory: the rules below never ask it for a duty, so its data is
    /// arbitrary.</summary>
    private static Fan AFan() => new(new FanSettings(false, [], 0));

    // ---- rule: a fan answers from the data it was handed, curve or fixed speed ----

    /// <summary>The two fans of one preset are two different curves, and each is handed its own: this is the
    /// per-fan half of the curve that the frozen schema keeps in one object.</summary>
    [Fact]
    public void WithItsCurveOn_AFanFollowsItsOwnCurve_AndNotTheOtherFans()
    {
        var preset = new FanPreset
        {
            CpuUseCurve = true, CpuCurve = Ramp,
            GpuUseCurve = true, GpuCurve = GpuRamp,
        };

        Assert.Equal(20, CpuFan(preset).Duty(60));   // 10 + 10*10/10
        Assert.Equal(80, GpuFan(preset).Duty(60));   // 90 + (80-90)*10/10
        Assert.Equal(15, CpuFan(preset).Duty(55));
        Assert.Equal(75, GpuFan(preset).Duty(65));   // 80 + (70-80)*5/10 — the falling side of its curve
    }

    /// <summary>With its curve off a fan holds its own fixed speed, clamped into the duty range — even though
    /// its own curve is handed over alongside it, which is the half of this rule the pair could not state.</summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(70, 70)]
    [InlineData(101, 100)]
    [InlineData(255, 100)]
    public void WithItsCurveOff_AFanHoldsItsOwnFixedSpeed_ClampedIntoTheDutyRange(int fixedDuty, int expected)
    {
        var preset = new FanPreset { Cpu = fixedDuty, Gpu = 25, CpuCurve = Ramp, GpuCurve = GpuRamp };

        Assert.Equal(expected, CpuFan(preset).Duty(70));
        Assert.Equal(25, GpuFan(preset).Duty(70));
    }

    // ---- rule: the duty memory is the fan's, and an unreadable temperature holds THAT fan's last duty ----

    /// <summary>An unreadable temperature does not drop a fan to idle: it holds what this fan was last applied
    /// at. Each fan holds its own, and a fan with nothing committed uses its own curve's idle duty — reporting
    /// the idle duty for a fan that is running would spin it down on a single bad sample.</summary>
    [Fact]
    public void AnUnreadableTemperature_HoldsThisFansLastCommittedDuty()
    {
        var preset = new FanPreset
        {
            CpuUseCurve = true, CpuCurve = Ramp,
            GpuUseCurve = true, GpuCurve = GpuRamp,
        };
        var cpu = CpuFan(preset);
        var gpu = GpuFan(preset);
        cpu.Commit(88);

        Assert.Equal(88, cpu.Duty(-1));       // not the curve's first duty, 10
        Assert.Equal(88, cpu.Duty(-100));     // any negative reading is the same unknown
        Assert.Equal(90, gpu.Duty(-1));       // this fan has committed nothing: its own idle duty
    }

    /// <summary>The memory is per fan, and both operations touch one fan at a time — which is what a <see
    /// cref="FanCurveEngine.Commit"/> of a pair becomes inside the model.</summary>
    [Fact]
    public void TheMemoryIsPerFan_CommitAndResetTouchOneFanAtATime()
    {
        var cpu = AFan();
        var gpu = AFan();

        Assert.Equal(-1, cpu.LastApplied);
        Assert.Equal(-1, gpu.LastApplied);

        cpu.Commit(40);
        gpu.Commit(70);

        Assert.Equal(40, cpu.LastApplied);
        Assert.Equal(70, gpu.LastApplied);

        cpu.Reset();

        Assert.Equal(-1, cpu.LastApplied);
        Assert.Equal(70, gpu.LastApplied);   // the other fan's memory is untouched
    }

    /// <summary>The memory survives being handed fresh data, which is what every step of the sensor loop does:
    /// re-reading a fan's settings must not make the deadband forget what the hardware is actually at.
    /// Configure is internal, and this is the only place the rule is stated.</summary>
    [Fact]
    public void HandingAFanFreshData_KeepsItsMemory()
    {
        var fan = CpuFan(new FanPreset { CpuUseCurve = true, CpuCurve = Ramp });
        fan.Commit(42);

        fan.Configure(new FanSettings(true, [60, 60, 60, 60, 60], 0));

        Assert.Equal(42, fan.LastApplied);
        Assert.Equal(42, fan.Duty(-1));      // the fresh curve's first duty is 60; the committed duty still wins
    }

    // ---- rule: what is per fan is the memory; the WRITE DECISION stays the pair's ----

    /// <summary>The pairing the split must not lose. The deadband's boundary is pinned in
    /// <c>FanCurveEngineStepTests</c>; what is pinned here is that moving the two memories into two objects did
    /// not turn the decision into two: one fan's movement writes BOTH, and the pair — not each fan — is what
    /// the next Commit remembers. The port is why: <see cref="IFanControl.SetCustomSpeeds"/> takes a pair.</summary>
    [Fact]
    public void OneFansMovementWritesBothFans_AndThePairIsWhatIsCommitted()
    {
        // The CPU follows its curve and still says 15 at 55 °C, which is where it was last committed; the GPU
        // is on a fixed 46, six units above its committed 40.
        var preset = new FanPreset { CpuUseCurve = true, CpuCurve = Ramp, Gpu = 46 };
        var engine = new FanCurveEngine();
        engine.Commit(15, 40);

        Assert.Equal((15, 46), engine.Step(FansOf(preset), new SensorSnapshot { CpuTempC = 55, GpuTempC = 50 }));

        engine.Commit(15, 46);   // what the caller commits after the write succeeded

        Assert.Null(engine.Step(FansOf(preset), new SensorSnapshot { CpuTempC = 55, GpuTempC = 50 }));
    }

    // ---- cross-checks: the answer the model states is the one the port receives ----

    /// <summary>A mode whose stored preset drives the CPU from a curve and the GPU from its fixed speed — the
    /// shape that exercises both halves of <see cref="Fan.Duty"/> on one pass.</summary>
    private static (LaptopServiceFixture F, FakeFanControl Fan, FanPreset Stored) ServiceOnACurve(int gpuFixed)
    {
        var settings = new Settings();
        settings.FanPresets["balanced"] = new FanPreset
        {
            Mode = (int)FanMode.Custom,
            CpuUseCurve = true, CpuCurve = Ramp,
            Gpu = gpuFixed,
        };
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);
        var fan = new FakeFanControl();
        f.Device.FanControl = fan;
        return (f, fan, settings.FanPresets["balanced"]);
    }

    private static SensorSnapshot Hot { get; } = new() { CpuTempC = 55, GpuTempC = 50 };

    /// <summary>THE CROSS-CHECK the whole file rests on. The service's stored preset is handed to the model, and
    /// its two answers must be the pair the port actually received. Without this the model could be a parallel
    /// statement of the curve that production ignores — the failure mode this wave exists to avoid, and the one
    /// a direct-only test cannot see. It is also what pins the service's mapping of stored halves to fans: the
    /// literal pair below is the CPU's curve value against the GPU's fixed speed, so swapping the two halves
    /// in the service fails here.</summary>
    [Fact]
    public void TheDutiesTheModelStatesAreTheOnesThePortReceives()
    {
        var (f, fan, stored) = ServiceOnACurve(gpuFixed: 40);

        f.Service.ApplyCustom(Hot);

        Assert.Equal([FanMode.Custom], fan.ModeCalls);          // Custom behaviour first, speeds second
        Assert.Single(fan.SpeedCalls);
        Assert.Equal(((byte)15, (byte)40), fan.SpeedCalls[0]);  // the answer is not vacuous
        Assert.Equal(((byte)CpuFan(stored).Duty(55), (byte)GpuFan(stored).Duty(50)), fan.SpeedCalls[0]);
    }

    /// <summary>...and the same for the deadband, whose verdict is the one thing the model deliberately does
    /// not give: an engine primed with what the write committed must stay silent on the same step the service
    /// stayed silent on — otherwise the object could be driving different numbers than the service writes.</summary>
    [Fact]
    public void TheDeadbandsVerdictIsTheOneTheServiceActsOn()
    {
        var (f, fan, stored) = ServiceOnACurve(gpuFixed: 40);

        f.Service.ApplyCustom(Hot);
        Assert.Single(fan.SpeedCalls);

        f.Service.ApplyCustom(Hot);
        Assert.Single(fan.SpeedCalls);   // nothing moved, so nothing was written

        var engine = new FanCurveEngine();
        engine.Commit(15, 40);           // what the first pass left the fans at
        Assert.Null(engine.Step(FansOf(stored), Hot));
    }

    /// <summary>The other half of the same contract, through the service: the memory advances only on a write
    /// that succeeded, so a refused pair is offered again on the next pass instead of becoming the baseline the
    /// deadband then measures against.</summary>
    [Fact]
    public void ARefusedPairIsReOffered_AndTheDeadbandHoldsOnlyAfterASuccessfulWrite()
    {
        var (f, fan, _) = ServiceOnACurve(gpuFixed: 40);
        fan.SetCustomSpeedsResult = false;

        f.Service.ApplyCustom(Hot);
        f.Service.ApplyCustom(Hot);

        Assert.Equal(2, fan.SpeedCalls.Count);                    // offered again, not swallowed
        Assert.Equal(((byte)15, (byte)40), fan.SpeedCalls[1]);

        fan.SetCustomSpeedsResult = true;
        f.Service.ApplyCustom(Hot);
        f.Service.ApplyCustom(Hot);

        Assert.Equal(3, fan.SpeedCalls.Count);                    // committed once, then inside the deadband
    }
}
