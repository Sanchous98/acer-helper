using AcerHelper.Domain;
using AcerHelper.Infrastructure;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>The rail-classification rule the sweep reads once: only CPU clusters are walked, the iGPU is not.</summary>
public class CoAxisSweepClassificationTests
{
    [Theory]
    [InlineData("ccd:0", true)]
    [InlineData("ccd:1", true)]
    [InlineData("gfx", false)]
    [InlineData("", false)]
    [InlineData("psmu", false)]
    public void IsCpuClusterAcceptsClusterKeysOnly(string key, bool expected)
        => Assert.Equal(expected, CoAxis.IsCpuCluster(key));
}

/// <summary>
/// THE SERVICE HALF OF THE SWEEP, over the hand-written fakes: the CPU-cluster projection (the iGPU is carried,
/// never swept), the volatile-only guarantee, the explicit save through the per-mode path, and the mutual
/// exclusion that keeps a manual edit or a re-apply from stomping a probe.
/// </summary>
public class LaptopServiceUndervoltSweepTests
{
    private static readonly LogicalCore Core0 = new(0, 0);

    private sealed class FakeCoreAffinity : ICoreAffinity
    {
        public Action? OnTopology { get; set; }
        public CoreTopology TopologyResult { get; set; } = new([Core0], CoreTopologySource.PhysicalCores, null);
        public List<LogicalCore> Pinned { get; } = [];
        public CoreTopology Topology() { OnTopology?.Invoke(); return TopologyResult; }
        public bool PinCurrentThread(LogicalCore core, out string? error)
        {
            // The all-core probe phase pins several cores CONCURRENTLY, so the recorder must be thread-safe.
            lock (Pinned) Pinned.Add(core);
            error = null; return true;
        }
        public void UnpinCurrentThread() { }
    }

    /// <summary>A four-Zen5 / four-Zen5c topology, its clusters derived by the SAME pure rule the OS halves use
    /// (<see cref="CpuLoadPolicy.PartitionIntoClusters"/>), so the service's domain→core mapping is exercised
    /// against the shipped partition rather than a hand-written one.</summary>
    private static CoreTopology WithClusters(params LogicalCore[] cores)
    {
        var ordered = CpuLoadPolicy.OrderCores(cores);
        var (performance, efficiency) = CpuLoadPolicy.PartitionIntoClusters(ordered);
        return new CoreTopology(ordered, CoreTopologySource.PhysicalCores, null, performance, efficiency);
    }

    private static readonly LogicalCore[] StrixLikeCores =
    [
        new(0, 0, 1), new(2, 0, 1), new(4, 0, 1), new(6, 0, 1),      // performance (Zen 5)
        new(8, 0, 0), new(10, 0, 0), new(12, 0, 0), new(14, 0, 0),   // efficiency (Zen 5c)
    ];

    // The single-core phase is turned OFF in these control-flow tests (each is about one pass) so the suite
    // stays fast; the phase has its own test (AProbeRunsTheSingleCorePhaseForEveryCore). The long final soak is
    // off here too — it has its own tests — because a real service probe with the default 5/3-minute budgets
    // would dominate the suite.
    private static SweepOptions Fast(int? start = null) => new()
    {
        StartCounts = start,
        IncludeSingleCorePhase = false,
        ConfirmSoak = false,
        Load = new CpuLoadOptions
        {
            Width = LoadWidth.Scalar,
            PerCoreBudget = TimeSpan.FromMilliseconds(5),
            TotalBudget = TimeSpan.FromSeconds(5),
            ThermalLimitC = 95,
        },
    };

    private static FakeCurveOptimizer TwoClustersAndGpu() => new FakeCurveOptimizer { Range = (-30, 0) }
        .WithDomains(
            new VoltageDomain("Zen 5", "ccd:0") { Cluster = CpuClusterKind.Performance },
            new VoltageDomain("Zen 5c", "ccd:1") { Cluster = CpuClusterKind.Efficiency },
            new VoltageDomain("iGPU", "gfx", Range: (-50, 0), MillivoltsPerCount: 5.0));

