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
re-asserts it rather than reconciling it (see `LaptopService.ReassertProfile`).

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

* It is **not known** to survive a reboot, which is why `ApplyStartupState` re-asserts the profile.
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
