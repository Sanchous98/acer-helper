# Acer Nitro 18 (AN18-61) — RGB lighting behaviour & control (reverse-engineered)

Reverse-engineered from NitroSense 5.1.392 (OpenRGB fork + `AcerECKeyboardController.dll` /
`AcerECLightbarController.dll` disassembly), API Monitor / Frida captures of the live stack, and
direct on-device HID/WMI/SMBIOS/ACPI probing. Goal: drive per-profile lighting from **AcerHelper**
standalone (no Acer services), cross-platform.

## Device

- Lighting MCU: **ENE / Darfon**, enumerates as HID `\\?\hid#enek5130#…` (I2C-HID; the path string is
  `enek5130`, *not* `vid_0cf2&pid_5130`). Single HID collection, **feature-report length = 11 bytes**.
- Two lighting **targets** on this one device (enumerated via report `0xA1` → reply `A1 02 65 21`):
  - `0x21` = **keyboard** (4 zones, zonemask `0x0F`)
  - `0x65` = **lightbar** (5 zones per `A3` caps `a3 65 01 05 01 1b`)

## HID protocol (all `HidD_SetFeature`, 11-byte feature reports)

| Report | Purpose |
|--------|---------|
| `A1` | list targets (reply: count + target ids). Init/handshake only. |
| `A2 [tgt]` | query/select target. Init/handshake only. |
| `A3 [tgt]` | get caps (zone count etc.). Init/handshake only. |
| `A4 …` | **set colour/mode — the runtime write.** |

**`A4` layout:** `A4 [target] [mode] [bri] [speed] [flag] c0 c1 c2 [zonemask] 00`
- colour byte order is **mode-dependent** (verified on-device):
  - **arbitrary-colour writes** (keyboard STATIC `A4 21 02`, lightbar `A4 65`) render the three bytes as
    **R, G, B** — a UI red (255,0,0) must go out `FF 00 00`; sending `00 00 FF` shows blue.
  - the **OPMODE profile-flash** (`A4 21 06`) is a separate firmware handler that recognises its palette in
    **B, G, R** (e.g. amber #C7AE00 → `00 AE C7`) and whitelists it. So the flash and the arbitrary-colour
    paths use *opposite* byte orders; `EneHidController.Send` emits R,G,B and `SetProfileFlash` emits B,G,R.
- fields are placed by the plugin at offsets read from the device's own HID report descriptor
  (`HidP_GetValueCaps`); on this unit the layout matches the table above and the report is padded to
  the descriptor length (11).

**Mode byte** (OpenRGB enum → wire byte, from the plugin's translation table):
- STATIC (enum 0x00) → wire **`0x02`**
- OPMODE (enum 0x22) → wire **`0x06`**   ← "operating mode" = the profile-switch flash
- Direct (enum 0xFF) → per-LED
The plugin treats the mode byte as opaque and writes brightness/colour identically for every mode.

## Two layers & who owns what

1. **STATIC (`A4 21 02`) — keyboard steady colour.** Accepts **arbitrary RGB**; holds standalone
   (verified: magenta set with no Acer services and held).
2. **OPMODE (`A4 21 06`) — GLOBAL operating-mode "flash".** One write on target `0x21` drives BOTH
   keyboard and lightbar. This is the ONLY channel that reaches the **lightbar** flash. (Writing to
   `0x65` for the flash does nothing — the lightbar follows the global `0x21` OPMODE.)
3. **Lightbar arbitrary steady colour** is only settable via `A4 65 …` applied **after** a profile
   switch (a short window), i.e. the delayed-write approach.

## OPMODE colour is a hard firmware WHITELIST (the per-profile palette)

`A4 …06…` only renders one of these 5 values; **any other RGB → firmware forces amber**:

| Profile | wire (BGR) | RGB |
|---------|-----------|-----|
| Quiet | `FF FF FF` | white |
| Eco | `10 DC 00` | green (0,220,16) |
| Balanced | `00 AE C7` | amber #C7AE00 (199,174,0) |
| Performance | `2E 09 C7` | red (199,9,46) |
| Turbo | `C7 00 FF` | purple (255,0,199) |

All 5 render regardless of the current profile; custom colours (magenta/cyan/orange) → amber.
**A custom (non-palette) flash colour is impossible.**

## Profile-switch flash & the per-profile palette (software-driven)

- Switching the performance profile (`SetGamingMiscSetting`, gmInput `0x0B | (profileByte<<8)`;
  Quiet 0x00, Balanced 0x01, Performance 0x04, Turbo 0x05, Eco 0x06) makes the EC default the OPMODE slot to
  **amber** on keyboard + lightbar. It also **wipes** any pre-switch `A4 21 06 <colour>`.
- The destination profile's palette colour is **NOT autonomous** — it is **software-driven**: NitroSense (and
  AcerHelper) send `A4 21 06 <destination profile's palette>` right after the switch, and *that* is what turns
  the flash into the profile's colour. With no software running, every switch just shows amber. (Verified: with
  the app not driving it, the lightbar stayed amber on every profile; sending the palette per profile changes
  it correctly.)
