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
LampArrayTransport       ── Vendors/Generic/*.Windows.cs  device node (SwDeviceCreate) + IOCTL channel
        ▼
LampArrayBridge          ── Features/LampArrayBridge.cs   rate limit, write collapsing, ownership arbitration
        ▼
RgbZone.ApplySubZone /   ── Features/Rgb.cs               the existing zone model
RgbZone.ApplyEffect
        ▼
EneHidController         ── Vendors/Acer/                 A4 feature reports over HID-over-I2C
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
| host takes the surface | `LampArrayBridge.HostOwnsLighting` = true; `LightingCoordinator.Paint` stops painting the app's lighting; status line says so (G HUB likewise blocks its lighting UI while Dynamic Lighting is on) |
| host frame arrives | translated to zone writes, throttled + collapsed as above |
| profile switch / resume / lid-open | the host's last frame is re-asserted, ignoring the dedupe |
| host releases, or the feature is switched off | the app repaints the current mode's lighting immediately, then runs its usual re-apply burst |
| lid shut in clamshell mode | blanking still wins — a hidden keyboard stays dark whoever owns it |

The Lighting panel's controls are **not** disabled while a host owns the surface (they would need a
rebuild-time flag); a value changed there is simply overwritten within ~100 ms. The status line is the signal.

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
