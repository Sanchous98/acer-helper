# AMD Curve Optimizer on Strix Point — SMU mailboxes, encoding, granularity

Everything here was measured / verified on **one** sample: the CPU of the Nitro AN18-61 — an AMD Strix Point
part (family `0x1A`) with **4× Zen 5 + 6× Zen 5c** clusters and a Radeon 880M iGPU. Where a fact is a property
of this individual die rather than of Strix Point as a family, it says so. The die sample matters: the safe
undervolt floor below is explicitly *not* generalised, and one of the dead ends here is a per-SKU opcode
difference.

## What the feature is

AMD's Curve Optimizer is an **AVFS voltage-curve offset** applied through the SMU. A negative offset shifts the
whole voltage/frequency curve down: less voltage at every frequency point, so a fixed workload draws less power
and runs cooler, and inside a power-limited envelope the part holds higher clocks. It is the CPU-side analogue
of the GPU clock offset in `NvidiaGpu`, and just as **volatile** — see below.

## Three rails, two mailboxes

| rail | mailbox | set | read back |
|---|---|---|---|
| Zen 5 (CCD 0) | MP1 | `0x4B` per slot | — none |
| Zen 5c (CCD 1) | MP1 | `0x4B` per slot | — none |
| all cores | MP1 | `0x4C` | — none |
| iGPU (Radeon 880M) | RSMU | `0x1F` | `0x20` |

One port covers all three, because they are one kind of thing — a curve offset on this package, SMU-resident,
re-applied per performance mode — not because they share a path. Only the mailbox and the argument encoding
differ.

### Mailbox register triples (AMD family `0x1A` / Strix Point)

```
MP1    message 0x03B10928   response 0x03B10978   arg base 0x03B10998
RSMU   message 0x03B10A20   response 0x03B10A80   arg base 0x03B10A88
```

Argument *n* lives at `arg base + 4n`. All six arguments are rewritten on every transaction, so no stale
argument can leak into a message. Both triples **agree across RyzenAdj** (`lib/nb_smu_ops.c`), **ZenStates-Core**
and **G-Helper** — three independent reverse-engineering projects landing on the same addresses is what makes
them trustworthy.

The two triples are **not interchangeable**: the CPU curve is on MP1, the graphics curve on RSMU. That is why
the triple travels with every transaction instead of being ambient state.

## What each rail can and cannot promise

**Cores — no read-back, and acknowledgement is not proof.** The mailbox is known to answer `REP_MSG_OK` while
the platform's power mode suppresses the effect. A successful write means *the SMU accepted the message*, and
nothing more. That the curve really moves was established here by **measurement**, not by trusting the response
code: an all-core `-30` dropped every per-core voltage word in the SMU's PM telemetry table by **~76 mV** under
constant load (from ~1.03 V to ~0.95 V) and back to stock on reset.

**iGPU — has a getter, so every write is confirmed.** `0x20` reads the margin straight back; a mismatch is
reported as a failure. This is the one rail where an *accepted-but-ignored* write cannot hide — which is exactly
the failure mode that forced the CPU path to rely on statistics.

## Scale: 2.5 mV/count on the cores, 5.0 mV/count on the iGPU

Measured, and **not the same number**:

- **Cores: 2.5 mV per count.** All-core `-30` moved every per-core voltage word in the PM table from ~1.03 V to
  ~0.95 V, i.e. ~76 mV over 30 counts. So `-40` is **~100 mV**. AMD publishes no mV-per-count figure for Zen 5;
  the community's Zen 3 number was 3–5 mV.
- **iGPU: 5.0 mV per count**, exactly double. Measured as 5 counts = 25 mV, 10 = 50, 20 = 100 — **strictly
  linear**, read off the iGPU core voltage under load. Reusing the CPU figure here would have understated every
  offset by half.

Either figure is only a **display aid**: a Curve Optimizer offset shifts the whole V/F curve rather than clamping
a voltage, so the delivered delta moves with frequency and temperature. The UI shows it with a "≈" for that
reason.

