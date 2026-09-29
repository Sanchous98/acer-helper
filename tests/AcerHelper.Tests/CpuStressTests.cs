using System.Runtime.CompilerServices;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;
using Device = AcerHelper.Infrastructure.Composition.Device;

namespace AcerHelper.Tests;

/// <summary>
/// THE LOAD TOOL'S ORACLE AND ITS RUNNER, offline. Everything here drives the pure kernel, the pure policy and
/// the runner through a fake affinity and a fake temperature delegate, so no core is actually loaded beyond a few
/// tens of milliseconds and no OS affinity call is made by the behavioural tests. The affinity P/Invoke itself is
/// deliberately thin and is only guard-tested for topology (a harmless read); pinning is left to hardware
/// validation and said so in the report.
///
/// THE GOLDEN VALUES ARE INDEPENDENT. They were derived offline from the recurrence (a standalone program, not a
/// call into this assembly) and are pinned here as literals, so the oracle does not verify itself: a systematic
/// miscompute on the tested core cannot agree with a number the compiler embedded.
/// </summary>
public class CpuStressTests
{
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    // Offline-derived goldens (see the class remarks), one set per mix. All were computed by a standalone
    // program, never by this assembly.
    private const ulong GoldenN0 = 0x0007AAA0F5C17D53UL;
    private const ulong GoldenN1 = 0x80BF4929185C18FEUL;
    private const ulong GoldenN16 = 0x03AEBD25DD28C8D9UL;
    private const ulong GoldenN256 = 0xB37FADBF366FF157UL;
    private const ulong GoldenBlockInteger = 0x6117C64471DCD42EUL;

    private const ulong FpN0 = 0xD1EEB58C24C05425UL;
    private const ulong FpN1 = 0x0054EE2FE075B066UL;
    private const ulong FpN16 = 0xA4CF0E007EDF67EEUL;
    private const ulong FpN256 = 0x3DBB47633F032B91UL;
    private const ulong FpBlock = 0xD2C0E3F4104CC4A7UL;

    private const ulong MemN0 = 0xD4E673981DCF8F87UL;
    private const ulong MemN16 = 0x6E1DED986AFEE9E9UL;
    private const ulong MemN256 = 0x6D90146D868AE609UL;
    private const ulong MemBlock = 0x594564883F846141UL;

    private static readonly LoadWidth[] AllWidths =
        [LoadWidth.Auto, LoadWidth.Scalar, LoadWidth.Vector128, LoadWidth.Vector256, LoadWidth.Vector512];

    private static readonly LoadMix[] AllMixes = [LoadMix.Integer, LoadMix.FpFma, LoadMix.Memory];

    // ================= the kernel =================

    /// <summary>The INTEGER algorithm is pinned at a small count, at EVERY width: the scalar and every SIMD path
    /// must return the same number, which is what makes the width only an instruction-mix knob and never an oracle
    /// difference. A change to the recurrence or the fold reddens all rows.</summary>
    [Theory]
    [InlineData(LoadWidth.Auto)]
    [InlineData(LoadWidth.Scalar)]
    [InlineData(LoadWidth.Vector128)]
    [InlineData(LoadWidth.Vector256)]
    [InlineData(LoadWidth.Vector512)]
    public void TheIntegerChecksumIsStableAtPinnedCounts(LoadWidth width)
        => Assert.Equal(GoldenN16, CpuStressKernel.Compute(LoadMix.Integer, width, 16));

    [Fact]
    public void TheIntegerChecksumIsStableAcrossCounts()
    {
        Assert.Equal(GoldenN0, CpuStressKernel.Compute(LoadMix.Integer, LoadWidth.Scalar, 0));
        Assert.Equal(GoldenN1, CpuStressKernel.Compute(LoadMix.Integer, LoadWidth.Scalar, 1));
        Assert.Equal(GoldenN256, CpuStressKernel.Compute(LoadMix.Integer, LoadWidth.Scalar, 256));
    }

    /// <summary>THE FP/FMA MIX IS EXACT AND WIDTH-INDEPENDENT. The same pinned literals must come out of every
    /// width, and the FMA and non-FMA paths must agree, because all the values are exactly representable integers
    /// (see the kernel's exactness argument). A mutation that let rounding in would redden a row.</summary>
    [Theory]
    [InlineData(LoadWidth.Auto)]
    [InlineData(LoadWidth.Scalar)]
    [InlineData(LoadWidth.Vector128)]
    [InlineData(LoadWidth.Vector256)]
    [InlineData(LoadWidth.Vector512)]
    public void TheFpChecksumIsExactAtEveryWidth(LoadWidth width)
    {
        Assert.Equal(FpN16, CpuStressKernel.Compute(LoadMix.FpFma, width, 16));
        Assert.Equal(FpBlock, CpuStressKernel.Compute(LoadMix.FpFma, width, CpuStressKernel.DefaultBlockIterations));
    }

    [Fact]
    public void TheFpChecksumIsStableAcrossCounts()
    {
        Assert.Equal(FpN0, CpuStressKernel.Compute(LoadMix.FpFma, LoadWidth.Scalar, 0));
        Assert.Equal(FpN1, CpuStressKernel.Compute(LoadMix.FpFma, LoadWidth.Scalar, 1));
        Assert.Equal(FpN256, CpuStressKernel.Compute(LoadMix.FpFma, LoadWidth.Scalar, 256));
    }