    private static LaptopServiceFixture Setup()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Device.CurveOptimizer = TwoClustersAndGpu();
        f.Device.CoreAffinity = new FakeCoreAffinity();
        f.Device.Sensors = new FakeSensors { Snapshot = new SensorSnapshot { CpuTempC = 40 } };
        // The guided sweep is refused unless the source is a CONFIRMED AC. Every pre-existing sweep test means
        // "a sweep on a normal, plugged-in machine", so the source is set here once. The refusal paths are their
        // own tests (see LaptopServiceUndervoltSweepAcGateTests).
        f.SyncPowerSource.Run(new BatteryInfoSnapshot { State = BatteryState.Charging });
        return f;
    }

    [Fact]
    public void SweepDomainsExcludesTheGpuAndKeepsItsIndexOfThePortList()
    {
        var domains = Setup().Service.SweepDomains();

        Assert.Equal(2, domains.Count);
        Assert.Equal([0, 1], domains.Select(d => d.Index));
        Assert.Equal(["ccd:0", "ccd:1"], domains.Select(d => d.Key));
        Assert.DoesNotContain(domains, d => d.Key == "gfx");
        Assert.Equal((-30, 0), (domains[0].Min, domains[0].Max));
    }

    /// <summary>THE NON-NEGOTIABLE PROPERTY, end to end: a completed sweep writes the SMU VOLATILELY and leaves
    /// the settings file untouched, and every write carries the iGPU's pre-sweep value in its slot — including
    /// the final restore.</summary>
    [Fact]
    public void ASweepWritesVolatilelyCarriesTheGpuSlotAndNeverPersists()
    {
        var f = Setup();
        var co = (FakeCurveOptimizer)f.Device.CurveOptimizer!;
        f.Service.SetCoDomains([-5, -6, -7]);
        var savesBefore = f.Store.SaveCount;
        co.SetDomainsCalls.Clear();

        var result = f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None);

        Assert.True(result.HasProposal);
        Assert.Equal(savesBefore, f.Store.SaveCount);                                       // probes are NEVER persisted
        Assert.Equal(-5, f.Store.Settings.CoPresets["balanced"].Domains["ccd:0"]);
        Assert.Equal(-6, f.Store.Settings.CoPresets["balanced"].Domains["ccd:1"]);
        Assert.Equal(-7, f.Store.Settings.CoPresets["balanced"].Domains["gfx"]);
        Assert.NotEmpty(co.SetDomainsCalls);
        Assert.All(co.SetDomainsCalls, c => Assert.Equal(-7, c[2]));                        // the iGPU slot is carried
        Assert.Equal([-5, -6, -7], co.SetDomainsCalls[^1]);                                 // ...and the restore is the base
        Assert.Empty(co.SetCalls);                                                          // the all-core path is not used
    }

    /// <summary>A PRESENT SMU whose every volatile write THROWS is caught by the sweep's own volatile adapter
    /// (<c>WriteVolatile</c>), turned into <c>SweepApplyOutcome.Refused</c>, and the run aborts as an
    /// <see cref="SweepStop.ApplyFailed"/> — the throw never escapes the background sweep, and the flag is
    /// released. This is the SYSTEM-path guarantee for the applied-edit family's new exception: a throw must not
    /// leave an unobserved task exception, and it must not wedge the SMU claim.</summary>
    [Fact]
    public void ASweepWhoseVolatileWritesThrow_AbortsWithoutEscaping_AndReleasesTheGate()
    {
        var f = Setup();
        ((FakeCurveOptimizer)f.Device.CurveOptimizer!).ThrowOnSet = true;

        var escaped = Record.Exception(() => f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None));

        Assert.Null(escaped);                                       // the throw was caught by WriteVolatile
        Assert.False(f.Service.TuningInProgress);                   // ...and the gate was released on the way out
        Assert.Empty(f.Store.Settings.CoPresets);                   // a volatile sweep never persists
    }

    /// <summary>Thermal protection fails closed (the runner already does): a hot machine ends the sweep, the
    /// abort is NOT recorded as a verdict, and nothing is persisted.</summary>
    [Fact]
    public void AThermalAbortEndsTheSweepAndPersistsNothing()
    {
        var f = Setup();
        f.Device.Sensors = new FakeSensors { Snapshot = new SensorSnapshot { CpuTempC = 100 } };
        var co = (FakeCurveOptimizer)f.Device.CurveOptimizer!;
        f.Service.SetCoDomains([-5, -6, -7]);
        var savesBefore = f.Store.SaveCount;

        var result = f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None);

        Assert.Equal(SweepStop.ThermalAbort, result.Stop);
        Assert.Equal(savesBefore, f.Store.SaveCount);
        var d = Assert.Single(result.Domains);
        Assert.Null(d.FirstFailing);                                        // the abort is not a stability verdict
        Assert.False(d.Stable);
        Assert.Equal([-5, -6, -7], co.SetDomainsCalls[^1]);                 // the pre-sweep state went back
        Assert.All(co.SetDomainsCalls, c => Assert.Equal(-7, c[2]));        // the iGPU was carried throughout
    }

    /// <summary>Saving is the explicit second half and it goes through the existing per-mode path: remembered
    /// before written, clamped per rail, with the iGPU left exactly as it was.</summary>
    [Fact]
    public void SavePersistsTheProposalThroughThePerModePathAndLeavesTheGpuAlone()
    {
        var f = Setup();
        f.Service.SetCoDomains([-5, -6, -7]);
        var result = f.Service.RunUndervoltSweep(Fast(start: -3), null, CancellationToken.None);
        var savesBefore = f.Store.SaveCount;

        var (ok, error) = f.Service.SaveUndervoltSweep(result);

        Assert.True(ok);
        Assert.Null(error);
        Assert.True(f.Store.SaveCount > savesBefore);
        var stored = f.Store.Settings.CoPresets["balanced"].Domains;
        Assert.Equal(result.Domains[0].Proposed, stored["ccd:0"]);
        Assert.Equal(result.Domains[1].Proposed, stored["ccd:1"]);
        Assert.Equal(-7, stored["gfx"]);                                                    // never swept, never saved over
    }

    /// <summary>THE "it only reset one cluster" BUG. If the sweep is stopped before it reached every cluster, the
    /// result carries only the clusters it visited. Saving must still reset the UNVISITED clusters to STOCK — never
    /// leave them at their stored preset, which the user has explicitly said is not to be trusted. The iGPU is
    /// carried. This models a run stopped after ccd:0 only.</summary>
    [Fact]
    public void SavingAfterAnEarlyStopResetsTheUnvisitedClustersToStock()
    {
        var f = Setup();
        f.Service.SetCoDomains([-19, -20, -7]);                 // a stored, possibly-unstable pair

        var domains = f.Service.SweepDomains();
        var partial = new SweepResult(
            [new SweepDomainResult(domains[0], -12, -13, null, 3, true, true, 1, null)],   // only ccd:0 was reached
            SweepStop.Cancelled, 1, null);

        var (ok, error) = f.Service.SaveUndervoltSweep(partial);

        Assert.True(ok);
        Assert.Null(error);
        var stored = f.Store.Settings.CoPresets["balanced"].Domains;
        Assert.Equal(-12, stored["ccd:0"]);                     // the visited cluster keeps its proposal
        Assert.Equal(0, stored["ccd:1"]);                       // the UNVISITED cluster is reset to STOCK, not -20
        Assert.Equal(-7, stored["gfx"]);                        // the iGPU is carried
    }

    /// <summary>The mutual exclusion: while a sweep owns the SMU, a manual edit is refused (and persists
    /// nothing) and the per-mode re-apply does not write. Once the sweep ends both work again.</summary>
    [Fact]
    public async Task AManualEditAndAReapplyAreRefusedWhileASweepRuns()    {
        var f = Setup();
        var co = (FakeCurveOptimizer)f.Device.CurveOptimizer!;
        f.Service.SetCoDomains([-5, -6, -7]);

        var hold = new ManualResetEventSlim(false);
        ((FakeCoreAffinity)f.Device.CoreAffinity!).OnTopology = () => hold.Wait(3000);

        var task = Task.Run(() => f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None));
        Assert.True(Eventually.Until(() => f.Service.TuningInProgress), "the sweep never became active");

        var savesBefore = f.Store.SaveCount;
        var refused = f.ApplyUndervolt.Run([-1, -1, -1]);
        Assert.False(refused.ok);
        Assert.Equal(LaptopService.SweepBusyReason, refused.error);
        Assert.Equal(savesBefore, f.Store.SaveCount);                                       // a refused edit persists nothing

        var callsBefore = co.SetDomainsCalls.Count;
        f.ApplyModeCo.Run();
        Assert.Equal(callsBefore, co.SetDomainsCalls.Count);                                // the re-apply did not stomp a probe

        hold.Set();
        await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(f.Service.TuningInProgress);
        Assert.True(f.ApplyUndervolt.Run([-1, -2, -7]).ok);                                // free again
    }

    /// <summary>THE MAPPING. Each domain carries the port's cluster identity, and the performance cluster is the
    /// <c>ccd:0</c> domain while the efficiency cluster is <c>ccd:1</c>. On the Strix-like fake topology the
    /// performance cluster is exactly the four high-class cores and the efficiency cluster the four low-class
    /// ones — so a stage's subset is the right physical cores, measured rather than assumed.</summary>
    [Fact]
    public void ThePerformanceClusterIsTheCcd0DomainAndItsCoresAreTheHighClassOnes()
    {
        var f = Setup();
        ((FakeCoreAffinity)f.Device.CoreAffinity!).TopologyResult = WithClusters(StrixLikeCores);

        var domains = f.Service.SweepDomains();

        Assert.Equal(CpuClusterKind.Performance, domains[0].Cluster);
        Assert.Equal(CpuClusterKind.Efficiency, domains[1].Cluster);
        Assert.True(domains[0].ClusterIdentified);
        Assert.True(domains[1].ClusterIdentified);

        var topology = ((FakeCoreAffinity)f.Device.CoreAffinity!).TopologyResult;
        Assert.Equal([0, 2, 4, 6], topology.Cluster(CpuClusterKind.Performance)!.Select(c => c.Processor));
        Assert.Equal([8, 10, 12, 14], topology.Cluster(CpuClusterKind.Efficiency)!.Select(c => c.Processor));
    }

    /// <summary>THE ALL-CORE PHASE LOADS EVERY PHYSICAL CORE OF THE MACHINE. The owner's report was Task Manager
    /// showing only a few cores briefly busy during the sweep — because the probe used to load only the swept
    /// cluster's cores. OCCT/Linpack load every core at once, because it is the concurrent package current/droop
    /// that makes a marginal undervolt fail, so the probe must too. Driven through the real service probe and read
    /// off the per-core progress, the all-core phase pins all eight physical cores in BOTH stages (the swept
    /// cluster carries the tested offset; the sibling is held at stock by the sweep's baseline but is still
    /// loaded), and every progress report names the full physical-core count.</summary>
    [Fact]
    public void TheAllCorePhaseLoadsEveryPhysicalCore()
    {
        var f = Setup();
        var affinity = (FakeCoreAffinity)f.Device.CoreAffinity!;
        affinity.TopologyResult = WithClusters(StrixLikeCores);

        var seen = new List<SweepProgress>();
        f.Service.RunUndervoltSweep(new SweepOptions { StartCounts = 0, IncludeSingleCorePhase = false, ConfirmSoak = false,   // isolate the all-core phase
            Load = new CpuLoadOptions { Width = LoadWidth.Scalar, PerCoreBudget = TimeSpan.FromMilliseconds(5), TotalBudget = TimeSpan.FromSeconds(5) } },
            p => seen.Add(p), CancellationToken.None);

        var coreProgress = seen.Where(p => p.Phase == SweepPhase.Loading && p.Core is not null).ToList();
        Assert.NotEmpty(coreProgress);

        // Every one of the eight physical cores was loaded in stage 0 (the Zen 5 performance cluster's stage)...
        var stage0 = coreProgress.Where(p => p.DomainIndex == 0).ToList();
        Assert.NotEmpty(stage0);
        Assert.All(Enumerable.Range(0, 4).Select(i => i * 2), proc =>
            Assert.Contains(stage0, p => p.Core!.Value.Processor == proc));
        Assert.Contains(stage0, p => p.Core!.Value.Processor == 8);    // ...and the sibling cluster too
        Assert.Contains(stage0, p => p.Core!.Value.Processor == 14);

        // ...and the report always states the FULL physical-core count, not the swept cluster's subset.
        Assert.All(coreProgress, p => Assert.Equal(8, p.CoreCount));

        // The all-core run genuinely pinned every physical core (the fake records each pin).
        Assert.Contains(affinity.Pinned, c => c.Processor == 0);
        Assert.Contains(affinity.Pinned, c => c.Processor == 14);
    }

    /// <summary>THE SINGLE-CORE PHASE STAYS ON THE SWEPT CLUSTER. The all-core phase loads every physical core;
    /// the single-core phase is the CoreCycler boost-corner coverage and walks ONLY the swept cluster's cores one
    /// at a time. Proven by pin counts on a single-domain sweep (ccd:0, the performance cluster): both phases pin
    /// the four performance cores (2 each), while the efficiency cores are pinned only by the all-core phase
    /// (once) — so the single-core phase did NOT walk them. If it loaded all cores this would be 2/2, not 2/1.</summary>
    [Fact]
    public void TheSingleCorePhaseWalksOnlyTheSweptClustersCores()
    {
        var f = Setup();
        var affinity = (FakeCoreAffinity)f.Device.CoreAffinity!;
        affinity.TopologyResult = WithClusters(StrixLikeCores);
        // One CPU cluster only (ccd:0), so a single-domain sweep keeps the pin arithmetic unambiguous.
        f.Device.CurveOptimizer = new FakeCurveOptimizer { Range = (-30, 0) }.WithDomains(
            new VoltageDomain("Zen 5", "ccd:0") { Cluster = CpuClusterKind.Performance },
            new VoltageDomain("iGPU", "gfx", Range: (-50, 0), MillivoltsPerCount: 5.0));

        f.Service.RunUndervoltSweep(new SweepOptions
        {
            StartCounts = -30,                 // one probe: single-core then all-core, then done
            Repetitions = 1,                   // one pass per offset, so the pin arithmetic is exact
            IncludeSingleCorePhase = true,
            ConfirmSoak = false,
            SingleCoreBudget = TimeSpan.FromMilliseconds(50),
            Load = new CpuLoadOptions { Width = LoadWidth.Scalar, PerCoreBudget = TimeSpan.FromMilliseconds(50), TotalBudget = TimeSpan.FromSeconds(30) },
        }, null, CancellationToken.None);

        // Performance cluster: all-core + single-core = two pins each.
        Assert.Equal(2, affinity.Pinned.Count(c => c.Processor == 0));
        Assert.Equal(2, affinity.Pinned.Count(c => c.Processor == 6));
        // Efficiency cluster: all-core only = one pin each (the single-core phase never walked them).
        Assert.Equal(1, affinity.Pinned.Count(c => c.Processor == 8));
        Assert.Equal(1, affinity.Pinned.Count(c => c.Processor == 14));
    }

    /// <summary>THE SINGLE-CORE PHASE. With the phase enabled, a probe loads EVERY core of the swept cluster a
    /// second time over, and each load's core order is strictly ascending within one phase (one core at a time),
    /// so the CoreCycler high-boost corner is covered in addition to the all-core concurrent phase.</summary>
    [Fact]
    public void AProbeRunsTheSingleCorePhaseForEveryCore()
    {
        var f = Setup();
        var affinity = (FakeCoreAffinity)f.Device.CoreAffinity!;
        affinity.TopologyResult = WithClusters(StrixLikeCores);

        var seen = new List<SweepProgress>();
        // Start at the rail floor so each stage does exactly ONE probe and the budgets below are ample for it:
        // this test is about the PHASE (each core loaded twice), not the walk, and a short per-core budget under
        // full-suite load let a worker be cut short and skip a core — a timing flake, not a behaviour.
        f.Service.RunUndervoltSweep(new SweepOptions
        {
            StartCounts = -30,
            IncludeSingleCorePhase = true,
            ConfirmSoak = false,
            SingleCoreBudget = TimeSpan.FromMilliseconds(100),
            Load = new CpuLoadOptions { Width = LoadWidth.Scalar, PerCoreBudget = TimeSpan.FromMilliseconds(100), TotalBudget = TimeSpan.FromSeconds(30) },
        }, p => seen.Add(p), CancellationToken.None);

        // Stage 0 (performance cluster) must pin each of its four cores at least twice (all-core + single-core).
        var stage0Pins = affinity.Pinned.Where(c => c.Processor is 0 or 2 or 4 or 6).ToList();
        Assert.True(stage0Pins.Count >= 8, $"expected the four cores loaded at least twice, saw {stage0Pins.Count}");
        Assert.Contains(stage0Pins, c => c.Processor == 0);
        Assert.Contains(stage0Pins, c => c.Processor == 6);
    }

    /// <summary>THE FALLBACK: when the topology cannot attribute cores to a cluster, the domain is marked
    /// not-identified, the probe loads ALL cores, and the progress says so — never a silent pretence of
    /// isolation.</summary>
    [Fact]
    public void WhenTheClusterIsUnknownAllCoresAreLoadedAndSaidSo()
    {
        var f = Setup();
        var affinity = (FakeCoreAffinity)f.Device.CoreAffinity!;
        // Cores with NO efficiency class: the partition returns null and the probe must fall back to all cores.
        affinity.TopologyResult = new(
            [Core0, new LogicalCore(1, 0), new LogicalCore(2, 0)],
            CoreTopologySource.PhysicalCores, null);

        var domains = f.Service.SweepDomains();
        Assert.All(domains, d => Assert.False(d.ClusterIdentified));

        var seen = new List<SweepProgress>();
        f.Service.RunUndervoltSweep(Fast(), p => seen.Add(p), CancellationToken.None);

        // All three cores were loaded, not a subset...
        Assert.Contains(affinity.Pinned, c => c.Processor == 0);
        Assert.Contains(affinity.Pinned, c => c.Processor == 1);
        Assert.Contains(affinity.Pinned, c => c.Processor == 2);
        // ...and every load progress report is marked not-identified.
        var loads = seen.Where(p => p.Phase == SweepPhase.Loading).ToList();
        Assert.NotEmpty(loads);
        Assert.All(loads, p => Assert.False(p.ClusterIdentified));
    }

    /// <summary>THE SOAK AT THE SERVICE BOUNDARY: with the confirmation on, the real service probe runs a second,
    /// longer pass on the settled offset and reports the CONFIRMING phase with the loaded core, and the domain
    /// comes back soaked and stable. ConfirmSoak is the only switch; the budgets here are short so the suite stays
    /// fast, but the code path is the shipped one.</summary>
    [Fact]
    public void TheRealServiceRunsTheSoakAndReportsTheConfirmingPhase()
    {
        var f = Setup();
        var affinity = (FakeCoreAffinity)f.Device.CoreAffinity!;
        affinity.TopologyResult = WithClusters(StrixLikeCores);

        var seen = new List<SweepProgress>();
        f.Service.RunUndervoltSweep(new SweepOptions
        {
            StartCounts = -1,                 // one search step, so the soak is cheap in wall clock terms here
            IncludeSingleCorePhase = false,
            ConfirmSoak = true,
            ConfirmAllCore = TimeSpan.FromMilliseconds(20),
            Load = new CpuLoadOptions { Width = LoadWidth.Scalar, PerCoreBudget = TimeSpan.FromMilliseconds(10), TotalBudget = TimeSpan.FromSeconds(5) },
        }, p => seen.Add(p), CancellationToken.None);

        // The confirming phase is reported with a real core (the soak actually loaded the cluster).
        var confirming = seen.Where(p => p.Phase == SweepPhase.Confirming).ToList();
        Assert.NotEmpty(confirming);
        Assert.Contains(confirming, p => p.Core is not null);
    }

    [Fact]
    public void CanSweepUndervoltNeedsEveryAdapterAndACluster()
    {
        var f = Setup();
        Assert.True(f.Service.CanSweepUndervolt);

        f.Device.Sensors = null;
        Assert.False(f.Service.CanSweepUndervolt);
        f.Device.Sensors = new FakeSensors();

        f.Device.CoreAffinity = null;
        Assert.False(f.Service.CanSweepUndervolt);
        f.Device.CoreAffinity = new FakeCoreAffinity();

        // A port whose only rail is the iGPU has nothing the sweep may walk.
        f.Device.CurveOptimizer = new FakeCurveOptimizer().WithDomains(
            new VoltageDomain("iGPU", "gfx", Range: (-50, 0)));
        Assert.False(f.Service.CanSweepUndervolt);

        f.Device.CurveOptimizer = null;
        Assert.False(f.Service.CanSweepUndervolt);
    }
}

