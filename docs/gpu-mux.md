# GPU mode (MUX) switch — shared abstraction and the Acer implementation

Status: **implemented**. The Acer backend reads the mode over the recovered `AcerGamingFunction` selectors and
queues a change. Runtime is **unverified** (no MUX hardware was available to this change), and the Acer `Set`
effect in particular is untested (see §4).

A MUX switch is the hardware multiplexer that routes the built-in panel either through the integrated GPU
(hybrid / Optimus) or straight to the discrete GPU (discrete / Ultimate). Switching it is the most dangerous
single control in the app: until the firmware re-routes the panel there may be **no display**, and the change
only takes effect on the **next restart**. Every vendor path therefore shares one abstraction and one set of
safety rules.

## 1. The shared Domain port

`Domain/GpuMux.cs` — one port, one state, one result, one vocabulary:

| Member | Meaning |
|---|---|
| `IGpuMux.Supported` | false when the firmware exposes no switchable MUX (the UI then shows the refusal) |
| `IGpuMux.Modes` | the labelled modes the machine offers, ids being the vendor's stable wire values |
| `IGpuMux.Read()` | the current mode, the queued mode (null when none or equal to the current), and whether a restart is required |
| `IGpuMux.Request(modeId)` | QUEUES a change; takes effect on the next restart. A request equal to the device-reported current mode is a no-op (nothing written, no pending, no restart) |
| `GpuMuxState(Current, Pending, RebootRequired)` | what the UI shows — current AND pending, never a blind toggle. A pending equal to the current is normalized to `null`/`false` (no change); an unknown current keeps its pending |
| `GpuMuxChange(Ok, Queued, Error, NoOp)` | exactly one success shape: `Queued` means a restart follows, `NoOp` means the mode is already current; `Error` is a key or the vendor's words |
| `GpuMuxMessages` | the shared mode labels and the restart + black-screen warning (one Russian entry) |

The port is attached to the machine as `Device.GpuMux`. The UI is the shared `GpuMuxViewModel` card in the
**Overclocking and Power** drawer (`UI/ViewModels/GpuMuxViewModel.cs`, rendered inline in `TuningView.axaml`),
reached through `LaptopService.ReadGpuMux` / `RequestGpuMux` and the `GpuMuxSection` of `UiActions`.

**THE CARD IS ONE STABLE LINE.** It renders the current mode in a single, explicitly non-wrapping sentence; while
the value that will apply after a reboot DIFFERS from the ORIGINAL value the card first showed, the value is
MARKED with a trailing `*` on that same sentence — `"GPU mode: Hybrid*"` — and a short muted note NEXT TO THE
SELECTOR (`"* Will be applied after a reboot."` / `"* Будет применено после перезагрузки."`) explains the mark.
There is **no Apply button**: the ComboBox selector IS the switch, and choosing a different mode runs the change
flow (below). There is no inline suffix and no bottom caption. The mode labels drop the vendor brand words
("Optimus"/"Ultimate" live on in the backend comments) and are kept short, so the sentence fits the ~400px card
interior on one line even in Russian; the line is `TextWrapping="NoWrap"`, so the single marker cannot wrap onto a
second row or resize the `SizeToContent` flyout. The note shares the selector's row and is conditional, so it
neither reserves an always-present blank nor adds a full-width caption. (The no-MUX refusal is a separate
wrapping `TextBlock`, visible only when the port is unsupported, so the one-line rule applies to the mode
sentence.) Outcomes that are not a queued change (asking for the mode the machine is already in, a vendor
refusal) are not drawn in the card at all: they go to the app's existing transient status line, the same one
every other control failure uses. No notification is raised for MUX outcomes.

## 2. Safety posture (applies to every implementation)

1. **Queued, never immediate.** No implementation applies a MUX change directly or from the periodic refresh
   loop. The Acer path has no "apply" call at all: its single `SetGamingMiscSetting` write is the queue, and the
   panel re-routes on the next boot.
2. **Explicit consent with a warning.** Every real request goes through a confirmation dialog carrying the restart
   and black-screen warning (`GpuMuxMessages.Warning`). There is **no Apply button**: changing the selector IS
   the request (`GpuMuxViewModel.OnSelectedIndexChanged` → the apply flow). A declined prompt queues nothing AND
   **reverts the selector** to the mode actually in effect; a refused write does the same. A selection that
   equals the device-reported current mode is a no-op (no confirmation, nothing queued, no mark/note), and the
   ViewModel's own programmatic selection updates (from `Refresh`/a revert) are suppressed so they are never
   mistaken for a user request.
