# HID LampArray / Windows Dynamic Lighting — design

Goal: let **Windows Dynamic Lighting** (Settings → Personalisation → Dynamic Lighting) and any LampArray-aware
app paint this laptop's keyboard, with AcerHelper as the translation layer — the equivalent of what Logitech
G HUB provides for LIGHTSYNC hardware.

## Why a driver is unavoidable

Windows enumerates lighting devices **only** as HID LampArray collections (usage page `0x59`, per
[HID Usage Tables 1.4](https://www.usb.org/sites/default/files/hut1_4.pdf)). Microsoft's own
[device guidance](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/dynamic-lighting-devices)
lists exactly two ways to be compatible: native firmware, or a **VHF driver**. There is no user-mode API to
register a LampArray; `Windows.Devices.Lights.LampArray` is a *consumer* API.

Logitech's "LampArray translation layer" is precisely that, and its shape is visible on any machine with G HUB
installed:

```
logi_lamparray_usb.inf   class USB      – filter on the real device; enumerates USB\VID_046D&PID_xxxx&LAMPARRAY\…_SLOTnn
logi_lamparray_hid.inf   class HIDClass – HKR,,"LowerFilters",…,"vhf" + "logi_lamparray"
logi_lamparray.sys       KMDF, 89 KB    – Signer: Microsoft Windows Hardware Compatibility Publisher (Attested)
logi_lamparray_service   Win32 service  – the actual protocol translation
```

AcerHelper mirrors it, minus the USB filter (there is no real LampArray-capable device to filter — the lamps
are synthesised from the ENE controller's zones) and minus the separate service (the app is already running
elevated).

## Layers

```
Windows Settings / a game / any LampArray app
        │  HID feature reports 1–6 (attributes, attribute request/response, multi-update, range-update, control)
        ▼
AcerHelperLampArray.sys  ── driver/                     virtual HID device via vhf.sys; owns the report descriptor
        │  3 IOCTLs (driver/AcerHelperLampArray/public.h)
        ▼
LampArrayTransport       ── Infrastructure/Vendors/Generic/*.Windows.cs  device node (SwDeviceCreate) + IOCTL channel
        ▼
LampArrayBridge          ── Infrastructure/Lighting/    rate limit, write collapsing, ownership arbitration
        ▼
RgbZone.ApplySubZone /   ── Domain/Rgb.cs               the existing zone model
RgbZone.ApplyEffect
        ▼
EneHidController         ── Infrastructure/Vendors/Acer/                 A4 feature reports over HID-over-I2C
```

Deliberate split: the driver is as dumb as possible (static descriptor, a lamp table pushed down from user
mode, frame accumulation) because every change to it means re-signing. Everything interesting — geometry,
rate limiting, who owns the backlight — is C#.

## The lamp model

The ENE controller exposes **zones, not keys**: 4 keyboard zones (`zonemask 0x0F`) plus, on models that have
it, a 5-zone lightbar addressed as one region (see [lighting-an18-61.md](lighting-an18-61.md)). So the layout
is built from whatever `RgbZone`s the active controllers advertise (`LampArrayLayout.Build`):

- The **first** zone is treated as the keyboard: the full 330 × 110 mm rectangle, its sub-zones spread evenly
  left-to-right (zone-mask bit 0 = leftmost), one lamp each.
- Any **further** zone becomes a 12 mm strip below it, one lamp. That keeps spatial effects honest — a vertical
  wipe reaches the front lightbar last, as it would on real hardware.
- `LampArrayKind` = `Keyboard` when the first zone is multi-zone, else `Chassis` (a single-lamp device should
  not attract key-shaped effects).
- Level counts: 255/255/255 per channel, **IntensityLevelCount = 1** — this hardware has no per-lamp gain
  (brightness is a per-write byte for the whole keyboard), and per spec that tells the host to bake brightness
  into RGB. Intensity 0 therefore means "lamp off".
- A "follows performance profile" lightbar is **not** offered as a lamp while that flag is on: the firmware owns
  it then, and the app doesn't drive it either.

Nothing reports the keyboard's real dimensions (not SMBIOS, not the WMI, not `acer-models.json`), so the
geometry is nominal. What matters to the host is the *proportions and order*, which decide which way a wave
sweeps — not absolute accuracy.

## Wire format

Units on the wire are **micrometres** and **microseconds**. The report descriptor is static (292 bytes,
Microsoft's canonical one from [ArduinoHidForWindows](https://github.com/microsoft/ArduinoHidForWindows), MIT)
— lamp count, geometry and kind travel in the *attributes report*, not in the descriptor, which is why the app
can change the layout without touching the driver.

| Report | Dir | Payload | Purpose |
|---|---|---|---|
| 1 | GET | 22 B | lamp count, bounding box, kind, min update interval |
| 2 | SET | 2 B | select which lamp report 3 describes |
| 3 | GET | 28 B | that lamp's position/latency/purposes/levels — then **auto-advances** (this is how the host enumerates) |
| 4 | SET | 50 B | up to 8 × (lamp id, RGBI) |
| 5 | SET | 9 B | one RGBI for an id range |
| 6 | SET | 1 B | `AutonomousMode`: host takes (0) or releases (1) the surface |

Reports 4/5 carry an *update-complete* flag on the last report of a batch. The driver stages writes and only
promotes them to a frame on that flag, so the app never paints a half-updated keyboard. With ≤ 8 lamps a whole
frame is a single report 4.

### App ↔ driver IOCTLs

Defined once in [`driver/AcerHelperLampArray/public.h`](../driver/AcerHelperLampArray/public.h); the C# side
writes the same structs by hand (`BinaryPrimitives`), so field order and sizes are load-bearing and asserted
with `C_ASSERT` on the C side.

| IOCTL | Payload | Notes |
|---|---|---|
| `SET_LAYOUT` | `AHLA_LAYOUT` (1820 B) | publishes the virtual device; re-publishing re-enumerates it (the host caches attributes) |
| `WAIT_FRAME` | out `AHLA_FRAME` (268 B) | pends in a manual queue; **last-one-wins**, so a slow app skips frames rather than queueing them |
| `STOP` | — | un-publishes; also happens when the last handle closes |

Two handles, on purpose: a synchronous file object serialises its requests, so a `STOP` issued while
`WAIT_FRAME` is pending would queue behind it. Control and frames therefore use separate handles, and the
driver counts opens so a crashed app can't leave a zombie entry in Dynamic Lighting.

The device node itself is created by the **app** (`SwDeviceCreate`, root-parented, `DriverRequired`) when the
feature is switched on, and destroyed when it is switched off or the app exits. A permanently installed node
(`devgen`) would leave Windows offering a lighting device that nothing answers.

## What makes this more than a memcpy

Three hardware facts, all documented in [lighting-an18-61.md](lighting-an18-61.md):

1. **The bus.** A host paints at 30–60 Hz. The ENE controller hangs off HID-over-I2C, a full keyboard apply is
   several feature reports, and bursts land corrupted when a display contends the bus (this is why
   `EneHidController` paces writes 10 ms apart and coalesces per region). So:
   - the layout **advertises** `MinUpdateInterval = 100 ms`, which a well-behaved host honours by itself;
   - the bridge **enforces** the same 100 ms regardless, sleeping after each apply. Since frames are
     last-one-wins in the driver, throttling drops intermediate frames instead of building a backlog.
2. **Write count.** A uniform colour across a zone is **one** all-zones report instead of one per sub-zone —
   and most host effects (solid, breathing, "match my accent colour") are uniform. Sub-zones whose colour did
   not change (±5 per channel) are skipped entirely, which kills the write-per-frame a slow gradient would
   otherwise generate. This is the same trick `LightViewModel.ApplyNow` uses, for the same reason.
3. **The EC fights back.** A performance-profile switch forces the keyboard to the amber OPMODE flash and
   wipes the RGB; sleep drops it; a clamshell lid-open restores from black. Every one of those paths calls
   `LightingCoordinator.Paint`, which — while a host owns the surface — re-asserts the host's *last frame*
   instead of the app's own lighting (`LampArrayBridge.Reassert`). The profile flash itself is skipped then: it
   is a global write that would visibly fight the host's colours.

## Ownership

`AutonomousMode` is the whole protocol for this: a device starts autonomous (painting itself — for us, showing
the app's own per-mode lighting), a host clears the flag to take the surface, and sets it again to hand it
back.

| Event | Behaviour |
|---|---|
| host takes the surface | `LampArrayBridge.HostOwnsLighting` = true; `LightingCoordinator.Paint` stops painting the app's lighting **and the Lighting panel's controls are greyed out**; status line says so (G HUB likewise blocks its lighting UI while Dynamic Lighting is on) |
| host frame arrives | translated to zone writes, throttled + collapsed as above |
| profile switch / resume / lid-open | the host's last frame is re-asserted, ignoring the dedupe |
| host releases, or the feature is switched off | the controls come back, and the app repaints the current mode's lighting immediately, then runs its usual re-apply burst |
| lid shut in clamshell mode | blanking still wins — a hidden keyboard stays dark whoever owns it |

A TAKE-over becomes visible a beat after it happens: the driver publishes a hand-BACK at once, but a takeover
only together with the host's first completed colour frame (`AHLA_REPORT_CONTROL`, because the staged colours
are still black at that point). So the flag, and everything gated on it, follows the host's first PAINT rather
than its declaration.

### What that flag can and cannot see — corrected 2026-09-18

This file used to describe the interference as symmetric ("a host owns the surface — Windows Dynamic Lighting /
a LampArray app"), which invited the reading that Windows Dynamic Lighting is one of several programs that can
grab the keyboard behind the app's back. **It is not, and the distinction decides what the gate above is worth.**

- **Windows Dynamic Lighting cannot bypass this driver.** Windows enumerates lighting devices *only* as HID
  LampArray collections and there is no user-mode API to register one, so the virtual device this app publishes
  is the only route Dynamic Lighting has to these lamps. It therefore arrives as a host **through** the bridge,
  and `HostOwnsLighting` sees it — which is exactly the case the gate is built for.
- **The interferer that can bypass it is OpenRGB.** `EneHidController` is the same controller OpenRGB drives
  (`VID 0x0CF2`, `PID 0x5130`, 11-byte `0xA4` feature reports — [lighting-an18-61.md](lighting-an18-61.md)),
  and **NitroSense drives the keyboard through OpenRGB under the hood** (it is an OpenRGB fork plus
  `AcerECKeyboardController.dll`, ibid.). A program writing those feature reports reaches the LEDs without
  touching the published device.
- **Nothing in the tree would notice that.** `HostOwnsLighting` is set from one channel only — the lamp frames
  the driver hands back (`LampArrayBridge.WorkerLoop` ← `ILampArrayTransport.WaitFrame`) — and no code path
  reads a colour back out of the EC; the only hardware read in the RGB model is `RgbZone.ReadBrightness`, which
  is the keyboard-brightness register. So an OpenRGB takeover moves no flag, greys nothing, and the app carries
  on painting over it.

**What it would take to observe it** is a read the hardware does not offer: the EC has no "who wrote last"
register, so noticing a foreign writer means either (a) reading back what the controller is currently showing
and comparing it with what we last wrote — a per-zone colour read the ENE protocol has never been shown to
expose, unlike the brightness register — or (b) an out-of-band signal from the other program, which does not
exist. Until one of those is measured, **the gate is honest about Dynamic Lighting hosts and blind to OpenRGB**,
and any UI text promising more than that would be theatre. (A second, narrower limit is on the flag itself: a
host that clears autonomous and paints nothing is not yet visible — see the table above.)

The Lighting panel's controls **are greyed out while a host owns the surface** — the owner's decision of
2026-09-18, taken on the mechanism the next paragraphs describe.

> **Superseded 2026-09-18 — kept as the record of what the defect was.** This section used to read:
>
> The Lighting panel's controls are **not** stopped while a host owns the surface — a decision the owner has not
> made yet, and NOT the rebuild-time constraint this line used to claim (see "what stopping them would take"
> below). A value changed there is overwritten by the host's next frame — **but only if that frame differs from
> the one before it**. The bridge's dedupe (`LampArrayBridge.Paint`/`Unchanged`, ±5 per channel) compares each
> incoming frame against `_written`, the mirror of what was last written, and a panel's apply goes straight to
> the zone without touching that mirror: so against a STATIC host frame — the common case, a solid colour — the
> host's re-sends look unchanged, are skipped, and the app's value stays on the keyboard. That window is a
> defect, recorded here rather than described as behaviour; it is the same mechanism that made the drawer's
> re-apply on open visible, and the open is now gated on ownership (`LightingViewModel.Reapply`). The status
> line is the signal that a host is in charge.
>
> That was accurate for as long as it stood: the panel writes straight to the zone, the mirror is never told,
> and the app's colour therefore stuck on a static host frame. What changed is the decision, not the mechanism —
> the mechanism is why the decision mattered.

**The open of the Lighting drawer is gated, and so are the controls in it.** Opening it re-applies the app's own
lighting *except* while a host owns the surface, when the panels write nothing at all — that path does not go
through `LightingCoordinator.Paint`, so it has its own check rather than inheriting that one
(`LightingViewModel.Reapply`). The controls yield through `LightingViewModel.ControlsEnabled`, an observable
flag the coordinator sets from ownership and seeds from the surface as the section is built.

**Why greying rather than dropping the edit.** The owner's ruling: a refusal the user can see beats a silent
one. A check that let the drag through and swallowed the write would leave the slider moving over a keyboard
that does not change — the failure mode this replaces, only quieter. An `IsEnabled`-bound flag needs no
rebuild-time state, contrary to what this file used to say about it: the flag is observable, so the ownership
change reaches the live view-models, and a section rebuilt under a host (a language change) seeds itself from
the surface's own flag as it is constructed. The single interception point is still where the record below says
it is; it is simply not the shape that was taken.

> **Superseded 2026-09-18 — kept as the record of what stopping the controls would have taken.** This section
> used to read:
>
> **What stopping the controls would take, and why it is not done.** Stopping a write needs no rebuild-time
> flag, contrary to what this file used to say: every interactive edit goes through ONE choke point — the
> `applyAll` / `applyZone` delegates `LightingViewModel.BuildPanel` hands each `LightViewModel`, which ALSO
> carry the re-apply a panel runs as it is built (the path the follows-flip reaches when it builds one). One
> ownership check there, or one wrapper around those two lambdas, would stop all of those writes at once. It is
> left alone because gating a write DROPS the user's edit silently — the slider moves and the keyboard does not
> — and the alternative, greying the controls out, is the shape that would need state built with the flip (a
> rebuild-time flag, or a rebuild on the ownership change). That is a product decision, so it is recorded for
> the owner in `docs/domain-refactoring-plan.md` §7 rather than taken here.
>
> The choke-point analysis held. The two claims that did not: greying needs no rebuild-time state (above), and
> the choice between greying and dropping was the owner's to make, and was made.

**What is NOT gated, and the one write that still slips through under a host.** The plain
`IKeyboardBrightness` slider is deliberately outside the flag, for the reason `LightingViewModel.Reapply`
records: it is separate hardware that no LampArray carries. And a panel's
CONSTRUCTION re-apply (`LightViewModel`'s `if (state.Configured) ApplyNow()`) still runs while a host owns the
surface — that is the startup / language-rebuild write, not a user edit, and it is unchanged here.

## Limitations

- **4 zones are 4 zones.** A per-key "wave" from Dynamic Lighting arrives as 4 columns. Per-key would need the
  controller's `Direct` mode (`0xFF` in the mode table) which has never been verified on AN18-61 — that is a
  separate piece of reverse engineering.
- Only *arbitrary-colour* zones can be painted: the bridge picks each zone's `HasColor && !HasSpeed` effect
  (STATIC on Acer). Zones that only offer self-cycling effects are skipped.
- The user's brightness slider does not apply while a host owns the surface — host frames are absolute colours,
  with brightness already folded in.
- Windows 11 22H2+ only (that is where the Dynamic Lighting consumer lives); the INF states the same floor.

## Status

The C# half (layout, bridge, transport, wiring) compiles for both target frameworks, warning-clean, including
under the AOT/trim analysers.

The driver **compiles and links**, but has not been loaded yet. It is cross-built in a Linux container with
clang-cl/lld-link against the WDK/SDK NuGet packages — no Visual Studio or WDK install needed
([../driver/README.md](../driver/README.md)) — and the result checks out as a kernel driver image: `pei-x86-64`,
entry point `FxDriverEntry`, sections `.text` / `INIT` (discardable) / `PAGE`, imports `WDFLDR.SYS` and
`ntoskrnl.exe`, linked against KMDF 1.33 so it loads on the INF's 22621 floor. VHF resolves through the static
`vhfkm.lib` (which talks to `vhf.sys` down the device stack — hence no `vhf.sys` import).

So the C is proven to compile against the real WDK headers, and the API usage type-checks against them; what is
still unproven is **runtime**. Loading it needs either test signing (Secure Boot off) or an attestation-signed
package. Treat it as reviewed-and-built but unproven until it has been through:

1. `pnputil /add-driver` + enable the toggle → device node appears, no code 28/52.
2. Settings → Dynamic Lighting lists the device with the right lamp count.
3. A solid colour there lights all 4 zones; an "Ambient"/wave effect sweeps left-to-right.
4. Switch performance profile while a host effect is running → the amber flash is corrected within ~400 ms
   (`ReapplyTicks`), not left standing.
5. Sleep/resume, and lid-close in clamshell mode → dark while hidden, host colours back on open.
6. Turn Dynamic Lighting off in Windows → the app's own per-mode lighting comes back by itself.
7. Kill the app → the device disappears from Dynamic Lighting.

## Linux

Nothing to consume LampArray exists in the Linux desktop stack, so `LampArrayHost.Create()` returns null there
and the feature is absent. It is, however, the *cheaper* side to build if that changes: `/dev/uhid` lets a
plain user-space process create a HID device with an arbitrary report descriptor and answer GET/SET_REPORT
itself — same interface (`ILampArrayTransport`), no kernel module, no code signing. The kernel's own virtual
LampArray for TUXEDO NB04 laptops is the same idea done in-kernel.

## Implementation notes

Details that are load-bearing but do not fit the narrative above. Numbers here are the ones on the wire.

### Spec provenance and units

Field and report semantics follow **"Lighting And Illumination Page (0x59)" of HID Usage Tables 1.4** and
Microsoft's **reference implementation** (`github.com/microsoft/ArduinoHidForWindows`, MIT). The units **on the
wire** are **micrometres and microseconds** — hence the µm/µs in the C# model; the conversion from mm/ms happens
**once**, where the layout is built, so neither the driver nor the bridge has to think about it.

### Hand-written structs, kept in sync by hand

`protocol` is kept **byte-for-byte in sync with `driver/AcerHelperLampArray/public.h` — if you change one, change
both.** The C# side writes the same fixed-size structs **by hand** (`BinaryPrimitives`), so there is **no
struct-layout marshalling to get wrong**; field order and sizes are load-bearing and asserted with `C_ASSERT` on
the C side.

```
device path   \\.\AcerHelperLampArray
service id    AcerHelperLampArray          hardware id  AcerHelperLampArray
MaxLamps 64   LampSize 28   LayoutSize 1820 (= 28 + 64*28)   FrameSize 268 (= 12 + 64*4)
DeviceType 0xB007 (vendor range)   LayoutVersion 1
IOCTL = (DeviceType << 16) | (access << 14) | (function << 2)
  SET_LAYOUT  Ctl(0x800, write)   WAIT_FRAME  Ctl(0x801, read)   STOP  Ctl(0x802, write)
```

Frame layout: `seq` (u32), `autonomous` (u32), `count` (u32), then `count` × RGBA bytes. Lamp count is clamped
to the layout's `_lampCount`, so a host frame can never index past the published table.

### Geometry actually published

- Keyboard: a nominal **330 × 110 mm** rectangle, sub-zones spread evenly left-to-right (**zone-mask bit 0 =
  leftmost**), each lamp at **half the height** of the band.
- Any further zone: a **12 mm** strip **18 mm** below the keyboard band. The separate Y band (rather than folding
  the strip into the keyboard rectangle) is what keeps a vertical wipe reaching it **last**, like the real
  hardware.
- Lamp **purposes**: keyboard lamps get `Illumination | Accent`; strip lamps get `Accent` only. Advisory, but
  honest values cost nothing.
- Every lamp reports the **same latency: 30 ms** (`LampLatencyMs`). That is honest because the ENE write path is a
  **queued, paced** feature report (`PacingMs = 10` on the worker, see
  [lighting-an18-61.md](lighting-an18-61.md)) — so ~30 ms from *"host wrote a frame"* to *"LEDs changed"* is a
  realistic figure, not a placeholder.

Nothing reports the keyboard's real dimensions (not SMBIOS, not the WMI, not `acer-models.json`), so the geometry
is nominal. What matters to the host is the **proportions and order**, which decide which way a wave sweeps — not
absolute accuracy.

### `DriverInstalled` — a driver-store check, not a device probe

The whole feature (and its Options row) is **hidden until the driver package is staged** on this machine: the
driver ships **separately from the app** because it needs a signature Windows will load **without test-signing**,
so most installs won't have it.

The check looks for the package in the **driver store** — that is what `pnputil /add-driver` creates, and it is
**true before any device node exists**. That ordering matters: the app creates the node itself, so there is **no
device to interrogate yet**. The `System32\drivers` copy is accepted too, for a package built with `DIRID 12`.

### Device-node creation (and why it is not a permanent node)

The node is created **root-parented** by the app (`SwDeviceCreate`, enumerator `AcerHelper`, parent
`HTREE\ROOT\0`, instance id `AcerHelperLampArray`, description *"Acer Helper keyboard lighting"*, hardware ids as
a **double-NUL-terminated MULTI_SZ**). Capability flags:

| flag | reason |
|---|---|
| `Removable` | so PnP is happy to see it come and go |
| `SilentInstall` + `NoDisplayInUI` | keep it out of the user's face — it is plumbing, not a device they plugged in |
| `DriverRequired` | tells PnP to actually match our INF instead of leaving a raw devnode |

Two waits are needed because nothing here is synchronous:

1. `SwDeviceCreate` **reports its outcome through a callback, not its return value** — the result is polled
   briefly (sentinel `int.MinValue` = callback not seen yet), and a negative result closes the handle.
2. After that, **PnP still has to start the device and the driver has to create its symbolic link** — both happen
   *after* the callback. So the transport **polls for the device for up to 25 × 200 ms** rather than assuming.

If the node appears but the driver never starts, the node is **destroyed again** and the error says so
(*"device node created but the driver did not start"*). A permanently installed node (`devgen`/`devcon`) was
rejected: it would leave a **dead lighting device** listed in Dynamic Lighting whenever the app isn't running.

### `Stop()` ordering

`CancelIoEx` is **not itself an I/O request**, so it does **not** queue behind the pending `WAIT_FRAME` on the
frame handle — it **unblocks** it. Only then is `STOP` safe to send. A `WAIT_FRAME` that fails with
`ERROR_OPERATION_ABORTED` (**995**) is our own `Stop()` cancelling the wait, **not** a failure, and must not be
reported as one.

Closing the handles destroys the device node (the driver tears the device down when the **last** one closes), so
an app crash cannot leave a zombie entry in Dynamic Lighting.

### `LampArrayLayout.Build` — the layout rule

The first **included** zone is treated as the keyboard and gets the full keyboard rectangle, its sub-zones spread
evenly left-to-right; every further zone becomes a strip below it. That rule is **vendor-neutral** (no zone-name
matching) and matches the physical reality of these laptops, where the multi-zone surface is the keyboard and
anything extra is a front/rear lightbar. `include` filters out zones the app must not drive — notably the Acer
lightbar while it "follows the performance profile", because the firmware owns it then.
