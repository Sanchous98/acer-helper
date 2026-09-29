using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// THE GUIDED UNDERVOLT SWEEP'S PURE POLICY, offline. Everything here drives UndervoltSweep through a fake
/// target — no SMU, no core, no OS — so the decisions the feature rests on are pinned directly: stepping, the
/// stop on the first oracle failure, the safety back-off plus re-verify, per-domain independence, the preserved
/// iGPU slot, the per-rail clamp, cancellation, and thermal aborts not being counted as verdicts. The
/// restore-on-every-exit property is asserted in each of them, because it is the safety model. The sweep has NO
/// time-based boundary: it terminates at the rail's hard floor and a bounded back-off, so a run over a rail that
/// never fails walks to the floor and reports Completed.
/// </summary>
public class SweepPolicyTests
{
    private static SweepDomain D(int index, int min = -5, int max = 0, string key = "ccd:0", string label = "Zen 5")
        => new(index, key, label, min, max, 2.5);

    [Fact]
    public void NextOffsetStepsDeeperAndStopsAtTheFloor()
    {
        Assert.Equal(-1, SweepPolicy.NextOffset(0, 1, -5));
        Assert.Equal(-5, SweepPolicy.NextOffset(-5, 1, -5));
        Assert.Equal(-2, SweepPolicy.NextOffset(0, 3, -2));      // the floor wins over the step
        Assert.Equal(-1, SweepPolicy.NextOffset(0, 0, -5));      // a non-positive step is one
    }

    [Fact]
    public void BackOffGoesTowardStockAndNeverPastIt()
    {
        Assert.Equal(-3, SweepPolicy.BackOff(-4, 1, 0));
        Assert.Equal(-2, SweepPolicy.BackOff(-4, 2, 0));
        Assert.Equal(0, SweepPolicy.BackOff(-1, 5, 0));          // never past stock
        Assert.Equal(0, SweepPolicy.BackOff(0, 1, 0));
        Assert.Equal(-1, SweepPolicy.BackOff(-2, 0, 0));         // a non-positive margin is one
    }

    [Theory]
    [InlineData(SweepProbeStatus.Passed, true)]
    [InlineData(SweepProbeStatus.CoreError, true)]
    [InlineData(SweepProbeStatus.ThermalAbort, false)]
    [InlineData(SweepProbeStatus.Cancelled, false)]
    [InlineData(SweepProbeStatus.Failed, false)]
    public void OnlyPassAndCoreErrorAreStabilityVerdicts(SweepProbeStatus status, bool expected)
        => Assert.Equal(expected, SweepPolicy.IsStabilityVerdict(status));

    [Fact]
    public void WithOffsetReplacesOnlyTheSweptSlot()
    {
        int[] baseline = [0, 0, -7];                            // index 2 is the iGPU, which must never move
        Assert.Equal([-2, 0, -7], SweepPolicy.WithOffset(baseline, D(0), -2));
        Assert.Equal([0, -3, -7], SweepPolicy.WithOffset(baseline, D(1), -3));
        Assert.True(baseline.SequenceEqual([0, 0, -7]));        // the caller's array is not mutated
    }

    /// <summary>EVERY swept CPU cluster starts from STOCK, not from its stored preset: probing ccd:0 must not run
    /// with ccd:1's (possibly unstable) saved offset live, and vice versa. Non-cluster slots (the iGPU) are
    /// carried.</summary>
    [Fact]
    public void StockBaselineHoldsEverySweptClusterAtZeroAndCarriesTheGpu()
    {
        Assert.Equal([0, 0, -7], SweepPolicy.StockBaseline([-19, -15, -7], [D(0), D(1)]));
        Assert.Equal([0, -15, -7], SweepPolicy.StockBaseline([-19, -15, -7], [D(0)]));   // only walked clusters zeroed
    }

    [Fact]
    public void WithOffsetClampsToTheDomainsOwnBounds()
    {
        Assert.Equal([-4, 0, -7], SweepPolicy.WithOffset([0, 0, -7], D(0, -4), -99));
        Assert.Equal([0, 0, -7], SweepPolicy.WithOffset([0, 0, -7], D(0, -4), 5));
    }
}

/// <summary>The runner's control flow through a fake target and a scripted oracle.</summary>
public class UndervoltSweepRunTests
{
    private static SweepDomain D(int index, int min = -5, int max = 0, string key = "ccd:0", string label = "Zen 5")
        => new(index, key, label, min, max, 2.5);