3. **Refuse over guess.** The Acer path validates against its recovered mode enum and gates on the selector-9
   capability signal, refusing an unknown mode or an unreadable state. Nothing writes a method id or a value the
   device did not report.
4. **Single-flight.** The Acer write is serialized by the MUX port's own lock, so two requests cannot overlap.
5. **Current + pending are shown, in one line.** The card reads the live state when the drawer opens and after a
   request — never on a timer — and folds it into a single, non-wrapping sentence: the current mode, MARKED with
   a trailing `*` while the value that will apply after a reboot differs from the ORIGINAL the card first showed.
   A short note NEXT TO THE SELECTOR explains the mark and is shown only in that same condition (no separate
   pending line, no inline suffix, no always-present caption). The labels are deliberately compact so the marked
   sentence fits the card's single line even in Russian, and the note shares the selector's row rather than
   adding a full-width caption.
6. **Requesting the current mode is a no-op; an equal queued value is no change.** `Request(modeId)` compares
   the request against the mode the device reports as current on that read-back. If they are equal nothing is
   written, no pending value is recorded and `GpuMuxChange.NoOp` is returned — so the card says "already the
   current mode" and never marks an after-restart result that will not happen. Independently, `Read()`
   normalizes an already-queued value that EQUALS the reported current to no pending and `RebootRequired =
   false` (`GpuMuxState.From`). The ViewModel keeps the note/mark keyed to the ORIGINAL value captured on the
   first populated read: it shows while the requested/pending value differs from that original and hides when
   the user reverts to it (so the `*`/note survive a read-back that already reports the requested mode, but a
   revert or a no-op produces neither). When the current mode cannot be read it is UNKNOWN and both the pending
   and the request are kept/queued as real (equality is never guessed); the ViewModel then falls back to "there
   is a pending change" for the note. The ViewModel enforces the request rule *before* the confirmation, so the
   restart/black-screen warning only appears when something will actually be queued; the port re-checks it
   independently.

## 3. Acer implementation — the recovered gaming-WMI path

`Infrastructure/Vendors/Acer/AcerGpuMux.cs` implements the shared port over the SAME `AcerGamingFunction` WMI
class this project already uses for profiles, fans and sensors (root\WMI, GUID
`7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56`). **No new WMI method is opened**: everything rides
`Get/SetGamingMiscSetting`, whose selector is the low byte of `gmInput` — the encoding the profile path already
uses at selector `0x0B`. The interface was recovered by reverse-engineering `AcerQAAgent.exe` on an AN18-61
(high confidence):

| Step | `gmInput` | Decode |
|---|---|---|
| Capability probe | selector **9** | `status = gmOutput & 0xFF`, signal byte = `(gmOutput >> 8) & 0xFF`; **present** when the status is 0 and the byte is neither 0 nor 255 (observed **7**). It is a presence/bitmask signal, NEVER the mode enum |
| Read current mode | selector **2** | `status = gmOutput & 0xFF` (0 = ok), `mode = (gmOutput >> 8) & 0xFF` |
| Write a mode | `(mode << 8) \| 2` | the reply's low byte is the status |

Mode enum: **1 = MS Hybrid (Optimus)**, **2 = Discrete Only**, **3 = Auto Select / DDS**, **255 = Not
Supported**. The Windows backend builds the port from its existing `GmGet`/`GmSet` helpers in
`AcerDevice.Windows.InitVendor` — the port takes the two calls as delegates, so it holds no transport and is
testable without WMI — and only when `_gaming` opened; otherwise the machine keeps the
`AcerGpuMux.Unsupported` refusal (and the shared card states it).

- **Read** never guesses: a non-zero status, the 255 sentinel or a value outside the enum yields no current
  mode (the card says "unknown"), not a routing the firmware did not report.
- **Request** is **queued, not immediate**: the single WMI write records the mode and the port reports `Queued`;
  it never claims the panel already moved. A request equal to the mode the selector 2 read reports as current is
  a **no-op** (no write, `GpuMuxChange.NoOp`); an unreadable current mode is unknown, so the request proceeds.
  The pending claim is session-scoped, so a fresh process after the restart reads the real mode with no pending.
  Because the firmware reports the request back at once, that read equals the current and `Read()` normalizes the
  pending away (no pending, no restart) — the card never marks `"2*"` once the machine already reads 2.
- The write is **single-flight** (one lock around the WMI call) and validated against the enum before any call
  reaches the firmware. The explicit confirmation (§2.2) is the shared card's, so this backend needs no
  write-consent gate of its own.

