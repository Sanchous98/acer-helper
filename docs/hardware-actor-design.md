# Hardware actor — design, defects and risk

Working document for Wave 5 of `docs/open-decisions.md`: replace the scattered synchronisation primitives
with a single serialized executor that owns the transports. Written **before** any code, because the change
rewrites the concurrency model and the project has no behavioural tests.

Everything below is a read of the tree at v0.32.0. Line numbers are as of that revision.

**Line numbers here are not maintained (noted 2026-09-14).** They were written against the revision current when
each passage was written and have already drifted; treat the symbol names as the anchor and re-locate by name.
The tree has also been reorganised since: `LaptopService` is now six `LaptopService*.cs` partial files, and
`OptionsAssembler` has moved from `UI/` to the repo root (later `Infrastructure/Composition/`, see below;
the namespace in between was `AcerHelper.Application`).

**Paths were also reorganised (2026-09-15), and this document predates that.** Every path quoted below was
written against the old layout; `namespace` now equals the folder path verbatim, and 85 files moved. The
substitutions a reader needs:

| quoted here (pre-2026-09-15) | actually now |
|---|---|
| `Vendors/…` | `Infrastructure/Vendors/…` |
| `Composition/…` | `Infrastructure/Composition/…` |
| `Diagnostics/…` | `Infrastructure/Diagnostics/…` |
| `Features/…`, and the bare `Settings.cs` / `Options.cs` / `AppArgs.cs` at the root | `Domain/…` |
| `LaptopService*.cs` / `OptionsAssembler.cs` at the root | `Application/…` |
| `Program.cs` at the root | `Bootstrap/Program.cs` |
| `Os/…` | `Infrastructure/Vendors/Generic/…` (the folder no longer exists) |
| `AcerHelper.Features`, `AcerHelper.Vendors.*`, `AcerHelper.Os`, `AcerHelper.Composition`, `AcerHelper.Diagnostics` | `AcerHelper.Domain`, `AcerHelper.Infrastructure.Vendors.*`, `AcerHelper.Infrastructure.*` |

**And the service layer left `Application/` (2026-09-18).** The substitution table's row for
`LaptopService*.cs` / `OptionsAssembler.cs` now reads `Infrastructure/Composition/…`, not `Application/…`:
the owner ruled that a "service" over the hardware and the persisted settings graph is Infrastructure, and
the re-apply operation was split with it — `Application/ReapplyPlan.cs` holds the plan (`ReapplyTrigger`,
`Schedule`, `RunsOffTheCallersThread`, `Reflects`) and `Infrastructure/Composition/HardwareReconciler.cs`
executes it. `Settings.cs` and the presets followed to `Infrastructure/Composition/`, `CoAxis`/`OffsetCounts`
to `Infrastructure/Vendors/Generic/`, and the declared-setting CONTRACT stayed in `Domain/DeclaredSetting.cs`.
Every `LaptopService*` symbol named below is unchanged; only its folder is. See
`docs/domain-refactoring-plan.md` §7, item 3.

**And the LampArray layer left `Domain/` too (2026-09-17).** `Domain/LampArrayBridge.cs` and
`Domain/LampArray.cs` are now `Infrastructure/Lighting/LampArrayBridge.cs` and
`Infrastructure/Lighting/LampArray.cs`; the transport types of `Domain/Rgb.cs` (`IRgbController`, `RgbDevice`)
are in `Infrastructure/Lighting/RgbController.cs`, while `RgbZone` and `IRgbDevice` stayed in `Domain/Rgb.cs`.
That move also added lines near the top of `LampArrayBridge.cs`, so every line number below it drifted a second
time — the symbol names remain the anchor, as noted above.

