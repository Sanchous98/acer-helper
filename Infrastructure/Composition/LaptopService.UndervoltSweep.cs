using System.Diagnostics;
using System.Threading;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Composition;

// THE GUIDED UNDERVOLT SWEEP'S INFRASTRUCTURE HALF: the volatile SMU adapter, the load-runner invocation, the
// CPU-cluster projection and the sweep's mutual-exclusion gate. The decisions live in
// Infrastructure/Vendors/Generic/UndervoltSweep.cs; this half is I/O and composition, and it is deliberately as
// thin as the port it drives. It implements BOTH kinds of probe the pure loop asks for: the short SEARCH probe
// (Load.PerCoreBudget / SingleCoreBudget) and the long SOAK confirmation (ConfirmAllCore / ConfirmSingleCore).
// The loop owns WHEN to soak; this half owns only the budgets, and the classification is identical, so the soak
// cannot become a different verdict by accident.
//
// WHAT IT NEVER DOES: it never calls RememberRails/RememberAllCore and never touches Settings. Every write in
// here is ICurveOptimizer.SetDomains — the SMU mailbox and nothing else — so a probe offset cannot reach
// settings.json (see docs/auto-undervolt.md §2c and
// Infrastructure/Vendors/Generic/UndervoltSweep.cs's safety model). Saving is a
// separate, explicit action (SaveUndervoltSweep) that goes through SaveCoValues, the existing per-mode path.
//
// THE GATE, and why a flag is enough. A sweep owns the SMU for its whole duration; the manual sliders and the
// periodic re-apply must not write while it does. The port itself already serialises individual mailbox
// transactions, so what the gate has to prevent is a manual/re-apply write landing BETWEEN two probes and
// invalidating one. _tuningGate makes "read the flag, then write the SMU" atomic for every writer: a manual edit
// takes the gate and refuses when the flag is up; the sweep's own writes take the gate too (so a write already
// in flight when the flag goes up finishes first, and the sweep's base snapshot is taken after it). The gate is
// held for ONE mailbox transaction at a time, never for the whole sweep, so it can never stall the UI on a
// minutes-long run.

public sealed partial class LaptopService
{
    /// <summary>Serialises the tuning writes that share the SMU (the sweep, the manual Curve-Optimizer edits and
    /// the per-mode re-apply) so checking <see cref="_sweepActive"/> and performing a write are atomic. It is NOT
    /// held across a sweep; only across one mailbox transaction.</summary>
    private readonly Lock _tuningGate = new();

    /// <summary>1 while a guided sweep owns the SMU. Set/cleared under <see cref="_tuningGate"/>.</summary>
    private int _sweepActive;

    /// <summary>Why a manual edit was refused mid-sweep. Reaches the UI as a port-style reason; the sliders are
    /// disabled while a sweep runs, so this is a guard rather than a path the user is expected to hit.</summary>
    internal const string SweepBusyReason = "a guided undervolt sweep is running";

    /// <summary>True while a guided sweep owns the SMU. The manual Curve-Optimizer edits and the per-mode
    /// re-apply consult it (and refuse/skip), so nothing stomps a probe.</summary>
    internal bool TuningInProgress => Volatile.Read(ref _sweepActive) != 0;

    /// <summary>Whether this machine can run a guided sweep at all: a Curve-Optimizer port, the load tool's
    /// affinity adapter, a readable CPU temperature (thermal protection fails closed, so a sweep without one
    /// would abort on its first block) and at least one CPU cluster to walk.</summary>
    public bool CanSweepUndervolt
        => device.CurveOptimizer != null && device.CoreAffinity != null && device.Sensors != null
           && SweepDomains().Count > 0;

