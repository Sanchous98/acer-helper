using System.Diagnostics;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// THE GUIDED UNDERVOLT SWEEP (auto-undervolt.md §2c) as a set of pure decisions and a loop, and it sits in
// Infrastructure/Vendors/Generic, NOT Application: the owner ruled the automatic undervolt to be in substance a
// WRAPPER over the manual voltage change (Application/Undervolt.cs), so it carries no independent domain logic
// and is UI + Infrastructure only. It orchestrates the load runner over the CPU-stress kernel and the
// Curve-Optimizer apply, and the decisions that matter (step, stop on the first oracle failure, back off,
// re-verify, when a probe is not a stability verdict) are pure and offline-testable with injected delegates. It
// is deliberately NOT a Domain model: the value it sweeps is a projection of a vendor rail (VoltageDomain, the
// AMD Curve Optimizer), and the "what to do" is a plan of action. Nothing here performs I/O — every SMU write
// and every load run is behind IUndervoltSweepTarget, the thin-contract shape Application/Undervolt.cs
// established.
//
// THE SAFETY MODEL IS THE WHOLE POINT OF THIS FILE, and every clause is enforced here rather than trusted to a
// caller:
//
//  1. PROBES ARE NEVER PERSISTED. There is no persist delegate at all. The only contract is
//     IUndervoltSweepTarget, whose Apply/Restore write the SMU VOLATILELY (the real implementation forwards to
//     ICurveOptimizer.SetDomains and nothing else). A crash or a power cycle mid-sweep therefore cannot leave an
//     aggressive offset in settings.json: a power cycle restores stock, and the store was never touched.
//  2. ANY EXIT RETURNS TO THE PRE-SWEEP STATE. Run's finally calls target.Restore with the pre-sweep counts, so
//     a normal finish, the first error, cancellation, a thermal abort and a thrown delegate all end with the
//     machine back where it started. This is the property the tests are written around.
//  3. THE RESULT IS A PROPOSAL, NOT AN APPLY. Saving is a separate, explicit user action that goes through the
//     existing per-mode settings path (LaptopService.SetCoValues); this file never asks for it.
//  4. BOUNDED AND CANCELLABLE, BUT NOT BY TIME. The walk terminates at the rail's hard floor (domain.Min) and a
//     bounded back-off (MaxBackoffAttempts), and each PROBE is bounded by the load runner's own per-core/thermal
//     limits; a CancellationToken stops the loop between steps. TIME IS NOT A STOP: the only boundary of the
//     sweep's search is an error in the computation (the oracle), never a wall-clock budget — a sweep that is
//     merely slow must finish, so a stable rail is walked all the way to its floor.
//  5. ONE CLUSTER AT A TIME, AND FROM STOCK. Domains are swept independently — one STAGE per CPU cluster — and
//     every OTHER CPU cluster is held at STOCK (0) in every probe, so each rail is tested from a clean baseline
//     and a stored preset that is itself unstable cannot contribute to (or hide behind) a result. A probe loads
//     ONLY the swept cluster's physical cores where the machine identifies them, so a failure belongs to that
//     cluster; where it cannot (the topology carries no cluster classes), it loads all cores and says so in the
//     progress rather than pretending the stage was isolated. The iGPU (or any other non-swept slot) keeps its
//     pre-sweep value, which the index-alignment rule SetDomains needs. The true pre-sweep counts are restored
//     on exit; the stock baseline is probe-only and never persisted.
//
// WHAT IT DELIBERATELY DOES NOT DECIDE. WHICH rails are CPU clusters and WHERE the mV scale and the hard floor
// live are the vendor port's (Infrastructure projects them into SweepDomain); the clamp uses the per-domain
// Range the caller supplied and never a hard-coded -40/-50. This type names no "gfx", no "ccd:" and no
// VoltageDomain.