- So per-profile lightbar colour = re-send the profile's palette on each switch. Send it **immediately** after
  the switch, else the previous colour lingers for a beat and looks like a stray flash (AcerHelper does this in
  `AppController.ApplyFollowLighting`, called the instant the profile change is seen).
- The colour set is still a hard whitelist (§ *OPMODE colour is a hard firmware WHITELIST*): only the 5
  per-profile palette colours render — a custom (non-palette) flash colour is impossible. The whitelist is
  firmware; the *timing and which palette colour* are software. So the residual amber flicker during the switch
  transition can be minimised but not fully removed (NitroSense has the same brief transition).

## Effects: which honour a chosen colour

Only **Static** (`A4 21 02`) paints an arbitrary chosen colour. **Breathing** (`A4 04`) and every other animated
effect **cycle the firmware's own built-in palette and ignore the colour bytes** — there is no single-colour
breathing on this controller. Verified on AN18-61: mode `0x04` cycles regardless of the colour bytes, regardless
of byte[5] (`0x01` vs `0x02`), and even after pre-seeding the zone colour with a Static write and then switching
to Breathing. So in `RgbEffects` only Static is `hasColor:true`; Breathing/Neon/Wave/… are `hasColor:false` (the
UI shows no colour picker for them).

## Where the palette lives (BIOS ruled out)

The whitelist is enforced **inside the ENE controller firmware** (the reject/accept happens when the
controller receives the `A4` report — no BIOS/OS code runs at that instant, and the controller cannot
read BIOS tables). The palette RGB values are **absent from every software-readable BIOS source**:
- SMBIOS incl. OEM **type 172** (the table the lightbar driver actually reads — contains only
  capability bytes: `0E=05` lightbar zones, `05=0F` keyboard zones, type/flags — no colours).
- ACPI **DSDT** (40 KB) and all readable ACPI tables (FACP/APIC/UEFI/SDEV/PCCT/… + one SSDT).
- Not in the controller/service binaries or the NitroSense Electron `app.asar` (0 hits).

⇒ Palette is in **device firmware** (ENE controller, possibly seeded by the EC), not the BIOS.
(Unverified only: 34 of 35 SSDTs — GetSystemFirmwareTable can't index duplicate signatures — and UEFI
NVRAM vars; both irrelevant since enforcement is device-side.)

## NitroSense specifics (this model)

- OEM per-model config `…\WindowsApps\ULICTekInc.NitroSenseforNotebook_*\app\win32\Nitro\Nitro AN18-61.json`
  declares `Lighting:{keyboard:true, lightbar:false, logo:false, lightbar_rear:false}`.
- That gates only the **lightbar-target (`A4 65`)** path — so there's no lightbar UI and no `A4 65` writes.
  But NitroSense STILL sends the **global** operating-mode flash `A4 21 06 <profile palette>` (keyboard target)
  on each switch, and that paints the lightbar too. So NitroSense's per-profile lightbar colour is
  software-driven via the global OPMODE write, not an autonomous EC effect.