### Numbers, exactly as measured

| experiment | result |
|---|---|
| all-core `-30`, constant load, PM telemetry table | every per-core voltage word 1.03 V → 0.95 V, ~76 mV |
| offset applied to **one** slot, PM telemetry table | exactly one per-core voltage word dropped **~78 mV**, then recovered |
| iGPU margin `0 → -5 → 0`, reading `0x20` back | every transaction `REP_MSG_OK`, each step confirmed |
| iGPU 5 / 10 / 20 counts | 25 / 50 / 100 mV, strictly linear |
| stock, under load | Zen 5 ≈ **1.17 V**, Zen 5c ≈ **1.02 V** |

The `~76 mV` and `~78 mV` figures are two views of the same experiment measured two ways (all cores vs one slot)
and differ by measurement noise, not by mechanism.

## Granularity is per CLUSTER, not per core — and that is hardware

`0x4B` *does* address a single slot and provably works: it moves exactly one per-core voltage word in the PM
table. But that word is the core's **request**, not what it is fed. Each cluster shares a rail whose setpoint is
the **MAXIMUM** of its cores' requests, with no per-core LDO drop below it on this die. So within a cluster the
delivered undervolt is the **smallest** offset among its cores: offsetting one core alone changes nothing, and
per-core sliders would leave **8 of 10 cores inert**.

The clusters, however, are **independent domains**: measured at stock under load Zen 5 sits near 1.17 V and
Zen 5c near 1.02 V, and offsetting one cluster moves only that cluster. Hence exactly **two** tunable CPU values.
Each is written to **every slot of its CCD** so that no core is left holding a milder request that the rail
would then follow.

## Slot topology (measured, because the SMU will not say)

`0x4B`'s per-core argument packs the selector and the margin:

```
[31:28] CCD     [23:20] core within CCD     [15:0] margin
```

Which slots are **populated** had to be measured: an unpopulated slot answers `REP_MSG_OK` and changes nothing,
**exactly like a populated one**, so cores cannot be enumerated by response code.

Measured on this die (4× Zen 5 + 6× Zen 5c, 8 slots per CCD):

- **Zen 5 on CCD 0: slots 0, 2, 4, 6** — stride two, *not* consecutive;
- **Zen 5c on CCD 1: slots 2..7** — the **last six** of eight.

The code reproduces exactly that and extrapolates to the other Strix Point configuration: a Ryzen AI 9 HX 370 is
4+8, so its Zen 5c would fill slots 0..7. A wrong guess on some future SKU is **benign rather than dangerous**:
a core would simply get no slider, or a slider would drive an empty slot and do nothing.

## The iGPU opcode: a wrong-opcode false negative

The graphics rail is **not** the opcode RyzenAdj's `set_cogfx` uses. That one — **PSMU `0xB7`**, inherited from
the Rembrandt…Hawk Point lineage — does not list Strix Point at all, and probing it here answers **`0xFD`
"prerequisites not met" every single time**, idle and under GFX load alike. That reads exactly like an absent
feature; it is a wrong opcode.

The right numbers come from **ZenStates-Core**, whose `APUSettings1_Strix` inherits `APUSettings1_Phoenix`. What
makes that table trustworthy for *this* die rather than one more guess: the same file carries MP1
`SetDldoPsmMargin` **`0x4B`** and `SetAllDldoPsmMargin` **`0x4C`** — the CPU opcodes already proven to work
here. Verified on this machine: `0 → -5 → 0`, every transaction `REP_MSG_OK`, each step confirmed by reading
`0x20` back.

## Argument encoding — three forms, two of which fail silently if confused

```
Encode(counts)                20-bit field:  negatives as 0x100000 - |counts|, then & 0xFFFFF
CoreArg(ccd, slot, margin)    (ccd << 28) | ((slot % 8) << 20) | (margin & 0xFFFF)
GpuMargin(counts)             ZenStates' Utils.MakePsmMarginArg verbatim: 16-bit two's complement
```

Three load-bearing details:

