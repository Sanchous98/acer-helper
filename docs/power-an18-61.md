# The performance envelope on the Nitro AN18-61 — EC HID, not WMI

Reverse-engineered and measured on a Nitro AN18-61 (RTX 5070 Ti Laptop, AMD Radeon 880M iGPU),
BIOS V1.53, NitroSense 5.1.392.

## The finding

**On this machine the gaming-WMI profile byte is only an indicator. The actual power envelope lives in
the EC and is reachable only over HID.**

`SetGamingMiscSetting` index `0x0B` — the call the app used for profiles — moves the tray state, the
per-mode presets and the lightbar palette, and the EC does report it back as "current profile". It does
**not** move the power envelope. Proof, measured live: NitroSense switching Quiet ↔ Turbo moved the dGPU's
`enforced.power.limit` between **71 W and 108 W** while *every* gaming-WMI value stayed frozen — index
`0x0B` sat on Eco (`0x06`) the whole time and `GetGamingProfile` never changed from `0x1000001000000`.

Consequence before this was fixed: the app could show (and the EC could report) "Turbo" while the dGPU ran
the lowest power row — 70 W base TGP plus whatever Dynamic Boost granted, ~78 W sustained instead of ~108 W.

## Device and wire format

| | |
|---|---|
| Device | VID `0x1025`, PID `0x174B` |
| Interface | the vendor collection — usage page `0xFF05`, usage `0x0001` (`…&col01`) |
| Reports | **65-byte feature reports**, report id `0xA0` |
| Transport | `HidD_SetFeature` (Windows) / `HIDIOCSFEATURE` (Linux hidraw) |

The device exposes nine collections; only this one has 65-byte feature reports (the others report 0, 3, 4
or 6), so the report length alone identifies it unambiguously. It sits on HID-over-I2C — the same bus as the
ENE RGB controller, which is why writes must never happen on the UI thread.

```
byte  0   0xA0   report id
byte  1   0x00   reserved
byte  2   0xA0   command marker
byte  3-4        feature id, little-endian uint16
byte  5          command id   (0x01 = set, 0x02 = get)
byte  6…         parameters
                 …zero-padded to 65
```

A `GetFeature` of report `0xA0` returns the reply, with **`byte[2] == 0xE0`** on success. That is
**frame-level only**: an out-of-range value is acknowledged the same way and then silently ignored — see the
methodology warning below.

### Commands used / known

```
SetTargetSystemUsageMode   A0 00 A0 01 00 01 <mode>      feature 0x0001, cmd 0x01   <-- what the app sends
GetOCProfileCapability     A0 00 A0 00 00 04 00 00 00    -> reply[7] = 4 profiles
GetOCProfileTable(idx)     A0 00 A0 02 00 02 <idx>       -> 22-byte row, idx 0..3
GetSystemUsageModeLimit    A0 00 A0 00 00 06 00 00 00
```

`GetCurrentSystemUsageMode` (`A0 00 A0 01 00 02 …`) is **rejected** on this BIOS — reply `[5] = 0x00` with
`0xFF` params. Acer's own agent hits the same wall on every switch and logs it, then retries through a v2
class that is just a wrapper around the same rejected call:

```
ERROR   acer_hid_2025.cpp:362    GetCurrentSystemUsageMode() Incorrect command id …BIOS return: 0
WARNING acer_hid_2025_v2.cpp:163 GetCurrentSystemUsageMode() Try GetCurrentSystemUsageMode() again!
```

So **there is no way to read the current mode back**. The app therefore treats the mode as write-only and
re-asserts it rather than reconciling it (see the boot sync in `AcerDevice.Windows.InitVendor`).

## Mode byte → measured dGPU power

Measured under sustained GPU load, each mode entered from a **re-confirmed mode 0**:

| mode | steady `enforced.power.limit` | SM clock | maps to |
|------|------------------------------|----------|---------|
| 0 | **108 W** | ~2230 MHz | Turbo |
| 1 | 93 W | 2050 MHz | Performance |
| 2 | 79 W | 1895 MHz | Balanced |
| 3 | 71 W | ~1700 MHz | Quiet |
| 4 | 71 W | ~1700 MHz | Eco |
| 5+ | — | — | acknowledged, then **ignored** |

Five valid values, matching the EC's own `System usage mode capability: 5`. Modes 3 and 4 are the same GPU
row and differ only in the CPU envelope, so Quiet and Eco map to them in that order.

### The EC's own power table

`GetOCProfileTable` dumps four rows; the values are little-endian uint16 watts and line up exactly with the
measurements (`TGP = 70 base + CTGP`, and `TGP + 15 W Dynamic Boost = 115 W`, which is precisely what
`nvidia-smi` reports as *Max Power Limit*):

