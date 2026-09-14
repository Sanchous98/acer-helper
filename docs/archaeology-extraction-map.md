# Archaeology extraction map (Волна 4)

Mechanical worklist for the edit step: for every comment block whose narrative now lives in `docs/`, the
**exact text to leave in the code**. The invariant the block used to carry must survive in the code — a reader
who never opens the document still has to know the rule they must not break — plus a link to where the
reasoning, the measurements and the dead ends now live.

**Line numbers here are not maintained (noted 2026-09-14).** They were written against the revision current when
each entry was written and have already drifted; treat the symbol names as the anchor and re-locate by name.
The tree has also been reorganised since: `LaptopService` is now six `LaptopService*.cs` partial files, and
`OptionsAssembler` has moved from `UI/` to the repo root (`namespace AcerHelper.Application`).

## How to use this

- Entries are grouped **by file**, and files are alphabetical, so a `.cs` file is opened once.
- Line ranges were taken from the working tree on **2026-09-14** (branch `refactor/layering`). They will drift;
  each entry quotes enough of the original first line in its heading context that a re-anchor is trivial.
- `file:start-end` refers to the **comment block only** — never to the code under it.
- **The replacement is literal pasteable text.** Where the original is a `///` block, the replacement is written
  in the same XML-doc style; where it is a `//` block, the replacement is `//` lines. Indentation matches the
  original block.
- Where a replacement **drops** a claim, the entry says so — those are the recorded contradictions, and the
  dropped sentence must not be carried over.
- Blocks the narrative came from but that are **not** in this map are listed under *Deliberately left alone* and
  *Candidates for deletion* at the end.

**Documents the map points at**

| doc | subject |
|---|---|
| `docs/power-an18-61.md` | EC HID power envelope (Acer) — AN18-61 measurements |
| `docs/lighting-an18-61.md` | ENE RGB / lighting — AN18-61 measurements |
| `docs/lamparray.md` | HID LampArray / Windows Dynamic Lighting design |
| `docs/curve-optimizer-strix-point.md` | **new** — AMD Curve Optimizer, SMU mailboxes, encoding |
| `docs/pawnio.md` | **new** — the PawnIO ring-0 gateway and its licence rules |
| `docs/wmi-interop.md` | **new** — hand-rolled WMI COM interop and `WmiSession.Gate` |
| `docs/nvidia-gpu-oc.md` | **new** — NvAPI clock offsets, caps, volatility |
| `docs/dell-firmware.md` | **new** — Dell BIOS attributes (Windows WMI + Linux kernel ABI) |

**Totals: 37 Part-A blocks (≥8 comment lines) + 32 Part-B blocks (4–7 lines carrying measured facts) = 69 entries.**

---

# Part A — blocks of 8 or more comment lines

## `Features/LampArray.cs`

### `3-22` → `docs/lamparray.md` — *Why a driver is unavoidable*, *Layers*, *Wire format*

```csharp
// ---------------------------------------------------------------------------------------------------------
// HID LampArray — the vendor- and OS-neutral half of the "translation layer" that lets Windows Dynamic
// Lighting (Settings > Personalisation > Dynamic Lighting) and any LampArray-aware app paint this laptop's
// keyboard. The whole picture is in docs/lamparray.md; the short version:
//
//   Windows / an app  --HID feature reports-->  virtual HID LampArray device (driver/, VHF-based)
//                     --ILampArrayTransport-->  LampArrayBridge  -->  RgbZone.ApplySubZone / ApplyEffect
//
// Windows only enumerates lighting devices that expose the HID LampArray usage page (0x59) — there is NO
// user-mode "register a LampArray" API — so the device itself has to be a small kernel driver. Everything
// ABOVE it lives in C# so the semantics, geometry and rate limiting iterate without re-signing a driver.
//
// Units ON THE WIRE are micrometres and microseconds; the conversion from mm/ms happens once, here.
// ---------------------------------------------------------------------------------------------------------
```

### `148-155` → `docs/lamparray.md` — *Implementation notes → `LampArrayLayout.Build`*

```csharp
/// <summary>Describe the device's controllable surface as a lamp array. <paramref name="include"/> filters
/// out zones the app must not drive (the Acer lightbar while it "follows the performance profile" — the
/// firmware owns it then). Returns null when nothing is left to expose.
///
/// Layout rule: the FIRST included zone is treated as the keyboard and gets the full keyboard rectangle, its
/// sub-zones spread evenly left-to-right; every further zone becomes a strip below it. That is vendor-neutral
/// (no zone-name matching) and matches these laptops physically. See docs/lamparray.md.</summary>
```

## `Features/LampArrayBridge.cs`

### `3-32` → `docs/lamparray.md` — *What makes this more than a memcpy*, *Ownership*

The `Threading:` paragraph (**28-32**) is architecture, not archaeology, and is **kept** in the replacement.

```csharp
/// <summary>
/// The translation layer proper: takes lamp frames a host (Windows Dynamic Lighting, or any LampArray-aware
/// app) writes to our virtual HID device and turns them into this device's zone writes — and arbitrates
/// ownership of the backlight while it does, so the app and the OS don't fight over it.
///
/// Three problems make this more than a memcpy, all properties of the hardware (docs/lamparray.md):
///  1. RATE. A host paints at 30–60 Hz; the ENE controller is on HID-over-I2C and a full keyboard apply is
///     several feature reports (hence EneHidController's pacing and coalescing). Frames are rate-limited HERE
///     to the interval the layout ADVERTISES as MinUpdateInterval, and are last-one-wins, so slowing down
///     drops intermediate frames rather than building a backlog.
///  2. WRITE COUNT. A uniform colour across a zone is ONE all-zones report instead of one per sub-zone;
///     unchanged sub-zones (±ColorEpsilon per channel) are skipped outright.
///  3. OWNERSHIP. While the host drives the surface the app must stop painting it, or every profile switch,
///     resume and re-apply tick would stomp the host's frame. <see cref="HostOwnsLighting"/> is that gate;
///     <see cref="Reassert"/> is its counterpart, repainting the host's last frame after the EC clobbers the
///     surface on a profile switch or across sleep.
///
/// Threading: one long-lived worker thread pumps the transport (a blocking wait) and applies frames; the zone
/// writes it makes are themselves non-blocking (EneHidController queues them onto its own writer). Public
/// members are safe to call from any thread. <see cref="OwnerChanged"/> fires on the worker thread — marshal
/// it if you touch UI state.
/// </summary>
```

## `Features/Ports.cs`

### `160-169` → `docs/curve-optimizer-strix-point.md` — *Port contract*

```csharp
/// <summary>CPU undervolt via AMD's Curve Optimizer: a signed offset in AVFS "counts" applied to the whole
/// voltage/frequency curve (negative = less voltage at every frequency, 0 = stock). The CPU-side twin of
/// <see cref="IGpuOverclock"/> — same shape, same volatility, same re-apply duty: the offset lives in SMU
/// state, so a power cycle restores stock and the app is the source of truth per performance mode.
///
/// Present only on a CPU whose SMU mailbox layout is known AND where the required ring-0 gateway is installed —
/// null otherwise, so the UI hides the section. Two properties are unusual and deliberate: a successful
/// <see cref="Set"/> means the SMU <i>accepted</i> the message, not that the curve provably moved, and a
/// too-aggressive offset fails hours later at idle rather than under load — so callers must treat it as
/// opt-in, warn, and default to stock. See docs/curve-optimizer-strix-point.md.</summary>
```

> **Note on the dropped clause.** The original says the hardware "offers no trustworthy read-back". That is
> true of the **CPU clusters** and false of the **iGPU**, which has a getter and is verified on every write
> (`0x20`). The replacement does not repeat the over-generalisation.

### `201-213` → `docs/curve-optimizer-strix-point.md` — *The iGPU as a third domain*, *Port contract*

```csharp
/// <summary>One independently tunable voltage domain of the processor package — on a hybrid part a core
/// cluster, and on an APU also the integrated GPU, which is a separate rail on the same SMU.
/// <see cref="Label"/> is for display (e.g. "Zen 5c", "iGPU") and is an architecture name or a technical
/// abbreviation, so it is not translated. <see cref="Key"/> is the stable identity used as the settings key:
/// it names the hardware domain rather than a position in a list, so a preset survives a change in how
/// domains are ordered or labelled.
///
/// <see cref="Range"/> overrides the port-wide range for this domain alone; null means "use the port's".
/// <see cref="MillivoltsPerCount"/> is different — null there means <i>unknown for this domain, show no
/// estimate</i>, NOT "inherit". Volts-per-count is a property of one particular rail and has to be measured
/// on it (on this hardware the graphics rail moves 5 mV/count against the cores' 2.5), so a domain that
/// borrowed a neighbour's figure would misreport every offset by a factor of two.
/// See docs/curve-optimizer-strix-point.md.</summary>
```

## `LidWatcher.Windows.cs`

### `5-14` → `docs/lighting-an18-61.md` — *Why the lid watcher creates a real (hidden) window*

```csharp
// Windows lid-state hook: RegisterPowerSettingNotification(GUID_LIDSWITCH_STATE_CHANGE) delivers a
// WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE to the registered window whenever the lid opens or closes — and
// it fires even when the lid-close power action is "do nothing" (clamshell keep-awake), which is exactly the
// case we care about. The change is delivered TARGETED to the registered HWND, but Windows only documents
// WM_POWERBROADCAST delivery to TOP-LEVEL windows, so this defensively creates an ordinary top-level window
// that is never shown (a strict superset of a message-only window; WS_EX_TOOLWINDOW keeps it out of the
// taskbar / Alt-Tab). Created on the UI thread so Avalonia's Win32 message loop dispatches its messages.
// See docs/lighting-an18-61.md.
```