**Linux** is unchanged: mainline `acer-wmi` and the Linuwu-Sense module expose no MUX control at all, so the
backend keeps the clean `AcerGpuMux.Unsupported` refusal on non-Windows.

**Unverifiable**: the selector numbers and the packing come from the disassembly, not from a call this app has
made, and the actual `SetGamingMiscSetting(selector 2)` effect on a real panel is untested. Nothing here
guesses — but re-verify against the binary/device before trusting a write.

## 4. Rollback / recovery

- **Acer.** On an unsupported/refused machine nothing is ever written, so there is nothing to roll back; the
  user is directed to NitroSense/PredatorSense or the BIOS. On a supported machine the request is a single
  `SetGamingMiscSetting` write that the panel applies at the next boot: if the screen is black after the restart,
  force a power-off (hold the power button), start the machine, and set the mode back — possibly from a console
  or a second display. The previous mode is readable through `IGpuMux.Read()` (selector 2).
- The app owns no "revert" timer and never restores a previous mode silently.

## 5. Files, tests, and what is unverifiable

| Area | Files |
|---|---|
| Domain | `Domain/GpuMux.cs` |
| Acer | `Infrastructure/Vendors/Acer/AcerGpuMux.cs` (`AcerGpuMux` + `AcerGpuMuxProtocol`; wiring in `AcerDevice.Windows.InitVendor`) |
| Composition | `Device.GpuMux`; `LaptopService.ReadGpuMux`/`RequestGpuMux` (`LaptopService.Tuning.cs`) |
| UI | `GpuMuxViewModel.cs`, `TuningViewModel.cs`, `TuningView.axaml`, `UiActions.GpuMuxSection`, `AppController`/`FlyoutCoordinator` |
| Tests | `GpuMuxTests.cs` (Acer read/packing/capability/queued/single-flight/refusal + wiring + service flow + translation), `GpuMuxViewModelTests.cs` |

Pinned by tests: the Acer read decode (`status`/`mode`, 255 and error status), the Acer write packing
`(mode << 8) | 2`, the selector-9 capability gate, the Acer single-flight write, the session-scoped pending, the
**queued-not-immediate** rule (a single WMI write, never an immediate apply), **the current-mode no-op** (no
write / no pending / `RebootRequired = false`, and the conservative unknown-current path), **the equal-pending
normalization** (a queued value equal to the reported current is reported as no pending / `RebootRequired =
false`), unknown-mode and declined-consent refusals, the Windows wiring, the service/device flow, the card's
confirm gating (including "already current" with no confirm), and that every shared message is translated.

**The card is one line (the layout guard).** `GpuMuxViewModelTests` pins that a value differing from the
ORIGINAL captured on the first populated read marks the current-mode sentence with a trailing `*` on the same
line (no newline, no inline suffix) and sets `NoteVisible`, that an equal pending is suppressed (no `*`/note),
that reverting to the original hides the note, that a later read reporting the requested mode does not move the
baseline, that the labels are compact, and that `Note` explains the mark. The same tests pin the selector-driven
apply: changing the selector runs the confirmation and queues (with no command invoked), cancelling reverts the
selection and queues nothing, a refused write reverts the selection, the port's NoOp queues nothing, and a
programmatic `Refresh` selection is not treated as a request. The source guard in
`GpuMuxTests.TheMuxCardHasOneStableLineAndTheFlyoutIsSizeToContentAgain` pins that `TuningView.axaml`'s
`GpuMuxState` line is `TextWrapping="NoWrap"` with no `MinHeight`/`MaxHeight`, that there is no
`GpuMux.ApplyCommand`/`{l:Tr Change}` button, that `GpuMuxNote` sits NEXT TO the selector (same Grid row,
`IsVisible` bound to `GpuMux.NoteVisible`, muted) with no always-present
`GpuMuxFootnote` caption, that the (separate, wrapping) `GpuMuxUnsupported` refusal is the only wrapping block,
and that there is no `PendingText`/`Message` line; it also pins that `MainWindow.axaml` is back to its normal
`SizeToContent="WidthAndHeight"` flyout — no hard-coded 468x860 frame and no cap-to-Home, so a short Home page
shows no empty gap and a queued MUX change cannot resize the flyout.

**Unverifiable** (flagged, not hidden): the Acer selector numbers/packing (from the binary) and especially the
actual effect of a `SetGamingMiscSetting(selector 2)` write on a real panel; and whether any given Acer model's
NitroSense switch maps onto the same interface.
