using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// THE TIMELINE REGRESSION: the single-core phase must provably load ONE core at a time, for EVERY probe where it
/// is enabled, over the SWEPT CLUSTER's cores — and it must run BEFORE the all-core phase. The owner reported the
/// guided sweep "полностью пропускает одноядерную проверку" — it skipped the single-core check and appeared to hop
/// through all cores then run them all at once. These tests drive the REAL service sweep through an affinity
/// adapter that records each pin's start/end interval, and assert the properties the phase's whole purpose rests
/// on:
///
///   1. the single-core intervals are pairwise DISJOINT (one core loaded at a time — a genuine high-boost corner);
///   2. a single-core phase runs for EVERY probe where it is enabled, INCLUDING one whose all-core phase was
///      inconclusive (a refused pin), which used to skip the phase outright;
///   3. its core set is the SWEPT CLUSTER's cores, not the whole machine;
///   4. the SINGLE-CORE phase comes FIRST and the all-core phase SECOND (the light phase is reported before the
///      heavy one), and a single-core real error / thermal abort / cancellation skips the all-core phase entirely
///      (fail fast, so the much longer burst is not paid for on a verdict the light phase already produced).
///
/// The concurrent all-core burst overlaps by construction; the single-core pins are the ones with no overlap.
/// The intervals are read off the fake adapter, so this is the shipped control flow (LaptopService.RunUndervoltSweep
/// → SweepTarget.Probe → CpuLoadRunner.Run) and not a claim.
/// </summary>
public class UndervoltSweepSingleCoreTimelineTests
{
    private sealed record Interval(LogicalCore Core, long StartTicks, long EndTicks);

    /// <summary>Records, per pin, the core and the wall-clock interval it was held. Thread-safe: the all-core
    /// phase pins several cores at once.</summary>
    private sealed class TimelineAffinity : ICoreAffinity
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, (LogicalCore Core, long Start)> _live = [];
        public List<Interval> Intervals { get; } = [];
        public CoreTopology TopologyResult { get; set; } = new([new LogicalCore(0, 0)], CoreTopologySource.PhysicalCores, null);
        public Func<LogicalCore, (bool ok, string? error)>? PinBehaviour { get; set; }

        public CoreTopology Topology() => TopologyResult;

        public bool PinCurrentThread(LogicalCore core, out string? error)
        {
            lock (_gate) _live[Environment.CurrentManagedThreadId] = (core, DateTime.UtcNow.Ticks);
            var (ok, reason) = PinBehaviour?.Invoke(core) ?? (true, null);
            error = reason;
            return ok;
        }

        public void UnpinCurrentThread()
        {
            lock (_gate)
                if (_live.Remove(Environment.CurrentManagedThreadId, out var e))
                    Intervals.Add(new Interval(e.Core, e.Start, DateTime.UtcNow.Ticks));
        }

