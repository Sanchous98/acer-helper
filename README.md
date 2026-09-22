# Acer Helper

A lightweight tray app (C# / .NET 10, [Avalonia](https://avaloniaui.net/) UI) — an
open, minimal alternative to NitroSense, in the spirit of
[G-Helper](https://github.com/seerge/g-helper) but for Acer Nitro / Predator laptops.

Built on the hardware-verified Acer gaming WMI interface (`AcerGamingFunction`,
GUID `7A4DDFE7-…`).

## v1 — performance profiles

Switch the platform performance profile from a tray icon and a compact window:

- **Quiet · Balanced · Performance · Turbo · Eco**
- Reads the current profile and the supported-profiles mask from the EC
  (misc-setting `0x0B` / `0x0A`), and offers exactly what the source reports —
  nothing is gated by power source. On Linux all five profiles stay selectable
  and writable on battery (each was written and read back on battery), so no
  choice is greyed out for being on battery. What a profile moves here is
  measurable only as CPU-package watts — RAPL reports 49.12 / 59.27 / 73.47 /
  83.35 W for low-power / balanced / balanced-performance / performance — not as
  the ~108 W dGPU envelope of the Windows measurements, which this box cannot
  reproduce: the Linux side sees no discrete GPU at all
  ([docs/acer-linux.md](docs/acer-linux.md)).
- Tray icon shows the active profile; right-click to switch; window auto-refreshes.

### How it works

`SetGamingMiscSetting(gmInput = 0x0B | (value << 8))` to set,
`GetGamingMiscSetting(gmInput = 0x0B)` to read (status in byte 0, value in byte 1),
via `root\WMI` class `AcerGamingFunction`.

**Plus a second channel on models that need it.** On the Nitro AN18-61 that WMI byte turned out to be only an
*indicator*: it moves the tray state and the lightbar palette, and the EC reports it back as the current
profile, but it does **not** move the power envelope. The envelope — GPU TGP/CTGP and the CPU limits — lives in
the EC's own "system usage mode", reachable only over HID (VID `0x1025` / PID `0x174B`, 65-byte feature
reports). Without it the dGPU stays at its bare vBIOS default (~78 W sustained instead of ~108 W) no matter
which profile the app shows. A profile switch now drives both channels, and at startup the EC mode is synced —
EC only, no profile switch — to whatever profile the hardware reports, because the profile byte survives a
reboot while the EC mode behind it does not. Full protocol, the measured mode→watts table and the dead ends:
[`docs/power-an18-61.md`](docs/power-an18-61.md).

## Requirements

- Acer gaming laptop exposing `AcerGamingFunction` (Nitro / Predator, recent gen).
- Windows 10/11.
- **Run as Administrator** (Acer WMI/ACPI methods require elevation — the app
  manifest already requests it).
- Remove or disable NitroSense and the Acer service stack. Not just cosmetic: `AcerQAAgent`, while running,
  re-applies its own EC usage mode every minute or two and will overwrite the power envelope this app sets.

## Architecture

One project, organised by layer; **namespaces match the directories** (`AcerHelper` + path).
The dependency arrow is **Infrastructure → Application → Domain**: Infrastructure depends on
and *uses* Application, Application depends on Domain, and neither the reverse nor a skip is
allowed. Infrastructure implements what Application declares. `ArchitectureMapTests` asserts it.

- **`Domain/`** (`AcerHelper.Domain`) — the vendor- and OS-agnostic core: model
  (`PerformanceProfile`, `FanMode`, `SensorSnapshot`, `HotkeyAction`, …) and one fine-grained
  *port* per capability (`IPowerProfiles`, `IFanControl`, `ISensors`, `IRgbDevice`, `IHotkeys`,
  `IDisplayTint`, `IAutostart`, `IClamshell`). The machine that HOLDS them is Infrastructure's
  (`Device`, in `Infrastructure/Composition/`): each port is **nullable** — `null` means the feature
  is absent, so the UI shows exactly what the hardware has.
  Two capabilities state their own shape instead of occupying a port: the battery is an OBJECT
  (`Battery`) declaring its properties one by one, so a firmware with no charge limiter is a battery
  whose `ChargeLimit` is null instead of a missing port beside three others; and the settings a
  backend owns are **declared** (`SettingDeclaration`, in `Domain/DeclaredSetting.cs`) under the
  backend's own opaque key, so the domain knows no hardware-specific setting name — the settings
  CONTAINER (Infrastructure's) is constructed with the set of them and switches only within it,
  refusing anything this machine does not declare. See
  `docs/domain-refactoring-plan.md` §3.1 and §5 (waves 5 + 9).
  Also the pieces that are logic rather than I/O: `Fan` (one fan's duty curve and the duty applied to it)
  with `FanCurveEngine` (the pair of fans and the deadband over them), and the
  compile-time version constant.
- **`Application/`** (`AcerHelper.Application`) — the use cases, and the contracts Infrastructure
  implements. `ReapplyPlan` states which axes a boot, a mode switch and a wake re-apply, in what order
  and on what thread; `ReapplySettings` is the use case that drives them over `IReapplyTarget`, the one
  contract it declares (an axis per call, not an interface per axis), and its result is Domain
  vocabulary (`FanAxisState`, `GpuAxisState`) so that it can cross this boundary at all. Then the
  family of APPLIED EDITS, one use case and one contract per thing the user sets: `FanAxis`
  (`IFanAxisTarget` — a curve edit names one fan and carries the other fan's half over), `GpuOffsets`,
  `CpuPowerOverlay`, `Undervolt`, `DeclaredSetting`. Each states a rule the
  method body it came from only happened to implement — what a fan edit must not clobber, that an
  offset is remembered BEFORE it is written, that a refused declared setting is never remembered — and
  each is pinned through its own contract with a stub, without Infrastructure.
  `AppArgs` is the CLI constant. None of this names an Infrastructure type — see the arrow above, and note what
  that still forbids: a use case needing the stored settings container or a vendor port by name cannot
  live here yet, which is why the profile switch did not move — it holds the graph lock across the port
  call, and that measurement is kept in `docs/open-decisions.md`'s note on the retired analysis. The
  per-mode lighting was on that list too and is not any more: it had to cross by contract rather than
  by live reference, and now does (`Application/LightZone.cs`).
- **`Infrastructure/`** (`AcerHelper.Infrastructure`) — everything that touches the machine:
  `UpdateChecker`/`WindowsUpdater`/`AppImageUpdater`, `HardwareAccess`, `LidWatcher`,
  `ResumeWatcher`, plus `Composition/`, `Diagnostics/`, `Lighting/` and `Vendors/`.
- **`Infrastructure/Lighting/`** (`AcerHelper.Infrastructure.Lighting`) — the RGB transport framework
  (`IRgbController`, `RgbDevice`) that the vendor backends and the app's own zone lighting are written
  against. The HID **LampArray** translation layer that used to sit on top of it (`LampArrayLayout`,
  `LampArrayBridge` — how the app's zones were published as a virtual lighting device, how host frames became
  zone writes, and who owned the backlight while a host painted it) went with the Windows Dynamic Lighting
  feature on 2026-09-22, below.
- **`Infrastructure/Vendors/Acer/`** (`AcerHelper.Infrastructure.Vendors.Acer`) — Acer feature
  implementations. There is **no
  separate platform layer**: the OS access is folded into the vendor implementation, split per OS
  by file name — `AcerDevice.Windows.cs` (WMI) and `AcerDevice.Linux.cs` (hidraw + sysfs, with no kernel
  module required) sit side by side.
  Within Acer, capabilities are **probed at runtime** (RGB device present? EC supported-profile
  mask? nullable WMI getters?) — so most models work without an entry. Profiles (shared enum) and
  fan topology (dual) are not per-model. The only un-probeable per-model bits — friendly name and
  RGB layout (zone count, lightbar) — live in a config file **`acer-models.json`** (embedded
  default + optional user override at `%AppData%/AcerHelper` / `~/.config/AcerHelper`), matched by
  DMI product name via `AcerModels.Detect`. (Design validated against Linuwu-Sense and G-Helper:
  probe-first, with a thin per-model quirks/override table.)
- **`Infrastructure/Vendors/Generic/`** (`AcerHelper.Infrastructure.Vendors.Generic`) — the
  genuinely vendor-agnostic backend: **performance profiles via standard OS APIs** (Windows
  power-mode overlay / Linux ACPI `platform_profile`), blue-light gamma, autostart, clamshell,
  battery, keyboard brightness, hwmon sensors + a small WMI helper, again split by
  `*.Windows.cs` / `*.Linux.cs`. It is a *vendor* only in the sense that its "vendor" is the OS —
  which is why it lives beside `Acer/` rather than in a layer of its own. The one part that is
  *not* vendor-agnostic lives here too, next to the transport that made it concrete: `CoAxis`
  (the Curve Optimizer's four write rules) and `OffsetCounts` (its AVFS counts), because a rail
  clamped in AVFS counts models one vendor's silicon rather than the task.
- **`Infrastructure/Vendors/Dell/`** (`AcerHelper.Infrastructure.Vendors.Dell`) — the second vendor
  backend, the one that proves the split is real rather than Acer-shaped.
- **`Infrastructure/Composition/`** (`AcerHelper.Infrastructure.Composition`) — the machine's
  service and the shape its settings are kept in: `LaptopService` (split across `LaptopService.*.cs`
  partials, one per feature family), `OptionsAssembler` (the hardware rows), `HardwareReconciler`
  (executes `Application`'s re-apply plan against the ports), `Settings` and the persisted presets
  (`FanPreset`, `GpuOcPreset`, `CoPreset`, `LightSettings`, …) with `ModeKey` and `ISettingsStore`,
  and `JsonSettingsStore`. `DeviceFactory` detects the machine and BUILDS it — `Device`, whose ports
  the vendor backend fills and which carries the settings its backend declared. When no
  vendor backend matches (a non-Acer laptop, or no elevation), it falls back to a **generic
  device** offering those OS-standard basics — so the app is useful on any laptop. (Validated on a
  Dell Latitude 5540 on Linux: shows the firmware's cool/quiet/balanced/performance profiles.)
- **`Infrastructure/Diagnostics/`** (`AcerHelper.Infrastructure.Diagnostics`) — the always-on
  gate counters (`GateStats`, `GateStatsLog`) and the log they append to.
- **`Bootstrap/`** (`AcerHelper.Bootstrap`) — `Program.cs`: the entry point, single-instance
  mutex, command-line parsing.
- **`UI/`**, **`Localization/`** — the Avalonia tray + windows (capability-driven; binds to
  `Application` and `Domain`, never to a vendor), and the string tables.

**There was a `driver/` until 2026-09-22** — the one piece that could not be C#: `AcerHelperLampArray.sys`, a
KMDF HID *source* driver over the in-box Virtual HID Framework that published the keyboard's zones as a **HID
LampArray** so Windows Dynamic Lighting could paint them. The whole tree, both builds of the package and the
container stages that produced them were removed by the owner's decision; why is below, and in full in
[docs/lamparray.md](docs/lamparray.md).

OS-specific code is selected by the `*.Windows.cs` / `*.Linux.cs` file-name suffix (MSBuild
`<Compile Remove>` globs per target framework) — **no preprocessor directives**. Adding a laptop
vendor = a new set of files under `Infrastructure/Vendors/`; adding an OS = `*.Linux.cs` siblings. The UI never changes.

## Windows Dynamic Lighting (LampArray) — removed 2026-09-22

This feature is **gone**, by the owner's decision. The app used to expose the keyboard's zones (and the
lightbar, when it wasn't following the performance profile) as a virtual **HID LampArray**, so Windows' own
Dynamic Lighting page — and any app or game driving lighting through it — could paint them; the driver tree,
`LampArrayBridge` and its transport, the Options toggle and the container stages that built the package were
all removed with it. There is no `driver/` directory in the tree any more and no build that produces one.

**The reason is a signing wall, not a change of mind.** Windows enumerates lighting devices **only** as HID
LampArray collections, and Microsoft's device guidance lists exactly two compatible routes: native firmware, or
a **VHF** driver. `VhfCreate` lives in `VhfKm.lib` and its documentation speaks of KMDF only — there is no
user-mode variant, so the UMDF2 idea (appealing precisely because a self-signed certificate suffices for one,
where a kernel-mode driver needs Microsoft's) could not carry this capability. A KMDF driver therefore needs
either test signing (Secure Boot off, with its BitLocker and anti-cheat consequences) or **attestation
signing** — an EV certificate (~€250–400/year), a Partner Center hardware account, and a returned `.cat`. The
owner declined both, which is why the feature is dropped instead of shipped.

**Your keyboard lighting is not affected by any of this.** The app's own lighting — the per-mode palettes, the
lighting panel, the lightbar — never went through the driver: it is the `RgbZone`/ENE path and it is still
there. What is gone is the bridge that made those zones *visible to Windows* as a lighting device.

The design, the wire format, the limitations and the two builds of the package are kept as the historical
record in [docs/lamparray.md](docs/lamparray.md) and [docs/rust-driver.md](docs/rust-driver.md).

## Build

The project multi-targets `net10.0-windows` (full Acer/Windows) and `net10.0` (portable; Acer,
Dell and generic Linux backends). CI (the `build` workflow — two parallel jobs) produces Native-AOT
artifacts per OS: an `AcerHelper.exe` + WiX MSI on a Windows runner, and a self-updating AppImage packed
on the runner out of the publish folder the container build exports. Both are Native AOT. Locally:

```
# Windows (Native AOT — must run on Windows)
dotnet publish AcerHelper.csproj -c Release -f net10.0-windows -r win-x64 --self-contained true -p:PublishAot=true -o publish

# Linux (Native AOT — needs clang + zlib-devel to link)
dotnet publish AcerHelper.csproj -c Release -f net10.0 -r linux-x64 --self-contained true -p:PublishAot=true -o publish-linux
```

**The one artefact that can be built on Linux is containerised, in the repository's multi-stage Dockerfile.**
One command yields it:

```
docker buildx build --target artefacts --output type=local,dest=dist .
```

That is the portable app (Native AOT), in `dist/linux/`, with `dist/PROVENANCE.txt` beside it. **Until
2026-09-22 the same target exported a second one** — `dist/driver/`, the LampArray driver package; that
artefact went with the feature (*Windows Dynamic Lighting*, above). Commands,
what is pinned, how the artefact comes out, and what could not be verified:
[docs/build-image.md](docs/build-image.md).

**2026-09-18 — the Windows publish above cannot move into that container.** Native AOT refuses to
cross-compile from Linux to Windows, and the refusals are errors in the toolchain rather than advice
(`Microsoft.NETCore.Native.Publish.targets`: "Cross-OS native compilation is not supported.", plus a demanded
`PackageReference` for the host ILCompiler; `Microsoft.NETCore.Native.Windows.targets` hard-wires `link.exe`
and runs `findvcvarsall.bat`). The Windows artefact therefore stays on a Windows host — the `windows` CI job —
and the Linux container builds the other one. The line above saying the Windows publish "must run on Windows"
was already right; what is new is that the reason is measured, and that it is not an installation problem a
bigger container could solve. **Corrected 2026-09-22:** the count in this paragraph is now one instead of
two — when it was written the container built the portable app *and* the LampArray driver package, and the
second went with the feature (*Windows Dynamic Lighting*, above).

**2026-09-18 — CI now builds through that image.** The `linux` job of the `build` workflow runs the
`docker buildx build` command above (with `--cache-from`/`--cache-to type=gha,mode=max` and `--build-arg
GIT_SHA`) and packs the AppImage out of the `dist/linux` it exports. That job no longer sets up the .NET SDK,
installs nothing, and no longer runs in a `fedora:41` container: the toolchain it used to install (clang,
zlib) is pinned inside the image, and a job-level container has no route to the Docker daemon buildx talks to.
One consequence worth knowing: the binary inside a released AppImage is now the one this image produces — it
used to be built in `fedora:41` and so differed in bytes, which is what `docs/build-image.md` recorded until
this change. **2026-09-22 — the driver package is no longer built there at all.** The same job used to build
the LampArray package on every tagged run and upload it as the `AcerHelperLampArray-driver-package` workflow
artefact, deliberately **not** attached to the release: an unsigned package is one Windows will not load, and
the shipping path was attestation signing. Both the package and that upload were removed with the feature
(*Windows Dynamic Lighting*, above); the job now builds the AppImage and nothing else. The `windows` job is
untouched by all of this.

## Install (Windows)

The `windows` workflow builds an **MSI** (`packaging/AcerHelper.wxs`, WiX) from the publish folder — run
`AcerHelper-Setup.msi` to install to Program Files with a Start-menu shortcut + uninstaller (admin
elevation prompt; the app self-elevates at runtime too). WiX only builds on Windows, so build it there:

```powershell
dotnet publish AcerHelper.csproj -c Release -f net10.0-windows -r win-x64 --self-contained true -p:PublishAot=true -o publish
dotnet tool install --global wix --version 5.0.2
# Version MUST match the csproj <Version> (it becomes the MSI ProductVersion — a stale value breaks
# MajorUpgrade ordering and disagrees with the version shown in the app). PublishDir MUST be absolute:
# WiX resolves relative paths against the .wxs folder (packaging\), harvesting nothing -> empty MSI.
$ver = [regex]::Match((Get-Content AcerHelper.csproj -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
wix build packaging\AcerHelper.wxs -arch x64 -d Version=$ver -d "PublishDir=$PWD\publish" -o AcerHelper-Setup.msi
```

## Install (Linux)

The `build` workflow produces a Native-AOT **AppImage** (`AcerHelper-x86_64.AppImage`). Download it, make it
executable, run. It lives in your home dir — so on **immutable Fedora** (Silverblue/Kinoite/uBlue) it needs
no rpm-ostree layering or reboot — and it **self-updates**: the in-app update check downloads the new
AppImage and replaces it in place. On first run it offers a one-click **"Grant hardware access"** (a single
pkexec/polkit password prompt) that installs the udev rules and the tmpfiles.d entry, which make the root-only
controls writable — keyboard backlight, battery charge thresholds, the platform profile and, where the driver
exposes it, the fan PWM attributes. **On an Acer** (the machine is read from
`/sys/devices/virtual/dmi/id/sys_vendor`) the same prompt additionally installs a modprobe.d options line
(`options acer_wmi predator_v4=1 force_caps=7200`, with its provenance, its mask derivation, the measured value
that this one is not, and the rule to re-derive it in `packaging/acer-helper-modprobe.conf`), which is what makes the mainline `acer-wmi`
Predator/Nitro features exist on a model its DMI quirk table does not list: the five platform profiles, the
fan/temperature telemetry **and fan control** (the hwmon `pwm*` nodes, which is what the udev rule above grants
write access to — `force_caps` is a whole-mask replacement and its value is machine-specific, which is why the
file records how it was derived rather than just the number); a Dell or generic machine is never asked to
install parameters for a module it does not load, and it keeps the other two files. The prompt reloads udev, and
on an Acer the module last, so the parameters apply immediately; **restart the app afterwards** — it caches its
sysfs paths at construction. A portable binary can't ship system files itself, so this is the one privileged
step.

```
chmod +x AcerHelper-x86_64.AppImage && ./AcerHelper-x86_64.AppImage
```

Build the AppImage locally: `dotnet publish … -p:PublishAot=true -o publish-linux` (above), assemble an
AppDir (the publish output + `packaging/{AppRun,acer-helper.desktop,acer-helper.png}`), then
`appimagetool AcerHelper.AppDir AcerHelper-x86_64.AppImage`. The three permission/options files need no listing
here: the csproj carries them as **Linux-only `Content` items** (`CopyToPublishDirectory`), so the publish output
already contains `60-acer-helper.rules`, `acer-helper.conf` and `acer-helper-modprobe.conf` flat next to the
binary — which is exactly where `HardwareAccess` looks for them. A local AppDir therefore needs no hand copy of
them (the release workflow re-copies the same bytes into the AppDir anyway, deliberately, because an AppImage
that lost them loses the access offer with no error anywhere).

The Linux backend's own reference is [docs/acer-linux.md](docs/acer-linux.md): what each module parameter turns
on and how to re-derive its value (including the `force_caps` procedure and its measured result), the
`platform_profile` name ↔ EC byte table with the "`performance` means Turbo" trap, the fan and sensor channel
maps, the EC HID reconnaissance and why that route is closed, and the list of capabilities that stay absent on
Linux.

## Roadmap

- Linux hardware backend — Acer probe-first: hidraw for RGB and the EC power envelope, `platform_profile`
  for the firmware profile set, evdev hotkeys, X/Wayland gamma. The Linux backend is **module-free by
  construction** — the Linuwu-Sense tier is deleted, so its node-backed controls (LCD overdrive, battery
  limiter/calibration, backlight timeout, USB charging, backlight read) are declared ABSENT rather than
  optional, and the module parameters above are the only kernel-side dependency left. Clamshell is
  Windows-only: on Linux `Clamshell.Linux` reports unsupported, because the desktop's own power manager holds
  the lid switch (KDE PowerDevil blocks `handle-lid-switch` and suspends by its own config, so a third-party
  logind inhibitor is redundant and does not stop it).
- Additional vendors behind the same Domain ports
- Per-key RGB; fan curves
