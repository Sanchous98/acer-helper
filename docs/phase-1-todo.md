# Phase 1 TODO — Acer plugin (Native AOT, C-ABI)

Authoritative design: `docs/vendor-plugins.md` §4.4 (transport SDK), §6 Phase 1, §5.1 (layout).
This file tracks Phase 1. A task is `done` only after **both host TFMs build 0/0, the plugin builds
0/0, and the full test suite is green**. One task = one subagent.

> Tracker lives in git because the OpenCode `todowrite` tool is not registered in this harness.

## Status legend

`todo` · `doing` · `review` · `done` · `blocked`

## The shape of Phase 1 (the honest read)

Phase 1 is **not a file move**: `AcerDevice : GenericDevice` *fills the host's `Device`*, while a plugin
must *return a manifest and answer ops*. So the transfer is a **rewrite of the backend**, and the Acer
code currently depends on host-only types (`GenericDevice`, `Infrastructure/Lighting`, `Localization`).
Slices are ordered so the host tree stays green after each:

```
P1 extract SDK ──► P2 scaffold plugin (compiles standalone) ──► P3 backend → manifest+ops
                                                                          │
                                                          P4 cut-over: DeviceFactory + delete in-host Acer
```

## Tasks

| # | Task | Status | Depends | Notes |
|---|---|---|---|---|
| P1 | Extract the source-shared transport SDK (`Infrastructure/Plugins/Sdk/`, namespace renamed, `PluginSdk.props`) | **done** (`3a337e7`) | — | 9 files moved; 37 files re-pointed; 1909 tests |
| P2 | Scaffold `plugins/AcerHelper.Vendor.acer-nitro/` — existing Acer data/codec files compile standalone; `ah_*` thunks stubbed; **host untouched** | **done** | P1 | plugin 0/0 both TFMs; host 1913 tests |
| P3 | Rewrite the Acer backend to manifest + ops: implement `ah_create`/`ah_matches`/`ah_invoke` over the Acer data; port the wiring (`AcerDevice.InitVendor`, `AcerBattery.Windows`, `EneHidController` RGB, `AcerProfilePorts`); drop the `GenericDevice`/`Localization`/`Lighting` dependencies | **done** | P2 | plugin 0/0 both TFMs; host 1929 tests |
| P4 | Cut-over: `DeviceFactory` loads the best plugin (loader) and the Acer `if` is deleted; move/re-point Acer tests; the plugin is no longer compiled into the host | **done** | P3 | host IL carries no Acer (verified); 1930 tests |

## P2 — what the scaffold exposed (P3 dependencies)

1. `GenericDevice` — `AcerDevice : GenericDevice`; `ah_create` must rebuild `InitVendor` as a
   manifest/session without the host base class.
2. `AcerHelper.Infrastructure.Lighting` (`IRgbController`/`RgbDevice`) — blocks `EneHidController*.cs`.
3. `AcerHelper.Localization` (`Loc.T`) in `AcerProfilePorts.cs` — must become manifest-declared keys.
4. The wiring/data split: `AcerBattery.Windows.cs`/`BatteryWmi` and `AcerDevice.{Windows,Linux}.cs`.
5. P4 host reconciliation: importing `PluginSdk.props` into the host duplicates `MachineInfo`,
   `GateStats` and the relocated transports — they must be removed from the host's own tree then.

## P2 — files that could NOT compile standalone (P3 must handle)

| File | Blocker |
|---|---|
| `AcerDevice.cs` | `: GenericDevice` + `Infrastructure/Lighting` |
| `AcerDevice.Windows.cs` / `.Linux.cs` | same, plus host device wiring |
| `AcerBattery.Windows.cs` | wiring (compiles, but deferred) |
| `EneHidController*.cs` | `Infrastructure/Lighting` (`IRgbController`, `RgbDevice`) |
| `AcerProfilePorts.cs` | `Localization` (`Loc.T`) |
