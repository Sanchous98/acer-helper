# Automatic CPU undervolt — what "automatic" can mean, and how it would be built

The user asked whether the app can have an **automatic CPU undervolt**. This is the assessment-and-design
answer, written before any code: it evaluates the four things "automatic" could mean, recommends a shape,
and lays out how each part would be built, where it would live, and what is not yet known. **Nothing here
is implemented and nothing here changes product code.** Where a fact is a measurement it is marked as one;
where it is a claim about Windows or Linux behaviour that was checked against public documentation rather
than measured on this machine, it says so; where it is a guess it is marked `needs measurement`.

The authoritative sources this builds on are `docs/curve-optimizer-strix-point.md` (the measured SMU story:
three rails, 2.5 mV/count on the cores against 5.0 on the iGPU, no CPU read-back, the volatile offset, and
the failure that arrives hours later at idle) and `docs/curve-optimizer-linux.md` (the Linux transport, the
gate, the privilege path, and what has and has not been validated on this machine). This document does not
repeat them; it assumes them.

---

## 1. Current state — the feature is already opt-in, manual and conservative

What exists today is one Curve-Optimizer axis with these properties, all of them on purpose:

- **Manual, per domain.** `ICurveOptimizer` (`Domain/Ports.cs`) exposes `Domains` (three on this part:
  `ccd:0`, `ccd:1`, `gfx`), `Range`, `MillivoltsPerCount`, and the two writes `Set` (all-core) and
  `SetDomains` (index-aligned, per rail). `UI/ViewModels/CoViewModel.cs` renders one slider per rail,
  applies on change, debounced 400 ms, and hands the array to `AppController.SetCo`, which moves the write
  off the UI thread.
- **Per performance mode.** The stored form is `CoPreset` (`AllCore` plus `Domains`, keyed by the rail's
  hardware identity) in `Settings.CoPresets` (`Infrastructure/Composition/Settings.cs`). The axis is
  `ModeAxis.Co`, is volatile (`ModeAxisTable.IsVolatile`), and `EmptyAxisPolicy.ForceStock` applies to a
  mode with no entry — a stale undervolt carried into an unconfigured mode is "exactly how an unexplained
  instability happens" (`Domain/ModeAxis.cs`).
- **Re-applied on startup, resume and mode switch.** The offset lives in SMU state and a power cycle
  restores stock, so the app is the source of truth: `ApplyModeCo` (`LaptopService.Tuning.cs`) is driven
  through `Application/ReapplySettings.cs` by `Infrastructure/Composition/HardwareReconciler.cs`, with the
  Curve Optimizer deferred to the pool because one transaction per core slot waits on the machine-wide PCI
  lock (`Application/ReapplyPlan.cs`).
- **Conservative bounds.** `-40…0` on the cores (`~100 mV` at the measured 2.5 mV/count) and `-50…0` on
  the iGPU; positive offsets are unrepresentable (`OffsetCounts.Clamp`). The stated reason for the floor is
  the failure mode, not arithmetic: on Zen 5 a too-aggressive offset fails **hours later at idle** as
  machine-check errors or silent corruption, not as an obvious crash under load.
