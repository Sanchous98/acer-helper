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

Implementation: `AppController.ApplyFollowLighting` sends `IRgbDevice.SetProfileFlash(<the profile's flash
colour>)` then re-applies the per-zone colours, called the instant a profile change is seen (+ a couple of safety
re-applies). The colour is looked up per profile from the backend's own table since 2026-09-22
(`LaptopService.FlashColorOf`, `ProfileTraits`) — it used to be a field on `PerformanceProfile`, which no longer
carries it or the profile's class.
Sleep/hibernate clears the EC's RGB state, so the same re-apply runs on wake too (`ResumeWatcher` →
`AppController.ReapplyLighting`; Windows `SystemEvents.PowerModeChanged`/`Resume`, more retries as the
HID/EC can be slow to wake). The keyboard-brightness read-back (`GetGamingKBBacklight`) is unreliable:
after an OPMODE flash it reports 0 while the keyboard is lit. A read is used in exactly one place — the
out-of-band Fn-key event (`LightViewModel.AdoptFromHardware` → `AdoptBrightness`), where it is an author of
intent like the slider is. Everywhere else the app's STORED value is authoritative: a CONFIGURED zone's
brightness is neither read at construction nor overridden by a read (docs/state-and-events.md — a read is not
an author of intent), and the read only seeds an UNCONFIGURED zone (a fresh install), which applies nothing.
The old construction path let the register win, which is why a zone stored at 100 could show a slider at 0 and
drive the keyboard dark, and why a profile switch appeared to apply the wrong saved look.

### A genuine Fn dim to 0, and the flash's spurious 0 (2026-09-29)

The register lying is a real hazard on a Fn-key read, but the guard must not confuse it with the user's own
action. The owner's report: **dimming the keyboard with the Fn brightness key stopped the slider at 25% and it
never reached 0.** The mechanism was that the refusal keyed on a STATIC capability predicate,
`profileFlashPossible = () => followZones.Count > 0 && FollowsProfile` — "this machine's register CAN lie".
That is permanently TRUE on the AN18-61 (it has a lightbar that follows the profile), so it refused EVERY
read-back 0 with `Brightness > 0`, including a genuine monotonic Fn dim `100 → 75 → 50 → 25 → 0` (each step is
a real read of the new level, and 0 is the final real step). The capability answers the wrong question: the
OPMODE flash zeroes the register only **at the switch**, in a short window — it is not a property of the device.

The discriminator is the flash **EVENT**, not the capability. `LightingCoordinator.Paint` calls
`LightingViewModel.NoteProfileFlash()` **immediately before** it sends the palette, arming a suspicion on every
panel, and `LightViewModel.AdoptBrightness` refuses a read-back `0` over a lit keyboard **only while that
suspicion is fresh** (a 5 s window, `FlashSuspectSeconds`; the same "short window" the coordinator already uses
for `PendingTimeoutSeconds`/`WakeTailSeconds`). A **non-zero read clears it early**, because the register only
lies at the switch and the first real Fn step proves it live again. So:

- a **monotonic Fn dim to 0** (no palette sent) lands at 0 — slider and stored value;
- a **profile-switch flash's 0** (over a lit keyboard, inside the window) is refused — the app keeps its stored
  brightness and does **not** persist the lie;
- a **later genuine dim to 0 after a flash** lands once the window has passed (or as soon as any non-zero read
  clears the suspicion), so the decrease control is never dead.

The guard is NOT deleted: the spurious-0 protection the flash needs is kept, only its trigger moved from a
permanent device fact to the event that can actually produce the lie. Pinned in `LightingAdoptionTests` (the Fn
dim table, the refused in-window 0, the window expiry, the early clear) and, for the arming order, by the source
guard `TheFlashArmsTheSpuriousZeroSuspicion_BeforeThePaletteLands` in `LightingReapplyFlashTests`.
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
- **The burst never re-sends the palette.** The palette flash is a **global** write that briefly repaints the
  whole keyboard with the palette colour before the per-zone paint overrides it — one visible blink of keyboard
  **and** lightbar. Every path that wants the palette sends it **ONCE**, at the action instant (a restore paints
  at startup/resume/lid-open; the follows-profile flip paints on the flip; an out-of-band profile change paints
  from the pass). A burst tick re-asserts only the per-zone colours, which is silent when already correct. An
  earlier shape re-sent the palette on the first couple of ticks, which turned **every** restore and the
  follows-profile flip into **two** blinks — the second write landing 400 ms after the first. The rule is one
  flash per action; the per-zone self-heal is the safety net the burst keeps.