    // repetitions defaults to 1 so the pre-existing control-flow tests keep their exact probe counts; the
    // repetition behaviour has its own test. The other numbers stay explicit so a change to the production
    // defaults cannot silently rewrite what these tests assert. ConfirmSoak is OFF here because these are
    // SEARCH-phase control-flow tests; the soak has its own dedicated tests below.
    private static SweepOptions Options(int? start = null, int step = 1, int margin = 1, int maxBackoff = 2, int repetitions = 1)
        => new()
        {
            StartCounts = start,
            Step = step,
            SafetyMargin = margin,
            MaxBackoffAttempts = maxBackoff,
            Repetitions = repetitions,
            ConfirmSoak = false,
        };

    private sealed class FakeTarget : IUndervoltSweepTarget
    {
        public Func<SweepDomain, int, SweepProbeResult> ProbeBehaviour { get; set; } = (_, _) => SweepProbeResult.Pass();
        public Func<IReadOnlyList<int>, CancellationToken, SweepApplyOutcome> ApplyBehaviour { get; set; }
            = (_, _) => SweepApplyOutcome.Applied;

        /// <summary>Optional distinct behaviour for the SOAK probe. When null the soak reuses
        /// <see cref="ProbeBehaviour"/>, so a pre-existing search-only test is unaffected.</summary>
        public Func<SweepDomain, int, SweepProbeResult>? SoakBehaviour { get; set; }

        public List<int[]> Applied { get; } = [];
        public List<int[]> Restored { get; } = [];
        public List<int> ProbedOffsets { get; } = [];
        public List<int> SoakedOffsets { get; } = [];

        public SweepApplyOutcome Apply(IReadOnlyList<int> counts, CancellationToken cancellation)
        {
            Applied.Add([.. counts]);
            return ApplyBehaviour(counts, cancellation);
        }

        public SweepProbeResult Probe(SweepDomain domain, int offset, IReadOnlyList<int> counts,
            int domainIndex, int domainCount, Action<SweepProgress>? progress, CancellationToken cancellation,
            SweepProbeMode mode)
        {
            if (mode == SweepProbeMode.Soak)
            {
                SoakedOffsets.Add(offset);
                progress?.Invoke(new SweepProgress(SweepPhase.Confirming, domainIndex, domainCount, domain.Label,
                    offset, 0, 1, new LogicalCore(0, 0), 0, 44));
                return SoakBehaviour is { } soak ? soak(domain, offset) : ProbeBehaviour(domain, offset);
            }

            ProbedOffsets.Add(offset);
            progress?.Invoke(new SweepProgress(SweepPhase.Loading, domainIndex, domainCount, domain.Label,
                offset, 0, 1, new LogicalCore(0, 0), 0, 44));
            return ProbeBehaviour(domain, offset);
        }

        public SweepApplyOutcome Restore(IReadOnlyList<int> counts, CancellationToken cancellation)
        {
            Restored.Add([.. counts]);
            return SweepApplyOutcome.Applied;
        }
    }

    private static SweepRequest Request(SweepOptions options, IReadOnlyList<int> baseCounts, params SweepDomain[] domains)
        => new(domains, baseCounts, options);

    /// <summary>The restore list holds ARRAYS, so membership is asserted structurally rather than by reference.</summary>
    private static void AssertRestoredTo(FakeTarget target, int[] expected)
        => Assert.Contains(target.Restored, r => r.SequenceEqual(expected));

    private static SweepProbeResult ErrorAt(int offset, int failAt)
        => offset >= failAt
            ? SweepProbeResult.Pass()
            : SweepProbeResult.Error(new LogicalCore(0, 0), LoadOutcome.ChecksumMismatch, $"failed at {offset}");

    /// <summary>The core loop: deeper while the oracle passes, stop on the FIRST failure, back off one step and
    /// re-verify — and every application in between carries the iGPU slot untouched.</summary>
    [Fact]
    public void StepsDeeperStopsOnTheFirstFailureAndBacksOffToTheLastPassingOffset()
    {
        var target = new FakeTarget { ProbeBehaviour = (_, offset) => ErrorAt(offset, -3) };   // fails at -4
        var result = UndervoltSweep.Run(Request(Options(), [0, 0, -7], D(0)), target, null, CancellationToken.None);

        Assert.Equal(SweepStop.FirstError, result.Stop);
        var d = Assert.Single(result.Domains);
        Assert.Equal(-3, d.Proposed);
        Assert.Equal(-4, d.FirstFailing);
        Assert.True(d.Reverified);
        Assert.True(d.Stable);
        Assert.Equal(4, d.Passes);
        Assert.Equal([0, -1, -2, -3, -4, -3], target.Applied.Select(a => a[0]));
        Assert.All(target.Applied, a => Assert.Equal(-7, a[2]));    // the iGPU slot is carried, never swept
        AssertRestoredTo(target, [0, 0, -7]);                // ...and the pre-sweep state goes back
    }