/// <summary>One rail the sweep may walk — a CPU cluster, projected out of the port's domain list so this layer
/// never names <c>VoltageDomain</c>. <see cref="Index"/> is the slot in the port's FULL domain array, which is
/// what keeps <c>SetDomains</c> index-aligned: the swept slot is overwritten and every other slot (the iGPU
/// included) is carried verbatim.</summary>
public sealed record SweepDomain(
    int Index,
    string Key,
    string Label,
    int Min,
    int Max,
    double? MillivoltsPerCount = null,
    CpuClusterKind? Cluster = null,
    bool ClusterIdentified = true)
{
    /// <summary>The rail's own bounds, with the ceiling never positive — the same rule OffsetCounts states for
    /// every write. A domain whose range the caller did not narrow is the port's, already resolved upstream.</summary>
    public int Clamp(int counts) => Math.Clamp(counts, Math.Min(Min, 0), Math.Min(Max, 0));

    /// <summary>The offset in millivolts, ONLY where this rail's scale is known (null means "unknown for this
    /// rail, show no estimate" — deliberately not inherited from the port's, because the rails differ).</summary>
    public int? Millivolts(int counts) => MillivoltsPerCount is { } mv ? (int)Math.Round(counts * mv) : null;
}

/// <summary>What the sweep is doing, so the UI can say it in the user's language. The words are the UI's; this is
/// the phase only.</summary>
public enum SweepPhase { Applying, Loading, BackingOff, Restoring, Finished }

/// <summary>One progress report, emitted on the sweep's own worker thread. The UI marshals it.
/// <see cref="ClusterIdentified"/> is false when the probe could NOT attribute a physical core to the swept
/// cluster, so it loaded ALL cores — the UI must say "all cores (cluster not identified)" rather than let the
/// stage look isolated when it was not.</summary>
public sealed record SweepProgress(
    SweepPhase Phase,
    int DomainIndex,
    int DomainCount,
    string DomainLabel,
    int Offset,
    int CoreIndex,
    int CoreCount,
    LogicalCore? Core,
    double ElapsedMs,
    int TemperatureC,
    bool ClusterIdentified = true)
{
    /// <summary>The stage number shown as "stage N/M": the domain being swept, 1-based. There is one stage per CPU
    /// cluster (the domain list already carries <see cref="DomainIndex"/>/<see cref="DomainCount"/>), so the UI
    /// says which cluster it is on rather than a bare "core x/y".</summary>
    public int Stage => DomainIndex + 1;
}

/// <summary>The outcome of one volatile SMU write. <c>Ok</c> false aborts the sweep (an unapplied candidate is a
/// broken probe, not a stability verdict) and carries the port's own reason where there is one.</summary>
public sealed record SweepApplyOutcome(bool Ok, string? Error)
{
    public static readonly SweepApplyOutcome Applied = new(true, null);
    public static SweepApplyOutcome Refused(string? error) => new(false, error);
}

/// <summary>What one self-checking load run means for the sweep. <see cref="CoreError"/> is a real oracle
/// failure (checksum mismatch or fault); <see cref="ThermalAbort"/> and <see cref="Cancelled"/> are explicitly
/// NOT verdicts and must not be recorded as one; <see cref="Failed"/> is "the probe could not be run at all".</summary>
public enum SweepProbeStatus { Passed, CoreError, ThermalAbort, Cancelled, Failed }

/// <summary>One probe's result: the status, the core that failed where there was one, and the load runner's own
/// reason.</summary>
public sealed record SweepProbeResult(
    SweepProbeStatus Status,
    LogicalCore? Core,
    LoadOutcome? CoreOutcome,
    string? Detail)
{
    public static SweepProbeResult Pass() => new(SweepProbeStatus.Passed, null, LoadOutcome.Passed, null);
    public static SweepProbeResult Error(LogicalCore? core, LoadOutcome? outcome, string? detail)
        => new(SweepProbeStatus.CoreError, core, outcome, detail);
    public static SweepProbeResult Thermal(string? detail) => new(SweepProbeStatus.ThermalAbort, null, null, detail);
    public static SweepProbeResult Cancelled() => new(SweepProbeStatus.Cancelled, null, null, null);
    public static SweepProbeResult Failed(string? detail) => new(SweepProbeStatus.Failed, null, null, detail);
}

