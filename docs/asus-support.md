# ASUS support: plan, Acer↔ASUS parity map, phase breakdown

Status: **Phases 1–4 implemented** (Linux/asusd + Windows ATK ACPI). Runtime is unverified on ASUS hardware for
both OS paths; the remaining gaps are listed at the end of §5.
This document is the durable context for the remaining phases: it records what exists on the Acer side,
how the ASUS integration target maps onto it, and what each later phase is allowed to touch.

> Working language: this file is English (the code's comments are English). The Acer reference study is
> `docs/acer-linux.md` — read it before touching any Linux backend; almost every number there is
> kernel/DMI-specific and must be re-derived, not copied. The ASUS numbers in this file come from the asusd
> source (`rog-platform/src/platform.rs`, `rog-dbus/src/zbus_platform.rs`, `asusd/src/ctrl_platform.rs`)
> and the recon in the Phase-1 brief; re-verify against the installed asusd before trusting them.

---

## 1. Integration target (Linux): the asusd / asusctl stack

asusd is an optional user-space daemon. **When it is absent the machine must read exactly as the generic
backend leaves it** — that is the whole fallback design, not a nicety.

| Fact | Value |
|---|---|
| System-bus name | `xyz.ljones.Asusd` |
| Object base path | `/xyz/ljones` |
| Presence test | `busctl --system status xyz.ljones.Asusd` (exit 0 = owned now) |
| `xyz.ljones.Platform` | properties `platform_profile`, `platform_profile_choices`, `charge_control_end_threshold` (20..=100), `version`, `supported_properties()`; methods `next_platform_profile()`, `one_shot_full_charge()`, `supported_properties()` |
| `xyz.ljones.Platform` profile wire form | **two generations**: current asusd declares `PlatformProfile` with `#[zvariant(signature = "u")]` → `u`/`au` numeric (0 Balanced, 1 Performance, 2 Quiet, 3 LowPower, 4 Custom); older builds exposed strings (`s`/`as`): `quiet`, `balanced`, `performance`, `low-power`, `custom` |
| `xyz.ljones.FanCurves` | per-profile custom fan curves: `fan_curve_data(profile)`, `set_fan_curve`, `set_fan_curves_enabled`, `set_profile_fan_curve_enabled`, `set_curves_to_defaults`. **Phase 2 read; Phase 3 writes (consent-gated)** |
| `xyz.ljones.Backlight` | **display** backlight (`primary_brightness` 0..100, i32) + screenpad properties. **Phase 2 port implemented, NOT wired** (wrong slot for keyboard brightness) |
| `xyz.ljones.Aura` | per-device RGB: `brightness` (LedBrightness u32), `led_mode` (AuraModeNum u32), `led_mode_data` (AuraEffect struct `(uu(yyy)(yyy)ss)`), `supported_brightness`/`supported_basic_modes`/`supported_basic_zones`, `all_mode_data()`, `direct_addressing_raw()`. Objects live at `/xyz/ljones/Aura/<per-device>`. **Phase 2 wired** |
| `xyz.ljones.AsusArmoury` | one object per attribute at `/xyz/ljones/asus_armoury/<name>`: `name:s`, `available_attrs:as`, `current_value:i`, `default_value:i`, `min_value:i`, `max_value:i`, `scalar_increment:i`, `possible_values:ai`, `queued_gpu_value:i`; write via `set-property current_value i`; methods `restore_default()`, `apply_queued_gpu_value():b`. Kinds: **Gpu = queued** (`gpu_mux_mode`, `dgpu_disable`, …), **Ppt** (PPT/TDP, `nv_dynamic_boost`, `nv_temp_target`), **ReadOnly** (`nv_base_tgp`), Immediate/Bios. **Phase 3 implemented, consent-gated, unwired** |

**Do not add a D-Bus NuGet.** The project's D-Bus pattern is `busctl` over the system bus
(`Infrastructure/Vendors/Generic/PowerProfiles.Linux.cs` → `internal static class Busctl`), with signals read
via `gdbus monitor` (`Infrastructure/ResumeWatcher.Linux.cs`). `Tmds.DBus.*` is Avalonia-transitive only and
must never be referenced directly.

**Generic fallbacks already cover the non-asusd ASUS machine** (do not duplicate them):
`SysfsPowerProfiles` (`platform_profile`), `SysfsChargeLimit` (`charge_control_end_threshold`),
`HwmonSensors`/`HwmonFanControl` (fans/temps), `SysfsKbdBacklight`. asusd ports must only *replace* these,
never stand in front of a working generic port half-built.

---

## 2. Acer vendor surface → app wiring (the parity map)

Every capability the Acer backend provides, where its Domain contract/model lives, its Infrastructure
files, and its UI/VM surface. This is the checklist the ASUS phases work down, so "parity with Acer" means
"each row below has an ASUS implementation or a documented reason it cannot".

| Capability | Domain port / model | Infrastructure files | UI / VM surface |
|---|---|---|---|
| Performance profiles | `IPowerProfiles`, `IProfileTraits`; `PerformanceProfile`, `ProfileKind`, `ProfileTraits` | Generic: `Generic/PowerProfiles.Linux.cs` (`PpdPowerProfiles`, `SysfsPowerProfiles`, `Busctl`), `PowerProfiles.Windows.cs` (`OverlayPowerProfiles`), `ProfileKind.cs`, `DelegatePorts.cs` (`ProfilesPort`). Acer: `AcerProfiles.cs`, `AcerProfilePorts.cs` (`AcerMappedProfiles`, `EcSyncedProfiles`), `AcerDevice.{Linux,Windows}.cs`, `AcerEcHidController.cs` | `UI/ViewModels/ProfilesViewModel.cs`; tray; `LaptopService.Profiles.cs`; Options (per-source rows) |
| Fan telemetry (RPM) + temps | `ISensors`, `SensorSnapshot`, `FanReading` | Generic: `Generic/Hwmon.Linux.cs` (`HwmonSensors`). Acer: `AcerFanPort.cs` (`AcerFanChannels`, `AcerSysInfoSensors`, `AcerTemperatureHold`), `AcerDevice.Linux.ReadAcerSensors`, `AcerDevice.Windows.Sensor` | `UI/ViewModels/MonitorViewModel.cs` |
| Fan control (mode + custom speeds) | `IFanControl`, `FanCapability`, `FanMode`, `FanModes` | Generic: `HwmonFanControl` (`Hwmon.Linux.cs`), `DelegatePorts.cs` (`FanPort`). Acer: `AcerFanPort.cs` (`AcerFanPort`, `AcerFanEnable`, `AcerFanDuty`, `AcerHwmonChip`), `AcerDevice.{Linux,Windows}.cs` | `UI/ViewModels/FansViewModel.cs`; `LaptopService.Fans.cs`; `Domain/FanCurveEngine.cs` |
| Battery telemetry | `Battery` (Domain object), `BatteryInfoSnapshot` | Generic: `BatteryInfo.cs` + `BatteryInfo.{Linux,Windows}.cs` | `UI/ViewModels/BatteryViewModel.cs` |
| Battery charge limit | `Battery.ChargeLimit` (`BatteryToggle`) | Generic: `BatteryChargeLimit.Linux.cs` (`SysfsChargeLimit`). Acer/Windows: `AcerBattery.Windows.cs` (`BatteryWmi`), `AcerDevice.Windows.cs` | `UI/ViewModels/BatteryViewModel.cs` |
| Battery calibration | `Battery.Calibration` (`BatteryToggle`) | Acer/Windows: `AcerBattery.Windows.cs` | `BatteryViewModel.cs` |
| Battery charge modes | `Battery.ChargeMode` (`BatteryChoice`) | Dell only so far (`Dell/DellDevice.Linux.cs`) | `BatteryViewModel.cs` |
| Keyboard backlight (plain) | `IKeyboardBrightness` | Generic: `KeyboardBrightness.Linux.cs` (`SysfsKbdBacklight`). Acer: brightness read-back injected into the RGB zone (Windows WMI; Linux none) | `UI/ViewModels/LightingViewModel.cs`; Options |
| RGB lighting | `IRgbDevice`, `RgbZone`, `RgbModeInfo` | Framework: `Domain/Rgb.cs`, `Infrastructure/Lighting/RgbController.cs`. Acer: `EneHidController.{cs,Linux,Windows}.cs`, `RgbEffects.cs`, `AcerModel.cs`, `AcerDevice.WireRgb` | `UI/ViewModels/LightingViewModel.cs`; `LightingDrawer` |
| LCD overdrive | declared `FlagSetting` (`lcd_override`), `IFlagPort` | Acer/Windows: `AcerDevice.Windows.cs` | Options drawer |
| Keyboard-backlight timeout | declared `FlagSetting` (`backlight_timeout`), `IFlagPort` | Acer/Windows: `AcerDevice.Windows.cs` | Options drawer |
| USB charging | declared `ChoiceSetting` (`usb_charging`), `IChoicePort` | Acer/Windows: `AcerDevice.Windows.cs` | Options drawer |
| Hotkeys (Turbo/Nitro) | `IHotkeys`, `HotkeyAction` | Acer: `AcerHotkeys.{Linux,Windows}.cs`, `AcerHotkeyReports.cs`, shared HID id in `AcerEcHidController.cs` | `UI/AppController.cs` |
| dGPU clock overclock | `IGpuOverclock` | Generic: `NvidiaGpu.{Linux,Windows}.cs`, `NvidiaGpuPolicy.cs` | `UI/ViewModels/GpuViewModel.cs`; `LaptopService.Tuning.cs` |
| CPU undervolt (Curve Optimizer) | `ICurveOptimizer`, `VoltageDomain` | Generic: `RyzenCurveOptimizer.{Linux,Windows}.cs`, `CurveOptimizerPolicy.cs` (un-suffixed policy), `SmuMailboxAccess`/`RyzenCurveOptimizer.Linux.cs` | `UI/ViewModels/CoViewModel.cs` |
| CPU power overlay (Windows) | `ICpuPower` | Generic: `OverlayCpuPower.Windows.cs` | `UI/ViewModels/CpuViewModel.cs` |
| Display blue-light | `IDisplayTint` | Generic: `DisplayTint.{Linux,Windows}.cs`, `DisplayTintChain.cs`, `KwinTint*`, `GnomeTint*`, `TintChain.Linux.cs` | Options drawer |
| Autostart | `IAutostart` | Generic: `Autostart.{Linux,Windows}.cs` | Options drawer |
| Clamshell keep-awake (Windows) | `IClamshell` | Generic: `Clamshell.{Linux,Windows}.cs` | Options drawer |
| GPU access (app-side) | (no Domain port) | Generic: `CardwireGpuAccess.{cs,Linux,Windows}.cs` | Options drawer |
| Driver setup (PawnIO) | `IDriverSetup` | Generic: `PawnIo*.Windows.cs` | Options drawer |

Vendor layout rule: `Infrastructure/Vendors/Generic/` (ports + vendor-neutral implementations),
`Infrastructure/Vendors/Acer/`, `Infrastructure/Vendors/Dell/`, and the new `Infrastructure/Vendors/Asus/`.
Vendor selection is a single branch in `Infrastructure/Composition/DeviceFactory.cs` (DMI `sys_vendor`).

---

## 3. ASUS capability → app capability map (the ASUS work list)

| asusd / ASUS source | App capability it feeds | Phase | Notes |
|---|---|---|---|
| `Platform.platform_profile`, `platform_profile_choices`, `next_platform_profile()` | `IPowerProfiles` + `IProfileTraits` | **1 ✅** | `AsusdPlatformPort`; ids are the kernel tokens, traits mirror the generic sysfs table |
| `Platform.charge_control_end_threshold` (20..=100) | `Battery.ChargeLimit` (`BatteryToggle`) | **1 ✅** | ON = 80 %, OFF = 100 % — same mapping as `SysfsChargeLimit` |
| `Platform.one_shot_full_charge()` | **distinct action**, not `Battery.Calibration` | **2 ✅ (call only)** | `AsusdOneShotCharge`; a momentary command has no latch to be a toggle, and is not the persistent limit. Surfacing needs an owner decision about where the action lives |
| `Platform.platform_profile_on_ac/battery`, `change_*`, `platform_profile_linked_epp`, `enable_ppt_group` | per-source profile memory + CPU EPP | 4 (deferred) | The app already owns AC/battery profile memory; asusd's own policy must not fight it. Not needed for the Phase-3 writes (PPT values are only applied while tuning is already enabled in asusd) |
| `Backlight.primary_brightness` | `IKeyboardBrightness` | **2 ✅ port, NOT wired** | **The interface is the DISPLAY panel** (`intel_backlight`/`asus_screenpad`), NOT the keyboard LED. `AsusdBacklightPort` is implemented + tested but deliberately not bound to the keyboard slot (that would replace a working keyboard slider with screen brightness). See the caveat below |
| `Aura` (per-device objects/paths) | `IRgbDevice` (controller in `Infrastructure/Lighting`) | **2 ✅ wired** | `AsusdAuraController`; one zone per device, effects from `supported_basic_modes`, `led_mode_data` + `brightness` apply. Runtime unverified |
| `FanCurves` | **dedicated port** (NOT `IFanControl`) | **2 ✅ read; 3 ✅ write** | `AsusdFanCurvesReader` + `AsusdFanCurvesWriter`. A firmware temperature-point curve does not map to the app's mode/speed `IFanControl`, so the write gets its own consent-gated port. Runtime unverified |
| `AsusArmoury` `ppt_pl1_spl`/`ppt_pl2_sppt`/`ppt_pl3_fppt`/`ppt_fppt`, `nv_dynamic_boost`, `nv_temp_target`, `panel_od` | **dedicated `AsusdArmouryPort`** | **3 ✅ consent-gated, unwired** | Read the descriptor, **re-read the live min/max/step/possible-values on every write and validate against it**; refuse over guessing; single-flight; Ppt/Immediate apply now, ReadOnly refused, unknown names refused |
| `AsusArmoury` `gpu_mux_mode`, `dgpu_disable` | **shared `IGpuMux`** (ASUS adapter over `AsusdArmouryPort`) | **3 ✅ wired + consent-gated** | `gpu_mux_mode` drives the shared GPU-mode (MUX) port; QUEUED, never applied immediately (the app never calls `apply_queued_gpu_value`); explicit consent shows the reboot + black-screen warning; rollback documented. `dgpu_disable` remains a separate armoury attribute (not surfaced as the MUX). See docs/gpu-mux.md |
| Windows: ASUS WMI / Armoury / Aura | vendor ports | 4 | Mirror the Acer Windows approach (see Phase 4) |

---

## 4. Phase 1 — implemented

### Files added

| File | Role |
|---|---|
| `Infrastructure/Vendors/Asus/AsusDevice.cs` | `AsusDevice : GenericDevice`, the vendor backend; OS-independent |
| `Infrastructure/Vendors/Asus/AsusDevice.Linux.cs` | Probe asusd; hand it the ports it owns; keep generic ports otherwise |
| `Infrastructure/Vendors/Asus/AsusDevice.Windows.cs` | Phase-1 stub: adds nothing, generic Windows ports stand (required so both TFMs compile) |
| `Infrastructure/Vendors/Asus/AsusdPlatform.cs` | **Un-suffixed/testable**: asusd constants + `busctl` argument lists, `AsusProfiles` vocabulary, `AsusdValues` parsers (string + numeric wire forms), `AsusdPlatformPort` (`IPowerProfiles`/`IProfileTraits`), `AsusdPlatformFacts`/`AsusdOwnership`/`AsusWiring` |
| `Infrastructure/Vendors/Asus/AsusdChargeLimit.cs` | **Un-suffixed/testable**: charge-percentage→toggle rules and the asusd-backed `BatteryToggle` factory |
| `Infrastructure/Vendors/Asus/AsusdPlatform.Linux.cs` | The only I/O: `busctl status` presence probe + `Busctl.Call` factory methods |

### Files changed

| File | Change |
|---|---|
| `Infrastructure/Composition/DeviceFactory.cs` | New branch: `sys_vendor` containing `ASUS` (covers `ASUSTeK COMPUTER INC.`) → `new AsusDevice(product)`; unknown vendors still fall back to `GenericDevice` |

### Tests added (67, all green)

| File | Covers |
|---|---|
| `tests/AcerHelper.Tests/AsusProfilesTests.cs` | token ↔ name/class/colour; numeric code round-trip; both payload parsers; `busctl` argument lists verbatim; failure formatting |
| `tests/AcerHelper.Tests/AsusdPlatformPortTests.cs` | the port over a recorded busctl runner: probing, both wire forms, current read, writes in the observed form, refusals, `next_platform_profile`, traits |
| `tests/AcerHelper.Tests/AsusChargeLimitTests.cs` | threshold parse by signature, on/off mapping, the exact writes, refusals, unreadable→no port |
| `tests/AcerHelper.Tests/AsusDeviceTests.cs` | ownership policy; fallback (generic ports kept when asusd absent); all-null ASUS machine flows through `LaptopService`; asusd-less machine still serves generic profiles; source guard on `DeviceFactory` |

### Key design decisions

1. **`busctl`, not a D-Bus library.** Reuses the existing `Busctl.Call` (`PowerProfiles.Linux.cs`), the
   project-wide system-bus pattern. No new dependency; `Tmds.DBus.*` stays Avalonia-transitive only.
2. **Both asusd wire forms.** Current asusd reports `platform_profile` as a uint (`u`/`au`); older builds
   as strings (`s`/`as`). The port reads the live signature and writes in the form it observed, so neither
   generation is assumed.
3. **Graceful degradation, one direction.** asusd *replaces* a generic port and only when present **and
   usable**; it never stands in front of a working port half-built. The policy is a pure, tested function
   (`AsusdOwnership`) so the fallback is held to something even though `AsusDevice.Linux.cs` is not compiled
   into the test TFM.
4. **Stable ids = kernel tokens.** ASUS profile ids are `low-power`/`quiet`/`balanced`/`performance`/`custom`,
   the same ids the generic `SysfsPowerProfiles` uses, and the traits mirror its table — so `settings.json`
   written with asusd present still applies when the sysfs fallback is live (and vice versa).
5. **Pure logic un-suffixed, I/O in `*.Linux.cs`.** Per `CardwireGpuAccess.cs` + `.Linux.cs` and
   `AcerProfilePorts.cs`: the test project is `net10.0-windows`, so anything left in a Linux file is
   unreachable.
6. **Charge limit stays a boolean.** The existing `BatteryToggle` and Battery UI are on/off; asusd's 20..100
   percentage is mapped (ON 80 / OFF 100) rather than exposed as a new choice, so the surface and
   `settings.json` are identical whether asusd or sysfs is the source.
7. **No status message / localization in Phase 1.** Avoids claiming an asusd state the app has not measured;
   the generic base's messages still apply.

---

## 4b. Phase 2 — implemented (Linux/asusd, no EC-risky writes)

### Files added

| File | Role |
|---|---|
| `Infrastructure/Vendors/Asus/AsusdBacklight.cs` | **Un-suffixed/testable**: `AsusdBacklightPort` over `primary_brightness` (0..100, signature `i`) + the clamp rule. **Implemented but NOT wired** — the interface is the display panel (see caveat) |
| `Infrastructure/Vendors/Asus/AsusdAura.cs` | **Un-suffixed/testable**: Aura per-device path discovery, the `AuraModeNum` vocabulary, the `LedBrightness`/`Speed`/`Direction` encoders, the `(uu(yyy)(yyy)ss)` effect-argument builder, and `AsusdAuraController : IRgbController` |
| `Infrastructure/Vendors/Asus/AsusdFanCurves.cs` | **Un-suffixed/testable, READ-ONLY**: `AsusFanCurve` model + tolerant `fan_curve_data` parser + `AsusdFanCurvesReader`. No write, no `IFanControl` takeover |
| `Infrastructure/Vendors/Asus/AsusdChargeLimit.cs` (extended) | `AsusdOneShotCharge` — the `one_shot_full_charge()` call, kept a distinct action (not calibration) |
| `Infrastructure/Vendors/Asus/AsusdPlatform.Linux.cs` (extended) | Introspection presence test (`Asusd.HasInterface`), `CreateBacklightPort`, `CreateAuraDevice` (tree → introspect → per-device controllers), `CreateFanCurvesReader` |

Changed: `AsusdPlatform.cs` (`IntrospectArguments`/`TreeArguments`/`GetPropertyArgumentsAt`/`SetPropertyArgumentsAt`/`CallArgumentsAt`/`HasInterface`, `ParseScalarUInt`/`ParseUIntArray`, `TakeOverAura`/`TakeOverBacklight`/`TakeOverFanCurves`, `AsusWiring.ApplyAura`); `AsusDevice.Linux.cs` (wires Aura; documents why Backlight/FanCurves/one-shot are not wired).

### Tests added (53; 120 ASUS tests total; 1643 full suite)

| File | Covers |
|---|---|
| `AsusdBacklightTests.cs` | read/clamp, unreadable→0, `set-property` args with signature `i`, refusal handling |
| `AsusdAuraTests.cs` | per-device path discovery from a tree, exact-row interface presence, mode vocabulary/handles, brightness/speed/direction encoders, the flattened effect struct, apply/sub-zone/brightness-read/blank over a recorded bus |
| `AsusdFanCurvesTests.cs` | fan word mapping, the CurveData parse (length-prefixed byte arrays), malformed→null, both profile wire forms, refusal |
| `AsusOneShotChargeTests.cs` | the exact call, success, refusal |
| `AsusDeviceTests.cs` (extended) | Aura ownership gate + `ApplyAura` one-directional fallback |

### Per-feature status (and what is read-only or deferred)

- **Aura — wired.** `AsusDevice.Linux.cs` enumerates `busctl tree` → `/xyz/ljones/Aura/<per-device>`, confirms each by introspection, and assembles the live ones into an owned `RgbDevice` on the `Lighting` slot. **Runtime unverified**: no ASUS Aura hardware was available; the wire shape and mapping are tested, the on-device effect of a write is not.
- **Backlight — implemented, NOT wired.** asusd's `xyz.ljones.Backlight` is the **display** panel (`rog-platform` binds `Primary` to `intel_backlight`, `Screenpad` to `asus_screenpad`), while the app's `IKeyboardBrightness` slot is the plain **keyboard** backlight (and the UI only shows it when there are no RGB panels). Binding it would replace a working keyboard-LED slider with a screen-brightness control under a keyboard label. The correct port exists and is tested; the one-line enable is marked in `AsusDevice.Linux.cs`. The generic `SysfsKbdBacklight` remains the keyboard port.
- **FanCurves — read-only.** `AsusdFanCurvesReader` reads `fan_curve_data(profile)` and parses it into a pure model. **No write and no `IFanControl` takeover**: asusd's curve is temperature-point based (eight temp/pwm pairs per fan per profile) while `IFanControl` is mode/speed based, and any curve *write* touches the EC (`pwmN_auto_point*` + `pwmN_enable`, and asusd itself re-applies the PPT group after it). Both the write and the takeover are Phase 3.
- **One-shot full charge — call implemented, not surfaced.** `AsusdOneShotCharge.Run` makes the call; it is documented as a **distinct** action and deliberately **not** mapped to `Battery.Calibration` (a momentary command has no latch to be a toggle, and it is not the persistent charge limit). Where the action belongs in the UI is an owner decision.
- **Gating.** Phase-1 ports gate on the property being readable; the Phase-2 peripherals gate on `busctl introspect` finding the interface (Backlight, Aura), since neither has a `supported_properties()` of its own. Every takeover stays one-directional and leaves the generic fallback intact.

---

## 4c. Phase 3 — implemented (Linux/asusd, EC-risky, consent-gated, deliberately UNWIRED)

### Files added

| File | Role |
|---|---|
| `Infrastructure/Vendors/Asus/AsusArmoury.cs` | **Un-suffixed/testable**: `AsusdArmoury` (coordinates + argument lists + path discovery), `AsusArmouryKinds` (asusd's Ppt/Gpu/ReadOnly/Immediate/Bios table), `AsusAttributeDescriptor`, `AsusWriteConsent`/`AsusWriteOutcome`, `AsusArmouryMessages`, `AsusArmouryRules.Validate`, and `AsusdArmouryPort` (enumerate, read, refresh-then-validate, consent, single-flight write) |
| `Infrastructure/Vendors/Asus/AsusFanCurveWrites.cs` | **Un-suffixed/testable**: `AsusFanCurveCodec` (validate + CurveData encoding), `AsusFanCurveCalls` (the four method argument lists, verbatim), `AsusdFanCurvesWriter` (consent-gated, single-flight) |

Changed: `AsusdPlatform.cs` (`CallArgumentsAt` gained a flattened-value overload; `AsusdValues.ParseStringArray`); `AsusdFanCurves.cs` (`ProfileValue` shared by reader and writer, reader now uses it); `Localization/Strings.Ru.cs` (19 new keys); this document.

### Tests added (64; 184 ASUS tests total; 1707 full suite)

| File | Covers |
|---|---|
| `AsusArmouryTests.cs` | kind table; path discovery; exact argument lists; validation at every boundary (range, step, value set, read-only, unknown name, unavailable, no-range) |
| `AsusArmouryPortTests.cs` | enumeration + gating; live-limit refresh; valid write args; read-only/unknown/no-range refusals; consent decline; daemon refusal words; **GPU queued and `apply_queued_gpu_value` never called**; queued-value read; deterministic single-flight |
| `AsusFanCurveWritesTests.cs` | codec validation + flattened CurveData encoding; the four argument lists verbatim (both wire forms); consent/unknown-profile/unknown-fan refusals; writer happy path; single-flight |
| `AsusPhase3LocalizationTests.cs` | every Phase-3 message key (read by reflection) has a Russian entry |

### Mapping decision: fan curves do NOT map to `IFanControl`

The app's `IFanControl` is mode/speed shaped (Auto/Max/Custom + one CPU/GPU percentage) and its own curve is an
EMULATION (`Domain/FanCurveEngine.cs` computes a duty per sensor tick and writes it). asusd's curve is a FIRMWARE
artifact: eight temperature/pwm points per fan per profile, written once to `pwmN_auto_point*` and enabled with
`pwmN_enable`, after which the firmware interpolates. There is no mode to report and no home for sixteen points
in a two-slider custom speed. The write therefore gets a **dedicated port** (`AsusdFanCurvesWriter`) and
`IFanControl` is left untouched.

### Safety mechanisms (all unit-tested)

1. **Refresh-then-validate.** Every Armoury write re-reads `available_attrs`/`min_value`/`max_value`/
   `scalar_increment`/`possible_values` from the device and validates against the LIVE limits (PPT bounds move
   with the profile), never a cached or invented one. A failing re-read is a refusal.
2. **Refuse over guess.** `AsusArmouryRules.Validate` accepts a value only if it is in the device's
   `possible_values`, or within `[min_value, max_value]` and a whole `scalar_increment` from the minimum. An
   attribute with NEITHER a value set NOR a range is refused; read-only attributes and unknown names are refused
   before the bus.
3. **Explicit consent.** Every write requires an `AsusWriteConsent` answer. The GPU warning names the restart and
   the black-screen risk; a false answer refuses and writes nothing.
4. **Single-flight.** A lock spans consent + the bus call, so two writes cannot run concurrently (tested with a
   blocking consent delegate, deterministically).
5. **Never from the refresh loop.** The ports have no periodic caller; the writes are only reachable by an
   explicit call. The armoury port is now the engine of the shared GPU-mode (MUX) port, whose only caller is
   the Tuning-drawer card after an explicit confirmation (docs/gpu-mux.md); the fan-curve writer still has no
   caller at all. This is deliberate for EC-risky code that is unverified on ASUS hardware.
6. **GPU writes queue only; the app never applies them.** `apply_queued_gpu_value()` is deliberately NOT called:
   asusd applies the queue at shutdown, which is exactly the reboot the warning names, and an immediate MUX
   apply is the highest-risk operation in the feature.
7. **asusd only.** These ports are asusd-only; no generic path is written alongside them.

### Rollback / recovery (per risky write)

- **GPU MUX / dGPU (`gpu_mux_mode`, `dgpu_disable`).** The write only QUEUES a value in asusd; asusd applies it
  at shutdown. If the screen is black after the restart, force a power-off (hold the power button), start the
  machine, and set the mode back. Recovery from a black screen may need a second display or a console/TTY; the
  firmware default is the previous (non-disabled / Optimus) mode. To change it back through asusd, use its own
  tools (ROG Control Center / `asusctl`) or the same port from a future consent UI — the previous value is
  readable through `current_value` and the queued one through `queued_gpu_value`.
- **PPT/TDP (`ppt_*`, `nv_dynamic_boost`, `nv_temp_target`).** The value is stored by asusd and only written to
  the firmware while the current profile's custom tuning is ENABLED in asusd (we never enable it). To roll back,
  disable that tuning in asusd / ROG Control Center, or write the attribute's own `default_value` (readable in
  `AsusAttributeDescriptor.DefaultValue`).
- **Fan curves.** `AsusdFanCurvesWriter.ResetToDefaults(profile)` calls asusd's `set_curves_to_defaults`, which
  restores the firmware defaults for that profile. The `set_fan_curves_enabled(profile, false)` call also
  disables a written curve without deleting it.
- No rollback is attempted silently by the app: each of these is a user-initiated call, and the app owns no
  "revert" timer.

---

## 5. Phase 4 — implemented (Windows ASUS, ATK ACPI)

### Protocol and sources (no ID is invented)

ASUS Windows is driven through the **ATK ACPI device** `\\.\ATKACPI` with `DeviceIoControl` (IOCTL
`0x0022240C`) and the `DEVS`/`DSTS` buffers. Both the method/device IDs and the Windows buffer layout are
public and cross-checked:

- **Linux** `drivers/platform/x86/asus-wmi.c` + `include/linux/platform_data/x86/asus-wmi.h` — the authoritative
  device IDs (`ASUS_WMI_DEVID_*`), the method names (`ASUS_WMI_METHODID_DEVS` = `0x53564544`, `DSTS` =
  `0x53545344`), the management GUID `97845ED0-4E6D-11DE-8A39-0800200C9A66`, and the DSTS presence bit
  (`0x00010000`).
- **G-Helper** `app/AsusACPI.cs` — the Windows transport: `\\.\ATKACPI`, IOCTL `0x0022240C`, the call header
  `[method u32 LE][args length u32 LE][args…]`, the 16-byte output, and the `- 0x10000` presence decode.

Device IDs used: profiles `0x00120075` (Vivo `0x00110019`); charge limit `0x00120057`; GPU MUX `0x00090016`
(Vivo `0x00090026`); panel overdrive `0x00050019` (support probe `0x00050020`); fan curves `0x00110024`/`25`/`32`;
TUF keyboard RGB `0x00100056`.

### Files added / changed

| Area | Files |
|---|---|
| Protocol | `Infrastructure/Vendors/Asus/AsusAtk.cs` (pure buffers + decoders + `AsusAtkDevice`), `AsusAtkChannel.Windows.cs` (CreateFile/DeviceIoControl) |
| Profiles | `AsusWindowsProfiles.cs` (`IPowerProfiles`/`IProfileTraits`) |
| Battery / panel | `AsusWindowsBattery.cs` (charge limit reusing `AsusdChargeLimitRules`; panel-overdrive `FlagPort`) |
| Fan curves | `AsusWindowsFanCurves.cs` (16-byte codec + reader/writer, reusing the Linux `AsusFanCurve`/`AsusFanCurveCodec`) |
| GPU MUX | `AsusWindowsGpuMux.cs` (shared `IGpuMux`) |
| Lighting | `AsusAtkRgb.cs` (TUF ATK RGB `IRgbController`) |
| Quirks | `AsusModel.cs` (model table) |
| Wiring | `AsusDevice.Windows.cs`; `AsusDevice.cs` (Product for the quirks lookup); `OptionsAssembler.cs` (`panel_od` label) |

### Mapping decisions

- **Profiles**: Windows modes map onto the app vocabulary with the SAME ids/kinds the Linux path uses — Silent →
  `quiet`/Quiet, Balanced → `balanced`/Balanced, Turbo → `performance`/Performance — so a preset written on one
  OS reads on the other. An unnamed firmware mode reads as null rather than a nearby profile.
- **Fan curves are NOT `IFanControl`** (same as Linux): the ATK path is a firmware temperature-point curve per fan
  (`DEVS` with a 16-byte temps+percents buffer; `DSTS` to read it with the performance-mode swap G-Helper
  documents). `AsusWindowsFanCurvesReader`/`Writer` reuse the Linux model and validator; the writer is
  consent-gated and single-flight. It is **not wired to `IFanControl`** and has no caller, deliberately.
- **GPU MUX**: the shared `IGpuMux`. `gpu_mux_mode` is 0 = dGPU/discrete, 1 = Optimus/hybrid (asus-wmi's own
  meaning). The ATK write stores the value immediately, but the PANEL re-routes on the next boot — so a request
  made in this session is reported as Pending with "restart required" (a fresh process reads the real mode and
  shows no pending), and the app never claims the routing already moved.
- **Panel overdrive**: a plain declared on/off setting (`panel_od`), offered only when the support probe returns 1.
- **TUF keyboard RGB**: the ATK 6-byte packet `[0xb4, mode, r, g, b, speed]` behind `IRgbDevice` (one zone).
  **ROG per-key Aura HID is NOT implemented** — no verified device selection/codec — and is refused rather than
  writing reports to whatever device answers.

### Safety parity with Linux

Capability-probed (`DSTS` presence bit) so an absent feature is simply absent; the charge/MUX/profiles paths
refuse on an unanswered or refused write; the fan-curve write is consent-gated and single-flight; the MUX goes
through the shared confirmation + black-screen warning; nothing is reachable from the refresh loop; the Windows
backend references **no** asusd/busctl code, so the two transports cannot both write the same machine (pinned by
a source guard).

### Tests (31; 1756 full suite) and unverifiable

`AsusWindowsTests.cs`: the buffer builders and decoders verbatim, the presence/unsupported decode, profile
mapping + traits + Vivo preference + unnamed-mode null, charge-limit on/off mapping, panel-overdrive gating,
curve encode/decode + mode swap + device mapping + consent/refusal/single-flight, MUX supported/unsupported +
queued-not-immediate + unknown-mode refusal, TUF RGB packet, quirks lookup, and the wiring source guard.

**Unverifiable without ASUS hardware**: the actual `DeviceIoControl` transport, whether a given device ID answers
on a given model, the fan-curve buffer layout and mode swap, the TUF RGB packet, and the Windows MUX
"takes effect on reboot" timing. All are marked in the source.

### Remaining gaps

- ROG per-key Aura HID (needs a verified device selection/codec).
- ASUS Armoury tunables on Windows (PPT/TDP, dynamic boost) — the ATK IDs are known (`0x001200A0`/`A3`/`C0`/`C1`/
  `C2`) but they need a numeric-range UI this phase does not add.
- Windows fan telemetry (temps/RPM) is not exposed; `Sensors` stays whatever the generic base wires.

---

## 6. Status checklist

- [x] Recon: Acer vendor surface fully read; parity map written to this file
- [x] `AsusDevice` + per-OS halves; `DeviceFactory` ASUS branch; unknown-vendor fallback preserved
- [x] `AsusdPlatformPort` (`IPowerProfiles`/`IProfileTraits`) over shared `Busctl`; both wire forms; traits
- [x] `next_platform_profile()` + `platform_profile_choices` probing
- [x] Battery charge limit over asusd `charge_control_end_threshold`, with sysfs fallback when absent
- [x] `busctl status xyz.ljones.Asusd` presence probe; take over only when present/owned
- [x] Pure logic un-suffixed/testable; `busctl` invocation in `*.Linux.cs`
- [x] Unit tests (67) for Phase 1: mapping, parser, argument builders, ownership/fallback, all-null service flow
- [x] Both TFMs build; full suite green (1590 after Phase 1)
- [x] Phase 2 — Aura wired (per-device paths, modes, effect struct, brightness); runtime unverified
- [x] Phase 2 — Backlight port implemented + tested, deliberately NOT wired (asusd Backlight is the display panel)
- [x] Phase 2 — FanCurves READ-ONLY parse + reader; write and `IFanControl` takeover deferred to Phase 3
- [x] Phase 2 — `one_shot_full_charge()` call implemented as a distinct action (not calibration)
- [x] Phase 2 tests (53; 120 ASUS total); both TFMs build; full suite green (1643)
- [x] Phase 3 — FanCurves WRITES (dedicated port, not `IFanControl`): validate, consent, single-flight, verbatim args
- [x] Phase 3 — AsusArmoury read + validated writes: live-limit refresh, refuse-over-guess, read-only/unknown refused
- [x] Phase 3 — GPU MUX / dGPU queued only; `apply_queued_gpu_value` never called; reboot + black-screen warning
- [x] Phase 3 — rollback/recovery documented per risky write (MUX black screen included)
- [x] Phase 3 — `AsusdArmouryPort`/`AsusdFanCurvesWriter` consent-gated; the armoury port is now the engine of the shared MUX, the fan-curve writer still has no caller
- [x] Phase 3 — 19 Russian strings added (validation refusals + consent warnings); pinned by a reflection test
- [x] Phase 3 tests (64; 184 ASUS total); both TFMs build; full suite green (1707)
- [x] Shared GPU-mode (MUX) `IGpuMux` port + Tuning-drawer card; ASUS adapter over the armoury port (queued, consent, current+pending)
- [x] Acer MUX: recovered `AcerGamingFunction` `Get/SetGamingMiscSetting` selectors (capability 9, read 2, write `(mode<<8)|2`); read + queued write on Windows, clean refusal elsewhere (docs/gpu-mux.md §4)
- [x] GPU-mode tests (19; 1726 full suite); both TFMs build
- [x] Phase 4 — Windows ATK backend: profiles, charge limit, panel overdrive, GPU MUX (shared port), TUF RGB, quirks DB
- [x] Phase 4 — fan-curve windows codec + consent-gated reader/writer (read-only wiring, not `IFanControl`); ROG Aura HID refused/documented
- [x] Phase 4 — 2 new Russian strings + `panel_od` label; tests (31; 1756 full suite); both TFMs build
- [ ] Remaining gaps: Windows Armoury tunables (PPT/Dynamic Boost), ROG Aura HID, Windows fan telemetry (docs/asus-support.md §5)
