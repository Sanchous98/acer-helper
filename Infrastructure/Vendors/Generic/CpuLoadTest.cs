using System.Diagnostics;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// THE LOAD TEST: contracts, the pure policy, and the runner. It lives in Infrastructure/Vendors/Generic, beside
// the kernel it drives and the per-OS affinity adapter, because the owner ruled the automatic undervolt to be a
// WRAPPER over the manual voltage change (Application/Undervolt.cs) — UI and Infrastructure only, no domain
// logic of its own. The two rules the owner's map cares about are still kept apart from the I/O: the pass/fail,
// core-ordering, thermal and cancellation decisions are pure and offline-testable here, while every P/Invoke,
// sysfs read and thread-affinity call is behind ICoreAffinity in the OS-split files beside it.
//
// THE CONTRACT IS INFRASTRUCTURE'S, NOT A DOMAIN PORT. Per docs/domain-layering-map.md a contract that models an
// OS technology (thread affinity, processor groups, cpusets) is integration; the map's remedy is the
// thin-contract shape Application/Undervolt.cs established (IUndervoltTarget), not a Domain/Ports.cs
// declaration. Nothing in Domain needs to name it, so adding it to Domain/Ports.cs would be the "forced Domain
// port" the task forbids.

/// <summary>Per-OS thread affinity and physical-core topology — the thin adapter the runner drives. Implemented
/// by Infrastructure/Vendors/Generic/CoreAffinity.{Windows,Linux}.cs, one file per OS, nothing else. The runner
/// is written against this and never against a platform API, which is what lets the suite drive the control flow
/// with a fake.</summary>
public interface ICoreAffinity
{
    /// <summary>The cores to test — one logical processor per physical core where the OS can report topology,
    /// otherwise the flat logical list (see <see cref="CoreTopology.Source"/>). Must not throw; a machine whose
    /// topology cannot be read returns the documented fallback.</summary>
    CoreTopology Topology();

    /// <summary>Pin the CALLING thread to one logical processor. False with a reason when the platform refused.
    /// On Windows this also takes the thread-affinity hold; pair it with <see cref="UnpinCurrentThread"/>.</summary>
    bool PinCurrentThread(LogicalCore core, out string? error);

    /// <summary>Release what <see cref="PinCurrentThread"/> took. Safe to call even if the pin failed, and a
    /// no-op where the platform has nothing to release (Linux).</summary>
    void UnpinCurrentThread();
}

/// <summary>Why a run ended, so the caller can tell "finished every core" from every early exit.</summary>
public enum CpuLoadStop
{
    Completed,
    BudgetExhausted,
    StoppedOnError,
    ThermalAbort,
    SensorUnreadable,
    Cancelled,
}

/// <summary>The thermal policy's verdict for one reading.</summary>
public enum ThermalDecision { Proceed, CoolDown, Abort }

/// <summary>Bounds and switches for one run. Every default is conservative: a bounded total and per-core budget,
/// the CoreCycler-style 95 °C cutoff, stop on the first error, below-normal priority, and no periodic suspension
/// (an explicit opt-in).</summary>
public sealed record CpuLoadOptions
{
    /// <summary>Requested instruction width; <see cref="LoadWidth.Auto"/> picks the widest accelerated path and
    /// the effective width is reported in the result.</summary>
    public LoadWidth Width { get; init; } = LoadWidth.Auto;

