using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The re-apply SCHEDULE as an operation: <see cref="HardwareReconciler"/> drives one trigger's axes against
/// the ports, checked on the three things the sites used to decide by hand — WHICH axes are driven, in WHAT
/// ORDER, and on WHICH THREAD — plus the fourth, which exception escapes.
///
/// WHY THIS FILE EXISTS. <c>ModeAxisTable</c> stated the schedule and, until this wave, no production code read
/// it: the same operation was written out four times, and the order was the part nothing checked. A test that
/// asserted the domain's own list back at itself would be the tautology the table's own test file warns about,
/// so every observation here comes from the PORTS: a recording wrapper around each fake appends to one shared
/// log, and the log is compared against literals. The domain's schedule is then compared against the same
/// observation — two statements that must agree, with the call log as the third party that can disagree with
/// both.
///
/// WHAT IS NOT CHECKED HERE, and it is not a small part. That the SITES actually call the reconciler cannot be
/// observed by this suite for two of the three: <c>AppController.BackgroundPass</c> and
/// <c>LightingCoordinator.OnResume</c> need a desktop lifetime and a live refresh loop (the same limit
/// <c>ReconcileScheduleTests</c> and <c>CpuPrimeTests</c> record). Their wiring is therefore unproven — what is
/// proven is that the operation they now call behaves, so a site that kept its own list would be invisible here.
/// The startup site IS reachable, and the existing startup tests drive it end to end (<c>ReconcileScheduleTests</c>
/// and <c>ModeAxisTableTests.StartupDrivesExactlyTheAxesTheTableCallsVolatile</c>), which is as close to the
/// wiring as this suite gets.
///
/// The names say what HAPPENS. Nothing here asserts what a boot or a wake OUGHT to re-apply; the schedule is
/// the owner's, and this file records what it is today.
/// </summary>
public class HardwareReconcilerTests
{
    private sealed record Arranged(LaptopServiceFixture F, CallLog Log, FakeRgbDevice Rgb);