## What AcerHelper can do (standalone, Windows HID / Linux hidraw)

Governed by a single vendor-scoped flag, "Lightbar follows performance profile" (stored under the backend key
`acer.lightbarFollowsProfile` in the neutral `Settings.DeviceSettings` bag; surfaced via `IRgbDevice.ProfileFollowKey`):

- **Keyboard:** always a user zone — any per-profile custom colour via `A4 21 02 64 00 01 B G R 0F 00` (STATIC).
- **Lightbar, follows-profile ON (default):** shows the destination profile's **palette** colour, sent as the
  global `A4 21 06 <palette>` re-applied immediately on each switch (exactly what NitroSense does). Limited to
  the 5 palette colours; no lightbar tab (colour is the profile's, not user-chosen).
- **Lightbar, follows-profile OFF:** a user zone — custom colour/effects via `A4 65 …` applied after the switch
  (arbitrary colour, but with the palette flash before it and less certain timing).
- **The switch transition flash can be minimised (send the palette immediately) but not fully removed** — the
  EC defaults the slot to amber on every switch; NitroSense has the same brief transition.

Implementation: `AppController.ApplyFollowLighting` sends `IRgbDevice.SetProfileFlash(profile.FlashColor)` then
re-applies the per-zone colours, called the instant a profile change is seen (+ a couple of safety re-applies).
Sleep/hibernate clears the EC's RGB state, so the same re-apply runs on wake too (`ResumeWatcher` →
`AppController.ReapplyLighting`; Windows `SystemEvents.PowerModeChanged`/`Resume`, more retries as the
HID/EC can be slow to wake). The keyboard-brightness read-back (`GetGamingKBBacklight`) is unreliable right
after an OPMODE flash — it reports 0 while the keyboard is lit — so the UI ignores a 0 read-back while it is
driving a non-zero brightness (`LightViewModel.AdoptBrightness`).
Colour order on the wire is mode-dependent — **R,G,B** for arbitrary-colour writes (keyboard STATIC, lightbar),
**B,G,R** for the OPMODE profile-flash whitelist (see the `A4` layout note above). On Linux the identical `A4`
report goes via
`ioctl(HIDIOCSFEATURE)` / write to `/dev/hidrawN` for the `enek5130` device — no Acer services needed.

## Clamshell keep-awake: blank the backlight while the lid is shut

When "Stay awake when lid closed" (clamshell) is enabled the machine stays on with the lid shut, but the
keyboard/lightbar are then hidden — pointless light + heat. So while clamshell is enabled the app blanks the
backlight on lid-close and restores it on lid-open. `LidWatcher` (Windows: a hidden top-level window +
`RegisterPowerSettingNotification(GUID_LIDSWITCH_STATE_CHANGE)`; a message-only `HWND_MESSAGE` window does **not**
receive `WM_POWERBROADCAST`, hence a real window) raises open/closed; `AppController.OnLidChanged` (gated on
`Clamshell.Enabled`) calls `IRgbDevice.Blank()` on close and `ReapplyLighting()` on open. `Blank()` darkens the
keyboard with a STATIC write at brightness 0 (the STATIC path honours the brightness byte — unlike the OPMODE
flash, which merely zeroes the WMI read-back register while the keyboard stays lit) and the lightbar with a black
STATIC colour (`A4 65`, since the lightbar ignores the brightness byte). It writes hardware only — the stored
per-zone state is untouched, so lid-open restores the exact previous look. When clamshell is off a lid-close just
sleeps the machine, so the lid handler no-ops and the normal resume re-apply handles the wake. Linux is a no-op
(clamshell keep-awake itself is unsupported there — see `Clamshell.Linux.cs`).

### Why the lid watcher creates a real (hidden) window