1. **`0` is sent as a plain `0`, never as `0x100000`.** `0x100000` would set bit 20, which the *per-core* form of
   the message uses as a core selector — a known source of rejected arguments in other tools. The trailing mask
   keeps that invariant local instead of depending on the caller's clamp.
2. **The CPU's 20-bit `-5` is `0xFFFFB`; the GPU's is `0xFFFB`.** The mailbox accepts either **without
   complaint** while meaning something else entirely. That is why the GPU form is a separate function rather
   than a shared one with a width parameter — the difference fails silently.
3. **The iGPU read-back is asymmetric with its setter.** The setter takes a 16-bit two's-complement margin
   (`-5` = `0xFFFB`), the getter answers a **full 32-bit signed** value (`-5` = `0xFFFFFFFB`). Sign-extending
   the reply from bit 15 turns a perfectly good `-5` into `-65541`.

## SMU response register

Values per AMD's own driver (`smu_cmn.c`); AMD notes they are defined per-ASIC, so an unrecognised value is
reported raw rather than folded into a generic "failed".

| value | meaning | handling |
|---|---|---|
| `0x00` | still executing | polled |
| `0x01` | OK | success |
| `0xFC` | busy with another command, retry | polled through like an in-flight response, not surfaced |
| `0xFD` | valid command, prerequisites not met | reported; documented cause is the active platform power mode — the mailbox refuses while the system sits in an energy-saving mode, hence "try a performance power mode" |
| `0xFE` | this firmware has no such command | **latched** (`_unsupported`) so a mode switch can't spam a dead mailbox |
| `0xFF` | the command ran and its status was failure | reported |

A separate sentinel (`uint.MaxValue`) means "the transaction never reached a response" — a register access or the
interlock failed — distinct from every value the register can hold. When it is set, `Transact`'s more specific
reason (interlock, busy, no answer) is preserved rather than overwritten.

## The transaction

1. **Take the cross-process PCI interlock** (see below).
2. **Wait for the mailbox to be idle.** A zero response means a message is still in flight — someone else's or a
   leftover — and writing ours on top of it would race. Budget `200 ms`.
3. **Clear the response, publish the arguments, then the message.** Order matters: the SMU latches on the
   *message* write, so the arguments must already be in place, and a stale non-zero response would otherwise be
   mistaken for this message's answer.
4. **Poll the response.** Normally acknowledged in microseconds. A `0xFC` is polled through like an in-flight
   response. Spin 32 × `SpinWait(64)` before falling back to `Sleep(1)`, up to the same `200 ms`.
5. **Read argument 0 inside the interlock.** That is where a getter's value comes back, and reading it *after*
   releasing would race another tuning tool's transaction into our reply.

### Timeout budget, deliberately finite

`200 ms`. RyzenAdj's equivalent poll loop has **no timeout at all**, which is how a stuck mailbox turns into a
hung caller. Anything beyond the budget is a wedged SMU and we return rather than spin.

### Cross-process interlock: `Global\Access_PCI`

The mailbox is reached through the PCI config index/data pair on `00:00.0`, which **HWiNFO, CPU-Z, Ryzen Master
and RyzenAdj** all poke as well — hence the one name that ecosystem agreed on. Held across the **whole**
transaction, not per register access: per-access locking still lets another agent's message execute against our
arguments. Wait budget `5000 ms`.

Two behaviours worth knowing:

- The mutex is **opened per transaction** rather than held for the process lifetime, so the app never keeps a
  machine-wide lock while idle. If the name cannot even be created the transaction proceeds **unlocked** — better
  a possible collision with another tuning tool than no Curve Optimizer at all.
- `AbandonedMutexException` means the previous owner died holding it; it is ours now and the transaction
  continues.
- If the wait times out, the write is abandoned with *"another tool is holding the PCI access lock"* and **the
  offset silently does not apply**. Users read that as "the undervolt doesn't hold" — recorded as a known defect
  in `docs/refactoring-plan.md`, not fixed here.

## Exposed range: `-40 … 0`

Negative only. A positive offset **raises** voltage, which buys nothing here and is a thermal and stability risk,
so it is not offered.