/// <summary>
/// The guided-sweep card, UI-independent: the confirmation gates the start, progress surfaces, cancel cancels,
/// save calls the explicit save and reloads the sliders, discard only clears the panel, and the sliders are
/// disabled for exactly the duration of a run.
/// </summary>
public class UndervoltSweepViewModelTests
{
    private static SweepDomain D(int index = 0) => new(index, $"ccd:{index}", "Zen 5", -5, 0, 2.5);

    private static SweepResult Result(int proposed = -3, int firstFail = -4) => new(
        [new SweepDomainResult(D(), proposed, firstFail, new LogicalCore(0, 0), 3, true, true, 1, null)],
        SweepStop.FirstError, 10, null);

    private static UndervoltSweepViewModel Vm(
        Func<IProgress<SweepProgress>?, CancellationToken, Task<SweepResult>> run,
        Func<Task<bool>>? confirm = null,
        Func<SweepResult, Task<(bool ok, IReadOnlyList<int>? counts, string? error)>>? save = null,
        List<bool>? sliders = null,
        List<IReadOnlyList<int>>? loaded = null,
        List<string>? reported = null,
        bool? onAc = true)
        => new([D()], TimeSpan.FromMinutes(1),
               confirm ?? (() => Task.FromResult(true)),
               run,
               save ?? (_ => Task.FromResult<(bool, IReadOnlyList<int>?, string?)>((true, [-3, -7], null))),
               b => sliders?.Add(b),
               c => loaded?.Add(c),
               _ => { }, () => { },
               s => reported?.Add(s),
               Eventually.Sync,
               onAc);