    /// <summary>The CPU clusters the sweep may walk, projected out of the port's domain list with their INDEX in
    /// that list preserved. The iGPU (and any future non-cluster rail) is excluded here, once, so the sweep and
    /// the index alignment cannot disagree about which slots are carried.
    ///
    /// EACH DOMAIN CARRIES ITS CLUSTER AND WHETHER IT IS IDENTIFIABLE. The cluster identity is the port's
    /// (<see cref="VoltageDomain.Cluster"/> — the port knows which rail is the performance cores), and
    /// <see cref="SweepDomain.ClusterIdentified"/> is true only when the machine's TOPOLOGY can also say which
    /// physical cores carry that class. The probe's all-core phase loads every physical core either way; this flag
    /// governs the SINGLE-CORE phase, which walks only the swept cluster's cores where it can and all cores (with
    /// the progress saying so) where it cannot — never a silent pretence of isolation. The topology is read once
    /// here so every domain agrees.</summary>
    public IReadOnlyList<SweepDomain> SweepDomains()
    {
        var co = device.CurveOptimizer;
        if (co == null) return [];

        var topology = device.CoreAffinity?.Topology();
        var list = new List<SweepDomain>();
        for (var i = 0; i < co.Domains.Count; i++)
        {
            var d = co.Domains[i];
            if (!CoAxis.IsCpuCluster(d.Key)) continue;
            var range = d.Range ?? co.Range;
            var identified = d.Cluster is { } kind && topology?.Cluster(kind) is { Count: > 0 };
            list.Add(new SweepDomain(i, d.Key, d.Label, range.Min, range.Max, d.MillivoltsPerCount, d.Cluster, identified));
        }
        return list;
    }