## `UI/LightingCoordinator.cs`

### `38-45` → `docs/lighting-an18-61.md` — *The re-apply regime: ticks, the double blink, and the self-heal*

```csharp
// Of those ticks, how many also RE-SEND the profile palette flash. The flash is a global write that briefly
// repaints the whole keyboard with the palette colour before the per-zone paint overrides it — one more
// visible blink of keyboard and lightbar. Worth that cost on the RESTORE paths (startup, resume, lid open,
// host hand-back), where nothing else re-establishes the palette and the bus may be contended, so those
// kicks ask for it. A profile SWITCH does not: the firmware flashes the new palette itself at the moment of
// the write and we now send ours in the same instant (see OnProfileApplied), so a re-send 400 ms later is
// simply a second blink cycle. The per-zone KEYBOARD paint (the actual "half green/half orange" self-heal)
// still runs on EVERY tick, which is silent when already correct. See docs/lighting-an18-61.md.
```

### `142-149` → `docs/lighting-an18-61.md` — *Why applying on the switch instant matters (the double blink)*

```csharp
/// <summary>A profile was just applied BY US (user pick, tray, hotkey, Turbo switch) — the caller passes the
/// profile that actually landed, so nothing has to be read back out of the hardware. Repaint NOW, in the same
/// instant as the firmware's own palette flash, so the two coincide into one.
///
/// This used to wait for the refresh pass to DISCOVER the change by polling, which is what produced the double
/// blink: the firmware flashes the new palette the instant the profile byte is written, and our own palette
/// write then landed ~750 ms later as a second, separate flash cycle. The burst we kick here deliberately
/// carries NO further palette re-sends, only the per-zone self-heal. See docs/lighting-an18-61.md.</summary>
```

### `249-256` → `docs/curve-optimizer-strix-point.md` — *Volatility and re-apply*; `docs/nvidia-gpu-oc.md` — *Volatility*

```csharp
// GPU clock offsets and the CPU curve-optimizer offset are both VOLATILE hardware state: the dGPU
// power-cycles across suspend (Optimus D3-cold) and comes back at 0 offset, and the Curve Optimizer offset
// lives in SMU state the platform restores to stock across a power transition — so re-assert the current
// mode's values, together with the CPU power mode. Off the UI thread (ApplyModeCpuPower reads the EC, which
// can stall right after wake, and we must not block the UI). No UI reflect needed (values unchanged); no-op
// when those ports are absent. Guarded because the device can be tearing down (an exit racing the wake), and
// an escaping throw here would be an unobserved task exception.
// See docs/nvidia-gpu-oc.md and docs/curve-optimizer-strix-point.md.
```

## `UI/ViewModels/CoViewModel.cs`

### `9-22` → `docs/curve-optimizer-strix-point.md` — *UI (`UI/ViewModels/CoViewModel.cs`)*

```csharp
/// <summary>CPU-undervolt section: one Curve-Optimizer offset slider per independently tunable voltage domain
/// (on a hybrid part, "Zen 5" and "Zen 5c" — separate rails measured near 1.17 V and 1.02 V, so one number for
/// both is pinned by whichever gives out first — and on an APU also the iGPU), or a single all-core slider on
/// a CPU with one domain. Per-CORE is deliberately not offered: within a rail the delivered voltage follows
/// the mildest core's request, so all but one core per cluster would be inert. Applies on change (debounced)
/// and persists PER performance mode — switching mode reloads that mode's offsets (see
/// <see cref="Load"/>), and an unconfigured mode is stock. Only built when the device exposes an
/// <see cref="Features.ICurveOptimizer"/> port.
///
/// Shaped like <see cref="GpuViewModel"/>, with one difference that matters: applying is slow here (an SMU
/// mailbox transaction per core slot, waiting on a machine-wide lock), so the <c>apply</c> delegate this
/// receives is expected to hand the work off a thread itself — see AppController.SetCo. Nothing in this class
/// may block. All rows are applied together on one debounce tick rather than per row, so dragging one slider
/// does not re-write the others' cores one transaction at a time.
/// See docs/curve-optimizer-strix-point.md.</summary>
```

### `124-133` → `docs/curve-optimizer-strix-point.md` — *UI (`UI/ViewModels/CoViewModel.cs`)*

```csharp
// AVFS step count first — it is what the hardware takes and what every tool and write-up talks in — with the
// millivolts it works out to in brackets, since that is the unit an undervolt is actually thought about in.
// The mV figure carries "≈" on purpose: a count is only approximately a fixed voltage, because the offset
// shifts the whole V/F curve rather than clamping a voltage, so the delivered delta moves with frequency and
// temperature. Stock reads as a plain 0.
//
// Each rail brings its OWN scale — 2.5 mV a count on the cores, 5 on the graphics rail — so the arithmetic is
// per row rather than per section. A rail that has never been measured passes null and shows the bare count:
// steps are the hardware's own unit and remain usable, which beats printing a millivolt figure borrowed from a
// different rail's measurement, since that would look authoritative and be wrong.
// See docs/curve-optimizer-strix-point.md.
```

## `Vendors/Acer/AcerEcHidController.Linux.cs`

### `7-16` → `docs/power-an18-61.md` — *How the app drives it (`AcerEcHidController`)*

```csharp
// Linux transport for the Acer EC controller: hidraw directly, no HID library — the same approach and the same
// reason as EneHidController.Linux.cs (this controller sits on HID-over-I2C, which HidSharp's Linux enumeration
// never lists). One hidraw node covers all of a device's collections, so matching the parent hid device's
// HID_ID (bus:vendor:product) is enough; the report id in byte 0 selects the vendor collection's report.
// Reaching /dev/hidrawN without root relies on the desktop's uaccess ACL (present for built-in HID) or a udev
// rule — the same prerequisite the RGB controller already documents.
//
// NOTE: untested on Linux hardware. A missing or unwritable node degrades to Available = false and the profile
// path keeps its previous behaviour, so a wrong guess means "no EC envelope control", never a bad write.
// See docs/power-an18-61.md.
```

## `Vendors/Acer/AcerEcHidController.cs`

### `5-43` → `docs/power-an18-61.md` — *Device and wire format*, *Mode byte → measured dGPU power*, *It is a latch, not a daemon*, *How the app drives it*

```csharp
// Cross-platform Acer EC HID controller: the channel that actually carries the performance envelope on recent
// Nitro/Predator models (device VID 0x1025 / PID 0x174B, vendor collection on usage page 0xFF05, 65-byte
// feature reports). The packets are identical on every OS, so this file is the codec and the per-OS partials
// supply the transport hooks.
//
// WHY THIS EXISTS. On the Nitro AN18-61 the gaming-WMI profile byte (SetGamingMiscSetting index 0x0B) is only
// an *indicator*: writing it moves the tray state and the lightbar palette but does not touch the power
// envelope. Measured live — NitroSense switching Quiet<->Turbo moved the dGPU's enforced limit 71 W <-> 108 W
// while EVERY gaming-WMI value stayed frozen. So the envelope (GPU TGP/CTGP plus the CPU limits) lives in the
// EC's own "system usage mode", reachable only over this HID interface.
//
// WIRE FORMAT: A0 00 A0 <featureId:LE16> <cmdId> <params…>, zero-padded to 65. A GetFeature of report 0xA0
// answers with byte[2] = 0xE0 when the EC accepted the FRAME — but that is frame-level only: an out-of-range
// mode is acknowledged the same way and then silently ignored, so this controller is WRITE-ONLY.
//
// The EC LATCHES the mode: it survives this app exiting and needs no resident daemon. It does NOT necessarily
// survive a reboot, which is why LaptopService re-asserts the profile at startup (EC-only — a full profile
// switch there re-flashes the lightbar and cascades at every boot).
//
// Writes go through a background writer thread, never the caller's (UI) thread: WriteFeature is a synchronous
// no-timeout HID write on the same HID-over-I2C bus as the RGB controller, and a contended bus can block it
// for a long time. Only the newest mode matters, so the queue is a single coalescing slot.
//
// Mode byte -> steady dGPU limit (0 = 108 W, 1 = 93 W, 2 = 79 W, 3 = 71 W, 4 = 71 W, 5+ acknowledged then
// ignored), the wire format, the measurement methodology and every dead end: see docs/power-an18-61.md.
```

## `Vendors/Acer/EneHidController.cs`

### `5-13` → `docs/lighting-an18-61.md` — *Device*, *How the app drives the ENE controller*

```csharp
// Cross-platform RGB controller: the ENE HID device (VID 0x0CF2 / PID 0x5130, 11-byte feature report id
// 0xA4) — the same controller OpenRGB drives. The packets are identical on every OS; only the transport
// differs, so this file is the Acer packet codec + zone model, and the per-OS partials supply the three
// transport hooks (OpenTransport/SetFeature/Dispose): Windows uses HidSharp, Linux talks to hidraw directly —
// HidSharp's Linux enumeration only sees USB HID, and on several models (e.g. Nitro AN18-61) this controller
// hangs off HID-over-I2C. It exposes its physical regions as RgbZone bricks: "Keyboard" (multi sub-zone) and,
// on models that have it, "Lightbar". Keyboard brightness read-back isn't on this HID interface — it's the
// gaming WMI's job (Windows only) — so that reader is injected by AcerDevice (null on Linux).
// See docs/lighting-an18-61.md.
```

### `116-123` → `docs/lighting-an18-61.md` — *Serialised background writer, and the 10 ms pacing*

