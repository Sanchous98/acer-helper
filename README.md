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
  (misc-setting `0x0B` / `0x0A`), so unavailable profiles (e.g. Turbo on
  battery) are greyed out automatically.
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

One project, organised by layer; **namespaces match the directories** (`AcerHelper` + path):

- **`Domain/`** (`AcerHelper.Domain`) — the vendor- and OS-agnostic core: model
  (`PerformanceProfile`, `FanMode`, `SensorSnapshot`, `HotkeyAction`, …) and one fine-grained
  *port* per capability (`IPowerProfiles`, `IFanControl`, `ISensors`, `ILighting`, `IHotkeys`,
  `IDisplayTint`, `IAutostart`, `IClamshell`). The aggregate `IDevice` exposes each port as
  **nullable** — `null` means the feature is absent, so the UI shows exactly what the hardware has.
  Two capabilities state their own shape instead of occupying a port: the battery is an OBJECT
  (`Battery`) declaring its properties one by one, so a firmware with no charge limiter is a battery
  whose `ChargeLimit` is null instead of a missing port beside three others; and the settings a
  backend owns are **declared** (`SettingDeclaration` on `IDevice.DeclaredSettings`) under the
  backend's own opaque key, so the domain knows no hardware-specific setting name. See
  `docs/domain-refactoring-plan.md` §3.1 and §5 (waves 5 + 9).
  Also the pieces that are logic rather than I/O: `Fan` (one fan's duty curve and the duty applied to it)
  with `FanCurveEngine` (the pair of fans and the deadband over them), and the
  compile-time version constant.
- **`Application/`** (`AcerHelper.Application`) — the use cases, and nothing else: `LaptopService`
  (split across `LaptopService.*.cs` partials, one per feature family) and `OptionsAssembler`.
  This is the only layer the UI talks to.
- **`Infrastructure/`** (`AcerHelper.Infrastructure`) — everything that touches the machine:
  `UpdateChecker`/`WindowsUpdater`/`AppImageUpdater`, `HardwareAccess`, `LidWatcher`,
  `ResumeWatcher`, plus `Composition/`, `Diagnostics/`, `Lighting/` and `Vendors/`.
- **`Infrastructure/Lighting/`** (`AcerHelper.Infrastructure.Lighting`) — the RGB transport framework
  (`IRgbController`, `RgbDevice`) and the HID **LampArray** translation layer (`LampArrayLayout`,
  `LampArrayBridge`): how the app's zones are published as a virtual lighting device, how host frames
  become zone writes, and who owns the backlight while a host paints it.
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
  which is why it lives beside `Acer/` rather than in a layer of its own.
- **`Infrastructure/Vendors/Dell/`** (`AcerHelper.Infrastructure.Vendors.Dell`) — the second vendor
  backend, the one that proves the split is real rather than Acer-shaped.
- **`Infrastructure/Composition/`** (`AcerHelper.Infrastructure.Composition`) —
  `DeviceFactory.Windows.cs` / `DeviceFactory.Linux.cs`
  detect the device and assemble an `IDevice`; `CompositeDevice`, `JsonSettingsStore`. When no
  vendor backend matches (a non-Acer laptop, or no elevation), it falls back to a **generic
  device** offering those OS-standard basics — so the app is useful on any laptop. (Validated on a
  Dell Latitude 5540 on Linux: shows the firmware's cool/quiet/balanced/performance profiles.)
- **`Infrastructure/Diagnostics/`** (`AcerHelper.Infrastructure.Diagnostics`) — the always-on
  gate counters (`GateStats`, `GateStatsLog`) and the log they append to.
- **`Bootstrap/`** (`AcerHelper.Bootstrap`) — `Program.cs`: the entry point, single-instance
  mutex, command-line parsing.
- **`UI/`**, **`Localization/`** — the Avalonia tray + windows (capability-driven; binds to
  `Application` and `Domain`, never to a vendor), and the string tables.
- **`driver/`** — the one piece that can't be C#: `AcerHelperLampArray.sys`, a KMDF HID *source* driver over the
  in-box Virtual HID Framework that publishes the keyboard's zones as a **HID LampArray** so Windows Dynamic
  Lighting can paint them. Windows only enumerates lighting devices as LampArray HID collections, so a driver
  has to exist; it is kept deliberately dumb (static report descriptor + a lamp table pushed down over three
  IOCTLs) with all the logic in `Infrastructure/Lighting/LampArrayBridge.cs`. See [docs/lamparray.md](docs/lamparray.md).

OS-specific code is selected by the `*.Windows.cs` / `*.Linux.cs` file-name suffix (MSBuild
`<Compile Remove>` globs per target framework) — **no preprocessor directives**. Adding a laptop
vendor = a new set of files under `Infrastructure/Vendors/`; adding an OS = `*.Linux.cs` siblings. The UI never changes.

## Windows Dynamic Lighting (LampArray)

Optional: expose the keyboard's zones (and the lightbar, when it isn't following the performance profile) as a
virtual **HID LampArray**, so Windows' own Dynamic Lighting page — and any app or game that drives lighting
through it — can paint them. This is the same construction as Logitech G HUB's "LampArray translation layer":
a small signed HID source driver over `vhf.sys`, plus a user-mode translator (here, the app itself).

The app publishes the device only while the option is on, throttles host frames to 10 Hz (the ENE controller
sits on a contended HID-over-I2C bus), collapses uniform frames to a single write, and yields its own
per-mode lighting while a host holds the surface — re-asserting the host's last frame after the events that
clobber the EC's RGB (profile switch, resume, lid-open).

The toggle appears in **Options** only once the driver package is installed
(`pnputil /add-driver`, see [driver/README.md](driver/README.md)); it needs a signature Windows will load, so
it is not shipped in the MSI. Design, wire format and limitations: [docs/lamparray.md](docs/lamparray.md).

The driver builds **without Visual Studio or a WDK install** — `driver/Dockerfile` cross-compiles it in a Linux
container with clang-cl/lld-link against the WDK/SDK NuGet packages, and the signing tools (`signtool`,
`inf2cat`) come out of those same packages:

```
docker build -t acerhelper-wdk driver
docker run --rm -v "$PWD/driver/AcerHelperLampArray:/src" acerhelper-wdk
```

## Build

The project multi-targets `net10.0-windows` (full Acer/Windows) and `net10.0` (portable; Acer,
Dell and generic Linux backends). CI (the `build` workflow — two parallel jobs) produces Native-AOT
artifacts per OS: an `AcerHelper.exe` + WiX MSI on a Windows runner, and a self-updating AppImage in
a Fedora container. Both are Native AOT. Locally:

```
# Windows (Native AOT — must run on Windows)
dotnet publish AcerHelper.csproj -c Release -f net10.0-windows -r win-x64 --self-contained true -p:PublishAot=true -o publish

# Linux (Native AOT — needs clang + zlib-devel to link)
dotnet publish AcerHelper.csproj -c Release -f net10.0 -r linux-x64 --self-contained true -p:PublishAot=true -o publish-linux
```

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
pkexec/polkit password prompt) that installs the udev/tmpfiles rules so the root-only controls become
writable — a portable binary can't ship system files itself, so this is the one privileged step.

```
chmod +x AcerHelper-x86_64.AppImage && ./AcerHelper-x86_64.AppImage
```

Build the AppImage locally: `dotnet publish … -p:PublishAot=true -o publish-linux` (above), assemble an
AppDir (the publish output + `packaging/{AppRun,acer-helper.desktop,acer-helper.png,60-acer-helper.rules,acer-helper.conf}`),
then `appimagetool AcerHelper.AppDir AcerHelper-x86_64.AppImage`.

## Roadmap

- Linux hardware backend — Acer probe-first: hidraw for RGB and the EC power envelope, `platform_profile`
  for the firmware profile set, Linuwu-Sense optional for the node-backed controls; evdev hotkeys, X/Wayland
  gamma, logind clamshell
- Additional vendors behind the same Domain ports
- Per-key RGB; fan curves