- **Volatile and acknowledged, not confirmed.** A `true` from `Set`/`SetDomains` means the SMU *accepted
  the message*, nothing more — the CPU rails have no read-back (`Ports.cs`, and the measured note that the
  mailbox answers `REP_MSG_OK` while the platform's power mode suppresses the effect). Only the iGPU can be
  confirmed, by reading `0x20` back.

There is also a known defect that matters to anything "automatic": if the `Global\Access_PCI` interlock is
held too long by another tool (Ryzen Master, RyzenAdj, HWiNFO), the write is abandoned and **the offset
silently does not apply** (`RyzenCurveOptimizer.Windows.cs:419`; recorded in `docs/open-decisions.md`, both
under the known violations and in §«Противоречия» item 5). Any watchdog that reasons about "the offset is
set" must remember that the app's own model can disagree with what the SMU actually holds.

So the honest starting point is: the feature is already the *most conservative possible* version of
"undervolt" — explicit, per-mode, stock by default, one Reset click from off. "Automatic" is therefore a
question about **who picks the offset and who backs it out**, not about whether the plumbing exists. The
plumbing (`ICurveOptimizer`, `CoAxis`, the per-mode graph, the re-apply) does not change.

---

## 2. What "automatic" can mean — four options, evaluated separately

"Automatic" is not one feature. It is at least four, with completely different feasibility and risk, and
collapsing them is how the unsafe one gets built by accident.

### 2a. Auto-preset / auto-apply — one click, conservative fixed offsets, no search

**What it is.** A row (or a per-mode default) that writes a fixed conservative offset to every rail
without the user dragging anything: "apply a mild undervolt", e.g. a small fraction of the range, chosen
once and not searched for. The user still chooses it; the app only fills the value in.

**Feasibility.** Nearly free. It is exactly `SetCoValues` / `SetCoDomains` with a preset the app supplies
instead of the user, and the existing clamp (`CoAxis.ClampEach`) keeps it inside the rails' own bounds.

**Risk.** Low, provided the value is genuinely conservative and the UI never lets a single click land on
the end of the slider. But "conservative" is not a number the app knows: the stable limit is a property of
the individual die (`curve-optimizer-strix-point.md`), so the app can only offer a value it believes is
mild. The label must carry the range ("−10 (≈−25 mV)") and the Reset must stay one click away, exactly as
the manual slider already does.

**What it takes.** A `CoPreset` seed value and a button; no new ports, no new detection. This is the only
part of "automatic" that is safe on its own, and it should be the smallest part of whatever ships.

### 2b. Auto-revert watchdog — designed, shipped, then REMOVED (2026-09-27)

> **REMOVED 2026-09-27 — the owner replaced auto-revert with the transaction model.** An undervolt offset is a
> **transaction** that is committed in exactly two cases: (a) the user moves the **manual sliders**, or (b) the
> user **confirms/saves a guided-sweep proposal** (`SaveUndervoltSweep`, which goes through `SetCoValues`). In
> every other case a restart re-applies the last committed value (for an auto mode, stock = 0). The app must
> **never** silently change or revert a stored offset, so the whole startup auto-revert **watchdog**, its
> clean/unclean **sentinel**, and the compute-error **canary** that existed only to feed the watchdog were
> **deleted outright**:
> `Infrastructure/Vendors/Generic/UndervoltWatchdog.cs`,
> `Infrastructure/Vendors/Generic/UndervoltCanary.cs`,
> `Infrastructure/Composition/UndervoltSentinel.cs`,
> `Infrastructure/Composition/LaptopService.UndervoltCanary.cs`,
> `Infrastructure/Composition/LaptopService.UndervoltGuard.cs`, and `CoPreset.Suspect`. The calls in
> `LaptopService.ApplyStartupState`/`Dispose`, the `StartUndervoltCanary()` call in `UI/App.axaml.cs`, the
> `_canaryActive` checks in the sweep gate, and the tests/fakes for them are gone too. **The guided sweep's own
> safety model was NOT changed**: it writes probes volatilely, restores on every exit via a `finally`, and
> persists only through the explicit `SaveUndervoltSweep`.
>
> **What it was (kept for the record).** The sections below describe the removed design; read them as history.
> - **A compute-error canary (primary oracle).** A bounded, low-priority, cancellable periodic pulse
>   (`UndervoltCanary.cs` + `LaptopService.UndervoltCanary.cs`) reusing the EXISTING `CpuStressKernel` and golden
>   — no second kernel. Default ~1.5 s on ONE rotated physical core, scheduled by the shared `PeriodicSchedule`
>   at a default 5-minute interval. It only ran while a non-zero CPU-cluster offset was active for the current
>   mode AND a Curve-Optimizer port existed AND no sweep was running AND the machine was on AC. A checksum
>   mismatch or a faulted run was a **computation error**; a thermal abort, cancellation and an incomplete burst
>   were NOT verdicts.
> - **The pure watchdog policy** (`UndervoltWatchdog.cs`) returned an intent
>   (`ReapplyAsIs` / `StepBack` / `Stock`); compute errors were authoritative and named first, an unclean session
>   was secondary, and a repeated offender (already suspect) went to stock.
> - **The sentinel + step-back-before-reapply** (`IUndervoltSentinel`, the file implementation in
>   `UndervoltSentinel.cs`, the startup guard in `LaptopService.UndervoltGuard.cs`, and `CoPreset.Suspect`). The
>   guard ran at the top of `ApplyStartupState`, BEFORE `Reconciler.Reapply(Startup)`, and adjusted the stored
>   preset so the re-apply sent the safer value. The **startup re-apply now simply re-applies the committed
>   value** and never steps it back.
> - **Mutual exclusion** through the sweep's existing `_tuningGate`: the canary and the sweep could not both own
>   the cores, and neither could a manual edit land mid-probe. The sweep's own `_sweepActive` mutual exclusion
>   STAYS; only the `_canaryActive` half was removed.

**What it is (historical).** The app watches for instability, and when it sees it, steps the affected preset back or
reverts it to stock, then tells the user. It never searches for a deeper offset; it only ever relaxes.

**The oracle hierarchy, stated plainly.** The doc previously treated "clean vs unclean" as *the* primary signal and
OS event logs as the corroboration. That is now inverted:
1. **Computation errors (primary).** A self-checking integer kernel run while the undervolt is active returns a
   wrong answer → immediate, attributable fault. This is the only signal that fires *before* a crash and the only
   one readable on both platforms without new privileges. It is measured, not inferred.
2. **Unclean session (secondary).** The app's own sentinel file (below) still present at startup means the last
   session did not exit through the app — a kernel panic, a machine-check reboot, a power loss, a kill. Coarse,
   but it is the ONLY thing that catches a failure the app was not running to observe (a crash/hang at idle).
3. **WHEA/MCE event logs (deferred, corroboration only).** Still unimplemented; they would sharpen the secondary
   signal from "unclean" to "machine fault", not replace the primary one.

**Why it is the recommended core, and what makes it non-trivial.** The offset is volatile and the app
**re-applies it at startup**. That is the whole design problem, and it is not a monitoring problem — it is
an ordering problem:

> On a normal boot the app finds `CoPresets` and puts the same offset back. If the previous session ended
> because that offset caused a machine-check or a hang, the app would faithfully restore the exact offset
> that just failed — the "re-apply" that makes the feature usable is the same mechanism that would re-arm
> the fault.

So the watchdog cannot be a background monitor bolted on. It has to run **at startup, before the Curve
Optimizer's re-apply**, and it has to be able to answer: *did the last session end cleanly, and was an
undervolt active in it?* If the answer is "no, unclean, and yes, one was active", it must **step back
before** `ApplyModeCo` sends anything — half the offset, or stock — and mark the preset suspect.

This is the app's own rule from `docs/state-and-events.md` turned on the undervolt: on doubt, re-apply —
and on this axis the safe re-apply is *less*, not *the same*. Detection is not adoption: an event log is
allowed to say "the last boot failed", but it is the **event**, not a read of "is the CPU stable now",
that changes state, and there is no such read here.

**What "step back" means.** Two defensible choices, and the design should offer both:

- **Halve** the offset (toward stock, never past it), preserving some of the user's intent and giving a
  second chance. Repeated unclean sessions halve again until stock. This is the "relax, don't discard"
  behaviour a tuner wants.
- **Stock** the rail, the conservative choice when the user has not asked for automatic retreat.

Which one is used should be a setting; the app should default to **step back one notch and mark suspect**,
because that preserves intent while removing most of the risk, and because "revert fully" is already one
click away in the UI (`CoViewModel.Reset`). The suspect flag is the important part: the app must remember
that *this preset is not trusted* so the next clean session does not re-raise what an unclean one lowered.

**What it takes, honestly.** Two ingredients, and only the first is cheap:

1. A **clean-vs-unclean sentinel** the app writes and clears itself. This needs no OS-specific parsing at
   all: write "undervolt active, these offsets, this mode" to a marker file when the Curve Optimizer is
   applied, clear it on a clean exit (`AppController.ExitApp` is the one teardown point, and it already
   covers tray-exit and session-end together). A marker still present at startup means the last session
   did not exit through `ExitApp`. That is a real, portable signal, and it catches a kernel panic, a
   machine-check reboot, a power loss, and a Task-Manager kill alike. Its one weakness is false positives —
   a forced kill of a healthy session also leaves the marker — which is why the step-back is paired with a
   "keep it / revert fully" prompt rather than applied silently.
2. **OS instability signals**, which are the hard part and get their own section (§3). They are what let
   the watchdog distinguish "the app was killed" from "the machine fell over", and they are where the two
   platforms diverge sharply.

**Why it does not require a search and is therefore reliable.** The watchdog never tries to find the
stable offset; it only ever moves toward stock. Its worst case is "the undervolt is smaller than it could
have been", which is safe, and its detection is event-based rather than statistical. This is the property
that makes it buildable where the guided tuner (2c) is not.

### 2c. Guided auto-tuner (sweep) — iterate offsets with a stress load, back off on errors

**What it is.** The app walks the offset deeper (say from a mild value toward the floor), runs a load and a
soak per step, watches for errors, and keeps the deepest value that survived. This is what "automatic
undervolt" usually means, and it is the one that must not be built naively here. It is also **not
hypothetical**: the shipped tools that do exactly this loop — AMD's own Ryzen Master, Hydra, CoreCycler,
`linux-corecycler` — are surveyed in **Prior art** (between this section and §3), and the mapping back to
this hardware is the payoff there.

> **SHIPPED (2026-09-27).** A bounded version of this loop exists: the guided sweep in
> `Infrastructure/Vendors/Generic/UndervoltSweep.cs` + `Infrastructure/Composition/LaptopService.UndervoltSweep.cs`.
> It is **two stages, one per CPU cluster** (Zen 5 then Zen 5c); each stage loads **only the swept cluster's
> cores**, and **where the cluster cannot be identified** it loads all cores and says so. It probes volatilely,
> restores on every exit via a `finally`, holds the sibling cluster at stock, and persists only through the
> explicit `SaveUndervoltSweep`. It is bounded by the rail's hard range cap, the bounded back-off, the load's
> per-core/thermal limits and cancellation — **NOT by time**; the stop is the first oracle error (or a completed
> walk to the rail's floor). The core attribution and the all-cores fallback are the parts added after the first
> cut, whose progress read a bare "core x/10" that did not match the two stages.
>
> **The source gate is the BARREL, not "the OS says AC" (2026-09-27).** The sweep is refused unless the
> effective source is AC — and USB-C Power Delivery is treated as the battery (`LaptopService.RecomputeOnAc`),
> so it is refused there too. Windows reports USB-C PD as plain "AC" (it is charging), so the distinction comes
> from the typed EC reading (`Battery.PowerSource` → `SetPowerAdapter`); a machine with no EC channel keeps the
> OS answer, and an `Unknown` reading is ignored so a failed poll cannot flap the gate.

**Why it is fundamentally unreliable on this hardware.** Each reason is a measured fact from the curve
documents, not a preference:

- **Failure arrives hours later at idle, not under load.** A sweep's whole premise is that a load catches
  instability. On Zen 5 the opposite is often true: at low load a core boosts to its highest frequency at
  the lowest voltage on the curve, which is precisely where an aggressive negative offset removes too much
  voltage. A machine that passes an hour of stress can still fail while idle. "It survived the soak" is
  *not* evidence of stability, so the sweep must soak far longer than it can justify.
- **`OK` is not "the curve moved".** The CPU rails have no read-back. The SMU acknowledges while the
  platform's power mode suppresses the effect, so the tuner cannot tell "the offset took" from "the SMU
  took the message and ignored it" without a telemetry-table measurement of its own — which is exactly the
  measurement the port does not expose. The iGPU's `0x20` read-back is the only confirmed rail and does
  nothing for the CPU clusters.
- **Granularity is per cluster, not per core.** `0x4B` can write one slot, but the rail follows the
  **mildest** request among its cores (`curve-optimizer-strix-point.md`); a per-core sweep would tune the
  mildest core and leave the rest inert. The tuner can only tune two CPU values, and the value it finds is
  bounded by the worst core in each cluster — which the app cannot identify.
- **It needs a load generator and an error oracle, and the app has neither.** Shipping a stress workload is
  a product decision this app has never made (it keeps the CPU envelope at the EC's profile), and the error
  oracle does not exist on either platform yet (§3).
- **Time cost is unbounded in the direction that matters.** The safe soak is hours; a sweep of several
  steps is days. The user would have to leave a tuning session running across reboots — and the app
  re-applies on each boot, so the sweep has to survive the very restarts it is trying to detect.

**If it is ever built, it must be bounded hard.** The non-negotiable bounds:

- **Never auto-persist a found offset as trusted.** A swept value is a *candidate*, not a known-good.
- **Long soak, and an explicit "this is not a stability proof" warning** in the UI.
- **Hard range caps** independent of the rail's own range (e.g. stop well short of the `-40`/`-50`
  floors), and **one rail (STAGE) at a time**, so a failure can be attributed. The shipped sweep runs in **two
  stages, one per CPU cluster** — first the Zen 5 (performance) cluster, then the Zen 5c (efficiency) cluster —
  walking each **from stock (0)** — never from the mode's stored preset, which may itself be unstable — and holds
  every *other* CPU cluster at stock during a probe, so a stored sibling offset can neither cause nor mask a
  result. **A stage's probe loads only the cores of the cluster being swept** (the four Zen 5 or the six Zen 5c
  cores on this part), so a failure is attributable to that cluster; the progress reads "stage N/M — cluster
  <label> — ... core i/n" with `n` the cluster's own core count. **When the machine cannot attribute cores to a
  cluster** — the topology carries no per-core efficiency class (see below) — the probe falls back to loading
  *all* physical cores and the progress says **"all cores (cluster not identified)"** rather than pretending the
  stage was isolated. The iGPU is never swept (AMD does not auto-derive GFX either) and is carried verbatim. The
  true pre-sweep counts are restored on exit; the stock baseline is probe-only and never persisted. The sweep is
  **bounded by the rail's hard range cap, the bounded back-off (`MaxBackoffAttempts`), the load's per-core/thermal
  limits and cancellation — NOT by time**: its only stability boundary is the first oracle error, and a rail that
  keeps passing is walked all the way to its floor. Time is never a stop reason.
- **The cluster identity comes from the OS, and its absence is stated, not guessed.** Each physical core's
  topology entry carries an OS-neutral **efficiency class** (`LogicalCore.EfficiencyClass`; a higher value is the
  faster, less power-efficient core). On Windows this is the `PROCESSOR_RELATIONSHIP.EfficiencyClass` byte
  (already present in the buffer the topology read parses — measured on this box: the four Zen 5 cores report
  `1`, the six Zen 5c cores `0`); on Linux it is *derived* from `cpu_capacity` (the higher capacity is the
  performance cluster), with the honest caveat that `cpu_capacity` is a scheduler-relative number and may be
  equal across clusters, absent, or partial — in which case the class is left **unknown** and the all-cores
  fallback applies. The port declares each rail's cluster (`VoltageDomain.Cluster`; the Zen 5 rail is
  `Performance`, the Zen 5c rail `Efficiency`), so the mapping is the port's knowledge, not a `ccd:`-string
  convention parsed in the sweep layer. `CpuLoadPolicy.PartitionIntoClusters` is the single pure rule that turns
  classes into clusters, and the sweep marks a domain "not identified" unless both its port cluster AND a
  topology partition exist.