The floor is `-40` to match what the rest of the ecosystem allows, and because it is a real voltage bound rather
than a round number: at the measured ~2.5 mV/count it is **~100 mV** — a normal undervolt target — while the only
published failure datum on a sibling Zen 5 die is a crash at `-50`.

The stated reason for not going lower:

- An **all-core** offset is bounded by the **worst** core, which is exactly why per-core Curve Optimizer exists.
- Undervolt failures on Zen 5 surface **hours later at idle** as machine-check errors or silent corruption rather
  than as an obvious crash under load, so the end of the slider should not be a place a single drag lands by
  accident.

> **Contradiction, recorded not fixed.** The same block states that per-core is "unreachable here (opcode `0x4B`
> is unconfirmed on this die, reported rejected on Krackan Point, and there is no way to learn the core-fuse
> topology)". The rest of the file says the opposite: `0x4B`/`0x4C` are "the CPU opcodes already proven to work
> here", the per-core argument layout is confirmed by single-slot measurement, and `SetDomains` — the path the
> 3-domain UI actually uses — writes `0x4B` to every slot of each CCD. Per-core *sliders* may well be the wrong
> UI, but per-core *writes* are demonstrably in use.

### The iGPU floor: `-50`

`-50` is the full range ZenStates documents for the PSM margin on Zen 4 and newer, **deliberately not narrowed**.
At 5 mV/count that floor is **-250 mV**, and this sample already fell over well before it — but the stable limit
is a property of the **individual die**, and clipping every machine to one unlucky one would cost good parts real
headroom. The guard is the **label**, not the bound: the row reads `-50 (≈-250 mV)`, and a figure like that warns
far better than a slider that silently stops somewhere. Undervolt is opt-in, starts at stock, and Reset is one
click away.

## Volatility and re-apply

The offset lives in **SMU state**; a power cycle restores stock. Nothing is written to firmware — which is also
the recovery path. The app is therefore the source of truth and re-applies per performance mode at **startup, on
resume, and on each mode switch** (`LaptopService`), exactly like the GPU clock offsets.

## Gating: which CPU, and only with the driver

`TryCreate` returns null — so the port stays null and the UI hides the section — unless **both**:

- the CPU is a known Strix Point, and
- PawnIO is installed.

The CPU check is CPUID only: vendor `AuthenticAMD`, then family = `((eax>>8)&0xF) + ((eax>>20)&0xFF)`,
model = `((eax>>4)&0xF) | (((eax>>16)&0xF)<<4)`, requiring **family `0x1A`, model `0x20` or `0x24`**.

**Krackan Point (`0x60`) and Strix Halo (`0x70`) are deliberately NOT accepted.** They share the family case
group in the reverse-engineering projects, but the published results **diverge** — the same opcode is reported
rejected on one and effective on the other — so claiming support would be guessing on someone else's hardware.

The probe is deliberately **cheap** (CPUID plus a registry read) because composition runs on the **UI thread**.
The driver handle is opened on first use instead, off that thread, because loading a PawnIO module makes the
kernel verify a signed bytecode blob — exactly the kind of blocking call this app keeps off the UI thread. A
failed open is retried on the next call, since the user may install the driver while the app is running.

The driver check is the **registry** one, not a device open: it must stay cheap and must not depend on elevation.
Gating on it keeps the section honest — without the driver the sliders could appear and then refuse every write.

Cluster detection uses physical cores: CPUID leaf `0x8000001E` EBX[15:8] is *threads per compute unit minus 1*,
so logical ÷ that = cores. Falls back to the logical count if the leaf is absent, which only mislabels the group
sizes — nothing addresses cores by them. The section header name comes from CPUID leaves `0x80000002..0x80000004`
(the 48-byte brand string).

## The iGPU as a third domain

The iGPU is offered as a third row in the same list rather than in a port of its own: it is the same kind of
thing as a cluster — an AVFS curve offset on one rail of this package, volatile, re-applied per performance
mode.