    /// <summary>FMA vs SEPARATE MULTIPLY-ADD: the two paths are the same computation on exact-valued doubles, so
    /// they must agree bit-for-bit at every width. This is the property that lets the oracle use `==` on a mix
    /// that actually exercises the fused units.</summary>
    [Theory]
    [InlineData(LoadWidth.Scalar)]
    [InlineData(LoadWidth.Vector128)]
    [InlineData(LoadWidth.Vector256)]
    [InlineData(LoadWidth.Vector512)]
    public void TheFmaAndNonFmaFpPathsAgree(LoadWidth width)
    {
        foreach (var n in new[] { 0, 1, 16, 256 })
            Assert.Equal(
                CpuStressKernel.ComputeFp(width, n, useFma: false),
                CpuStressKernel.ComputeFp(width, n, useFma: true));
    }

    /// <summary>THE MEMORY MIX is deterministic and pinned; the width is irrelevant to it (it is a pointer walk).
    /// Two calls in a row and every width must give the same literal.</summary>
    [Theory]
    [InlineData(LoadWidth.Scalar)]
    [InlineData(LoadWidth.Vector512)]
    public void TheMemoryChecksumIsDeterministicAndPinned(LoadWidth width)
    {
        Assert.Equal(MemN16, CpuStressKernel.Compute(LoadMix.Memory, width, 16));
        Assert.Equal(MemN0, CpuStressKernel.Compute(LoadMix.Memory, width, 0));
        Assert.Equal(MemN256, CpuStressKernel.Compute(LoadMix.Memory, width, 256));
        Assert.Equal(MemBlock, CpuStressKernel.Compute(LoadMix.Memory, width, CpuStressKernel.DefaultBlockIterations));
        Assert.Equal(CpuStressKernel.Compute(LoadMix.Memory, width, 1024),
                     CpuStressKernel.Compute(LoadMix.Memory, width, 1024));   // repeat is identical
    }

    /// <summary>EVERY mix agrees with its own pinned golden at the default block, by construction, at every
    /// width. This is the oracle's contract: the runtime constant and the algorithm cannot drift apart, and a
    /// width never changes the answer.</summary>
    [Fact]
    public void EveryMixMatchesItsGoldenAtTheDefaultBlock()
    {
        foreach (var mix in AllMixes)
            foreach (var width in AllWidths)
                Assert.Equal(CpuStressKernel.GoldenChecksum(mix, width),
                    CpuStressKernel.Compute(mix, width, CpuStressKernel.DefaultBlockIterations));
    }

    /// <summary>The integer mix's width-only golden overload is the same value as the mix-aware one, so the
    /// runners that only know the width keep the exact same oracle.</summary>
    [Fact]
    public void TheIntegerGoldenIsTheSameThroughBothOverloads()
    {
        Assert.Equal(0x6117C64471DCD42EUL, CpuStressKernel.Compute(LoadMix.Integer, LoadWidth.Scalar, CpuStressKernel.DefaultBlockIterations));
        Assert.All(AllWidths, w => Assert.Equal(CpuStressKernel.GoldenChecksum(w), CpuStressKernel.GoldenChecksum(LoadMix.Integer, w)));
    }

    /// <summary>The oracle rejects a value that is NOT the golden, however close — the whole point of comparing
    /// against a pinned value rather than against a recomputation on the same core.</summary>
    [Fact]
    public void TheOracleRejectsAPerturbedValue()
    {
        var actual = CpuStressKernel.Compute(LoadMix.Integer, LoadWidth.Scalar, 16);

        Assert.True(CpuStressKernel.Matches(actual, GoldenN16));
        Assert.False(CpuStressKernel.Matches(actual ^ 1UL, GoldenN16));                       // a bit flip
        Assert.False(CpuStressKernel.Matches(actual, GoldenN16 ^ 1UL));                       // a wrong golden
        Assert.False(CpuStressKernel.Matches(actual, 0UL));
        Assert.False(CpuStressKernel.Matches(FpBlock ^ 1UL, FpBlock));                        // no tolerance anywhere
        Assert.False(CpuStressKernel.Matches(MemBlock ^ 1UL, MemBlock));
    }