### Why applying on the switch instant matters (the double blink)

Applying used to **wait for the refresh pass to discover the change by polling**. That produced a **double
blink**: the firmware flashes the new palette the instant the profile byte is written, and the app's own palette
write then landed **~750 ms later** as a second, separate flash cycle. Painting at the switch instant puts both
writes in the same moment so they **coincide into one** — and the burst kicked there deliberately carries **no**
further palette re-sends, only the per-zone self-heal.

### The switch instant rebinds to the LANDED mode (the stored-zero that stayed lit)

The switch instant is only correct if the paint that follows the palette flash carries the **target** mode's
stored zones. `OnProfileApplied` fires in the same instant as the port write, when the refresh poll has **not**
read the new mode's door yet — so the lighting section (`LightingViewModel`) is still bound to the **previous**
mode. The original shape repainted the section's cached `_lights` (`LightingViewModel.Repaint`), which are the
mode being LEFT, so the per-zone write that followed the flash carried the **old** brightness and the
palette-free burst kept re-asserting it. The owner's report «сейчас у меня Эко режим сохранил яркость подсветки
на нуле, но при смене профиля подсветка горит» is exactly this: the target profile (Eco) was saved at keyboard
brightness **0**, but switching to it left the keyboard **lit**, because the write after the flash sent the
previous mode's brightness and the stored 0 only reached the port a poll later — if at all.

The fix is to **rebind** rather than repaint: `OnProfileApplied` resolves the landed profile's own door
(`LaptopService.LightsForCurrentMode(applied)` — the overload that takes an already-read profile, so it costs no
EC read) and paints through it, so the sequence is **flash (palette) → zone repaint at the TARGET profile's
stored brightness** (0 → dark), and the burst that follows keeps the target's values instead of the old mode's.
The follow-up `OnStateChanged` pass that finally reports the same profile is still consumed by the `_pendingId`
claim (it clears the claim and rebinds to the same door, idempotently), and a stale pass describing the previous
profile is still dropped — so there is still exactly **one palette flash per action**. The announcer's UI-thread
marshaller is an injected delegate (`uiPost`) so a test can drive the whole path synchronously; the app passes
`Dispatcher.UIThread` (inline on the UI thread, posted from the pool).

### The burst must not clobber a brightness edit in flight (the slider that would not persist)

The owner's other report — «настройки яркости не пишутся»: moving the keyboard-zone brightness slider did not
persist, while the profile-switch memory did. A live watch of `settings.json` while switching profiles and
dragging the slider to 0 showed the source slot changing but **no `LightPresets` brightness ever**:

```
19:32:49 OnAc=1/False k0=100 k1=100 k4=100 k6=100   (Balanced; k1 should have become 0)
```

The mechanism is a **race the burst wins**, and it is NOT the read. A slider move only arms a **120 ms
debounce** (`LightViewModel.Schedule`); the value is applied and saved on the tick (`ApplyDebounced →
ApplyNow + SaveState`), and `LightingViewModel._lights[zone]` is updated only then (`Store`). The post-switch
re-apply burst (`LightingCoordinator.ReapplyTick`) runs every 400 ms for ~3 s after every switch, and the owner
was switching profiles right before editing — so a burst tick landed inside the 120 ms window. The old
`Repaint` called `LightViewModel.Rebind(_lights[panel.Title])` — the **last committed** value — which overwrote
the user's 0 on the slider and set `_loading = true`, which suppressed the edit's own `Schedule()`. The pending
tick then applied and **persisted the stale 100**: the 0 was reverted before it was ever written. `Repaint`'s
old comment ("reads nothing, so the burst is safe to run mid-edit") was false for exactly this case: it does not
*read* the graph, but it *rebinds* from the section's own copy, which is behind the user.