    /// <summary>No probe deeper than the first failure is attempted; the back-off re-verifies the safer value.</summary>
    [Fact]
    public void NoProbeGoesDeeperThanTheFirstFailure()
    {
        var target = new FakeTarget { ProbeBehaviour = (_, offset) => ErrorAt(offset, -1) };   // fails at -2
        UndervoltSweep.Run(Request(Options(), [0, 0, -7], D(0)), target, null, CancellationToken.None);

        Assert.Equal(-2, target.Applied.Min(a => a[0]));           // -2, never -3
    }

    /// <summary>The step uses the rail's own hard floor, not a hard-coded -40/-50.</summary>
    [Fact]
    public void TheStepClampsToTheDomainsOwnFloor()
    {
        var target = new FakeTarget();
        var result = UndervoltSweep.Run(Request(Options(), [0, 0, -7], D(0, min: -3)), target, null, CancellationToken.None);

        Assert.Equal([0, -1, -2, -3], target.Applied.Select(a => a[0]));
        Assert.Equal(-3, result.Domains[0].Proposed);
    }

    /// <summary>Per-domain independence: each cluster is walked on its own, from a stock baseline, and a failure
    /// on one rail does not move the other's proposal.</summary>
    [Fact]
    public void EachDomainIsSweptIndependently()
    {
        var target = new FakeTarget
        {
            ProbeBehaviour = (domain, offset) => domain.Index == 0 ? ErrorAt(offset, -1) : ErrorAt(offset, -3),
        };
        var result = UndervoltSweep.Run(
            Request(Options(), [0, 0, -7], D(0), D(1, min: -3, key: "ccd:1", label: "Zen 5c")),
            target, null, CancellationToken.None);

        Assert.Equal(2, result.Domains.Count);
        Assert.Equal(-1, result.Domains[0].Proposed);
        Assert.Equal(-3, result.Domains[1].Proposed);
        Assert.All(target.Applied, a => Assert.Equal(-7, a[2]));    // the iGPU slot is carried throughout
        // While ccd:0 is swept, ccd:1 is at stock; while ccd:1 is swept, ccd:0 is back to stock — never a live
        // sibling preset during a probe.
        Assert.Contains(target.Applied, a => a[0] == -1 && a[1] == 0);
        Assert.Contains(target.Applied, a => a[0] == 0 && a[1] != 0);
    }

    /// <summary>The default start is STOCK (0), AND the sibling cluster is ALSO held at stock while this one is
    /// swept — a stored preset is never live during a probe. A configured start overrides only the swept slot.</summary>
    [Fact]
    public void TheStartIsStockByDefaultAndSiblingsAreHeldAtStock()
    {
        var target = new FakeTarget();
        UndervoltSweep.Run(Request(Options(), [-2, -3, -7], D(0), D(1, key: "ccd:1", label: "Zen 5c")),
            target, null, CancellationToken.None);
        Assert.Equal(0, target.Applied[0][0]);                     // domain 0 starts at STOCK, not its stored -2
        Assert.Equal(0, target.Applied[0][1]);                     // domain 1 is ALSO at stock while 0 is swept
        Assert.Contains(target.Applied, a => a[0] == -1 && a[1] == 0);

        var configured = new FakeTarget();
        UndervoltSweep.Run(Request(Options(start: -1), [0, 0, -7], D(0)), configured, null, CancellationToken.None);
        Assert.Equal(-1, configured.Applied[0][0]);
    }

    /// <summary>The documented failure is "it did not crash"; a run that could not test every core is not a pass,
    /// which the probe adapter reports as Failed — here modelled as a thermal abort that must not be a verdict.</summary>
    [Fact]
    public void AThermalAbortIsNotAVerdictAndStillRestores()
    {
        var target = new FakeTarget { ProbeBehaviour = (_, _) => SweepProbeResult.Thermal("too hot") };
        var result = UndervoltSweep.Run(Request(Options(), [0, 0, -7], D(0)), target, null, CancellationToken.None);

        Assert.Equal(SweepStop.ThermalAbort, result.Stop);
        var d = Assert.Single(result.Domains);
        Assert.Null(d.FirstFailing);                               // no offset was called unstable
        Assert.False(d.Stable);
        AssertRestoredTo(target, [0, 0, -7]);
    }