    /// <summary>
    /// Run one guided sweep. Blocking for its whole duration (minutes), so call it OFF the UI thread. It writes
    /// candidate offsets to the SMU VOLATILELY and restores the pre-sweep counts on every exit path; it never
    /// persists anything. The result is a PROPOSAL the caller shows the user; saving it is
    /// <see cref="SaveUndervoltSweep"/> and only happens on an explicit click.
    /// </summary>
    public SweepResult RunUndervoltSweep(SweepOptions options, Action<SweepProgress>? progress, CancellationToken cancellation)
    {
        if (device.CurveOptimizer == null) return SweepResult.Empty;

        // ---- THE AC GATE: the FIRST thing checked, before the flag, the topology, the profile or the SMU. ----
        // On battery the platform uses an energy-saving mode where the SMU can ACKNOWLEDGE a Curve-Optimizer
        // message while SUPPRESSING its effect (the documented 0xFD "prerequisites not met" cause), so a probe
        // could "pass" against an offset that was never applied — a false stable value. A sweep is long, invasive
        // and can only be trusted on AC. UNKNOWN (null: no reading yet, no battery, a desktop) is treated as NOT
        // AC, deliberately: the deliberate-action rule is to fail closed on an unproven source rather than risk a
        // false verdict. This refuses BEFORE any write or profile change, and reports a refusal stop that is never
        // a stability verdict.
        if (OnAc != true)
            return SweepResult.NotOnAc(OnAc == false
                ? "the guided sweep requires the original charger; on battery or USB-C PD the platform can accept "
                  + "a Curve-Optimizer write without applying it, which would make a probe pass against an offset "
                  + "that was never set"
                : "the power source is unknown, so the original charger cannot be proven; the guided sweep "
                  + "requires the original charger");

        // THE FLAG IS SET FIRST, in its own critical section. It used to be taken together with the base snapshot,
        // but the snapshot now depends on the domain projection, which reads the processor topology — and the gate
        // must never span that (or any) OS call: it is held for ONE mailbox transaction. Setting the flag first
        // keeps the mutual-exclusion property intact — once it is up, every other SMU writer refuses or skips, so
        // nothing can land before the snapshot — while the gate itself is released before the topology is read.
        //
        // EVERYTHING AFTER THE FLAG IS INSIDE ONE try/finally THAT ALWAYS CLEARS IT. This is the fix for the
        // "auto-undervolt fails after the first cycle" report: the flag used to be cleared only by the inner
        // finally (which began after the mode-key/base-count snapshots and the forced profile), so ANY throw from
        // those pre-try reads — a transient EC/WMI failure in CurrentCoDomains, the profile port's Selectable(),
        // or the forced switch — left `_sweepActive = 1` FOREVER. Every later sweep was then refused with
        // SweepBusyReason, and because that refusal is a returned result rather than a throw the card rendered it
        // through a path the user read as a generic failure. Clearing the flag in the OUTERMOST finally makes the
        // gate exception-safe: a sweep can fail, but it can never leave the SMU claimed by a run that is over.
        lock (_tuningGate)
        {
            if (_sweepActive != 0)
                return new SweepResult([], SweepStop.ApplyFailed, 0, SweepBusyReason);
            _sweepActive = 1;
        }

        try
        {
            // THE MODE KEY IS SNAPSHOTTED BEFORE THE PROFILE IS FORCED. The forced switch writes the port and
            // announces the lighting, but leaves Settings, so CurrentModeKey() would not change anyway — but
            // pinning the key here makes the requirement structural rather than a consequence of the forced path
            // staying transient: the proposal is saved to the mode the USER was in, never to the forced Turbo.
            // It travels on the result.
            var userModeKey = CurrentModeKey();

            // THE BASE COUNTS ARE ALSO READ BEFORE THE FORCE. CurrentCoDomains() derives the mode key from the
            // live profile, so reading it after the force would snapshot the TURBO mode's preset instead of the
            // user's and restore the wrong numbers. Read under the user's key, then force.
            var previous = CurrentProfile();                   // what to put back, read before we force anything
            var baseCounts = CurrentCoDomains();
            var forced = ForceSweepProfile(out var profileNote);
            try
            {
                var domains = SweepDomains();   // reads the topology, OFF the gate
                if (domains.Count == 0) return SweepResult.Empty;

                var request = new SweepRequest(domains, baseCounts, options);
                var target = new SweepTarget(this, options, device.CoreAffinity);
                var result = UndervoltSweep.Run(request, target, progress, cancellation);
                // The key the save must target is the USER'S, carried out of band rather than re-derived after
                // the profile was forced. If the profile was NOT touched, the reason it was not is reported in
                // the existing Detail channel (only when the run did not already have a stop reason).
                if (profileNote is not null && result.Detail is null) result = result with { Detail = profileNote };
                return result with { ProposedModeKey = userModeKey };
            }
            finally
            {
                // THE PROFILE RESTORE RIDES THE SAME DISCIPLINE AS THE COUNTS RESTORE, one level out:
                // UndervoltSweep.Run's finally (inside the try above) has already put the pre-sweep COUNTS back;
                // this finally puts the pre-sweep PROFILE back, on every exit path (success, first error, cancel,
                // thermal, exception). Profile restore last is deliberate: the counts restore is the one that must
                // go back to the SMU whatever happens, and it must not race a profile change. It is best-effort:
                // a throw from the restore must not shadow the sweep's own failure (the outer finally still
                // releases the gate).
                try { RestoreSweepProfile(previous, forced); } catch { /* best-effort restore */ }
            }
        }
        finally
        {
            // The gate is released on EVERY exit path, including a throw from the snapshots or the forced profile
            // that happens before the inner try — the wedge this fixes. No sweep can leave the SMU claimed.
            lock (_tuningGate) _sweepActive = 0;
        }
    }

    /// <summary>Pick and apply the temporary performance profile for a sweep, for the CURRENT source. Turbo is
    /// preferred, then Performance; a machine with no power profiles, or one whose live source offers neither,
    /// runs WITHOUT changing the profile. The write is <see cref="ApplyProfileTransient"/>, so it never reaches
    /// the remembered per-source slot. Returns what was applied (null when nothing was), for
    /// <see cref="RestoreSweepProfile"/>.</summary>
    private PerformanceProfile? ForceSweepProfile(out string? label)
    {
        label = null;
        var pp = device.PowerProfiles;
        if (pp == null) { label = "no power profiles on this machine"; return null; }

        // AvailableOn(true): AC is mandatory (the gate above), so the AC set is the truthful one. Filter by the
        // port's own Selectable() too, so we never force a profile the live hardware refuses to offer.
        var selectable = pp.Selectable();
        var offered = (pp as IProfileAvailability)?.AvailableOn(true) ?? pp.All;
        bool Offered(PerformanceProfile p)
            => offered.Any(a => a.Id == p.Id) && (selectable.Count == 0 || selectable.Any(s => s.Id == p.Id));

        var target = pp.All.FirstOrDefault(p => KindOf(p) == ProfileKind.Turbo && Offered(p))
                  ?? pp.All.FirstOrDefault(p => KindOf(p) == ProfileKind.Performance && Offered(p));
        if (target == null) { label = "no Turbo/Performance profile is available on this source"; return null; }

        if (ApplyProfileTransient(target) is not null) return target;
        label = "the performance profile could not be applied";
        return null;
    }