/// <summary>Bounds and switches for one sweep. Every default is conservative: step one count, a one-count safety
/// back-off and a modest per-core load budget. The sweep is finite without any wall-clock bound: it stops at the
/// rail's hard floor, the back-off is capped, and the cancel token stops it between steps; time is deliberately
/// NOT a stop, because the only boundary of the search is an error in the computation.</summary>
public sealed record SweepOptions
{
    /// <summary>Where each domain starts. Null means STOCK (0), clamped into the domain's own range: the sweep
    /// always walks the rail from stock downward, so it covers the whole range and a current preset that is itself
    /// unstable cannot hide behind the starting point. A value set here overrides that and is clamped too.</summary>
    public int? StartCounts { get; init; }

    /// <summary>How many counts one step deepens.</summary>
    public int Step { get; init; } = 1;

    /// <summary>How far back toward stock the sweep retreats from the first failing offset before re-verifying
    /// (the Difference-Offset knob of AMD's own tool). With <see cref="Step"/> = 1 this lands on the last offset
    /// that passed; the re-verify proves it again.</summary>
    public int SafetyMargin { get; init; } = 1;

    /// <summary>How many times the back-off may be re-verified before the sweep falls back to the last value that
    /// provably passed (or stock). Bounded so a pathological rail cannot loop.</summary>
    public int MaxBackoffAttempts { get; init; } = 2;

    /// <summary>The load run each step performs, with its own per-core, thermal and cancellation policy. Kept
    /// modest by default (tens of seconds per core) because a sweep over every physical core multiplies it.</summary>
    public CpuLoadOptions Load { get; init; } = new()
    {
        PerCoreBudget = TimeSpan.FromSeconds(20),
        TotalBudget = TimeSpan.FromMinutes(20),
        ThermalLimitC = 95.0,
    };
}

/// <summary>Why the sweep stopped. <see cref="FirstError"/> is the only "the sweep found the edge" case;
/// every other non-completed value is an early exit whose proposal is what had passed so far (if anything).
/// <see cref="NotOnAc"/> is a REFUSAL that never started: no SMU write and no profile change happened, and it
/// must not be read as a stability verdict (the platform can acknowledge a Curve-Optimizer message on battery
/// while suppressing its effect — the documented <c>0xFD</c> "prerequisites not met" cause — so a probe could
/// "pass" against an offset that was never applied).</summary>
public enum SweepStop
{
    Completed,
    FirstError,
    ThermalAbort,
    Cancelled,
    ApplyFailed,
    NoDomains,
    NotOnAc,
}

/// <summary>One domain's proposal and the evidence behind it. <see cref="Proposed"/> is ALWAYS a value the
/// caller may hand to the user as a candidate — never a claim of proven stability. <see cref="Stable"/> says the
/// proposed offset was itself observed to pass; a false here means the sweep fell back (to stock, usually).</summary>
public sealed record SweepDomainResult(
    SweepDomain Domain,
    int Proposed,
    int? FirstFailing,
    LogicalCore? FailedCore,
    int Passes,
    bool Reverified,
    bool Stable,
    double ElapsedMs,
    string? Detail)
{
    public int? ProposedMillivolts => Domain.Millivolts(Proposed);
    public int? FirstFailingMillivolts => FirstFailing is { } f ? Domain.Millivolts(f) : null;
}

