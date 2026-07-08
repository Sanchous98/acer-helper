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
`AppController.ReapplyLightingOnResume`; Windows `SystemEvents.PowerModeChanged`/`Resume`, more retries as the
HID/EC can be slow to wake). The keyboard-brightness read-back (`GetGamingKBBacklight`) is unreliable right
after an OPMODE flash — it reports 0 while the keyboard is lit — so the UI ignores a 0 read-back while it is
driving a non-zero brightness (`LightViewModel.SyncBrightness`).
Colour order on the wire is mode-dependent — **R,G,B** for arbitrary-colour writes (keyboard STATIC, lightbar),
**B,G,R** for the OPMODE profile-flash whitelist (see the `A4` layout note above). On Linux the identical `A4`
report goes via
`ioctl(HIDIOCSFEATURE)` / write to `/dev/hidrawN` for the `enek5130` device — no Acer services needed.
