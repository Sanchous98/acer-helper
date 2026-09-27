# Phase 0 TODO — vendor plugins (Native AOT, C-ABI)

Authoritative design: `docs/vendor-plugins.md` (Phase 0, §6). This file is the live task tracker for
Phase 0. One task = one subagent; the owner files are disjoint so agents never collide on the same file.
A task is `done` only after **both TFMs build 0/0 and the full test suite is green**.

> Why a file and not a chat list: the OpenCode `todowrite` tool is not registered in this session's
> harness (direct call returns `Unknown tool 'todowrite'`; the catalog exposes only `browser.*` +
> `opencode.*`). This markdown file is the standing replacement.

## Status legend

- `todo` — not started
- `doing` — a subagent is working on it
- `review` — implementation returned, being verified (build + suite) by the orchestrator
- `done` — verified and committed
- `blocked` — waiting on a decision or another task

## Dependency order

```
T1 ──► T2 ─┐
      ├──► T4 ──► T5 ──► T6 ──► T7 ──► T8
T1 ──► T3 ─┘
```

T1 and T3 must both land before T4. Run tasks strictly one at a time (shared `obj/` — parallel builds
conflict).

## Tasks

| # | Task | Status | Depends | Owner files |
|---|---|---|---|---|
| T1 | ABI foundation (`PluginApi` generation, `Abi/`, `PluginManifest`, `PluginJsonContext`, `IPluginSession`) | **done** | — | `AcerHelper.csproj`, `Infrastructure/Plugins/**` |
| T2 | Manifest + signature (`vendor-plugins.json` schema, canonical string, ECDSA P-256 verifier, `keyId`) | **done** | T1 | `Infrastructure/Plugins/Distribution/**` |
| T3 | Per-major adapters (`IPluginAbiAdapter`, `PluginAbiRegistry`, `Abi/V1/`) | **done** | T1 | `Infrastructure/Plugins/Abi/**` (Registry/V1) |
| T4 | Loader / session / binding (`INativePluginBinding` + `NativeLibrary`, `VendorPluginLoader`, `PluginSession`) | **done** | T1, T3 | `Infrastructure/Plugins/VendorPluginLoader*`, `PluginSession*`, `NativePluginBinding*` |
| T5 | Capability adapters (`PluginPowerProfiles/Fan/Sensors/Battery/DeclaredSetting/Hotkeys`, `PluginVendorDevice`) — **not wired into `DeviceFactory`** | **done** | T1 | `Infrastructure/Plugins/Adapters/**` |
| T6 | Proof plugin (`plugins/AcerHelper.Vendor.Proof/`) + a synthetic deprecated-major fixture | todo | T1 | `plugins/**` |
| T7 | CI proof job (AOT-publish the proof plugin Win+Linux, smoke + 30-min soak) | todo | T6 | `.github/workflows/**`, test runner |
| T8 | Guards (host has no vendor names; every plugin exports the ABI names; ABI round-trip) | todo | T1–T6 | `tests/**` (new files only) |

### T1 — ABI foundation (`done`, commit pending)

Delivered: `<PluginApiVersion>1.0</PluginApiVersion>` + `GeneratePluginApiVersion` target (generated
`PluginApi` const, sibling of `GenerateAppVersion`); `plugins/**` excluded from the host build;
`AbiStatus`, `Capability`, `Operation`, `VendorAbi` + `VendorAbiExports` (the eight `ah_*` names and
`delegate* unmanaged[Cdecl]` signatures); `PluginManifest` DTOs; `PluginJsonContext` (source-gen);
`IPluginSession` + `PluginCallResult`. Tests: `PluginAbiFoundationTests` (7).
Result: net10.0 / net10.0-windows 0 warnings / 0 errors; **1791 passed / 0 failed**.

## Decisions locked for Phase 0

- Authenticode: **no** for v1 (app's own signature verification is the gate).
- Arch: **x64 only** for v1 (arm64 in the manifest later).
- Signing primitive: **ECDSA P-256** (Ed25519 only as a fallback if unsupported).
- Plugin search path: per-user writable cache + `%ACERHELPER_PLUGIN_DIR%` override.
- API version: separate from the app version; host holds current + deprecated major.

## Open knobs (owner, non-blocking for Phase 0)

See `docs/vendor-plugins.md` §7.3 (Authenticode cert, key rotation, Nitro/Predator discrimination,
`acer-models.json` split, manifest cadence, arm64 timing, deprecated-major removal interval).