/// <summary>The sweep's whole outcome: one result per domain it reached, why it stopped, and the total time.
/// This is a PROPOSAL — the caller presents it and only persists it on an explicit save.</summary>
public sealed record SweepResult(
    IReadOnlyList<SweepDomainResult> Domains,
    SweepStop Stop,
    double ElapsedMs,
    string? Detail)
{
    public static readonly SweepResult Empty = new([], SweepStop.NoDomains, 0, null);

    /// <summary>The mode key the proposal must be SAVED under — the mode the USER was in when the sweep
    /// started. The sweep forces a temporary performance profile for the run, and even though that is transient
    /// (it never touches the remembered slot) the requirement is pinned structurally: the save targets this key
    /// rather than re-deriving it from a possibly-forced live profile. Null on a result built without a save
    /// path (the pure runner's tests), where <c>SaveUndervoltSweep</c> falls back to the live key.</summary>
    public string? ProposedModeKey { get; init; }

    /// <summary>The AC-gate refusal: a sweep that never started because the machine is not on AC (or its source
    /// is unknown). It carries no domains, so it can never be mistaken for a proposal or a stability verdict —
    /// see <see cref="SweepStop.NotOnAc"/>.</summary>
    public static SweepResult NotOnAc(string? detail = null)
        => new([], SweepStop.NotOnAc, 0, detail);

    public bool HasProposal => Domains.Count > 0;
}

/// <summary>What a sweep needs from whoever owns the SMU and the load tool. THREE members, and the absence of a
/// fourth is the design: there is NO persist member, because a probe offset must never reach the settings graph.
/// <see cref="Apply"/> and <see cref="Restore"/> are volatile writes only (the real target forwards to
/// <c>ICurveOptimizer.SetDomains</c>); <see cref="Probe"/> runs the self-checking load over the physical cores —
/// over ONLY the swept cluster's cores where the machine identifies them, over all cores (and saying so in the
/// progress) where it cannot.</summary>
public interface IUndervoltSweepTarget
{
    /// <summary>Write the FULL index-aligned counts to the SMU, volatile, and report whether the port took them.
    /// Never persists anything.</summary>
    SweepApplyOutcome Apply(IReadOnlyList<int> counts, CancellationToken cancellation);

    /// <summary>Run the self-checking load across the physical cores for the candidate and classify the result.
    /// A thermal abort and a cancellation are NOT stability verdicts; the sweep treats them as early exits.</summary>
    SweepProbeResult Probe(
        SweepDomain domain, int offset, IReadOnlyList<int> counts,
        int domainIndex, int domainCount, Action<SweepProgress>? progress, CancellationToken cancellation);

    /// <summary>Write the pre-sweep counts back, volatile. Called from <see cref="UndervoltSweep.Run"/>'s finally
    /// on every exit path, so "any exit returns to the pre-sweep state" holds even when the loop throws.</summary>
    SweepApplyOutcome Restore(IReadOnlyList<int> counts, CancellationToken cancellation);
}

/// <summary>One sweep's inputs: which domains may be walked, the FULL index-aligned counts the sweep RESTORES to
/// on exit (the pre-sweep preset — the probes themselves never run from it; they run from
/// <see cref="SweepPolicy.StockBaseline"/>, every swept cluster at stock, with the iGPU carried), and the bounds.</summary>
public sealed record SweepRequest(
    IReadOnlyList<SweepDomain> Domains,
    IReadOnlyList<int> BaseCounts,
    SweepOptions Options);

/// <summary>
/// THE PURE DECISIONS, named so each one is pinned directly: the next deeper offset, the back-off toward stock,
/// which probe outcomes are stability verdicts, and the full-array assembly that keeps <c>SetDomains</c>
/// index-aligned (the iGPU slot is carried, never swept). No I/O, no threads, no OS names.
/// </summary>
public static class SweepPolicy
{
    /// <summary>The next step deeper, never past the rail's hard floor. A non-positive step is treated as one.</summary>
    public static int NextOffset(int current, int step, int floor)
        => Math.Max(floor, current - Math.Abs(step == 0 ? 1 : step));

    /// <summary>The offset the sweep retreats to after the first failure: toward stock by the safety margin,
    /// never past stock. A non-positive margin is treated as one.</summary>
    public static int BackOff(int firstFailing, int margin, int ceiling)
        => Math.Min(Math.Min(ceiling, 0), firstFailing + Math.Abs(margin == 0 ? 1 : margin));