    /// <summary>Every axis port attached, with the light port present too — so "the reconciler did not drive the
    /// UI-owned axis" is an assertion about a port that exists and was not called, rather than about a port that
    /// was never there. The device reports "balanced" as current, which is the key the presets are filed under.
    /// Every port is wrapped so the call reaches the shared <see cref="CallLog"/> and then behaves normally.</summary>
    private static Arranged Setup(Settings settings)
    {
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);
        var log = new CallLog();
        f.Device.FanControl = new RecordingFan(new FakeFanControl(), log);
        f.Device.GpuOverclock = new RecordingGpu(new FakeGpuOverclock(), log);
        f.Device.CurveOptimizer = new RecordingCo(new FakeCurveOptimizer(), log);
        f.Device.CpuPower = new RecordingCpu(new FakeCpuPower { CurrentId = "best-efficiency" }, log);
        var rgb = new FakeRgbDevice();
        f.Device.Lighting = rgb;
        return new Arranged(f, log, rgb);
    }

    /// <summary>A settings graph with a preset for the CURRENT mode on every axis that can hold one, so no
    /// expectation below can be satisfied merely because an axis had nothing to apply. Each axis writes exactly
    /// ONCE through these presets, which is what lets the log be compared to the axis sequence directly.</summary>
    private static Settings FullyConfigured()
    {
        var s = new Settings();
        s.FanPresets["balanced"] = new FanPreset { Mode = (int)FanMode.Max, Cpu = 42, Gpu = 84 };
        s.GpuOcPresets["balanced"] = new GpuOcPreset { Core = -150, Mem = 800 };
        s.CpuPowerModes["balanced"] = "best-performance";
        s.CoPresets["balanced"] = new CoPreset { AllCore = -20 };
        return s;
    }

    // ---- what each trigger drives, in the order the ports saw it ----

    /// <summary>A boot re-applies the three axes the platform forgot, in the order <c>All</c> lists them, and
    /// leaves the fans alone: the EC latches the fan mode across a reboot, so there is nothing to put back.
    /// The fan preset is configured, so its absence from the log is a decision rather than an empty graph.</summary>
    [Fact]
    public void AStartupDrivesTheGpuOffsetsThenTheCpuOverlayThenTheCurveOptimizer()
    {
        var settings = FullyConfigured();
        var a = Setup(settings);

        a.F.Service.Reconciler.Reapply(ReapplyTrigger.Startup);

        a.Log.WaitFor(ModeAxis.Co);
        Assert.Equal([ModeAxis.GpuOc, ModeAxis.CpuPower, ModeAxis.Co], a.Log.Axes);
        Assert.NotNull(settings.FanPresets["balanced"]);   // there IS a fan preset for this mode, and it is not applied
    }

    /// <summary>A mode switch drives every axis, fans FIRST — <c>All</c>'s order is the mode-switch order, which
    /// is what its own docstring claims and what nothing checked until now. The lighting axis is in the trigger's
    /// schedule and is NOT in the log: it belongs to the UI layer (which re-applies it with its own burst), and
    /// the light port is attached, so an untouched port is a witness rather than an absence.</summary>
    [Fact]
    public void AModeSwitchDrivesTheFansThenTheGpuOffsetsThenTheCpuOverlayThenTheCurveOptimizer()
    {
        var a = Setup(FullyConfigured());

        a.F.Service.Reconciler.Reapply(ReapplyTrigger.ModeChange);

        a.Log.WaitFor(ModeAxis.Co);
        Assert.Equal([ModeAxis.Fans, ModeAxis.GpuOc, ModeAxis.CpuPower, ModeAxis.Co], a.Log.Axes);
        Assert.Empty(a.Rgb.FlashCalls);
        Assert.Equal(0, a.Rgb.BlankCalls);
    }

    /// <summary>A wake drives the same three axes a boot does — the dGPU comes back at zero offset, the SMU at
    /// stock — plus the lighting, which the firmware drops over suspend. The lighting is the coordinator's
    /// <c>Paint()</c> and happens BEFORE this call; the reconciler must not touch the light port itself.</summary>
    [Fact]
    public void AWakeDrivesTheGpuOffsetsThenTheCpuOverlayThenTheCurveOptimizer()
    {
        var a = Setup(FullyConfigured());

        a.F.Service.Reconciler.Reapply(ReapplyTrigger.Resume);

        a.Log.WaitFor(ModeAxis.Co);
        Assert.Equal([ModeAxis.GpuOc, ModeAxis.CpuPower, ModeAxis.Co], a.Log.Axes);
        Assert.Empty(a.Rgb.FlashCalls);
        Assert.Equal(0, a.Rgb.BlankCalls);
    }

    /// <summary>The schedule the reconciler executes is the domain's, and for a mode switch it is every axis:
    /// this is the cross-check between "what the ports saw" above and "what the table says". The two are separate
    /// facts — a mode switch could drive three axes with a five-axis schedule and nothing would notice.</summary>
    [Fact]
    public void EveryTriggerDrivesExactlyTheHardwareAxesItsScheduleNames()
    {
        foreach (var trigger in Enum.GetValues<ReapplyTrigger>())
        {
            var a = Setup(FullyConfigured());

            a.F.Service.Reconciler.Reapply(trigger);

            a.Log.WaitFor(ModeAxis.Co);
            var scheduled = ModeAxisTable.Schedule(trigger)
                .Where(axis => ModeAxisTable.Owner(axis) == ReassertOwner.Hardware)
                .ToArray();
            Assert.Equal(scheduled, a.Log.Axes);
        }
    }

    // ---- which thread each axis lands on ----

    /// <summary>The fast half of a boot runs on the CALLING thread: the GPU offsets are one NvAPI call and the
    /// CPU overlay is a powrprof transaction, and neither may be handed off — the startup site has no catch, so a
    /// port that throws there must still reach it (see the throw tests below). The Curve Optimizer is off it,
    /// because its SMU mailbox waits on a machine-wide PCI lock and can block for seconds.</summary>
    [Fact]
    public void AStartupDrivesTheFastAxesOnTheCallersThreadAndTheCurveOptimizerOffIt()
    {
        var a = Setup(FullyConfigured());
        var caller = Environment.CurrentManagedThreadId;

        a.F.Service.Reconciler.Reapply(ReapplyTrigger.Startup);

        a.Log.WaitFor(ModeAxis.Co);
        Assert.Equal(caller, a.Log.ThreadOf(ModeAxis.GpuOc));
        Assert.Equal(caller, a.Log.ThreadOf(ModeAxis.CpuPower));
        Assert.NotEqual(caller, a.Log.ThreadOf(ModeAxis.Co));
    }

    /// <summary>A mode switch fires on the refresh pass's own pool thread, and the fans, offsets and overlay are
    /// driven there — the pass is already off the UI thread, so handing them off again would buy nothing and would
    /// change what a throw does (the pass's catch covers the whole pass, not a fire-and-forget task).</summary>
    [Fact]
    public void AModeSwitchDrivesTheHardwareAxesOnThePassThreadAndTheCurveOptimizerOffIt()
    {
        var a = Setup(FullyConfigured());
        var caller = Environment.CurrentManagedThreadId;

        a.F.Service.Reconciler.Reapply(ReapplyTrigger.ModeChange);

        a.Log.WaitFor(ModeAxis.Co);
        Assert.Equal(caller, a.Log.ThreadOf(ModeAxis.Fans));
        Assert.Equal(caller, a.Log.ThreadOf(ModeAxis.GpuOc));
        Assert.Equal(caller, a.Log.ThreadOf(ModeAxis.CpuPower));
        Assert.NotEqual(caller, a.Log.ThreadOf(ModeAxis.Co));
    }

    /// <summary>A wake fires ON the UI thread, and the EC can stall right after it, so the WHOLE hardware
    /// schedule goes to one pool task — which is also what keeps the three in a single catch, the guarantee this
    /// site has. One task rather than three, so their relative order survives.</summary>
    [Fact]
    public void AWakeDrivesTheWholeHardwareScheduleOffTheUiThread()
    {
        var a = Setup(FullyConfigured());
        var caller = Environment.CurrentManagedThreadId;

        a.F.Service.Reconciler.Reapply(ReapplyTrigger.Resume);

        a.Log.WaitFor(ModeAxis.Co);
        Assert.NotEqual(caller, a.Log.ThreadOf(ModeAxis.GpuOc));
        Assert.NotEqual(caller, a.Log.ThreadOf(ModeAxis.CpuPower));
        Assert.NotEqual(caller, a.Log.ThreadOf(ModeAxis.Co));
        Assert.All(a.Log.Calls.Select(c => c.Thread), thread => Assert.Equal(a.Log.ThreadOf(ModeAxis.Co), thread));
    }

    // ---- what a throw does at each site ----

    /// <summary>The startup site's guarantee, pinned where it is observable: it has NO catch around its
    /// synchronous axes, so a throwing port arrives at its caller (the <c>AppController</c> constructor). That is
    /// the recorded gap — it is not fixed here, and this test is what would notice if the reconciler started
    /// swallowing it, e.g. by handing the fast axes to the pool.</summary>
    [Fact]
    public void AThrowFromABootAxisOnTheCallersThreadReachesTheCaller()
    {
        var a = Setup(FullyConfigured());
        var boom = new ThrowingGpu(new FakeGpuOverclock());
        a.F.Device.GpuOverclock = boom;

        Assert.Throws<InvalidOperationException>(() => a.F.Service.Reconciler.Reapply(ReapplyTrigger.Startup));
        Assert.True(boom.Reached);
    }

    /// <summary>...and the same site's other half: the Curve Optimizer is dispatched, so its failure cannot reach
    /// anyone — there is no unobserved-task-exception reader in the app either. The reached flag is what keeps
    /// this from passing vacuously against a deferred axis that was never tried at all.</summary>
    [Fact]
    public void AThrowFromTheDeferredAxisNeverReachesTheCaller()
    {
        var a = Setup(FullyConfigured());
        var boom = new ThrowingCo(new FakeCurveOptimizer());
        a.F.Device.CurveOptimizer = boom;

        var escaped = Record.Exception(() => a.F.Service.Reconciler.Reapply(ReapplyTrigger.Startup));

        Assert.Null(escaped);
        Assert.True(Eventually.Until(() => boom.Reached), "the deferred axis was never tried");
    }

    // ---- the UI reflect values ----

    /// <summary>The mode-switch site is the only one that reads the outcome — its UI pass reflects the presets —
    /// and the Curve Optimizer's row is filled on the calling thread from the STORED domains, because the axis's
    /// apply is the deferred one and has no value here. The other two triggers leave it null, and that is
    /// load-bearing rather than tidy: <c>CurrentCoDomains</c> takes the current mode key under <c>_state</c>,
    /// which is an EC transaction, so reading it for a caller that discards the answer would be a new hardware
    /// read on the boot path.</summary>
    [Fact]
    public void OnlyAModeSwitchFillsTheUiRowValues()
    {
        var a = Setup(FullyConfigured());

        var started = a.F.Service.Reconciler.Reapply(ReapplyTrigger.Startup);
        var switched = a.F.Service.Reconciler.Reapply(ReapplyTrigger.ModeChange);
        var woken = a.F.Service.Reconciler.Reapply(ReapplyTrigger.Resume);
        a.Log.WaitFor(ModeAxis.Co);

        Assert.Null(started.Co);
        Assert.Null(woken.Co);
        Assert.Null(woken.Fan);
        Assert.NotNull(switched.Co);
        Assert.Equal([-20], switched.Co);                       // the stored all-core value, as the row always showed
        Assert.Equal("best-performance", switched.CpuPower);
        Assert.Equal(-150, switched.GpuOc?.Core);
        Assert.Equal((int)FanMode.Max, switched.Fan!.Mode);
    }
}

