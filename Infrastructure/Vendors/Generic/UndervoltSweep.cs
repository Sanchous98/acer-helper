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
//     existing per-mode settings path (LaptopService.SaveCoValues); this file never asks for it.
//  4. BOUNDED AND CANCELLABLE, BUT NOT BY TIME. The walk terminates at the rail's hard floor (domain.Min) and a
//     bounded back-off (MaxBackoffAttempts), and each PROBE is bounded by the load runner's own per-core/thermal
//     limits; a CancellationToken stops the loop between steps. TIME IS NOT A STOP: the only boundary of the
//     sweep's search is an error in the computation (the oracle), never a wall-clock budget — a sweep that is
//     merely slow must finish, so a stable rail is walked all the way to its floor.
//  5a. THE PROBE IS STRONGER NOW, AND THE PROPOSAL MORE CONSERVATIVE. Each offset is probed Repetitions times.
//      EVERY probe runs the SINGLE-CORE phase FIRST — the light one-core-at-a-time walk over the swept cluster's
//      cores (where identified; all cores otherwise, and the progress says so), the CoreCycler high-boost corner —
//      and only then the ALL-CORE phase: the load runner's full exact mix set (integer + FP/FMA + memory) over
//      EVERY physical core of the machine CONCURRENTLY, the package current/droop corner. The single-core phase
//      RUNS FOR EVERY probe where it is enabled, never skipped because the all-core phase was merely
//      inconclusive, and the two verdicts are combined fail-closed (SweepPolicy.CombineProbes: an all-core pass
//      cannot mask a single-core error). ORDER MATTERS FOR COST: a single-core real error, thermal abort or
//      cancellation is discovered BEFORE the much longer all-core phase is paid for, so the probe returns then
//      and there. The weak one-core-one-integer-pass probe is what let a sweep walk to -40 and crash. After the
//      first failure the proposal backs off by SafetyMargin (default 3, not 1), so it sits below the edge. This
//      is all still volatile-only and still restore-on-every-exit.
//  5b. THE SEARCH FINDS THE EDGE; A LONG SOAK CONFIRMS THE PROPOSAL. The per-offset probes above are deliberately
//      short — their job is to find the edge quickly, and lengthening every step would turn a full-rail walk into
//      many hours. They are NOT the evidence a value is presented as stable on. Once a cluster has settled on a
//      candidate (either the floor it walked to, or the value its back-off re-verified), the loop runs a FINAL
//      SOAK CONFIRMATION on that exact offset: the same phase order as the search (single-core first, then the
//      all-core concurrent pass), but with the much longer budgets — ConfirmSingleCore (default 3 min per core)
//      and ConfirmAllCore (default 5 min per core). Only a soak PASS makes the offset stable. A soak FAILURE
//      retreats toward stock by SafetyMargin and re-soaks, up to MaxBackoffAttempts; if nothing above stock
//      verifies, the loop proposes the last offset any soak passed (or stock) with Stable = false — never the
//      offset that just failed. The soak is cancellable, thermal-safe (fail-closed, the same policy as the
//      search), progress-reporting (SweepPhase.Confirming) and rides the same never-persist /
//      restore-on-every-exit model.
//
//  5. ONE CLUSTER AT A TIME, AND FROM STOCK. Domains are swept independently — one STAGE per CPU cluster — and
//     every OTHER CPU cluster is held at STOCK (0) in every probe, so each rail is tested from a clean baseline
//     and a stored preset that is itself unstable cannot contribute to (or hide behind) a result. A probe runs
//     its SINGLE-CORE phase FIRST — the swept cluster's physical cores one at a time where the machine
//     identifies them (all cores otherwise, and the progress says so rather than pretending the stage was
//     isolated), the CoreCycler boost corner — and then its ALL-CORE phase, which loads EVERY physical core of
//     the machine at once (that is what creates the package current/droop the oracle needs). The SWEPT cluster is
//     the only one whose offset moved — the others are held at stock by the baseline, so a failure is still
//     attributable to the swept cluster. The iGPU (or any other non-swept slot) keeps its pre-sweep value, which
//     the index-alignment rule SetDomains needs. The true pre-sweep counts are restored on exit; the stock
//     baseline is probe-only and never persisted.
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
/// the phase only. A probe reports <see cref="SingleCore"/> FIRST and <see cref="Loading"/> SECOND: the light
/// one-core-at-a-time phase precedes the heavy all-core burst.
///
/// <see cref="Loading"/> is the ALL-CORE phase (every physical core of the machine loaded at once — the package
/// current/droop the oracle needs). <see cref="SingleCore"/> is the one-core-at-a-time phase on the swept
/// cluster's cores (the CoreCycler high-boost corner), and it is a DISTINCT phase on purpose: the two phases used
/// to share <see cref="Loading"/>, so the card could not say which one was running and a single-core check that
/// walked one core at a time was indistinguishable from the all-core load. <see cref="Confirming"/> is the long
/// final soak's all-core pass; its single-core half reports <see cref="SingleCore"/> like the search's does.</summary>
public enum SweepPhase { Applying, Loading, SingleCore, BackingOff, Confirming, Restoring, Finished }