    /// <summary>Whether a probe's result is evidence about the offset. `Passed` and `CoreError` are; a thermal
    /// abort, a cancellation and a probe that could not run at all are not, and must not be recorded as one.</summary>
    public static bool IsStabilityVerdict(SweepProbeStatus status)
        => status is SweepProbeStatus.Passed or SweepProbeStatus.CoreError;

    /// <summary>The baseline every probe is built from: the pre-sweep array with EVERY swept (CPU-cluster) slot
    /// held at STOCK (0), and every non-swept slot (the iGPU, and any future non-cluster rail) carried verbatim.
    /// This is what makes each rail tested from a clean baseline — a stored preset on a sibling cluster is never
    /// live while this cluster is probed, and it is never what the probe's failure is attributed to.</summary>
    public static int[] StockBaseline(IReadOnlyList<int> baseCounts, IReadOnlyList<SweepDomain> domains)
    {
        var counts = baseCounts.ToArray();
        foreach (var d in domains)
            if (d.Index >= 0 && d.Index < counts.Length) counts[d.Index] = 0;
        return counts;
    }

    /// <summary>The counts one probe is given: the STOCK baseline with the swept domain's own slot replaced.
    /// Every other CPU cluster stays at stock and the iGPU is carried verbatim — the index-alignment rule
    /// <c>SetDomains</c> needs, tested here rather than trusted to the caller.</summary>
    public static int[] WithOffset(IReadOnlyList<int> baseline, SweepDomain domain, int offset)
    {
        var counts = baseline.ToArray();
        if (domain.Index >= 0 && domain.Index < counts.Length) counts[domain.Index] = domain.Clamp(offset);
        return counts;
    }
}

/// <summary>
/// The sweep loop. It walks each domain deeper one step at a time while the self-checking load passes, stops
/// the DEEPENING of that rail on its first oracle failure, backs off and re-verifies the safer value, and then
/// moves to the next rail — each cluster is walked independently, which is what makes its failure attributable.
/// Thermal abort, cancellation and an apply refusal stop the WHOLE sweep, because none of them is a verdict about
/// the offset; a first error is recorded for its rail but does NOT end the sweep, and there is no time budget to
/// exhaust — the walk ends only at the rail's floor. In a finally no exit path can skip, the pre-sweep counts go
/// back to the SMU. It returns a PROPOSAL and writes nothing to settings. See the file header for the whole safety
/// model.
/// </summary>
public static class UndervoltSweep
{
    public static SweepResult Run(
        SweepRequest request,
        IUndervoltSweepTarget target,
        Action<SweepProgress>? progress,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);

        if (request.Domains.Count == 0) return SweepResult.Empty;

        var clock = Stopwatch.StartNew();
        var results = new List<SweepDomainResult>();
        var stop = SweepStop.Completed;
        string? detail = null;

        // Every probe is built from this: all swept CPU clusters at STOCK, the iGPU carried. Computed once, so
        // probing cluster B never runs with cluster A's stored preset live.
        var baseline = SweepPolicy.StockBaseline(request.BaseCounts, request.Domains);