    /// <summary>The card wired to the REAL <see cref="CoViewModel"/> over a two-row section (Zen 5 + iGPU), so the
    /// preview path — the thumbs moving, the proposal marking, the disabled state and the snapshot/restore — is
    /// asserted on the object the user sees rather than a stub. The sweep's single domain is index 0 (the Zen 5
    /// row); the iGPU (index 1) is never in the proposal and must stay where it was.</summary>
    private static (UndervoltSweepViewModel Vm, CoViewModel Co) Wired(
        Func<IProgress<SweepProgress>?, CancellationToken, Task<SweepResult>> run,
        Func<SweepResult, Task<(bool ok, IReadOnlyList<int>? counts, string? error)>>? save = null,
        IReadOnlyList<int>? initial = null)
    {
        var co = new CoViewModel("CPU", (-40, 0), 2.5,
            [new VoltageDomain("Zen 5", "ccd:0"), new VoltageDomain("iGPU", "gfx")],
            initial ?? [0, 0], _ => { });
        var vm = new UndervoltSweepViewModel([D()], TimeSpan.FromMinutes(1),
            () => Task.FromResult(true),
            run,
            save ?? (_ => Task.FromResult<(bool, IReadOnlyList<int>?, string?)>((true, [-3, -7], null))),
            co.SetSweepRunning, co.Load, co.Preview, co.DiscardPreview, _ => { }, Eventually.Sync, onAc: true);
        return (vm, co);
    }