The fix is the same guard the read path already keeps (`AdoptBrightness`: "the hardware read raced ahead of the
apply"), moved onto the re-apply path. `LightViewModel.Repaint(state)`:

- **edit pending** (`_debounce.IsRunning`) → re-apply the panel's OWN current values (`ApplyNow`) and **leave the
  controls alone**; the pending tick applies and saves the user's value, so the burst can no longer revert it;
- **nothing pending** → `Rebind(state)` exactly as before, which re-applies a committed/adopted value and keeps
  the "a mode's first sight of a zone becomes configured" rule.

The distinction between "adopt the target mode" and "do not touch the pending edit" is made by the **call**, not
guessed from the values: a genuine mode change is `LightingViewModel.Reload` → `LightViewModel.Rebind`, which
**still adopts** the target's stored values even with an edit in flight (a mode change is a new intent), and
`Rebind` now stops the superseded debounce so the old edit cannot tick later and save its stale capture over the
target. Repaint is only ever the SAME-mode re-apply. Pinned in `LightingInFlightEditTests` (the repro plus the
mode-change differential).

### The two remaining double blinks, and how they are closed

The switch instant fixed profile switches; two paths still blinked twice, each by a different mechanism:

- **Resume.** `OnResume` paints the palette once at the wake instant. The refresh pass that follows the wake
  re-reads the same profile and **reports it changed** — a second palette send ~1 s later. A pass within the
  wake window that carries the **same** palette the wake just painted is the wake's **tail**, not a second
  action: it binds the mode's zones and sends no palette (`LightingCoordinator.IsWakeTail`). A pass carrying a
  **different** palette is a genuine out-of-band restore and keeps its one flash.
- **Lightbar mode switch (the follows-profile toggle).** The flip painted the palette and then kicked a burst
  that re-sent it on its first ticks — the same second blink, 400 ms later. With the burst palette-free the flip
  is a single palette send.

### The one switch use case (the 2–3× flash)

The owner's later report — the palette flashing **2–3×** during a guided undervolt sweep and on resume — was a
third instance of the same class, with a structural cause rather than a second timing bug. The sweep temporarily
forced a performance profile for the run and restored it afterwards. It did the port write with a **private**
transient method (`LaptopService.ApplyProfileTransient`) that bypassed `LightingCoordinator.OnProfileApplied` —
the call that records `_pendingId` so the ~1 s refresh poll does not repaint the palette the firmware has already
flashed. So the sweep flashed the palette on the force, the poll repainted it (~1 s later), the restore flashed
it again, and a manual Turbo on top made three.

The port write and the lighting claim were **two separate steps the sweep could take only one of**. The fix makes
them one indivisible action: `Application/ProfileSwitch.cs` (`SwitchProfile`) now owns **both**, and every profile
switch routes through it — `ApplyProfile`, `SetTurbo`, `TogglePerformance` and the (since-removed) sweep's
force/restore alike.
The announcement is a second Application contract, `IProfileAnnouncer`, implemented by `LightingCoordinator`
(which marshals to the UI thread, since a background switch can run off it); the profile target is `IProfileTarget`,
implemented
by `LaptopService`. Because the use case owns both halves, there is no path by which a switch can write the port
and skip the claim. **2026-10-07: the guided sweep was removed outright** (it could not reliably find a stable
voltage), and with it the transient force/restore — but `SwitchProfile` remains the one door for every profile
write, so the property it protects (one write → one announcement) still holds for the paths that are left.

### The power-source change on resume (the old-profile-then-new-profile flash)

The owner's next report — **on resume from sleep, if the power source changed, the palette flashes twice: once
for the old power profile, once for the new** — was the same class again, on the last port write that still
bypassed the switch use case: the per-source restore (`LaptopService.ApplyStoredMode`), reached from
`SyncPowerSource` on any AC↔battery/USB-C change **and** from the power-source row. It had two independent
faults, and both are needed to explain the two flashes:

- **The intermediate base write.** Restoring a source whose remembered mode was **Turbo over a base** did
  `pp.Set(base)` and then `pp.Set(turbo)` as two unconditional port writes. Each Acer `Set` makes the firmware
  re-flash the palette, so the machine visibly **dropped out of Turbo and back** — the first ("old profile")
  flash, then the target's. The base under Turbo is **slot bookkeeping** (`BaseProfile` reads it to know what
  Turbo sits over); it is not a precondition the EC needs, because `AcerDevice.Windows.SetProfile` writes the
  Turbo byte directly (`SetGamingMiscSetting` selector `0x0B` with the byte, plus the EC envelope). So there was
  never a platform reason for the base write at all, and it is **removed** rather than coalesced — one write, one
  repaint, the target's.