Added **unconditionally** on a supported Strix Point rather than probed, because probing means opening the driver
and loading a module, and the constructor runs during composition on the UI thread. Every Strix Point ships the
integrated Radeon, so the assumption is safe; and if some SKU ever refuses, the write fails **loudly** through
`LastError` instead of silently doing nothing.

Deliberately **not** offered when cluster detection failed: that path falls back to the single all-core `Set()`,
and a domain list containing only the iGPU would quietly turn the CPU slider into a GPU one.

Domain identities (the settings key names the hardware domain, so a preset survives reordering/relabelling):

| label | key | range | mV/count |
|---|---|---|---|
| Zen 5 | `ccd:0` | `-40 … 0` | 2.5 |
| Zen 5c | `ccd:1` | `-40 … 0` | 2.5 |
| iGPU | `gfx` | `-50 … 0` | 5.0 |

## `SetDomains` — write every slot, abort on first refusal

Each CPU domain is written to **every slot of its CCD — all 8, populated or not**. Two reasons: the rail follows
the mildest request in the cluster, so a core left un-offset would undo the setting; and an empty slot answers
`REP_MSG_OK` and changes nothing (measured), which also makes this correct on a SKU with a different populated
set. The iGPU is one write on the other mailbox, not eight on this one.

A partial failure leaves the clusters inconsistent, so the **first refusal aborts** and is reported rather than
pressed on with.

## Read-back handling on the iGPU

The write is confirmed by reading `0x20` back. A **missing or refused read-back is not treated as a failed
write**: the write was acknowledged, and reporting an error would send the user chasing a problem that may only
exist in the verification path. A **mismatch** is a failure:
*"the SMU accepted iGPU {counts} but reads back {applied}"*.

## Port contract (`Domain/Ports.cs`)

- `Set`/`SetDomains` returning true means the SMU **accepted the message**, not that the curve provably moved.
- A too-aggressive offset fails **hours later at idle** rather than under load, so callers must treat it as
  opt-in, warn, and default to stock.
- `VoltageDomain.Range` null = "use the port's". `VoltageDomain.MillivoltsPerCount` null is **different**: it
  means *unknown for this domain, show no estimate*, **not** "inherit". Volts-per-count is a property of one
  particular rail and has to be measured on it — a domain that borrowed a neighbour's figure would misreport
  every offset by a factor of two. A domain nobody has measured must be able to say so rather than guess.
- One caveat on the port's own doc: it states this hardware "offers no trustworthy read-back". That is true of
  the CPU clusters but **not** of the iGPU, which has `0x20` and is verified on every write.

## UI (`UI/ViewModels/CoViewModel.cs`)

One slider per independently tunable voltage domain, or a single all-core slider on a one-domain CPU. Per-**core**
is deliberately not offered: within a rail the delivered voltage follows the mildest core's request, so all but
one core per cluster would be inert.

- Applies on change, **debounced 400 ms**, and persists **per performance mode**; an unconfigured mode is stock.
- Applying is **slow** here — an SMU mailbox transaction per core slot, waiting on a machine-wide lock — so the
  `apply` delegate handed to the view-model is expected to move the work off the UI thread itself
  (`AppController.SetCo`). Nothing in `CoViewModel` may block.
- All rows are applied **together on one debounce tick** rather than per row, so dragging one slider does not
  re-write the other clusters' cores one transaction at a time.
- The label shows the **AVFS step count first** — it is what the hardware takes and what every tool and write-up
  talks in — with the millivolts it works out to in brackets, since that is the unit an undervolt is actually
  thought about in. The mV figure carries "≈" on purpose. Each rail brings its **own** scale, so the arithmetic
  is per row; a rail that has never been measured (`null`) shows the bare count, because steps are the hardware's
  own unit and remain usable, which beats printing a millivolt figure borrowed from a different rail's
  measurement — that would look authoritative and be wrong.
- A domain label is an architecture name or a technical abbreviation ("Zen 5c", "iGPU"), so it is shown as-is
  and not translated; the single-domain case uses the localized section word instead.
