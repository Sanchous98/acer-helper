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
- **`Os/`** (`AcerHelper.Os`) — genuinely vendor-agnostic OS services (blue-light gamma,
  autostart, clamshell) + a small WMI helper, also split by `*.Windows.cs` / `*.Linux.cs`.
- **`Composition/`** (`AcerHelper.Composition`) — `DeviceFactory.Windows.cs` / `DeviceFactory.Linux.cs`
  detect the device and assemble an `IDevice`; `CompositeDevice`, `JsonSettingsStore`.
- **root** (`AcerHelper`) — the application use cases (`LaptopService`, `Settings`) and the
  Avalonia UI (tray + windows), capability-driven (binds to `Features` only).

OS-specific code is selected by the `*.Windows.cs` / `*.Linux.cs` file-name suffix (MSBuild
`<Compile Remove>` globs per target framework) — **no preprocessor directives**. Adding a laptop
vendor = a new set of files under `Vendors/`; adding an OS = `*.Linux.cs` siblings. The UI never changes.

## Build

The project multi-targets `net10.0-windows` (full Acer/Windows) and `net10.0` (portable; Linux
backend is currently mostly stubs). CI (GitHub Actions, `acer-helper` workflow) produces a single
self-contained Native-AOT `AcerHelper.exe` on a Windows runner. Locally:

```
# Windows (Native AOT — must run on Windows)
dotnet publish AcerHelper.csproj -c Release -f net10.0-windows -r win-x64 --self-contained true -p:PublishAot=true -o publish

# Linux (self-contained single-file, JIT)
dotnet publish AcerHelper.csproj -c Release -f net10.0 -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish-linux
```

## Roadmap

- Linux hardware backend — Acer via Linuwu-Sense sysfs, evdev hotkeys, X/Wayland gamma, logind clamshell
- Additional vendors behind the same Domain ports
- Per-key RGB; fan curves