    /// <summary>When even the start fails, the proposal is stock and the panel says so — never the failing value.</summary>
    [Fact]
    public void AStartThatFailsFallsBackToStockAndIsNotClaimedStable()
    {
        var target = new FakeTarget { ProbeBehaviour = (_, _) => SweepProbeResult.Error(new LogicalCore(0, 0), LoadOutcome.ChecksumMismatch, "bad") };
        var result = UndervoltSweep.Run(Request(Options(), [0, 0, -7], D(0)), target, null, CancellationToken.None);

        Assert.Equal(SweepStop.FirstError, result.Stop);
        var d = Assert.Single(result.Domains);
        Assert.Equal(0, d.Proposed);
        Assert.Equal(0, d.FirstFailing);
        Assert.False(d.Stable);
        Assert.Contains("did not verify", d.Detail);
    }

    /// <summary>An apply the SMU refused aborts the sweep with the port's own reason, and still restores.</summary>
    [Fact]
    public void ARefusedApplyAbortsAndRestores()
    {
        var target = new FakeTarget
        {
            ApplyBehaviour = (counts, _) => counts[0] <= -2
                ? SweepApplyOutcome.Refused("the SMU returned 0xFF")
                : SweepApplyOutcome.Applied,
        };
        var result = UndervoltSweep.Run(Request(Options(), [0, 0, -7], D(0)), target, null, CancellationToken.None);

        Assert.Equal(SweepStop.ApplyFailed, result.Stop);
        Assert.Equal("the SMU returned 0xFF", result.Detail);
        AssertRestoredTo(target, [0, 0, -7]);
    }

    /// <summary>Cancellation stops between steps, keeps what passed, and restores.</summary>
    [Fact]
    public void CancellationStopsAndRestores()
    {
        using var cts = new CancellationTokenSource();
        var target = new FakeTarget
        {
            ProbeBehaviour = (_, offset) =>
            {
                if (offset <= -1) cts.Cancel();                    // cancel during the probe; the loop notices next
                return SweepProbeResult.Pass();
            },
        };
        var result = UndervoltSweep.Run(Request(Options(), [0, 0, -7], D(0)), target, null, cts.Token);

        Assert.Equal(SweepStop.Cancelled, result.Stop);
        Assert.Equal(2, result.Domains[0].Passes);                 // 0 and -1 passed before the cancel was noticed
        Assert.Equal(-1, result.Domains[0].Proposed);
        AssertRestoredTo(target, [0, 0, -7]);
    }

    /// <summary>THE EXCEPTION GUARANTEE: a delegate that throws is not allowed to leave a probe offset on the
    /// SMU, so the restore runs and the throw then propagates to the caller.</summary>
    [Fact]
    public void AThrownProbeStillRestores()
    {
        var target = new FakeTarget { ProbeBehaviour = (_, _) => throw new InvalidOperationException("boom") };

        Assert.Throws<InvalidOperationException>(() =>
            UndervoltSweep.Run(Request(Options(), [0, 0, -7], D(0)), target, null, CancellationToken.None));

        AssertRestoredTo(target, [0, 0, -7]);
    }

    [Fact]
    public void AThrownApplyStillRestores()
    {
        var target = new FakeTarget { ApplyBehaviour = (_, _) => throw new InvalidOperationException("boom") };

        Assert.Throws<InvalidOperationException>(() =>
            UndervoltSweep.Run(Request(Options(), [0, 0, -7], D(0)), target, null, CancellationToken.None));

        AssertRestoredTo(target, [0, 0, -7]);
    }

    /// <summary>Progress is emitted for the apply and the load phases, with the domain and offset.</summary>
    [Fact]
    public void ProgressNamesTheDomainAndOffset()
    {
        var target = new FakeTarget();
        var seen = new List<SweepProgress>();
        UndervoltSweep.Run(Request(Options(), [0, 0, -7], D(0)), target, p => seen.Add(p), CancellationToken.None);

        Assert.Contains(seen, p => p.Phase == SweepPhase.Applying && p.Offset == 0 && p.DomainLabel == "Zen 5");
        Assert.Contains(seen, p => p.Phase == SweepPhase.Loading && p.Offset == 0);
    }

    /// <summary>The stage number is the domain's 1-based position, and a domain marked not-identified carries
    /// that into its progress so the UI can say "all cores" rather than a stage that looks isolated.</summary>
    [Fact]
    public void ProgressCarriesTheStageAndTheClusterIdentifiedFlag()
    {
        var target = new FakeTarget();
        var seen = new List<SweepProgress>();
        var domain = new SweepDomain(1, "ccd:1", "Zen 5c", -3, 0, 2.5, CpuClusterKind.Efficiency, ClusterIdentified: false);
        UndervoltSweep.Run(Request(Options(), [0, 0, -7], D(0), domain), target, p => seen.Add(p), CancellationToken.None);

        Assert.Contains(seen, p => p.DomainIndex == 0 && p.Stage == 1 && p.DomainCount == 2 && p.ClusterIdentified);
        Assert.Contains(seen, p => p.DomainIndex == 1 && p.Stage == 2 && p.DomainCount == 2 && !p.ClusterIdentified);
    }