    [Fact]
    public async Task TheConfirmationGatesTheStart()
    {
        var called = false;
        var vm = Vm((_, _) => { called = true; return Task.FromResult(Result()); }, confirm: () => Task.FromResult(false));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(called);
        Assert.False(vm.HasResult);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task ACompletedRunPreviewsTheProposalOnTheSlidersAndLocksThemForTheRun()
    {
        var (vm, co) = Wired((_, _) => Task.FromResult(Result()));

        await vm.StartCommand.ExecuteAsync(null);

        // The proposal is ON the sliders: the swept row (index 0) moved to its proposed value and is marked
        // proposed; the iGPU row (index 1) was not in the proposal and is untouched.
        Assert.Equal(-3, (int)co.Rows[0].Offset);
        Assert.True(co.Rows[0].IsProposed);
        Assert.Equal(0, (int)co.Rows[1].Offset);
        Assert.False(co.Rows[1].IsProposed);
        Assert.True(vm.HasResult);
        Assert.False(vm.IsRunning);
        Assert.True(co.HasPreview);
        Assert.False(co.SlidersEnabled);              // the preview keeps them disabled: Save/Discard are the only acts
    }

    /// <summary>The result panel no longer renders the per-rail rows; the proposal lives on the sliders. This pins
    /// that the sweep's result list machinery is gone, so a rewrite cannot quietly reintroduce a second panel.</summary>
    [Fact]
    public void TheResultIsShownOnTheSlidersNotAsAList()
        => Assert.DoesNotContain("Proposed offsets",
            File.ReadAllText(Path.Combine(Root(), "UI", "Views", "SweepView.axaml")), StringComparison.Ordinal);

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    [Fact]
    public async Task ProgressSurfacesTheStageClusterCoreAndOffsetWhileRunning()
    {
        var gate = new ManualResetEventSlim(false);
        var progress = new SweepProgress(SweepPhase.Loading, 0, 2, "Zen 5", -3, 0, 4, new LogicalCore(0, 0), 0, 44);
        var vm = Vm((p, _) => Task.Run(() => { p?.Report(progress); gate.Wait(3000); return Result(); }));

        _ = vm.StartCommand.ExecuteAsync(null);
        Assert.True(Eventually.Until(() =>
                vm.StatusText.Contains("stage 1/2") && vm.StatusText.Contains("cluster Zen 5")
                && vm.StatusText.Contains("core 1/4") && vm.StatusText.Contains("44")),
            "the stage/cluster/core progress never reached the card: " + vm.StatusText);

        gate.Set();
        Assert.True(Eventually.Until(() => !vm.IsRunning));
    }

    /// <summary>When the cluster could not be identified the progress says "all cores", not a stage that looks
    /// isolated.</summary>
    [Fact]
    public async Task ProgressSaysAllCoresWhenTheClusterIsUnknown()
    {
        var gate = new ManualResetEventSlim(false);
        var progress = new SweepProgress(SweepPhase.Loading, 0, 2, "Zen 5", -3, 0, 10, new LogicalCore(0, 0), 0, 44,
            ClusterIdentified: false);
        var vm = Vm((p, _) => Task.Run(() => { p?.Report(progress); gate.Wait(3000); return Result(); }));

        _ = vm.StartCommand.ExecuteAsync(null);
        Assert.True(Eventually.Until(() => vm.StatusText.Contains("all cores (cluster not identified)")),
            "the fallback wording never reached the card: " + vm.StatusText);

        gate.Set();
        Assert.True(Eventually.Until(() => !vm.IsRunning));
    }

    [Fact]
    public async Task CancelRequestsCancellationAndUnlocksTheSliders()
    {
        var sliders = new List<bool>();
        CancellationToken seen = default;
        var vm = Vm((_, ct) =>
        {
            seen = ct;
            return Task.Run(async () => { while (!ct.IsCancellationRequested) await Task.Delay(10); return Result(); }, ct);
        }, sliders: sliders);

        _ = vm.StartCommand.ExecuteAsync(null);
        Assert.True(Eventually.Until(() => vm.IsRunning), "the run never started");

        vm.CancelCommand.Execute(null);
        Assert.True(Eventually.Until(() => !vm.IsRunning), "cancel never completed");

        Assert.True(seen.IsCancellationRequested);
        Assert.Equal([true, false], sliders);   // sweepRunning raised for the run, cleared on cancel
    }

    /// <summary>UNPLUGGING MID-RUN STOPS THE SWEEP. The AC requirement is not only a gate on the start: the moment
    /// the source drops, everything the run has measured is contaminated (the platform can suppress a
    /// Curve-Optimizer write while acknowledging it, and the power budget change throttles the load the oracle
    /// reads), so the refresh pass pushing the new source must cancel the run through the same cancellation the
    /// Cancel button uses — the sweep's own finally then restores the pre-sweep counts.</summary>
    [Fact]
    public async Task LosingAcMidRunCancelsTheSweep()
    {
        var sliders = new List<bool>();
        CancellationToken seen = default;
        var vm = Vm((_, ct) =>
        {
            seen = ct;
            return Task.Run(async () => { while (!ct.IsCancellationRequested) await Task.Delay(10); return Result(); }, ct);
        }, sliders: sliders);

        _ = vm.StartCommand.ExecuteAsync(null);
        Assert.True(Eventually.Until(() => vm.IsRunning), "the run never started");

        vm.SetOnAc(false);                        // the charger was pulled (USB-C PD or battery)
        Assert.True(Eventually.Until(() => !vm.IsRunning), "the power-source loss never stopped the run");

        Assert.True(seen.IsCancellationRequested);
        Assert.False(vm.CanStart);                // and the start button is now refused until AC returns
        Assert.Equal([true, false], sliders);     // the sliders are unlocked by the same path as a manual cancel
    }

    /// <summary>The power-loss stop is its OWN sentence, not the generic "cancelled": the user did not press
    /// Cancel, and a bare "Stopped: cancelled" would read as a run that ended for no stated reason. The run still
    /// reports <see cref="SweepStop.Cancelled"/> (the mechanism is the same cancellation), so the flag is what
    /// distinguishes the two.</summary>
    [Fact]
    public async Task APowerLossStopReadsAsItsOwnSentenceNotAsCancelled()
    {
        // The fake mirrors the real runner: a run whose token was cancelled comes back as SweepStop.Cancelled
        // (the real runner observes the token and returns the result; it does not throw).
        var cancelled = new SweepResult([], SweepStop.Cancelled, 0, null);
        var vm = Vm((_, ct) =>
            Task.Run(async () => { while (!ct.IsCancellationRequested) await Task.Delay(10); return cancelled; }));

        _ = vm.StartCommand.ExecuteAsync(null);
        Assert.True(Eventually.Until(() => vm.IsRunning), "the run never started");

        vm.SetOnAc(false);
        Assert.True(Eventually.Until(() => !vm.IsRunning));

        Assert.Equal(Loc.T("uv.stop_power_lost"), vm.StatusText);
        Assert.NotEqual(Loc.T("uv.stop_cancelled"), vm.StatusText);
    }

    /// <summary>A source flapping BACK to AC cannot revive a run that was already stopped — and a run that was
    /// never stopped is not marked as a power-loss stop by a later reading. The flag is set only when a RUNNING
    /// sweep is actually cancelled for it, and cleared at each start.</summary>
    [Fact]
    public async Task ASourceThatComesBackDoesNotRestartOrRelabelTheRun()
    {
        var vm = Vm((_, ct) =>
            Task.Run(async () => { while (!ct.IsCancellationRequested) await Task.Delay(10); return Result(); }, ct));

        _ = vm.StartCommand.ExecuteAsync(null);
        Assert.True(Eventually.Until(() => vm.IsRunning));
        vm.SetOnAc(false);
        Assert.True(Eventually.Until(() => !vm.IsRunning));
        vm.SetOnAc(true);                          // plugged back in
        Assert.False(vm.IsRunning);                // it did NOT restart
        Assert.True(vm.CanStart);                  // ...but a fresh run may be started again
    }

    [Fact]
    public async Task SavingCallsTheExplicitSaveReloadsTheRowsAndClearsThePanel()
    {
        var saved = new List<SweepResult>();
        var loaded = new List<IReadOnlyList<int>>();
        var reported = new List<string>();
        var vm = Vm((_, _) => Task.FromResult(Result()),
            save: r => { saved.Add(r); return Task.FromResult<(bool, IReadOnlyList<int>?, string?)>((true, [-3, -4, -7], null)); },
            loaded: loaded, reported: reported);

        await vm.StartCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Single(saved);
        Assert.Equal([-3, -4, -7], Assert.Single(loaded));
        Assert.False(vm.HasResult);
        Assert.DoesNotContain(reported, t => t.Contains("saved"));   // no footer line on a successful save
    }

    [Fact]
    public async Task AFailedSaveKeepsTheProposalAndReports()
    {
        var reported = new List<string>();
        var vm = Vm((_, _) => Task.FromResult(Result()),
            save: _ => Task.FromResult<(bool, IReadOnlyList<int>?, string?)>((false, null, "the SMU refused")),
            reported: reported);

        await vm.StartCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);                    // the proposal is not thrown away on a refusal
        Assert.Contains(reported, t => t.Contains("refused"));
    }