    /// <summary>Put the pre-sweep profile back after a forced sweep. <paramref name="forced"/> is what
    /// <see cref="ForceSweepProfile"/> applied (null when the profile was never touched); <paramref name="previous"/>
    /// is what the port reported before it was forced. Where the port could not report a previous profile, the
    /// remembered AC mode is the fallback — better than leaving the machine on the temporary Turbo. A best-effort
    /// transient switch, exactly like the counts restore, because this finally must not throw over the sweep's own
    /// failure.</summary>
    private void RestoreSweepProfile(PerformanceProfile? previous, PerformanceProfile? forced)
    {
        if (forced == null) return;                       // the profile was never touched -> nothing to restore
        var restore = previous ?? SourceProfile(onAc: true);
        if (restore == null || restore.Id == forced.Id) return;
        ApplyProfileTransient(restore);
    }

    /// <summary>A deliberately pessimistic ETA for the UI, from the CPU clusters, the current preset and the
    /// number of cores each stage's probe will actually load. The ALL-CORE phase of every stage loads EVERY
    /// physical core of the machine (the package current/droop the oracle needs), so every stage is costed at the
    /// full physical-core count; the single-core phase inside <see cref="UndervoltSweep.RoughEta"/> is costed at
    /// that same count as its (bounded) upper bound, and both phases are costed for every offset that reaches
    /// them — the single-core-first fail-fast order only ever makes the real run cheaper than this worst case. It
    /// is the honest estimate of a FULL walk to the floor — there is no time budget to clamp it to — and an order
    /// of magnitude, not a promise — see UndervoltSweep.RoughEta.</summary>
    public TimeSpan UndervoltSweepEta(SweepOptions options)
    {
        var domains = SweepDomains();
        if (domains.Count == 0) return TimeSpan.Zero;
        var topology = device.CoreAffinity?.Topology();
        var cores = topology?.Cores.Count ?? Environment.ProcessorCount;
        var perStage = domains.Select(_ => cores).ToList();
        return UndervoltSweep.RoughEta(domains, CurrentCoDomains(), options, perStage);
    }

    /// <summary>
    /// Persist a finished sweep's proposal as the CURRENT mode's offsets — the explicit save, and the only path
    /// from a swept value to the settings file. It goes through <see cref="SaveCoValues"/>, the same per-mode edit
    /// path the sliders use (remembered before written, clamped per rail).
    ///
    /// EVERY CPU cluster is written, not only the ones the sweep reached: a cluster the sweep did NOT visit (a
    /// run stopped or cancelled early) is saved as STOCK, never as its stored preset — the user's rule is that a
    /// saved offset is not trusted, and leaving an unvisited cluster at its old, possibly-unstable value is
    /// exactly the "it only reset one cluster" failure. The iGPU (never swept, manual) is carried as it was.
    ///
    /// IT WRITES TO THE MODE THE USER WAS IN. The sweep forces a temporary performance profile for the run, so
    /// the live profile at save time can be that forced one; the proposal must land on the user's real mode, not
    /// on Turbo. The mode key is the sweep's snapshot (<see cref="SweepResult.ProposedModeKey"/>), with the live
    /// key only as a fallback for a result built without one.</summary>
    public (bool ok, string? error) SaveUndervoltSweep(SweepResult result)
    {
        var co = device.CurveOptimizer;
        if (co == null || result.Domains.Count == 0) return (false, null);

        var counts = CurrentCoDomains();
        if (counts.Length != co.Domains.Count) return (false, null);

        // Start from STOCK for every CPU cluster (the same baseline the probes ran from), carry the iGPU, then
        // lay the swept proposals on top.
        foreach (var d in SweepDomains())
            if (d.Index >= 0 && d.Index < counts.Length) counts[d.Index] = 0;
        foreach (var d in result.Domains)
            if (d.Domain.Index >= 0 && d.Domain.Index < counts.Length)
                counts[d.Domain.Index] = d.Proposed;

        return SaveCoValues(result.ProposedModeKey ?? CurrentModeKey(), counts);
    }