    /// <summary>REPETITIONS RAISE THE DWELL: every offset that passes is probed the configured number of times,
    /// all of which must pass. This is what makes mild, probabilistic instability manifest.</summary>
    [Fact]
    public void EachPassingOffsetIsProbedTheConfiguredNumberOfTimes()
    {
        var target = new FakeTarget();
        var result = UndervoltSweep.Run(Request(Options(repetitions: 3), [0, 0, -7], D(0, min: -2)), target, null,
            CancellationToken.None);

        // offsets 0, -1, -2 each probed three times.
        Assert.Equal([0, 0, 0, -1, -1, -1, -2, -2, -2], target.ProbedOffsets);
        Assert.Equal(SweepStop.Completed, result.Stop);
        Assert.Equal(-2, result.Domains[0].Proposed);
    }

    /// <summary>ANY failing repetition fails its offset — the sweep stops at the FIRST offset where a repetition
    /// errored, even if earlier repetitions of that same offset passed.</summary>
    [Fact]
    public void AnyFailingRepetitionFailsItsOffset()
    {
        var counts = new Dictionary<int, int>();
        var target = new FakeTarget
        {
            ProbeBehaviour = (_, offset) =>
            {
                lock (counts)
                {
                    counts.TryGetValue(offset, out var n);
                    counts[offset] = ++n;
                    if (offset == -2 && n == 2)   // the second repetition of -2
                        return SweepProbeResult.Error(new LogicalCore(0, 0), LoadOutcome.ChecksumMismatch, "flaky");
                }
                return SweepProbeResult.Pass();
            },
        };

        var result = UndervoltSweep.Run(Request(Options(repetitions: 3), [0, 0, -7], D(0)), target, null,
            CancellationToken.None);

        Assert.Equal(-2, result.Domains[0].FirstFailing);
        Assert.Equal(-1, result.Domains[0].Proposed);      // backed off from the failure and re-verified
        Assert.True(result.Domains[0].Reverified);
    }

    /// <summary>The rough ETA is positive and scales with the number of rails — and, with no total budget to
    /// clamp it to, it is the honest estimate of a full walk (so it is NOT capped at any time bound).</summary>
    [Fact]
    public void TheRoughEtaIsUnboundedButPositiveAndScales()
    {
        var options = Options() with
        {
            Load = new CpuLoadOptions { PerCoreBudget = TimeSpan.FromSeconds(10), TotalBudget = TimeSpan.FromMinutes(5) },
        };
        var one = UndervoltSweep.RoughEta([D(0)], [0, 0, -7], options, [10]);
        var two = UndervoltSweep.RoughEta([D(0), D(1, key: "ccd:1")], [0, 0, -7], options, [10, 10]);

        Assert.True(one > TimeSpan.Zero);
        Assert.True(two > one);
    }

    /// <summary>The ETA is HONEST about the stronger probe: repetitions and the single-core phase each multiply
    /// the estimate, so the UI does not promise a fast run it cannot deliver.</summary>
    [Fact]
    public void TheRoughEtaGrowsWithRepetitionsAndTheSingleCorePhase()
    {
        var load = new CpuLoadOptions { PerCoreBudget = TimeSpan.FromSeconds(30), TotalBudget = TimeSpan.FromMinutes(20) };
        var baseOptions = Options() with { Load = load, IncludeSingleCorePhase = false, Repetitions = 1, SafetyMargin = 1 };
        var repeated = baseOptions with { Repetitions = 4 };
        var singleCore = baseOptions with { IncludeSingleCorePhase = true, SingleCoreBudget = TimeSpan.FromSeconds(30) };

        var baseline = UndervoltSweep.RoughEta([D(0, min: -5)], [0, 0, -7], baseOptions, [10]);
        var withReps = UndervoltSweep.RoughEta([D(0, min: -5)], [0, 0, -7], repeated, [10]);
        var withSingle = UndervoltSweep.RoughEta([D(0, min: -5)], [0, 0, -7], singleCore, [10]);

        Assert.Equal(baseline * 4, withReps);                       // four repetitions is four times the load
        Assert.True(withSingle > baseline);                        // the added phase is not free
    }