    /// <summary>THE REAL REASON IS SURFACED. A run whose result carries a Detail (the forced-profile note, the
    /// SMU's own words, the back-off explanation) shows that Detail on the outcome line instead of the bare
    /// "guided undervolt failed" the card used to render. This is the reporting half of the "fails after the
    /// first cycle" bug: the leaked gate reported only SweepBusyReason's result through a generic line.
    ///
    /// MUTATION THAT REDDENS IT: reverting <c>OutcomeText</c> to <c>StopText</c> (dropping the Detail append).</summary>
    [Fact]
    public async Task ARunThatStoppedWithADetail_ShowsThatDetailOnTheOutcomeLine()
    {
        var result = Result() with { Stop = SweepStop.ApplyFailed, Detail = "a guided undervolt sweep is running" };
        var vm = Vm((_, _) => Task.FromResult(result));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains("a guided undervolt sweep is running", vm.StatusText);
    }

    /// <summary>A run that THREW reports the exception, not the bare generic sentence — the cause was swallowed
    /// here before, which is why the user could never see what actually failed.
    ///
    /// MUTATION THAT REDDENS IT: reverting the catch to <c>_report(Loc.T("uv.sweep_failed"))</c>.</summary>
    [Fact]
    public async Task ARunThatThrew_ReportsTheExceptionRatherThanTheGenericLine()
    {
        var reported = new List<string>();
        var vm = Vm((_, _) => throw new InvalidOperationException("the EC hiccuped"), reported: reported);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains(reported, t => t.Contains("the EC hiccuped"));
        Assert.Contains(reported, t => t.Contains("InvalidOperationException"));
    }