    /// <summary>One volatile write of the full index-aligned counts. Not persisted, ever.</summary>
    private SweepApplyOutcome WriteVolatile(IReadOnlyList<int> counts)
    {
        var co = device.CurveOptimizer;
        if (co == null) return SweepApplyOutcome.Refused(null);
        lock (_tuningGate)
        {
            try { return co.SetDomains(counts) ? SweepApplyOutcome.Applied : SweepApplyOutcome.Refused(co.LastError); }
            catch { return SweepApplyOutcome.Refused(null); }   // a throwing port has no words of its own to report
        }
    }

    /// <summary>The Application contract's implementation: volatile SMU writes plus the real load run. Held as a
    /// nested type so the load wiring does not widen this class's public surface.</summary>
    private sealed class SweepTarget(LaptopService owner, SweepOptions options, ICoreAffinity? affinity)
        : IUndervoltSweepTarget
    {
        public SweepApplyOutcome Apply(IReadOnlyList<int> counts, CancellationToken cancellation)
            => owner.WriteVolatile(counts);

        public SweepApplyOutcome Restore(IReadOnlyList<int> counts, CancellationToken cancellation)
            => owner.WriteVolatile(counts);

        public SweepProbeResult Probe(
            SweepDomain domain, int offset, IReadOnlyList<int> counts,
            int domainIndex, int domainCount, Action<SweepProgress>? progress, CancellationToken cancellation,
            SweepProbeMode mode)
        {
            if (affinity is null) return SweepProbeResult.Failed("this machine has no CPU affinity adapter");

            var topology = affinity.Topology();
            var allCores = CpuLoadPolicy.OrderCores(topology.Cores);
            if (allCores.Count == 0) return SweepProbeResult.Failed("no physical cores were reported");

            // THE SINGLE-CORE PHASE RUNS FIRST — the light one-core-at-a-time walk precedes the heavy all-core
            // concurrent burst. The two phases probe DIFFERENT failure corners (the single-core high-boost case
            // CoreCycler exists for, and the package current/droop case OCCT/Linpack reach), and the order is
            // deliberate: the light phase is cheap, so a real oracle error, a thermal abort or a cancellation it
            // reports is discovered before the (much longer) all-core phase is ever paid for. A single-core PASS
            // proceeds to the all-core phase; only the two together can produce a PASS. Thermal/cancel/apply
            // refusals remain GLOBAL stops exactly as before, and a single-core error is the offset's stability
            // verdict regardless of what the all-core phase might have said.
            //
            // THE ALL-CORE PHASE LOADS EVERY PHYSICAL CORE OF THE MACHINE, not only the swept cluster's. This is
            // the fix for the owner's "a few cores briefly busy, the rest near idle" report: OCCT/Linpack load
            // every core at once, because it is the CONCURRENT package current/droop that makes a marginal
            // undervolt fail, and a probe that loaded only the swept cluster's few cores produced almost none of
            // that current. The offset being tested is still the swept cluster's: the sweep's baseline holds
            // every OTHER cluster at STOCK (SweepPolicy.StockBaseline), so loading a stock-held sibling adds the
            // package current the oracle needs without contaminating the attribution — a failure is reported
            // per-core (Classify names the core), and the swept cluster's offset is the only one that moved.
            //
            // THE SWEPT CLUSTER'S CORES ARE COMPUTED HERE, because the SINGLE-CORE phase walks exactly them
            // (where the topology can attribute them; otherwise all cores, with the progress marked
            // not-identified) so the CoreCycler high-boost corner belongs to the cluster on the bench.
            var subset = domain.ClusterIdentified && domain.Cluster is { } kind ? topology.Cluster(kind) : null;
            var identified = subset is { Count: > 0 };
            var clusterCores = identified ? CpuLoadPolicy.OrderCores(subset!) : allCores;

            // WHICH BUDGETS THE PHASE GETS is the only difference between the short SEARCH probe and the long
            // SOAK confirmation. The search uses the per-offset Load/SingleCoreBudget; the soak uses the much
            // longer ConfirmAllCore/ConfirmSingleCore. Everything else — the all-core load, the concurrency, the
            // classification, the thermal/cancel policy — is identical, so the soak cannot become a weaker oracle
            // or a different verdict by accident.
            var allCoreBudget = mode == SweepProbeMode.Soak ? options.ConfirmAllCore : options.Load.PerCoreBudget;
            var singleCoreBudget = mode == SweepProbeMode.Soak ? options.ConfirmSingleCore : options.SingleCoreBudget;

            // The probe's own run must be able to test every core it loads; a caller's short total budget is
            // widened to fit rather than turning a complete test into a false "budget exhausted". Stop-on-error is
            // forced: the sweep wants the FIRST oracle failure, not a continued run. The all-core phase forces
            // ConcurrentCores on (the whole point of the heavier oracle) and widens the total budget to
            // per-core × (cores + 1), which is the worst case where the workers serialise on a single logical CPU
            // rather than the usual all-at-once wall clock.
            Func<int> temperature = () => owner.device.Sensors?.Read().CpuTempC ?? -1;
            var clock = Stopwatch.StartNew();

            // THE PHASE NAMES THE KIND OF LOAD, NOT THE SEARCH/SOAK STAGE: the single-core half always reports
            // SingleCore, and the all-core half reports Loading (or Confirming for the soak), so the card can say
            // "one core at a time" for both the search and the soak. The progress order mirrors the run order:
            // SingleCore first, then Loading/Confirming.
            var singleCorePhase = SweepPhase.SingleCore;
            SweepProbeResult? singleVerdict = null;

            // The single-core half is gated ONLY by IncludeSingleCorePhase/budget — never by the all-core outcome,
            // which by construction has not run yet. A caller cancellation stops the whole probe, so it is not
            // worth starting a minutes-long single-core pass on an already-cancelled sweep.
            if (options.IncludeSingleCorePhase && singleCoreBudget > TimeSpan.Zero
                && !cancellation.IsCancellationRequested)
            {
                Action<LogicalCore, int, int> onSingleCore = (core, index, count) => progress?.Invoke(new SweepProgress(
                    singleCorePhase, domainIndex, domainCount, domain.Label, offset,
                    index, count, core, clock.Elapsed.TotalMilliseconds, temperature(), identified));

                // The single-core phase is sequential (one core at a time) over the swept cluster's cores, with
                // its own total budget widened to per-core × (cores + 1) so a complete walk is never cut short.
                var single = options.Load with
                {
                    ConcurrentCores = false,
                    StopOnFirstError = true,
                    PerCoreBudget = singleCoreBudget,
                    TotalBudget = singleCoreBudget * (clusterCores.Count + 1),
                };
                var singleRun = CpuLoadRunner.Run(affinity, temperature, single, cancellation, kernel: null,
                    onCore: onSingleCore, cores: clusterCores);
                singleVerdict = Classify(singleRun);
            }

            // FAIL FAST, AND DO NOT SPEND THE ALL-CORE PHASE. A single-core run that already produced a REAL
            // oracle error is a stability verdict for the offset; a thermal abort and a cancellation are global
            // stops. None of them may be followed by the much longer all-core load. The combined verdict is
            // returned then and there: with no all-core phase run, the single-core verdict stands unchanged (the
            // pure rule's `single`-only case). A single-core PASS — or a merely INCONCLUSIVE single-core result
            // (a refused pin, a budget-exhausted walk) — falls through to the all-core phase, exactly as the
            // fail-closed combination expects: an inconclusive single-core result must still not suppress the
            // all-core evidence.
            if (singleVerdict is { } early && early.Status is SweepProbeStatus.CoreError
                or SweepProbeStatus.ThermalAbort or SweepProbeStatus.Cancelled)
                return SweepPolicy.CombineProbes(early, null);

            // The ALL-CORE phase runs second, reporting Loading (or Confirming for the soak's all-core pass). It
            // still loads every physical core and forces Concurrency/StopOnFirstError, and it is still bounded by
            // the phase's own budget with the total widened to per-core × (cores + 1).
            var allCorePhase = mode == SweepProbeMode.Soak ? SweepPhase.Confirming : SweepPhase.Loading;
            Action<LogicalCore, int, int> onCore = (core, index, count) => progress?.Invoke(new SweepProgress(
                allCorePhase, domainIndex, domainCount, domain.Label, offset,
                index, count, core, clock.Elapsed.TotalMilliseconds, temperature(), identified));

            var load = options.Load;
            var needed = allCoreBudget * (allCores.Count + 1);
            if (load.TotalBudget < needed) load = load with { TotalBudget = needed };
            load = load with { PerCoreBudget = allCoreBudget, StopOnFirstError = true, ConcurrentCores = true };

            var run = CpuLoadRunner.Run(affinity, temperature, load, cancellation, kernel: null,
                onCore: onCore, cores: allCores);
            var classification = Classify(run);

            // THE TWO VERDICTS ARE COMBINED FAIL-CLOSED by the pure rule in SweepPolicy.CombineProbes: a
            // cancellation wins, then a real oracle error from EITHER phase, then a thermal abort from either
            // phase, and only two passes make a pass — so an all-core PASS can never mask a single-core error,
            // and an inconclusive result from either phase is never upgraded to a pass. (The single-core phase's
            // own pass can no longer paper over an inconclusive all-core result, and vice versa.)
            return SweepPolicy.CombineProbes(classification, singleVerdict);

        }

        /// <summary>Turn a load run into the sweep's three-way probe result. A run that completed with every core
        /// passed is the ONLY "Passed"; an oracle failure is the only "CoreError"; a thermal abort, an unreadable
        /// sensor, a cancellation, an unfinished budget and a core the OS refused to pin all fail CLOSED — none of
        /// them is a stability verdict, which is the property the sweep's early-exit handling depends on.</summary>
        private static SweepProbeResult Classify(CpuLoadRunResult run)
        {
            if (run.Stop == CpuLoadStop.Cancelled) return SweepProbeResult.Cancelled();
            if (run.Stop is CpuLoadStop.ThermalAbort or CpuLoadStop.SensorUnreadable)
            {
                var hot = run.Cores.FirstOrDefault(r => r.Outcome == LoadOutcome.ThermalAbort);
                return SweepProbeResult.Thermal(hot?.Detail ?? "the thermal cutoff stopped the load");
            }

            var bad = run.Cores.FirstOrDefault(r => CpuLoadPolicy.IsError(r.Outcome));
            if (bad is not null) return SweepProbeResult.Error(bad.Core, bad.Outcome, bad.Detail);

            if (run.Stop == CpuLoadStop.StoppedOnError)
                return SweepProbeResult.Error(null, null, "the load reported an error");

            // Completed: it is a pass only if EVERY core was actually tested. A skipped core is a pin the OS
            // refused, and a budget that ran out leaves untested cores — neither proves the offset.
            var untested = run.Cores.FirstOrDefault(r => r.Outcome != LoadOutcome.Passed);
            if (untested is not null && untested.Outcome != LoadOutcome.Skipped)
                return SweepProbeResult.Failed($"core {untested.Core.Group}:{untested.Core.Processor} did not pass");
            if (run.Stop == CpuLoadStop.BudgetExhausted || run.Cores.Any(r => r.Outcome == LoadOutcome.Skipped))
                return SweepProbeResult.Failed("not every physical core could be tested");

            return SweepProbeResult.Pass();
        }
    }
}