        /// <summary>The pins that did NOT overlap any other pin: the one-at-a-time (single-core) phase. The
        /// all-core burst is excluded automatically because its pins all overlap each other.</summary>
        public IReadOnlyList<Interval> IsolatedPins()
        {
            var all = Intervals.ToList();
            return all.Where(i => !all.Any(j => !ReferenceEquals(i, j)
                && i.StartTicks < j.EndTicks && j.StartTicks < i.EndTicks)).ToList();
        }
    }

    private static CoreTopology WithClusters(params LogicalCore[] cores)
    {
        var ordered = CpuLoadPolicy.OrderCores(cores);
        var (performance, efficiency) = CpuLoadPolicy.PartitionIntoClusters(ordered);
        return new CoreTopology(ordered, CoreTopologySource.PhysicalCores, null, performance, efficiency);
    }

    /// <summary>Split the ordered progress stream into one list per PROBE. A probe starts at its marker report —
    /// Applying (a fresh search offset), BackingOff (a back-off offset) or Confirming with no core (the soak's
    /// first report) — and runs until the next marker. Within a probe, the single-core reports must all precede
    /// the all-core ones; this is the phase order the reversal introduced.</summary>
    private static List<List<SweepProgress>> Probes(IReadOnlyList<SweepProgress> progress)
    {
        var probes = new List<List<SweepProgress>>();
        foreach (var p in progress)
        {
            var isMarker = p.Core is null
                && p.Phase is SweepPhase.Applying or SweepPhase.BackingOff or SweepPhase.Confirming;
            if (isMarker || probes.Count == 0) probes.Add([]);
            probes[^1].Add(p);
        }
        return probes;
    }

    /// <summary>Assert, for EVERY probe, that the single-core phase is reported BEFORE the all-core/confirming
    /// phase, and that at least one of each ran. The progress stream is appended on the sweep's own thread, so
    /// position in the list is the timeline; a probe with no single-core reports (the phase disabled) is skipped.
    /// </summary>
    private static void AssertSingleCorePhaseComesFirst(IReadOnlyList<SweepProgress> progress)
    {
        var probes = Probes(progress).Where(pr => pr.Any(p => p.Phase == SweepPhase.SingleCore)).ToList();
        Assert.NotEmpty(probes);
        foreach (var probe in probes)
        {
            var lastSingle = probe.FindLastIndex(p => p.Phase == SweepPhase.SingleCore);
            var firstAllCore = probe.FindIndex(p => p.Core is not null && p.Phase is SweepPhase.Loading or SweepPhase.Confirming);
            Assert.True(firstAllCore < 0 || lastSingle < firstAllCore,
                "a single-core report was emitted after the all-core phase in the same probe");
        }
    }

    // The owner's exact Strix Point shape: 4 Zen 5 (performance) + 6 Zen 5c (efficiency) = 10 physical cores.
    private static readonly LogicalCore[] Strix10 =
    [
        new(0, 0, 1), new(2, 0, 1), new(4, 0, 1), new(6, 0, 1),
        new(8, 0, 0), new(10, 0, 0), new(12, 0, 0), new(14, 0, 0), new(16, 0, 0), new(18, 0, 0),
    ];

    private static LaptopServiceFixture Setup(TimelineAffinity affinity)
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Device.CurveOptimizer = new FakeCurveOptimizer { Range = (-1, 0) }
            .WithDomains(
                new VoltageDomain("Zen 5", "ccd:0") { Cluster = CpuClusterKind.Performance },
                new VoltageDomain("Zen 5c", "ccd:1") { Cluster = CpuClusterKind.Efficiency },
                new VoltageDomain("iGPU", "gfx", Range: (-50, 0), MillivoltsPerCount: 5.0));
        f.Device.CoreAffinity = affinity;
        f.Device.Sensors = new FakeSensors { Snapshot = new SensorSnapshot { CpuTempC = 40 } };
        f.SyncPowerSource.Run(new BatteryInfoSnapshot { State = BatteryState.Charging });
        return f;
    }

    // Shipped budgets scaled down to keep the suite fast; the CODE PATH is the shipped one. The single-core
    // budget is long enough that each pin's interval is clearly its own, distinct from the concurrent burst.
    private static SweepOptions Fast(int min) => new()
    {
        StartCounts = min,                 // one offset per domain (at the floor)
        Repetitions = 1,
        IncludeSingleCorePhase = true,
        ConfirmSoak = false,
        SingleCoreBudget = TimeSpan.FromMilliseconds(120),
        Load = new CpuLoadOptions
        {
            Width = LoadWidth.Scalar,
            PerCoreBudget = TimeSpan.FromMilliseconds(120),
            TotalBudget = TimeSpan.FromMinutes(5),
            ConcurrentCores = true,
        },
    };

    [Fact]
    public void EveryProbeRunsASingleCorePhaseWhoseIntervalsAreDisjointAndOnTheSweptCluster()
    {
        var affinity = new TimelineAffinity { TopologyResult = WithClusters(Strix10) };
        var f = Setup(affinity);
        var progress = new List<SweepProgress>();
        f.Service.RunUndervoltSweep(Fast(-1), p => { lock (progress) progress.Add(p); }, CancellationToken.None);

        // (1) DISJOINT: no two single-core (isolated) intervals overlap — truly one core at a time.
        var isolated = affinity.IsolatedPins();
        Assert.NotEmpty(isolated);
        for (var a = 0; a < isolated.Count; a++)
            for (var b = a + 1; b < isolated.Count; b++)
                Assert.False(isolated[a].StartTicks < isolated[b].EndTicks && isolated[b].StartTicks < isolated[a].EndTicks,
                    $"single-core pins on cores {isolated[a].Core.Processor} and {isolated[b].Core.Processor} overlapped");

        // (2) ONE PHASE PER PROBE, over the SWEPT CLUSTER's cores. Two domains × the 4-core performance cluster
        //     (ccd:0) and the 6-core efficiency cluster (ccd:1) = 10 single-core pins, and NEVER a concurrent
        //     cluster's cores mixed into one phase's interval set.
        var clusterProcs = new[] { 0, 2, 4, 6 }.Concat(new[] { 8, 10, 12, 14, 16, 18 }).ToHashSet();
        Assert.All(isolated, i => Assert.Contains(i.Core.Processor, clusterProcs));
        Assert.Equal(4 + 6, isolated.Count);                    // each domain walked its own cluster once, one at a time

        // (3) The single-core phase is REPORTED as its own phase with the cluster's core order ascending, so the
        //     one-at-a-time check is visible rather than hidden inside the all-core load.
        var single = progress.Where(p => p.Phase == SweepPhase.SingleCore).ToList();
        Assert.NotEmpty(single);
        foreach (var domain in single.GroupBy(p => p.DomainIndex))
        {
            var procs = domain.Where(p => p.Core is not null).Select(p => p.Core!.Value.Processor).ToList();
            Assert.Equal(procs.OrderBy(x => x), procs);         // strictly ascending: one at a time
            Assert.Equal(procs.Count, procs.Distinct().Count());
        }

        // (4) THE NEW ORDER: in every probe, the single-core reports precede the all-core ones, so the light
        //     phase is run (and reported) first.
        AssertSingleCorePhaseComesFirst(progress);
    }

    /// <summary>
    /// THE SOAK'S SINGLE-CORE HALF IS THE SAME TEST. With the final soak enabled, the long confirmation runs the
    /// all-core concurrent phase AND the one-core-at-a-time phase, over the settled offset. This pins that the
    /// soak's single-core pins are disjoint and on the swept cluster too (the phase is not a search-only
    /// decoration), and that the single-core phase reports the same SingleCore phase in the soak as in the search.
    /// </summary>
    [Fact]
    public void TheSoakAlsoRunsADisjointSingleCorePhaseOnTheSweptCluster()
    {
        var affinity = new TimelineAffinity { TopologyResult = WithClusters(Strix10) };
        var f = Setup(affinity);
        var options = Fast(-1) with
        {
            ConfirmSoak = true,
            ConfirmAllCore = TimeSpan.FromMilliseconds(120),
            ConfirmSingleCore = TimeSpan.FromMilliseconds(120),
        };
        var progress = new List<SweepProgress>();
        var result = f.Service.RunUndervoltSweep(options, p => { lock (progress) progress.Add(p); }, CancellationToken.None);

        // Two domains, each: one search probe's single-core walk (4 + 6) and one soak's single-core walk (4 + 6).
        var isolated = affinity.IsolatedPins();
        Assert.Equal(2 * (4 + 6), isolated.Count);
        Assert.All(isolated, i => Assert.Contains(i.Core.Processor,
            new[] { 0, 2, 4, 6, 8, 10, 12, 14, 16, 18 }));
        for (var a = 0; a < isolated.Count; a++)
            for (var b = a + 1; b < isolated.Count; b++)
                Assert.False(isolated[a].StartTicks < isolated[b].EndTicks && isolated[b].StartTicks < isolated[a].EndTicks,
                    "soak/search single-core intervals overlapped");

        // The single-core phase is reported in the soak too (the same SingleCore phase the search uses), and it
        // comes BEFORE the soak's all-core Confirming phase — the search and the soak both use the new order.
        Assert.Contains(progress, p => p.Phase == SweepPhase.SingleCore);
        AssertSingleCorePhaseComesFirst(progress);
        Assert.True(result.Domains.All(d => d.Stable));
    }

    /// <summary>
    /// THE SKIP BUG. An all-core phase that is merely INCONCLUSIVE — here the first pin of every core is refused, so
    /// the load runner records every core as Skipped and classifies the phase "not every physical core could be
    /// tested" — is not a verdict about the offset and must NOT suppress the other phase. WITH THE NEW ORDER the
    /// single-core phase runs FIRST, so an all-core phase can no longer skip it by construction; the mirror is now
    /// the property to pin: a merely INCONCLUSIVE SINGLE-CORE phase (every cluster pin refused) must NOT fail
    /// fast — only a real error, a thermal abort or a cancellation does — so the all-core phase still runs, and the
    /// probe still fails closed because an inconclusive result from either phase is never upgraded to a pass.
    ///
    /// MUTATION THAT REDDENS IT: treating an inconclusive single-core result as a fail-fast stop (the
    /// all-core `Loading` progress then disappears), or upgrading an inconclusive phase to a pass (the probe would
    /// be reported stable).
    /// </summary>
    [Fact]
    public void AnInconclusiveSingleCorePhaseStillRunsTheAllCorePhaseAndFailsClosed()
    {
        // Refuse the FIRST pin of each cluster core (the sequential single-core walk, now first) and allow the
        // SECOND (the concurrent all-core burst) — the mirror of the old scenario under the reversed order.
        var pins = new Dictionary<int, int>();
        var affinity = new TimelineAffinity
        {
            TopologyResult = WithClusters(Strix10),
            PinBehaviour = core =>
            {
                lock (pins)
                {
                    pins.TryGetValue(core.Processor, out var n);
                    pins[core.Processor] = ++n;
                    return n == 1 ? (false, "the platform refused the pin") : (true, null);
                }
            },
        };
        var f = Setup(affinity);
        var progress = new List<SweepProgress>();
        var result = f.Service.RunUndervoltSweep(Fast(-1), p => { lock (progress) progress.Add(p); }, CancellationToken.None);

        // The single-core phase ran FIRST over the swept cluster's cores...
        var single = progress.Where(p => p.Phase == SweepPhase.SingleCore && p.Core is not null).ToList();
        Assert.NotEmpty(single);
        Assert.All(single, p => Assert.Contains(p.Core!.Value.Processor, new[] { 0, 2, 4, 6 }));

        // ...was INCONCLUSIVE, and did NOT fail fast: the all-core phase still ran (reported Loading with cores).
        Assert.Contains(progress, p => p.Phase == SweepPhase.Loading && p.Core is not null);

        // The probe still fails closed: the refused single-core walk is not a stability verdict, so the sweep
        // does not propose a deeper offset off the back of it.
        Assert.False(result.Domains[0].Stable);
    }

    /// <summary>
    /// FAIL FAST ON A SINGLE-CORE ERROR: the single-core phase runs first, so when its pin FAULTS (the worker
    /// throws, which the runner records as <see cref="LoadOutcome.Faulted"/> — a real oracle error) the much longer
    /// all-core phase must NOT run. The error is the offset's verdict already; paying for the all-core burst after
    /// it would be pure waste. Asserted through the real service: with the pin throwing for the swept cluster, no
    /// <c>Loading</c> report and no all-core pin happens.
    /// </summary>
    [Fact]
    public void ASingleCoreErrorSkipsTheAllCorePhase()
    {
        var affinity = new TimelineAffinity
        {
            TopologyResult = WithClusters(Strix10),
            PinBehaviour = _ => throw new InvalidOperationException("the core faulted"),
        };
        var f = Setup(affinity);
        var progress = new List<SweepProgress>();
        var result = f.Service.RunUndervoltSweep(Fast(-1), p => { lock (progress) progress.Add(p); }, CancellationToken.None);

        // The single-core phase is reported, then the probe returns on its error — the all-core phase never runs.
        Assert.Contains(progress, p => p.Phase == SweepPhase.SingleCore);
        Assert.DoesNotContain(progress, p => p.Phase == SweepPhase.Loading);
        // The single-core walk pinned a core before it faulted; the probe is not stable.
        Assert.Contains(affinity.Intervals, i => i.Core.Processor == 0);
        Assert.False(result.Domains[0].Stable);
    }

    /// <summary>
    /// FAIL FAST ON A SINGLE-CORE THERMAL ABORT: the single-core phase runs first; a hot machine aborts it and the
    /// all-core phase must NOT run. The abort is a non-verdict global stop (it never becomes a stability verdict),
    /// but it is still a stop — so the probe returns then and there.
    /// </summary>
    [Fact]
    public void ASingleCoreThermalAbortSkipsTheAllCorePhase()
    {
        var affinity = new TimelineAffinity { TopologyResult = WithClusters(Strix10) };
        var f = Setup(affinity);
        f.Device.Sensors = new FakeSensors { Snapshot = new SensorSnapshot { CpuTempC = 100 } };   // at/over the limit
        var progress = new List<SweepProgress>();
        var result = f.Service.RunUndervoltSweep(Fast(-1), p => { lock (progress) progress.Add(p); }, CancellationToken.None);

        Assert.Contains(progress, p => p.Phase == SweepPhase.SingleCore);
        Assert.DoesNotContain(progress, p => p.Phase == SweepPhase.Loading);
        Assert.All(affinity.Intervals, i => Assert.Contains(i.Core.Processor, new[] { 0, 2, 4, 6 }));
        Assert.Equal(SweepStop.ThermalAbort, result.Stop);
        Assert.Null(result.Domains.Count > 0 ? result.Domains[0].FirstFailing : null);   // never a verdict
    }

    /// <summary>
    /// FAIL FAST ON A SINGLE-CORE CANCELLATION: the single-core phase runs first; cancelling during it makes it
    /// return <see cref="SweepProbeStatus.Cancelled"/>, and the all-core phase must NOT run. A cancellation is a
    /// global stop, not a verdict — so the run ends cancelled with no all-core load.
    /// </summary>
    [Fact]
    public void ASingleCoreCancellationSkipsTheAllCorePhase()
    {
        using var cts = new CancellationTokenSource();
        var cancelled = false;
        var affinity = new TimelineAffinity
        {
            TopologyResult = WithClusters(Strix10),
            PinBehaviour = _ =>
            {
                // Cancel on the very first single-core pin; the runner observes the shared token and stops.
                lock (cts)
                {
                    if (!cancelled) { cancelled = true; cts.Cancel(); }
                }
                return (true, null);
            },
        };
        var f = Setup(affinity);
        var progress = new List<SweepProgress>();
        var result = f.Service.RunUndervoltSweep(Fast(-1), p => { lock (progress) progress.Add(p); }, cts.Token);

        Assert.Contains(progress, p => p.Phase == SweepPhase.SingleCore);
        Assert.DoesNotContain(progress, p => p.Phase == SweepPhase.Loading);
        Assert.All(affinity.Intervals, i => Assert.Contains(i.Core.Processor, new[] { 0, 2, 4, 6 }));
        Assert.Equal(SweepStop.Cancelled, result.Stop);
    }

    /// <summary>
    /// A SINGLE-CORE ORACLE ERROR IS NOT MASKED BY AN ALL-CORE PASS. The all-core phase loads every physical core
    /// (concurrent), the single-core phase walks the swept cluster one at a time; a failure only the latter sees
    /// must reach the sweep. Driven at the sweep boundary the two phases are not independently injectable, so the
    /// pure combining rule is pinned directly here — an all-core Pass plus a single-core CoreError is a CoreError,
    /// in BOTH orders of the arguments the implementation could use.
    /// </summary>
    [Fact]
    public void ASingleCoreErrorIsNotMaskedByAnAllCorePass()
    {
        var error = SweepProbeResult.Error(new LogicalCore(0, 0), LoadOutcome.ChecksumMismatch, "single-core edge");
        var pass = SweepProbeResult.Pass();
        var thermal = SweepProbeResult.Thermal("hot");

        Assert.Equal(SweepProbeStatus.CoreError, SweepPolicy.CombineProbes(pass, error).Status);
        Assert.Equal(SweepProbeStatus.CoreError, SweepPolicy.CombineProbes(error, pass).Status);
        // A cancellation from either phase wins over a pass.
        Assert.Equal(SweepProbeStatus.Cancelled, SweepPolicy.CombineProbes(SweepProbeResult.Cancelled(), pass).Status);
        Assert.Equal(SweepProbeStatus.Cancelled, SweepPolicy.CombineProbes(pass, SweepProbeResult.Cancelled()).Status);
        // A thermal abort from either phase is a non-verdict, not downgraded by a pass.
        Assert.Equal(SweepProbeStatus.ThermalAbort, SweepPolicy.CombineProbes(pass, thermal).Status);
        Assert.Equal(SweepProbeStatus.ThermalAbort, SweepPolicy.CombineProbes(thermal, pass).Status);
        // Only two passes make a pass; an inconclusive single-core result stands (fail closed).
        Assert.Equal(SweepProbeStatus.Passed, SweepPolicy.CombineProbes(pass, pass).Status);
        Assert.Equal(SweepProbeStatus.Failed, SweepPolicy.CombineProbes(pass, SweepProbeResult.Failed("nope")).Status);
        // No single-core phase: the all-core verdict stands unchanged.
        Assert.Equal(SweepProbeStatus.Passed, SweepPolicy.CombineProbes(pass, null).Status);
        Assert.Equal(SweepProbeStatus.Failed, SweepPolicy.CombineProbes(SweepProbeResult.Failed("nope"), null).Status);
    }
}