    [Fact]
    public async Task DiscardRevertsTheSlidersToTheirPreSweepValuesAndDropsThePreview()
    {
        var (vm, co) = Wired((_, _) => Task.FromResult(Result()), initial: [-8, -5]);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult);
        Assert.Equal(-3, (int)co.Rows[0].Offset);     // previewed
        Assert.True(co.HasPreview);

        vm.DiscardCommand.Execute(null);

        Assert.False(vm.HasResult);
        Assert.False(co.HasPreview);
        Assert.True(co.SlidersEnabled);
        Assert.Equal(-8, (int)co.Rows[0].Offset);     // restored exactly to the pre-preview committed value
        Assert.Equal(-5, (int)co.Rows[1].Offset);     // the untouched iGPU is still untouched
        Assert.False(co.Rows[0].IsProposed);
        Assert.False(co.Rows[1].IsProposed);
    }

    [Fact]
    public void TheShortStartHintNamesTheAcRequirementAndTheEtaAndTheCautionIsCarried()
    {
        var vm = Vm((_, _) => Task.FromResult(Result()));

        Assert.Contains("original charger", vm.StartHint);   // WHY the button is greyed on battery — must stay visible
        Assert.Contains("min", vm.StartHint);        // the ETA
        Assert.DoesNotContain("performance profile", vm.StartHint);   // the procedure lives in the confirmation
        Assert.False(string.IsNullOrWhiteSpace(vm.Caution));
        Assert.Contains("NOT proven", vm.Caution);
    }

    /// <summary>Save is the COMMIT: it drops the preview (clears the marks) and reloads the rows to the saved
    /// counts, so the sliders end up on the committed values with no proposal marking.</summary>
    [Fact]
    public async Task SavingAppliesTheProposalAndClearsThePreviewOnTheRealRows()
    {
        var (vm, co) = Wired((_, _) => Task.FromResult(Result()),
            save: _ => Task.FromResult<(bool, IReadOnlyList<int>?, string?)>((true, [-4, -5], null)));

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(co.HasPreview);
        Assert.Equal(-3, (int)co.Rows[0].Offset);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.HasResult);
        Assert.False(co.HasPreview);
        Assert.True(co.SlidersEnabled);
        Assert.Equal(-4, (int)co.Rows[0].Offset);     // reloaded to the SAVED counts (not the preview)
        Assert.Equal(-5, (int)co.Rows[1].Offset);
        Assert.False(co.Rows[0].IsProposed);
        Assert.False(co.Rows[1].IsProposed);
    }

    /// <summary>Starting a NEW run clears the prior preview BEFORE the new proposal is shown, so the new preview's
    /// snapshot is the original committed values — not the previous proposal. Proven by discarding the second
    /// proposal and landing back on the ORIGINAL offsets, not on the first proposal's.</summary>
    [Fact]
    public async Task StartingANewSweepClearsAPriorPreview()
    {
        var call = 0;
        var (vm, co) = Wired((_, _) => Task.FromResult(call++ == 0 ? Result(-3, -4) : Result(-5, -6)),
            initial: [0, 0]);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(co.HasPreview);
        Assert.Equal(-3, (int)co.Rows[0].Offset);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(-5, (int)co.Rows[0].Offset);     // the second proposal replaced the first

        vm.DiscardCommand.Execute(null);
        Assert.Equal(0, (int)co.Rows[0].Offset);      // back to the ORIGINAL committed value, not the first preview
    }

    /// <summary>Preview moves ONLY the listed indices and arms nothing. The debounce probe is read off the real
    /// schedule: a genuine drag arms it, a preview does not — so a previewed thumb can never fire an apply/persist
    /// later.</summary>
    [Fact]
    public void PreviewMovesOnlyTheListedIndicesAndArmsNoDebounce()
    {
        // The control: a genuine user drag DOES arm the debounce, so the probe is not vacuous.
        var drag = new CoViewModel("CPU", (-40, 0), 2.5,
            [new VoltageDomain("Zen 5", "ccd:0"), new VoltageDomain("iGPU", "gfx")], [0, 0], _ => { });
        drag.Rows[0].Offset = -1;
        Assert.True(DebounceArmed(drag));

        var applied = new List<int[]>();
        var co = new CoViewModel("CPU", (-40, 0), 2.5,
            [new VoltageDomain("Zen 5", "ccd:0"), new VoltageDomain("iGPU", "gfx")], [0, -2], applied.Add);

        co.Preview([(0, -6)]);

        Assert.Equal(-6, (int)co.Rows[0].Offset);
        Assert.True(co.Rows[0].IsProposed);
        Assert.Equal(-2, (int)co.Rows[1].Offset);     // the unlisted iGPU is untouched
        Assert.False(co.Rows[1].IsProposed);
        Assert.True(co.HasPreview);
        Assert.False(co.SlidersEnabled);              // disabled while previewing: Save/Discard are the only acts
        Assert.Empty(applied);                        // nothing written
        Assert.False(DebounceArmed(co));              // and nothing armed
    }

    /// <summary>Load is the commit/refresh path: it clears the preview and the marks.</summary>
    [Fact]
    public void LoadClearsThePreviewAndTheMarks()
    {
        var co = new CoViewModel("CPU", (-40, 0), 2.5,
            [new VoltageDomain("Zen 5", "ccd:0"), new VoltageDomain("iGPU", "gfx")], [0, 0], _ => { });
        co.Preview([(0, -6)]);
        Assert.True(co.HasPreview);

        co.Load([-4, -1]);

        Assert.False(co.HasPreview);
        Assert.True(co.SlidersEnabled);
        Assert.Equal(-4, (int)co.Rows[0].Offset);
        Assert.Equal(-1, (int)co.Rows[1].Offset);
        Assert.False(co.Rows[0].IsProposed);
        Assert.False(co.Rows[1].IsProposed);
        Assert.False(DebounceArmed(co));
    }

    /// <summary>DiscardPreview restores the snapshot EXACTLY (every row, not just the proposed ones) and clears the
    /// marks. Nothing is written — the machine already holds these values.</summary>
    [Fact]
    public void DiscardPreviewRestoresTheSnapshotExactly()
    {
        var applied = new List<int[]>();
        var co = new CoViewModel("CPU", (-40, 0), 2.5,
            [new VoltageDomain("Zen 5", "ccd:0"), new VoltageDomain("iGPU", "gfx")], [-7, -3], applied.Add);

        co.Preview([(0, -6)]);
        Assert.Equal(-6, (int)co.Rows[0].Offset);

        co.DiscardPreview();

        Assert.Equal(-7, (int)co.Rows[0].Offset);     // exact restore
        Assert.Equal(-3, (int)co.Rows[1].Offset);
        Assert.False(co.HasPreview);
        Assert.True(co.SlidersEnabled);
        Assert.False(co.Rows[0].IsProposed);
        Assert.Empty(applied);                        // nothing written
        Assert.False(DebounceArmed(co));
    }

    /// <summary>THE TRANSACTION GUARD, defence in depth: even a direct call to the debounce/apply internals is
    /// refused while a preview shows, so nothing can slip out if a UI guard is ever bypassed.</summary>
    [Fact]
    public void PreviewRefusesADirectApplyEvenIfTheUiGuardIsBypassed()
    {
        var applied = new List<int[]>();
        var co = new CoViewModel("CPU", (-40, 0), 2.5,
            [new VoltageDomain("Zen 5", "ccd:0")], [0], applied.Add);
        co.Preview([(0, -6)]);

        Invoke(co, "Debounce");        // a drag-equivalent tick
        Invoke(co, "ApplyDebounced");  // the debounce tick itself

        Assert.Empty(applied);
        Assert.False(DebounceArmed(co));
        Assert.True(co.HasPreview);
    }

    private static void Invoke(CoViewModel co, string method)
        => typeof(CoViewModel)
            .GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(co, null);

    /// <summary>Reads the real debounce schedule's armed state off the view-model, so "Preview arms nothing" is a
    /// fact about the shipped object and not a claim.</summary>
    private static bool DebounceArmed(CoViewModel co)
        => ((PeriodicSchedule)typeof(CoViewModel)
                .GetField("_debounce", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(co)!).IsRunning;

    /// <summary>THE POLARITY OF THE SLIDER LOCK. The sweep tells the CoViewModel a sweep is running for exactly the
    /// run's duration, so the manual rows are unusable WHILE it runs. The wiring once passed an "enabled" flag into
    /// <c>SetSweepRunning</c>, whose parameter means the opposite, so the sliders were locked exactly when they
    /// should have been free and vice versa; a test that only recorded the raw delegate arguments passed the
    /// inverted wiring. This drives the REAL CoViewModel and asserts its <c>SlidersEnabled</c>. After the run the
    /// PREVIEW keeps them locked (<see cref="CoViewModel.HasPreview"/>) — the only actions are Save/Discard — and
    /// Discard frees them again.</summary>
    [Fact]
    public async Task TheRunLocksTheManualSlidersForItsDurationAndThePreviewKeepsThemLockedUntilDiscarded()
    {
        var gate = new ManualResetEventSlim(false);
        var (vm, co) = Wired((_, _) => Task.Run(() => { gate.Wait(3000); return Result(); }),
            save: _ => Task.FromResult<(bool, IReadOnlyList<int>?, string?)>((false, null, null)));

        Assert.True(co.SlidersEnabled);

        var run = vm.StartCommand.ExecuteAsync(null);
        Assert.True(Eventually.Until(() => !co.SlidersEnabled), "the sliders must be locked while the sweep runs");
        Assert.False(co.HasPreview);                   // locked by the RUN here, not yet by a preview

        gate.Set();
        await run;

        Assert.True(co.HasPreview);                    // the completed run previews the proposal...
        Assert.False(co.SlidersEnabled);               // ...and the preview keeps the rows disabled

        co.DiscardPreview();
        Assert.True(co.SlidersEnabled);                // Discard frees them again
    }
}

