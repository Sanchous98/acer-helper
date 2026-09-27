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

    // Offline-derived goldens (see the class remarks).
    private const ulong GoldenN0 = 0x0007AAA0F5C17D53UL;
    private const ulong GoldenN1 = 0x80BF4929185C18FEUL;
    private const ulong GoldenN16 = 0x03AEBD25DD28C8D9UL;
    private const ulong GoldenN256 = 0xB37FADBF366FF157UL;

    private static readonly LoadWidth[] AllWidths = [LoadWidth.Auto, LoadWidth.Scalar, LoadWidth.Vector128, LoadWidth.Vector256];

    // ================= the kernel =================

    /// <summary>The algorithm is pinned at a small count, at EVERY width: the scalar and both SIMD paths must
    /// return the same number, which is what makes the width only an instruction-mix knob and never an oracle
    /// difference. A change to the recurrence or the fold reddens all four rows.</summary>
    [Theory]
    [InlineData(LoadWidth.Auto)]
    [InlineData(LoadWidth.Scalar)]
    [InlineData(LoadWidth.Vector128)]
    [InlineData(LoadWidth.Vector256)]
    public void TheChecksumIsStableAtPinnedCounts(LoadWidth width)
        => Assert.Equal(GoldenN16, CpuStressKernel.Compute(width, 16));

    [Fact]
    public void TheChecksumIsStableAcrossCounts()
    {
        Assert.Equal(GoldenN0, CpuStressKernel.Compute(LoadWidth.Scalar, 0));
        Assert.Equal(GoldenN1, CpuStressKernel.Compute(LoadWidth.Scalar, 1));
        Assert.Equal(GoldenN256, CpuStressKernel.Compute(LoadWidth.Scalar, 256));
    }

    /// <summary>The runtime oracle's own constant matches the algorithm it is compared against — pinned through
    /// the same offline literal, so the constant and the loop cannot drift apart unnoticed.</summary>
    [Fact]
    public void TheGoldenMatchesTheAlgorithmAtTheDefaultBlock()
    {
        Assert.Equal(0x6117C64471DCD42EUL, CpuStressKernel.Compute(LoadWidth.Scalar, CpuStressKernel.DefaultBlockIterations));
        Assert.All(AllWidths, w => Assert.Equal(CpuStressKernel.GoldenChecksum(w),
            CpuStressKernel.Compute(w, CpuStressKernel.DefaultBlockIterations)));
    }

    /// <summary>The oracle rejects a value that is NOT the golden, however close — the whole point of comparing
    /// against a pinned value rather than against a recomputation on the same core.</summary>
    [Fact]
    public void TheOracleRejectsAPerturbedValue()
    {
        var actual = CpuStressKernel.Compute(LoadWidth.Scalar, 16);

        Assert.True(CpuStressKernel.Matches(actual, GoldenN16));
        Assert.False(CpuStressKernel.Matches(actual ^ 1UL, GoldenN16));                       // a bit flip
        Assert.False(CpuStressKernel.Matches(actual, GoldenN16 ^ 1UL));                       // a wrong golden
        Assert.False(CpuStressKernel.Matches(actual, 0UL));
    }

    /// <summary>Cancellation is cooperative and checked inside the loop, so a cancelled kernel returns
    /// promptly instead of running the block out.</summary>
    [Fact]
    public void TheKernelIsCancellable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => CpuStressKernel.Compute(LoadWidth.Scalar, 1 << 20, cts.Token));
    }

    /// <summary>A negative count is a programming error, refused rather than silently treated as zero.</summary>
    [Fact]
    public void ANegativeIterationCountIsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => CpuStressKernel.Compute(LoadWidth.Scalar, -1));

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

        public CoreTopology Topology() => TopologyResult;

        public bool PinCurrentThread(LogicalCore core, out string? error)
        {
            Pinned.Add(core);
            var (ok, reason) = PinBehaviour?.Invoke(core) ?? (true, null);
            error = reason;
            return ok;
        }

        public void UnpinCurrentThread() => Unpins++;
    }

    private static CpuLoadOptions Tiny(TimeSpan? perCore = null) => new()
    {
        Width = LoadWidth.Scalar,
        PerCoreBudget = perCore ?? TimeSpan.FromMilliseconds(40),
        TotalBudget = TimeSpan.FromSeconds(10),
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
            kernel: (width, _, _) => CpuStressKernel.GoldenChecksum(width) ^ 1UL);

        Assert.Equal(LoadOutcome.ChecksumMismatch, run.Cores[0].Outcome);
        Assert.Contains("expected", run.Cores[0].Detail);
        Assert.Equal(LoadOutcome.Skipped, run.Cores[1].Outcome);
        Assert.Equal(CpuLoadStop.StoppedOnError, run.Stop);
        Assert.Single(affinity.Pinned);
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
            kernel: (width, _, _) => CpuStressKernel.GoldenChecksum(width) ^ 1UL);

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
            options, CancellationToken.None, kernel: (width, _, _) => CpuStressKernel.GoldenChecksum(width));

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
            options, CancellationToken.None, kernel: (width, _, _) => CpuStressKernel.GoldenChecksum(width));

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