    /// <summary>Wall-clock bound for the whole run across all cores. The runner also never runs the UI thread.</summary>
    public TimeSpan TotalBudget { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Wall-clock bound for one core.</summary>
    public TimeSpan PerCoreBudget { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional iteration bound across the whole run; <see cref="long.MaxValue"/> means the time budget
    /// alone decides.</summary>
    public long MaxIterations { get; init; } = long.MaxValue;

    /// <summary>Thermal cutoff in °C, checked before every block. An unreadable reading is fatal (fail closed).</summary>
    public double ThermalLimitC { get; init; } = 95.0;

    /// <summary>How long to wait before re-reading the temperature when it is at/over the limit.</summary>
    public TimeSpan CoolDown { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How often the SAMPLER reads the CPU temperature while a run is active, and so the bound on how
    /// stale a reading the pinned load thread may act on. The thermal cutoff only needs to be current on the
    /// timescale a temperature can actually move, while each read on Windows is a firmware/WMI round-trip that can
    /// cost tens of milliseconds. The read is done on the sampler's own thread (see <see cref="CpuLoadRunner"/>),
    /// never on the pinned load thread, so a slow provider cannot leave a core idle — the "one core is not loaded,
    /// the load is too low" symptom this guards. A non-positive value falls back to the default cadence.</summary>
    public TimeSpan TemperaturePollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>CoreCycler's stopOnError default: the first checksum mismatch or fault ends the run.</summary>
    public bool StopOnFirstError { get; init; } = true;

    /// <summary>CoreCycler's suspendPeriodically: deliberately pause the load now and then to hit the idle/load
    /// transitions where a too-aggressive undervolt tends to surface.</summary>
    public bool SuspendPeriodically { get; init; }

    /// <summary>How often to take a suspend pause when <see cref="SuspendPeriodically"/> is on.</summary>
    public TimeSpan SuspendPeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long each suspend pause lasts when <see cref="SuspendPeriodically"/> is on.</summary>
    public TimeSpan SuspendDuration { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Worker priority, below-normal by default so the UI is not starved. Applied on a best-effort
    /// basis; a platform that refuses it does not fail the run.</summary>
    public ThreadPriority Priority { get; init; } = ThreadPriority.BelowNormal;
}

/// <summary>One run's structured result. <see cref="Cores"/> carries one entry per core the run visited, in the
/// order it visited them; a core after an early stop is present as <see cref="LoadOutcome.Skipped"/> rather than
/// absent, so a consumer can tell "not reached" from "not tested".</summary>
public sealed record CpuLoadRunResult(
    IReadOnlyList<CoreLoadResult> Cores,
    CoreTopologySource TopologySource,
    string? TopologyDetail,
    LoadWidth Width,
    long TotalIterations,
    CpuLoadStop Stop,
    double ElapsedMs);

/// <summary>The pure decisions: core ordering, the thermal verdict, and the stop-on-error rule. No I/O, no OS
/// names, no threads — everything here is driven directly by the tests.</summary>
public static class CpuLoadPolicy
{
    /// <summary>The order the runner visits cores: ascending by group then processor, duplicates removed. One
    /// entry per physical core already (the topology's job), so this only makes the visit order deterministic
    /// rather than letting the OS enumeration order leak into results.</summary>
    public static IReadOnlyList<LogicalCore> OrderCores(IReadOnlyList<LogicalCore> cores)
        => cores.Distinct().OrderBy(c => c.Group).ThenBy(c => c.Processor).ToArray();

    /// <summary>Thermal verdict. Unreadable (&lt; 0) ALWAYS aborts — fail closed, never "assume cool" — and a
    /// reading at/over the limit cools down first; if it cannot cool within the core's budget the runner aborts
    /// (see the runner).</summary>
    public static ThermalDecision DecideThermal(int temperatureC, double limitC)
        => temperatureC < 0 ? ThermalDecision.Abort
         : temperatureC >= limitC ? ThermalDecision.CoolDown
         : ThermalDecision.Proceed;

    /// <summary>Whether an outcome is a real error the oracle caught (as opposed to a skip, a thermal abort or a
    /// cancellation, which have their own run-level stop reasons).</summary>
    public static bool IsError(LoadOutcome outcome)
        => outcome is LoadOutcome.ChecksumMismatch or LoadOutcome.Faulted;

    /// <summary>The stop-on-error rule, named so the default and the continue-on-error switch are the same line
    /// the runner uses.</summary>
    public static bool ShouldStopForError(CoreLoadResult result, bool stopOnFirstError)
        => stopOnFirstError && IsError(result.Outcome);

    /// <summary>
    /// Partition <paramref name="cores"/> into the performance and efficiency clusters by the efficiency class
    /// each carries, in the order given (the caller passes them already in visit order). Returns
    /// <c>(null, null)</c> when the partition cannot be made.
    ///
    /// THE RULE. Every core must carry a real class (not <see cref="LogicalCore.NoEfficiencyClass"/>) and
    /// there must be exactly TWO distinct classes; the HIGHER class is the performance cluster. This is the single
    /// place the platform convention ("a higher class is the faster, less power-efficient core") is stated, and it
    /// holds on both operating systems — Windows' <c>PROCESSOR_RELATIONSHIP.EfficiencyClass</c> and Linux's
    /// capacity-derived class both use it — so the OS halves only have to FILL the class and this can be shared
    /// and tested offline.
    ///
    /// WHY NULL RATHER THAN A BEST EFFORT. One unknown core, or a single class on a homogeneous part, means the
    /// machine cannot say which cores belong to which rail; returning a split anyway would let a caller claim it
    /// isolated a cluster it did not. A class at/below the sentinel is treated as unknown for the same reason:
    /// neither platform emits it, so it means the OS did not answer.
    /// </summary>
    public static (IReadOnlyList<LogicalCore>? Performance, IReadOnlyList<LogicalCore>? Efficiency)
        PartitionIntoClusters(IReadOnlyList<LogicalCore> cores)
    {
        if (cores.Count == 0) return (null, null);

        var classes = new HashSet<int>();
        foreach (var core in cores)
        {
            if (core.EfficiencyClass <= LogicalCore.NoEfficiencyClass) return (null, null);
            classes.Add(core.EfficiencyClass);
        }
        if (classes.Count != 2) return (null, null);   // one class: homogeneous or unreadable, not two rails

        var high = classes.Max();
        var performance = cores.Where(c => c.EfficiencyClass == high).ToArray();
        var efficiency = cores.Where(c => c.EfficiencyClass != high).ToArray();
        return (performance, efficiency);
    }
}

/// <summary>
/// Reads the CPU temperature on its OWN background thread and publishes the latest reading for the pinned load
/// thread to read cheaply. THIS IS WHY IT EXISTS: the temperature provider is a firmware/WMI round-trip on
/// Windows (tens of milliseconds), and the load thread must never block on it — a load thread that calls the
/// provider itself spends most of the wall clock parked and the core reads far below 100%.
///
/// It owns exactly ONE sampler thread, started with the run and stopped in the runner's finally. The published
/// value is a plain <see cref="Volatile"/> int read (a few nanoseconds) by the load thread; the sampler writes it
/// after every read. FAIL-CLOSED IS PRESERVED: no provider, a thrown provider and a negative reading all publish
/// the unreadable sentinel (<c>-1</c>), which <see cref="CpuLoadPolicy.DecideThermal"/> aborts on — the load
/// thread only has to see the published value to reach the same verdict, so an unreadable sensor still aborts.
///
/// INITIAL AND EXPLICIT SAMPLES. The constructor takes one synchronous sample so the first core never starts on a
/// stale/default value, and <see cref="SampleNow"/> forces a fresh synchronous read (used after a cool-down, so
/// the load thread re-reads a fresh reading rather than the one that sent it to cool down). A <c>-1</c> is only
/// ever published before the first successful sample or while the provider is genuinely unreadable.
/// </summary>
internal sealed class CpuTemperatureSampler : IDisposable
{
    private readonly Func<int>? _provider;
    private int _value;
    private Thread? _thread;
    private volatile bool _stop;

    /// <summary>Publishes a first sample synchronously (fail closed), then starts the background sampler.</summary>
    internal CpuTemperatureSampler(Func<int>? provider, TimeSpan interval)
    {
        _provider = provider;
        _value = ReadOnce();                       // never start a core on an unprimed value

        if (provider is null) return;              // nothing to poll; the sentinel stands, fail closed
        var period = interval > TimeSpan.Zero ? interval : TimeSpan.FromMilliseconds(500);
        _thread = new Thread(() => Loop(period))
        {
            IsBackground = true,
            Name = "cpu-temp-sampler",
        };
        // THE SAMPLER MUST NEVER OUT-PRIORITIZE THE LOAD. It floats (it is not pinned), so on a busy core a
        // higher-priority sampler could preempt the pinned worker; Lowest removes that possibility while the
        // provider's own blocking means even a loaded machine keeps the cadence.
        try { _thread.Priority = ThreadPriority.Lowest; } catch { /* priority is best-effort */ }
        _thread.Start();
    }

    /// <summary>The latest reading. Cheap: one volatile load, no I/O — safe to read on the pinned load thread.</summary>
    internal int Current => Volatile.Read(ref _value);

    /// <summary>Force a fresh synchronous read and publish it, used when the load thread needs a current reading
    /// it can act on immediately (after a cool-down).</summary>
    internal void SampleNow() => Volatile.Write(ref _value, ReadOnce());

    private void Loop(TimeSpan period)
    {
        while (!_stop)
        {
            try { Thread.Sleep(period); } catch (ThreadInterruptedException) { return; }
            if (_stop) return;
            Volatile.Write(ref _value, ReadOnce());
        }
    }

    /// <summary>Read fail-closed: no provider, a thrown provider or a negative reading all become -1.</summary>
    private int ReadOnce()
    {
        if (_provider is null) return -1;
        try { return _provider(); }
        catch { return -1; }
    }

    public void Dispose()
    {
        _stop = true;
        var thread = _thread;
        if (thread is null) return;
        thread.Interrupt();                        // cut a long Sleep short so the run does not linger
        if (!thread.Join(TimeSpan.FromSeconds(2)))
        {
            // The sampler is blocked in a provider that ignores interruption; it is a background thread and
            // carries no state the run needs, so abandon it rather than block the run's completion.
        }
        _thread = null;
    }
}

/// <summary>
/// Runs the kernel one core at a time. For each core in <see cref="CpuLoadPolicy.OrderCores"/> order it starts a
/// dedicated worker thread (pinning is a property of a thread, so a pool thread would carry the affinity back),
/// pins it through <see cref="ICoreAffinity"/>, and runs repeated blocks of <see cref="CpuStressKernel"/> until
/// the per-core budget, the iteration bound or a stop. The caller runs this off the UI thread —
/// <see cref="RunAsync"/> is provided for that — and cancels through the token, which the kernel checks inside
/// every block.
///
/// WHAT IT CHECKS PER BLOCK: the thermal verdict first (fail closed), then the block's checksum against the
/// pinned golden, then the optional suspend pause. The thermal reading is a cached value published by a
/// <see cref="CpuTemperatureSampler"/> on its own thread — the pinned worker never calls the temperature
/// provider, so a slow WMI/firmware read cannot park the core (see <see cref="CpuLoadOptions.TemperaturePollInterval"/>).
/// A mismatch or a fault is an error; with
/// <see cref="CpuLoadOptions.StopOnFirstError"/> (the default) the run ends and the remaining cores are recorded
/// as skipped, mirroring CoreCycler's stopOnError/skipCoreOnError shape.
/// </summary>
public static class CpuLoadRunner
{
    /// <summary>Run the load test. Blocking; call <see cref="RunAsync"/> from a UI thread. Never throws for a
    /// hardware or platform failure — those are reported in the result.</summary>
    public static CpuLoadRunResult Run(
        ICoreAffinity affinity,
        Func<int>? cpuTemperatureC,
        CpuLoadOptions options,
        CancellationToken cancellation) => Run(affinity, cpuTemperatureC, options, cancellation, kernel: null, onCore: null);

    /// <summary>Non-blocking wrapper for the UI thread.</summary>
    public static Task<CpuLoadRunResult> RunAsync(
        ICoreAffinity affinity,
        Func<int>? cpuTemperatureC,
        CpuLoadOptions options,
        CancellationToken cancellation) => Task.Run(() => Run(affinity, cpuTemperatureC, options, cancellation), cancellation);

    /// <summary>The run, with an optional injected kernel so the checksum-mismatch path is testable on a healthy
    /// machine, an optional per-core hook so a caller (the guided undervolt sweep) can show which core is being
    /// loaded, and an optional caller-supplied core list so that caller can load a SUBSET of the topology (one
    /// cluster's cores) rather than every physical core. A null/empty <paramref name="cores"/> means "the whole
    /// topology", which is the shipped behaviour for every existing caller.</summary>
    internal static CpuLoadRunResult Run(
        ICoreAffinity affinity,
        Func<int>? cpuTemperatureC,
        CpuLoadOptions options,
        CancellationToken cancellation,
        Func<LoadWidth, int, CancellationToken, ulong>? kernel,
        Action<LogicalCore, int, int>? onCore = null,
        IReadOnlyList<LogicalCore>? cores = null)
    {
        ArgumentNullException.ThrowIfNull(affinity);

        var topology = affinity.Topology();
        // The caller may narrow the visit to a subset (the swept cluster); otherwise the whole topology, as before.
        var ordered = cores is { Count: > 0 } ? cores : topology.Cores;
        var coresToVisit = CpuLoadPolicy.OrderCores(ordered);
        var width = CpuStressKernel.Resolve(options.Width);
        var expected = CpuStressKernel.GoldenChecksum(width);

        var results = new List<CoreLoadResult>(coresToVisit.Count);
        var totalIterations = 0L;
        var stop = CpuLoadStop.Completed;
        var clock = Stopwatch.StartNew();

        // THE TEMPERATURE IS READ OFF THE LOAD THREAD. One sampler serves the whole run; the pinned worker only
        // reads the published value (a few ns) and never calls the provider, so a slow WMI/firmware read can never
        // park a core. Disposing it in the finally stops the thread on every exit path.
        using var sampler = new CpuTemperatureSampler(cpuTemperatureC, options.TemperaturePollInterval);

        for (var i = 0; i < coresToVisit.Count; i++)
        {
            if (cancellation.IsCancellationRequested)
            {
                stop = CpuLoadStop.Cancelled;
                AppendSkipped(results, coresToVisit, i, width, expected);
                break;
            }
            if (clock.Elapsed >= options.TotalBudget)
            {
                stop = CpuLoadStop.BudgetExhausted;
                AppendSkipped(results, coresToVisit, i, width, expected);
                break;
            }

            onCore?.Invoke(coresToVisit[i], i, coresToVisit.Count);   // progress only; the caller runs this off the UI thread

            var (result, fatal) = RunOneCore(coresToVisit[i], affinity, sampler, options, width, expected, cancellation, kernel);
            results.Add(result);
            totalIterations += result.Iterations;

            if (fatal is { } stopReason)
            {
                stop = stopReason;
                AppendSkipped(results, coresToVisit, i + 1, width, expected);
                break;
            }
            if (CpuLoadPolicy.ShouldStopForError(result, options.StopOnFirstError))
            {
                stop = CpuLoadStop.StoppedOnError;
                AppendSkipped(results, coresToVisit, i + 1, width, expected);
                break;
            }
        }

        clock.Stop();
        return new CpuLoadRunResult(results, topology.Source, topology.Detail, width, totalIterations, stop, clock.Elapsed.TotalMilliseconds);
    }

    private static (CoreLoadResult Result, CpuLoadStop? Fatal) RunOneCore(
        LogicalCore core,
        ICoreAffinity affinity,
        CpuTemperatureSampler sampler,
        CpuLoadOptions options,
        LoadWidth width,
        ulong expected,
        CancellationToken cancellation,
        Func<LoadWidth, int, CancellationToken, ulong>? kernel)
    {
        CoreLoadResult result = null!;
        CpuLoadStop? fatal = null;
        var executor = kernel ?? (Func<LoadWidth, int, CancellationToken, ulong>)
            ((w, n, ct) => CpuStressKernel.Compute(w, n, ct));

        var worker = new Thread(() =>
        {
            var clock = Stopwatch.StartNew();
            var iterations = 0L;
            var checksum = 0UL;
            var outcome = LoadOutcome.Passed;
            string? detail = null;
            var nextSuspend = options.SuspendPeriod;

            try
            {
                if (!affinity.PinCurrentThread(core, out var pinError))
                {
                    outcome = LoadOutcome.Skipped;
                    detail = pinError ?? "could not pin the thread to this core";
                }
                else
                {
                    while (true)
                    {
                        if (cancellation.IsCancellationRequested)
                        {
                            outcome = LoadOutcome.Cancelled;
                            fatal = CpuLoadStop.Cancelled;
                            break;
                        }
                        if (clock.Elapsed >= options.PerCoreBudget || iterations >= options.MaxIterations) break;

                        // THE THERMAL CHECK IS FREE ON THIS THREAD. The sampler reads the provider on its own
                        // thread; here we only load the published value (a few ns) and apply the same pure policy.
                        // FAIL CLOSED is unchanged: an unreadable sensor is published as -1 and aborts below.
                        var temperature = sampler.Current;
                        switch (CpuLoadPolicy.DecideThermal(temperature, options.ThermalLimitC))
                        {
                            case ThermalDecision.Abort:
                                outcome = LoadOutcome.ThermalAbort;
                                detail = temperature < 0
                                    ? "the CPU temperature is unreadable"
                                    : $"the CPU temperature {temperature} °C is at/over the {options.ThermalLimitC:0.#} °C limit";
                                fatal = temperature < 0 ? CpuLoadStop.SensorUnreadable : CpuLoadStop.ThermalAbort;
                                break;
                            case ThermalDecision.CoolDown:
                                // Wait once; if the pause would overrun the core's budget, stop as a thermal
                                // abort rather than loop forever on a hot core. Then force a FRESH synchronous
                                // sample so the next iteration acts on a current reading, not the stale hot one.
                                if (clock.Elapsed + options.CoolDown >= options.PerCoreBudget)
                                {
                                    outcome = LoadOutcome.ThermalAbort;
                                    detail = $"the CPU temperature {temperature} °C stayed above the {options.ThermalLimitC:0.#} °C limit";
                                    fatal = CpuLoadStop.ThermalAbort;
                                    break;
                                }
                                Thread.Sleep(options.CoolDown);
                                sampler.SampleNow();
                                continue;
                        }

                        if (fatal is not null) break;

                        try
                        {
                            checksum = executor(width, CpuStressKernel.DefaultBlockIterations, cancellation);
                        }
                        catch (OperationCanceledException)
                        {
                            outcome = LoadOutcome.Cancelled;
                            fatal = CpuLoadStop.Cancelled;
                            break;
                        }

                        if (!CpuStressKernel.Matches(checksum, expected))
                        {
                            outcome = LoadOutcome.ChecksumMismatch;
                            detail = $"expected 0x{expected:X16}, computed 0x{checksum:X16}";
                            break;
                        }

                        iterations += CpuStressKernel.DefaultBlockIterations;

                        if (options.SuspendPeriodically && options.SuspendPeriod > TimeSpan.Zero && clock.Elapsed >= nextSuspend)
                        {
                            Thread.Sleep(options.SuspendDuration);
                            nextSuspend += options.SuspendPeriod;
                        }
                    }

                    if (cancellation.IsCancellationRequested && outcome == LoadOutcome.Passed)
                    {
                        outcome = LoadOutcome.Cancelled;
                        fatal = CpuLoadStop.Cancelled;
                    }
                }
            }
            catch (Exception ex)
            {
                outcome = LoadOutcome.Faulted;
                detail = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                affinity.UnpinCurrentThread();
                clock.Stop();
            }

            result = new CoreLoadResult(core, outcome, width, iterations, checksum, expected, clock.Elapsed.TotalMilliseconds, detail);
        })
        {
            IsBackground = true,
            Name = $"cpu-load-{core.Group}:{core.Processor}",
        };

        try { worker.Priority = options.Priority; } catch { /* below-normal is best-effort */ }

        worker.Start();
        worker.Join();
        return (result, fatal);
    }

    private static void AppendSkipped(
        List<CoreLoadResult> results, IReadOnlyList<LogicalCore> cores, int from, LoadWidth width, ulong expected)
    {
        for (var i = from; i < cores.Count; i++)
            results.Add(new CoreLoadResult(cores[i], LoadOutcome.Skipped, width, 0, 0, expected, 0, "not reached"));
    }
}