| idx | CPU sust | CPU boost | **TGP** | **CTGP** | **Dyn Boost** | **core OC** |
|-----|----------|-----------|---------|----------|---------------|-------------|
| 0 | 35 | 85 | **100** | 30 | 15 | +100 MHz |
| 1 | 35 | 70 | **85** | 15 | 15 | +50 MHz |
| 2 | 35 | 55 | **70** | 0 | 15 | 0 |
| 3 | 25 | 45 | **70** | 0 | 15 | 0 |

The `+100 MHz` core overclock in row 0 is what NitroSense advertises as "overclocking" in Turbo. The EC does
**not** apply it — Acer's `AcerQAAgent` does, through NvAPI `SetPstates20`. This app already has that axis
(the Tuning drawer), so it is configured there rather than implied by the mode.

## It is a latch, not a daemon

The EC holds the mode: with **all nine Acer services stopped and NitroSense killed**, the limit stayed at
100 W+ under load with zero Acer processes alive. So a single write is enough and no resident agent is
needed — this is why the app can replace NitroSense outright rather than shadowing it.

Two caveats:

* It is **not known** to survive a reboot, which is why `AcerDevice.Windows.InitVendor` pushes the mode for the
  currently reported profile at startup. That write is deliberately **EC-only**: driving a full profile switch
  there (what 0.28.0 did from `LaptopService.ApplyStartupState`) re-flashed the lightbar and raced the
  power-source restore, producing a visible Balanced→Turbo→Eco cascade at every boot.
* Acer's `AcerQAAgent`, while running, re-applies **its** mode within a minute or two and will overwrite
  these writes. That is an argument for removing the Acer stack, not for polling here.

## Methodology warnings (both of these produced a wrong answer first)

1. **Never measure modes in a descending sequence.** A mode that is a no-op leaves the *previous* mode's
   power level in place and reads as if it worked — exactly how byte 5 first looked valid. Always return to
   mode 0, re-confirm ~108 W, then send the mode under test.
2. **Allow 20–30 s per mode.** An 8-second settle reported mode 2 as a noisy 65–77 W when its true steady
   value is a clean 79 W.

Instrument with `nvidia-smi --query-gpu=enforced.power.limit` under load. *Max Power Limit* is useless here
— it reads 115 W regardless. Idle readings mislead in the other direction: at idle, mode 5 shows a flat
115 W while under load it does nothing at all.

## Dead ends

* `nvidia-smi -pl` / NVML power limit — blocked: *"Changing power management limit is not supported in
  current scope"*.
* NvAPI — Acer's binaries only ever call `Get/SetPstates20` (clock offsets). No `ClientPowerPolicies*`
  function ids anywhere, and clock offsets cannot move a power limit.
* WMI — a full sweep of `GetGamingMiscSetting` / `GetGamingSysInfo` / `GetGamingProfileSetting` over
  `0x00`–`0x1F` found no power knob; `Get/SetGamingProfileSetting` returns status 1 or 2 in every encoding.
  There is no `SetGamingProfileConfiguration` method in any `root\WMI` class — that name is only an internal
  Acer service command that maps onto `SetGamingProfile`.
* MS Hybrid vs Discrete Only — a red herring on this machine: an external display always forces MS Hybrid
  here, and it is unrelated to the envelope.
* Reading the NVPCF ACPI tables through `GetSystemFirmwareTable` — impossible: ~40 SSDTs exist but Windows
  returns only the first per signature.

## Useful log paths for further work

* `C:\Windows\Temp\QuickAccess\AcerQAAgent.log` (SYSTEM; needs elevation) — prints
  `System usage mode capability: 5`, `OC profile capability: 4`, `Support Acer EC HID`,
  `Support Overclocking`, `current system usage mode: …`, and the command-id failures quoted above.
* NitroSense app log —
  `…\Packages\ULICTekInc.NitroSenseforNotebook_*\LocalCache\Roaming\acernitrosense\logs\nitrosense.log`.
  Confirms the app itself only ever sends `SET_DEVICE_DATA: OPERATING_MODE,v:N` (plus `FAN_CONTROL`) over
  TCP `127.0.0.1:46933`; all EC work happens inside the Acer services.

## The CPU-power axis on this machine: the OS overlay, nothing else

There is **no firmware CPU-power setter on this platform**, and that shapes the whole CPU-power design:

- Real **PPT / STAPM / TDP** is **ring-0** (RyzenAdj / SMU) — unreachable without a kernel driver.
- **Acer — unlike ASUS — exposes NO WMI/ACPI CPU-power setter.** It bakes the whole CPU envelope into its
  **fixed EC profiles** (that is the `System usage mode` table above).