    /// <summary>THE ETA IS HONEST ABOUT THE SOAK: enabling the final confirmation adds a large term, and the
    /// longer the confirm budgets the larger it gets — so the UI estimate does not understate a run that now
    /// spends minutes soaking each settled domain.</summary>
    [Fact]
    public void TheRoughEtaIncludesTheFinalSoak()
    {
        var load = new CpuLoadOptions { PerCoreBudget = TimeSpan.FromSeconds(30), TotalBudget = TimeSpan.FromMinutes(20) };
        var searchOnly = Options() with { Load = load, ConfirmSoak = false, IncludeSingleCorePhase = false };
        var soaked = searchOnly with { ConfirmSoak = true, ConfirmAllCore = TimeSpan.FromMinutes(5) };
        var longer = soaked with { ConfirmAllCore = TimeSpan.FromMinutes(10) };

        var baseline = UndervoltSweep.RoughEta([D(0, min: -5)], [0, 0, -7], searchOnly, [4]);
        var withSoak = UndervoltSweep.RoughEta([D(0, min: -5)], [0, 0, -7], soaked, [4]);
        var withLonger = UndervoltSweep.RoughEta([D(0, min: -5)], [0, 0, -7], longer, [4]);

        Assert.True(withSoak > baseline);                          // the soak is not free
        Assert.True(withLonger > withSoak);                        // a longer soak costs more
        // The soak term is per settled domain (cores x budget x attempts), not per search step, and is large:
        // at 5 minutes and 4 cores it is at least cores * budget.
        Assert.True(withSoak - baseline >= TimeSpan.FromMinutes(5) * 4);
    }

    /// <summary>THE CONSERVATIVE PROPOSAL: with the shipped defaults the back-off steps more than one count, so
    /// the proposal lands strictly above (safer than) the last offset that merely passed — never exactly on the
    /// edge where mild instability was one unlucky run away.</summary>
    [Fact]
    public void TheDefaultBackOffProposesSaferThanTheLastPassingOffset()
    {
        var defaults = new SweepOptions();                          // the shipped numbers, not the test-local ones
        var target = new FakeTarget { ProbeBehaviour = (_, offset) => ErrorAt(offset, -4) };
        var result = UndervoltSweep.Run(Request(defaults, [0, 0, -7], D(0, min: -8)), target, null, CancellationToken.None);

        var d = result.Domains[0];
        Assert.Equal(-5, d.FirstFailing);                           // last passed -4, failure at -5
        Assert.Equal(-2, d.Proposed);                               // margin 3 from -5, i.e. 2 counts safer than -4
        Assert.True(d.Proposed > d.FirstFailing);                   // strictly away from the edge
        Assert.True(d.Reverified);
    }