```csharp
// ---- serialized background writer ----
// Every feature report is enqueued here and written by ONE long-lived worker thread — NOT the caller's
// (UI) thread. WriteFeature (the per-OS transport) is a synchronous, no-timeout HID write that can block
// hard on a contended HID-over-I2C bus; off the UI thread, that stall freezes only the worker, so the app
// stays responsive. Fire-and-forget: SetFeature only enqueues. Writes coalesce by region (see SameRegion)
// so a stalled worker can't accumulate — and won't replay — a flood of stale writes: when the bus frees it
// applies just the latest state per region. A single in-flight write is also the de-facto circuit breaker,
// keeping the app's bus contention minimal. See docs/lighting-an18-61.md.
```

### `130-137` → `docs/lighting-an18-61.md` — *Serialised background writer, and the 10 ms pacing*

```csharp
// Inter-write pacing: a small gap between consecutive feature reports on the worker (NOT the UI thread —
// this only ever stalls the writer). A full keyboard apply is several back-to-back reports; on a HID-over-I2C
// bus that an externally-booted display is contending, a tight burst tends to land some reports corrupted
// (amber fallback) and others clean ("half green/half orange"). Spacing the reports decorrelates them so they
// don't all fall inside one contention window — it can't phase-lock to the display's traffic, only randomise
// phase, so it *reduces* the odds of a fully corrupt apply rather than guaranteeing a clean one. 5 reports ×
// this delay stays well under the ~120 ms apply debounce. 0 disables. See docs/lighting-an18-61.md.
```

## `Vendors/Dell/DellBiosWmi.Windows.cs`

### `6-15` → `docs/dell-firmware.md` — *Windows: `root\dcim\sysman\biosattributes`*

```csharp
// Windows transport for Dell's AGENTLESS BIOS-attribute interface: the ACPI-WMI classes Dell firmware
// itself publishes in root\dcim\sysman\biosattributes on 2018+ business models (Latitude/Precision/XPS/
// OptiPlex) — present on stock Windows, no Dell Command software needed. (Do not confuse with the DCIM_*
// classes in root\dcim\sysman, which only exist after installing Dell Command | Monitor.)
// Reads = query the *Attribute classes (one instance per BIOS setting); writes = the
// BIOSAttributeInterface.SetAttribute method, whose Status output is 0 on success (1 Failed, 2 Invalid
// Parameter, 3 Access Denied — e.g. a BIOS admin password is set, which this transport does not supply).
// A thin accessor: the attribute names/values live in DellDevice.Windows.cs.
// NOTE: exercised only through the shared WmiSession COM layer proven on Acer — not yet verified on
// Dell-Windows hardware. See docs/dell-firmware.md.
```

## `Vendors/Dell/DellDevice.cs`

### `5-14` → `docs/dell-firmware.md` — whole document

```csharp
/// <summary>
/// Dell laptop backend. Extends <see cref="GenericDevice"/> with what Dell firmware exposes beyond the
/// generic OS surface — battery charge modes (Adaptive / Express charge / Primarily AC / Standard / Custom),
/// USB PowerShare, Fn-lock, keyboard-backlight timeout and the full 4-mode thermal set (Optimized / Cool /
/// Quiet / UltraPerformance). Both OSes drive the SAME firmware knobs through different bindings:
/// Linux = the Dell kernel drivers' sysfs (dell-laptop/dell-wmi-ddv power_supply extension, dell-pc
/// platform_profile, dell-wmi-sysman firmware-attributes); Windows = Dell's agentless BIOS-attribute
/// ACPI-WMI (root\dcim\sysman\biosattributes, stock firmware on 2018+ business models — no Dell software
/// needed). Per-OS wiring lives in DellDevice.{Linux,Windows}.cs; unsupported surfaces simply stay generic.
/// See docs/dell-firmware.md.
/// </summary>
```

## `Vendors/Generic/FirmwareAttributes.Linux.cs`

### `6-16` → `docs/dell-firmware.md` — *Linux: `/sys/class/firmware-attributes/<device>/`*

```csharp
/// <summary>Generic Linux accessor for the kernel's firmware-attributes class
/// (<c>/sys/class/firmware-attributes/&lt;device&gt;/</c>) — BIOS settings exposed as sysfs attributes with
/// <c>current_value</c>/<c>possible_values</c>/<c>type</c> files, plus an <c>authentication/</c> sub-tree.
/// Vendor-neutral: the same ABI serves Dell (dell-wmi-sysman), Lenovo (think-lmi) and HP (hp-bioscfg); the
/// vendor device supplies the device name and the attribute names it understands.
///
/// Access model (important): attribute METADATA is world-readable, but <c>current_value</c> is typically
/// root-only for BOTH read and write, so a feature is only offered when its current value is readable
/// (<see cref="CanRead"/>). Writes are additionally gated by the firmware: when a BIOS admin password is
/// configured (<see cref="RequiresPassword"/>), the kernel rejects attribute writes unless the password is
/// supplied first — which this app doesn't do — so callers should not offer those controls.
/// See docs/dell-firmware.md.</summary>
```

## `Vendors/Generic/GenericDevice.Windows.cs`

### `38-46` → `docs/power-an18-61.md` — *The CPU-power axis on this machine: the OS overlay, nothing else*

```csharp
// CPU power management via the Windows power-mode overlay — the one CPU-power knob that needs no driver at all
// (ring-0 PPT is out of reach; the ring-0 undervolt axis is the separate CurveOptimizer port wired in
// InitPlatform, and Acer exposes no WMI power path — it bakes the whole envelope into its fixed EC profiles).
// Wired here (after the vendor backend has finalized the profile port) rather than in InitPlatform, and ONLY
// when the performance profiles are NOT themselves the Windows overlay: OverlayCpuPower and
// OverlayPowerProfiles drive the SAME overlay with the SAME GUIDs, so if the profile picker already IS the
// overlay (generic laptop, or a vendor whose WMI/BIOS profile path was unavailable) a CPU-power control would
// fight it — and, since the per-mode key is then the overlay GUID, corrupt the per-profile store. So it's an
// independent axis only when a vendor WMI/EC profile port took over. Null (section hidden) otherwise, and on
// an OS without the overlay API. See docs/power-an18-61.md.
```

## `Vendors/Generic/LampArrayTransport.Linux.cs`

### `5-14` → `docs/lamparray.md` — *Linux*

```csharp
/// <summary>Composition helper: the LampArray transport for this OS, or null where there is none.
///
/// Linux has no equivalent consumer of HID LampArray in the desktop stack (Dynamic Lighting is a Windows
/// feature), so this returns null and the whole bridge stays absent — the Options row for it never appears.
///
/// It is, however, the cheap side to implement if that changes: <c>/dev/uhid</c> lets a plain user-space
/// process create a HID device with an arbitrary report descriptor and answer GET/SET_REPORT itself, so the
/// same <see cref="ILampArrayTransport"/> could be satisfied here with no kernel module and no code signing at
/// all. See docs/lamparray.md.</summary>
```

## `Vendors/Generic/LampArrayTransport.Windows.cs`

### `8-30` → `docs/lamparray.md` — *Why a driver is unavoidable*, *App ↔ driver IOCTLs*, *Implementation notes*

```csharp
/// <summary>
/// Windows transport for the LampArray bridge: the channel to <c>AcerHelperLampArray.sys</c> (driver/), the
/// VHF-based virtual HID device that makes this laptop's backlight visible to Windows Dynamic Lighting.
///
/// Why a driver at all: Windows enumerates lighting devices ONLY as HID LampArray collections — there is no
/// user-mode API to register one — so a HID source driver has to exist. It is deliberately dumb: it owns the
/// static HID report descriptor, answers the host's attribute GETs from a layout blob we push down, and hands
/// back completed lamp frames. All the semantics (geometry, rate limiting, ownership) stay in C#, so the
/// signed binary almost never has to change.
///
/// Protocol (kept byte-for-byte in sync with driver/AcerHelperLampArray/public.h — if you change one, change
/// both): three IOCTLs, plain buffered, fixed-size structs written here by hand so there is no struct-layout
/// marshalling to get wrong.
///
///   SET_LAYOUT  app -> driver   the lamp table; also what makes the virtual device appear
///   WAIT_FRAME  driver -> app   blocks until the host completes a frame; last-one-wins (never a backlog)
///   STOP        app -> driver   un-publish the device
///
/// Two handles, on purpose: a synchronous file object serialises its requests, so a STOP issued while
/// WAIT_FRAME is pending would queue BEHIND it and deadlock until the host happened to send a frame. Control
/// and frame traffic therefore use separate handles (the driver tears the device down when the last one
/// closes, so an app crash can't leave a zombie entry in Dynamic Lighting either). See docs/lamparray.md.
/// </summary>
```

## `Vendors/Generic/NvidiaGpu.Windows.cs`

### `7-23` → `docs/nvidia-gpu-oc.md` — whole document

```csharp
/// <summary>
/// NVIDIA discrete-GPU clock overclocking via NvAPI (nvapi64.dll) — the same mechanism G-Helper and MSI
/// Afterburner use: a signed core- and memory-clock offset written into the P0 (max-performance) state's
/// frequency delta, which shifts the whole voltage/frequency boost curve. NvAPI exposes exactly one real DLL
/// export, <c>nvapi_QueryInterface</c>, which resolves each function's pointer by a stable hex ID; those
/// pointers are then invoked through unmanaged function pointers (<c>delegate* unmanaged</c>) over blittable
/// structs — no runtime-generated marshalling and no COM, so it is Native-AOT-safe.
///
/// The offset is VOLATILE — the driver zeroes it on every reboot / driver reload / dGPU power-cycle (an
/// Optimus laptop dGPU going D3-cold), so the app is the source of truth: LaptopService persists the user's
/// choice per performance mode and re-applies it at startup, on resume, and on each mode switch. Writing the
/// offset needs the process elevated (the app already runs as admin for the EC/WMI controls).
///
/// Windows-only: nvapi64.dll ships with the NVIDIA driver and is simply absent on AMD/Intel-only laptops,
/// where <see cref="TryCreate"/> returns null and the UI hides the GPU section. See docs/nvidia-gpu-oc.md.
/// </summary>
```