- **The unannounced write.** Neither `pp.Set` went through `SwitchProfile`, so nothing recorded the light claim
  (`_pendingId`) and the ~1 s refresh pass repainted the palette the firmware had already flashed — the same
  second-blink mechanism already closed for picks and the sweep.

The restore now goes **through the switch use case as a TRANSIENT** (`ProfileSwitch.Run(profile, transient:
true)`): the port write and the announcement are one action, so the landing is announced (the app paints once, at
the switch instant) and the stale pass is suppressed — while `transient` keeps it from re-remembering the mode
(the slot already holds it; `SetSourceProfile` is the door that remembers a choice). The "already in that profile
→ do nothing" guard is kept, so a same-mode source change stays flash-free.

The **wake** is where the owner saw it, and `LightingCoordinator.OnResume` is part of the fix: sleep suspends the
polling, so the source may have moved over the sleep and the refresh pass would only discover it a poll later.
`OnResume` therefore **re-syncs the power source first** (through the same `SyncPowerSource` → `ApplyStoredMode`
use cases the refresh pass uses), **off the UI thread** (the restore may write the EC, which can stall right after
wake). When that restore announces a landing, `OnProfileApplied` has already painted the **new** palette and set
`_pendingId`, so the wake's own paint carries **only the per-zone colours** — no cached **old** palette is sent
over the profile the restore just painted. When the source did not change, the restore writes nothing, nothing is
announced, and the wake paints its one palette exactly as before. The wake-tail suppression (`IsWakeTail`) that
coalesces the refresh pass after a wake is unchanged. One palette repaint per wake, whichever way the source
went.

### Repeated commands to the SAME target are deduplicated at the write seam (the plug-in-then-pick flash)

The owner's next report — «подключил зарядку и быстро поменял на turbo режим и подсветка моргнула дважды. Было бы
неплохо иметь дедупликацию команд» — was a different mechanism from the four above: the palette was not flashed
by the poll, but by a **second local write of the same target**. Plugging in the charger triggers the per-source
restore, which may already switch to the remembered Turbo; a rapid manual pick of Turbo then re-sends the SAME
profile. The only dedup that existed was the "already in that profile" guard in `LaptopService.ApplyStoredMode`,
which reads the **port** — and between two local writes the ~1 s battery / 3 s full refresh pass has not caught up,
so the port still reports the old profile (the "stale read" the `_pendingId` claim already suppresses on the
*lighting* side, but which the *write* path never had a record for). Each Acer profile `Set` makes the firmware
repaint the palette, so two switches to the same target produced two flashes for one intended state.

The fix is at the profile-write seam, `Application/ProfileSwitch.cs`: `SwitchProfile.Run` records the profile it
**last WROTE** (`_lastApplied`, keyed on the resolved id the port returned) and coalesces a second switch to that
same target within **`CoalesceWindowSeconds = 5` s** — the port is not written again and **nothing is announced**,
so the palette flashes once; the caller still gets the already-applied profile as a **success** ("already in it"),
never an error. 5 s is the poll latency the local writes outrun (~3 s full pass), and it is deliberately the same
number the coordinator already uses for `WakeTailSeconds`/`PendingTimeoutSeconds`, so the write seam and the
light-claim seam agree on what "a short window" is; it is short enough that a deliberate later re-switch still
writes. The record is made **when the write is taken**, never on a read.

**The rule cannot swallow a needed write.** It is keyed on the target: a switch to a DIFFERENT profile always
writes, which is what keeps the sweep's force/restore — two different targets — intact, and a same-target restore
was already a port-guard no-op before this. An absent port stays a value (the capability gate answers before the
dedup is consulted) and a present port's refusal still throws. It covers every writer — user pick, tray, hotkey,
the Turbo switch, the sweep's transient force/restore and the per-source restore — because they all route through
the one `SwitchProfile` use case. The regression timeline (restore-to-Turbo then an immediate manual Turbo pick:
TWO `Set` calls / two flashes before, ONE after) is pinned in `ProfileWriteDedupTests`.

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