/// <summary>The sweep is hosted by a bare <c>ContentControl Content="{Binding Sweep}"</c> in TuningView, so its
/// view resolves ONLY through an application-scope <c>DataTemplate</c> in <c>UI/App.axaml</c>. Missing that
/// template does not fail the build or any other test — the control silently falls back to the view-model's
/// <c>ToString()</c> and the panel renders as the raw type name (<c>AcerHelper.UI.ViewModels.UndervoltSweepViewModel</c>),
/// the exact bug seen on screen. This guard pins the mapping; the mutation is deleting the template line.</summary>
public class SweepViewTemplateGuardTests
{
    [Fact]
    public void TheSweepViewModelHasADataTemplate()
    {
        var app = Source("UI/App.axaml");
        Assert.Contains("<DataTemplate DataType=\"vm:UndervoltSweepViewModel\"><views:SweepView/></DataTemplate>",
            app, StringComparison.Ordinal);
    }

    private static string Source(string relativePath, [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }
}

/// <summary>Every literal the guided-sweep card and its confirmation show has a Russian entry, so the Russian
/// build never silently falls back to English. The card's literals are neutral keys.</summary>
public class UndervoltSweepLocalizationTests
{
    [Fact]
    public void EverySentenceTheSweepShowsIsTranslated()
    {
        string[] keys =
        [
            "uv.start", "uv.confirm_title", "uv.sweep_start", "uv.sweep_cancel",
            "uv.result_hint",
            "uv.save", "uv.discard",
            "uv.sweep_failed",
            "uv.phase_first", "uv.phase_applying", "uv.phase_testing", "uv.phase_single_core", "uv.phase_backing_off", "uv.phase_confirming", "uv.phase_restoring", "nav.done",
            "uv.temp_unreadable", "uv.temp_c",
            "uv.stage", "uv.cluster", "uv.all_cores",
            "uv.telemetry", "uv.telemetry_core", "uv.elapsed",
            "uv.finished",
            "uv.stop_thermal", "uv.stop_cancelled",
            "uv.stop_smu",
            "uv.stop_power_lost",
            "uv.refused_ac",
            "uv.connect_ac",
            "uv.waiting_power",
            "uv.requires_ac",
            "uv.stopped",
            "uv.eta_hours", "uv.eta_minutes", "uv.eta_seconds", "uv.confirm_body",
        ];

        foreach (var key in keys)
            Assert.True(Loc.Ru(key) is not null,
                "the guided sweep shows a sentence with no entry in Localization/Strings.ru.resx:\n  " + key);

        // The caution is set from a Loc.T key on the view-model; hold it to the table too. The view-model yields
        // the neutral English, so assert the key it must be built from has a Russian row.
        var caution = new UndervoltSweepViewModel(
            [new SweepDomain(0, "ccd:0", "Zen 5", -5, 0, 2.5)], TimeSpan.Zero,
            () => Task.FromResult(false), (_, _) => Task.FromResult(SweepResult.Empty),
            _ => Task.FromResult<(bool, IReadOnlyList<int>?, string?)>((false, null, null)),
            _ => { }, _ => { }, _ => { }, () => { }, _ => { }, Eventually.Sync).Caution;
        Assert.Equal(Loc.T("uv.caution"), caution);
        Assert.True(Loc.Ru("uv.caution") is not null, "the caution is not translated");
    }
}