    /// <summary>THE SHIPPED BUDGETS, pinned so a default cannot silently drift. The search single-core dwell is
    /// 30 s per core (raised from 10 s because the owner reported the check was still too short) and the all-core
    /// search budget stays 30 s; the soak is still the final evidence with its much longer confirm budgets (5 min
    /// all-core, 3 min single-core). <see cref="UndervoltSweep.RoughEta"/> multiplies by these, so the UI estimate
    /// moves with them.</summary>
    [Fact]
    public void TheShippedBudgetsAreTheDocumentedOnes()
    {
        var defaults = new SweepOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), defaults.SingleCoreBudget);   // the search's high-boost dwell
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.Load.PerCoreBudget); // the all-core search budget, unchanged
        Assert.Equal(TimeSpan.FromMinutes(5), defaults.ConfirmAllCore);      // the soak's all-core pass
        Assert.Equal(TimeSpan.FromMinutes(3), defaults.ConfirmSingleCore);   // the soak's single-core pass
        Assert.True(defaults.IncludeSingleCorePhase);
        Assert.True(defaults.ConfirmSoak);
    }

    /// <summary>THE OWNER'S RULE (source guard): time is NOT a boundary of the sweep, so the sweep's source must
    /// carry no sweep-level budget — neither a declared <see cref="SweepOptions"/> property nor a read of one —
    /// and no BudgetExhausted stop. The load runner's OWN <c>TotalBudget</c> (its per-run measurement window) is a
    /// different thing and stays.</summary>
    [Fact]
    public void TheSweepHasNoTimeBasedBoundary()
    {
        var sweep = File.ReadAllText(Path.Combine(Root(), "Infrastructure", "Vendors", "Generic", "UndervoltSweep.cs"));

        Assert.DoesNotContain("PerDomainBudget", sweep, StringComparison.Ordinal);
        Assert.DoesNotContain("BudgetExhausted", sweep, StringComparison.Ordinal);   // the stop enum has no such value
        Assert.DoesNotContain("TimeSpan TotalBudget", sweep, StringComparison.Ordinal);   // SweepOptions declares none
        Assert.DoesNotContain("options.TotalBudget", sweep, StringComparison.Ordinal);    // ...and nothing reads one
        Assert.DoesNotContain("Options.TotalBudget", sweep, StringComparison.Ordinal);
    }

    /// <summary>THE PROPERTY the owner asked for: a run over a domain that NEVER fails cannot be cut off by
    /// elapsed time — it walks the whole rail to its floor and returns Completed, with a proposal at the floor.</summary>
    [Fact]
    public void ARunOverADomainThatNeverFailsWalksToTheFloorAndReturnsCompleted()
    {
        var target = new FakeTarget { ProbeBehaviour = (_, _) => SweepProbeResult.Pass() };
        var result = UndervoltSweep.Run(Request(Options(), [0, 0, -7], D(0, min: -5)), target, null,
            CancellationToken.None);

        Assert.Equal(SweepStop.Completed, result.Stop);
        var d = Assert.Single(result.Domains);
        Assert.Equal(-5, d.Proposed);                              // the rail's floor, not a time-clipped value
        Assert.Null(d.FirstFailing);                               // nothing failed
        Assert.Equal(6, d.Passes);                                 // 0,-1,-2,-3,-4,-5 all passed
        Assert.Contains(target.Applied, a => a[0] == -5);          // the floor was actually probed
        AssertRestoredTo(target, [0, 0, -7]);
    }

    // ---- the final soak confirmation ----

    /// <summary>THE CORE PROPERTY: the soak runs EXACTLY ONCE per settled domain, on the offset the search
    /// settled on, and its pass is what makes the proposal stable (Soaked = true). It does NOT run per search
    /// step.</summary>
    [Fact]
    public void TheSoakRunsOncePerSettledDomainAndItsPassMakesTheProposalStable()
    {
        var target = new FakeTarget();                            // every search probe and the soak pass
        var result = UndervoltSweep.Run(Request(Options() with { ConfirmSoak = true }, [0, 0, -7], D(0, min: -3)),
            target, null, CancellationToken.None);

        Assert.Equal(SweepStop.Completed, result.Stop);
        var d = Assert.Single(result.Domains);
        Assert.Equal(-3, d.Proposed);
        Assert.True(d.Stable);
        Assert.True(d.Soaked);
        Assert.Equal([-3], target.SoakedOffsets);                 // once, on the settled offset
    }

    /// <summary>TWO DOMAINS SOAK INDEPENDENTLY, once each, on their own settled offsets.</summary>
    [Fact]
    public void EachDomainIsSoakedOnceOnItsOwnSettledOffset()
    {
        var target = new FakeTarget
        {
            ProbeBehaviour = (domain, _) => domain.Index == 0
                ? SweepProbeResult.Pass()                             // domain 0 walks to its floor
                : SweepProbeResult.Error(new LogicalCore(0, 0), LoadOutcome.ChecksumMismatch, "edge"),
        };
        var result = UndervoltSweep.Run(
            Request(Options(margin: 1, maxBackoff: 1) with { ConfirmSoak = true }, [0, 0, -7],
                D(0, min: -2), D(1, min: -2, key: "ccd:1")),
            target, null, CancellationToken.None);

        Assert.Equal(2, target.SoakedOffsets.Count);              // one soak per domain
        Assert.Equal(-2, result.Domains[0].Proposed);             // domain 0 at its floor
        Assert.True(result.Domains[0].Soaked);
    }

    /// <summary>A SOAK FAILURE RETREATS: when the settled offset fails the long soak, the sweep backs off by
    /// SafetyMargin and re-soaks; a pass at the safer value is the proposal and is stable.</summary>
    [Fact]
    public void ASoakFailureRetreatsTowardStockAndReSoaks()
    {
        // The search passes everywhere; the soak fails at the floor (-4) but passes from -1 (margin 3) upward.
        var target = new FakeTarget
        {
            SoakBehaviour = (_, offset) => offset >= -1
                ? SweepProbeResult.Pass()
                : SweepProbeResult.Error(new LogicalCore(0, 0), LoadOutcome.ChecksumMismatch, "soak failed"),
        };
        var result = UndervoltSweep.Run(Request(Options(margin: 3, maxBackoff: 2) with { ConfirmSoak = true },
            [0, 0, -7], D(0, min: -4)), target, null, CancellationToken.None);

        var d = Assert.Single(result.Domains);
        Assert.Equal(-1, d.Proposed);                              // retreated from -4 by 3 and verified
        Assert.True(d.Stable);
        Assert.True(d.Soaked);
        Assert.Equal([-4, -1], target.SoakedOffsets);             // the settled offset, then the retreat
    }

    /// <summary>SOAK FAILURE WITH NOWHERE SAFER PROPOSES STOCK, NOT THE FAILED OFFSET, WITH Stable = false.</summary>
    [Fact]
    public void ASoakFailureWithNowhereSaferProposesStockAndIsNotStable()
    {
        var target = new FakeTarget
        {
            // Every soak fails, including stock; the search never failed (so there is no back-off candidate).
            SoakBehaviour = (_, _) => SweepProbeResult.Error(new LogicalCore(0, 0), LoadOutcome.ChecksumMismatch, "soak failed"),
        };
        var result = UndervoltSweep.Run(Request(Options(margin: 3, maxBackoff: 2) with { ConfirmSoak = true },
            [0, 0, -7], D(0, max: 0)), target, null, CancellationToken.None);

        var d = Assert.Single(result.Domains);
        Assert.Equal(0, d.Proposed);                               // stock, never the offset that just failed
        Assert.False(d.Stable);
        Assert.False(d.Soaked);                                    // no soak ever passed
        Assert.Contains("soak", d.Detail);
        // It retreated from the settled value toward stock by the margin until it could not get safer.
        Assert.All(target.SoakedOffsets, o => Assert.True(o <= 0));
        Assert.Contains(0, target.SoakedOffsets);                  // stock itself was the last attempt
    }

    /// <summary>A thermal abort DURING the soak is not a stability verdict: the sweep stops as thermal, restores,
    /// and does not present the offset as stable (nor as unstable).</summary>
    [Fact]
    public void AThermalAbortDuringTheSoakIsNotAStabilityVerdict()
    {
        var target = new FakeTarget
        {
            SoakBehaviour = (_, _) => SweepProbeResult.Thermal("too hot in the soak"),
        };
        var result = UndervoltSweep.Run(Request(Options() with { ConfirmSoak = true }, [0, 0, -7], D(0, min: -3)),
            target, null, CancellationToken.None);

        Assert.Equal(SweepStop.ThermalAbort, result.Stop);
        var d = Assert.Single(result.Domains);
        Assert.Null(d.FirstFailing);                               // no offset was called unstable
        Assert.False(d.Stable);
        Assert.False(d.Soaked);
        AssertRestoredTo(target, [0, 0, -7]);
    }

    /// <summary>The soak is CANCELLABLE: a cancel observed in the soak ends the sweep as Cancelled, restores, and
    /// the offset is not claimed stable.</summary>
    [Fact]
    public void TheSoakIsCancellableAndStillRestores()
    {
        using var cts = new CancellationTokenSource();
        var target = new FakeTarget
        {
            // The fake cancels the token and reports the load run as Cancelled, exactly as the real runner does
            // when the caller's token is set mid-soak.
            SoakBehaviour = (_, _) => { cts.Cancel(); return SweepProbeResult.Cancelled(); },
        };
        var result = UndervoltSweep.Run(Request(Options() with { ConfirmSoak = true }, [0, 0, -7], D(0, min: -3)),
            target, null, cts.Token);

        Assert.Equal(SweepStop.Cancelled, result.Stop);
        Assert.False(result.Domains[0].Stable);
        AssertRestoredTo(target, [0, 0, -7]);
    }

    /// <summary>WHEN THE SOAK IS OFF the search's result is the proposal and no soak runs, so a caller that wants
    /// the old, shorter behaviour gets it.</summary>
    [Fact]
    public void WithConfirmSoakOffNoSoakRuns()
    {
        var target = new FakeTarget();
        var result = UndervoltSweep.Run(Request(Options() with { ConfirmSoak = false }, [0, 0, -7], D(0, min: -3)),
            target, null, CancellationToken.None);

        Assert.Empty(target.SoakedOffsets);
        Assert.False(result.Domains[0].Soaked);
        Assert.Equal(-3, result.Domains[0].Proposed);
    }

    /// <summary>The soak emits the CONFIRMING phase with the settled offset, so the UI can say "confirming".</summary>
    [Fact]
    public void TheSoakReportsTheConfirmingPhase()
    {
        var target = new FakeTarget();
        var seen = new List<SweepProgress>();
        UndervoltSweep.Run(Request(Options() with { ConfirmSoak = true }, [0, 0, -7], D(0, min: -2)),
            target, p => seen.Add(p), CancellationToken.None);

        Assert.Contains(seen, p => p.Phase == SweepPhase.Confirming && p.Offset == -2);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>The AC-gate refusal shape: no domains, the dedicated stop, and no proposal — so a caller can
    /// never read it as a verdict or a value.</summary>
    [Fact]
    public void TheNotOnAcRefusalCarriesNoProposalAndItsOwnStop()
    {
        var refusal = SweepResult.NotOnAc("needs AC");

        Assert.Equal(SweepStop.NotOnAc, refusal.Stop);
        Assert.Empty(refusal.Domains);
        Assert.False(refusal.HasProposal);
        Assert.Equal("needs AC", refusal.Detail);
    }
}