/// <summary>One progress report, emitted on the sweep's own worker thread. The UI marshals it.
/// <see cref="ClusterIdentified"/> is true when the machine could attribute physical cores to the swept cluster,
/// so the SINGLE-CORE phase walked exactly those; false when it could not, so the single-core phase walked ALL
/// cores — the UI must say "all cores (cluster not identified)" rather than let the stage look isolated when it
/// was not. (The all-core phase always loads every physical core regardless; this flag is about the single-core
/// phase's scope and the honesty of the label.)</summary>
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

/// <summary>WHICH KIND OF LOAD RUN A PROBE IS. <see cref="Search"/> is the short per-offset probe whose job is to
/// find the edge quickly; <see cref="Soak"/> is the long, once-per-settled-domain confirmation whose job is to
/// decide whether the proposal may be called stable. The classification of a run is identical for both — only the
/// budgets differ (the pure loop owns when the soak runs; the target owns the numbers).</summary>
public enum SweepProbeMode { Search, Soak }

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
/// back-off, a modest per-core load budget for the short search probes, and a long final soak confirmation whose
/// budgets are materially larger. The sweep is finite without any wall-clock bound: it stops at the rail's hard
/// floor, the back-off is capped, and the cancel token stops it between steps; time is deliberately NOT a stop,
/// because the only boundary of the search is an error in the computation.</summary>
public sealed record SweepOptions
{
    /// <summary>Where each domain starts. Null means STOCK (0), clamped into the domain's own range: the sweep
    /// always walks the rail from stock downward, so it covers the whole range and a current preset that is itself
    /// unstable cannot hide behind the starting point. A value set here overrides that and is clamped too.</summary>
    public int? StartCounts { get; init; }

    /// <summary>How many counts one step deepens.</summary>
    public int Step { get; init; } = 1;

    /// <summary>How far back toward stock the sweep retreats from the first failing offset before re-verifying
    /// (the Difference-Offset knob of AMD's own tool). The default is DELIBERATELY larger than
    /// <see cref="Step"/>: the proposal then sits a couple of counts BELOW the last offset that merely passed,
    /// rather than on the exact edge where mild instability was one unlucky run away. The re-verify proves the
    /// backed-off value again.</summary>
    public int SafetyMargin { get; init; } = 3;

    /// <summary>How many times the back-off may be re-verified before the sweep falls back to the last value that
    /// provably passed (or stock). Bounded so a pathological rail cannot loop.</summary>
    public int MaxBackoffAttempts { get; init; } = 2;

    /// <summary>HOW MANY INDEPENDENT PROBE RUNS EACH OFFSET GETS, all of which must pass. Mild undervolt
    /// instability is probabilistic and load/dwell dependent, so one short pass is a weak witness; two (the
    /// default) materially raise the chance a genuinely marginal offset fails at least once. A non-positive
    /// value is treated as one. This multiplies every offset's cost — see <see cref="UndervoltSweep.RoughEta"/>.</summary>
    public int Repetitions { get; init; } = 2;