    /// <summary>Cancellation is cooperative and checked inside every mix's loop, so a cancelled kernel returns
    /// promptly instead of running the block out.</summary>
    [Fact]
    public void TheKernelIsCancellable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => CpuStressKernel.Compute(LoadMix.Integer, LoadWidth.Scalar, 1 << 20, cts.Token));
        Assert.Throws<OperationCanceledException>(() => CpuStressKernel.Compute(LoadMix.FpFma, LoadWidth.Scalar, 1 << 20, cts.Token));
        Assert.Throws<OperationCanceledException>(() => CpuStressKernel.Compute(LoadMix.Memory, LoadWidth.Scalar, 1 << 20, cts.Token));
    }

    /// <summary>The default mix set is all three, in a fixed order — the runner falls back to it, so it is the
    /// shipped oracle's definition.</summary>
    [Fact]
    public void TheDefaultMixesAreAllThree()
        => Assert.Equal([LoadMix.Integer, LoadMix.FpFma, LoadMix.Memory], CpuStressKernel.DefaultMixes);

    /// <summary>A negative count is a programming error, refused rather than silently treated as zero.</summary>
    [Fact]
    public void ANegativeIterationCountIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuStressKernel.Compute(LoadMix.Integer, LoadWidth.Scalar, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuStressKernel.Compute(LoadMix.FpFma, LoadWidth.Scalar, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuStressKernel.Compute(LoadMix.Memory, LoadWidth.Scalar, -1));
    }

    // ================= the policy =================

    [Theory]
    [InlineData(-1, ThermalDecision.Abort)]       // unreadable: fail closed, never "assume cool"
    [InlineData(0, ThermalDecision.Proceed)]
    [InlineData(94, ThermalDecision.Proceed)]
    [InlineData(95, ThermalDecision.CoolDown)]    // at the limit
    [InlineData(96, ThermalDecision.CoolDown)]
    public void TheThermalPolicyFailsClosed(int temperature, ThermalDecision expected)
        => Assert.Equal(expected, CpuLoadPolicy.DecideThermal(temperature, 95));

    [Fact]
    public void CoresAreOrderedByGroupThenProcessorAndDeduplicated()
    {
        IReadOnlyList<LogicalCore> input = [new LogicalCore(5, 1), new LogicalCore(1, 0), new LogicalCore(5, 1), new LogicalCore(0, 0)];
        Assert.Equal([new LogicalCore(0, 0), new LogicalCore(1, 0), new LogicalCore(5, 1)], CpuLoadPolicy.OrderCores(input));
    }

    /// <summary>The sentinel is the literal the primary constructor defaults to, so the two cannot drift: a core
    /// built without a class must read as unknown, and the sentinel is negative so no real class collides.</summary>
    [Fact]
    public void ACoreWithoutAClassIsUnknown()
    {
        Assert.Equal(LogicalCore.NoEfficiencyClass, new LogicalCore(3, 0).EfficiencyClass);
        Assert.Equal(-1, LogicalCore.NoEfficiencyClass);
    }

    /// <summary>THE CLUSTER RULE, offline: with exactly two efficiency classes the HIGHER one is the performance
    /// cluster. This is the single place the "higher class = faster core" convention lives, shared by the Windows
    /// (EfficiencyClass) and Linux (cpu_capacity) halves.</summary>
    [Fact]
    public void TheHigherEfficiencyClassIsThePerformanceCluster()
    {
        // 4 performance cores (class 1) and 2 efficiency cores (class 0) — the Strix Point shape, at a small scale.
        IReadOnlyList<LogicalCore> input =
        [
            new LogicalCore(0, 0, 1), new LogicalCore(2, 0, 1), new LogicalCore(4, 0, 1), new LogicalCore(6, 0, 1),
            new LogicalCore(8, 0, 0), new LogicalCore(10, 0, 0),
        ];

        var (performance, efficiency) = CpuLoadPolicy.PartitionIntoClusters(input);

        Assert.NotNull(performance);
        Assert.NotNull(efficiency);
        Assert.Equal([0, 2, 4, 6], performance!.Select(c => c.Processor));
        Assert.Equal([8, 10], efficiency!.Select(c => c.Processor));
    }

    /// <summary>The fallback: one unknown core, or a single class (homogeneous or unreadable), yields NO clusters
    /// rather than a guessed split — which is what makes the sweep's all-cores fallback reachable.</summary>
    [Fact]
    public void NoClusterPartitionWhenTheClassesCannotBeRead()
    {
        Assert.Equal((null, null), CpuLoadPolicy.PartitionIntoClusters([]));
        Assert.Equal((null, null), CpuLoadPolicy.PartitionIntoClusters(
            [new LogicalCore(0, 0, 1), new LogicalCore(2, 0)]));                       // one core with no class
        Assert.Equal((null, null), CpuLoadPolicy.PartitionIntoClusters(
            [new LogicalCore(0, 0, 0), new LogicalCore(2, 0, 0)]));                   // one class only
        Assert.Equal((null, null), CpuLoadPolicy.PartitionIntoClusters(
            [new LogicalCore(0, 0, 1), new LogicalCore(2, 0, 2), new LogicalCore(4, 0, 0)]));   // three classes
    }

    /// <summary>The runner can be narrowed to a caller-supplied subset (the swept cluster's cores): ONLY those
    /// cores are pinned, and the result holds only them.</summary>
    [Fact]
    public void TheRunnerVisitsOnlyTheCallerSuppliedSubset()
    {
        var affinity = new FakeAffinity
        {
            TopologyResult = new(
                [new LogicalCore(0, 0), new LogicalCore(1, 0), new LogicalCore(2, 0), new LogicalCore(3, 0)],
                CoreTopologySource.PhysicalCores, null),
        };

        var run = CpuLoadRunner.Run(affinity, () => 40, Tiny(), CancellationToken.None,
            kernel: null, onCore: null, cores: [new LogicalCore(1, 0), new LogicalCore(3, 0)]);

        Assert.Equal([new LogicalCore(1, 0), new LogicalCore(3, 0)], affinity.Pinned);
        Assert.Equal(2, run.Cores.Count);
        Assert.All(run.Cores, r => Assert.Equal(LoadOutcome.Passed, r.Outcome));
    }

    [Theory]
    [InlineData(LoadOutcome.Passed, false)]
    [InlineData(LoadOutcome.Skipped, false)]
    [InlineData(LoadOutcome.ThermalAbort, false)]
    [InlineData(LoadOutcome.Cancelled, false)]
    [InlineData(LoadOutcome.ChecksumMismatch, true)]
    [InlineData(LoadOutcome.Faulted, true)]
    public void OnlyChecksumAndFaultAreErrors(LoadOutcome outcome, bool expected)
        => Assert.Equal(expected, CpuLoadPolicy.IsError(outcome));

    [Fact]
    public void StopOnErrorIsTheConfiguredSwitch()
    {
        var mismatch = new CoreLoadResult(new LogicalCore(0, 0), LoadOutcome.ChecksumMismatch, LoadWidth.Scalar, 0, 0, 0, 0, null);
        var passed = new CoreLoadResult(new LogicalCore(0, 0), LoadOutcome.Passed, LoadWidth.Scalar, 0, 0, 0, 0, null);

        Assert.True(CpuLoadPolicy.ShouldStopForError(mismatch, stopOnFirstError: true));
        Assert.False(CpuLoadPolicy.ShouldStopForError(mismatch, stopOnFirstError: false));
        Assert.False(CpuLoadPolicy.ShouldStopForError(passed, stopOnFirstError: true));
    }

    // ================= the runner =================

    private sealed class FakeAffinity : ICoreAffinity
    {
        public CoreTopology TopologyResult { get; set; } =
            new([new LogicalCore(0, 0)], CoreTopologySource.PhysicalCores, null);

        public List<LogicalCore> Pinned { get; } = [];
        public int Unpins { get; private set; }
        public Func<LogicalCore, (bool ok, string? error)>? PinBehaviour { get; set; }

        // How many threads are pinned at once, and the high-water mark. This is the observable that distinguishes
        // "cores overlapped" from "cores were walked one at a time".
        public int MaxLive { get; private set; }
        private int _live;

        public CoreTopology Topology() => TopologyResult;

        public bool PinCurrentThread(LogicalCore core, out string? error)
        {
            lock (Pinned) Pinned.Add(core);
            var live = System.Threading.Interlocked.Increment(ref _live);
            lock (Pinned) MaxLive = Math.Max(MaxLive, live);
            var (ok, reason) = PinBehaviour?.Invoke(core) ?? (true, null);
            error = reason;
            return ok;
        }

        public void UnpinCurrentThread()
        {
            Unpins++;
            System.Threading.Interlocked.Decrement(ref _live);
        }
    }

    // The runner's OWN default is ConcurrentCores = true; these tests keep the sequential walk (the
    // single-core coverage) unless a test asks for concurrency explicitly, so their ordering assertions stay
    // meaningful. The sweep probe's default is set on SweepOptions.Load separately.
    private static CpuLoadOptions Tiny(TimeSpan? perCore = null, bool concurrent = false) => new()
    {
        Width = LoadWidth.Scalar,
        PerCoreBudget = perCore ?? TimeSpan.FromMilliseconds(40),
        TotalBudget = TimeSpan.FromSeconds(10),
        ConcurrentCores = concurrent,
    };

    /// <summary>The happy path: every core passed, in deterministic order, each pinned once and unpinned once,
    /// and the checksum oracle ran for real (iterations were counted).</summary>
    [Fact]
    public void EachCoreIsPinnedInOrderAndPasses()
    {
        var affinity = new FakeAffinity
        {
            TopologyResult = new([new LogicalCore(2, 0), new LogicalCore(0, 0)], CoreTopologySource.PhysicalCores, null),
        };

        var run = CpuLoadRunner.Run(affinity, () => 40, Tiny(), CancellationToken.None);

        Assert.Equal([new LogicalCore(0, 0), new LogicalCore(2, 0)], affinity.Pinned);
        Assert.Equal(2, affinity.Unpins);
        Assert.Equal(2, run.Cores.Count);
        Assert.All(run.Cores, r => Assert.Equal(LoadOutcome.Passed, r.Outcome));
        Assert.All(run.Cores, r => Assert.True(r.Iterations > 0, "a passing core must have run at least one block"));
        Assert.True(run.TotalIterations > 0);
        Assert.Equal(CpuLoadStop.Completed, run.Stop);
        Assert.Equal(CoreTopologySource.PhysicalCores, run.TopologySource);
    }

    /// <summary>Stop-on-error (the default): the first mismatch ends the run and the remaining cores are
    /// recorded as skipped, not silently dropped.</summary>
    [Fact]
    public void TheRunStopsOnTheFirstErrorByDefault()
    {
        var affinity = new FakeAffinity
        {
            TopologyResult = new([new LogicalCore(0, 0), new LogicalCore(1, 0)], CoreTopologySource.PhysicalCores, null),
        };

        var run = CpuLoadRunner.Run(affinity, () => 40, Tiny(TimeSpan.FromMilliseconds(20)), CancellationToken.None,
            kernel: (mix, width, _, _) => CpuStressKernel.GoldenChecksum(mix, width) ^ 1UL);

        Assert.Equal(LoadOutcome.ChecksumMismatch, run.Cores[0].Outcome);
        Assert.Contains("expected", run.Cores[0].Detail);
        Assert.Equal(LoadMix.Integer, run.Cores[0].FailedMix);      // the first mix to fail is named
        Assert.Equal(LoadOutcome.Skipped, run.Cores[1].Outcome);
        Assert.Equal(CpuLoadStop.StoppedOnError, run.Stop);
        Assert.Single(affinity.Pinned);
    }

    /// <summary>THE MIX THAT FAILED IS NAMED. A kernel that corrupts only the FP/FMA mix must surface exactly
    /// that mix in the result (and in the detail), not a generic failure — that is the whole point of running
    /// several mixes.</summary>
    [Fact]
    public void TheFailingMixIsNamedInTheResult()
    {
        var affinity = new FakeAffinity();
        var run = CpuLoadRunner.Run(affinity, () => 40, Tiny(TimeSpan.FromMilliseconds(20)), CancellationToken.None,
            kernel: (mix, width, _, _) => mix == LoadMix.FpFma
                ? CpuStressKernel.GoldenChecksum(mix, width) ^ 1UL
                : CpuStressKernel.GoldenChecksum(mix, width));

        Assert.Equal(LoadOutcome.ChecksumMismatch, run.Cores[0].Outcome);
        Assert.Equal(LoadMix.FpFma, run.Cores[0].FailedMix);
        Assert.Contains("FpFma", run.Cores[0].Detail);
    }

    /// <summary>A run over only ONE mix (a configured subset) never runs the others — so the mix set is a real
    /// switch, not decoration.</summary>
    [Fact]
    public void OnlyTheConfiguredMixesRun()
    {
        var seen = new List<LoadMix>();
        var affinity = new FakeAffinity();
        var options = Tiny(TimeSpan.FromMilliseconds(20)) with { Mixes = [LoadMix.Memory] };

        CpuLoadRunner.Run(affinity, () => 40, options, CancellationToken.None,
            kernel: (mix, width, _, _) =>
            {
                lock (seen) seen.Add(mix);
                return CpuStressKernel.GoldenChecksum(mix, width);
            });

        Assert.NotEmpty(seen);
        Assert.All(seen, m => Assert.Equal(LoadMix.Memory, m));
    }

    /// <summary>Continue-on-error, the configured alternative: every core is visited and its own error reported,
    /// and the run itself completes.</summary>
    [Fact]
    public void ContinueOnErrorVisitsEveryCore()
    {
        var affinity = new FakeAffinity
        {
            TopologyResult = new([new LogicalCore(0, 0), new LogicalCore(1, 0)], CoreTopologySource.PhysicalCores, null),
        };
        var options = Tiny(TimeSpan.FromMilliseconds(20)) with { StopOnFirstError = false };

        var run = CpuLoadRunner.Run(affinity, () => 40, options, CancellationToken.None,
            kernel: (mix, width, _, _) => CpuStressKernel.GoldenChecksum(mix, width) ^ 1UL);

        Assert.Equal(2, run.Cores.Count);
        Assert.All(run.Cores, r => Assert.Equal(LoadOutcome.ChecksumMismatch, r.Outcome));
        Assert.Equal(CpuLoadStop.Completed, run.Stop);
        Assert.Equal(2, affinity.Pinned.Count);
    }

    /// <summary>An unreadable temperature is fatal and fails closed: the first core aborts, the rest are skipped,
    /// and the run says which reason it was.</summary>
    [Fact]
    public void AnUnreadableTemperatureFailsClosed()
    {
        var affinity = new FakeAffinity
        {
            TopologyResult = new([new LogicalCore(0, 0), new LogicalCore(1, 0)], CoreTopologySource.PhysicalCores, null),
        };

        var run = CpuLoadRunner.Run(affinity, cpuTemperatureC: null, Tiny(TimeSpan.FromMilliseconds(20)), CancellationToken.None);

        Assert.Equal(LoadOutcome.ThermalAbort, run.Cores[0].Outcome);
        Assert.Contains("unreadable", run.Cores[0].Detail);
        Assert.Equal(LoadOutcome.Skipped, run.Cores[1].Outcome);
        Assert.Equal(CpuLoadStop.SensorUnreadable, run.Stop);
        Assert.Equal(0, run.TotalIterations);
    }

    /// <summary>A temperature over the limit aborts rather than loading a hot core; when the cooldown would
    /// overrun the core's budget there is no point waiting.</summary>
    [Fact]
    public void AnOverLimitTemperatureAborts()
    {
        var affinity = new FakeAffinity();
        var options = Tiny(TimeSpan.FromMilliseconds(20)) with { ThermalLimitC = 30, CoolDown = TimeSpan.FromMilliseconds(500) };

        var run = CpuLoadRunner.Run(affinity, () => 50, options, CancellationToken.None);

        Assert.Equal(LoadOutcome.ThermalAbort, run.Cores[0].Outcome);
        Assert.Equal(CpuLoadStop.ThermalAbort, run.Stop);
        Assert.Equal(0, run.TotalIterations);
    }

    /// <summary>THE SAMPLER MAKES A RISING TEMPERATURE OBSERVABLE. The load thread reads a cached value, so the
    /// proof that thermal safety survives moving the read off-thread is that a provider which starts cool and
    /// turns over-limit is picked up and aborts the core within the poll interval — not only at the first block.
    /// Without the background sampler the cached value would stay at the initial cool reading and the run would
    /// complete instead of aborting.</summary>
    [Fact]
    public void ARisingTemperatureIsPickedUpByTheSamplerAndAborts()
    {
        var calls = 0;
        var affinity = new FakeAffinity();
        var options = Tiny(TimeSpan.FromSeconds(5)) with
        {
            ThermalLimitC = 45,
            CoolDown = TimeSpan.FromMilliseconds(500),
            TemperaturePollInterval = TimeSpan.FromMilliseconds(25),
        };

        // First read (the sampler's priming sample) is cool; every later read is over the limit.
        var run = CpuLoadRunner.Run(affinity, () => Interlocked.Increment(ref calls) == 1 ? 40 : 50,
            options, CancellationToken.None);

        Assert.Equal(LoadOutcome.ThermalAbort, run.Cores[0].Outcome);
        Assert.Equal(CpuLoadStop.ThermalAbort, run.Stop);
        Assert.True(calls > 1, "the sampler must have re-read the provider after the priming sample");
    }

    /// <summary>A token cancelled before the run does no hardware work at all — every core is a skip and nothing
    /// is pinned.</summary>
    [Fact]
    public void CancellationBeforeTheRunDoesNoWork()
    {
        var affinity = new FakeAffinity
        {
            TopologyResult = new([new LogicalCore(0, 0), new LogicalCore(1, 0)], CoreTopologySource.PhysicalCores, null),
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var run = CpuLoadRunner.Run(affinity, () => 40, Tiny(), cts.Token);

        Assert.Equal(CpuLoadStop.Cancelled, run.Stop);
        Assert.All(run.Cores, r => Assert.Equal(LoadOutcome.Skipped, r.Outcome));
        Assert.Empty(affinity.Pinned);
    }

    /// <summary>A refused pin is a skip, not an error, and does not stop the run — the next core still gets its
    /// chance. Every worker still releases what it took.</summary>
    [Fact]
    public void ARefusedPinSkipsTheCoreWithoutStoppingTheRun()
    {
        var affinity = new FakeAffinity
        {
            TopologyResult = new([new LogicalCore(0, 0), new LogicalCore(1, 0)], CoreTopologySource.PhysicalCores, null),
            PinBehaviour = _ => (false, "the platform refused the pin"),
        };

        var run = CpuLoadRunner.Run(affinity, () => 40, Tiny(TimeSpan.FromMilliseconds(20)), CancellationToken.None);

        Assert.All(run.Cores, r => Assert.Equal(LoadOutcome.Skipped, r.Outcome));
        Assert.All(run.Cores, r => Assert.Contains("refused", r.Detail));
        Assert.Equal(CpuLoadStop.Completed, run.Stop);
        Assert.Equal(2, affinity.Unpins);
    }

    /// <summary>
    /// THE TEMPERATURE PROVIDER IS NOT CALLED ON THE LOAD THREAD IN THE STEADY STATE. It is a firmware/WMI
    /// round-trip that on Windows costs tens of milliseconds; calling it between kernel blocks parked the pinned
    /// core and is the "нагрузка низкая / one core is not loaded" report. This records the thread that got pinned
    /// and the thread that called the provider and asserts they are disjoint for a normal (never-over-limit) run —
    /// the sampler owns the reads, the core does not. (The ONE sanctioned exception is the fresh re-read after a
    /// cool-down, which only happens once the core is already over the limit and pausing.) The mutation —
    /// restoring the per-block read — reddens this at once.
    /// </summary>
    [Fact]
    public void TheTemperatureProviderIsNeverCalledOnTheLoadThread()
    {
        int? loadThread = null;
        var providerThreads = new List<int>();

        var affinity = new FakeAffinity
        {
            PinBehaviour = _ => { loadThread = Environment.CurrentManagedThreadId; return (true, null); },
        };
        var options = Tiny(TimeSpan.FromMilliseconds(300)) with { TemperaturePollInterval = TimeSpan.FromMilliseconds(50) };

        var run = CpuLoadRunner.Run(affinity, () => { lock (providerThreads) providerThreads.Add(Environment.CurrentManagedThreadId); return 40; },
            options, CancellationToken.None, kernel: (mix, width, _, _) => CpuStressKernel.GoldenChecksum(mix, width));

        Assert.True(run.Cores[0].Iterations > 0);
        Assert.NotNull(loadThread);
        lock (providerThreads)
        {
            Assert.NotEmpty(providerThreads);
            Assert.DoesNotContain(loadThread!.Value, providerThreads);   // the load thread never calls the provider
        }
    }

    /// <summary>The sampler stops with the run: after <c>Run</c> returns, the provider must not be read again.
    /// A leaked sampler thread would keep calling it, which this catches by waiting past several intervals.</summary>
    [Fact]
    public void TheSamplerStopsWhenTheRunEnds()
    {
        var reads = 0;
        var options = Tiny(TimeSpan.FromMilliseconds(150)) with { TemperaturePollInterval = TimeSpan.FromMilliseconds(20) };

        CpuLoadRunner.Run(new FakeAffinity(), () => { Interlocked.Increment(ref reads); return 40; },
            options, CancellationToken.None, kernel: (mix, width, _, _) => CpuStressKernel.GoldenChecksum(mix, width));

        var settled = Volatile.Read(ref reads);
        Thread.Sleep(300);   // many intervals later
        Assert.Equal(settled, Volatile.Read(ref reads));
    }

    /// <summary>The sampler is fail-closed like the old inline read was: a THROWING provider publishes the
    /// unreadable sentinel, so the first core still aborts with the sensor-unreadable reason.</summary>
    [Fact]
    public void AThrowingTemperatureProviderStillAbortsFailClosed()
    {
        var run = CpuLoadRunner.Run(new FakeAffinity(), () => throw new InvalidOperationException("sensor exploded"),
            Tiny(TimeSpan.FromMilliseconds(20)), CancellationToken.None);

        Assert.Equal(LoadOutcome.ThermalAbort, run.Cores[0].Outcome);
        Assert.Contains("unreadable", run.Cores[0].Detail);
        Assert.Equal(CpuLoadStop.SensorUnreadable, run.Stop);
    }

    // ================= concurrency =================

    /// <summary>THE ALL-CORE DEFAULT. With the default concurrent phase, a four-core run has all four cores
    /// pinned AT THE SAME TIME (the fake's high-water mark reaches four), which one-at-a-time could never do.
    /// The injected kernel makes each block slow enough that the workers genuinely overlap.</summary>
    [Fact]
    public void TheDefaultConcurrentRunLoadsEveryCoreAtOnce()
    {
        var affinity = new FakeAffinity
        {
            TopologyResult = new(
                [new LogicalCore(0, 0), new LogicalCore(1, 0), new LogicalCore(2, 0), new LogicalCore(3, 0)],
                CoreTopologySource.PhysicalCores, null),
        };
        var options = Tiny(TimeSpan.FromMilliseconds(150), concurrent: true) with { Mixes = [LoadMix.Integer] };

        var run = CpuLoadRunner.Run(affinity, () => 40, options, CancellationToken.None,
            kernel: (mix, width, _, _) => { Thread.Sleep(25); return CpuStressKernel.GoldenChecksum(mix, width); });

        Assert.Equal(4, affinity.MaxLive);                       // all four overlapped
        Assert.Equal(4, run.Cores.Count);
        Assert.All(run.Cores, r => Assert.Equal(LoadOutcome.Passed, r.Outcome));
        Assert.Equal(CpuLoadStop.Completed, run.Stop);
    }

    /// <summary>THE ALL-CORE PHASE COVERS THE WHOLE TOPOLOGY, CLUSTERS INCLUDED. The sweep now runs its all-core
    /// phase over every physical core of the machine (the package current/droop the oracle needs), not only a
    /// swept cluster's subset. A topology that carries two clusters must therefore have ALL of its physical cores
    /// pinned AT THE SAME INSTANT — the fake's high-water mark reaches the full core count — when the runner is
    /// given the whole topology. The injected kernel sleeps so the workers genuinely overlap.</summary>
    [Fact]
    public void TheAllCorePhaseCoversEveryPhysicalCoreOfTheTopology()
    {
        // The Strix Point shape at a small scale: 4 performance + 4 efficiency physical cores.
        LogicalCore[] cores =
        [
            new(0, 0, 1), new(2, 0, 1), new(4, 0, 1), new(6, 0, 1),
            new(8, 0, 0), new(10, 0, 0), new(12, 0, 0), new(14, 0, 0),
        ];
        var (performance, efficiency) = CpuLoadPolicy.PartitionIntoClusters(CpuLoadPolicy.OrderCores(cores));
        var affinity = new FakeAffinity
        {
            TopologyResult = new(CpuLoadPolicy.OrderCores(cores), CoreTopologySource.PhysicalCores, null,
                performance, efficiency),
        };
        var options = Tiny(TimeSpan.FromMilliseconds(200), concurrent: true) with { Mixes = [LoadMix.Integer] };

        var run = CpuLoadRunner.Run(affinity, () => 40, options, CancellationToken.None,
            kernel: (mix, width, _, _) => { Thread.Sleep(25); return CpuStressKernel.GoldenChecksum(mix, width); });

        Assert.Equal(8, affinity.MaxLive);                    // every physical core overlapped
        Assert.Equal(8, run.Cores.Count);
        Assert.Equal(cores, affinity.Pinned.OrderBy(c => c.Processor));   // clusters included, none omitted
        Assert.All(run.Cores, r => Assert.Equal(LoadOutcome.Passed, r.Outcome));
    }

    /// <summary>The sequential option really does walk one at a time — the high-water mark is one, which is the
    /// single-core high-boost coverage the concurrent phase cannot provide.</summary>
    [Fact]
    public void TheSequentialOptionLoadsOneCoreAtATime()
    {
        var affinity = new FakeAffinity
        {
            TopologyResult = new(
                [new LogicalCore(0, 0), new LogicalCore(1, 0), new LogicalCore(2, 0), new LogicalCore(3, 0)],
                CoreTopologySource.PhysicalCores, null),
        };
        var options = Tiny(TimeSpan.FromMilliseconds(60), concurrent: false) with { Mixes = [LoadMix.Integer] };

        var run = CpuLoadRunner.Run(affinity, () => 40, options, CancellationToken.None,
            kernel: (mix, width, _, _) => { Thread.Sleep(10); return CpuStressKernel.GoldenChecksum(mix, width); });

        Assert.Equal(1, affinity.MaxLive);
        Assert.All(run.Cores, r => Assert.Equal(LoadOutcome.Passed, r.Outcome));
    }

    /// <summary>STOP-ON-ERROR STOPS THE WHOLE CONCURRENT PHASE. A kernel that fails the second core cancels the
    /// shared token, so the other cores quit (their own result is cancelled) rather than keep loading a machine
    /// that just miscomputed. The run's stop is StoppedOnError and the failed core names its mix.</summary>
    [Fact]
    public void AStopOnErrorInTheConcurrentPhaseEndsTheOtherCores()
    {
        var affinity = new FakeAffinity
        {
            TopologyResult = new(
                [new LogicalCore(0, 0), new LogicalCore(1, 0), new LogicalCore(2, 0), new LogicalCore(3, 0)],
                CoreTopologySource.PhysicalCores, null),
        };
        var options = Tiny(TimeSpan.FromSeconds(5), concurrent: true) with { Mixes = [LoadMix.Integer] };
        var gate = new object();
        var failed = false;

        // Exactly ONE core miscomputes (deterministically, the first into the kernel); the others park on the
        // shared stop and observe the cancellation the mismatch raised. That is the property under test: a single
        // error ends the whole concurrent phase rather than letting the other cores keep loading.
        var run = CpuLoadRunner.Run(affinity, () => 40, options, CancellationToken.None,
            kernel: (mix, width, _, ct) =>
            {
                lock (gate) { if (!failed) { failed = true; return CpuStressKernel.GoldenChecksum(mix, width) ^ 1UL; } }
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(4));
                ct.ThrowIfCancellationRequested();
                return CpuStressKernel.GoldenChecksum(mix, width);
            });

        Assert.Equal(CpuLoadStop.StoppedOnError, run.Stop);
        Assert.Single(run.Cores, r => r.Outcome == LoadOutcome.ChecksumMismatch);
        Assert.All(run.Cores, r => Assert.NotEqual(LoadOutcome.Passed, r.Outcome));
    }

    // ================= the OS adapter guard =================

    /// <summary>
    /// The Windows topology read, exercised for real (a harmless read) so a wrong struct offset does not ship
    /// silently: it must list at least one core and never more cores than there are logical processors. This is
    /// the ONLY part of the affinity adapter the suite touches — the pin itself changes a thread's real affinity
    /// and is left to hardware validation.
    /// </summary>
    [Fact]
    public void TheWindowsTopologyListsPhysicalCores()
    {
        var topology = new CoreAffinity().Topology();

        Assert.NotEmpty(topology.Cores);
        Assert.True(topology.Cores.Count <= Environment.ProcessorCount,
            $"topology listed {topology.Cores.Count} cores but the machine has {Environment.ProcessorCount} logical processors");
        Assert.True(Enum.IsDefined(topology.Source));
        Assert.All(topology.Cores, c => Assert.InRange(c.Processor, 0, 63));
    }

    /// <summary>The generic backend builds the adapter, so a future tuner reads it off the machine exactly the
    /// way it reads the other OS facilities.</summary>
    [Fact]
    public void TheGenericBackendWiresTheAffinityAdapter()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "Infrastructure", "Vendors", "Generic", "GenericDevice.cs"));
        Assert.Contains("CoreAffinity = new CoreAffinity()", source, StringComparison.Ordinal);
        Assert.NotNull(typeof(Device).GetProperty("CoreAffinity"));
    }

    /// <summary>The Linux half is not compiled into this net10.0-windows test project, so it cannot be exercised
    /// here; this reads it (the technique AcerLinuxWiringTests uses) to pin that the mechanism is the kernel's own
    /// affinity call and the kernel's own topology file, not something invented. Behaviour is left to hardware
    /// validation.</summary>
    [Fact]
    public void TheLinuxAffinitySourceUsesTheKernelAffinityAndTopology()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "Infrastructure", "Vendors", "Generic", "CoreAffinity.Linux.cs"));
        Assert.Contains("sched_setaffinity", source, StringComparison.Ordinal);
        Assert.Contains("thread_siblings_list", source, StringComparison.Ordinal);
        // The Linux cluster class is derived from cpu_capacity, and the parse must yield it into the topology.
        Assert.Contains("cpu_capacity", source, StringComparison.Ordinal);
        Assert.Contains("TopologyWithClusters", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE SAMPLER NEVER CALLS <c>Thread.Interrupt</c>. It is unreservedly
    /// <see cref="PlatformNotSupportedException"/> on Windows under Native AOT — the shipped build — so calling it
    /// to "cut the sleep short" on Dispose made EVERY guided sweep fail with "Operation is not supported on this
    /// platform." before a single probe ran. The shutdown is a cooperative <c>_stop</c> flag plus a bounded
    /// <c>Join</c>, and the loop re-checks the flag after each short sleep, so nothing needs interrupting.
    ///
    /// A source guard rather than a behavioural one because the suite runs under the JIT (where <c>Interrupt</c>
    /// works) and can never reproduce the AOT-only throw: the regression is exactly the call coming back, and that
    /// is what this pins. Scope is the load tool's file, which owns the sampler.
    /// </summary>
    [Fact]
    public void TheTemperatureSamplerDoesNotCallThreadInterrupt()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "Infrastructure", "Vendors", "Generic", "CpuLoadTest.cs"));

        Assert.DoesNotContain(".Interrupt(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ThreadInterruptedException", source, StringComparison.Ordinal);
    }

    /// <summary>The Windows half carries the efficiency class out of the PROCESSOR_RELATIONSHIP buffer, which is
    /// the byte that makes the cluster split possible — pinned by source because the parse cannot run offline.</summary>
    [Fact]
    public void TheWindowsAffinitySourceReadsTheEfficiencyClass()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "Infrastructure", "Vendors", "Generic", "CoreAffinity.Windows.cs"));
        Assert.Contains("offset + 8 + 1", source, StringComparison.Ordinal);   // EfficiencyClass, after Flags at +8
        Assert.Contains("TopologyWithClusters", source, StringComparison.Ordinal);
    }
}