        try
        {
            for (var di = 0; di < request.Domains.Count; di++)
            {
                if (cancellation.IsCancellationRequested) { stop = SweepStop.Cancelled; break; }

                var (domainResult, domainStop, domainDetail) = SweepOneDomain(
                    request, baseline, target, progress, cancellation, di, clock);

                results.Add(domainResult);
                // A rail's own first failure is NOT terminal: each cluster is swept independently (that is what
                // makes a failure attributable), so the next cluster still gets its proposal. Only a genuinely
                // GLOBAL stop — thermal, cancel or an apply refusal — ends the sweep; every other outcome
                // (a first error, or a completed walk to the floor) lets the loop move to the next cluster.
                if (domainResult.FirstFailing is not null) stop = SweepStop.FirstError;
                if (domainStop is SweepStop.ThermalAbort or SweepStop.Cancelled or SweepStop.ApplyFailed)
                {
                    stop = domainStop;
                    detail ??= domainDetail;
                    break;
                }
            }
        }
        finally
        {
            // THE GUARANTEE: whatever happened — a normal finish, the first error, a thermal abort, a
            // cancellation, an apply refusal, or a delegate that THREW — the pre-sweep counts go back to the
            // SMU. Volatile only; nothing here can persist. A throw from the restore itself is swallowed so the
            // original failure is the one that surfaces, but the attempt is always made.
            try { target.Restore(request.BaseCounts, cancellation); } catch { /* best-effort restore */ }
        }