So the only CPU-power knob available **with no driver at all** is the **Windows Power-Mode overlay** (the
taskbar battery-slider modes: *Best power efficiency* / *Balanced* / *Best performance*), driven through
`powrprof.dll`'s `PowerSetActiveOverlayScheme`. Mirroring G-Helper's driverless CPU axis, the app **maps an OS
power mode to each performance profile**. The overlay GUIDs are the documented ones (Balanced is the all-zero
GUID; Best power efficiency is `961cc777-2547-4f9d-8174-7d86181b8a7a`); the same set `OverlayPowerProfiles`
uses. **No elevation is needed** for the overlay (the app runs elevated anyway for the EC/WMI controls).

The **voltage-curve axis is a separate port** and does need a ring-0 gateway — see
`docs/curve-optimizer-strix-point.md` and `docs/pawnio.md`.

### Why it does not fight the EC

The overlay is an axis **orthogonal** to the Acer performance profile: **the Acer WMI profile write carries no
overlay GUID and touches no Windows power scheme**. So setting the overlay per profile does not fight the EC.

### Wiring constraint (easy to get wrong)

The port is wired **after** the vendor backend has finalized the profile port (`FinalizeCompositionPlatform`),
**not** in `InitPlatform`, and **only when the performance profiles are NOT themselves the Windows overlay**.

`OverlayCpuPower` and `OverlayPowerProfiles` drive the **same** overlay with the **same** GUIDs. If the profile
picker already *is* the overlay — a generic laptop, or a vendor whose WMI/BIOS profile path was unavailable —
then a CPU-power control would **fight it**, and, since the per-mode key is *then* the overlay GUID, **corrupt
the per-profile store**. So the CPU-power axis exists as an independent control **only when a vendor WMI/EC
profile port took over**. Null (section hidden) otherwise, and on any OS without the overlay API.

## How the app drives it (`AcerEcHidController`) — implementation notes

The class is a **cross-platform codec** (`AcerEcHidController.cs`) with **per-OS transport partials**. The
packets are identical on every OS — only the transport hooks differ (`OpenTransport` / `WriteFeature` /
`CloseTransport`).

- **Windows**: HidSharp (Win32 HID API).
- **Linux**: **hidraw directly**, no HID library — this controller hangs off **HID-over-I2C**, which HidSharp's
  Linux enumeration **never lists** (the same reason `EneHidController` has a Linux partial). One hidraw node
  covers *all* of a device's collections, so matching the parent hid device's `HID_ID`
  (`bus:vendor:product`) is enough; the report id in byte 0 selects the vendor collection's report. Reaching
  `/dev/hidrawN` without root relies on the desktop's **uaccess ACL** (present for built-in HID) or a udev rule —
  the same prerequisite the RGB controller documents.
- **Linux is untested on hardware.** The codec is verified on Windows. A missing or unwritable node degrades to
  `Available = false` and the profile path keeps its previous behaviour — so a wrong guess here means "no EC
  envelope control", **never** a bad write.

### Mode mapping per app profile class

| app profile | EC mode byte |
|---|---|
| Turbo | 0 |
| Performance | 1 |
| Balanced | 2 |
| Quiet | 3 |
| Eco | 4 |
| **Other** (unrecognised vendor profile) | **none — the EC is left alone** |

An unrecognised vendor profile deliberately gets **no** mode: inventing a power envelope for it would be worse
than not touching the EC.

### Writes never happen on the caller's (UI) thread

`WriteFeature` is a **synchronous, no-timeout** HID write on the **same HID-over-I2C bus as the RGB
controller**. A contended bus (external USB-C display, worst at boot) can block it for a long time. Doing that on
the UI thread freezes the app until the bus frees — which is exactly what was observed (frozen until the monitor
was unplugged). So all writes go through a **long-lived background writer thread**.

**Only the newest mode matters**, so the queue is a **single coalescing slot**, not per-region: a burst of
profile switches collapses to the last one. (Contrast `EneHidController`, which needs a per-region list because
different regions must each keep their latest state.)

Two further details:

- `Apply` is **fire-and-forget**: it only enqueues, so `true` means *"accepted for sending"*, **not** *"the EC
  applied it"*.
- The worker **drops the transport handle on a write failure** so the next write re-opens it. A handle opened in
  a bad state during boot-with-display would otherwise stay broken until restart. The worker never dies on an
  exception (`catch` keeps it alive).
- `Dispose` does a **bounded 1-second join**: a worker stuck inside a blocked write is a background thread and
  cannot keep the process alive, so it proceeds and lets `CloseTransport` unstick it — disposing the handle
  faults the pending write.

The `_gate` here is a plain `object`, **not** `System.Threading.Lock`: the worker parks on
`Monitor.Wait`/`Monitor.Pulse`, which `Lock` does not support. This gate is **pacing and coalescing, not
serialisation** — it must not be merged into the WMI EC gate (see `docs/wmi-interop.md` and the constraints in
`docs/open-decisions.md`).