`RegisterPowerSettingNotification(GUID_LIDSWITCH_STATE_CHANGE)` delivers a `WM_POWERBROADCAST` /
`PBT_POWERSETTINGCHANGE` to the registered window whenever the lid opens or closes — **and it fires even when the
lid-close power action is "do nothing"** (clamshell keep-awake), which is exactly the case we care about.

The notification is delivered **targeted** to the registered `HWND`, not via the legacy top-level power
broadcast — so it would *very likely* reach a message-only (`HWND_MESSAGE`) window too. But Windows only
**documents and guarantees** `WM_POWERBROADCAST` delivery to **top-level** windows; targeted delivery to
message-only ones is not documented. So the watcher **defensively creates an ordinary top-level window that is
never shown**:

- a top-level window receives **both** the broadcast and the targeted notification — a strict superset of a
  message-only window's behaviour;
- `WS_EX_TOOLWINDOW` keeps it out of the taskbar and Alt-Tab;
- it is created on the **UI thread** so Avalonia's Win32 message loop dispatches its messages.

The notification's `Data` is a DWORD: `0` = lid closed, `1` = lid open.

## The re-apply regime: ticks, the double blink, and the self-heal

A profile switch does two things to the lighting at once: it makes the firmware repaint the **palette flash**
across keyboard + lightbar, which clobbers the per-zone colours, and (on a contended HID-over-I2C bus) the app's
own first apply can land **corrupted** — the *"half green / half orange"* or amber-fallback failure. So a kick
schedules a **bounded burst of re-applies**, not one.