        clock.Stop();
        return new SweepResult(results, stop, clock.Elapsed.TotalMilliseconds, detail);
    }

    private static (SweepDomainResult Result, SweepStop Stop, string? Detail) SweepOneDomain(
        SweepRequest request,
        IReadOnlyList<int> baseline,
        IUndervoltSweepTarget target,
        Action<SweepProgress>? progress,
        CancellationToken cancellation,
        int domainIndex,
        Stopwatch totalClock)
    {
        var domain = request.Domains[domainIndex];
        var options = request.Options;
        var domainClock = Stopwatch.StartNew();

        var start = options.StartCounts is { } configured
            ? domain.Clamp(configured)
            : domain.Clamp(0);   // stock by default: walk the whole rail, not from the current preset

        var value = start;
        var passes = 0;
        int? bestPassed = null;
        int? firstFail = null;
        LogicalCore? failCore = null;
        var stop = SweepStop.Completed;
        string? detail = null;
        string? outcomeDetail = null;

        // ---- the forward sweep: deeper while the oracle passes ----
        while (true)
        {
            if (cancellation.IsCancellationRequested) { stop = SweepStop.Cancelled; break; }

            var counts = SweepPolicy.WithOffset(baseline, domain, value);
            progress?.Invoke(new SweepProgress(SweepPhase.Applying, domainIndex, request.Domains.Count,
                domain.Label, value, 0, 0, null, totalClock.Elapsed.TotalMilliseconds, -1, domain.ClusterIdentified));

            var apply = target.Apply(counts, cancellation);
            if (!apply.Ok)
            {
                stop = SweepStop.ApplyFailed; detail ??= apply.Error; break;
            }

            var probe = target.Probe(domain, value, counts, domainIndex, request.Domains.Count, progress, cancellation);
            if (probe.Status == SweepProbeStatus.Cancelled) { stop = SweepStop.Cancelled; break; }
            if (probe.Status == SweepProbeStatus.Failed) { stop = SweepStop.ApplyFailed; detail ??= probe.Detail; break; }
            if (probe.Status == SweepProbeStatus.ThermalAbort) { stop = SweepStop.ThermalAbort; detail ??= probe.Detail; break; }
            if (probe.Status == SweepProbeStatus.CoreError)
            {
                firstFail = value; failCore = probe.Core; outcomeDetail = probe.Detail;
                stop = SweepStop.FirstError;
                break;
            }

            passes++;
            bestPassed = value;
            if (value <= domain.Min) break;                       // the rail's hard floor, reached while passing
            value = SweepPolicy.NextOffset(value, options.Step, domain.Min);
        }

        // ---- the back-off: from the first failing offset toward stock, then re-verify once (bounded) ----
        var proposed = bestPassed ?? start;
        var reverified = false;
        if (firstFail is { } failing && stop is SweepStop.FirstError or SweepStop.Completed)
        {
            var candidate = SweepPolicy.BackOff(failing, options.SafetyMargin, domain.Max);
            var max = Math.Max(1, options.MaxBackoffAttempts);
            for (var attempt = 0; attempt < max; attempt++)
            {
                if (cancellation.IsCancellationRequested) { stop = SweepStop.Cancelled; break; }

                var counts = SweepPolicy.WithOffset(baseline, domain, candidate);
                progress?.Invoke(new SweepProgress(SweepPhase.BackingOff, domainIndex, request.Domains.Count,
                    domain.Label, candidate, 0, 0, null, totalClock.Elapsed.TotalMilliseconds, -1, domain.ClusterIdentified));

                var apply = target.Apply(counts, cancellation);
                if (!apply.Ok) { stop = SweepStop.ApplyFailed; detail ??= apply.Error; break; }

                var probe = target.Probe(domain, candidate, counts, domainIndex, request.Domains.Count, progress, cancellation);
                if (probe.Status == SweepProbeStatus.Cancelled) { stop = SweepStop.Cancelled; break; }
                if (probe.Status == SweepProbeStatus.Failed) { stop = SweepStop.ApplyFailed; detail ??= probe.Detail; break; }
                if (probe.Status == SweepProbeStatus.ThermalAbort) { stop = SweepStop.ThermalAbort; detail ??= probe.Detail; break; }
                if (probe.Status == SweepProbeStatus.Passed)
                {
                    proposed = candidate; reverified = true;
                    break;
                }

                // It failed again: retreat another margin, and stop once there is nowhere safer to go.
                var next = SweepPolicy.BackOff(candidate, options.SafetyMargin, domain.Max);
                if (next <= candidate) break;
                candidate = next;
            }

            if (!reverified && stop is SweepStop.FirstError or SweepStop.Completed)
            {
                // Nothing above the failing offset verified. The safe answer is the last offset that PASSED, or
                // stock where even the start failed — never the offset that just failed the oracle.
                proposed = bestPassed ?? 0;
                detail ??= "the backed-off offset did not verify; proposing the last offset that passed, or stock";
            }
        }

        var stable = reverified || (proposed == bestPassed && bestPassed is not null);
        if (!stable && firstFail is null) detail ??= outcomeDetail;

        var result = new SweepDomainResult(
            domain, proposed, firstFail, failCore, passes, reverified, stable,
            domainClock.Elapsed.TotalMilliseconds, detail);

        // The rail's first failure is reported in its result but does not stop the sweep; thermal, cancel and an
        // apply refusal ARE global stops. This method reports only what happened on ITS rail; the caller decides
        // whether the stop is global (see Run).
        return (result, stop, detail);
    }

    /// <summary>A rough, deliberately pessimistic ETA for the UI: every probe pays the full per-core budget for
    /// every core it loads, and every domain (STAGE) is walked from its start to its floor plus the back-off
    /// probes. <paramref name="coresPerStage"/> is, per domain, the number of cores that stage's probe will load
    /// (the cluster's subset where identified, otherwise all physical cores), so the ETA tracks the real load
    /// rather than always assuming every core in every stage. It is the honest estimate of a FULL walk — there is
    /// no total budget to clamp it to, so it may be large; it is meant to be honest about the order of magnitude,
    /// not exact.</summary>
    public static TimeSpan RoughEta(IReadOnlyList<SweepDomain> domains, IReadOnlyList<int> baseCounts,
                                    SweepOptions options, IReadOnlyList<int> coresPerStage)
    {
        if (domains.Count == 0) return TimeSpan.Zero;
        var cost = TimeSpan.Zero;
        for (var i = 0; i < domains.Count; i++)
        {
            var domain = domains[i];
            var cores = i < coresPerStage.Count ? Math.Max(1, coresPerStage[i]) : 1;
            var start = options.StartCounts is { } configured
                ? configured
                : 0;   // the default start is stock, so the sweep walks the whole rail
            var depth = Math.Max(0, start - domain.Min);
            var perDomainProbes = depth / Math.Max(1, Math.Abs(options.Step)) + 2 + options.MaxBackoffAttempts;
            cost += options.Load.PerCoreBudget * cores * perDomainProbes;
        }
        return cost;
    }
}