- **Explicit confirmation before each deeper step**, so the user is the author of the risk.
- **Back off on any fatal signal and stop the sweep**, never continue past an error.
- Optional and **off by default**. It is the last thing built, only after §5's experiments justify it, and
  then **modelled on the shipped tools** (`Prior art`): AMD's sweep-then-propose with a Difference-Offset
  back-off, and `linux-corecycler`'s crash-safe journal / `CO=0`-floor / resume-crash circuit breaker /
  correctness oracle / thermal fail-closed.

### 2d. Adaptive-per-load undervolt — not possible with this interface

**What it would be.** A continuous controller that reads load and adjusts the offset in real time to hold
the voltage at the lowest stable point.

**Why it is not possible here.** The interface is a **static SMU value**, and changing it is not a cheap
register poke: every change is a full mailbox transaction, and on Windows that means taking the
machine-wide `Global\Access_PCI` lock shared with Ryzen Master, RyzenAdj, HWiNFO and CPU-Z, with a 5-second
wait budget (`docs/curve-optimizer-strix-point.md`). A per-load controller would be a stream of such
transactions, each one able to silently abandon on a lock timeout. On Linux there is no cross-process lock
taken (the driver serialises its own SMU traffic), but the transaction is still a kernel-mediated write
with a 200 ms budget, and the offset is still a whole-package curve value, not a per-frequency trim.

The interface gives no per-load granularity, no read-back on the CPU rails to close the loop, and no
firmware storage — so the only realistic granularity remains **per performance mode**, which the app
already has (`CoPresets`). "Adaptive" therefore decomposes into "different fixed offsets per mode", which
is 2a plus the existing per-mode graph, not a controller. This option should be recorded as **not viable**,
not deferred.

### Recommendation

Ship **2a + 2b** as one coherent feature: a conservative one-click/auto-preset, guarded by a
startup-time auto-revert watchdog that steps back before it re-applies. Keep **2c** as an optional,
off-by-default, heavily bounded tool for a later phase, and only if the measurements in §5 make it
defensible. Do not build **2d**. This ordering is now **grounded in the prior art** rather than in
principle alone: every shipped auto-tuner (AMD Ryzen Master included) calls its derived values "a starting
point", hedges with a back-off knob, and leaves the user to validate — so the durable value is the watchdog
(2b), and a tuner is at most a proposal generator. If 2c is built, model it on CoreCycler and
`linux-corecycler`, not on a silent auto-persist (see **Prior art**).

---

## Prior art: how the shipped auto-tuners do it (как реальные авто-тюнеры это делают)

The user pushed back, correctly, that automated Curve Optimizer tuning already exists in the wild. It does,
and the point of this section is to ground §2c in what those tools actually do, then map that onto this
platform. The short version: **the loop is exactly 2c — apply an offset, run a load, watch an oracle, step
back on error — and the hard part is not the SMU write, it is the oracle and the load.** Everything below is
cited to the tool's own documentation where it could be checked; where a specific UI string or number could
not be confirmed against a primary source, it says so.

### AMD Ryzen Master — Curve Optimizer, Method: Automatic

