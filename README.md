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

## Requirements

- Acer gaming laptop exposing `AcerGamingFunction` (Nitro / Predator, recent gen).
- Windows 10/11.
- **Run as Administrator** (Acer WMI/ACPI methods require elevation — the app
  manifest already requests it).
- Stop NitroSense / `PredatorSenseService` if running, or it may override changes.

## Architecture

One project, organised by module; **namespaces match the directories** (`AcerHelper` + path):

- **`Features/`** (`AcerHelper.Features`) — the vendor- and OS-agnostic core: model
  (`PerformanceProfile`, `FanMode`, `SensorSnapshot`, `HotkeyAction`, …) and one fine-grained
  *port* per capability (`IPowerProfiles`, `IFanControl`, `ISensors`, `ILcdOverdrive`,
  `IBatteryChargeLimit`, `IUsbCharging`, `IKeyboardBacklight`, `ILighting`, `IHotkeys`,
  `IDisplayTint`, `IAutostart`, `IClamshell`). The aggregate `IDevice` exposes each port as
  **nullable** — `null` means the feature is absent, so the UI shows exactly what the hardware has.
- **`Vendors/Acer/`** (`AcerHelper.Vendors.Acer`) — Acer feature implementations. There is **no
  separate platform layer**: the OS access is folded into the vendor implementation, split per OS
  by file name — `AcerGaming.Windows.cs` (WMI), and future `*.Linux.cs` (sysfs) sit side by side.
  Within Acer, capabilities are **probed at runtime** (RGB device present? EC supported-profile
  mask? nullable WMI getters?) — so most models work without an entry. Profiles (shared enum) and
  fan topology (dual) are not per-model. The only un-probeable per-model bits — friendly name and
  RGB layout (zone count, lightbar) — live in a config file **`acer-models.json`** (embedded
  default + optional user override at `%AppData%/AcerHelper` / `~/.config/AcerHelper`), matched by
  DMI product name via `AcerModels.Detect`. (Design validated against Linuwu-Sense and G-Helper:
  probe-first, with a thin per-model quirks/override table.)
- **`Os/`** (`AcerHelper.Os`) — genuinely vendor-agnostic OS services: **performance profiles via
  standard OS APIs** (Windows power-mode overlay / Linux ACPI `platform_profile`), blue-light
  gamma, autostart, clamshell + a small WMI helper, also split by `*.Windows.cs` / `*.Linux.cs`.
- **`Composition/`** (`AcerHelper.Composition`) — `DeviceFactory.Windows.cs` / `DeviceFactory.Linux.cs`
  detect the device and assemble an `IDevice`; `CompositeDevice`, `JsonSettingsStore`. When no
  vendor backend matches (a non-Acer laptop, or no elevation), it falls back to a **generic
  device** offering those OS-standard basics — so the app is useful on any laptop. (Validated on a
  Dell Latitude 5540 on Linux: shows the firmware's cool/quiet/balanced/performance profiles.)
- **root** (`AcerHelper`) — the application use cases (`LaptopService`, `Settings`) and the
  Avalonia UI (tray + windows), capability-driven (binds to `Features` only).

OS-specific code is selected by the `*.Windows.cs` / `*.Linux.cs` file-name suffix (MSBuild
`<Compile Remove>` globs per target framework) — **no preprocessor directives**. Adding a laptop
vendor = a new set of files under `Vendors/`; adding an OS = `*.Linux.cs` siblings. The UI never changes.

## Build

The project multi-targets `net10.0-windows` (full Acer/Windows) and `net10.0` (portable; Acer,
Dell and generic Linux backends). CI (GitHub Actions, `acer-helper` workflow) produces a
self-contained Native-AOT `AcerHelper.exe` + a Windows installer on a Windows runner, and an RPM
on a Fedora runner. Locally:

```
# Windows (Native AOT — must run on Windows)
dotnet publish AcerHelper.csproj -c Release -f net10.0-windows -r win-x64 --self-contained true -p:PublishAot=true -o publish

# Linux (self-contained single-file, JIT)
dotnet publish AcerHelper.csproj -c Release -f net10.0 -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish-linux
```

## Install (Windows)

The `windows` workflow builds an **MSI** (`packaging/AcerHelper.wxs`, WiX) from the publish folder — run
`AcerHelper-Setup.msi` to install to Program Files with a Start-menu shortcut + uninstaller (admin
elevation prompt; the app self-elevates at runtime too). WiX only builds on Windows, so build it there:

```
dotnet publish AcerHelper.csproj -c Release -f net10.0-windows -r win-x64 --self-contained true -p:PublishAot=true -o publish
dotnet tool install --global wix --version 5.0.2
wix build packaging\AcerHelper.wxs -arch x64 -d Version=0.14.0 -d PublishDir=publish -o AcerHelper-Setup.msi
```

## Install (Linux)

CI builds an **RPM** (`linux-rpm` job / `packaging/acer-helper.spec`). It bundles the binary plus the udev +
tmpfiles rules that grant the `wheel` group write access to the root-only control nodes (battery charge
mode, keyboard backlight, thermal profile, Dell BIOS attributes), so the app runs **unprivileged**.

```
# Traditional Fedora (dnf): %post reloads udev + tmpfiles, effective immediately.
sudo dnf install ./acer-helper-*.rpm

# Atomic Fedora (Silverblue/Kinoite/uBlue — rpm-ostree): layer it, then reboot to apply.
rpm-ostree install ./acer-helper-*.rpm && systemctl reboot   # uninstall: rpm-ostree uninstall acer-helper
```

On atomic systems the RPM's `%post` runs in the compose chroot (no live system), so it no-ops and the rules
take effect on the reboot rpm-ostree needs anyway (udev + systemd-tmpfiles run at boot). To apply the rules
**immediately, without layering or reboot**, drop them into the writable `/etc` (it overrides `/usr` and
survives ostree updates):

```
sudo install -m0644 packaging/60-acer-helper.rules /etc/udev/rules.d/
sudo install -m0644 packaging/acer-helper.conf     /etc/tmpfiles.d/
sudo udevadm control --reload-rules && sudo udevadm trigger \
     --subsystem-match=power_supply --subsystem-match=leds --subsystem-match=platform-profile
sudo systemd-tmpfiles --create /etc/tmpfiles.d/acer-helper.conf
```

Build the RPM locally (needs `rpm-build` + `systemd-rpm-macros`):

```
V=$(grep -oPm1 '(?<=<Version>)[^<]+' AcerHelper.csproj)
mkdir -p ~/rpmbuild/SOURCES && rm -f publish-linux/*.pdb
tar -czf ~/rpmbuild/SOURCES/acer-helper-$V-linux-x64.tar.gz -C publish-linux .
cp packaging/{60-acer-helper.rules,acer-helper.conf,acer-helper.desktop} ~/rpmbuild/SOURCES/
rpmbuild -bb --define "appversion $V" packaging/acer-helper.spec
```

## Roadmap

- Linux hardware backend — Acer via Linuwu-Sense sysfs, evdev hotkeys, X/Wayland gamma, logind clamshell
- Additional vendors behind the same Domain ports
- Per-key RGB; fan curves