/// <summary>One ordered log shared by every recording port below, so a call is attributed to an AXIS and the
/// sequence ACROSS ports survives — per-port lists cannot see the order, and the order is the subject. The thread
/// is captured at the moment of the call, which is the other thing a site decided by hand until this wave.</summary>
internal sealed class CallLog
{
    private readonly List<(ModeAxis Axis, int Thread)> _calls = [];

    public void Record(ModeAxis axis)
    {
        lock (_calls) _calls.Add((axis, Environment.CurrentManagedThreadId));
    }

    public IReadOnlyList<(ModeAxis Axis, int Thread)> Calls { get { lock (_calls) return [.. _calls]; } }

    public ModeAxis[] Axes => [.. Calls.Select(c => c.Axis)];

    /// <summary>The thread one axis landed on. Every trigger drives each axis exactly once with the graphs these
    /// tests arrange, so a single answer is the whole answer.</summary>
    public int ThreadOf(ModeAxis axis) => Calls.Single(c => c.Axis == axis).Thread;

    /// <summary>Wait for the deferred axis, so an assertion on the log is not a race. Returns whether it ever
    /// arrived; the callers assert on the log itself, whose failure then names what was missing.</summary>
    public void WaitFor(ModeAxis axis) =>
        Assert.True(Eventually.Until(() => Calls.Any(c => c.Axis == axis)), $"the {axis} axis was never driven");
}