The *analysis* is unaffected — no claim in this document rests on a folder's name, and the one path that
mattered to a defect (`WmiSession`'s gate) is identified by symbol. Only the addresses moved. Rationale and the
resulting `Vendors/Generic` naming oddity: `04229ff` (the 85-file move) + `dd9f8d9` (the stale references).

**Wave numbers here are the pre-2026-09-17 plan's (noted 2026-09-17).** They are `docs/refactoring-plan.md`'s
wave list — the plan this document was written against — which has since been deleted and replaced by
`docs/domain-refactoring-plan.md`, and that plan re-specified its waves (see its §5 and the blocks under each
wave, `f4483ec` onwards). So a number here does not name the same work as the same number there: in this
document **Wave 5 is the actor** — which `docs/open-decisions.md` §1, its sibling on the same numbering, still
calls «Волна 5» — and "Wave 5, step 5" is the actor's own Step 5 from Part 4.7 below. Where a note below cites
a commit hash, that hash is its anchor — the number is not.

## Scope

This is an inventory and a design, not a patch. Nothing here is implemented. Two boundaries are load-bearing
and are repeated wherever they matter:

- **The UI-thread boundary is not removable.** `Dispatcher.UIThread.Post` is a marshalling requirement of
  Avalonia, not a synchronisation choice. An actor cannot absorb it, and a design that claims to is wrong.
- **Some coordination is inter-process.** `Global\Access_PCI` and the LampArray device node are shared with
  other tools and with Windows itself. An in-process actor cannot replace them, and the design must not
  quietly assume it can.

A third, softer boundary: serialization is not the only thing the primitive count is doing. Some of these
primitives are **rate limiting and coalescing** (the HID writer threads), some are **lifecycle publication**
(`volatile bool _stopping`), and some are **verification protocols** (`VerifiedHwValue`'s desired latch). Only
the first kind is what an actor replaces. Conflating them is the main way this rewrite goes wrong.

---

# Part 1 — Synchronisation inventory

## 1.1 Counting rule

The plan's figure of "91 primitives in 22 files" counts by a rule it doesn't state. This inventory counts
**every occurrence**: one row per declared gate, plus one row per use site, plus one row for each standalone
mechanism. That yields **137**, and the difference from 91 is the ~50 call sites that are not declarations
(60 `lock` statements against 13 gate objects; 10 `Task.Run` hand-offs; 15 UI-thread posts).

| Category | Occurrences |
|---|---|
| Exclusion gates (`Lock` / `object` declarations) | 13 |
| `lock` statement sites | 60 |
| `Monitor.Wait` / `Pulse` sites | 7 |
| `Interlocked` sites (one protocol) | 6 |
| `volatile` fields | 7 |
| Named background threads | 4 |
| `DispatcherTimer` instances | 9 |
| `Mutex` (one of them cross-process) | 2 |
| Task-continuation chain (`HwSerial`) | 1 |
| `Task.Run` hand-off sites | 10 |
| `Dispatcher.UIThread.Post` marshalling sites | 15 |
| `CancellationToken` plumbing (updater only) | 3 |
| **Total** | **137** |

## 1.2 Exclusion gates

Scope column: **HW** = the gate is held across hardware I/O; **MEM** = in-memory state only; **BOTH**.

| # | Gate | Declared | Protects | Serialises against | Scope |
|---|---|---|---|---|---|
| G1 | `WmiSession.Gate` | `Infrastructure/Vendors/Generic/WmiSession.Windows.cs:28` | nothing in-process — it is a pure interlock | every other WMI/EC transaction in the process; **all 4 other WMI-using layers** | HW |
| G2 | `LaptopService._state` | `LaptopService.cs` `_state` | the whole `Settings` graph (`Settings.cs`), `_onAc`, the per-source slots, `_fanCurve`, and `Save()` | G1 (nested, always after), `_lampGate` (never held together), and every UI-thread action | BOTH |
| G3 | `LaptopService._lampGate` | `LaptopService.Lighting.cs` `_lampGate` | `_lampArray`, `_lampArrayBuilt` (lazy build) | nothing — deliberately NOT `_state` | MEM |
| G4 | `LampArrayBridge._gate` | `Domain/LampArrayBridge.cs:51` | `Enabled`, `_worker`, `_layout`, `_written`, `_frame`, `_stopping` | G5 (never held together) | MEM |
| G5 | `LampArrayBridge._apply` | `Domain/LampArrayBridge.cs:52` | a frame apply (worker thread) vs `Reassert` (caller thread) — both write the same zones through `_rgb` | G4 (never held together) | HW |
| G6 | `PawnIo._gate` | `Infrastructure/Vendors/Generic/PawnIo.Windows.cs:51` | the module handle: `Execute` vs `Dispose` | nothing in-process — the real interlock is the `Global\Access_PCI` mutex in G12 | HW |
| G7 | `RyzenCurveOptimizer._gate` | `Infrastructure/Vendors/Generic/RyzenCurveOptimizer.Windows.cs:165` | lazy `Io()` construction + `Dispose` + the whole `Transact` sequence | the cross-process mutex (see 1.4) | HW |
| G8 | `Clamshell._sync` | `Infrastructure/Vendors/Generic/Clamshell.cs:19` | `_applied`, `Enabled`, and `Clamshell.Windows`'s `_originalLidAction` | nothing — but `Evaluate()` runs from **three** threads: two `SystemEvents` callbacks, the UI thread, and the background refresh pass | BOTH |
| G9 | `LampArrayTransport._gate` | `Infrastructure/Vendors/Generic/LampArrayTransport.Windows.cs:50` | `_control`, `_frames`, `_swDevice` handles | nothing | HW |
| G10 | `EneHidController._gate` | `Infrastructure/Vendors/Acer/EneHidController.cs:124` | `_pending` (`List<byte[]>`), `_stopping`, `_worker` + the `Monitor.Wait`/`Pulse` park | nothing — the writer thread is the only consumer | MEM (+HW on the writer thread) |
| G11 | `AcerEcHidController._gate` | `Infrastructure/Vendors/Acer/AcerEcHidController.cs:100` | `byte? _pending`, `_stopping`, `_worker` + `Monitor.Wait`/`Pulse` | nothing | MEM (+HW on the writer thread) |
| G12 | `HwSerial._gate` | `UI/ViewModels/OptionsViewModel.cs:14` | `_tail` — the `Task.ContinueWith` chain | nothing | MEM |
| G13 | `LightViewModel._readGate` | `UI/ViewModels/LightingViewModel.cs:135` | `_reading`, `_readPending` — coalescing of the off-thread brightness read | nothing | MEM |

### Gate → call sites

```
G1  2   Infrastructure/Vendors/Generic/WmiSession.Windows.cs:83,116
G2  33  the 33 `lock (_state)` statements in the six `LaptopService*.cs` parts — note the count is
        stale: it predates three added scalar accessors and the locking fixes; measured today: 38
        (re-measured 2026-09-17: 42 — wave 6 added one to each of the four profile-taking overloads,
        `CurrentFan(cur)`, `CurrentGpuOc(cur)`, `CurrentCo(cur)` and `CurrentCoDomains(cur)`, and 38
        was already the count at the `LaptopService` split, not the 33 above)
G3  1   `LaptopService.Lighting.cs` `LampArray` (its getter's `lock (_lampGate)`)
G4  2   Domain/LampArrayBridge.cs:88,117
G5  3   Domain/LampArrayBridge.cs:145,178,182
G6  2   Infrastructure/Vendors/Generic/PawnIo.Windows.cs:92,125
G7  3   Infrastructure/Vendors/Generic/RyzenCurveOptimizer.Windows.cs:258,304,361
G8  3   Infrastructure/Vendors/Generic/Clamshell.cs:30,41,54
G9  2   Infrastructure/Vendors/Generic/LampArrayTransport.Windows.cs:84,119
G10 3+4 Infrastructure/Vendors/Acer/EneHidController.cs:145,176,195 + Monitor 156,159,178,195
G11 3+3 Infrastructure/Vendors/Acer/AcerEcHidController.cs:88,110,135 + Monitor 92,112,135
G12 1   UI/ViewModels/OptionsViewModel.cs:19
G13 2   UI/ViewModels/LightingViewModel.cs:209,221
```

(Total: 13 declarations + 60 `lock` sites + 7 `Monitor` sites = 80 occurrences.)

The three biggest facts in this table:

1. **G1 is process-wide and static.** Every EC-touching path in the app — gaming profile, fan, sensors,
   battery, LCD overdrive, backlight, Fn lock, USB charging, charge limit, display tint — funnels through one
   `static readonly Lock`. It is already the serialization point an actor is meant to provide; it is just not
   an object with a queue.
2. **G2 is held across hardware I/O.** `ApplyCustom` (`LaptopService.Fans.cs` `ApplyCustom`) takes `_state` and calls
   `ApplyFan` → port → G1. So the declared lock order "`_state` → WMI Gate" is real and is exercised on every
   refresh tick. This is what makes G2 a *latency* risk on the UI thread, not just a data race risk.
3. **G8's comment is the only place in the tree that documents a three-thread fan-in.** `Evaluate()` is
   called from `SystemEvents.PowerModeChanged`, `SystemEvents.DisplaySettingsChanged` and the background
   refresh pass. It is correct — but it is the pattern an actor is supposed to centralise.

## 1.3 Non-gate primitives

### Interlocked — the hand-rolled single-flight (6 sites)

`UI/AppController.cs:435,437,441,444,512,514`. Guards `_busy` / `_rerun` (`:36-37`). This is the only
lock-free protocol in the tree and it is load-bearing: it is what keeps a second refresh from queueing behind
a pass that is blocked on a stalled EC.

```
if (CAS(_busy, 1, 0) != 0) { Exchange(_rerun, 1); if (CAS(_busy, 1, 0) != 0) return; }
try { _ = Task.Run(BackgroundPass); } catch { Exchange(_busy, 0); }
```

The re-CAS at `:441` closes a real race (a pass finishing between the failed CAS and the `_rerun` write), and
the `finally` at `:512-514` re-enters via `Refresh()` on the pool thread. **This is the piece the actor
replaces wholesale**, and the re-entrancy is the part that must survive.

### volatile fields (7)

| Field | Where | Written by | Read by | Correct? |
|---|---|---|---|---|
| `_lastModeKey`, `_lastProfileId` | `AppController.cs:34,35` | background pass | background pass, seeded on UI thread in ctor and `RebuildForLanguage` | Yes — single-flight makes it effectively single-threaded |
| `_stopping` | `Domain/LampArrayBridge.cs:54` | UI thread under G4 | worker thread, unlocked | Yes — a benign one-way flag |
| `_stopped` | `Infrastructure/Vendors/Generic/LampArrayTransport.Windows.cs:55` | `Stop()` | `WaitFrame` | Yes |
| `_createResult` | `LampArrayTransport.Windows.cs:354` | `[UnmanagedCallersOnly] CreateCallback` (driver thread) | `Open()` poll loop | Yes — the only cross-thread field the actor cannot own; the callback is invoked by the OS |
| `_stopped` | `ResumeWatcher.Linux.cs:15` | UI thread | gdbus stdout pump thread | Yes |
| `_closing` | `Infrastructure/Vendors/Acer/AcerHotkeys.Linux.cs:21` | UI thread | evdev reader thread | Yes |

**Corrected 2026-09-17: there are eight volatile fields today, not seven.** Wave 6 added
`AppController._cpuPrimed` — written by the background pass, cleared on the UI thread in `RebuildForLanguage`
when a rebuild puts the CPU-power row's placeholder back — and it is correct for the same reason as the first
row above: the pass is single-flight. The six rows still describe the fields they name.

Counter-example worth noting: `EneHidController._stopping` (`:127`) and `AcerEcHidController._stopping`
(`:103`) are **not** volatile, and are correct because they are only ever touched under G10/G11 — including
inside the `Monitor.Wait` predicate, which is the one place a plain bool is safe by construction.

### Named background threads (4)

| Thread | Started | Owns | Termination |
|---|---|---|---|
| `lamparray-bridge` | `Domain/LampArrayBridge.cs:106` | blocking `WaitFrame` + paced zone apply | `_stopping` + `_transport.Stop()` + 1 s join (`:129`) |
| `ene-hid-writer` | `Infrastructure/Vendors/Acer/EneHidController.cs:39` | draining `_pending`, 10 ms pacing | `Monitor.Pulse` + 1 s bounded join |
| `acer-ec-hid-writer` | `Infrastructure/Vendors/Acer/AcerEcHidController.cs:65` | draining the single `_pending` slot | same |
| `acer-hotkeys` | `Infrastructure/Vendors/Acer/AcerHotkeys.Linux.cs:47` | evdev read loop | `_closing` |

Plus two OS-owned pumps that are not threads the app creates: the gdbus stdout pump
(`ResumeWatcher.Linux.cs:33-36`) and `SystemEvents`' hidden window thread, and the LidWatcher top-level
window — which, despite `LidWatcher.cs`'s "window-message thread" comment, is created on the **UI thread**
(`LidWatcher.Windows.cs:67`, and `Subscribe()` is called from `LightingCoordinator`'s ctor), so its `WndProc`
runs on the dispatcher. The comment is stale; the behaviour is fine.

### DispatcherTimer (9)

| Timer | Interval | Purpose | Actor relevance |
|---|---|---|---|
| `AppController.cs:87` | 3 s | the refresh poll | **replaced** by an actor poll command |
| `LightingCoordinator.cs:85` | 400 ms × 8 | post-switch lighting re-apply burst | survives — it is a UI-side retry policy |
| `UI/FanSpinner.axaml.cs:38` | ~Tick ms | animation | survives |
| `UI/ViewModels/CoViewModel.cs:26` | 400 ms | undervolt debounce | survives |
| `UI/ViewModels/GpuViewModel.cs:15` | 400 ms | GPU OC debounce | survives |
| `UI/ViewModels/FansViewModel.cs:25,26,27` | 400 ms × 3 | fan/curve debounce | survives |
| `UI/ViewModels/LightingViewModel.cs:133` | 120 ms | lighting debounce | survives |

All eight survivors are **input debouncing or animation on the UI thread**. They are not synchronisation and
must not be folded into an actor: debouncing is a policy about the user's hand, not about the hardware.

### Task.Run hand-offs (10)

`AppController.cs:95` (autostart heal), `:127` (`setup.Install`), `:286` (`HardwareAccess.Install`),
`:368` (`SetCo`), `:443` (`BackgroundPass`), `:492` (`ApplyModeCo`); `LaptopService.cs` `ApplyStartupState` (the `Task.Run` for `ApplyModeCo`
startup), `LaptopService.cs` `ApplyStartupState` (the `Task.Run` for `LampArray.Enable`); `LightingCoordinator.cs:255` (resume re-assert of GPU OC + CPU power +
CO); `LightingViewModel.cs:214` (brightness read loop).

**Corrected 2026-09-17: three of those ten sites are gone, and the work they did is now one site.** The two
`ApplyModeCo` hand-offs (`AppController.cs:492` and `ApplyStartupState`'s) and the resume re-assert
(`LightingCoordinator.cs:255`) all became `HardwareReconciler.Reapply`'s single deferred `Task.Run`, which
drives the trigger's off-thread axes under ONE catch (`c384d12`). A site now names only the MOMENT it is
(`ReapplyTrigger`); the hand-off is the reconciler's. Measured today: **eight** `Task.Run` hand-offs of this
kind. The same substitution applies wherever those three sites are named below — **T5**, **T7**, **D20**,
**D22**, **R10**'s hand-guard, and Part 4.7's Step 4 — none of which is a live site any more.

Four of these exist for the same stated reason — *the SMU mailbox / the PnP publish can block for seconds* —
and each one independently re-derives "get it off the calling thread". That is the strongest concrete argument
for the actor: the app currently has four ad-hoc answers to one problem.

### Task-continuation chain (1)

`HwSerial` (`OptionsViewModel.cs:12-23`). `_tail = _tail.ContinueWith(...)` under `_gate`. Preserves click
order and keeps the write+readback pair atomic. `VerifiedHwValue<T>` (`:32-69`) layers the `_desired` latch on
top so a superseded snap-back is dropped.

Note the shape: this is **a queue with order preservation and a generation counter**. It is a hand-rolled,
single-purpose actor for one control. The general actor subsumes it exactly.

### Dispatcher.UIThread.Post (15)

`AppController.cs:184,244,287,305,372,382,507`; `UI/FlyoutCoordinator.cs:103`;
`UI/LightingCoordinator.cs:106,113`; `UI/MainWindow.axaml.cs:65,171`; `OptionsAssembler.cs` `RunSet` (its `Dispatcher.UIThread.Post` call);
`UI/ViewModels/OptionsViewModel.cs:36`; `UI/ViewModels/LightingViewModel.cs:220`.

**All 15 survive.** They are the Avalonia contract: `SystemEvents` callbacks, `LidWatcher`'s `WndProc`,
`ResumeWatcher`'s and AcerHotkeys' threads, the gdbus pump, the HID writer threads and the `[UnmanagedCallersOnly]`
lamp callback all arrive off the UI thread and must come back to it. An actor can *produce* the values that
get posted; it can never do the posting for the caller without taking a UI dependency.

**Corrected 2026-09-17: two entries in the list above have moved, and the count is 16.** `OptionsAssembler`
no longer calls `Dispatcher.UIThread.Post` at all — it takes the marshaller as a `post` parameter (which
`RunSet` calls), supplied by `AppController.BuildUi` and defaulted in `OptionsViewModel` and
`LightingViewModel`, exactly so that the Application layer needs no UI toolkit. And `LightingCoordinator` has
**three** posts today, not two: the resume watcher, the LidWatcher (whose callback the doc's T13 argues is
already on the UI thread — the post is the defensive marshal) and `LampArrayBridge.OwnerChanged`. Nothing in
the paragraph above changes: every one of them survives, and the actor still cannot do the posting.

### CancellationToken (3)

`UpdateChecker.cs:19`, `WindowsUpdater.cs:52`, `AppImageUpdater.cs:27`. None protect shared state, none touch
hardware. **Out of scope** — leave them alone.

## 1.4 Cross-process and cross-application coordination — **not the actor's to absorb**

| # | Point | Where | Why an in-process actor cannot replace it |
|---|---|---|---|
| X1 | `Global\Access_PCI` | `RyzenCurveOptimizer.Windows.cs:137`, opened at `:606`, held across the whole transaction at `:410-448` | It is a **named global mutex shared with Ryzen Master, HWiNFO, RyzenAdj** and anything else poking the SMU mailbox. The code's own comment (`:442`) says per-access locking would let another agent's message execute against our arguments — so the mutex covers *the transaction*, not one register write. An actor serializing only this process changes nothing about the other processes. |
| X2 | SMU mailbox state on the die | same file, `WaitIdle`/`PollResponse` | The mailbox is a physical resource. The mutex is only advisory; a tool that ignores it still corrupts. The 5 s `MutexWaitMs` (`:138`) is a *contention budget against other applications* — the actor must not shorten it. |
| X3 | PawnIO kernel driver | `Infrastructure/Vendors/Generic/PawnIo.Windows.cs` | A shared machine-wide driver (signed ring-0 modules). `_gate` protects our handle; the driver is not ours. |
| X4 | LampArray virtual device node | `Infrastructure/Vendors/Generic/LampArrayTransport.Windows.cs` | Other processes *write frames to our device* — that is the whole point (Windows Dynamic Lighting, G HUB-style apps). The actor owns the transport handle; it does not own the frame producer, and `LampArrayBridge._apply` (G5) exists precisely because the caller and the worker race on the same zones. |
| X5 | Single-instance mutex | `Program.cs:11,16`, `AbandonedMutexException` at `:24` | Deliberately inter-process: it is what makes the self-update restart race safe. |
| X6 | `SystemEvents` / `RegisterPowerSettingNotification` | `ResumeWatcher.Windows.cs:9`, `LidWatcher.Windows.cs:70` | OS-delivered events. The actor can be a **consumer**; it cannot own the delivery. |

`X1` is the one that most needs saying out loud, because it is the only place the tree already coordinates
with a *different program*, and it is the site a casual reading would mark "redundant with the actor". It is
not redundant. The correct end state is: the actor serializes *our* transactions; `Global\Access_PCI`
serializes *everyone's*.

## 1.5 Summary

| Outcome | Count | Which |
|---|---|---|
| **Deleted** — absorbed by the actor | **82** | G1 (3), G2 (34), G3 (2), G4 (3), G5 (4), G6 (3), G7 (4), G8 (4), G9 (3), G12 (1) — the 6 `Interlocked`, `_lastModeKey`/`_lastProfileId`, the 3 s poll timer, `HwSerial`, and 7 of the 10 `Task.Run` sites (the 4 blocking-I/O hand-offs + `BackgroundPass` + `ApplyModeCo` + the brightness loop) |
| **Kept, in-process, outside the actor** | **53** | G10, G11 (15 — the HID writer threads keep their queues and pacing); 5 `volatile` flags; 4 threads; 8 timers; 3 `Task.Run` (installers + autostart heal); all 15 `Dispatcher.UIThread.Post`; 3 `CancellationToken` |
| **Cross-process — must stay** | **2** | `Global\Access_PCI`, the single-instance mutex |
| | **137** | |

---

# Part 2 — Thread and execution inventory

## 2.1 What actually runs concurrently

| # | Executor | Where it starts | What state it touches | What it blocks on | Must marshal to UI? |
|---|---|---|---|---|---|
| T1 | Avalonia UI thread | framework | every view-model, `Settings` (unguarded reads), `LaptopService` methods, `_lampGate` | G2, G1 (see 3.4) | — it *is* the UI thread |
| T2 | `BackgroundPass` pool task | `AppController.cs:443`, body `:450-516` | `Settings.TurboToggles`, `LightingCoordinator._pendingId`, `_lastModeKey/_lastProfileId`, `_fanCurve` | G2 then G1; `QueryDisplayConfig` | Yes — `:507` posts `UiPass` |
| T3 | `Refresh()` re-entry on the pool thread | `:514` | `_busy`/`_rerun` | same as T2 | — |
| T4 | `SetCo` pool task | `:368` | G7 → X1 (up to 5 s) | 5 s mutex wait | Yes — `:372` posts the failure |
| T5 | `ApplyModeCo` pool task | `:492`, `LaptopService.cs` `ApplyStartupState` (the `Task.Run` calling `ApplyModeCo`) | G7 → X1 | 5 s | No (result never surfaces) |
| T6 | `LampArray.Enable` pool task | `LaptopService.cs` `ApplyStartupState` (the `Task.Run` calling `la.Enable()`) | G9, PnP | driver start, seconds | No |
| T7 | Resume re-assert pool task | `LightingCoordinator.cs:255` | G7/G1/G2 | 5 s | No |
| T8 | `ene-hid-writer` | `EneHidController.cs:39` | `_pending` under G10, then a HID write | `Monitor.Wait`; the HID write | No — but it raises `OwnerChanged`? (no: the bridge does) |
| T9 | `acer-ec-hid-writer` | `AcerEcHidController.cs:65` | `_pending` under G11, `LastError` **unlocked** (`:127,128`) | `Monitor.Wait` | No — `LastError` written at `:113,114` and **read nowhere** (corrected 2026-09-14: no reader exists in the tree). **Corrected again 2026-09-17: there is no `LastError` on this class at all now** (`d7be021` deleted the dead field), so this thread touches only `_pending`. |
| T10 | `lamparray-bridge` | `LampArrayBridge.cs:106` | `_apply`, `_written`, `_frame`, `HostOwnsLighting`, `LastError`, `Enabled` | `WaitFrame` (blocking), 100 ms min interval | Yes — `OwnerChanged` → `LightingCoordinator.cs:113` |
| T11 | `acer-hotkeys` (Linux) | `AcerHotkeys.Linux.cs:47` | `_closing` | `read()` | Yes — `AppController.cs:382,305` |
| T12 | `SystemEvents` window thread | `ResumeWatcher.Windows.cs:9`, `Clamshell.Windows` | `Clamshell._sync` | — | Yes — `LightingCoordinator.cs:99` |
| T13 | LidWatcher `WndProc` | **UI thread** (window created there) | `Clamshell` | — | Already on it |
| T14 | gdbus stdout pump (Linux) | `ResumeWatcher.Linux.cs:33` | `_stopped` | pipe read | Yes |
| T15 | `HwSerial` continuations | `TaskScheduler.Default`, `OptionsViewModel.cs:20` | port `LastError`, `_desired` latch | G1 behind the row's own reads | Yes — `OptionsViewModel.cs:36,53` |
| T16 | `LightViewModel` brightness loop | `LightingViewModel.cs:214` | `_reading`/`_readPending` under G13; `LightSettings` fields | G1 | Yes — `:220` |
| T17 | `[UnmanagedCallersOnly] CreateCallback` | driver, `LampArrayTransport.Windows.cs:354` | `_createResult` | — | No |
| T18 | 8 UI debounce/anim timers | see 1.3 | view-model fields | — | — |
| T19 | `ApplyStartupState` | `AppController.cs:53`, **on the UI thread** | `Settings.Clamshell`, `Bluelight`, GPU OC, CPU power — all synchronous, all through G2→G1 before the window is even built | G2, G1 | — *(the `_state` half of that row is superseded — see D17's correction: `ApplyStartupState` reads its three scalars under G2 and makes the hardware calls outside it. The GPU OC and CPU power axes are still driven inline on this thread; the Curve Optimizer is the one that is not.)* |

## 2.2 The boundary that does not move

`Dispatcher.UIThread.Post` is used in 15 places, for four distinct reasons:

1. **OS callbacks** arrive on foreign threads (T12, T14, T11) — `ResumeWatcher`, `LidWatcher`, hotkeys.
2. **Worker threads** produce events the VMs must hear — `OwnerChanged` (`LightingCoordinator.cs:113`),
   `HwSerial` readbacks (`OptionsViewModel.cs:36`), failure notifications (`OptionsAssembler.RunSet`, which posts
   through the marshaller it is handed — the direct `Dispatcher.UIThread.Post` call moved to whoever supplies it;
   see 1.3).
3. **Pool tasks** must not touch VMs — `UiPass` (`AppController.cs:507`), `SetCo` (`:372`),
   `GrantHardwareAccessAsync` (`:287`).
4. **Reentrancy deferral** — `SetLanguage` (`:184`) posts `RebuildForLanguage` so the dropdown's own change
   handler unwinds before its window is destroyed. This one is not about threads at all; it is about the
   event we are inside.

An actor addresses reason 3 only, and only by making the *value* available — the post itself stays. Stating
this as "the actor removes the marshalling" would be the single most likely way to break the app.

---

# Part 3 — Defects and hazards

## 3.1 Confirmed invariant violations

**Corrected 2026-09-15: D1-D5 are FIXED and this table describes the state before `303beca`.** The commit
"пять незаблокированных чтений графа `Settings`" (Wave 2) took all five through the locked accessors that
already existed. Verified against the tree today: `UI/AppController.cs` contains **zero** `.Settings`
occurrences, and `OptionsAssembler` reads `svc.Bluelight` (the locked scalar accessor) rather than
`svc.Settings.Bluelight`. The table is kept because it is what the fix was judged against — read it as history,
not as a to-do list. What remains of the *root cause* is recorded below it.

The invariant declared at `LaptopService.cs` `_state` (its declaration comment) is: *"Guards ALL access to the mutable Settings graph (its
collections + scalars) … because these are now touched from TWO threads."* Five sites violate it.

| # | Site | Read | Thread | Why it matters |
|---|---|---|---|---|
| **D1** | `UI/AppController.cs:468` | `_svc.Settings.TurboToggles` | **pool** (T2) | Unguarded read on the background pass. The plan records the 158/165/167 sites but not this one, and this is the worse of the two: it is on the thread the lock exists for. |
| **D2** | `UI/AppController.cs:158` | `_svc.Settings.TurboToggles` | UI | Recorded in the plan. |
| **D3** | `UI/AppController.cs:165` | `_svc.Settings.TurboToggles` | UI | Same. |
| **D4** | `UI/AppController.cs:167` | `_svc.Settings.Language` | UI | Same. |
| **D5** | `OptionsAssembler.cs` `Choices` | `svc.Settings.Bluelight` | UI | Not recorded in the plan. A scalar `int`, so it cannot tear — but the *contract* is broken, and it is one more place a future `Dictionary` insert would hide. **Fixed:** the read is the locked scalar `svc.Bluelight` today (`OptionsAssembler.cs:88`); the cell above is the pre-`303beca` read this table is a record of. |

Also violating the spirit of the same invariant, without the plan saying so:

- `LaptopService.cs` `Settings` — `public Settings Settings { get; }` hands the whole mutable graph to anyone. Every
  read outside `_state` is a consequence of this, not an independent mistake. This is the root cause of D1-D5.
  **Addressed 2026-09-15, and the address is weaker than it sounds:** the property is now `internal`, but this is
  **one assembly** — `AcerHelper.csproj` compiles `Domain/`, `Application/`, `Infrastructure/`, `UI/` and
  `Bootstrap/` together — so `internal` is visible to all of them and enforces nothing inside the repo. It
  removes the *invitation* and makes a future assembly split (the only real enforcement; see the plan's Wave 1
  note) mechanical. The door that stays open is wider than the property ever was: eight accessors return a **live**
  preset out of the graph — `CurrentFan`, `ApplyModeFan`, `CurrentGpuOc`, `ApplyModeGpuOc`, `CurrentCo`,
  `ApplyModeCo`, both `LightsForCurrentMode` forms, `EnsureLightZone` — and `LightingViewModel` keeps the
  dictionary `LightsForCurrentMode` hands it and writes into it in place. Closing those means returning copies,
  which is a redesign of the lighting path (writing into the graph *is* the feature there), not a visibility
  change.
  **Corrected 2026-09-17: six of those eight no longer hand back a live preset — and which six matters.**
  `CurrentFan`, `ApplyModeFan`, `CurrentGpuOc`, `ApplyModeGpuOc`, `CurrentCo` and `ApplyModeCo` return
  `Snapshot()` (`d7be021`, release 0.33.1): a copy that shares no array and no dictionary with the stored
  instance, so the caller cannot reach the graph through it. **The two lighting entries were deliberately left
  live**, and that is a recorded decision, not an oversight: `LightsForCurrentMode` (both forms) and
  `EnsureLightZone` still hand back the stored reference, because writing into the graph there IS the feature —
  `LightsForCurrentModeTests` and `LaptopServicePresetTests` hold `Assert.Same` on the live dictionary, and they
  passed unchanged through `d7be021`, which is the evidence that the copies did not touch this path
  (`docs/open-decisions.md` §4). So the closing sentence is true, and true only of the light: for the six preset
  accessors a copy WAS the answer, and spreading one to the lighting entries would silently stop per-mode
  lighting from persisting.
  **Corrected again 2026-09-18: the two lighting entries are gone, and the owner's reason for keeping them is
  the one that fell.** «Это неверно с точки зрения архитектуры» — the two accessors handed the UI a live
  dictionary which it wrote into in place, which is a write into the persisted graph from the UI thread with no
  lock, and the sentence above read that as a feature. `EnsureLightZone` is deleted outright and
  `LightsForCurrentMode` returns a per-mode door (`ILightZoneMode`, `Application/LightZone.cs`) whose state
  crosses as `LightZoneState` values; the `Assert.Same` guards it names here **could not stay green** and were
  rewritten to pin the rule instead of the aliasing. Full record: `docs/open-decisions.md` §4-бис.
- `LaptopService.cs` `LastError` — `public string? LastError { get; private set; }`, written under `_state` on some
  paths and outside it on others (e.g. `WmiInvoker`'s port results), read from the UI thread at
  `AppController` (`ApplyProfile`, `SetTurbo`, `SetGpuOc`, `SetCpuPower`, `SetCo`) and `OptionsAssembler.cs` `RunSet`. Reference writes are atomic so nothing
  tears; the *value* can still be another operation's error. This is the "wrong error message" class of bug, and
  it is invisible.
  **Confirmed 2026-09-14 by a dedicated trace — and it is the only `LastError` in this codebase that is
  genuinely read cross-thread.** No reader takes `_state`, so the lock orders nothing here. `volatile` is not
  the remedy: it cannot be applied to a property without rewriting it as a backing field, and on x64 it changes
  nothing observable (a store followed by a load of the same address from the same thread cannot be reordered, so
  a reader already sees at least its own write). All six races found are of the form "the reader picks up
  *another* call's error", with windows a few instructions wide — and `volatile` makes that *more*
  deterministic, not rarer, because it guarantees the reader sees the newest write, including the other
  thread's. The fix is to remove the shared side channel (return the error from the call), not to widen its
  visibility.
  **Fixed 2026-09-17 (`d7be021`, release 0.33.1) — and with the fix this bullet names.** `public string?
  LastError { get; private set; }` no longer exists on `LaptopService`: a write returns `(bool ok, string?
  error)` from `Attempt`, so the reason travels with the result and cannot be another call's. All six readers
  were migrated, and the compiler found them rather than a grep — deleting the property makes a forgotten reader
  a build error. `volatile` was not applied, for exactly the reason given above. The analysis here is kept as
  the record of why the field had to go; `docs/open-decisions.md` §2 records the closure and the rule that kept
  it from changing behaviour (a channel is given only where a reader exists today, so the silent points stay
  silent).

## 3.2 State guarded by nothing, touched from more than one thread

| # | Site | State | Writers | Readers | Severity |
|---|---|---|---|---|---|
| **D6** | `Domain/LampArrayBridge.cs:70` | `LastError` | worker at `:151` (unlocked); `Enable()` at `:83,86,88` under G4 — but `Enable()` **never runs on the UI thread** (corrected 2026-09-14: it is reached only via `OptionsAssembler.RunSet` on an `HwSerial` continuation, or `LaptopService.cs` `ApplyStartupState` on a `Task.Run`) | `LaptopService.Lighting.cs` `SetDynamicLighting`, and only on a *failed* `Enable` — i.e. when no worker is live | **Not reachable.** The worker exists only between a successful `Enable` and its own exit, and `Enable` returns early (`if (Enabled) return true`) while a worker is live, so the two writes cannot overlap a read. Downgraded to a latent hazard on the zombie-worker path: if `Disable`'s 1 s join times out (`:121`), the survivor can re-arm the transport's write. |
| **D7** | `Domain/LampArrayBridge.cs:68,72,81` | `Enabled`, `HostOwnsLighting`, `LampCount` | UI thread under G4 | UI thread unlocked, and `HostOwnsLighting` read in `LightingCoordinator.Paint` (`:215`) | Benign in practice (all writers are the UI thread) but undocumented — the properties *look* like they need `_gate` and two of them are read on a hot paint path. **Corrected 2026-09-17: "all writers are the UI thread" is not right.** `HostOwnsLighting` is also written by the bridge's own worker — `WorkerLoop` sets it true when a host takes the surface, and `ReleaseOwnership` (which `Disable` calls too) clears it on hand-back and teardown — so the field has a writer on the worker thread and a reader on the paint path. `Enabled` is written under `_gate` by whatever thread calls `Enable`/`Disable`: D6's correction above records that `Enable` is never the UI thread, and `Disable` is reached from there (a hand-off too, and from `LaptopService.Dispose`). The read side of this row is accurate: `Paint` reads `HostOwnsLighting` unlocked on the paint path. |
| **D8** | `Infrastructure/Vendors/Acer/AcerEcHidController.cs:45` | `LastError` | `acer-ec-hid-writer` at `:113,114` | **nowhere** (corrected 2026-09-14: verified by `git grep` — every reference to this class is a declaration, the ctor, or a partial-class header; not one `_ec.LastError`) | **Not a race — dead state.** Written and never read; the EC path surfaces failures through the port's `(ok, error)` tuple instead. A candidate for deletion, not for `volatile`. **Deleted 2026-09-17 (`d7be021`):** the dead field and the comment that falsely claimed it was "read by the UI" are gone from `AcerEcHidController`, and the write site now says plainly that an EC write failure goes nowhere. The *gap* stays open — `docs/open-decisions.md` §5. |
| **D9** | `Infrastructure/Vendors/Generic/DelegatePorts.cs:19,30,41,54,64` | `LastError` on all five ports | the port's own caller, through `Set` (`:21,33,43,44,58,67`) | the **same** thread — `LaptopService.cs` `Attempt` (the method this row called `Run` until it was renamed in the tree) | **Corrected 2026-09-14: no race in three of the five.** The write and the copy-out happen on the same `HwSerial` continuation, and `FlagPort`/`ChoicePort`/`LevelPort` are single-threaded by construction (each row owns its own port instance). The genuine `DelegatePorts` races are `ProfilesPort` (`:54`) and `FanPort` (`:41`) — UI vs POLL, a shape this table did not list. |
| **D10** | `UI/ViewModels/LightingViewModel.cs:345` (`SaveState()`) | `LightSettings` fields | UI thread | `BackgroundPass` → `Save()` → `JsonSettingsStore` | `LaptopService.Lighting.cs` `EnsureLightZone` exists to make the *structural* insert safe, and the comment says per-field edits "stay unguarded". That is true only because a per-field write cannot restructure the dictionary — the JSON serializer can still observe a half-updated `LightSettings` and persist it. Low impact (one debounce interval of stale brightness), but it is an unguarded cross-thread read of a mutable object. **CLOSED 2026-09-18.** The row's own premise — that the UI holds the stored `LightSettings` and writes its fields in place — is gone: the state crosses as `LightZoneState` values, and every write goes through `ILightZoneMode.Write`, which takes `_state` itself (`LaptopService.Lighting.cs` `LightZoneMode`). There is no longer an unguarded field write to observe, and the serializer can no longer see a half-updated entry. See `docs/open-decisions.md` §4-бис. |

## 3.3 Overlapping / redundant guards

| # | Overlap | Assessment |
|---|---|---|
| **D11** | `WmiSession.Gate` (G1) vs the fan-out of ports | G1 is correct and minimal. Every `GmGet`/`GmSet`/`Sensor`/`UiGet`/`UiSet` and every `AcerBattery`, `DellBiosWmi`, `OverlayCpuPower` call funnels through it. It is the *good* example in this codebase and the model the actor should copy, not delete. **Corrected 2026-09-17: `AcerBattery` is not a symbol in the tree any more** — the Acer battery's reads live in `BatteryWmi` (`AcerDevice`, in the file still named `AcerBattery.Windows.cs`), which reaches WMI through `WmiInvoker` and so through this gate. The claim holds; only the name is gone. |
| **D12** | G10/G11 vs G1 | **None.** The ENE and EC-HID writers do *not* take G1 — they are a genuinely separate transport (see `docs/power-an18-61.md`: on the AN18-61 the real power envelope is HID, not WMI). Any design that routes them through one actor must not accidentally put them behind G1. |
| **D13** | G7 (`RyzenCurveOptimizer._gate`) vs G1 | `_gate` guards lazy `Io()` and `Dispose`; the SMU transaction is guarded by X1. No overlap with G1 — different transport, different process boundary. **Corrected 2026-09-17: the transaction is guarded by G7 *and* X1, not by X1 alone** — every `Transact` call site runs inside `lock (_gate)` (`Set`, `SetDomains`, and the private `SetGpu` helper whose own doc says "Caller holds `_gate`"), which is what G7's own row says; the PCI mutex is taken inside that. The difference is load-bearing: `TryOpenPciMutex` can return null, and then G7 is the only guard the transaction has. |
| **D14** | G3 (`_lampGate`) vs G2 (`_state`) | **Deliberate and correct.** `LaptopService.Lighting.cs` `_lampGate` (its declaration comment) documents it: sharing `_state` would put an ACPI-EC stall in front of a UI-thread lighting repaint. This is a design the actor must preserve as a *latency* property, not a data property. |
| **D15** | G4/G5 (`LampArrayBridge`) | Correctly split: `_gate` for lifecycle, `_apply` for the frame-critical section. The only reason two exist is that `Reassert()` is called from the UI thread while the worker is mid-apply. |

## 3.4 Latency and blocking hazards

### D16 — A hardware read on the UI thread can block on G1 behind the background poll

**Corrected 2026-09-17: most of the list below is a record of the state before wave 6, not a to-do list.** The
four option-row bullets stopped being UI-thread reads in `b51c38d` ("defer option-row reads off the UI
thread", wave 6 step 1): `Toggles()`/`Choices()` now build with placeholders, and the rows are primed after
`BuildUi` returns, on each row's own serial worker (`OptionsViewModel.Prime()`). `CurrentCpuPower` left the
path the same way in `cc145c9` (step 3) — it is primed from `BackgroundPass`, not read here. The battery
bullet is stale a second time over: wave 4b (`9c17ae5`) deleted the four battery ports, so `limit.Get()` and
`cal.Get()` no longer exist — `BatteryLimit()`/`BatteryCalibration()` carry the `Read` ops of the
`Battery.ChargeLimit` and `Battery.Calibration` domain records, filled in off the UI thread by
`BatteryViewModel.Prime()`. What remains of the hazard is narrower but not gone, and the argument the section
carries — priority, not serialization — is unaffected; it is what wave 6 was aimed at.
`PowerSourceProfiles()` still reads `svc.SourceProfile(onAc)` under G2 at build time, and `CurrentFan`,
`CurrentGpuOc` and `CurrentCoDomains` are still read synchronously in `BuildUi`. Those three are hardware reads
too, not `Settings` reads: each takes `_state` and calls `CurrentModeKey()`, which asks the `PowerProfiles`
port for the live profile — so the UI thread still blocks on G1 from `BuildUi`, through fewer sites than the
list below names.

**Corrected a second time 2026-09-17: the three preset reads are gone from `BuildUi`, and the item is still not
retired.** `CurrentFan`, `CurrentGpuOc` and `CurrentCoDomains` gained an overload taking an already-read profile
— the pair `CurrentModeKey(PerformanceProfile?)` already had, and the remedy `docs/open-decisions.md` §3 names
for exactly this site — and `BuildUi` now passes the `cur` its caller read once. (`CurrentCo` gained the same
overload because `CurrentCoDomains` renders its rows from it: four methods, three call sites.) `BuildUi`
therefore reads no live profile through the `PowerProfiles` port any more — it still touches the port's `All`
list, which is a cached table built at construction, not a transaction — and the closing sentence above is
superseded: the UI thread no longer blocks on G1 from `BuildUi` through those three.

Read that as a narrowing, not a retirement. What survives, measured against the tree:

- the build path still performs **one** profile read on the UI thread — the deliberate hoist in the
  `AppController` constructor and in `RebuildForLanguage`, which exists so that `BuildUi` can be handed a
  profile at all. Its comment enumerates what keys off that one read; the enumeration now includes these three.
  So "the build path performs no hardware read" remains false, at one read per build instead of four.
- `BuildUi` still acquires G2 (`PowerSourceProfiles()` → `LaptopService.SourceProfile`), and the background
  pass holds G2 across EC writes (`ApplyCustom`, `ApplyModeCpuPower` — D17 below), so a build can still wait
  behind the poll. Removing a read removes a G1 transaction; it does not remove the exposure.
- two callers hold no profile and still read, each for a different reason: `LaptopService.ApplyModeGpuOc`
  (through the parameterless `CurrentGpuOc`, under G2 — a §3 site) and `HardwareReconciler.Reapply`'s
  Curve-Optimizer reflect (through the parameterless `CurrentCoDomains`, on the pool thread). Whether they
  should take a profile too is an open decision — `docs/domain-refactoring-plan.md` §7.
- the same shape exists one layer earlier, at startup, and the bullets below do not say so: the constructor
  runs `LaptopService.ApplyStartupState` → `HardwareReconciler.Reapply(Startup)` on the UI thread, and the
  Fans, GpuOc and CpuPower axes are all driven inline there (only `Co` is deferred), each deriving its key
  through the parameterless `CurrentModeKey()`. This is why the hazard is not a `BuildUi`-only property, and
  the Fans one is a §3 site for the same reason.

**What is withdrawn** is the conclusion that this item can be read as done — including the opening sentence
above, that "most of the list below is a record of the state before wave 6, not a to-do list". That sentence is
true of the LIST; it is not a statement about the UI thread, and this is the second time the difference has had
to be written down. The record of what was believed at each wave is kept, here and below.

The new shape is pinned by `tests/AcerHelper.Tests/PresetReadsWithProfileTests.cs`, whose fake port counts its
`Current()` reads: zero on the fixed path, value-identical to the reading path for the same profile, the handed
profile is the one used, and the two callers without a profile still read.

`BuildUi()` (`AppController`'s `BuildUi`) runs on the UI thread and calls, synchronously:

- `OptionsAssembler.Toggles()` → `lcd.Get()`, `kbd.Get()`, `fn.Get()`
- `OptionsAssembler.Choices()` → `usb.Get()`, `to.Get()`, `mode.Get()`
- `OptionsAssembler.BatteryLimit()` / `BatteryCalibration()` → `limit.Get()`, `cal.Get()`
- `OptionsAssembler.PowerSourceProfiles()` → `svc.SourceProfile(onAc)` under G2
- `LaptopService.CurrentFan` / `CurrentGpuOc` / `CurrentCpuPower` / `CurrentCoDomains` → G2
  *(superseded — see the second correction above; precisely, three of the four are still **called** in
  `BuildUi`, through the profile-taking overload, so they still take G2 — what is gone is the port read, not the
  call. `CurrentCpuPower` alone is not called there at all: its row is built with a placeholder and primed from
  the background pass.)*

Every one of those `Get()` calls is a WMI transaction and therefore takes **G1** (`WmiSession.Windows.cs:83`).
`BuildUi` is called at startup (from the `AppController` constructor) and on **every live language switch** (`RebuildForLanguage`).

Measured cost: ~6-10 serialized EC transactions, each ~1-5 ms when the EC is healthy. So the *typical* stall
is 10-50 ms — a visible hitch, not a freeze. But G1 is process-wide and the background pass (T2) is holding
it for its own ~10-20 transactions, and `BackgroundPass` can block on `QueryDisplayConfig` (`:454`) and on a
stalled EC during a display topology change — which the code itself cites as the reason `BackgroundPass`
exists at all ("that's what used to freeze the app when the ACPI-EC stalled during a display connect",
`:418-419`).

**Worst case is therefore unbounded and already historically observed as a full UI freeze.** The current design
caps it only by luck: `BuildUi` happens to run at moments (startup, language switch) when a pass may or may not
be in flight. There is no priority, no timeout and no cancellation between a UI-thread read and the poll.

This is the strongest concrete argument that something needs to change — and note that the *actor alone does
not fix it*. Moving G1's work onto one thread without giving user-initiated reads priority over the poll
converts a rare freeze into a latency spike on every language switch. Priority is the requirement, not
serialization.

### D17 — `_state` held across hardware I/O

`ApplyCustom` (`LaptopService.Fans.cs` `ApplyCustom`) holds G2 while calling `ApplyFan` → G1. `LaptopService.cs` `ApplyStartupState`
holds G2 across `ApplyModeGpuOc` and `ApplyModeCpuPower` — both hardware writes — and is called
from the **UI thread** (`AppController.cs:53`). `LaptopService.Tuning.cs` `SetGpuOc` holds G2 across `Save()`.

**Partly fixed 2026-09-15 (Wave 5, step 5).** `ApplyStartupState` now reads its three scalars under G2 and makes
all four hardware calls outside it — see the plan for why hoisting the *calls* is safe here while hoisting an
*argument* is not, and for the three sites left alone on that rule. `ApplyCustom` and `ApplyModeCpuPower`'s own
`cp.Set` still hold G2 across a write; `ApplyModeCpuPower` is now the only remaining site of this shape that a
concurrent caller can contend (UI thread vs `LightingCoordinator`'s `Task.Run`).

Consequence: any UI-thread acquirer of G2 (D16's `CurrentGpuOc` etc., and every `SetFan`/`SetTurbo` from a
slider) can block for as long as a full EC transaction, because the background pass's G2 is held across one.
The declared lock order makes this *safe*; it does not make it *fast*.

### D18 — `SetCo` can block for 5 seconds, and the UI does not say so

`AppController.cs:366-374` correctly hands it off, but the wait is `MutexWaitMs = 5000`
(`RyzenCurveOptimizer.Windows.cs:138`) and the only feedback is a failure notification *after* the fact. During
those 5 s the user's slider drag has been accepted, persisted, and not applied. If the mutex is never acquired,
`LastError = "another tool is holding the PCI access lock"` (`:419`) and the offset is **silently not applied**
— which the user experiences as "the undervolt doesn't stick", with no indication that another program is the
cause.

### D19 — `ApplyCustom` runs on every 3 s tick and writes the fans

`BackgroundPass` → `_svc.ApplyCustom(sensors)` (`:502`). Under Custom mode this can issue a fan write every
tick. Combined with D17, the UI thread's G2 acquirers compete with a periodic hardware write. The deadband
(`_fanCurve.Step`) limits it, but the write path is live on the poll.

## 3.5 Deadlock and lock-ordering

The declared order is `_state` (G2) → WMI `Gate` (G1), with the justification "the WMI layer never calls back
here". Verified: `WmiInvoker` and `WmiSession` have no reference to `LaptopService`. **No cycle exists today.**

Three ways the actor introduces one:

1. **A nested command from inside a command.** `InvokeMethod` re-takes G1 to call `QueryFirst`
   (`WmiSession.Windows.cs:116` → `:83`). Under a single-threaded actor this is free *only if* the pump runs a
   nested request inline on the same thread. If the pump instead enqueues it and blocks waiting, it
   **self-deadlocks on the first WMI method call**, because the only thread that could serve the nested request
   is the one waiting for it.
2. **A command that takes a surviving gate which is held by a thread waiting on the actor.** G10/G11 (the HID
   writers) do *not* block on the actor today. If a future change makes the actor's shutdown join a writer
   thread while that writer is waiting on an actor command, that is a deadlock. Keep the join order one-way:
   the actor may call into the writers; the writers never call into the actor.
3. **X1 acquired from inside the actor while a caller holds G2.** Unchanged from today, but the actor's single
   thread means a 5 s X1 wait now delays *everything queued behind it*, including sensor polls. See D18/4.4.

## 3.6 Fire-and-forget writes with no readback and no error path

| # | Site | Write | Consequence of failure |
|---|---|---|---|
| **D20** | `LaptopService.cs` `ApplyStartupState` (the `Task.Run` calling `ApplyModeCo()`) | `ApplyModeCo()` on startup | SMU offsets never applied at boot; user believes the undervolt is loaded. `catch { /* stays stock */ }`. |
| **D21** | `LaptopService.cs` `ApplyStartupState` (the `Task.Run` calling `la.Enable()`) | `la.Enable()` | Virtual LampArray doesn't appear; `Settings.DynamicLighting` stays true, so the Options row claims a device that isn't there — until `Read: () => lamps.Enabled` (`OptionsAssembler.cs` `Toggles`, the Dynamic Lighting row's `Read`) snaps it back on first open. Recoverable, but only by looking. |
| **D22** | `AppController.cs:492` | `ApplyModeCo()` on every mode switch | Same as D20, per switch. |
| **D23** | `LightingCoordinator.cs:255` | GPU OC + CPU power + CO on resume | All three volatile states silently stay at their post-sleep values. The comment says "the next resume or mode switch re-asserts" — true, but a user who never switches mode never gets their offsets back. |
| **D24** | `Infrastructure/Vendors/Acer/AcerDevice.Windows.cs:91` (in `SetProfile`, `:89`) and `:50` (`InitVendor`) | `_ec?.Apply(p.Kind)` then the WMI write | Two transports, one logical operation, no readback of either. If the HID write lands and the WMI one fails (or vice versa), the profile is half-applied and the app has no way to know. `docs/power-an18-61.md` says these two carry *different* things. |
| **D25** | `LaptopService.ApplyModeGpuOc/ApplyModeCpuPower/ApplyModeFan` | hardware writes | Return the preset for UI reflection; the hardware result is folded into the shared `LastError`, which a later unrelated operation can overwrite (see 3.1). **Corrected 2026-09-17:** there is no shared `LastError` to fold into (`d7be021`), and these three discard the port's result — still deliberately, and for the reason `ApplyFan`'s own comment gives: no caller of any of them has ever had a reader for the failure, so a channel would start showing a message the app has never shown (the rule in `docs/open-decisions.md` §2). |

The pattern: **the app cannot distinguish "applied" from "accepted for sending"**. `AcerEcHidController.Apply`
even says so in its own return contract. An actor that returns a `Task` per command does not by itself fix
this — it makes the *readback* possible, which is the actual deliverable.

---

# Part 4 — Proposed actor design

## 4.1 Ownership

The actor owns **transports and the mutable device state machine**. Concretely:

| Owned | Currently guarded by | Why |
|---|---|---|
| Every ACPI-WMI transaction | G1 (`WmiSession.Gate`) | already process-wide and already correct; the actor gives it a queue and a priority |
| The Acer EC-HID profile byte | G11's consumer side | the write is a command; the *queue* stays |
| The ENE RGB frame path | G5 | the apply is a command; the *pacing* stays |
| Plain keyboard-backlight level | `LevelPort` | a transport |
| CPU power overlay (powrprof) | `OverlayCpuPower` | a transport |
| Display tint | `DisplayTint` | a transport |
| Clamshell lid-action power setting | G8 | needs a single writer (today: three threads) |
| GPU OC (NvAPI) | **nothing** | currently unsynchronised; the actor gives it its first serialization |
| Curve Optimizer SMU mailbox | G7 + X1 | in-process part only; X1 stays with it |
| LampArray transport handle | G9 | the handle, not the device node |
| Battery reads / calibration trigger | G1 | a transport |
| The 3 s poll | `_busy`/`_rerun` + `DispatcherTimer` | becomes a command stream |

The actor owns **the `Settings` graph only as a serialization point, not as its data.** See 4.5.

## 4.2 What the actor must NOT own

- **X1-X6.** `Global\Access_PCI` stays in `RyzenCurveOptimizer` and is taken *inside* the command. Same for
  the single-instance mutex, PawnIO, the LampArray device node, the SMU mailbox and `SystemEvents`.
- **The UI thread.** No view-model, no `Dispatcher`, no `Loc` call, no `MainViewModel` reference. The actor's
  API returns values; callers post them.
- **The HID writer threads (G10, G11).** This is the most important exclusion. They exist to
  **rate-limit and coalesce** — 10 ms pacing, move-to-tail coalescing by region, a `MaxPending` of 32, a
  single-slot collapse for the EC profile byte. Moving them into the actor would put a paced, latency-bound
  write on the actor's critical path, so a 10 ms-per-report queue would serialize *behind* the sensor poll and
  a 5 s `SetCo` would stall the keyboard. The actor calls `controller.Apply(...)` and returns; the controller's
  queue does its job exactly as it does now.
- **The 8 UI debounce timers.** Debouncing is a policy about the user's hand.
- **`CancellationToken` plumbing** in the updaters.
- **`Save()`'s file I/O.** See 4.5.

## 4.3 Command set

Derived from operations that exist. No new capability is invented.

**Poll (low priority, idempotent, cancel-on-preempt):**
`PollProfile` · `PollSensors` → `SensorSnapshot` · `PollBattery` → `BatteryInfoSnapshot` ·
`PollDisplayTopology` (for clamshell `Evaluate`)

**Mode / profile:**
`ApplyProfile(PerformanceProfile)` · `SetTurbo(bool)` · `TogglePerformance()` ·
`SetFanMode(FanMode)` · `SetFanSpeeds(byte, byte)` · `ApplyFanCurve(bool gpu, bool use, int[] points)` ·
`ApplyCustom(SensorSnapshot)`

**Per-mode re-apply (the volatile-state family):**
`ApplyModeFan()` · `ApplyModeGpuOc()` · `ApplyModeCpuPower()` · `ApplyModeCo()`

**Tuning:**
`SetGpuOc(int core, int mem)` · `SetCpuPower(string id)` · `SetCoValues(int[] counts)`

**Options (generic ports):**
`SetFlag(IFlagPort, bool)` · `SetChoice(IChoicePort, string id)` · `SetBlueLight(int)` ·
`SetClamshell(bool)` · `SetKeyboardBrightness(int)` · `SetDynamicLighting(bool)`

**Power source:**
`SyncPowerSource(BatteryInfoSnapshot)` → applies the source's profile · `SetSourceProfile(bool onAc, PerformanceProfile)`

**Lighting:**
`SetProfileFlash(AccentColor)` · `BlankBacklight()` · `ReassertLampFrame()` · `ApplyZone(string, LightSettings)`

**Verification reads (the readback half of `VerifiedHwValue`):**
`ReadFlag(IFlagPort) → bool` · `ReadChoice(IChoicePort) → string?` · `ReadBrightness() → int` ·
`ReadLampEnabled() → bool`

**Lifecycle:** `Start()` · `StopAsync()` · `LastError` per command result.

## 4.4 Execution model

```
        UI thread                    actor thread                  writer threads
  ─────────────────────         ─────────────────────         ─────────────────────
  VM / debounce timer
        │  Request(cmd) -> Task<T>
        ├──────────────────────────▶ high-priority queue
        │                                  │  (re-entrant: a nested
        │                                  │   Request from inside a
        │                                  │   command runs INLINE)
        │                            execute ──▶ WmiSession (no Gate)
        │                                       ──▶ EneHidController.Apply ──▶ paced queue
        │                                       ──▶ RyzenCO: G7 + X1
        │                            low-priority poll queue
        │                                  │  (drained only when the
        │                                  │   high queue is empty)
        ◀── Task completes ────────────────┘
        │  Dispatcher.UIThread.Post(snap-back)
```

Four requirements, each of which is a hazard if missed:

1. **One dedicated thread, not the pool.** The pool cannot express total order, and a pool thread being
   blocked by a 5 s X1 wait is a thread-pool starvation bug. `IsBackground = true`, named, with the same
   1 s bounded-join teardown the HID writers use.
2. **Re-entrancy is mandatory, not a nicety.** `InvokeMethod` calls `QueryFirst` today
   (`WmiSession.Windows.cs:116,121`) and `SetFan → ApplyCustom`, `SetTurbo → ApplyProfile`,
   `SyncPowerSource → SeedSlotFromHardware → Save` are all nested (`LaptopService.cs` `_state` — its declaration comment names them). The pump must
   run a nested `Request` from inside a command **inline on the same thread**. Enqueue-and-wait self-deadlocks
   on the first WMI method call.
3. **Two priorities, or the poll starves the user.** A single FIFO queue turns every user action into a wait
   for the current poll pass (~10-20 transactions) and turns a 5 s `SetCo` into a 5 s freeze of the dash.
   Poll commands are separate, individually cancellable, and abandoned when a user command is pending.
   `SetCo` should also get its own lane or a shortened `MutexWaitMs` with an explicit "busy" result, so a
   contended SMU mailbox stops being a 5-second hole in the dashboard.
4. **The writer threads are downstream.** `EneHidController.Apply` / `AcerEcHidController.Apply` remain
   "accepted for sending", non-blocking, and are *not* awaited. The actor must never join a writer from inside
   a command.

## 4.5 What happens to `_state`

This is where the design has to be honest: **`_state` (G2) does not disappear.**

`Save()` (`LaptopService.cs` `Save`) serializes the whole `Settings` graph to JSON. The UI thread mutates
`LightSettings` fields between debounce ticks. `LaptopService.Lighting.cs` `EnsureLightZone` exists because a structural
insert can race the serializer. An actor owning the hardware does nothing about any of that.
*(Corrected 2026-09-18: that second sentence is no longer true of the lighting — the UI holds values and
writes through `ILightZoneMode`, which takes `_state` itself, and `EnsureLightZone` is gone. `Save()` still
serializes the graph under the same lock. See `docs/open-decisions.md` §4-бис.)*

So the end state is a **split**:

- **Hardware serialization** → the actor. G1's call sites become commands.
- **The `Settings` graph** → stays behind a lock, but the lock is now short-lived: it guards in-memory
  mutation and the `Save()` snapshot, **and never hardware I/O**. That is the change that matters. Today
  `ApplyCustom` holds G2 across a fan write and `ApplyStartupState` holds it across two hardware writes on the
  UI thread (D17). After the split, a command is built under the lock and executed outside it.
  *(Half of that `Today` is already past — the `ApplyStartupState` half; see D17's correction. `ApplyCustom`
  still holds G2 across a fan write, and `ApplyModeCpuPower`'s own `cp.Set` with it.)*

This also removes the `_state → Gate` ordering question entirely: there is no Gate to order against.

Concretely, `LaptopService.Settings` should stop being `public Settings Settings { get; }`
(`LaptopService.cs` `Settings`) and become a snapshot accessor:
`T Read<T>(Func<Settings, T> read)` that takes the lock, plus the actor's commands taking the lock only to
build their arguments. That is a smaller, independently reviewable change, and it kills D1-D5 at the root.

## 4.6 Keeping the UI-thread boundary

Unchanged: 15 `Dispatcher.UIThread.Post` sites stay. The actor's contract is
`Task<T> Request<T>(Command<T>)`, and the caller — already on the UI thread — awaits it and applies the result.
`UiPass` (`AppController.cs:520-538`) keeps its exact shape; only `BackgroundPass` changes from "do the I/O,
post a Tick" to "await a `PollSnapshot` command, post a Tick".

`VerifiedHwValue<T>` (`OptionsViewModel.cs:32-69`) keeps the `_desired` latch and the snap-back, and loses
`HwSerial`: `Apply` becomes *await `SetFlag` → await `ReadFlag` → post(snap-back unless superseded)*. The latch
is a UI-generation counter, not a lock, and it stays on the VM side.

## 4.7 Migration ordering

Every step below is separately buildable, keeps the app working, and is revertable on its own. The zero-warning
baseline holds throughout.

**Status 2026-09-17: two of the steps have partly happened, and the rest have not.** `Step 0` is in place as
`Infrastructure/Diagnostics/GateStats` (`4ba84cf`), with two differences from what the step asks: the counters
are deliberately **always on** rather than debug-only — they have to come from the build the owner actually runs
— and they are split by pool/non-pool thread rather than counted per pass. The `_state` half was **not** done,
so the UI-thread `_state` wait this step wanted is still unmeasured. `Step 5` is partly done:
`ApplyStartupState` no longer holds G2 across its hardware calls (D17's correction) and `SetGpuOc`'s port write
is outside the lock, but `ApplyCustom` and `ApplyModeCpuPower`'s own `cp.Set` keep it by recorded decision
(`docs/open-decisions.md` §3), the `Settings` property became `internal` rather than a locked read accessor, and
D1-D5 were fixed by `303beca` rather than here. Steps 1-4 and 6-8 are untouched. A reader of the numbers below
should also read `docs/open-decisions.md` §1: Step 0's measurement was never taken, so no step here has been
decided by it.

**Step 0 — instrument, change nothing.** Add a debug-only counter around `WmiSession.Gate`: total wait time,
max wait, acquisitions per pass. Same for the `_state` wait on the UI thread. This is the only way to know
whether the actor helped, and it is zero-risk to revert.

**Step 1 — land the actor with no callers.** New `HardwareActor` type: dedicated thread, two queues,
re-entrant inline nested requests, `Task<T>` request/response, `LastError` per result, bounded join on
`StopAsync`. Unit-testable on a mock transport with **no hardware**. Build both TFMs. Dead but reviewable.

**Step 2 — route WMI through it.** `WmiInvoker` calls `actor.Request(...)` instead of `WmiSession` directly.
The actor's thread becomes the only caller of `QueryFirst`/`InvokeMethod`; **`WmiSession.Gate` stays in place**
as an assertion (it is now uncontended, so it costs nothing and it proves the migration). Nested
`InvokeMethod → QueryFirst` must work inline — that is this step's acceptance test.

**Step 3 — fold the poll in.** `BackgroundPass` becomes `await actor.Request(PollSnapshot)`. Delete `_busy`,
`_rerun` and the `Interlocked` protocol (`AppController.cs:36-37,435-445,512-514`); delete the 3 s
`DispatcherTimer` (`:87`) in favour of the actor's poll schedule. **Requires priority (4.4.3) — do this step
only when the two-lane queue exists**, or the dashboard gets slower.

**Step 4 — take the blocking writes off `Task.Run`.** The four "this can block for seconds" sites
(`AppController.cs:368,492`; `LaptopService.cs` `ApplyStartupState` (its `Task.Run` calling `ApplyModeCo`); `LightingCoordinator.cs:255`) become commands. This is
the step that makes D20/D22/D23 *fixable*: each command can now report its own failure and trigger a readback,
instead of swallowing into `catch { }`.

**Step 5 — split `_state`.** Remove the hardware calls from under G2 (`ApplyCustom`, `ApplyStartupState`,
`SetGpuOc`); replace `public Settings Settings { get; }` with a locked read accessor. **D1-D5 are fixed here**,
and this step is worth doing even if the rest is abandoned.

**Step 6 — fold `HwSerial`/`VerifiedHwValue` writes into commands.** Delete G12 and `HwSerial`
(`OptionsViewModel.cs:12-23`). The readback becomes a second command; the latch stays.

**Step 7 — fold the remaining transports** (GPU OC, clamshell, tint, CPU power, LampArray handle) one at a
time. Each is independent and revertable. `Clamshell` is the one with the three-thread fan-in (G8) — expect it
to be the one that surfaces a real bug.

**Step 8 — delete the vestigial gates.** `PawnIo._gate` (G6), `LampArrayTransport._gate` (G9),
`LampArrayBridge._gate` (G4), `LaptopService._lampGate` (G3 — fold into the `_state` split from Step 5, but
**only after** verifying the lighting paint path is not blocked behind a hardware write), and finally
`WmiSession.Gate` (G1). Do not delete G1 before Step 7 is complete.

**Never:** G10, G11, the 15 posts, the 8 debounce timers, X1-X6.

---

# Part 5 — Risk register

Detectability is judged against the state of the project: **no behavioural tests today**, a test project
planned in Wave 2, and a mock transport feasible for everything that goes through `IDevice`'s ports (the ports
are already narrow interfaces with `null`-means-absent, and `VerifiedHwValue` already accepts an injectable
poster "so this can run under a plain unit test" — `OptionsViewModel.cs:31`).

**Corrected 2026-09-17: the test project is not planned, it exists.** `tests/AcerHelper.Tests` landed
(`7dcf6b4`) and has grown with every wave since — this document's own later corrections point at tests inside
it (`PresetReadsWithProfileTests`, `LightsForCurrentModeTests`, `LaptopServicePresetTests`), and the mock
transport this paragraph calls feasible is what `tests/AcerHelper.Tests/Fakes` already holds for the ports it
covers. So the *mock-sufficient* column below describes tests that can be written against a suite that exists
rather than a project that does not. The *hardware-only* column does not change: no mock replaces a
suspend/resume cycle, a second tuning tool, or a paced HID bus. One port-shaped detail has moved with it: the
battery's four ports are gone (wave 4b, `9c17ae5`), so a mock of that section is built on the `Battery`
object's ops, not on `IBatteryInfo`/`IBatteryChargeLimit`/`IBatteryCalibration`/`IBatteryChargeMode`.

| # | Risk | What breaks | User-visible symptom | Detectable without hardware? |
|---|---|---|---|---|
| **R1** | Nested command self-deadlock (4.4.2) | The first WMI method call hangs the actor thread forever | App freezes on launch / on any profile switch; tray stops responding; **hang, not a wrong value** | **Yes.** A mock transport whose command handler issues a nested request. Catches it on the first test run. |
| **R2** | Priority inversion — poll starves the user (4.4.3) | User writes queue behind a ~20-transaction poll, or behind a 5 s X1 wait | Fan/profile changes feel laggy or "sometimes doesn't apply"; the exact complaint the EC `Gate` comment (`WmiSession.Windows.cs:20-22`) says already happened once | **Partly.** Mock with injected per-command latency detects the *shape* (a user command waits behind N polls). The real magnitudes — 10 ms HID pacing, 5 s mutex budget — are hardware-only. |
| **R3** | A dropped or reordered volatile-state re-apply | Curve-Optimizer offsets, GPU OC offsets or CPU power silently stay at post-sleep / post-power-cycle values | **Silent.** No error, no UI change; the user finds out when the undervolt isn't there (or when a previously-unstable offset is gone and the machine is stable — the failure is invisibility itself) | **No.** Requires reading the SMU/GPU state back on a real machine after a suspend/reboot cycle. This is the risk that most needs a mock *and* a hardware pass. |
| **R4** | `_lampGate` folded into `_state` (Step 8) | A UI-thread lighting repaint (`LightingCoordinator.Paint`, `:215`) waits behind G2 held across an EC write | Visible lighting lag / a stutter on every profile switch — precisely the regression `LaptopService.Lighting.cs` `_lampGate` (its declaration comment) was written to prevent | **Yes.** Mock with an injected stall in a hardware command; assert the paint path is not blocked. |
| **R5** | Two writers on the LampArray device node | X4: another process's frames interleave with `Reassert` (G5) | Keyboard flickers between the host's colours and the app's; Dynamic Lighting appears to "fight" the app | **No.** Needs Windows Dynamic Lighting actually driving the virtual device. |
| **R6** | `Global\Access_PCI` shortened or bypassed in a refactor | Another tool's SMU message executes against our arguments (the exact race `:442` documents) | **Worst case in this list.** A wrong SMU message can set an out-of-range voltage/offset; the machine crashes under load, or in principle damages the rail. Silent until it isn't. | **No.** Requires running alongside Ryzen Master / HWiNFO on real hardware. |
| **R7** | Step 5's `Settings` snapshot accessor changes read semantics | A caller that used to see live mutations now sees a copy, or a nested read deadlocks | Options rows show stale values; worst case a "collection modified" during `Save()` drops a settings write silently | **Yes**, entirely — `JsonSettingsStore` + a fake store, no hardware needed. |
| **R8** | `AcerDevice.SetProfile`'s two-transport write (D24) is "fixed" by the actor | HID and WMI halves land inconsistently, or a new readback adds a second audible EC click | Wrong profile, or a double-click noise on every switch (a real complaint the tree already records at `OptionsAssembler.cs` `Toggles` (the LCD-overdrive comment) for LCD overdrive) | **Partly.** Mock detects the ordering; the audible click and the actual power envelope are hardware-only. |
| **R9** | `EneHidController`/`AcerEcHidController` pacing disturbed | 10 ms pacing or move-to-tail coalescing lost; frames queue instead of collapsing | Keyboard lighting lags behind the profile switch or tears mid-gradient; the "last-one-wins" guarantee (`ILampArrayTransport.WaitFrame`) breaks | **Yes** on a mock transport for the coalescing contract; **no** for whether the real ENE controller copes. |
| **R10** | The actor's teardown races `ExitApp` | A command in flight during shutdown touches a disposed transport | Crash on exit (the one place a user always notices) | **Yes.** Mock + a dispose-race test. Today `LightingCoordinator.cs:253-254` already guards this class of bug by hand. |

## Testable on a mock vs only on a real laptop

**Mock-sufficient:** R1, R4, R7, R10, the coalescing half of R9, the ordering half of R2 and R8, and all of
Step 5.

**Hardware-only:** R3 (the whole volatile-state family — this is the app's core value proposition),
R5, R6, the pacing half of R9, and every D18-class timing question. Note that `docs/power-an18-61.md`'s
finding — that the real power envelope is HID, not the WMI profile byte — means a mock built only from the WMI
ports would test the *least* important half of the profile path.

---

# Objections to doing this

Stated plainly, because a reasoned objection is more useful than agreement.

**1. The confirmed defects do not require an actor.** D1-D5 are five unguarded reads. D6-D9 are four unguarded
`LastError` properties. The entire confirmed defect list is fixable in roughly a dozen lines: make `Settings`
non-public behind a snapshot accessor, mark the four `LastError` properties `volatile`, and split the two
`_state`-across-hardware sites. That is a day's work, reviewable by diff, and it fixes the actual bugs. The
actor is a ~137-primitive rewrite whose payoff is **legibility, not correctness** — and it is being scheduled
against a risk list (R1-R10) that is longer and more severe than the defect list it replaces. If the goal is
correctness, Step 5 alone gets 90 % of it.

**2. `WmiSession.Gate` is already the actor, and it is good.** One static `Lock`, one EC transaction at a
time, re-entrant, documented, with the interleaving reasoning written out. It is the best-engineered
concurrency code in the tree. "Replace 91 primitives with one actor" is, for the EC path, *replace one correct
lock with one correct executor* — a lateral move that costs the whole migration to obtain.

**3. Serialization is not the problem; priority is.** D16 is a real and serious hazard — a UI-thread read can
block behind the background poll. But the fix is a *priority* and a *timeout*, which can be introduced without
displacing any primitive: give the poll lower priority at the `WmiSession.Gate` call sites, or move the
poll's reads behind a single `TryQueryFirst(timeoutMs)`. The actor's two-lane queue (4.4.3) is one way to
express that; it is not the only way, and it is the most expensive.

*(Re-checked 2026-09-17 against D16's second correction, and left standing as written. The correction removed
three reads from `BuildUi`; it did not empty the UI thread's hardware path. One profile read per build remains
there by design, `ApplyStartupState`'s inline axes still read there at startup, and `BuildUi` still waits on G2
while the poll holds it across an EC write. Priority is still the requirement — which is the objection's point,
not an argument that the remaining reads are acceptable.)*

**4. Centralising all transports creates a new coupling.** Today the SMU mailbox (5 s cross-process budget),
the ENE HID pacing (10 ms), the VHF frame pump (blocking) and the ACPI poll (~ms) are four independent
schedules that only meet at G2. One actor puts them on one thread, where a 5 s X1 wait *is* a dashboard
outage unless the lane design is exactly right. The HID writers are excluded for this reason, but the
exclusion is a design constraint that has to survive every future change — the tree would be trading four
local invariants for one global one that must never be violated.

**5. Wave ordering is right but the premise is optimistic.** The plan puts tests (Wave 2) before the actor
(Wave 5), which is correct. But a mock transport cannot test R3, R5, R6 or R9's pacing half — and R3 is the
one that loses silently. So "tests first" does not actually de-risk the actor's worst outcomes; it de-risks
the plumbing. The decision to proceed is therefore still a decision to be verifiable only on real hardware,
which is the same position as today.

**Where I would agree with doing it anyway:** if the goal is Wave 3/4 legibility and the plan is accepted as
an investment rather than a bug fix. The four ad-hoc `Task.Run` answers to one problem (1.3) are genuine
evidence that the current arrangement does not scale, and `HwSerial` is a hand-rolled actor that already
argues for the general one. My recommendation is to do **Step 0, Step 5 and the D6-D9 `volatile` fixes now**,
and to treat Steps 1-4 and 6-8 as a separate, later decision made with Step 0's measurements in hand.
