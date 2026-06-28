# Acer Helper

A lightweight tray app (C# / .NET 8 WinForms) — an open, minimal alternative to
NitroSense, in the spirit of [G-Helper](https://github.com/seerge/g-helper) but
for Acer Nitro / Predator laptops.

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

## Build

CI (GitHub Actions, `acer-helper` workflow) produces a single self-contained
`AcerHelper.exe`. Locally:

```
dotnet publish acer-helper/AcerHelper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Roadmap

- Sensors (CPU/GPU temp, fan RPM) — `GetGamingSysInfo`
- Keyboard + lightbar RGB — ENE HID (shared with the OpenRGB plugin)
- Fan control / battery charge limit (needs more reverse engineering)
- Global hotkey to cycle profiles