- **Introduced in Ryzen Master build 2.9.0.2093** (April 2022), alongside manual Curve Optimizer. The AMD
  "FAQ — Curve Optimizer Feature in Ryzen Master" PDF names that build and the feature
  ([amd.com PDF](https://www.amd.com/content/dam/amd/en/documents/products/software-tools/faq-curve-optimizer.pdf)). **Verified** (primary, AMD).
- **UI.** Curve Optimizer **Mode** is `Off` / `All Cores` / `Per Core` (the FAQ describes optimizing "the
  entire CPU or specific cores"; it names All cores and Per Core and Off). A newer `Per Die` mode is
  reported for the multi-CCD UI, but **I could not confirm `Per Die` in the FAQ or user guide** — treat it as
  **unverified**. **Method** is `Automatic` (the app derives the offsets) or `Manual` (the user sets them).
- **Inputs.** Under `Automatic`, the user supplies a **Max Offset** and the **Test Duration** from the
  Stress Test section of Settings; the FAQ confirms both the Test Duration parameter and a **Stress Test
  Type** of `CPU Only` or `Both`, and says a longer duration "significantly increases the derivation time
  but it may derive values which are relatively more stable". The **10–600 s range** and the "Max Offset
  Values" wording are community/release-note reported, **not confirmed verbatim here** — mark unverified.
- **Behaviour.** It is long and invasive. AMD's own user guide warns: *"Setting higher offset values than
  the default value may result in more system restarts during optimization and may cause system stability
  issues"* ([AMD Ryzen Master user guide](https://docs.amd.com/r/en-US/68886-ryzen-master-user-guide/CPU)) —
  **verified** (primary). Community reports that Ryzen Master "may take hours, and might restart a number of
  times" agree (Tom's Hardware thread, **anecdotal**). The specific UI wording "your system may restart
  during this process" and a progress bar with `Stop` / `Continue` / `Restart` that keeps the previous
  partial results are **reported but not confirmed against a primary source** — **unverified**.
- **AMD's own caveats, quoted from the FAQ** (all **verified**, primary):
  - Derived values "**act as a starting point** for the user to further tune it manually".
  - "So applying the values **as is may result in system stability issues** and users are advised to
    further adjust the derived values … after verifying Difference Offset Value, stability with their own
    workloads."
  - The **Difference Offset Value** exists precisely to "apply lesser value by setting it to a value greater
    than 0" — AMD's own step-back knob.
  - "**Automatic derivation of values is available for CPU only. For GFX, user has to manually set the
    values** in the allowed range." AMD does **not** auto-tune the iGPU.
- **Persistence.** A "Write CO to BIOS" option makes the result survive restarts; otherwise the values are
  volatile ([AMD Ryzen Master page](https://www.amd.com/en/products/software/ryzen-master.html) describes
  managing PBO/CO parameters "on-the-fly or through the BIOS to ensure that the changes remain effective
  across system restarts"). **Verified** (primary).
- **Revisions.** Release note 2.14.2.3341: *"Adds an upgraded stress test (activated through the features
  'Apply & Test' or 'Curve Optimizer Automatic')"*; 2.14.3.5040 carries a known issue that on some CPUs the
  stress test may not fully stress the processor ([AMD release notes](https://www.amd.com/en/resources/support-articles/release-notes/RN-RYZEN-MASTER-2-14-2-3341.html)).
  **Verified** (primary). The direction of travel — a better stress test, and an admitted gap — is the whole
  story of this prior art.
- **Community outcomes are mixed and anecdotal.** Reports of "very optimistic / too high values", BSODs
  during the run, and "days later randomly rebooting at idle or light loads" are common (Tom's Hardware,
  r/Amd, r/overclocking). **Anecdotal — not a measurement**, but entirely consistent with §2c's core
  objection and with `curve-optimizer-strix-point.md`'s hours-later-at-idle failure.

### Hydra (1usmus) — a fully automatic per-core search, plus verification

- Hydra runs an automated per-core Curve Optimizer search: a preliminary pass, then a separate **verification
  stage** with configurable verification cycles, using test-complexity presets (reported as `SAFE`/`FAST`)
  and, in later versions, a **"SQ AGENT"** that targets efficiency/balance within a CCD rather than the
  most-negative CO and skips cores that do not move the needle. The **SQ AGENT** name and its framing are
  **verified** ([1usmus Patreon announcement](https://www.patreon.com/1usmus/posts/hydra-2-0b-pro-137800168));
  the exact `SAFE`/`FAST` preset names and the "verification cycles" configuration are **documented in the
  Hydra tutorial** ([Hydra Big Tutorial](https://www.slideshare.net/slideshow/hydra-big-tutorialpdf/258481744))
  but **I could not verify them line-by-line** — treat as attributed.
- The developer's own framing — paraphrased, because I could **not** verify the exact sentence verbatim —
  is the load-bearing idea: there is always some stress test that will call a system unstable, even at a
  *positive* CO, so the presets are tuned for real-world workloads rather than a proof, and cover "most"
  usage. **Attributed / unverified verbatim.** The point stands regardless of the exact words: **"stable" is
  workload-relative, and an auto-tuner is a heuristic, not a proof.**

### CoreCycler (sp00n) — the de-facto reference for what all-core tests miss

CoreCycler is the tool the community uses to validate a Curve Optimizer setting, and it is the closest
existing model for the *oracle* half of 2c ([github.com/sp00n/corecycler](https://github.com/sp00n/corecycler)):

- **Per-core, single-thread, full-boost testing.** Its stated reason: modern CPUs boost *higher* under a
  single light load than under all-core load, so an all-core stress test never exercises the worst
  frequency/voltage corner. It tests one core at a time for this reason, and recommends testing **both**
  light (SSE) and heavy (AVX/AVX2) loads — "AVX/AVX2/AVX512 does stress the CPU more than the SSE mode …
  you should indeed test both scenarios". **Verified** (readme FAQ, and community explanation that SSE is
  intentionally the lighter load that lets the core boost higher).
- **`suspendPeriodically`**, on by default, "tries to simulate load changes by periodically suspending and
  resuming the stress test program" — i.e. it deliberately targets the idle/load **transitions** where the
  hours-later failure lives. **Verified** (release/thread notes).
- **Error oracle = more than "did not crash".** Detection is the stress program's own computational check
  (e.g. Prime95 rounding errors), process crash/hang, a CPU-utilization cross-check, and — since 0.10.0.0 —
  a **WHEA warning/error treated as a real error when the APIC ID matches the core under test**. **Verified**
  for the WHEA behaviour (GitHub releases); the others are readme-documented.
- **Automatic Test Mode** (0.10.0.0): start at the minimum CO per core, and on an error **increase** the
  offset by `incrementBy` (documented range 1–5), **never more negative**; `enableResumeAfterUnexpectedExit`
  plus a Scheduled Task with autologon to resume after a reboot; a **System Restore Point is recommended
  before starting**. **Verified** (GitHub releases and the config comments).
- **The cost math, from its own readme:** a "12 h prime-stable" target on a 12-core CPU is
  **12 × 12 = 144 hours**, because each core must soak separately. **Verified** ([readme as quoted on GitHub](https://github.com/sp00n/corecycler)).
- Its core insight, in substance: **all-core stress is ineffective for single-core stability precisely
  because cores cannot boost as high under all-core load.** **Verified** in substance (readme and the
  overclock.net thread).

### `Daaboulex/linux-corecycler` — the Linux counterpart, and the safety blueprint

This is the closest analogue to anything we would build: per-core testing over the AMD SMU **plus an
automatic, crash-safe tuner** ([github.com/Daaboulex/linux-corecycler](https://github.com/Daaboulex/linux-corecycler)).
Its README is specific enough to cite directly (**verified**, primary):

- **Crash-safe write-ahead journal** — every CO value is recorded **before** it is applied, so no sequence
  of crashes/reboots can lose track of what was last written.
- **`CO=0` (stock) is the only floor it trusts.**
- **Resume-crash circuit breaker** — a profile that keeps crashing on resume is **forced to stock and
  quarantined**, so the machine cannot be looped into re-crashing.
- **Oracle = MCE events + self-checking compute workloads**, explicitly "correctness over 'did not crash'".
- **Thermal protection**: a configurable limit (default 95 °C) pauses testing, and the tuner **fails
  closed** if no temperature sensor is readable.
- **Everything volatile / never touches BIOS** — writes go only through the SMU and reset on reboot.

This is concrete evidence that the approach is **buildable in our shape** (SMU writes, volatile, no
firmware), and it is the blueprint to copy if 2c is ever built.

### Mapping the prior art onto this platform (the payoff)

- **The mechanism is exactly 2c, and nothing exotic.** Every tool above is the same loop: write an offset
  into the SMU (or BIOS), run a load, read an oracle, step back on error. There is no secret technique our
  document was missing.
- **But all of them are per-core on desktop Ryzen, and our granularity is not.** On Strix Point the rail
  follows the **mildest core in its cluster** (`curve-optimizer-strix-point.md`), so the finest useful
  setting is **per cluster — two CPU domains — plus the iGPU**, which the app already exposes. A per-core
  search does not transfer 1:1: a per-core sweep would tune the mildest core and leave the rest inert. And
  AMD itself refuses to auto-derive the **GFX/iGPU** rail, so an auto-tuner here would be even narrower
  than Ryzen Master's.
- **The expensive part is the oracle and the load, not the SMU write.** Every tool above brings its own
  stress engine (Prime95/y-cruncher/its own kernels), its own computational error check, its own crash/hang
  detection, and its own WHEA/MCE reader. The app has **none** of these (§3), and that — not the mailbox
  transaction, which we already have — is the real cost of 2c. This is the single most important prior-art
  finding.
- **The prior art is built around exactly the failure mode we documented.** CoreCycler exists because
  all-core stress misses the single-core high-boost corner; `suspendPeriodically` exists to hit idle/load
  transitions; AMD ships a Difference Offset knob and calls its derived values "a starting point". A short
  sweep on this hardware would therefore **systematically overshoot**, for the reason
  `curve-optimizer-strix-point.md` records: the failure is hours-later-at-idle, not visible under a short
  load. This is the strongest argument for the recommendation below.
- **Therefore, if 2c is ever built, copy the prior art's safety model, not its speed:**
  - follow **AMD's model** — sweep to a *proposal*, then hand it to the user to back off (Difference
    Offset) and validate on their own workloads; never present the derived value as final; and
  - follow **linux-corecycler's model** for the plumbing — a write-ahead journal, `CO=0` as the only trusted
    floor, a resume-crash circuit breaker that forces stock and quarantines a repeatedly-crashing profile,
    a correctness-based oracle, and a thermal cutoff that fails closed; and
  - never **silently auto-persist** a swept offset as trusted, and keep **2a + 2b as the primary
    deliverable** — the shipped tools all agree that the automatic pass is a starting point, and the durable
    value is the watchdog that backs it out when it fails.

---

## 3. Instability detection — the hard part, per OS

> **HISTORICAL/UNIMPLEMENTED (2026-09-27).** This section specifies the detection layer that fed the watchdog.
> The watchdog and its canary/sentinel were REMOVED (§2b); the platform-neutral shape and the per-OS readers
> below were never built. Read it as design, not as a description of shipped code.

The watchdog's policy is simple and portable. The detection is neither, and it is where the app must be
honest about what it can and cannot see. This section specifies a platform-neutral shape first, then what
each OS can actually feed it.

### 3.1 The platform-neutral shape (the layering the app insists on)

The app's rule is **Infrastructure → Application → Domain**, and `*.Windows.cs` / `*.Linux.cs` live only
under `Infrastructure/` and hold **I/O and nothing else** (the split `CurveOptimizerPolicy` documents at
length, because the test project cannot compile the OS files). The watchdog must follow the same shape:
the *decision* is pure and testable offline; the *reading* is per-OS infrastructure; the port is a
nullable `Device` slot so a machine without a Curve Optimizer never grows a monitor.

Proposed pieces, following existing names and files:

| layer | piece | what it is |
|---|---|---|
| Domain | `InstabilityEvent` (record), `InstabilityKind` (`Corrected`, `Fatal`, `UncleanShutdown`, `Other`), `SessionEnding` (`Clean`, `Unclean`, `Unknown`), `InstabilityReport` | value types; no I/O, no OS names |
| Domain | `IInstabilityLog` (`Domain/Ports.cs`, beside `ICurveOptimizer`) | `InstabilityReport Read(TimeSpan window)` — recent instability events plus how the last session ended. I/O only, like `ICurveOptimizer` |
| Domain | `UndervoltWatchdog` (new file, pure) | `Verdict Decide(InstabilityReport report, bool undervoltWasActive)` → `ReapplyAsIs` / `StepBack` / `Stock` / `Suspect`. Reasons **only** over the Domain types above |
| Application | a thin use case (e.g. `GuardUndervolt`, beside `ApplyUndervolt`) over an `IUndervoltHealthTarget` | reads the report, asks the policy, orders "step back, remember, mark suspect". Mirrors `IUndervoltTarget`/`ApplyUndervolt` in `Application/Undervolt.cs` |
| Infrastructure | `WindowsInstabilityLog.cs` / `LinuxInstabilityLog.cs` | the per-OS readers in §3.2/§3.3, plus the app's own clean-exit sentinel |
| Infrastructure | `Device.InstabilityLog` slot (`Infrastructure/Composition/Device.cs`) | nullable, filled by `GenericDevice.InitPlatform` |
| Composition | `GenericDevice.Linux.cs` / `GenericDevice.Windows.cs` | wire it **only when `CurveOptimizer != null`** |
| Composition | `LaptopService.ApplyStartupState` | consult the watchdog **before** `Reconciler.Reapply(ReapplyTrigger.Startup)` |

Two layer notes that decide the shape:

- **The step-back arithmetic cannot live in Domain.** `CoAxis` and `OffsetCounts` are Infrastructure by
  the owner's ruling (the third of the "three walls" in `docs/open-decisions.md`), and a Domain policy may
  not name `CoPreset` or the rails. So `UndervoltWatchdog` returns an **intent** (`ReapplyAsIs`,
  `StepBack`, `Stock`, `Suspect`), and the Infrastructure target does the per-rail clamp and the graph
  write through the existing `CoAxis`/`RememberRails` path. This is the same boundary `Application/Undervolt.cs`
  already documents.
- **Gating.** `IInstabilityLog` is a nullable slot, and composition sets it only where a Curve Optimizer
  exists (`if (RyzenCurveOptimizer.TryCreate() is { } co) { CurveOptimizer = co; InstabilityLog = ...; }`).
  A machine with no undervolt never reads an event log at all, and the UI section never appears.

The startup ordering is the load-bearing part. `LaptopService.ApplyStartupState` (`LaptopService.cs`)
currently calls `Reconciler.Reapply(ReapplyTrigger.Startup)`, which defers `ModeAxis.Co` to the pool
(`ReapplyPlan.RunsOffTheCallersThread`). The watchdog must run **before** that deferral is scheduled, so
that `ApplyModeCo` sees the adjusted preset. The cleanest place is a guard at the top of
`ApplyStartupState`, executed off the UI thread, whose result mutates `CoPresets` under `_state` before the
re-apply reads them. It must not hold `_state` across the event-log read (an event-log query is slow, and
the lock must never span a hardware or I/O call — `docs/domain-refactoring-plan.md` §4).

### 3.2 Windows

The good news is that Windows has a real, queryable instability log and the app runs elevated
(`requireAdministrator` in `app.manifest`). The bad news is that the natural .NET API is **not** present in
this build without a new dependency.

**`System.Diagnostics.EventLog` / `EventLogReader` need a package reference.** Verified empirically: a
scratch `net10.0-windows` project that calls `new EventLog("System")` fails to compile with
`CS1069` ("the type was forwarded to the assembly `System.Diagnostics.EventLog`"), because on modern .NET
that assembly is a **NuGet package**, not part of the `Microsoft.NETCore.App` / `Microsoft.Windows.SDK`
projection, and this repo does not reference it (`obj/project.assets.json` contains no
`System.Diagnostics.EventLog`). Adding it would be a new dependency. Because the repo is Native-AOT and
deliberately avoids such packages (`System.Management` is refused for exactly this reason), the
dependency-free routes are:

- **P/Invoke over `wevtapi.dll`** — `EvtQuery` / `EvtNext` / `EvtRender` / `EvtGetEventInfo`, the native
  Windows Event Log API `EventLogReader` itself wraps. This matches the repo's existing posture (hand-rolled
  COM interop in `Infrastructure/Vendors/Generic/WbemInterop.Windows.cs`) and is AOT-friendly. **Not yet
  verified** that the specific event data (`EventData` name/value pairs such as `Component`, `Processor
  APIC ID`, `BugcheckCode`) renders cleanly through `EvtRender`; that is a small spike to run before
  committing to it.
- **The existing WMI interop** (`WmiSession`/`WmiInvoker`) querying `Win32_NTLogEvent` from the `System`
  log. Reuses code already proven AOT-safe, but `Win32_NTLogEvent` is slow and its coverage of provider
  detail is thinner. A fallback, not the first choice.

Reading the `System` log does **not** require elevation in principle — the default security descriptor for
the System log grants Interactive Users read (`(A;;0x1;;;IU)` in Microsoft's documented SDDL sample) — but
the app already runs elevated, so this is moot.

**Windows signals, with a verified/unverified flag.** All ID meanings below were checked against public
sources (Microsoft Learn for the shutdown/bugcheck IDs; vendor and community documentation for the WHEA
IDs). **None of them has been measured on the AN18-61** — see §5.

| signal | source / log | what it means | stable-undervolt indicator? | flag |
|---|---|---|---|---|
| WHEA-Logger **1** | `Microsoft-Windows-WHEA-Logger`, System | a **fatal** hardware error; the record is in the event's data section | strong, once a cause is implicated | verified (meaning), unverified on this machine |
| WHEA-Logger **18** | same | **fatal** hardware error, often "Processor Core / Machine Check Exception / Cache Hierarchy Error"; the WHEA_UNCORRECTABLE_ERROR (`0x124`) class | strongest single signal; this is what an unstable Curve Optimizer is reported to raise | verified (meaning), unverified on this machine |
| WHEA-Logger **46** | same | **fatal**, component Memory | strong, but points at memory rather than the CPU curve | verified (meaning), unverified on this machine |
| WHEA-Logger **19** | same | **corrected** hardware error, "Processor Core / Corrected Machine Check", commonly a cache-hierarchy error | the early warning; a *corrected* core error is the classic "offset too aggressive, machine caught it" signal | verified (meaning), unverified on this machine |
| WHEA-Logger **17** | same | **corrected** hardware error, "PCI Express Endpoint / Advanced Error Reporting (PCI Express)" | **weak / often unrelated** — usually a PCIe link or NVMe/card-reader issue, not the CPU curve | verified (meaning) |
| WHEA-Logger **47** | same | **corrected** hardware error, component Memory | weak / often unrelated to the CPU curve | verified (meaning) |
| WHEA-Logger **20** | — | **could not be verified as WHEA.** "Event 20" appears in Windows logs as a *Kernel-Boot* boot-phase event, not a WHEA error. Do not use it until confirmed | unknown | **unverified — do not rely on it** |
| Kernel-Power **41** | `Microsoft-Windows-Kernel-Power`, System, Critical | "The system has rebooted without cleanly shutting down first"; the event data carries `BugcheckCode` and its parameters. A non-zero `BugcheckCode` means a Stop error, not a power-button hold | strong (unclean session) | verified |
| BugCheck / WER-SystemErrorReporting **1001** | System | "The computer has rebooted from a bugcheck. The bugcheck was 0x…"; carries the code and dump path | strong; `0x124` (WHEA_UNCORRECTABLE_ERROR) is the one to look for | verified |
| EventLog **6008** | System, Error | "The previous system shutdown … was unexpected" | strong (dirty shutdown) | verified |
| EventLog **6006** | System, Information | "The Event log service was stopped" — a clean shutdown | clean marker | verified |
| EventLog **6005** / **6009** | System | event-log service started / OS started | boot marker | verified |
| Kernel-Power **109** | `Microsoft-Windows-Kernel-Power`, System, Information | "The kernel power manager has initiated a shutdown transition. The last boot's success status was …" — logged on a clean shutdown transition | clean/unclean marker | verified (meaning) |
| Kernel-General **12** / **13** | System | OS started (`12`) / OS is shutting down (`13`) | boot/shutdown markers | verified |

**Which of these actually indicate an unstable undervolt, and which are noise.** The corrected events are
the ambiguous ones and must be filtered, not counted blindly:

- A **fatal** event (1, 18, 46) or Kernel-Power 41 / BugCheck 1001 is unambiguous: the session did not
  survive. The watchdog should treat these as a step-back trigger.
- A **corrected** event is only a trigger when it points at the CPU: WHEA 19 with `Component` = "Processor
  Core" and a plausible `Error Type` (e.g. Cache Hierarchy Error). WHEA 17 (PCIe AER) and 47 (memory) are
  routinely benign platform noise and should be ignored for the undervolt unless they cluster with a fatal
  event or a Kernel-Power 41. This distinction is the difference between the watchdog helping and the
  watchdog nagging.
- The useful fields to filter on, as they appear in the rendered description and the `EventData` block,
  are `Component` ("Processor Core", "Memory", "PCI Express Endpoint"), `Error Source` ("Machine Check
  Exception", "Corrected Machine Check", "Advanced Error Reporting (PCI Express)"), `Error Type`, and
  `Processor APIC ID` (which could map a fault to a cluster, though the app does not currently know the
  APIC-to-cluster mapping — that is an open question, §5). **Exact `EventData` variable names and the
  qualifiers worth matching should be confirmed against a real event on the target machine before coding.**

**Clean vs unclean, concretely.** Three independent reads, strongest last:

1. Windows' own record: a Kernel-Power 41 and/or EventLog 6008 for the previous boot means unclean; an
   EventLog 6006 (and no 41/6008) means clean. Kernel-Power 109 is the clean-transition marker. This is
   readable from the log and needs no app state.
2. BugCheck 1001 / WHEA fatal events for the previous session's window.
3. **The app's own sentinel**, which is the only signal that also knows *an undervolt was active*: it is
   written when the Curve Optimizer is applied and cleared in `AppController.ExitApp`. Present at startup =
   last session did not end through a clean exit. This is the one that survives an app-kill false positive
   and is the same on both OSes, so it is the primary signal and the log is the corroboration that
   separates "machine fell over" from "app was killed".

### 3.3 Linux

Linux has the hardware-error machinery, but almost none of it is reachable from a non-root, group-granted
app, and this is where the design must be explicit about its limits.

**What exists, and who can read it:**

| signal | path / command | readable by the app? |
|---|---|---|
| MCE per-CPU config | `/sys/devices/system/machinecheck/machinecheck*/` (kernel doc: each CPU has a directory) | mostly root-owned config; not an error log |
| Kernel log MCE lines | `journalctl -k -b -1` / `dmesg` for `mce: [Hardware Error]`, `Machine Check`, `Hardware Error` | only with journal access (user in `systemd-journal`/`adm`) or if `kernel.dmesg_restrict=0`; the four node grants the app already installs do **not** cover this |
| `mcelog` | `/dev/mcelog` | root-only device; may not exist at all |
| rasdaemon | `ras-mc-ctl --summary` / `--errors` | root-only (reads `/dev/mcelog` or rasdaemon's DB), and not guaranteed installed |
| EDAC | `/sys/devices/system/edac/` | partly readable, but **AMD APUs frequently do not populate EDAC memory-controller counters**; do not assume it reports the CPU curve |
| Clean/unclean shutdown | `journalctl --list-boots`, `journalctl -b -1 -n` (a graceful boot ends with a shutdown sequence; a crash ends abruptly), `last -x`/wtmp reboot+shutdown pairs, auditd `SYSTEM_BOOT`/`SYSTEM_SHUTDOWN` | journal needs group access; **wtmp is typically `root:utmp` and not readable**; auditd needs root. The journal is the only one plausibly available |

**What is realistically readable from this app.** The app's Linux privilege model grants four SMU nodes to
`wheel` through a udev rule and a systemd unit (`docs/curve-optimizer-linux.md`); it grants nothing about
journals or MCE. So, out of the box, the app can read **none** of the MCE consumers. The two realistic
signals are:

1. **The app's own sentinel** (same as Windows) — portable, needs no privilege, and is the fallback the
   design leans on.
2. **The kernel log, only if the user's session grants journal read** (user in `systemd-journal` or
   `adm`, which is true on some desktops and not others). When available, a previous-boot `journalctl -b -1`
   filtered for `mce: [Hardware Error]` / `Machine Check` / `Hardware Error` is a genuine MCE signal, and
   an abruptly-ending previous-boot journal is a genuine unclean-shutdown signal. When not available, the
   reader returns `SessionEnding.Unknown` and no events.

**What likely cannot be detected on Linux, and the fallback.** A root-only MCE consumer cannot be read
without widening the app's privileges, which the owner has deliberately kept narrow. `dmesg_restrict`,
missing `mcelog`/`rasdaemon`, and no EDAC on an AMD APU all make a false negative likely. The design's
answer is **not** to pretend: when the Linux reader cannot see the machine's health, the watchdog falls
back to the sentinel alone and, on an unclean session, presents an **explicit user confirmation**
("the last session did not end cleanly while an undervolt was active — keep it, step it back, or revert
to stock") rather than silently deciding. On a clean session with no readable signal, it re-applies as
usual. And it must be stated in the UI that **Linux detection is best-effort** — the same honesty
`docs/curve-optimizer-linux.md` already applies to the un-exercised write path.

A separate limit worth writing down: **the app cannot observe a kernel panic at the moment it happens.**
It is not running, or it is dying with the kernel. Every Linux and Windows signal above is a *post-mortem*
read at the next start, which is precisely why the watchdog is a startup component and why the sentinel
must be written *before* the offset is applied, not after.

---

## 4. Safety, UX and data model

> **HISTORICAL (2026-09-27): the watchdog, sentinel and `CoPreset.Suspect` described in this whole section were
> REMOVED.** The owner replaced auto-revert with the transaction model (§2b): an offset is committed only by the
> manual sliders or the sweep's explicit save, a restart re-applies the last committed value, and the app never
> silently changes or reverts a stored offset. Read §4.1–§4.3 as the design that was built and then deleted; the
> sentinel is not on disk any more and `CoPreset` no longer carries `Suspect`.

### 4.1 Where the state lives

- **The sentinel is not user settings.** It describes one session's OS-level outcome, so it lives in a
  file the app owns (`%AppData%\AcerHelper\` on Windows, `~/.config/AcerHelper/` on Linux — beside
  `gate-stats.log`'s pattern), written when the Curve Optimizer is applied and deleted in
  `AppController.ExitApp`. It carries the mode key and the offsets that were written, so the watchdog knows
  what was live even if `settings.json` changed in between.
- **The suspicion is user settings**, because it is a persisted statement about a preset ("do not trust
  this"). The natural home is `CoPreset` (`Infrastructure/Composition/Settings.cs`), extended with
  `bool Suspect` and a `Dictionary<string,int> LastKnownGood` keyed by rail exactly like `Domains`. A
  missing entry means "no known-good recorded". This keeps it per-mode, survives a restart, and rides the
  existing `Snapshot()`/round-trip discipline (`EverySettingSurvivesTheRoundTrip` will require the new
  members to be copied and persisted, which is the point).
  - A rail-keyed `LastKnownGood` is what makes "step back" reversible: the one-click "keep it" clears
    `Suspect` and leaves the values; "revert fully" sets the rails to stock (`CoViewModel.Reset` already
    does this); "restore last known good" writes `LastKnownGood` back.
- **No firmware, ever.** The offset is SMU state; a power cycle restores stock. The watchdog never writes
  a "safe default" to firmware and never needs a BIOS setting. This remains the recovery path and must be
  stated in the prompt.

### 4.2 How the watchdog interacts with the per-mode graph

- The watchdog runs at **startup only** (v1), before the Curve Optimizer's re-apply. It reads the report,
  decides, and (if stepping back) writes the current mode's `CoPresets` entry under `_state` through the
  same path `RememberRails`/`RememberAllCore` use, marks it `Suspect`, then lets
  `Reconciler.Reapply(Startup)` push the adjusted value. It does **not** introduce a second writer: the
  graph write is still `LaptopService`'s, under the same lock, via `CoAxis`.
- **Mode switching is untouched.** A mode with no entry still `ForceStock`es (`ModeAxis.Co`); a suspect
  preset in another mode is left for that mode's own startup/mode-switch decision. The watchdog should
  apply to whichever mode is current at boot, which is the mode whose offsets were last live.
- **It must not fight the manual slider.** Once the app is running, the watchdog does not touch the
  offsets again: an in-session edit is the user's intent, and `SetCoValues`/`SetCoDomains` clear `Suspect`
  (a manual change is a new, deliberate value). A second, in-session WHEA watcher would be a Phase-2
  question only, and even then it should *notify and offer*, never write while the user is watching — a
  slider that moves itself under the user's hand is the failure mode to avoid.

### 4.3 How it is presented

- **`NotificationCenter`** (`UI/ViewModels/NotificationCenter.cs`) is the right surface: an id like
  `undervolt-suspect:`, raised on the startup that stepped back, surviving a language rebuild, dismissible.
  Its current shape supports an action and a body with one button; the three-way "keep it / step back /
  revert fully" choice needs either a small extension to a multi-button body or a body that opens the
  Tuning drawer, where the three actions can live as ordinary controls. The latter is cheaper and fits
  "nothing new in the notification model" better; recommend the notification carry **one** action ("Review
  undervolt") and the drawer carry the decision.
- **Wording, and it must be plain.** Something on the order of: *"Acer Helper stepped the CPU undervolt
  back to −20 because the last session ended with a hardware error or did not shut down cleanly. The
  undervolt is opt-in and never written to firmware — a restart always restores stock. Keep the new value,
  or revert to stock."* No jargon, no "machine check" without a plain-language gloss, and the fact that
  the app did something on its own is stated first.
- **Consent.** The auto-preset (2a) is an explicit action; the watchdog is an explicit *promise* made when
  the user first enables an undervolt ("if a session fails, the app may step it back and tell you"). It
  must not be enabled by default, and the step-back must always be visible, never silent.

---

## 5. Open questions — what must be measured before implementing

> **SUPERSEDED 2026-09-27.** The compute-error canary and the crash sentinel this section's 2026-09-26 priority
> update refers to were REMOVED (§2b); the owner replaced auto-revert with the transaction model and no detection
> layer currently ships. Every question below is now open again, and none of them gates a shipped feature. The
> canary/sentinel items are kept only as a record of what was measured (and left unmeasured) while they existed.

The design is deliberately gated on measurements the owner has not run. In the order that unblocks the
most:

1. **Do WHEA events fire at all on the AN18-61 for a too-aggressive offset?** Unmeasured. Experiment: from
   a known-stable state, apply a deliberately excessive offset (e.g. cores `-40`, or iGPU `-50`) and leave
   the machine idle for **hours**, then inspect the System log for WHEA 1/17/18/19/46/47 and any
   Kernel-Power 41 / BugCheck 1001. If nothing fires, the Windows watchdog has no oracle and must fall
   back to the sentinel alone — which changes the feature's value entirely and must be known before
   building it.
2. **On this platform, does the corrected/fatal distinction separate "unstable undervolt" from noise?**
   The target machine may raise WHEA 17/47 for its NVMe or card reader at stock; the filter in §3.2 must be
   tuned against the machine's own baseline, measured over days at stock.
3. **Is there any reliable Linux signal on this machine?** Check: is the dev user in `systemd-journal`?
   Does `journalctl -b -1` show MCE lines when an offset fails? Does `rasdaemon`/`mcelog` exist, and does
   `machinecheck`/EDAC report anything on this AMD APU? The expected answer based on public behaviour is
   "mostly no", but it must be measured before the Linux reader is sized.
4. **Does the app get re-applied/relaunched across a kernel panic or an unclean reboot?** The autostart is
   a scheduled task that acts as its own watchdog (`Autostart.EnsureCurrent`); confirm it actually relaunches
   after a crash and how long that takes, because the sentinel read must happen before any re-apply and
   the app's startup order is what guarantees it.
5. **Does the sentinel survive where the app is published?** Confirm the write path under Native AOT
   single-file on Windows and the AppImage on Linux, and that `AppController.ExitApp` is reached on
   shutdown/restart/logoff (it hooks `SessionEnding`) and **not** on sleep (the app stays alive across
   suspend, so the sentinel must not be cleared by a resume).
6. **Which WHEA `EventData` fields and qualifiers are stable enough to match on?** Needs a real event
   captured from the target machine, since the rendered description and the structured data are not the
   same thing.
7. **Is the `wevtapi` P/Invoke route usable under Native AOT, and does it render event data cleanly?**
   A small spike, since it decides between `wevtapi` and the WMI fallback.
8. **Does the `-40`/`-50` floor itself ever produce the failure, or only the extremes?** The relationship
   between offset size and time-to-failure is unknown; the step-back size (half vs one notch) depends on
   it.

---

## 6. Scope, non-goals, and recommended implementation order

**Non-goals.** No firmware writes; no per-core control (the hardware makes it inert, `curve-optimizer-strix-point.md`);
no positive offsets (`OffsetCounts` makes them unrepresentable); no automatic *deepening*; no shipped
stress workload; no new privilege path on Linux (the watchdog reads only what the app can already read, or
asks the user); no new NuGet dependency on Windows (the event log is read through `wevtapi` or the
existing WMI interop, per §3.2). The offset stays volatile, per-mode, and one Reset click from off.

### 6a. What ships, by layer (after the 2026-09-27 watchdog removal)

The watchdog/canary/sentinel were deleted on 2026-09-27 (see §2b); the table below is what remains of the
feature after that removal.

| layer | file | what it is |
|---|---|---|
| Infrastructure | `Infrastructure/Vendors/Generic/CpuLoadTest.cs` | the load tool's contracts (`ICoreAffinity`), the pure policy (`CpuLoadPolicy`) and the runner (`CpuLoadRunner` + the sampler), reused by the sweep |
| Infrastructure | `Infrastructure/Vendors/Generic/CpuStress.cs` | the self-checking integer kernel (`CpuStressKernel`) and its value types, the oracle the sweep uses |
| Infrastructure | `Infrastructure/Vendors/Generic/UndervoltSweep.cs` | the guided sweep's pure policy (`SweepPolicy`, `UndervoltSweep`, `RoughEta`) and its contracts (`IUndervoltSweepTarget`); each `SweepDomain` carries its cluster and whether it is identifiable, and `SweepProgress` carries the stage (`DomainIndex`/`DomainCount`) and `ClusterIdentified` |
| Infrastructure | `Infrastructure/Vendors/Generic/CpuLoadTest.cs` | also the pure cluster partition (`CpuLoadPolicy.PartitionIntoClusters`) and the runner's caller-supplied subset overload, which is how a stage loads only its cluster |
| Infrastructure | `Infrastructure/Vendors/Generic/CoreAffinity.Windows.cs` / `.Linux.cs` | the OS halves fill each physical core's efficiency class (Windows `EfficiencyClass`; Linux from `cpu_capacity`) so the topology can partition the clusters |
| Infrastructure | `Infrastructure/Composition/LaptopService.UndervoltSweep.cs` | the guided sweep's service half: volatile SMU writes, restore-on-every-exit, the domain→cluster projection, the cluster-subset probe (with the all-cores fallback), and the explicit `SaveUndervoltSweep` through the per-mode path |
| Infrastructure | `Infrastructure/Composition/Settings.cs` | `CoPreset` (`AllCore` + `Domains`) — the committed per-mode offsets; no `Suspect` member any more |

**Removed 2026-09-27** (deleted outright, not re-homed): `Infrastructure/Vendors/Generic/UndervoltCanary.cs`,
`Infrastructure/Vendors/Generic/UndervoltWatchdog.cs`, `Infrastructure/Composition/UndervoltSentinel.cs`,
`Infrastructure/Composition/LaptopService.UndervoltCanary.cs`, `Infrastructure/Composition/LaptopService.UndervoltGuard.cs`,
and `CoPreset.Suspect`, together with their tests (`UndervoltWatchdogTests.cs`, `Fakes/FakeUndervoltSentinel.cs`),
the `StartUndervoltCanary()` call in `UI/App.axaml.cs`, and the `_canaryActive` checks in the sweep gate.

**Deliberate omissions from the doc's earlier proposal.** There is no `IInstabilityLog` Domain port and no
`UndervoltWatchdog` in Domain: the doc's §3.1 proposed them, but the owner's layer rule (`docs/domain-layering-map.md`,
`DomainNeutralityTests`) keeps out of Domain any value the Domain's own logic does not branch on — `SessionEnding`,
the intent and the sentinel contract are this feature's vocabulary. Since the owner's ruling of 2026-09-27 (the
automatic undervolt is in substance a wrapper over the manual voltage change, so it is UI + Infrastructure only),
they live in `Infrastructure/Vendors/Generic/` exactly like `ReapplyTrigger` (which stays Application) and
`SweepProbeStatus` (now Infrastructure with them). None of that vocabulary remains after the removal. There is also no `LastKnownGood` member: the shipped step-back only
needed the suspect flag and the stepped value, and adding the "remember the last good" table before anything read
it would be carrying a value no logic branches on — the same rule.

**Order, if the owner later says go.** Each phase is independently useful and independently revertable. Phases 0
and 1 (the canary and the sentinel/step-back) were built and then **removed** on 2026-09-27 under the transaction
model; the Windows/Linux event-log readers (Phases 2–3) remain unimplemented and are not currently planned:

0. **Phase 0 - the compute-error canary (primary oracle). REMOVED 2026-09-27.** Was
   `Infrastructure/Vendors/Generic/UndervoltCanary.cs` + `Infrastructure/Composition/LaptopService.UndervoltCanary.cs`,
   scheduled by the shared `PeriodicSchedule`, reusing the existing kernel/golden. Deleted with the watchdog.
1. **Phase 1 — the sentinel and step-back-before-reapply. REMOVED 2026-09-27.** Was the marker file
   (`UndervoltSentinel.cs`), the pure `UndervoltWatchdog` policy, `CoPreset.Suspect`, the startup guard
   (`LaptopService.UndervoltGuard.cs`, wired before `Reconciler.Reapply(Startup)`) and the `RefreshUndervoltSentinel`
   lifecycle. Deleted; the startup re-apply now re-applies the committed value untouched.
2. **Phase 2 — the Windows reader. DEFERRED.** `wevtapi` (or WMI) reading the System log for the fatal/shutdown
   signals in §3.2, feeding the same report as CORROBORATION of the sentinel. This upgrades the sentinel's
   false-positive (app killed) into a confirmed machine fault; it does not replace the primary compute canary.
3. **Phase 3 — Linux best-effort. DEFERRED.** Journal read where granted, `Unknown` otherwise, with the explicit
   user-confirmation fallback. No privilege widening.
4. **Phase 4 — the optional guided tuner (2c), off by default**, and only if §5's experiments justify it.
   This is the only phase that adds new modes of operation and the only one that carries a "this is not a
   stability proof" warning in the UI. **Model it on the prior art**: AMD's sweep-then-propose (a candidate
   the user backs off with a Difference-Offset-style control and validates themselves) and
   `linux-corecycler`'s crash-safe write-ahead journal, `CO=0` trusted floor, resume-crash circuit breaker,
   correctness-based oracle, and thermal fail-closed — never a silent auto-persist (**Prior art**).

The auto-preset (2a) can ship with Phase 1 as a small UI affordance; it needs no detection and is the
cheapest visible half of "automatic".