## `Vendors/Generic/OverlayCpuPower.Windows.cs`

### `6-20` → `docs/power-an18-61.md` — *The CPU-power axis on this machine: the OS overlay, nothing else*

```csharp
/// <summary>
/// CPU power management via the Windows Power-Mode overlay (the taskbar battery-slider modes: Best power
/// efficiency / Balanced / Best performance), driven by <c>powrprof.dll</c>'s <c>PowerSetActiveOverlayScheme</c>.
/// This is the one CPU-power knob available with NO driver at all on this class of machine: real PPT/STAPM/TDP is
/// ring-0 (RyzenAdj/SMU) and Acer — unlike ASUS — exposes NO WMI/ACPI CPU-power setter (it bakes the whole envelope
/// into its fixed EC profiles). So, mirroring G-Helper's driverless CPU axis, the app maps an OS power mode to each
/// performance profile. The voltage-curve axis is a separate port that does need a ring-0 gateway — see
/// <see cref="ICurveOptimizer"/> / <see cref="RyzenCurveOptimizer"/>.
///
/// The overlay is an axis ORTHOGONAL to the Acer performance profile (the Acer WMI profile write carries no
/// overlay GUID and touches no Windows power scheme), so setting it per profile does not fight the EC.
/// Cross-vendor + Windows-only; <see cref="TryCreate"/> returns null on an OS without the overlay API so the UI
/// hides the CPU section. No elevation needed for the overlay (the app runs elevated anyway for the EC/WMI
/// controls). Named to avoid colliding with the <c>IDevice.CpuPower</c> port property.
/// See docs/power-an18-61.md.
/// </summary>
```

## `Vendors/Generic/PawnIo.Windows.cs`

### `7-36` → `docs/pawnio.md` — whole document

```csharp
/// <summary>
/// Windows transport for <b>PawnIO</b> (pawnio.eu) — the signed, sandboxed ring-0 gateway this app uses to reach
/// the AMD SMU. PawnIO is a kernel driver that executes small verified bytecode <i>modules</i>; a module declares
/// which hardware it may touch, so the driver grants narrow access instead of the blanket "read/write any port,
/// any MMIO" hole that WinRing0 and inpoutx64 are.
///
/// Why not the drivers already on this class of machine: WinRing0 (which RyzenAdj ships) is named in Microsoft's
/// vulnerable-driver blocklist and carries an active Defender signature, so it loads only while the blocklist and
/// HVCI happen to be off — a posture one Windows policy push can revoke. inpoutx64 cannot do the job at all: its
/// author documents <c>DlPortWritePortUlong</c> as not working as expected (and a 32-bit write to the PCI
/// CONFIG_ADDRESS port at 0xCF8 is exactly what an SMN transaction needs — PCI does not treat a byte/word write
/// there as an address-latch update, so it cannot be split), while <c>MapPhysToLin</c>/<c>GetPhysLong</c> are
/// documented as limited to physical addresses under 2 GB, which excludes any plausible MMCFG base.
///
/// Protocol: one device, two IOCTLs, both plain buffered.
///
///   LOAD_BINARY   the module blob; one loaded module per open handle, so a second module needs a second handle
///   EXECUTE_FN    call a function the module exports, by name
///
/// The execute buffer layout is a 32-byte ASCII function name (zero padded) followed by packed little-endian
/// 64-bit arguments; the output is packed 64-bit values. Sizes are validated STRICTLY by the module (PawnIO's
/// DEFINE_IOCTL_SIZED returns STATUS_INVALID_PARAMETER on any mismatch), so the caller must pass exactly the
/// argument and result counts the function declares. Everything is blittable — plain <c>byte[]</c> written with
/// <see cref="BitConverter"/>-free span helpers — so there is no marshalling stub and this stays Native-AOT-safe.
///
/// The driver and its module blobs ship SEPARATELY from the app (same arrangement as the LampArray driver, see
/// <see cref="LampArrayTransport"/>): PawnIO is installed by its own signed installer, and the module is a signed
/// binary only its author can produce. So this class never installs anything — it probes, and every consumer
/// treats "absent" as "feature unavailable" rather than as an error. See docs/pawnio.md.
/// </summary>
```

## `Vendors/Generic/PawnIoInstaller.Windows.cs`

### `7-30` → `docs/pawnio.md` — *Installing it: the redistributable, and the three rules*

```csharp
/// <summary>
/// Detects and, on request, installs <b>PawnIO</b> (pawnio.eu) — the signed ring-0 gateway the CPU-undervolt
/// feature needs (see <see cref="RyzenCurveOptimizer"/>). It is a third-party kernel driver by namazso, not part
/// of this app, and it is installed ONLY after the user says yes.
///
/// Why the app ships the installer at all: the signed edition is proprietary freeware, but its own binary carries
/// an express grant — "This installer can be redistributed unmodified." — and the author's module documentation
/// states the same ("Official and unrestricted binary editions: Proprietary, however redistribution of installer
/// is allowed"). That permission covers exactly one thing: shipping <c>PawnIO_setup.exe</c> byte-for-byte.
/// Unpacking PawnIO.sys or PawnIOLib.dll out of it and laying them down as our own files is NOT covered, so the
/// payload is invoked, never opened. The author's stated preference is that apps merely point users at
/// pawnio.eu, so the offer names what it installs and where it comes from rather than burying it.
///
/// Three rules this class will not break, because PawnIO is a SHARED dependency — UXTU, ZenTimings, FanControl and
/// LibreHardwareMonitor use the same driver:
///   * install only when it is absent (the installer refuses over an existing install anyway, and silently),
///   * never upgrade — a newer driver under another tool's feet is not ours to swap,
///   * never uninstall, including when AcerHelper itself is removed.
///
/// Detection is the ARP registry key, NOT the device handle: opening \\?\GLOBALROOT\Device\PawnIO tells you the
/// driver is *usable right now* (it fails when not elevated, or when the node is stopped), which is a different
/// question from whether it is installed — and answering it wrongly would fire the installer at a machine that
/// already has PawnIO, where it fails with no UI at all. See docs/pawnio.md.
/// </summary>
```

## `Vendors/Generic/RyzenCurveOptimizer.Windows.cs`

### `7-39` → `docs/curve-optimizer-strix-point.md` — whole document

```csharp
/// <summary>
/// AMD Curve Optimizer on Zen 5 mobile — an AVFS voltage-curve offset applied through the SMU. A negative offset
/// shifts the whole voltage/frequency curve down: less voltage at every frequency point, so a fixed workload draws
/// less power and runs cooler, and inside a power-limited envelope the part holds higher clocks. The CPU-side
/// analogue of the GPU clock offset in <see cref="NvidiaGpu"/>, and just as volatile.
///
/// THREE rails over TWO mailboxes: the two core clusters are offset on MP1 (0x4B per slot, 0x4C all-core), the
/// integrated Radeon on RSMU (0x1F to set, 0x20 to read back). One port, because they are one kind of thing — a
/// curve offset on this package, SMU-resident, re-applied per performance mode — not because they share a path.
///
/// What each rail can and cannot promise:
///  - CORES: no read-back exists, and the mailbox is known to acknowledge while the platform's power mode suppresses
///    the effect. A successful write means the SMU accepted the message and nothing more. That the curve really moves
///    was established here by measurement — an all-core -30 dropped every per-core voltage word in the SMU's PM
///    telemetry table by ~76 mV under constant load — not by trusting the response code.
///  - iGPU: has a getter, so every write is CONFIRMED by reading the margin back and a mismatch is reported as a
///    failure. Its scale was measured separately and is NOT the cores': 5 mV per count against their 2.5, so the two
///    rails cannot share a millivolt figure.
///
/// Granularity is per CLUSTER, not per core, and that is hardware rather than simplification: 0x4B does address a
/// single slot, but a cluster shares a rail whose setpoint is the MAXIMUM of its cores' requests, so the delivered
/// undervolt is the mildest offset in the cluster and per-core sliders would leave 8 of 10 cores inert.
///
/// The offset is VOLATILE — it lives in SMU state and a power cycle restores stock — so the app is the source of
/// truth and re-applies per performance mode at startup, on resume, and on each mode switch (see LaptopService),
/// exactly like the GPU clock offsets. That volatility is also the recovery path: nothing is written to firmware.
///
/// Gated to AMD family 0x1A models 0x20/0x24 (Strix Point) and to a machine where PawnIO is installed;
/// <see cref="TryCreate"/> returns null otherwise, so the port stays null and the UI hides the section
/// (<see cref="PawnIoInstaller"/> is what offers to install the driver). The probe is deliberately cheap — CPUID
/// plus a registry read — because composition runs on the UI thread; the driver handle is opened on first use
/// instead, off that thread, since loading a module is a kernel-side signature verify.
/// See docs/curve-optimizer-strix-point.md and docs/pawnio.md.
/// </summary>
```

### `57-67` → `docs/curve-optimizer-strix-point.md` — *The iGPU opcode: a wrong-opcode false negative*