/// <summary>A port wrapper that records the axis at the moment of the call and then delegates, so the fake's own
/// result and its normal effect are unchanged.</summary>
internal sealed class RecordingFan(IFanControl inner, CallLog log) : IFanControl
{
    public string? LastError => inner.LastError;
    public FanCapability Capability => inner.Capability;
    public bool SetMode(FanMode mode) { log.Record(ModeAxis.Fans); return inner.SetMode(mode); }
    public bool SetCustomSpeeds(byte cpu, byte gpu) { log.Record(ModeAxis.Fans); return inner.SetCustomSpeeds(cpu, gpu); }
}

internal sealed class RecordingGpu(IGpuOverclock inner, CallLog log) : IGpuOverclock
{
    public string? LastError => inner.LastError;
    public string Name => inner.Name;
    public (int Min, int Max) CoreRange => inner.CoreRange;
    public (int Min, int Max) MemRange => inner.MemRange;
    public bool Set(int coreMhz, int memMhz) { log.Record(ModeAxis.GpuOc); return inner.Set(coreMhz, memMhz); }
}

internal sealed class RecordingCpu(ICpuPower inner, CallLog log) : ICpuPower
{
    public string? LastError => inner.LastError;
    public IReadOnlyList<ChoiceOption> Modes => inner.Modes;
    public string? Current() => inner.Current();
    public bool Set(string id) { log.Record(ModeAxis.CpuPower); return inner.Set(id); }
}

internal sealed class RecordingCo(ICurveOptimizer inner, CallLog log) : ICurveOptimizer
{
    public string? LastError => inner.LastError;
    public string Name => inner.Name;
    public (int Min, int Max) Range => inner.Range;
    public double MillivoltsPerCount => inner.MillivoltsPerCount;
    public IReadOnlyList<VoltageDomain> Domains => inner.Domains;
    public bool Set(int counts) { log.Record(ModeAxis.Co); return inner.Set(counts); }
    public bool SetDomains(IReadOnlyList<int> counts) { log.Record(ModeAxis.Co); return inner.SetDomains(counts); }
}

/// <summary>A GPU port that fails the way a real one can. <see cref="Reached"/> is the witness: without it, "the
/// throw escaped" and "the throw never happened" are the same observation.</summary>
internal sealed class ThrowingGpu(IGpuOverclock inner) : IGpuOverclock
{
    public bool Reached { get; private set; }
    public string? LastError => inner.LastError;
    public string Name => inner.Name;
    public (int Min, int Max) CoreRange => inner.CoreRange;
    public (int Min, int Max) MemRange => inner.MemRange;
    public bool Set(int coreMhz, int memMhz) { Reached = true; throw new InvalidOperationException("no dGPU"); }
}

/// <summary>The same for the deferred axis: it records that it was reached and then throws, so the caller's
/// "nothing escaped" is an observation about a call that really happened.</summary>
internal sealed class ThrowingCo(ICurveOptimizer inner) : ICurveOptimizer
{
    private volatile bool _reached;
    public bool Reached => _reached;
    public string? LastError => inner.LastError;
    public string Name => inner.Name;
    public (int Min, int Max) Range => inner.Range;
    public double MillivoltsPerCount => inner.MillivoltsPerCount;
    public IReadOnlyList<VoltageDomain> Domains => inner.Domains;
    public bool Set(int counts) { _reached = true; throw new InvalidOperationException("no SMU mailbox"); }
    public bool SetDomains(IReadOnlyList<int> counts) { _reached = true; throw new InvalidOperationException("no SMU mailbox"); }
}
