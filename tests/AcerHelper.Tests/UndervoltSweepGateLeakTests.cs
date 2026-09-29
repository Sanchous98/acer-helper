using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// THE GATE MUST NOT LEAK WHEN A SWEEP FAILS BEFORE ITS MAIN BODY. This is the "auto-undervolt fails after the
/// first cycle" defect, reduced to the shape that caused it.
///
/// THE ROOT CAUSE. <c>LaptopService.RunUndervoltSweep</c> sets <c>_sweepActive = 1</c> first, then snapshots the
/// mode key and the base counts and forces a performance profile — and only THEN entered the try/finally that
/// cleared the flag. Any throw from those pre-body reads (a transient EC/WMI failure in <c>CurrentCoDomains</c>,
/// the profile port's <c>Selectable()</c>, or the forced switch) therefore escaped with the flag STILL SET. From
/// then on every subsequent sweep was refused with <see cref="LaptopService.SweepBusyReason"/> — a returned
/// result the card rendered as a generic failure — which is exactly "fails after the first cycle". The fix wraps
/// the whole flagged region in one try/finally, so the flag is released on every exit path.
///
/// THE STUB THROWS ON THE FORCE, which is the earliest pre-body read after the flag goes up, so this test fails
/// against the old shape (the flag stays set) and passes against the fix. The control assertion is that a SECOND
/// sweep, with the port healthy, is not refused for the gate.
/// </summary>
public class UndervoltSweepGateLeakTests
{
    private sealed class FakeAffinity : ICoreAffinity
    {
        private static readonly CoreTopology Topo =
            new([new LogicalCore(0, 0)], CoreTopologySource.PhysicalCores, null);
        public CoreTopology Topology() => Topo;
        public bool PinCurrentThread(LogicalCore core, out string? error) { error = null; return true; }
        public void UnpinCurrentThread() { }
    }

    /// <summary>A profiles decorator that throws from <see cref="Selectable"/> while <paramref name="shouldThrow"/>
    /// says so — the transient port failure the flag-leak bug turned into a permanent wedge.</summary>
    private sealed class FlakyProfiles(IPowerProfiles inner, Func<bool> shouldThrow) : IPowerProfiles
    {
        public string? LastError => inner.LastError;
        public IReadOnlyList<PerformanceProfile> All => inner.All;
        public IReadOnlyList<PerformanceProfile> Selectable()
        {
            if (shouldThrow()) throw new InvalidOperationException("the EC hiccuped reading profiles");
            return inner.Selectable();
        }
        public PerformanceProfile? Current() => inner.Current();
        public bool Set(PerformanceProfile profile) => inner.Set(profile);
    }

    private static SweepOptions Fast() => new()
    {
        Repetitions = 1,
        IncludeSingleCorePhase = false,
        ConfirmSoak = false,
        Load = new CpuLoadOptions
        {
            Width = LoadWidth.Scalar,
            PerCoreBudget = TimeSpan.FromMilliseconds(20),
            TotalBudget = TimeSpan.FromSeconds(5),
            ThermalLimitC = 95,
        },
    };

    private static LaptopServiceFixture Setup()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Device.CurveOptimizer = new FakeCurveOptimizer { Range = (-2, 0) }
            .WithDomains(new VoltageDomain("Zen 5", "ccd:0"), new VoltageDomain("iGPU", "gfx", Range: (-50, 0)));
        f.Device.CoreAffinity = new FakeAffinity();
        f.Device.Sensors = new FakeSensors { Snapshot = new SensorSnapshot { CpuTempC = 40 } };
        f.SyncPowerSource.Run(new BatteryInfoSnapshot { State = BatteryState.Charging });
        return f;
    }

    [Fact]
    public void AThrowBeforeTheBodyDoesNotLeaveTheGateClaimed_AndTheNextSweepRuns()
    {
        var f = Setup();
        var fail = true;
        f.Device.PowerProfiles = new FlakyProfiles(f.Pp!, () => fail);

        // First sweep: the forced-profile read throws. Against the leaky shape the flag stayed set.
        Assert.Throws<InvalidOperationException>(() => f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None));
        Assert.False(f.Service.TuningInProgress, "the sweep gate leaked: a throw left the SMU claimed forever");

        // Second sweep, port healthy: it must not be refused by a stale gate.
        fail = false;
        var second = f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None);
        Assert.NotEqual(LaptopService.SweepBusyReason, second.Detail);
    }
}