```csharp
// ---- integrated-GPU curve (RSMU) ----
// The graphics rail has its own Curve Optimizer, and it is NOT the opcode RyzenAdj's set_cogfx uses: that one
// (PSMU 0xB7, inherited from the Rembrandt..Hawk Point lineage) does not list Strix Point at all, and probing it
// here answers 0xFD "prerequisites not met" every single time, idle and under GFX load alike — a wrong-opcode
// false negative that reads exactly like an absent feature.
//
// The right numbers come from ZenStates-Core, whose APUSettings1_Strix inherits APUSettings1_Phoenix. What makes
// that table trustworthy for THIS die rather than one more guess: the same file carries MP1 SetDldoPsmMargin 0x4B
// and SetAllDldoPsmMargin 0x4C — the CPU opcodes already proven to work here.
//
// Verified on this machine: 0 -> -5 -> 0, every transaction REP_MSG_OK, each step confirmed by reading 0x20 back.
// See docs/curve-optimizer-strix-point.md.
```

### `89-99` → `docs/curve-optimizer-strix-point.md` — *Slot topology (measured, because the SMU will not say)*

```csharp
// Per-core argument layout: [31:28] = CCD, [23:20] = core within CCD, [15:0] = margin. Confirmed on this part by
// applying an offset to one slot at a time and watching exactly one per-core voltage word in the SMU's PM
// telemetry table drop by ~78 mV, then recover.
//
// Which slots are POPULATED had to be measured, because the SMU gives no way to ask: an unpopulated slot answers
// REP_MSG_OK and changes nothing, exactly like a populated one, so cores cannot be enumerated by response code.
// Measured on this die (4x Zen5 + 6x Zen5c): Zen5 on CCD0 slots 0, 2, 4, 6 — stride two, not consecutive — and
// Zen5c on CCD1 slots 2..7, i.e. the LAST six of eight. The rule below reproduces that and extrapolates to the
// other Strix Point configuration (a Ryzen AI 9 HX 370 is 4+8, so its Zen5c would fill slots 0..7). A wrong guess
// on some future SKU is benign rather than dangerous — a core would simply get no slider, or a slider would drive
// an empty slot and do nothing. See docs/curve-optimizer-strix-point.md.
```

### `119-128` → `docs/pawnio.md` — *The module: `RyzenSMU.bin`*

```csharp
// The PawnIO module that whitelists the SMU register window, and the functions we drive. We do NOT use the
// module's generic send-SMU-command entry point: its internal table resolves Strix Point to the RSMU address
// triple, so a 0x4C sent that way would go to the wrong mailbox. Hand-rolling the transaction over the raw
// register accessors is what G-Helper does, and it is legal because the module's own range check admits the
// whole 0x3B10000-0x3B10FFF window these three registers live in.
// The module blob is EMBEDDED, which is what its author prescribes: modules are LGPL-2.1-or-later, the
// integration guide says to take a release blob and "include its contents in your software", and the project
// states outright that module APIs are NOT stable across releases — so the version whose call shapes this file
// is written against has to travel with it. Note the PawnIO installer ships no modules at all, so there is no
// shared system location to read one from; an explicit override file is honoured for advanced use.
// See docs/pawnio.md.
```

### `145-159` → `docs/curve-optimizer-strix-point.md` — *Exposed range: `-40 … 0`*

> **Drops a false claim.** The original's last paragraph says per-core "is unreachable here (opcode `0x4B` is
> unconfirmed on this die, reported rejected on Krackan Point, and there is no way to learn the core-fuse
> topology)". That contradicts the rest of the file (`:65` "the CPU opcodes already proven to work here",
> `:89-99` measured slot topology, `SetDomains` writing `0x4B` on the live UI path). The replacement omits it.
> The genuine bound — an all-core offset is limited by the **worst** core — is kept.

```csharp
// Exposed range. Negative only: a positive offset RAISES voltage, which buys nothing here and is a thermal and
// stability risk, so it is not offered.
//
// The floor is -40 to match what the rest of the ecosystem allows, and because it is a real voltage bound
// rather than a round number: measured on this part, one count is ~2.5 mV (an all-core -30 moved every per-core
// voltage word in the SMU's PM table from ~1.03 V to ~0.95 V), so -40 is ~100 mV — a normal undervolt target,
// while the only published failure datum on a sibling Zen 5 die is a crash at -50.
//
// Not lower, for a reason that is not mere caution: an ALL-CORE offset is bounded by the WORST core, so past
// some point the limit is one weak core, not the average, and the good cores cannot cash in the difference.
// Undervolt failures on Zen 5 also surface hours later at idle as machine-check errors or silent corruption
// rather than as an obvious crash under load, so the end of the slider should not be a place a single drag
// lands by accident. See docs/curve-optimizer-strix-point.md.
```

### `199-210` → `docs/curve-optimizer-strix-point.md` — *Granularity is per CLUSTER, not per core*

```csharp
// ONE KNOB PER CLUSTER, not per core — the granularity the hardware actually honours.
//
// MP1 0x4B addresses a single slot and provably works (it moves exactly one per-core voltage word in the PM
// table). But that word is the core's REQUEST, not what it is fed: each cluster shares a rail whose setpoint is
// the MAXIMUM of its cores' requests, with no per-core LDO drop below it on this die. So within a cluster the
// delivered undervolt is the SMALLEST offset among its cores — offsetting one core alone changes nothing, and
// per-core sliders would leave 8 of 10 inert.
//
// The clusters, however, are INDEPENDENT domains: measured at stock under load Zen 5 sits near 1.17 V and
// Zen 5c near 1.02 V, and offsetting one cluster moves only that cluster. Hence exactly two tunable values.
// Each is written to every slot of its CCD so that no core is left holding a milder request that the rail
// would then follow. See docs/curve-optimizer-strix-point.md.
```

### `214-224` → `docs/curve-optimizer-strix-point.md` — *The iGPU as a third domain*

```csharp
// The iGPU rides along as a third domain. It is the same kind of thing as a cluster — an AVFS curve offset on
// one rail of this package, volatile, re-applied per performance mode — so it belongs in the same list rather
// than in a port of its own; only the mailbox and the argument encoding differ.
//
// Added unconditionally on a supported Strix Point rather than probed, because probing means opening the
// driver and loading a module, and this constructor runs during composition on the UI thread (see TryCreate).
// Every Strix Point ships the integrated Radeon, so the assumption is safe; and if some SKU ever refuses, the
// write fails loudly through LastError instead of silently doing nothing.
//
// Deliberately NOT offered when cluster detection failed above: that path falls back to the single all-core
// Set(), and a domain list containing only the iGPU would quietly turn the CPU slider into a GPU one.
// See docs/curve-optimizer-strix-point.md.
```

### `334-341` → `docs/curve-optimizer-strix-point.md` — *Read-back handling on the iGPU*, *Argument encoding*

```csharp
/// <summary>Write the integrated GPU's curve offset and CONFIRM it, which is the one thing the CPU side cannot do:
/// this rail has a getter, so a write is verified by reading the margin straight back rather than trusted because
/// the mailbox said REP_MSG_OK. A mismatch is reported as a failure — an accepted-but-ignored write is exactly the
/// failure mode that made the CPU path need statistics to believe.
///
/// Mind the ASYMMETRIC encoding, which is easy to get backwards: the SETTER takes a 16-bit two's-complement margin
/// (-5 is 0xFFFB), the GETTER answers a full 32-bit signed value (-5 comes back as 0xFFFFFFFB). Sign-extending the
/// reply from bit 15 turns a perfectly good -5 into -65541. Caller holds <see cref="_gate"/>.
/// See docs/curve-optimizer-strix-point.md.</summary>
```

## `Vendors/Generic/WbemInterop.Windows.cs`

### `6-20` → `docs/wmi-interop.md` — *Why not `System.Management`*, *Rule 1 — vtable order is load-bearing*

```csharp
// Low-level WMI COM interop, done with SOURCE-GENERATED COM ([GeneratedComInterface]) + raw VARIANT/
// SAFEARRAY pointers so the whole thing is Native-AOT-safe. This is why we don't use System.Management:
// it relies on classic COM interop (runtime-generated IL marshalling stubs), which Native AOT cannot do
// (it throws "COM Interop is not supported on this platform"). The source generator here emits the vtable
// dispatch at compile time instead, so no runtime codegen is needed.
//
// CRITICAL: the methods in each interface are declared in EXACT vtable order (from wbemcli.h). The vtable
// slot of a method = its declaration position (after IUnknown's QueryInterface/AddRef/Release). Methods we
// don't call are declared as ordered placeholders purely to occupy their slot — DO NOT reorder, rename, or
// delete any method, and add new ones only in their correct wbemcli.h position. A wrong slot is a silent
// wrong-function call (crash / garbage), invisible until it runs on real hardware.
//
// All string parameters are passed as BSTR handles (nint) that the caller allocates with SysAllocString and
// frees with SysFreeString — a BSTR is null-terminated UTF-16, so it also serves where the API wants a plain
// LPCWSTR (Get/Put property names). VARIANT/CIMTYPE out-params are passed as raw pointers to caller locals.
// See docs/wmi-interop.md.
```

### `56-64` → `docs/wmi-interop.md` — *Rule 2 — wrap with `UniqueInstance`, or leak silently*

```csharp
// ComWrappers instance that turns a raw COM pointer into a callable RCW for our [GeneratedComInterface]
// types. Always wrap through Wrap(): source-generated RCWs do NOT implement IDisposable (a cast is a
// silent no-op) — the only deterministic release is ComObject.FinalRelease(), and that is itself a no-op
// unless the wrapper was created with CreateObjectFlags.UniqueInstance. Getting this wrong doesn't fail
// loudly: every COM reference just waits for a gen-2 GC + finalizer pass, and an enumerator proxy created
// on the (STA) UI thread then gets Released from the (MTA) finalizer thread — a wrong-apartment Release
// that can leak the matching server-side object inside the WinMgmt service. This is why the interfaces
// below return raw nint out-params instead of typed RCWs: the generated marshaller would wrap them
// WITHOUT UniqueInstance, making deterministic release impossible. See docs/wmi-interop.md.
```