- **`ReapplyTicks = 8`** at the **400 ms** interval ≈ **3 s** of retries. Two jobs: (1) restore the per-zone
  colours the firmware's own palette repaint clobbers, and (2) **self-heal** a corrupted apply — each retry is a
  fresh chance to hit a clean bus window, and **once one lands it sticks** (the device is last-write-wins; the
  idle state isn't re-corrupted). Bounded on purpose: if the bus is corrupting **constantly** (no clean window)
  this retries for ~3 s and stops, rather than flickering forever. Re-asserting an already-correct colour is
  **visually silent** — the firmware re-latches the same value.
- **`FlashTicks = 2`** of those ticks also **re-send the profile palette flash**. The flash is a **global** write
  that briefly repaints the whole keyboard with the palette colour before the per-zone paint overrides it — one
  more visible blink of keyboard and lightbar. That cost is worth paying on the **restore paths** (startup,
  resume, lid open, host hand-back), where nothing else re-establishes the palette and the bus may be contended.
  A **profile switch does not** pay it: the firmware flashes the new palette itself at the moment of the write,
  and the app now sends its own in the **same instant**, so a re-send 400 ms later is just a **second blink
  cycle**. The per-zone **keyboard** paint (the actual self-heal) still runs on **every** tick, which is silent
  when already correct.

### Why applying on the switch instant matters (the double blink)

Applying used to **wait for the refresh pass to discover the change by polling**. That produced a **double
blink**: the firmware flashes the new palette the instant the profile byte is written, and the app's own palette
write then landed **~750 ms later** as a second, separate flash cycle. Painting at the switch instant puts both
writes in the same moment so they **coincide into one** — and the burst kicked there deliberately carries **no**
further palette re-sends, only the per-zone self-heal.

## How the app drives the ENE controller — implementation notes

`EneHidController` is a **cross-platform codec** (the `A4` packet plus the zone model) with per-OS transport
partials. It is the **same controller OpenRGB drives**: `VID 0x0CF2` / `PID 0x5130`, **11-byte feature reports,
report id `0xA4`**. The packets are identical on every OS; only the transport differs.

- **Windows**: HidSharp (Win32 HID API).
- **Linux**: **hidraw directly** — HidSharp's Linux enumeration only sees **USB** HID, and on several models
  (e.g. the Nitro AN18-61) this controller hangs off **HID-over-I2C**.

Regions are exposed as `RgbZone` bricks: `"Keyboard"` (multi sub-zone) and, on models that have it, `"Lightbar"`.
**Keyboard brightness read-back is not on this HID interface** — it is the gaming WMI's job (Windows only) — so
that reader is **injected by `AcerDevice`** and is `null` on Linux. The stream is opened lazily; the class is
`IDisposable`.

Constants worth knowing (they are the wire, and the zone masks are per-target):

```
ReportId 0xA4   TgtKeyboard 0x21   TgtLightbar 0x65   OpMode 0x06
FlagStatic 0x01   FlagEffect 0x02   FullBright 0x64
keyboard zones 4 (mask 0x0F = all)     lightbar zones 5 (mask 0x1F = all)
```

### Report byte 5 is the effect DIRECTION

For a **directional** effect (e.g. Wave) it is the user's choice (`1`/`2`). Otherwise it is the **mode default
the firmware expects**: `0x02` for animated effects, `0x01` for static.

### The lightbar ignores the brightness byte

Unlike the keyboard, the lightbar does **not** honour the HID brightness byte. Brightness is therefore emulated
by **scaling the colour** and sending at full brightness (`0x64`). That works for colour modes; self-cycling
effects generate their own colours and are unaffected.

### `Blank()` while the lid is shut

The keyboard honours the brightness byte, so a STATIC write at **brightness 0** darkens it. The lightbar ignores
that byte, so it is darkened with a **black STATIC colour** instead. A **follows-profile** lightbar (which has no
lamp/panel of its own) is included too — its palette is repainted from the profile flash on the restore.
`Blank()` writes hardware only; **stored state is untouched**, so lid-open restores the exact previous look.

### `SetProfileFlash()` does not route through `Send()`

The performance-profile "operating mode" flash is a **global** write (keyboard target `0x21`, mode `0x06`) that
paints **both** the keyboard and the lightbar at once. Unlike the arbitrary-colour paths — which the firmware
renders **R,G,B** — the OPMODE handler recognises its per-profile palette in **B,G,R** and whitelists it; anything
else reverts to amber. So this path emits the palette colour in B,G,R **directly**, reproducing byte-for-byte what
NitroSense sends, and **is not routed through `Send()`** (which applies the R,G,B arbitrary order).

### Serialised background writer, and the 10 ms pacing

Every feature report is enqueued and written by **one long-lived worker thread**, never the caller's (UI) thread.
`WriteFeature` (the per-OS transport) is a **synchronous, no-timeout** HID write that can block hard on a contended
HID-over-I2C bus; off the UI thread that stall freezes only the worker, so the app stays responsive. **Doing it on
the UI thread froze the whole app until the bus freed** (observed when a monitor was unplugged).

- `SetFeature` is **fire-and-forget**: it only enqueues.
- Writes **coalesce by region** so a stalled worker can't accumulate — and won't *replay* — a flood of stale
  writes: when the bus frees it applies just the **latest state per region**. A superseded same-region write is
  **moved to the tail** rather than dropped in place. (There is a hard cap of 32 pending as a backstop only;
  coalescing keeps it to a handful.)
- A **single in-flight write** is also the de-facto circuit breaker — the worker cannot issue a second write while
  one blocks — which keeps the app's bus contention minimal.

**Pacing: `PacingMs = 10`** between consecutive feature reports, on the worker only (it can never stall the UI
thread). A full keyboard apply is several back-to-back reports (profile-flash + per-zone paints + lightbar). On a
HID-over-I2C bus that an externally-booted display is contending, a **tight burst tends to land some reports
corrupted** (amber fallback) and others clean — the *"half green / half orange"* failure. Spacing the reports
**decorrelates** them so they don't all fall inside one contention window.

Be honest about what that buys: pacing **cannot phase-lock** to the display's traffic, it only randomises phase —
so it **reduces the odds of a fully corrupt apply rather than guaranteeing a clean one**. `5 reports × 10 ms`
stays well under the **~120 ms** apply debounce, so a normal apply is still visually instant. `0` disables it.

This gate is **pacing and coalescing, not serialisation**, and must not be merged into the WMI EC gate (see
`docs/wmi-interop.md` and the constraints in `docs/open-decisions.md`).