    /// <summary>WHETHER THE PROBE ALSO RUNS A SINGLE-CORE PHASE. The all-core concurrent phase creates the
    /// package current/droop a marginal undervolt fails under, but it cannot reach the single-core boost corner
    /// (with every core loaded, none boosts high). This phase loads one logical core at a time, which is the
    /// CoreCycler case the other phase misses. It is on by default because the two catch different failures; the
    /// cost is bounded by <see cref="SingleCoreBudget"/> per core and by the run's cancellation.</summary>
    public bool IncludeSingleCorePhase { get; init; } = true;

    /// <summary>Per-core wall-clock bound for the single-core SEARCH phase. Smaller than the load's own per-core
    /// budget because the phase runs one core at a time and so multiplies the offset cost by the cluster's core
    /// count; it is enough to reach the light-load boost corner, not a soak — the long
    /// <see cref="ConfirmSingleCore"/> is the real high-boost evidence, this is the quick probe that finds the
    /// edge. RAISED from 5 s, then from 10 s: at 5 s a core had barely reached its top boost bin before the dwell
    /// ended, and at 10 s the check was still reported as too short, so 30 s is the meaningful high-boost window
    /// while keeping a full walk tractable. A non-positive value with <see cref="IncludeSingleCorePhase"/> on is
    /// simply "no single-core search".</summary>
    public TimeSpan SingleCoreBudget { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>WHETHER THE FINAL SOAK CONFIRMATION RUNS AT ALL. The per-offset probes are a short SEARCH that
    /// finds the edge; the soak is the long CONFIRMATION that decides whether the settled offset may be presented
    /// as stable. On by default — without it the search's short dwell is the only evidence, which is the weakness
    /// this exists to fix. Turning it off restores the old "the search's result is the proposal" behaviour and
    /// makes the walk much shorter; it does not change the safety model (still volatile-only).</summary>
    public bool ConfirmSoak { get; init; } = true;

    /// <summary>Per-core wall-clock bound for the soak's ALL-CORE concurrent phase. DELIBERATELY much longer than
    /// the search probe's <see cref="CpuLoadOptions.PerCoreBudget"/> (30 s): the soak is what raises the chance a
    /// mildly unstable offset manifests, and 5 minutes per core is the low end of the owner's suggested 3–5
    /// minutes. It multiplies the cost of each settled domain, not of every step (see
    /// <see cref="UndervoltSweep.RoughEta"/>).</summary>
    public TimeSpan ConfirmAllCore { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Per-core wall-clock bound for the soak's SINGLE-CORE phase, run one core at a time so the loaded
    /// core can boost to its highest frequency on the lowest curve voltage — the CoreCycler corner the all-core
    /// phase cannot reach. DELIBERATELY much longer than the search's <see cref="SingleCoreBudget"/> (30 s) and
    /// applied to every physical core of the swept cluster, so a soak costs this much per core; 3 minutes is the
    /// low end of the owner's suggested range. A non-positive value with <see cref="IncludeSingleCorePhase"/>
    /// off is simply "no single-core soak". Reuses <see cref="IncludeSingleCorePhase"/> to decide whether the
    /// single-core half runs at all, so search and soak agree about the phases.</summary>
    public TimeSpan ConfirmSingleCore { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>The load run each step performs, with its own mix set, concurrency, per-core, thermal and
    /// cancellation policy. Materially stronger than the original single-integer-kernel, one-core-at-a-time,
    /// one-pass probe: all three mixes, all cores concurrent, a 30 s per-core dwell, and <see cref="Repetitions"/>
    /// passes per offset. A full all-core sweep of a ten-core part is therefore many tens of minutes — the UI
    /// estimate (<see cref="UndervoltSweep.RoughEta"/>) says so, and time remains NOT a stop.</summary>
    public CpuLoadOptions Load { get; init; } = new()
    {
        PerCoreBudget = TimeSpan.FromSeconds(30),
        TotalBudget = TimeSpan.FromMinutes(20),
        ThermalLimitC = 95.0,
        ConcurrentCores = true,
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
/// proposed offset passed the search AND, where the soak was enabled, the long final confirmation:
/// <see cref="Soaked"/> records whether the soak actually ran and passed for this proposal, so a caller can tell
/// "the long soak confirmed it" from "the search merely did not fail it". A false <see cref="Stable"/> means the
/// sweep fell back (to stock, or to the last value any soak passed).</summary>
public sealed record SweepDomainResult(
    SweepDomain Domain,
    int Proposed,
    int? FirstFailing,
    LogicalCore? FailedCore,
    int Passes,
    bool Reverified,
    bool Stable,
    double ElapsedMs,
    string? Detail,
    bool Soaked = false)
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
/// <c>ICurveOptimizer.SetDomains</c>); <see cref="Probe"/> runs the self-checking load over every physical core
/// of the machine concurrently (the swept cluster carries the tested offset, every other core is held at stock),
/// with the optional single-core phase over only the swept cluster's cores where the machine identifies them —
/// over all cores, and saying so in the progress, where it cannot.</summary>
public interface IUndervoltSweepTarget
{
    /// <summary>Write the FULL index-aligned counts to the SMU, volatile, and report whether the port took them.
    /// Never persists anything.</summary>
    SweepApplyOutcome Apply(IReadOnlyList<int> counts, CancellationToken cancellation);

    /// <summary>Run the self-checking load across the physical cores for the candidate and classify the result.
    /// A thermal abort and a cancellation are NOT stability verdicts; the sweep treats them as early exits.
    /// <paramref name="mode"/> selects the SEARCH dwell or the long soak confirmation; the pure loop owns WHEN
    /// to soak, this method only owns the budgets. Where the mode is <see cref="SweepProbeMode.Soak"/> the
    /// implementation must use the much longer confirmation budgets (<see cref="SweepOptions.ConfirmAllCore"/> /
    /// <see cref="SweepOptions.ConfirmSingleCore"/>) rather than the search ones. The classification is the same
    /// either way: a completed run whose every core passed is the only pass.
    ///
    /// WHOSE VERDICT THIS IS. The implementation runs a SINGLE-CORE phase first and an ALL-CORE phase second
    /// (where <see cref="SweepOptions.IncludeSingleCorePhase"/> is on); it combines them with
    /// <see cref="SweepPolicy.CombineProbes"/> so the single-core phase is never skipped just because the
    /// all-core phase was inconclusive, and an all-core pass never masks a single-core error. Because the light
    /// single-core phase runs first, a single-core REAL error, thermal abort or cancellation returns the combined
    /// verdict WITHOUT paying for the much longer all-core phase; a single-core pass proceeds to it. A return of
    /// <see cref="SweepProbeStatus.Passed"/> therefore means BOTH phases tested every core they loaded and every
    /// one of them passed.</summary>
    SweepProbeResult Probe(
        SweepDomain domain, int offset, IReadOnlyList<int> counts,
        int domainIndex, int domainCount, Action<SweepProgress>? progress, CancellationToken cancellation,
        SweepProbeMode mode);

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

    /// <summary>
    /// Combine the ALL-CORE phase's verdict with the SINGLE-CORE phase's into the probe's one verdict, FAIL
    /// CLOSED. The single-core phase is independent evidence, so its result can never be discarded just because
    /// the all-core phase was inconclusive — but an all-core PASS must equally not mask a single-core ERROR.
    ///
    /// THE RULE, in priority order:
    /// <list type="number">
    /// <item>A cancellation from EITHER phase is the run's stop (the caller asked to stop).</item>
    /// <item>A real oracle ERROR (<see cref="SweepProbeStatus.CoreError"/>) from EITHER phase is the verdict — a
    /// failure is never hidden by the other phase passing, which is the masking bug this rule exists to prevent
    /// (the single-core phase used to be skipped on a non-Passed all-core result AND to leave an all-core error in
    /// place only when it happened to also fail).</item>
    /// <item>A thermal abort from EITHER phase is a non-verdict and stops the sweep; it is reported as thermal
    /// (never a stability verdict), and an all-core thermal abort is NOT silently downgraded by a later pass.</item>
    /// <item>Otherwise only TWO passes make a pass. Where the all-core phase is a genuine pass, the single-core
    /// verdict is the result (so a single-core error/inconclusive is not masked); where the all-core phase is
    /// itself inconclusive, that inconclusive result stands (a single-core pass must not paper over "not every
    /// physical core could be tested"). This is the fail-closed direction.</item>
    /// </list>
    /// <paramref name="single"/> is null when the single-core phase did not run (disabled, a non-positive budget,
    /// or the sweep was already cancelled), in which case the all-core verdict stands unchanged. This is also the
    /// fail-fast path: when the single-core phase runs FIRST and reports a real error, a thermal abort or a
    /// cancellation, the target returns the combined verdict WITHOUT running the all-core phase — the single
    /// verdict passed as <paramref name="allCore"/> with a null <paramref name="single"/>. The phase ORDER lives
    /// in the target, not in this pure rule, which only combines whatever two verdicts it is given.
    /// </summary>
    public static SweepProbeResult CombineProbes(SweepProbeResult allCore, SweepProbeResult? single)
    {
        if (single is null) return allCore;

        // 1. A caller cancellation from either phase is the stop, whatever the other phase saw.
        if (allCore.Status == SweepProbeStatus.Cancelled) return allCore;
        if (single.Status == SweepProbeStatus.Cancelled) return single;

        // 2. A real oracle error wins from either phase — a pass in the other phase must not mask it. The all-core
        //    phase is checked first only so the attribution order is deterministic (both could name a core).
        if (allCore.Status == SweepProbeStatus.CoreError) return allCore;
        if (single.Status == SweepProbeStatus.CoreError) return single;

        // 3. A thermal abort is a non-verdict stop and is never downgraded by the other phase passing.
        if (allCore.Status == SweepProbeStatus.ThermalAbort) return allCore;
        if (single.Status == SweepProbeStatus.ThermalAbort) return single;

        // 4. Neither phase stopped: only TWO passes make a pass. An inconclusive result from EITHER phase (a
        //    refused pin, a budget that ran out, a probe that could not run at all) is NOT upgraded to a pass by
        //    the other phase passing — "not every physical core could be tested" is not a verdict, and the
        //    single-core phase's pass cannot paper over it. This is the fail-closed direction.
        if (allCore.Status == SweepProbeStatus.Passed && single.Status == SweepProbeStatus.Passed)
            return SweepProbeResult.Pass();
        return allCore.Status == SweepProbeStatus.Passed ? single : allCore;
    }

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
/// the DEEPENING of that rail on its first oracle failure, backs off and re-verifies the safer value, runs a
/// FINAL SOAK CONFIRMATION on the offset it settled on, and then moves to the next rail — each cluster is walked
/// independently, which is what makes its failure attributable. Thermal abort, cancellation and an apply refusal
/// stop the WHOLE sweep, because none of them is a verdict about the offset; a first error is recorded for its
/// rail but does NOT end the sweep, and there is no time budget to exhaust — the walk ends only at the rail's
/// floor. In a finally no exit path can skip, the pre-sweep counts go back to the SMU. It returns a PROPOSAL and
/// writes nothing to settings. See the file header for the whole safety model.
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

            var probe = ProbeRepeated(target, domain, value, counts, domainIndex, request.Domains.Count, progress, cancellation, options.Repetitions);
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

                var probe = ProbeRepeated(target, domain, candidate, counts, domainIndex, request.Domains.Count, progress, cancellation, options.Repetitions);
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

        // When a soak is configured, the short search dwell does NOT decide stability: only the long confirmation
        // below may set Stable back to true. This is what keeps a thermal abort, a cancellation or an apply
        // refusal DURING the soak from leaving the pre-soak "the search passed" value in place — none of them is a
        // verdict, and the offset must not be presented as confirmed off the back of one.
        if (options.ConfirmSoak) stable = false;

        // ---- THE FINAL SOAK CONFIRMATION: the long pass that decides "stable" ----
        // The search above finds the EDGE quickly with short probes; this half is the evidence the offset may be
        // presented as stable on. It runs ONCE per settled domain — on the offset the search landed on — with the
        // much longer ConfirmAllCore/ConfirmSingleCore budgets. A soak FAILURE retreats toward stock by
        // SafetyMargin and re-soaks, bounded by MaxBackoffAttempts (the settled value plus up to that many
        // retreats). If nothing above stock verifies, the proposal is the last offset that passed the soak, or
        // stock, NEVER the offset that just failed. Thermal/cancel/apply-refusal stop the whole sweep exactly as
        // in the search, so none of them is ever recorded as a stability verdict.
        var soakRan = false;
        var soakVerified = false;
        if (options.ConfirmSoak && stop is SweepStop.Completed or SweepStop.FirstError)
        {
            var candidate = proposed;
            var soakAttempts = Math.Max(1, options.MaxBackoffAttempts) + 1;   // the settled value + the retreats
            for (var attempt = 0; attempt < soakAttempts; attempt++)
            {
                if (cancellation.IsCancellationRequested) { stop = SweepStop.Cancelled; break; }

                var counts = SweepPolicy.WithOffset(baseline, domain, candidate);
                progress?.Invoke(new SweepProgress(SweepPhase.Confirming, domainIndex, request.Domains.Count,
                    domain.Label, candidate, 0, 0, null, totalClock.Elapsed.TotalMilliseconds, -1, domain.ClusterIdentified));

                var apply = target.Apply(counts, cancellation);
                if (!apply.Ok) { stop = SweepStop.ApplyFailed; detail ??= apply.Error; break; }

                var soak = target.Probe(domain, candidate, counts, domainIndex, request.Domains.Count, progress,
                    cancellation, SweepProbeMode.Soak);
                if (soak.Status == SweepProbeStatus.Cancelled) { stop = SweepStop.Cancelled; break; }
                if (soak.Status == SweepProbeStatus.Failed) { stop = SweepStop.ApplyFailed; detail ??= soak.Detail; break; }
                if (soak.Status == SweepProbeStatus.ThermalAbort) { stop = SweepStop.ThermalAbort; detail ??= soak.Detail; break; }

                soakRan = true;   // a real verdict was produced, not a thermal/cancel/apply early exit
                if (soak.Status == SweepProbeStatus.Passed)
                {
                    proposed = candidate;
                    stable = true;
                    soakVerified = true;
                    break;
                }

                // The soak failed: retreat another safety margin toward stock and re-soak, bounded. Once the
                // retreat cannot get any safer, stop — never loop on the same failing candidate.
                var next = SweepPolicy.BackOff(candidate, options.SafetyMargin, domain.Max);
                if (next <= candidate) break;
                candidate = next;
            }

            if (soakRan && !soakVerified && stop is SweepStop.Completed or SweepStop.FirstError)
            {
                // The soak ran and nothing above stock verified. Propose STOCK (the only unconditionally safe
                // value), never the offset that just failed the long pass, and do not call it stable.
                proposed = domain.Clamp(0);
                stable = false;
                detail ??= "no offset above stock passed the final soak; proposing stock, not the offset that failed";
            }
        }

        if (!stable && firstFail is null && !soakRan) detail ??= outcomeDetail;

        var result = new SweepDomainResult(
            domain, proposed, firstFail, failCore, passes, reverified, stable,
            domainClock.Elapsed.TotalMilliseconds, detail, Soaked: soakVerified);

        // The rail's first failure is reported in its result but does not stop the sweep; thermal, cancel and an
        // apply refusal ARE global stops. This method reports only what happened on ITS rail; the caller decides
        // whether the stop is global (see Run).
        return (result, stop, detail);
    }

    /// <summary>Run one offset's probe <paramref name="repetitions"/> times, ALL of which must be stability
    /// verdicts, and return the first non-passing one (a pass only if every repetition passed). Cancellation is
    /// returned promptly; a thermal/apply failure on any repetition ends the probe with that failure, because none
    /// of them is a verdict and the sweep must not continue on them. A non-positive count is one run.</summary>
    private static SweepProbeResult ProbeRepeated(
        IUndervoltSweepTarget target, SweepDomain domain, int offset, IReadOnlyList<int> counts,
        int domainIndex, int domainCount, Action<SweepProgress>? progress, CancellationToken cancellation, int repetitions)
    {
        var runs = Math.Max(1, repetitions);
        SweepProbeResult result = SweepProbeResult.Pass();
        for (var run = 0; run < runs; run++)
        {
            if (cancellation.IsCancellationRequested) return SweepProbeResult.Cancelled();
            result = target.Probe(domain, offset, counts, domainIndex, domainCount, progress, cancellation, SweepProbeMode.Search);
            if (result.Status != SweepProbeStatus.Passed) return result;
        }
        return result;
    }

    /// <summary>A rough, deliberately pessimistic ETA for the UI. Every probe pays the full per-core budget for
    /// every core it loads, and is run <see cref="SweepOptions.Repetitions"/> times; every domain (STAGE) is walked
    /// from its start to its floor plus the back-off probes. When <see cref="SweepOptions.IncludeSingleCorePhase"/>
    /// is set the single-core phase adds a per-core term per offset (its <see cref="SweepOptions.SingleCoreBudget"/>
    /// default is 30 s, a meaningful high-boost dwell). The order the phases run does NOT change the estimate: the
    /// single-core and all-core terms are both paid on every offset that reaches them; only the fail-fast case (a
    /// single-core error/thermal/cancel skips the all-core phase) makes the real run CHEAPER than this worst case,
    /// so the estimate stays an honest upper bound. THE SOAK IS PART OF THE ESTIMATE: with
    /// <see cref="SweepOptions.ConfirmSoak"/> on, every domain adds one final confirmation of the much longer
    /// <see cref="SweepOptions.ConfirmAllCore"/> per core plus a single-core soak term
    /// (<see cref="SweepOptions.ConfirmSingleCore"/>), and is budgeted for the full bounded set of soak attempts
    /// (the settled value plus MaxBackoffAttempts retreats) so the estimate stays honest even when a soak fails and
    /// retreats. <paramref name="coresPerStage"/> is, per domain, the number of cores the probe's ALL-CORE phase
    /// will load — which is every physical core of the machine, because that phase deliberately loads them all to
    /// build the package current/droop the oracle needs. The single-core term is costed with the same value: the
    /// single-core phase actually walks only the swept cluster's cores (fewer), so this is the honest UPPER BOUND
    /// rather than an understatement. It is the honest estimate of a FULL walk — there is no total budget to clamp
    /// it to, so it may be large; it is meant to be honest about the order of magnitude, not exact.</summary>
    public static TimeSpan RoughEta(IReadOnlyList<SweepDomain> domains, IReadOnlyList<int> baseCounts,
                                    SweepOptions options, IReadOnlyList<int> coresPerStage)
    {
        if (domains.Count == 0) return TimeSpan.Zero;
        var repetitions = Math.Max(1, options.Repetitions);
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
            var allCoreTerm = options.Load.PerCoreBudget * cores;
            var singleCoreTerm = options.IncludeSingleCorePhase ? options.SingleCoreBudget * cores : TimeSpan.Zero;
            cost += (allCoreTerm + singleCoreTerm) * perDomainProbes * repetitions;

            // The final soak: the settled value plus up to MaxBackoffAttempts retreats, each a long all-core run
            // plus (where the single-core phase is on) a long single-core run over the same cores.
            if (options.ConfirmSoak)
            {
                var soakAttempts = Math.Max(1, options.MaxBackoffAttempts) + 1;
                var soakAllCore = options.ConfirmAllCore * cores;
                var soakSingle = options.IncludeSingleCorePhase ? options.ConfirmSingleCore * cores : TimeSpan.Zero;
                cost += (soakAllCore + soakSingle) * soakAttempts;
            }
        }
        return cost;
    }
}