## `Vendors/Generic/WmiSession.Windows.cs`

### `19-27` → `docs/wmi-interop.md` — *`WmiSession.Gate` — one EC transaction at a time, process-wide*

```csharp
// Serializes every WMI transaction process-wide. The Acer ACPI-WMI methods talk to ONE embedded
// controller, which does not tolerate overlapping calls: a background set (a toggle) firing while the
// 3-second poll is mid-burst of sensor/profile reads would intermittently be rejected by the EC — the
// switch moved but the hardware didn't ("sometimes doesn't apply / rolls back"). Every QueryFirst /
// InvokeMethod takes this lock, so at most one EC transaction is ever in flight. Monitor is re-entrant,
// so InvokeMethod's own call to QueryFirst (to fetch the instance) doesn't self-deadlock. Static, so it
// spans all sessions/threads/classes (gaming, battery, APGe all hit the same EC). Each call still holds
// it only for its own transaction (released in between), so reads and writes just interleave, never
// overlap; the ~ms of blocking on the UI-thread poll is negligible.
// See docs/wmi-interop.md.
```

---

# Part B — blocks of 4–7 lines that carry measured facts

These are below the plan's "longer than 8 lines" threshold but each holds a measured number, a wire-level rule
or a licence constraint that belongs in the document, not in the body. Same format; the replacement is a
2–4 line invariant.

## `Features/LampArray.cs`

### `44-47` → `docs/lamparray.md` — *The lamp model*

```csharp
/// <summary>The colour to actually render. We advertise <c>IntensityLevelCount = 1</c> (this hardware has
/// no per-lamp gain — brightness is a per-write byte for the whole keyboard), which per spec means the
/// intensity channel degenerates to on/off and the host bakes brightness into RGB. So: intensity 0 is
/// "lamp off", anything else means "render RGB as sent". See docs/lamparray.md.</summary>
```

### `103-107` → `docs/lamparray.md` — *Implementation notes → Geometry actually published*

```csharp
// Nominal physical geometry. The Acer controllers expose ZONES, not keys, and nothing in firmware, WMI or
// acer-models.json reports the keyboard's real dimensions — so we describe a plausible full-size gaming
// keyboard and spread the zones across it. What matters to the host is not absolute accuracy but that the
// lamps are laid out left-to-right in the right PROPORTIONS: that is what makes a "wave" sweep the right way
// round. See docs/lamparray.md.
```

## `Features/LampArrayBridge.cs`

### `137-141` → `docs/lamparray.md` — *Ownership*

```csharp
/// <summary>Repaint the hardware from the host's last frame, ignoring the dedupe — for the events where the
/// EC or the OS clobbers the surface (a profile switch forces the amber OPMODE flash and wipes the RGB, sleep
/// drops it, a clamshell lid-open restores from black) and the host is the rightful owner of what should be
/// showing. See docs/lamparray.md.</summary>
```

## `Features/Ports.cs`

### `123-127` → `docs/nvidia-gpu-oc.md` — *Volatility*

```csharp
/// <summary>Discrete-GPU clock overclocking: signed core- and memory-clock offsets (MHz) layered on the GPU's
/// stock boost curve, each within the driver-reported allowed range. Present only when a controllable NVIDIA
/// dGPU is detected — the port is null otherwise, so the UI hides the section. The offsets are NOT persisted
/// by the driver (a reboot/driver-reload zeroes them), so the app is the source of truth and re-applies on
/// startup, on resume, and on every performance-mode switch (see LaptopService).
/// See docs/nvidia-gpu-oc.md.</summary>
```

### `142-148` → `docs/power-an18-61.md` — *The CPU-power axis on this machine*

```csharp
/// <summary>CPU power behaviour via the Windows Power-Mode overlay (Best efficiency / Balanced / Best
/// performance) — the one CPU-power knob that works with no driver at all on this class of machine. Acer exposes
/// no WMI power path (it bakes the whole PPT/STAPM envelope into its fixed EC profiles), so — exactly like
/// G-Helper's driverless CPU axis — this maps a chosen OS power mode to each performance profile. Ids are the
/// overlay scheme GUID strings; the mode set is the three fixed OS overlays. Present only where the overlay API
/// responds (probe-and-hide). The voltage-curve axis is separate and needs a driver: see
/// <see cref="ICurveOptimizer"/>. See docs/power-an18-61.md.</summary>
```

### `182-188` → `docs/curve-optimizer-strix-point.md` — *The iGPU as a third domain*, *UI*

```csharp
/// <summary>The independently tunable voltage domains, in display order — empty when the CPU accepts only one
/// offset for everything. A hybrid part has more than one because its clusters are separate rails, and that is the
/// finest granularity worth exposing: within a rail the delivered voltage follows the mildest core's request, so a
/// per-CORE control would leave all but one core per cluster inert. On an APU the integrated GPU is a domain too:
/// same SMU, same volatility, same per-mode preset, just a different rail and mailbox. A domain may carry its own
/// <see cref="VoltageDomain.Range"/> / <see cref="VoltageDomain.MillivoltsPerCount"/>, so callers must read those
/// per domain and fall back to the port's only when they are null. See docs/curve-optimizer-strix-point.md.</summary>
```

### `220-226` → `docs/pawnio.md` — *Installing it: the redistributable, and the three rules*

```csharp
/// <summary>A third-party driver some feature needs, which the app can offer to install. Present only when that
/// driver is relevant to THIS machine and this build actually carries its installer — so a machine that can never
/// use it is never asked, and a build without the payload never offers something it cannot do.
///
/// Deliberately an offer, not an action taken on the user's behalf: installing a kernel driver is the user's
/// decision, it is somebody else's software, and it may be shared with other tools on the machine. Nothing here
/// upgrades or removes anything. See docs/pawnio.md.</summary>
```

## `Vendors/Acer/AcerDevice.Windows.cs`

### `44-49` → `docs/power-an18-61.md` — *It is a latch, not a daemon*

```csharp
// Boot sync, EC-ONLY: the profile byte survives a reboot but the EC usage mode behind it does not, so
// the machine can report Turbo while running the lowest power row. Push the mode matching whatever
// profile the hardware reports — deliberately NOT a profile switch. Driving a full pp.Set() here (as
// 0.28.0 did from LaptopService.ApplyStartupState) re-flashed the lightbar and raced the power-source
// restore, producing a visible Balanced->Turbo->Eco cascade at every boot. This write is invisible: no
// WMI write, no palette flash, and it cannot disagree with what the UI shows. See docs/power-an18-61.md.
```

### `84-88` → `docs/power-an18-61.md` — *The finding*

```csharp
// TWO channels, both needed. The WMI byte is what the EC reports back as "current profile", so the tray,
// the per-mode presets and the lightbar palette all follow it. The EC HID usage mode is what actually moves
// the power envelope (GPU TGP/CTGP + CPU limits) on models that expose it — on the AN18-61 the WMI byte
// alone leaves the dGPU at its bare vBIOS default. They are independent; the EC write is only enqueued
// (it lands on the controller's writer thread), so it cannot slow this call down. See docs/power-an18-61.md.
```

## `Vendors/Acer/AcerProfiles.cs`

### `17-22` → `docs/lighting-an18-61.md` — *OPMODE colour is a hard firmware WHITELIST*

```csharp
// Accent = the UI accent (tray/highlight). Flash = the fixed colour the EC firmware paints the
// "operating mode" indicator (lightbar + keyboard flash) for this profile — the only colours the
// firmware accepts on the OPMODE write; sending anything else there reverts to amber. These are the
// per-profile lightbar colours (verified on Nitro AN18-61 via the OPMODE HID report; see
// docs/lighting-an18-61.md). Given as true RGB — EneHidController.SetProfileFlash serialises these to the
// OPMODE wire order (B,G,R), which differs from the R,G,B order of the arbitrary-colour writes.
```

## `Vendors/Acer/EneHidController.cs`

### `18-24` → `docs/lighting-an18-61.md` — *HID protocol*, *Device*

```csharp
// ENE RGB packet: A4 [TGT] [MODE] [BRI 0..0x64] [SPD] [FLAG] c0 c1 c2 [ZONEMASK] 00
// Colour byte ORDER is mode-dependent (verified on Nitro AN18-61): the arbitrary-colour writes — keyboard
// STATIC and the lightbar (A4 65) — render the three bytes as R,G,B (a UI red must go out FF 00 00, else the
// lightbar shows blue). The OPMODE profile-flash handler is a *separate* firmware path that instead
// recognises its per-profile palette in B,G,R and whitelists it (see SetProfileFlash). Send() below emits the
// R,G,B arbitrary-colour order; SetProfileFlash emits B,G,R itself and does not route through Send().
// Zone masks: keyboard has 4 zones (0x0F = all); the lightbar has 5 (0x1F = all).
// See docs/lighting-an18-61.md.
```

### `34-38` → `docs/lighting-an18-61.md` — *Serialised background writer, and the 10 ms pacing*

```csharp
// Feature writes go through a single background worker (see SetFeature), never the caller's (UI) thread:
// WriteFeature is a synchronous no-timeout HID write, and on HID-over-I2C models (AN18-61) it can block
// for a long time when that shared bus is saturated — e.g. an external USB-C display, worst at boot.
// Doing it on the UI thread froze the whole app until the bus freed (monitor unplug); the worker keeps
// such a stall off the UI thread. Started only on the keep-path (transport opened above).
// See docs/lighting-an18-61.md.
```

### `68-74` → `docs/lighting-an18-61.md` — *`SetProfileFlash()` does not route through `Send()`*

```csharp
// The performance-profile "operating mode" flash is a GLOBAL write (keyboard target 0x21, mode 0x06) that
// paints BOTH the keyboard and the lightbar at once. Unlike the arbitrary-colour paths (which the firmware
// renders R,G,B), the OPMODE handler recognises its per-profile palette in B,G,R and whitelists it — anything
// else reverts to amber. So this path is NOT routed through Send() (that applies the R,G,B arbitrary order):
// it emits the palette colour in B,G,R directly, reproducing byte-for-byte what NitroSense sends. This is how
// the lightbar gets its per-profile colour when it "follows the profile" — it has no standalone software
// colour, so we re-send this on each profile switch. See docs/lighting-an18-61.md.
```

### `78-82` → `docs/lighting-an18-61.md` — *`Blank()` while the lid is shut*

```csharp
// Turn every zone off — blanks the backlight while it's hidden under a shut lid in clamshell (keep-awake)
// mode; the app restores it by re-applying the current mode's lighting on lid-open. The keyboard honours the
// brightness byte, so a STATIC write at brightness 0 darkens it; the lightbar ignores that byte (see
// ApplyLightbar), so it's darkened with a black STATIC colour instead. A follows-profile lightbar (no panel)
// is included too — its palette is repainted from the profile flash on the restore. Doesn't touch stored
// state. See docs/lighting-an18-61.md.
```

### `149-155` → `docs/lighting-an18-61.md` — *Serialised background writer, and the 10 ms pacing*

```csharp
// Coalesce a superseded same-region write by MOVING it to the tail (not replacing in place): the
// coordinator always enqueues the profile-flash (mode 0x06) BEFORE the keyboard paint (mode 0x02) so the
// custom colour lands on top of the global flash. Move-to-tail orders each region by its most-recent enqueue,
// so the latest paint stays AFTER the latest flash in every interleaving — including a multi-second bus stall
// where a re-emitted flash+paint pair queues behind an in-flight write. In-place replacement would strand an
// early paint ahead of a later flash and leave the keyboard showing the flash palette instead of the custom
// colour. See docs/lighting-an18-61.md.
```

## `Vendors/Generic/LampArrayTransport.Windows.cs`

### `59-65` → `docs/lamparray.md` — *`DriverInstalled` — a driver-store check, not a device probe*

```csharp
/// <summary>Whether the driver package is staged on this machine. The whole feature (and its Options row) is
/// hidden until it is: the driver ships separately from the app because it needs a signature Windows will
/// load without test-signing (see docs/lamparray.md), so most installs won't have it.
///
/// Checked by looking for the package in the driver store — that is what <c>pnputil /add-driver</c> creates,
/// and it is true before any device node exists (the app creates the node itself, so there is no device to
/// interrogate yet). The System32\drivers copy is accepted too, for a package built with DIRID 12.</summary>
```

### `213-216` → `docs/lamparray.md` — *Device-node creation (and why it is not a permanent node)*

```csharp
// Create the root-parented software device node the driver binds to. This is what makes the virtual
// LampArray exist ONLY while the app wants it: the node (and with it the Dynamic Lighting entry) is
// created here and destroyed in Close(). The alternative — a permanently installed node via devgen/devcon
// — would leave a dead lighting device listed whenever the app isn't running (see docs/lamparray.md).
```

## `Vendors/Generic/NvidiaGpu.Windows.cs`

### `39-42` → `docs/nvidia-gpu-oc.md` — *Safety caps, and the raw-vs-effective memory figure*

```csharp
// Safety caps on the exposed offset range (MHz), applied even if the driver reports more headroom — a
// single slider drag to an extreme offset can hang or corrupt the GPU (NVIDIA XID 62). The memory value is
// the RAW memory-clock offset, matching G-Helper's convention (it writes the number as-is, no GDDR6
// doubling); an Afterburner "effective" figure is ~2× this. See docs/nvidia-gpu-oc.md.
```

## `Vendors/Generic/PawnIo.Windows.cs`

### `41-44` → `docs/pawnio.md` — *Protocol: one device, two IOCTLs*

```csharp
// CTL_CODE(41394, 0x821, METHOD_BUFFERED, FILE_ANY_ACCESS) and (…, 0x841, …). Spelled as literals because the
// device type is PawnIO's own (0xA1B2), not a Windows one: (0xA1B2 << 16) | (fn << 2). Both codes were verified
// byte-for-byte against an installed PawnIO 2.2.0.0 — they appear in PawnIOLib.dll and adjacently in PawnIO.sys's
// dispatch switch — so these are the driver's real numbers, not values copied out of a write-up.
// See docs/pawnio.md.
```

## `Vendors/Generic/RyzenCurveOptimizer.Windows.cs`

### `81-86` → `docs/curve-optimizer-strix-point.md` — *The iGPU floor: `-50`*

```csharp
// The full range ZenStates documents for the PSM margin on Zen 4 and newer, deliberately not narrowed. At 5 mV
// a count this floor is -250 mV, and this sample already fell over well before it — but the stable limit is a
// property of the individual die, and clipping every machine to one unlucky one would cost the good parts real
// headroom. The guard is the LABEL, not the bound: the row reads "-50 (≈-250 mV)", and a figure like that warns
// far better than a slider that silently stops somewhere. See docs/curve-optimizer-strix-point.md.
```

### `133-136` → `docs/curve-optimizer-strix-point.md` — *Cross-process interlock: `Global\Access_PCI`*

```csharp
// Cross-process interlock. The mailbox is reached through the PCI config index/data pair on 00:00.0, which
// HWiNFO, CPU-Z, Ryzen Master and RyzenAdj all poke as well; this is the name that ecosystem agreed on. It is
// held across the WHOLE transaction, because per-access locking still lets another agent's message execute
// against our arguments. See docs/curve-optimizer-strix-point.md.
```

### `176-179` → `docs/curve-optimizer-strix-point.md` — *Scale: 2.5 mV/count on the cores, 5.0 mV/count on the iGPU*

```csharp
/// <summary>Measured on this part, not taken from a spec: an all-core -30 moved every per-core voltage word in
/// the SMU's PM telemetry table from ~1.03 V to ~0.95 V under a constant load, and back on stock — ~76 mV over
/// 30 counts. (AMD publishes no mV-per-count figure for Zen 5; the community's Zen 3 number was 3-5 mV.) Only a
/// display aid, since the real delta moves with frequency and temperature. See docs/curve-optimizer-strix-point.md.</summary>
```

### `235-239` → `docs/curve-optimizer-strix-point.md` — *Gating: which CPU, and only with the driver*

```csharp
/// <summary>Probe for a tunable CPU. Returns null — feature hidden — unless this is a CPU whose MP1 mailbox
/// layout is known AND PawnIO is installed. The driver check is the registry one, not a device open: it must
/// stay cheap (composition runs on the UI thread) and it must not depend on elevation. Gating on it keeps the
/// section honest — without the driver the sliders could appear and then refuse every write.
/// <see cref="PawnIoInstaller"/> is what offers to install it. Never throws.
/// See docs/curve-optimizer-strix-point.md.</summary>
```

### `297-301` → `docs/curve-optimizer-strix-point.md` — *`SetDomains` — write every slot, abort on first refusal*

```csharp
/// <summary>Apply one offset per entry in <see cref="Domains"/>. Each is written to EVERY slot of its CCD — all 8,
/// populated or not — because the rail follows the mildest request in the cluster, so a core left un-offset would
/// undo the setting; and because an empty slot answers REP_MSG_OK and changes nothing (measured), which also makes
/// this correct on a SKU with a different populated set. A partial failure leaves the clusters inconsistent, so the
/// first refusal aborts and is reported rather than pressed on with. See docs/curve-optimizer-strix-point.md.</summary>
```

### `370-373` → `docs/curve-optimizer-strix-point.md` — *Gating*, *The transaction*

```csharp
// Opened on first use, never at composition time: loading a PawnIO module makes the kernel verify a signed
// bytecode blob, which is exactly the kind of blocking call this app keeps off the UI thread (and composition
// runs there). Cached once open; a failed open is retried on the next call, since the user may install the
// driver while the app is running. Caller holds _gate. See docs/curve-optimizer-strix-point.md.
```

### `383-386` → `docs/curve-optimizer-strix-point.md` — *Argument encoding*

```csharp
// Encode an offset as the SMU expects: a 20-bit field, negatives as 0x100000 - |counts|. 0 is sent as a plain 0,
// NOT as 0x100000 — that would set bit 20, which the per-core form of this message uses as a core selector and
// is a known source of rejected arguments in other tools. The mask keeps that invariant local rather than
// depending on the caller's clamp. See docs/curve-optimizer-strix-point.md.
```

### `395-398` → `docs/curve-optimizer-strix-point.md` — *Argument encoding*

```csharp
// The GRAPHICS rail's margin encoding — ZenStates' Utils.MakePsmMarginArg verbatim: 16-bit two's complement.
// Deliberately a separate function from Encode above rather than a shared one with a width parameter, because the
// two differ in a way that fails silently: the CPU's 20-bit form of -5 is 0xFFFFB, the GPU's is 0xFFFB, and the
// mailbox accepts either without complaint while meaning something else entirely.
// See docs/curve-optimizer-strix-point.md.
```

### `402-406` → `docs/curve-optimizer-strix-point.md` — *The transaction*

```csharp
/// <summary>Run one message on <paramref name="mb"/> to completion and return the SMU's response byte, or
/// <see cref="NoResponse"/> when the interlock or a register access failed (with <see cref="LastError"/> already
/// set). <paramref name="reply0"/> receives argument 0 as the SMU left it, which is where a getter's value comes
/// back; it is read INSIDE the PCI interlock, because reading it afterwards would race another tuning tool's
/// transaction into our reply. Caller holds <see cref="_gate"/>. See docs/curve-optimizer-strix-point.md.</summary>
```

### `500-506` → `docs/curve-optimizer-strix-point.md` — *Gating: which CPU, and only with the driver*

```csharp
// Only the CPUs whose MP1 mailbox layout is actually known: AMD family 0x1A (Zen 5), models 0x20 and 0x24 — the
// Strix Point pair every tool maps to one address set. Krackan Point (0x60) and Strix Halo (0x70) are
// deliberately NOT accepted: they share the family case group in the reverse-engineering projects, but the
// published results diverge (the same opcode is reported rejected on one and effective on the other), so
// claiming support here would be guessing on someone else's hardware.
/// <summary>Whether this CPU is one the undervolt supports, independent of whether the driver is installed — so
/// the driver-setup offer knows if it is even relevant here. See docs/curve-optimizer-strix-point.md.</summary>
```

### `570-574` → `docs/pawnio.md` — *The module: `RyzenSMU.bin`*

```csharp
// Embedded blob by default, with a user file allowed to override it — the same arrangement acer-models.json
// uses. The override exists because module APIs are explicitly unstable across releases: if a future blob
// changes a call shape, someone can pin their own without waiting for a build. It is deliberately an explicit
// path in the app's own config folder rather than a scan of shared locations, so a stray file elsewhere can
// never silently change which bytes get loaded into the kernel. See docs/pawnio.md.
```

## `Vendors/Generic/WmiSession.Windows.cs`

### `6-11` → `docs/wmi-interop.md` — *`WmiSession.Gate` — one EC transaction at a time, process-wide*

```csharp
/// <summary>A short-lived WMI connection to one namespace, built on the source-generated COM interop in
/// <see cref="Wbem"/>. Deliberately per-operation: WMI COM proxies are apartment-bound and can't be shared
/// across threads, and our callers run on both the UI thread (sensor polling) and thread-pool threads
/// (set operations). Rather than marshal proxies between apartments, each call opens its own session on the
/// calling thread (CoInitialize + connect + proxy-blanket) and tears it down. Connecting costs ~a few ms —
/// negligible for our low-frequency use. See docs/wmi-interop.md.</summary>
```

### `234-239` → `docs/wmi-interop.md` — *`PutValue`: matching the property's real CIM type*

```csharp
/// <summary>Set an in-parameter, matching the property's real CIM type. Strings go in as VT_BSTR
/// (CIM_STRING parameters — e.g. Dell's BIOSAttributeInterface AttributeName/AttributeValue). Byte
/// arrays go in as a SAFEARRAY of VT_UI1 (Acer's firmware blocks expect uint8[] — uReserved[], etc.).
/// 64-bit CIM integers must be a VT_BSTR decimal string (WMI's representation — a VT_I4/VT_UI8 there is
/// silently rejected, which is what broke every Set*/Get* with a UInt64 gmInput/gmOutput). Everything
/// else goes in as VT_I4, which WMI coerces to the property's 8/16/32-bit type. See docs/wmi-interop.md.</summary>
```

---

# Deliberately left alone

Not in the map, and why.

| location | why |
|---|---|
| `LaptopService.cs` `_state` (its declaration comment) | This **is** the locking invariant ("`_state` guards the whole mutable `Settings` graph"). It is the rule a future editor must not break — it *is* the 1–2 line statement, at length. |
| `Options.cs:3-11` | The `OptionToggle` contract (Read-back after a write; only one of `Confirm`/`ConfirmAsync`). Pure API semantics, no hardware claim. |
| `UI/MainWindow.axaml.cs:8-15` | Avalonia window behaviour (fixed size; resizing on X11 races repositioning). UI framework, not hardware. |
| `Vendors/Acer/AcerDevice.cs:6-13` | Layering/ownership summary ("relies on Generic where possible"; null port = capability absent). |
| `Vendors/Acer/AcerHotkeys.Linux.cs:7-14` | Carries real reverse engineering (scancode `E0 75` → `KEY_PRESENTATION`; the Turbo key is consumed in-kernel by `linuwu_sense`'s `cycle_gaming_thermal_profile`; `/dev/input/event*` is `root:input`, hence the udev `uaccess` tag). It is **Linux-only and the only block on the subject** — there is no Acer-Linux document to append to, and creating one for a single 8-line block would be a near-duplicate of nothing. Left for a future Acer-Linux doc. |
| `Vendors/Acer/AcerModel.cs:8-17` | Class summary whose only hardware claim is a **negative** one ("profiles and fan topology are NOT per-model on Acer"), already implied by `AcerProfiles.cs` and `power-an18-61.md`. |
| `Vendors/Generic/GenericDevice.cs:6-13` | The generic-device layering contract (`protected set` ports; `InitPlatform` per OS). |
| `Vendors/Generic/KeyboardBrightness.Linux.cs:6-13` | Kernel LED-class doc, already ends with its own consequence. The single hardware datum (Dell Latitude levels `0..2` = Off/Dim/Bright) is a Dell detail that would sit oddly in any current doc. |
| every block under 4 comment lines | Not mapped. Most are 1–3 line invariants that already say the rule and nothing else. |

# Candidates for deletion rather than extraction

Long blocks that are **not** archaeology: they explain code that could simply be written more clearly, or they
narrate a past refactor. Relocating these to `docs/` would create a document with no reader.

| location | what it is | suggested action |
|---|---|---|
| `Localization/Loc.cs:8-20` (13L) | Why the app uses built-in tables instead of `.resx` (satellite assemblies break Native AOT, dotnet/runtime#86651) | Compress to **one line + the issue link**. The rest is prose about a decision already made. |
| `UI/ViewModels/MainViewModel.cs:60-68` (9L) | Avalonia drawer re-hosting bug (pages were previously re-created; now each has its own host in `MainWindow.axaml`) | Compress to one line stating "each drawer page has its own host; do not re-create them", or delete — the reason is historical. |
| `OptionsAssembler.cs` `PowerSourceProfiles` (9L) | Semantics of the per-power-source profile rows | Keep the invariant (one row per power source; the row's value is the profile), drop the narration. |
| `UI/LightingCoordinator.cs:11-22` (12L) | Class summary describing which `AppController` members forward into it | Delete most of it: it is a call-graph description that `AppController` itself already shows. Keep the two real invariants (built before any UI exists; does no hardware reads of its own). |
| `Vendors/Generic/Autostart.Windows.cs:8-16` (9L) | Task Scheduler design, including a **rejected** approach (`RestartOnFailure` was tried and dropped) | The keep-alive *invariant* (1-minute repeat + `MultipleInstancesPolicy=IgnoreNew` is the watchdog; there is no second process) belongs in the code. The `RestartOnFailure` post-mortem is the one part worth keeping — it is a dead end, and dead ends are what `docs/` is for; either move just that sentence or drop it. |
| `UpdateChecker.cs:22-27` (6L) | AOT assembly-version reflection constraint | Compress to one line. |
| `WindowsUpdater.cs:41-47` (7L) | Staging-dir TOCTOU reasoning (random name vs fixed name in `%TEMP%`) | This is a genuine security invariant and is **already stated in the code path**; compress to two lines rather than relocate. (Compare `Autostart.Windows.cs:47-52`, the same reasoning, which was *not* flagged for extraction.) |

# Comment/code contradictions

Reported, **not fixed**. The extraction step should not carry any of these claims into a replacement.

1. **`Vendors/Generic/RyzenCurveOptimizer.Windows.cs:152-156` — the per-core claim is false.** It says per-core is
   "unreachable here (opcode `0x4B` is unconfirmed on this die, reported rejected on Krackan Point, and there is
   no way to learn the core-fuse topology)". The same file contradicts it three times: `:65` calls `0x4B`/`0x4C`
   "the CPU opcodes already proven to work here"; `:89-99` says the per-core argument layout was "Confirmed on
   this part by applying an offset to one slot at a time and watching exactly one per-core voltage word … drop
   by ~78 mV"; and `SetDomains` (`:314-329`) writes `0x4B` to **every slot of each CCD** — that is the live path
   the 3-domain UI uses. Per-core *sliders* may still be the wrong UI, but per-core *writes* are in production.
   Handled in the map by omitting the sentence.
2. **`Features/LampArrayBridge.cs:44-46` — stale, and misplaced.** *"How long the worker keeps re-trying after
   the transport fails (driver unloaded, device removed): it just stops."* documents a retry-duration constant
   that no longer exists, and the comment sits directly above the unrelated field
   `private readonly IRgbDevice _rgb;`. `WorkerLoop` simply breaks out of the loop; re-enabling is a user action.
   **Fix: delete the block** (not extract — there is no content left).
3. **`Features/Ports.cs:160-169` — over-generalisation.** "this hardware offers no trustworthy read-back" is true
   of the CPU clusters and false of the iGPU, which has a getter (`0x20`) and is verified on every write
   (`SetGpu`, `:342-357`). Handled in the map by not repeating the clause.
4. **Minor numeric drift, same experiment.** The all-core `-30` result is quoted as **~76 mV** at `:20` and
   `:177`, and as **~78 mV** at `:91` (single-slot offset). Both are honest measurements of the same die, but the
   two numbers sitting in one file will read as a contradiction. The document records them explicitly as two
   views of one experiment.
5. **`Vendors/Generic/RyzenCurveOptimizer.Windows.cs:419` — the offset silently does not apply.** After the
   5-second PCI-mutex wait times out, the write is abandoned with *"another tool is holding the PCI access
   lock"* and the setting is never applied; users read this as "the undervolt doesn't hold". Already recorded in
   `docs/refactoring-plan.md` ("Известные нарушения") — not repeated here as a new finding, but the *comment*
   at `:419` does not say the offset is dropped, so a reader of the code alone would not know.
