# Vendor laptops as loadable Native AOT plugins

Status: **design + reconnaissance, 2026-09-27; revised 2026-09-27 with the owner's decisions.** No production
code, csproj or test was modified by the task that produced this file; the only artefact is this document. It
is a proposal, and every claim about the current tree is cited with a file path and line number so it can be
checked rather than believed. Every claim about .NET is cited to Microsoft Learn or dotnet/runtime.

**Owner decisions folded into this revision** (authoritative, each implemented in the section named):

1. Plugin identity is a **model line**, not a vendor — `acer-nitro`, `acer-predator`, `dell-xps`, … (§1.4, §3.2).
2. Plugins are **published as GitHub Release assets** (one per OS/arch + a `vendor-plugins.json` manifest), **not bundled in the MSI/AppImage**; the host binary ships no vendor plugin (§5.2).
3. The host **downloads the matching plugin at runtime**, verifies it, caches it per-user, and offers a **restart to enable it** — reusing the existing update machinery (§5.3).
4. The plugin cache is a **writable per-user directory** (§5.3.1).
5. **Code signing is required**: a detached Ed25519-or-ECDSA signature over the manifest entry, public key embedded in the host (§5.4).
6. **Acer is the first plugin** (Windows then Linux); Dell/ASUS later (§6).
7. §7's open questions are resolved and the genuinely-still-open ones listed (§7).
8. **The host versions its plugin API separately from the app and checks the manifest for compatibility**: a host-owned `PluginApiVersion` (not `<Version>`), the current major plus a deprecated one held at once via per-major adapters, and three gates (manifest / load / handshake) (§3.8).

## 0. The goal, stated as a measurement

The owner's rule is that an **ASUS build must not carry the Acer integration and vice versa**.
Today they do, because every vendor backend is compiled into the one shipping assembly:

- `AcerHelper.csproj:4` — `<TargetFrameworks>net10.0-windows;net10.0</TargetFrameworks>`, one project,
  one assembly for all layers (README "Architecture").
- `Infrastructure/Vendors/{Acer,Dell,Generic}/` are compiled unconditionally. Only the **OS** axis is
  selected by file name: `AcerHelper.csproj:41-46` removes `**/*.Linux.cs` on the Windows TFM and
  `**/*.Windows.cs` on the portable TFM. **No vendor axis exists.**
- `DeviceFactory.Create` (`Infrastructure/Composition/DeviceFactory.cs:28-42`) names `AcerDevice`,
  `DellDevice` and `GenericDevice` directly, and the branches are the only vendor dispatch there is.

Measured vendor code volume (lines, `Get-Content | Measure-Object -Line`, 2026-09-27):

| Subtree | Files | Lines | Ships in a non-Acer build today? |
|---|---:|---:|---|
| `Infrastructure/Vendors/Acer/` | 19 | 2 403 | yes (both TFMs) |
| `Infrastructure/Vendors/Dell/` | 4 | 299 | yes (both TFMs) |
| `Infrastructure/Vendors/Generic/` | 57 | ~6 900 | yes (it is the OS backend, not a vendor) |
| Tests (`tests/AcerHelper.Tests`) | 91 | 1 123 `[Fact]`/`[Theory]` methods | n/a (excluded from the app, `AcerHelper.csproj:79-83`) |

The measured fact that forces the whole design: **a vendor backend does not stand alone.** `AcerDevice`
and `DellDevice` extend `GenericDevice` and are wired against *generic* infrastructure that is also
used by the OS backend and by the tests:

- `AcerDevice.cs:15` — `public sealed partial class AcerDevice : GenericDevice`.
- `DellDevice.cs:16` — `public sealed partial class DellDevice : GenericDevice`.
- `AcerDevice.Windows.cs:29,95,105` uses `WmiInvoker`; `DellBiosWmi.Windows.cs:26` uses `WmiSession`
  (`Infrastructure/Vendors/Generic/WmiSession.Windows.cs`).
- `DellDevice.Linux.cs:27,56,71` uses `SysfsInvoker`; `:83` uses `FirmwareAttributes`.
- `AcerDevice.Windows.cs:44-45` builds `ProfilesPort`/`ProfileTraitsLookup`
  (`Infrastructure/Vendors/Generic/DelegatePorts.cs:59-79`, `ProfileKind.cs:83-86`).
- `AcerProfilePorts.cs:1` and `AcerFanPort.cs:1` take their I/O as delegates built from
  `DelegatePorts`-style holders.

So the boundary is not "move `Vendors/Acer` out". It is "move the vendor-specific half of the wiring
out **together with the minimal set of generic transports it drives**", which is worked out in §4 and
§6. That is the single most important finding of the reconnaissance.

---

## 1. Surface inventory: exactly what a vendor fills, and what the host calls

### 1.1 The model a backend fills

`Device` is a class, not an interface (`Infrastructure/Composition/Device.cs:32`), and a backend
writes its members from its constructor. Every slot is `nullable` and `null` means "this machine does
not have it". The complete surface, with the slot declaration and the production call sites.

| Slot (declaration) | Domain/Infra | Backend that fills it | Host call sites (prod, non-vendor) |
|---|---|---|---:|
| `VendorName` `Device.cs:36` | Infra | Acer/Dell/Generic ctor | 0 direct; read by UI/tray indirectly |
| `StatusMessage` `Device.cs:40` | Infra | Acer/Dell ctor | 1 (`UI/AppController.cs:880`) |
| `PowerProfiles` `Device.cs:42` | Domain `Ports.cs:17` | Acer, Dell, Generic | 25 |
| `FanControl` `Device.cs:43` | Domain `Ports.cs:47` | Acer, Generic(Linux) | 3 |
| `GpuMux` `Device.cs:44` | Domain `GpuMux.cs:51` | Acer only (ASUS future) | 6 |
| `Sensors` `Device.cs:45` | Domain `Ports.cs:56` | Acer, Generic(Linux) | 5 |
| `Battery` `Device.cs:46` | Domain `Battery.cs:22` | all (properties added per vendor) | 32 |
| `KeyboardBrightness` `Device.cs:47` | Domain `Ports.cs:103` | Dell(Win), Generic(Linux) | 9 |
| `Lighting` (`IRgbDevice`) `Device.cs:48` | Domain `Rgb.cs:90` | Acer (ENE HID) | 15 |
| `Hotkeys` `Device.cs:49` | Domain `Ports.cs:115` | Acer only | 3 |
| `DisplayTint` `Device.cs:50` | Domain `Ports.cs:126` | Generic (both OS) | 3 |
| `GpuOverclock` `Device.cs:51` | Domain `Ports.cs:138` | Generic (Nvidia, both OS) | 3 |
| `CpuPower` `Device.cs:52` | Domain `Ports.cs:159` | Generic (Win overlay) | 8 |
| `CurveOptimizer` `Device.cs:53` | Domain `Ports.cs:180` | Generic (AMD SMU, both OS) | 17 |
| `DriverSetup` `Device.cs:54` | Domain `Ports.cs:258` | Generic (PawnIO, Win) | 1 |
| `Autostart` `Device.cs:55` | Domain `Ports.cs:274` | Generic (both OS) | 3 |
| `Clamshell` `Device.cs:56` | Domain `Ports.cs:287` | Generic (Win) | 10 |
| `CoreAffinity` `Device.cs:63` | Infra (`CpuLoadTest.cs:22`) | Generic (both OS) | 4 |
| `DeclaredSettings` `Device.cs:75-82` | Domain `DeclaredSetting.cs:26` | Acer, Dell | read via `LaptopService.DeclaredSettings` (`LaptopService.Toggles.cs:25`) into `OptionsAssembler` |
| `Dispose()` `Device.cs:94` | Infra | base (Own list `Device.cs:84-87`) | `LaptopService.cs:263` |
| `FinalizeComposition()` | Infra | base (`GenericDevice.cs:42`) | `DeviceFactory.cs:40` |

**Classification that matters for the split.** Three groups fall out of the table:

1. **Vendor-proprietary, must move into a plugin**: `PowerProfiles`, `IProfileTraits`,
   `IProfileAvailability`, `FanControl`, `GpuMux`, `Hotkeys`, `Lighting`, the *overridden* `Sensors`,
   the *added* `Battery` properties (`ChargeLimit`, `Calibration`, `ChargeMode`, `PowerSource` — see
   `Battery.cs:28-47`), `KeyboardBrightness` on a Dell, and `DeclaredSettings` / `VendorName` /
   `StatusMessage`.
2. **OS-generic, must stay in the host**: `DisplayTint`, `GpuOverclock`, `CpuPower`, `CurveOptimizer`,
   `DriverSetup`, `Autostart`, `Clamshell`, `CoreAffinity`, and the generic `Battery.Telemetry`.
   These are written by `GenericDevice`/`GenericDevice.{Windows,Linux}.cs` and are cross-vendor by
   construction (`GenericDevice.Windows.cs:15-30`, `GenericDevice.Linux.cs:33-63`).
3. **Both**: `Sensors`, `FanControl`, `KeyboardBrightness`, `Battery` properties — the host wires a
   generic implementation and the plugin *replaces or extends* it. The ABI must therefore be able to
   say "vendor overrides this slot", not just "vendor has it".

### 1.2 Per-port member surface (the ABI's method set)

Each port is small. Enumerated from the declarations:

| Port | Members the host calls | Thread |
|---|---|---|
| `IPowerProfiles` `Ports.cs:17-26` | `All`, `Selectable()`, `Current()`, `Set(p)`, `LastError` | UI + background pass |
| `IProfileTraits` `ProfileKind.cs:72-77` | `Traits(p)` | UI + background |
| `IProfileAvailability` `Ports.cs:39-44` | `AvailableOn(onAc)` | background/UI |
| `IFanControl` `Ports.cs:47-53` | `Capability`, `SetMode(m)`, `SetCustomSpeeds(c,g)`, `LastError` | UI + background |
| `ISensors` `Ports.cs:56-59` | `Read()` | background (3 s) |
| `IGpuMux` `GpuMux.cs:51-72` | `Supported`, `Modes`, `Read()`, `Request(id)`, `LastError` | UI, on open / after request |
| `IRgbDevice` `Rgb.cs:90-108` | `Zones`, `SetProfileFlash(c)`, `Blank()`, `ProfileFollowKey` | UI + coordinator |
| `IHotkeys` `Ports.cs:115-123` | `event Pressed`, `event InputActivity`, `Dispose` | plugin threads → UI |
| `IKeyboardBrightness` `Ports.cs:103-109` | `MaxLevel`, `Get()`, `Set(l)` | UI + background |
| `IDisplayTint` `Ports.cs:126-130` | `Levels`, `Apply(l)` | worker (`TintApplyPolicy`) |
| `IGpuOverclock` `Ports.cs:138-150` | `Name`, `CoreRange`, `MemRange`, `Set`, `LastError` | UI + background |
| `ICpuPower` `Ports.cs:159-168` | `Modes`, `Current()`, `Set(id)`, `LastError` | UI + background |
| `ICurveOptimizer` `Ports.cs:180-209` | `Name`, `Range`, `MillivoltsPerCount`, `Domains`, `SetDomains`, `Set`, `LastError` | worker (SMU) |
| `IAutostart` `Ports.cs:274-284` | `Label`, `IsEnabled`, `SetEnabled`, `EnsureCurrent` | pool (`AppController.cs:203`) |
| `IClamshell` `Ports.cs:287-293` | `Label`, `Enabled`, `SetEnabled`, `Evaluate` | UI + background + OS callback |
| `Battery` `Battery.cs:22-53` | `Telemetry`, `Read()`, `ChargeLimit`, `Calibration`, `ChargeMode`, `PowerSource`; each toggle is `(Func<bool> Read, Func<bool,(bool,string?)> Write)` (`Battery.cs:63`) | background (1 s / 5 s) |
| `SettingDeclaration` `DeclaredSetting.cs:26-69` | `Key`, `ReadbackVerifiesWrite`, `Apply(value)`; `FlagSetting.Read()`, `ChoiceSetting.Options/Read/IndexOf` | row worker (`OptionsAssembler.cs:181-198`) |

Measured concurrency facts that the ABI has to respect (they are already in the tree and are not
being changed):

- All hardware writes are made **outside** `LaptopService._state` (`LaptopService.cs:171-173`,
  `docs/domain-refactoring-plan.md` §4). The plugin's `Invoke` is therefore allowed to block for the
  duration of one transport transaction, but the host must never hold `_state` across it.
- The **background pass** (`UI/AppController.cs:863-958`) already calls `PowerProfiles.Current()`,
  `Sensors.Read()`, `Battery.Read()`, `FanControl` (via `ApplyCustom`, `LaptopService.Fans.cs:240-252`)
  and the reconciler's axes on a pool thread.
- `AcerEcHidController` (`AcerEcHidController.cs:42-214`) and `EneHidController`
  (`EneHidController.cs:15-234`) own **their own background writer threads** and are fire-and-forget
  from the caller. Those threads live inside the plugin if the plugin owns the controller.
- `GpuMux.Read/Request` are **UI-thread only, never on a timer** (`LaptopService.Tuning.cs:419-428`).
- `ICurveOptimizer.Set*` is called **off the UI thread** and can block seconds on the SMU PCI lock
  (`LaptopService.Tuning.cs:167-169`).

### 1.3 OS-specific vs pure-data vendor code

| Acer file | Kind | Moves? |
|---|---|---|
| `AcerProfiles.cs` (102 l) | pure vendor data (byte table, traits, availability) | yes — plugin |
| `AcerModel.cs` (123 l) | pure data + embedded `acer-models.json` + user override | yes — plugin (+ resource) |
| `AcerProfilePorts.cs` (161 l) | pure decorator policy (EC envelope, kernel-token mapping) | yes — plugin |
| `AcerFanPort.cs` (292 l) | pure policy + channel/scale tables (I/O passed in) | yes — plugin |
| `AcerGpuMux.cs` (156 l) | pure protocol + port | yes — plugin |
| `AcerHotkeyReports.cs` (46 l) | pure decode (shared by both OS halves) | yes — plugin |
| `RgbEffects.cs` (51 l) | pure RGB effect table | yes — plugin |
| `EneHidController.cs` (214 l) | codec + zone model (transport is per-OS) | yes — plugin |
| `EneHidController.{Windows,Linux}.cs` | OS transport | yes — plugin, per-OS |
| `AcerEcHidController.cs` (205 l) | codec + coalescing writer | yes — plugin |
| `AcerEcHidController.{Windows,Linux}.cs` | OS transport | yes — plugin, per-OS |
| `AcerDevice.cs` / `.{Windows,Linux}.cs` | wiring + WMI/sysfs encoding | yes — plugin |
| `AcerBattery.Windows.cs` (49 l) | WMI encoding helper (`BatteryWmi`) | yes — plugin |
| `AcerHotkeys.{Windows,Linux}.cs` | OS key capture | yes — plugin, per-OS |
| `Dell*` (4 files) | wiring + `DellBiosWmi` WMI | yes — plugin |
| `Generic/*` | OS backend + shared transports | **split** — see §4.4 |

**One measured surprise that changes the plan.** The RyzenSMU module is **not** vendor code: it is
embedded by the Windows TFM unconditionally (`AcerHelper.csproj:98-158`, `third-party/RyzenSMU.bin`)
and used by `Infrastructure/Vendors/Generic/RyzenCurveOptimizer.Windows.cs` — the AMD/CPU axis, which
is cross-vendor (`GenericDevice.Windows.cs:21-25`). It must **stay in the host**, not move to the
Acer plugin. Only two vendor-owned resources move: `acer-models.json` (`AcerHelper.csproj:93-96`) and
the Acer/Dell code itself.

### 1.4 Granularity: one plugin per model line, not per vendor

**Owner decision (2026-09-27): a plugin's identity is a LAPTOP LINE / MODEL FAMILY, not a vendor.** The
build approach is unchanged — each backend is a Native AOT shared library loaded with `NativeLibrary.Load`
(§2–§4). What changes is the *identity*: the library writes a line id (`acer-nitro`, `acer-predator`,
`acer-swift`, `dell-xps`, `asus-rog`, `generic`), several plugins may exist for one vendor, and the host
scans them all, calls `ah_matches`, and loads the **highest-confidence** match. No match (or a tie above
threshold, §4.1) falls back to the in-host `GenericDevice`.

The motivation is measured: within one vendor the firmware surface is not uniform, and the tree already
models vendor data *per model* rather than per vendor (`AcerModel`, `acer-models.json`). But the evidence
for a *line* split is thinner than the mechanism, and the honest table is this:

| Line id | Known members | Evidence in this tree for line-specific behaviour | Verdict |
|---|---|---|---|
| `acer-nitro` / `acer-predator` | AN18-61 (the reference machine) | `acer-models.json:11` has ONE entry (`AN18`/`Nitro 18`): friendly name + RGB layout only. `AcerProfiles.cs:8-10` states the profile enum is **shared across the whole gaming line** ("Predator and Nitro use the same profile enum"). `AcerEcHidController.cs:6-9` calls the EC usage-mode channel "recent Nitro/Predator models", probed by VID/PID. `AcerDevice.Windows.cs:29-110` probes WMI method availability (`AcerGamingFunction`, `BatteryControl`, `APGeAction`) rather than gating on a line. | **No in-tree evidence that Nitro and Predator differ in WMI or EC envelopes.** The split is an OPEN QUESTION (verify on hardware). |
| `acer-swift` (non-gaming) | none in tree | A non-gaming Acer is reached only by `acer-models.json`'s `default`; the gaming WMI and the EC channel are *probed absent* (`AcerDevice.Windows.cs:30-34` early-returns to the inherited generic ports). | Difference from `acer-nitro` not established; such a machine may need only the generic path. |
| `dell-xps`, `asus-rog` | none in tree | Dell exists as a vendor backend (`Infrastructure/Vendors/Dell/`); ASUS does not. No line data in either. | Later phases (§6); no evidence yet. |

What the tree *does* establish as per-model (not per-line) is exactly two fields: the friendly display name
and the keyboard RGB layout (`zones`, `lightbar`) — `AcerModel.cs:18-25`. Everything else is *probed*
(`AcerModel.cs:10-16`: "Most capabilities are PROBED at runtime… Profiles and fan topology are NOT
per-model on Acer"). The Linux `predator_v4=1` module parameter (`AcerDevice.Linux.cs:14-16`) is a
per-repo constant, not per-line data.

**Recommendation, stated plainly:** implement the line-grade `Matches` mechanism now (§3.2), but **ship one
Acer plugin first** and split into `acer-nitro`/`acer-predator` only when a hardware measurement shows a
real difference. The plugin id is opaque to the host, so the split is then a data/CI change, not an ABI
change. Writing two Acer plugins that contain the *same* WMI code would be inventing a difference the
reconnaissance did not find.

#### How a line plugin relates to `acer-models.json`

**Decision: `acer-models.json` moves INTO the line plugin; it does not stay host-side.** Reasons:

1. It is keyed on DMI product substrings and carries vendor vocabulary (`"Acer Nitro 18"`, `"AN18"` —
   `AcerModel.cs:20-22`, `acer-models.json:11`). A host-side DB is exactly the vendor knowledge the
   architecture guard (§6) forbids in the host.
2. Its `Match` rules are the same DMI product matching `ah_matches` must do. Leaving them host-side makes
   the host parse Acer product strings and then hand the plugin a decision it already made — the host would
   have to know the line identities it is explicitly not allowed to know.
3. Its only consumer is `EneHidController`'s zone/lightbar construction inside the plugin
   (`AcerDevice.cs:53` — `new EneHidController(_model.Zones, _model.Lightbar, …)`); no host code reads
   `AcerModel`.

So the embedded resource (`AcerHelper.csproj:93-96`) and `AcerModelJsonContext` (`AcerModel.cs:34-37`) move
with the code. The user override path is preserved verbatim — the plugin reads
`%AppData%\AcerHelper\acer-models.json` / `~/.config/AcerHelper/acer-models.json` (`AcerModel.cs:88-105`)
in-process, so the no-rebuild escape hatch still works. Each line plugin embeds only its line's slice of the
DB, so its `Match` substrings double as the line discriminator. **OPEN QUESTION (§7.3 item 4):** whether each
plugin embeds its own slice, or all Acer line plugins source-include one shared `acer-models.json` (like the
SDK in §4.4) so the match rules cannot drift between lines.

Alternative considered and rejected: keep a *neutral* host-side DB (a pure `product substring → (zones,
lightbar)` table with no vendor names). Rejected: it still has to match on the exact product strings, which
are vendor data, and its only consumer is the vendor HID codec.

---

## 2. Native AOT shared libraries: verified facts, with sources

### 2.1 Publishing a class library as a shared library

- Microsoft Learn, *Building native libraries*, https://learn.microsoft.com/dotnet/core/deploying/native-aot/libraries:
  "Publishing .NET class libraries as Native AOT allows creating libraries that can be consumed from
  non-.NET programming languages… **Only shared libraries (also known as DLLs on Windows) are
  supported.** Static libraries are not officially supported… Publishing a class library as Native AOT
  creates a native library that exposes methods… annotated with `UnmanagedCallersOnlyAttribute` with a
  non-null `EntryPoint` field."
- `dotnet/runtime`, `Microsoft.NETCore.Native.targets`:
  `<NativeLib Condition="'$(OutputType)' == 'Library' and '$(NativeLib)' == '' and '$(IlcMultiModule)' != 'true'">Shared</NativeLib>`.
  So a `classlib` with `PublishAot=true` and no explicit `NativeLib` **is** a shared library. The
  extension is chosen per OS: `.dll` (win), `.dylib` (apple), `.so` (everything else).
- The dotnet/samples `NativeLibrary` README and `LibraryFunctions.cs` show the shape and the
  restrictions:
  - exported methods must be **static**;
  - they accept only **primitives/blittable value types**; reference types must be marshalled by hand;
  - they **cannot be called from managed C#** (calling one throws);
  - they **cannot use regular C# exception handling** — "they should return error codes instead".
- The `UnmanagedCallersOnlyAttribute` reference additionally states: blittable arguments only, no
  generic type parameters, not contained in a generic class, `CallConvs` optional (default platform
  calling convention).
- **Critical for this design**: "Only methods marked `UnmanagedCallersOnly` in the **published
  assembly** are considered. **Methods in project references or NuGet packages won't be exported.**"
  (Learn, *Native code interop with Native AOT*, "Native exports"). This is why the ABI cannot be a
  referenced "ABI" class library whose methods get exported; the exported entry points must live in
  the plugin assembly itself. A shared ABI *source* project (compiled into each plugin, not
  referenced as a package) is fine — see §3.1.

Resulting artefact, verified against the sample: `dotnet publish -r <RID> Plugin.csproj` with
`PublishAot=true` drops `Plugin.dll` / `Plugin.so` (+ symbols) in
`bin/Release/<tfm>/<rid>/publish/`.

### 2.2 What the plugin may do internally

- **P/Invoke is supported and bound lazily by default**: Learn, *Native code interop with Native AOT*,
  "Direct P/Invoke calls: The P/Invoke calls in AOT-compiled binaries are bound lazily at runtime by
  default." So the plugin may keep driving `hidraw`/WMI/`SetupAPI` exactly as it does now. The
  `DllImport`/`LibraryImport` in `AcerHotkeys.Windows.cs:47-58` and the hand-written `delegate* unmanaged`
  in `NvidiaGpu.Windows.cs:11-13` both keep working inside an AOT plugin.
- **.NET 10 changes the native search path**: Learn breaking change *Native library search*
  (`.NET 10`): the application directory is no longer added to `NATIVE_DLL_SEARCH_DIRECTORIES` and the
  implicit NativeAOT `rpath` was removed. The host must load plugins by **absolute path** (it will
  anyway — `NativeLibrary.Load(fullPath)`), and a plugin that P/Invokes a sibling native lib must
  either link it (`<LinkerArg Include="-Wl,-rpath,...">`) or pass an absolute path.
- **`System.Text.Json` source generation is AOT-safe**: Learn, *How to use source generation in
  System.Text.Json* — a `partial class : JsonSerializerContext` with `[JsonSerializable]` works under
  Native AOT. `JsonSerializer.Serialize` is annotated `RequiresDynamicCode`; the overloads taking a
  `JsonTypeInfo`/`JsonSerializerContext` are the AOT path. The tree already uses this pattern
  (`AcerModel.cs:34-37`, `AcerModelJsonContext`).

### 2.3 Unloading: it cannot be done

- Learn, *Building native libraries*: "**Unloading Native AOT libraries (via `dlclose` or
  `FreeLibrary`, for example) is not supported.**"
- The dotnet/samples README repeats it: "Note that the .NET Runtime does not support unloading. Once
  a handle to the shared library is created, the library cannot be closed with `dlclose`/`FreeLibrary`."
- `dotnet/runtime#64629`, *Native aot'ed shared libraries should not be unloadable on unix*: on
  Windows the library is pinned so it cannot be unloaded; on Unix the runtime does not support
  `dlclose` either. `NativeLibrary.Free` (`System.Runtime.InteropServices.NativeLibrary.Free`) will
  release the OS handle, but no part of the AOT runtime, and the module's code/state is not
  reclaimed. `Marshal.GetDelegateForFunctionPointer`'s remarks and `NativeLibrary` docs note the
  runtime does no lifetime management of resolver handles.

**Consequence, stated honestly**: a "plugin" here is *in-process trusted native code*, not isolation.
A plugin that crashes or corrupts memory takes the host down; it can never be swapped at runtime.
`Dispose` releases the vendor's own OS handles (HID streams, WMI sessions, worker threads) but the
module stays mapped until process exit. That is why a downloaded plugin takes effect only on the next
launch (§5.3), and it is argued again in §7.

### 2.4 Two AOT runtimes in one process — the largest unknown

The host is a Native AOT executable; each plugin is a Native AOT shared library. Loading a plugin
therefore brings a **second copy of the AOT runtime** (its own GC, its own signal handlers) into the
process. This is not a documented supported scenario and there is a real field report of it going
wrong: `dotnet/runtime#121345`, *.NET application hangs some time after loading NativeAOT-compiled
library* — a SIGRTMIN delivered to a worker thread is handled by the loaded AOT runtime's handler,
which allocates TLS inside an async-signal context and deadlocks against the host's GC suspension.
`dotnet/runtime#109416` confirms there is no "hosting" story: NativeAOT "only uses the GC heap".

This is **the** risk of the approach and it is called out as such in §7. It does not block the
design — the design keeps the plugin's own threads internal and makes every plugin→host interaction
explicit — but the proof plugin in Phase 0 exists specifically to measure it before any vendor moves.

### 2.5 Calling convention

x64 is the only target (`build.yml:157,290`; `docs/build-image.md` pins `linux-x64`/`win-x64`). On
x64 there is one Windows calling convention and one SysV convention; the x86 `stdcall @n` mangling
noted in the runtime docs does not apply. The ABI therefore declares every export
`[UnmanagedCallersOnly(EntryPoint = "...", CallConvs = [typeof(CallConvCdecl)])]` and the host calls
it through `delegate* unmanaged[Cdecl]<...>` resolved by `NativeLibrary.GetExport`. This is the
AOT-friendly path: `Marshal.GetDelegateForFunctionPointer` is `RequiresDynamicCode`
(dotnet/runtime#85699, and the Learn note "Function pointers are more performant and AOT friendly
replacement for `Marshal.GetDelegateForFunctionPointer`").

### 2.6 Cross-OS still impossible

`docs/build-image.md:99-150` establishes, with the toolchain's own error text, that Native AOT cannot
cross-compile win-x64 from Linux (nor the reverse). Nothing in this design changes that: the Windows
plugin is built on Windows, the Linux plugin inside the Linux release image, each in its own OS job.

---

## 3. The ABI

### 3.1 Shape: a small generic envelope

Rather than one export per method (which would freeze ~90 signatures into the ABI and require an
export per capability), the ABI is a **stable envelope**: a handful of C entry points carried by
**one shared source file compiled into both the host and every plugin**. The exports must live in the
plugin assembly (§2.1), so the shared file is included by `<Compile Include="...\Abi.cs" Link="Abi.cs" />`
rather than referenced as a package. The host's copy declares the same struct layout and the same
`delegate* unmanaged[Cdecl]` signatures.

### 3.2 Exports

All pointers are raw; all buffers are UTF-8 unless said otherwise. `nint` = pointer-sized.

```c
// Every function is [UnmanagedCallersOnly(CallConvs = [CallConvCdecl])] and exported with these names.

// The plugin API version the plugin was BUILT for: (major << 16) | minor. This is NOT the app
// <Version>; it changes only when the vendor ABI changes (§3.8). Authoritative: the host trusts
// THIS, not the manifest, because a manifest can be stale or copied.
uint32 ah_abi_version(void);

// Cheap identity, no hardware touched: writes the MODEL-LINE id, e.g. "acer-nitro"
// / "acer-predator" / "acer-swift" / "dell-xps" / "asus-rog" / "generic", as UTF-8 (§1.4).
// It names what the plugin claims to be, NOT what it matched; ah_matches is the gate.
// Returns the number of bytes that WOULD be written; if > cap it wrote nothing.
int32  ah_plugin_id(uint8* outBuf, int32 cap);

// Cheap DMI gate, no hardware touched. descJson is the machine descriptor the host built:
// {"manufacturer":"Acer","product":"Nitro AN18-61","board":"...","boardProduct":"..."}.
// Discriminates at the LINE level. Writes a JSON decision
// {"match":true,"confidence":0..100,"reason":"..."}.
int32  ah_matches(const uint8* descJson, int32 descLen, uint8* outBuf, int32 cap, int32* outLen);

// Probe the hardware and build a session. Returns the probe manifest (see 3.4) and a handle.
// outHandle is opaque to the host; it is owned by the plugin.
int32  ah_create(const uint8* descJson, int32 descLen,
                 uint8** outManifest, int32* outManifestLen, uint64* outHandle);

// One capability operation. Request/response are JSON (3.3). The response buffer is allocated
// by the plugin and MUST be released by the host through ah_free.
int32  ah_invoke(uint64 handle, uint32 capability, uint32 op,
                 const uint8* request, int32 requestLen,
                 uint8** outResponse, int32* outResponseLen);

// Release a buffer the plugin returned (manifest or any response). Null is a no-op.
void   ah_free(uint8* ptr);

// Release the session: stop the plugin's worker threads, close HID/WMI handles. Idempotent.
void   ah_dispose(uint64 handle);

// Optional (v1 needs it for hotkeys): let the plugin raise events into the host.
// sink = delegate* unmanaged[Cdecl]<nint ctx, uint32 kind, const uint8* payload, int32 len, void>
void   ah_set_event_sink(uint64 handle, nint sink, nint ctx);
```

Status codes (`int32`), chosen so the host can branch without parsing the body:

| Code | Name | Meaning |
|---:|---|---|
| 0 | `Ok` | response body is the result |
| 1 | `Refused` | the machine/transport refused; the reason is in the JSON body (matches the current `(ok, error)` port shape) |
| 2 | `NotSupported` | this session does not implement that op/capability (the host should not have asked; a bug) |
| 3 | `InvalidArg` | malformed request (host bug) |
| -1 | `Internal` | an exception was caught inside the plugin; body carries `{"error":"..."}` |
| -2 | `AbiMismatch` | `ah_create` refused: the manifest declares a capability/API version the host cannot adapt, or the plugin requires a NEWER API than the host sent (§3.8.4) |

#### The machine descriptor and match confidence (§1.4)

The host builds `descJson` **once** from the cheapest DMI source it already reads
(`MachineInfo.Read()` → `(manufacturer, product)`, `MachineInfo.Windows.cs:8-11` via
`Win32_ComputerSystemProduct`, `MachineInfo.Linux.cs:8-9` via `/sys/class/dmi/id/sys_vendor` and
`product_name`) plus the extra DMI fields both OSes expose for free:

| Field | Windows source | Linux source | Why it is here |
|---|---|---|---|
| `manufacturer` | `Win32_ComputerSystemProduct.Vendor` | `sys_vendor` | vendor-level gate (cheap) |
| `product` | `Win32_ComputerSystemProduct.Name` | `product_name` | the line discriminator AND the `acer-models.json` key (`AcerModel.cs:20`) |
| `board` | `Win32_BaseBoard.Manufacturer` | `board_vendor` | separates a board/OEM rebrand from the retail line |
| `boardProduct` | `Win32_BaseBoard.Product` | `board_name` | e.g. an ODM code when `product` is generic |

All four are one-line DMI reads on both OSes (no hardware transaction), so `ah_matches` stays "touch nothing"
and the host pays one read for the whole scan. The descriptor is **never** the full `Device`; it is only what
a plugin needs to decide whether it is this machine's line.

**Confidence is the host's selection key.** Each plugin returns `confidence` in 0..100 and the host takes the
unique maximum above a threshold. The intended calibration, so the ordering is mechanical rather than a
plugin's opinion:

| Confidence | Meaning | Example |
|---:|---|---|
| 0 | not this machine (default; body may be `{"match":false,...}`) | `acer-nitro` on a Dell |
| 50 | manufacturer matches, line unknown | `acer-nitro` on an unlisted Acer product |
| 80 | product substring matches a line entry | `acer-nitro` on `"Nitro AN18-61"` |
| 100 | product **and** board fields match, no ambiguity | exact `acer-models.json` `Match` hit |

Ties are resolved by **refusing to pick** (fall back to `GenericDevice`, §4.1) rather than by folder order,
so two plugins claiming the same line is a bug the host surfaces instead of hiding behind filesystem order.
Exact tie-breaking policy (highest confidence; tie → no match; threshold value) is deliberately host-side
and testable without hardware.

**No managed type crosses the boundary.** Every argument is `uint32`/`int32`/`uint64`/pointer — which
is exactly the `UnmanagedCallersOnly` restriction (§2.1). No exception is allowed to escape: every
export body is wrapped in `try/catch` and returns `-1` (the sample's pattern).

**How the scan avoids loading every plugin.** Calling `ah_matches` requires the plugin to be loaded, and a
loaded AOT module cannot be unloaded (§2.3) — so a naive scan of N plugins leaves N runtimes resident, each
with its own signal handlers (§7.1 risk 1). The host therefore uses a **cheap host-side pre-filter**: the
manifest's `matchHint` (a coarse `manufacturer`/`product` substring set, §5.2) decides which candidates are
even worth downloading and loading. Only plugins whose `matchHint` is consistent with this machine's DMI are
loaded; the winner's session is kept and the (few) loaded losers stay mapped for the process — acceptable
because the per-machine candidate count is one or two, not the whole catalogue. The hint is advisory only:
`ah_matches` remains the authority, and a plugin with no hint is still evaluated if it is already cached.
Without this pre-filter the design would pay a full second runtime per unrelated plugin, which is the one
way the line-per-plugin choice could be *worse* than the old per-vendor one.

### 3.3 Payload encoding: `System.Text.Json` source generation (picked)

**Decision: JSON via `JsonSerializerContext` for v1**, not a hand-rolled binary codec.

Reason, with the measurement that settles it. The boundary is **not hot**: every call except
`profiles.list`/`sensors.read` is a firmware transaction measured in milliseconds to seconds
(`AcerEcHidController` header, lines 121-143; `LaptopService.Tuning.cs:167-169` — "can block for
seconds"; `OptionsAssembler.cs:103-108` — ~2 s tint apply). A JSON encode of a <1 KB payload is
microseconds. The three costs of JSON are real but small here:

- **Not zero-allocation.** The envelope reuses a plugin-side cached `Utf8JsonWriter`/buffer per
  session rather than allocating per call.
- **Schema drift risk.** Answered by source generation (`[JsonSerializable(typeof(FanSetRequest))]`
  etc.), which is AOT-safe and makes both sides' types part of the build, plus a schema-version field
  in every request (`"v":1`).
- **Size.** ~3–5× a packed binary, irrelevant at these sizes.

The tradeoff versus a binary codec, stated so the choice is reversible: a compact TLV/binary format
would cut bytes and remove the JSON parser, at the cost of a second hand-written codec to keep in
step on both sides and no human-readable capture when debugging across the boundary. JSON wins on the
debuggability axis, and the envelope is format-agnostic — a later minor version may add
`"enc":"bin"` without changing the export set.

### 3.4 The probe manifest (what `ah_create` returns)

The manifest is the one place a plugin declares **what this machine has**, so the host can build the
`Device` slots and the settings set before any further call. It mirrors `Device.cs:36-82` plus the
`Battery` property presence (`Battery.cs:28-47`):

```jsonc
{
  "abi": "2.0",                          // the plugin API major.minor this build speaks (§3.8), = ah_abi_version()
  "pluginId": "acer-nitro",              // ah_plugin_id: the MODEL LINE, not the vendor (§1.4)
  "vendor": "acer",
  "vendorName": "Nitro AN18-61",        // Device.VendorName (from acer-models.json's friendly name)
  "statusMessage": "status.linux_mainline_full", // a Loc KEY, never a sentence (Device.StatusMessage)
  "capabilities": {
    "powerProfiles": {
      "all": [{"id":"6","displayName":"profile.eco"}, ...],   // PerformanceProfile(Id, DisplayName)
      "currentId": "1",
      "availableOnAc":   ["0","1","4","5"],                    // IProfileAvailability
      "availableOnBattery": ["2","1"],
      "traits": [{"id":"5","kind":"Turbo","accent":[156,39,176],"flash":[255,0,199]}, ...]
    },
    "fan": {"capability":{"hasMax":true,"hasCustom":true,"hasGpuFan":true}},
    "sensors": {},                          // present => vendor overrides the generic read
    "keyboardBrightness": {"maxLevel":2},
    "rgb": {
      "profileFollowKey": "acer.lightbarFollowsProfile",
      "zones": [{"name":"Keyboard","subZones":4,"canFollowProfile":false,
                 "effects":[{"name":"Static","hasColor":true,"hasSpeed":false,"handle":1}, ...],
                 "readBrightness":true},
                {"name":"Lightbar","subZones":1,"canFollowProfile":true,"effects":[...]}]
    },
    "hotkeys": {},
    "gpuMux": {"supported":true,
               "modes":[{"id":"1","displayName":"gpu.mode_hybrid"}, ...],
               "current":"1","pending":null},
    "battery": {"chargeLimit":true,"calibration":true,"chargeMode":false,"powerSource":true},
    "curveOptimizerOverride": null,          // Acer does NOT override; Dell does not either
    "declaredSettings": [
      {"key":"lcd_override","shape":"flag","readbackVerifiesWrite":false},
      {"key":"usb_charging","shape":"choice","options":[{"id":"0","displayName":"level.off"}, ...]}
    ]
  }
}
```

Three design rules encoded here:

- **`abi` is the plugin's declared API version** (major.minor, §3.8), echoed from `ah_abi_version()`. The
  host treats the export as authoritative and this field as a cross-check; a mismatch between the two is a
  plugin bug and is refused at load.
- **Settings travel as declarations, not as ports.** `declaredSettings` carries the key + shape +
  options; the value is read/written through `ah_invoke(Capability.Settings, Op.Read/Set, {key})`.
  This preserves `Settings.cs:108-198` (the host model holds the set, `Apply`/`Remember`) and
  `DeclaredSetting.cs` (shape is the fork), so `OptionsAssembler` is untouched.
- **`StatusMessage` is a localization key**, exactly as today (`AppController.cs:880` runs it through
  `Loc.T`). The plugin never sends a sentence, so `Localization/` stays host-only.

### 3.5 Op table

`capability` constants: Power=1, Fan=2, Sensors=3, Battery=4, KeyboardBrightness=5, Rgb=6,
Hotkeys=7, GpuMux=8, Settings=9 (vendor-overridable ports only; the generic axes are host-side and
have no capability code).

| Capability | Op | Request | Response |
|---|---|---|---|
| Power (1) | 1 `Selectable` | `{}` | `{"ids":[...]}` |
| | 2 `Current` | `{}` | `{"id":"1"}` or `{"id":null}` |
| | 3 `Set` | `{"id":"4"}` | `{}` on Ok; `{"error":"..."}` on Refused |
| | 4 `AvailableOn` | `{"onAc":true}` | `{"ids":[...]}` |
| | 5 `Traits` | `{"id":"5"}` | `{"kind":"Turbo","accent":[r,g,b],"flash":[r,g,b]}` |
| Fan (2) | 1 `SetMode` | `{"mode":"Custom"}` | `{}` |
| | 2 `SetCustomSpeeds` | `{"cpu":70,"gpu":70}` | `{}` |
| Sensors (3) | 1 `Read` | `{}` | `{"cpuTempC":41,"gpuTempC":40,"fans":[{"label":"CPU","rpm":2100},...]}` |
| Battery (4) | 1 `ReadTelemetry` | `{}` | `BatteryInfoSnapshot` fields |
| | 2 `ReadToggle` | `{"property":"chargeLimit"}` | `{"value":true}` |
| | 3 `WriteToggle` | `{"property":"calibration","value":true}` | `{}` / `{"error":...}` |
| | 4 `ReadChoice` | `{"property":"chargeMode"}` | `{"id":"Adaptive"}` |
| | 5 `WriteChoice` | `{"property":"chargeMode","id":"Custom"}` | `{}` / error |
| | 6 `ReadPowerSource` | `{}` | `{"source":"UsbC"}` |
| KeyboardBrightness (5) | 1 `Get` / 2 `Set` | `{}` / `{"level":2}` | `{"level":0}` / `{}` |
| Rgb (6) | 1 `ApplyEffect` | `{"zone":"Keyboard","effectHandle":1,"brightness":100,"speed":5,"direction":1,"color":[r,g,b]}` | `{}` |
| | 2 `ApplySubZone` | `{"zone":"Keyboard","index":0,"brightness":80,"color":[r,g,b]}` | `{}` |
| | 3 `SetProfileFlash` | `{"color":[r,g,b]}` | `{}` |
| | 4 `Blank` | `{}` | `{}` |
| | 5 `ReadBrightness` | `{"zone":"Keyboard"}` | `{"brightness":50}` |
| GpuMux (8) | 1 `Read` | `{}` | `{"current":"1","pending":null,"rebootRequired":false}` |
| | 2 `Request` | `{"id":"2"}` | `{"ok":true,"queued":true,"noOp":false,"error":null}` |
| Settings (9) | 1 `Read` | `{"key":"lcd_override"}` | `{"value":"1"}` |
| | 2 `Set` | `{"key":"lcd_override","value":"1"}` | `{}` / `{"error":...}` |
| Hotkeys (7) | — | event-only, raised through `ah_set_event_sink` (kind 1 = Pressed with `{"action":"TogglePerformance"}`, kind 2 = InputActivity) | n/a |

### 3.6 Worked example A — "get the performance profiles", end to end

1. **Host load**: `NativeLibrary.Load("<cache>/plugins/acer-nitro/AcerHelper.Vendor.acer-nitro-win-x64.dll")`
   → `Load` returns `handle`; the runtime initialises (see §7.1 risk 1).
2. `ah_abi_version()` → `0x00020000` (2.0). The host selects this plugin's adapter from `SupportedMajors`
   (current 2, deprecated 1) — a major outside the set is refused with `status.plugin_incompatible` (§3.8.4).
3. `ah_plugin_id(buf, cap)` → "acer-nitro".
4. `ah_matches({"manufacturer":"Acer","product":"Nitro AN18-61","board":"...","boardProduct":"..."}, ...)` →
   `{"match":true,"confidence":80,"reason":"product 'Nitro AN18-61' matches line 'Nitro'"}`. (`AcerModels.Detect`
   uses the product name for quirks; the line gate is the same DMI read `MachineInfo` already does,
   `MachineInfo.Windows.cs:8-11`, `MachineInfo.cs:8`.)
5. `ah_create({"abi":{"major":2,"minor":0}, ...}, &manifest, &len, &session)` → status 0 (the handshake,
   §3.8.4); the plugin constructs its transports (`AcerEcHidController.Available`, `AcerGamingFunction` via
   WMI, ENE HID) exactly as `AcerDevice.Windows.cs:27-115` / `.Linux.cs:59-166` do today, and returns the
   manifest in §3.4.
6. **Host adapters**: `PluginPowerProfiles` is created from `capabilities.powerProfiles`; `Device`
   gets `PowerProfiles = pluginPowerProfiles`, `FanControl`, `Sensors`, …
7. **Late read**: `MainViewModel` / `TrayController` call `PowerProfiles.Current()` on the UI thread
   (`TrayController.cs:73`, `LaptopService.Profiles.cs:123`). The adapter marshals
   `ah_invoke(session, 1, 2, {"v":1}, ...)`. The plugin calls its `CurrentProfile()` (WMI
   `GetGamingMiscSetting` index 0x0B, `AcerDevice.Windows.cs:124-128`), serialises
   `{"id":"1"}`, calls `NativeMemory.Alloc`, copies UTF-8, sets `*outResponse`.
8. Host parses with the generated `JsonSerializerContext` into `PerformanceProfile?`, frees the
   buffer with `ah_free`, returns to the caller.

### 3.7 Worked example B — "set fan speed"

1. `LaptopService.ApplyFan` (`LaptopService.Fans.cs:20-38`) calls `SetMode(Custom)` then
   `SetCustomSpeeds(cpu, gpu)`.
2. Adapter → `ah_invoke(session, 2, 1, {"v":1,"mode":"Custom"})`; plugin writes the EC behaviour
   (Windows: `SetGamingFanBehavior` `AcerDevice.Windows.cs:155-156`; Linux: both `pwmN_enable`
   writes, `AcerFanPort.cs:267-283`), returns status 0 (or `Refused` + reason, which the adapter
   stores in `LastError`).
3. Adapter → `ah_invoke(session, 2, 2, {"v":1,"cpu":70,"gpu":70})`; plugin writes CPU then GPU
   (aborting on CPU failure — `AcerFanPort.cs:289-297`), returns `{}` or `Refused`.
4. `ApplyFan` ignores the result by design (`LaptopService.Fans.cs:34-37`); no behaviour change.

The ordering rule (behaviour before speeds) is preserved because the **host keeps the orchestration**
(`LaptopService`); the plugin only executes the two writes it always did.

### 3.8 API versioning and compatibility

**Owner requirement (2026-09-27, verbatim):** *"нам нужно решить проблему совместимости на случай смены api.
Было бы неплохо, чтобы приложение версионировало свой api и проверяла манифест плагина на совместимость"* —
and the decisions: **the app version and the plugin API version are separate**, the host can hold **several
API major versions at once (the current one and a deprecated one)**, and the compatibility window is
**current + previous major**.

#### 3.8.1 Two separate versions

The host owns a `PluginApiVersion { Major, Minor }` that is **not** the app `<Version>`
(`AcerHelper.csproj:12`, currently `0.37.0`). The two move for different reasons:

- the **app** version bumps every release, including releases that touch no vendor code;
- the **plugin API** version bumps only when the C ABI changes — a changed export set, a changed struct
  layout, a changed op number or payload semantics (§3.8.5).

Consequence, stated as a rule the CI enforces: **bumping the app version never forces a plugin rebuild;
bumping the API major does.** A plugin built against API 2 keeps loading after any number of app `0.x`
releases, because the ABI it speaks did not move.

It is baked into the host the same way `AppInfo.Version` is: the `GenerateAppVersion` MSBuild target
(`AcerHelper.csproj:213-237`) writes a const from `<Version>`, precisely so Native AOT's stripped
assembly-version reflection cannot lie about it. The API version gets its **own const from its own
property**, generated by the same pattern:

```xml
<PropertyGroup>
  <PluginApiVersion>2.0</PluginApiVersion>   <!-- Major.Minor; NOT <Version> (§3.8.1) -->
</PropertyGroup>
```

target `GeneratePluginApiVersion` (a sibling of `GenerateAppVersion`, same
`WriteLinesToFile`-into-`obj/` shape, `AcerHelper.csproj:220-236`) emits
`namespace AcerHelper.Infrastructure.Plugins; internal static class PluginApi { public const int Major = 2;
public const int Minor = 0; public const int Encoded = (Major << 16) | Minor; }`. The host passes
`Encoded` wherever a raw `uint32` is needed; a **generated** const, not a hand-maintained literal, is what
keeps the two sides from drifting the way the app-version target exists to prevent. The plugin csproj
calls the same target (or references the same SDK file, §4.4) so its `ah_abi_version` returns its own
API build version.

#### 3.8.2 The host holds several majors at once: per-major adapters

The host does **not** branch on the major at every call site. It has **one internal call model** — the
`PluginSession`/adapters of §4.2 — and a set of **per-major adapters**; an adapter is the only place that
knows the differences of one major and translates that major's wire form ↔ the internal model:

```
Infrastructure/Plugins/Abi/
  IPluginAbiAdapter.cs        // interface: Version, and Translate(manifest|request|response) per major
  V2/AbiV2Adapter.cs          // CurrentMajor = 2 — the internal model is V2's shape
  V1/AbiV1Adapter.cs          // DeprecatedMajor = 1 — "scheduled for removal" (see 3.8.3)
  PluginAbiRegistry.cs        // maps major -> adapter; SupportedMajors = { Current, Deprecated? }
```

`IPluginAbiAdapter` exposes, at minimum, `int Major { get; }`, `bool Deprecated { get; }`, the ability to
decode a plugin's probe manifest into the internal model, and to encode/decode an `ah_invoke` request and
response for that major. The internal model is the **current** major's model; V1 is a translation layer
that maps the old struct/field layout, old op numbers and old payload shape onto it. **Adding a new major
must not change the internal model — it adds an adapter** (and, if the model genuinely must move, that is
the next major's adapter doing the translation, not an edit to every adapter).

This is what makes "hold two majors concurrently" real: the loader asks the registry for the adapter for
the plugin's `ah_abi_version` major; if present, that adapter drives the session; if absent, the plugin is
refused (§3.8.4). There is no major-conditional code outside `Abi/`.

**Status today:** the first shipped API is **1.0**, so the host initially carries a single adapter
(`V1/`, current) and `SupportedMajors = { 1 }` — the deprecated slot is empty, and the mechanism costs
nothing until the first breaking change. The `V2`/`V1` split above is the shape *after* that first bump,
shown because that is the state the design must be proven in (the tests build it, §6). Naming the folders
by major (`V1/`, `V2/`) rather than by role is deliberate: when V1 is removed, V2's adapter stays `V2/`,
and the current-major adapter is whatever the registry says it is — no rename churn.

#### 3.8.3 Deprecated lifecycle and the removal trigger

Policy:

1. A plugin's code declares its major via `ah_abi_version` (§3.2).
2. **Bumping the host's `Major`** makes the *previous* major **deprecated** (still loadable, via its
   adapter, and its manifest entries still selectable) and the one before that **unsupported** (no
   adapter → refused). Concretely: at API 1 the host holds `{1}`; at API 2 it holds `{2 current, 1
   deprecated}`; at API 3 it holds `{3 current, 2 deprecated}` and 1 is gone.
3. The **deprecated** adapter, its manifest entries and its CI build legs are removed at an explicit
   trigger, so removal is a **decision recorded in advance, not drift**:

   > **Removal trigger (recommended): the first app release after the deprecated major has been
   > superseded for four app minor releases** (i.e. roughly one release cycle of grace). If a cleaner,
   > clock-independent rule is wanted, the alternative is *the first release after `PluginApiVersion` has
   > remained ≥ the current major for 6 months*.

   Whichever is chosen, it is written into this section and into the CI comment that builds the deprecated
   leg; the PR that removes a major cites this trigger. (The exact number/clock is left as an open
   question, §7.3 — the mechanism is decided, the interval is a knob.)
4. **Asymmetry, stated:** the *host* may hold two majors; a *shipping plugin* targets **one** — the current
   major. The deprecated window exists so an already-downloaded, still-cached plugin keeps working after an
   app upgrade, not so plugins are published against old majors.

#### 3.8.4 Three compatibility gates (defence in depth)

| Gate | When | What it checks | On failure |
|---|---|---|---|
| **Manifest** | before download | the entry's `api.major` ∈ host `SupportedMajors`, and `minHost` ≤ host app version | incompatible entries are **not selected**; if one was expected, a status line, and no download |
| **Load** | startup, after `NativeLibrary.Load` | the plugin's own `ah_abi_version().major` ∈ `SupportedMajors` | session discarded; `GenericDevice` + `status.plugin_incompatible` (§4.1.1) |
| **Handshake** | `ah_create` | the host's `PluginApiVersion` is acceptable to the plugin | plugin returns `-2 AbiMismatch`; session discarded, status line |

**Gate 1 — manifest (the owner's explicit ask).** Every `vendor-plugins.json` entry carries:

```jsonc
"api": { "major": 2, "minor": 0 },   // the API the ASSET was built against (§3.8)
"minHost": "0.38.0",                  // the oldest app <Version> that can load it
```

The host selects only entries whose `api.major` is in `SupportedMajors` (current, or deprecated while that
adapter still exists) **and** whose `minHost` ≤ its own `AppInfo.Version` (`AcerHelper.csproj:213-237`;
`UpdateChecker.TryParseVersion` is the existing comparison pattern, `UpdateChecker.cs:61-73`). Entries are
**indexed by `pluginId + os + arch + api.major`**, so a major bump makes the host download the asset built
for *its* major (both majors' assets coexist in the release during the deprecation window), and an
incompatible plugin is surfaced as a status line instead of ever being downloaded. Downloading an asset the
host cannot load is the waste this gate prevents.

**Gate 2 — load (authoritative).** At startup the host calls `ah_abi_version()` (the export is in §3.2;
this design predates this section) and requires its major in `SupportedMajors`. The manifest can be stale,
hand-edited or copied from another release, so the version **in the plugin's code is authoritative** — the
manifest gate above is an optimisation and a UX guard, not the trust anchor. A plugin whose major is
unknown (a removed major a user kept, or a newer major than the host) is refused and surfaced with the
same `status.plugin_incompatible` line.

**Gate 3 — handshake.** `ah_create`'s request (§3.4) gains a field the host fills from its generated const:

```jsonc
{ "abi": { "major": 2, "minor": 0 } }   // the PluginApiVersion the HOST runs
```

A plugin built for a **newer, unknown** API may refuse with the existing `-2 AbiMismatch` status (§3.2) —
the gate that catches a host older than the plugin it is trying to run, the case neither earlier gate can
see. The refusal body carries `{"error":"..."}` like every other plugin error.

#### 3.8.5 Minor vs major evolution rules

- **Within a major: additive only.** New optional ops; new optional JSON fields that the *other* side
  ignores. `System.Text.Json` ignores unknown members by default, and this design already commits to a
  generated `JsonSerializerContext` for both directions (§3.3; the tree already uses that pattern for
  `acer-models.json` via `AcerModel.cs:34-37`), so an old peer reading a new payload simply drops any field
  it does not know. No field removal, no rename, no semantic change of an existing field or op. That
  additive surface is what **`minor` tracks**.
- **Any breaking change is a new major**: a renamed/removed export, a changed struct layout, changed op
  semantics, a non-additive payload (a field that changes meaning, a required field, a reordered struct).
- **`minor` is informational to the host** (logging/support: "this plugin was built for 2.3"); the
  **major is the gate**. The host never refuses a plugin for an older/newer minor within a supported major.

---

## 4. Host side

### 4.1 Loader

New host-only folder `Infrastructure/Plugins/`:

```
Infrastructure/Plugins/
  VendorPluginLoader.cs      // scans the plugin dirs, Load, version/plugin-id/matches
  VendorAbi.cs               // shared ABI structs + delegate* signatures (source-linked into plugins too)
  PluginApi.cs               // GENERATED const Major/Minor/Encoded from <PluginApiVersion> (§3.8.1)
  Abi/IPluginAbiAdapter.cs, Abi/PluginAbiRegistry.cs, Abi/V2/AbiV2Adapter.cs, Abi/V1/AbiV1Adapter.cs
                             // one adapter per supported major; the only major-conditional code (§3.8.2)
  PluginSession.cs           // owns NativeLibrary handle + session + ah_free/ah_dispose lifetime
  PluginCache.cs             // per-user install/verify/update of downloaded plugins (§5.3)
  PluginVendorDevice.cs      // : Device — fills slots from the adapted manifest
  Adapters/PluginPowerProfiles.cs, PluginFanControl.cs, PluginSensors.cs, PluginBattery.cs,
           PluginRgbDevice.cs, PluginKeyboardBrightness.cs, PluginGpuMux.cs, PluginHotkeys.cs,
           PluginDeclaredSetting.cs
```

`DeviceFactory.Create` (`DeviceFactory.cs:28-42`) becomes:

1. `MachineInfo.Read()` → `(manufacturer, product)`; build the four-field descriptor (§3.2).
2. Enumerate the plugin search paths (§4.1.1) deterministically (sorted by path). For each candidate
   `.dll`/`.so`, first apply the manifest's advisory `matchHint` (§3.2) so unrelated plugins are not
   loaded; then `NativeLibrary.Load`, read `ah_abi_version` and require its major in
   `PluginAbiRegistry.SupportedMajors` (§3.8.4 gate 2 — an unsupported major is refused here, with a
   status line), select the adapter for that major, read `ah_plugin_id`, and call `ah_matches`
   with the descriptor. Collect every `{"match":true}` at or above the confidence threshold.
3. Pick the **unique highest-confidence** match. A tie above threshold, no match, or a failed load → fall
   back to `new GenericDevice()` (with the status-line exception below).
4. If one wins → `PluginVendorDevice.Create(session, adapter)` — the adapter decodes the manifest into the
   internal model (§3.8.2); else → `new GenericDevice()`.
5. `device.FinalizeComposition()` (unchanged).

`DeviceFactory` no longer `using AcerHelper.Infrastructure.Vendors.Acer/Dell`; the in-host `GenericDevice`
path stays. **The host ships with NO vendor plugin inside it** (§5): the search paths are the per-user cache
and a dev override, not `AppContext.BaseDirectory`. This is the deliberate contrast with how the app reads
its *bundled* files from `AppContext.BaseDirectory` (`HardwareAccess.cs:463-467`): those travel inside the
MSI/AppImage (`AcerHelper.csproj:48-70`), while plugins are downloaded (§5.3) and therefore cannot live in
the read-only install.

#### 4.1.1 Plugin search paths

| Order | Path | Purpose |
|---:|---|---|
| 1 | `%ACERHELPER_PLUGIN_DIR%` (if set) | dev/override: one directory, scanned recursively for `*/<id>/*.dll` |
| 2 | `%AppData%\AcerHelper\plugins\<plugin-id>\` | the writable per-user cache (§5.3) |
| 3 | `~/.local/share/acer-helper/plugins/<plugin-id>/` | the same on Linux (XDG data dir) |

There is **no** `AppContext.BaseDirectory/plugins` in production: the AppImage and the MSI install folder
are read-only (§5.3.1, §7.2), so a directory the updater writes into cannot be the install folder.
`AppContext.BaseDirectory` remains the source for *bundled* files and is cited only as the contrast.
Search order is stable and each candidate's id comes from `ah_plugin_id`, not from its folder name, so a
misnamed folder is caught at load, not trusted.

**Failure modes, made visible.** "Missing plugin" (no directory, no match) → `GenericDevice`, silently (the
current unknown-vendor fallback, `DeviceFactory.cs:35`). "Matched but incompatible" (a plugin returned
`match:true` but its `ah_abi_version` major is not in `SupportedMajors`, or `ah_create` returned
`-2 AbiMismatch`, or verification failed §5.4) → `GenericDevice` **plus a status line**
`status.plugin_incompatible` (`Device.StatusMessage`,
`Device.cs:40`, `AppController.cs:880` renders it via `Loc.T`). Rationale: the owner resolved the old §7
fallback question (§7.2) as "must not be silent" — a matched-but-incompatible plugin is a *broken install
the user paid for* (they downloaded it), the opposite of the neutral unknown-vendor case, and the integrity
decision is fail-closed (§5.4). The status line is how a fail-closed refusal stays actionable instead of
invisible.

### 4.2 Adapters forward to `Invoke`

Every adapter implements the existing Domain/Infra interface with the same members, calling
`session.Invoke(capability, op, request)` and deserialising. Representative shapes:

- `PluginPowerProfiles : IPowerProfiles, IProfileTraits, IProfileAvailability` — the three interfaces
  are implemented by the **same** object, exactly as `ProfilesPort` does today
  (`DelegatePorts.cs:59-64`). `All`/`Traits` come from the manifest (immutable, no call);
  `Current`/`Set`/`Selectable`/`AvailableOn` are `Invoke`s. `LastError` is set from a `Refused`
  body, preserving the "reason belongs to THIS call" rule (`LaptopService.cs:121-149`).
- `PluginFanControl : IFanControl` — `Capability` from the manifest; `SetMode`/`SetCustomSpeeds`
  forward; `LastError` from the refusal.
- `PluginSensors : ISensors` — `Read()` marshals the snapshot.
- `PluginBattery` — the host constructs the `Battery` object (`Battery.cs:22`) and assigns only the
  properties the manifest listed: `ChargeLimit = new BatteryToggle(ReadToggle, WriteToggle)`,
  `ChargeMode = new BatteryChoice(...)`, `PowerSource = ...`, reusing the exact delegates the
  `Battery` object already takes (`Battery.cs:63-69`). The Acer/Dell removal semantics
  (`AcerDevice.Windows.cs:101-103`, `DellDevice.Linux.cs:60`) become "the manifest did not list the
  property".
- `PluginDeclaredSetting : SettingDeclaration` — the host builds `FlagSetting`/`ChoiceSetting`
  (`DeclaredSetting.cs:75,122`) whose `Port` is a `PluginFlagPort`/`PluginChoicePort` forwarding to
  `Settings` capability. `Refuse`/`Apply`/`Remember` stay host-side and unchanged.
- `PluginHotkeys : IHotkeys` — registers a `ah_set_event_sink`; the `[UnmanagedCallersOnly]` sink
  marshals payload → `HotkeyAction` and raises the C# events on the captured `SynchronizationContext`
  (the plugin's key threads are its own; `AppController.cs:151-155` subscribes on the UI thread, and
  `AcerHotkeys.Linux.cs:140` already documents "AppController marshals to the UI thread").

**What does NOT become an adapter**: `DisplayTint`, `GpuOverclock`, `CpuPower`, `CurveOptimizer`,
`Autostart`, `Clamshell`, `CoreAffinity`, generic `Sensors`/`FanControl`/`KeyboardBrightness`/
`Battery.Telemetry`. Those are built by `GenericDevice` in the host, exactly as now. A vendor plugin
that overrides `Sensors`/`FanControl`/`KeyboardBrightness` replaces the generic adapter after
composition, mirroring how `AcerDevice.Linux.cs:222-223` wraps the generic sensors today.

### 4.3 Lifetime, threading, failure

- **Dispose**: `PluginVendorDevice.Dispose` (override of `Device.Dispose`, `Device.cs:94`) calls
  `ah_dispose` then releases the `NativeLibrary` handle with `ah_free` for any outstanding buffer.
  `NativeLibrary.Free` may be called for bookkeeping but is not a real unload (§2.3). The session is
  created once and lives for the process, matching `GenericDevice`'s `Own` list (`Device.cs:84-87`).
- **Threading**: adapters are thread-safe because `Invoke` is synchronous and the port contracts are
  the same ones the in-host ports honour today. The plugin's own writer threads (EC HID, ENE) stay
  inside the plugin; the host never calls into the plugin from a plugin thread, and the plugin never
  calls into the host except through the one event sink.
- **Missing plugin**: no plugin dir, no match, or a `DllNotFoundException` → `GenericDevice`
  (the current unknown-vendor fallback, `DeviceFactory.cs:35`). Never a crash.
- **Matched but incompatible**: `ah_abi_version` major ∉ `SupportedMajors` (§3.8.4 gate 2), `-2 AbiMismatch`
  from `ah_create` (§3.8.4 gate 3), or a failed signature/hash verification (§5.4) → the loader **discards
  the session** and falls back to `GenericDevice` **with** `StatusMessage = status.plugin_incompatible`
  (§4.1.1), logging the reason. The manifest gate (§3.8.4 gate 1) keeps an incompatible plugin from being
  downloaded at all; these two gates catch one already on disk or a stale manifest.
- **Plugin crash**: takes the process down (no isolation, §2.3). Documented, not defended against.

### 4.4 The transport split — the hard part

The vendor plugins need code that currently lives under `Vendors/Generic/`. Two options:

- **(A) A source-shared `PluginSdk` folder** (recommended): move the *vendor-agnostic transports*
  into `Infrastructure/Plugins/Sdk/` and include them by source in the plugin csprojs. The host
  keeps its own copy for `GenericDevice`. Candidates, measured by use:
  `WmiSession.Windows.cs` (318 l), `WbemInterop.Windows.cs` (177 l), `WmiInvoker.Windows.cs` (51 l),
  `SysfsInvoker.Linux.cs` (61 l), `FirmwareAttributes.Linux.cs` (55 l), `Hwmon.Linux.cs` (241 l),
  `HidSharp` (package), `SysfsLink.cs` (103 l), and the neutral helpers `DelegatePorts.cs` (84 l),
  `ProfileKind.cs` (80 l), `AcerDevice`'s `GmGet/GmSet` pattern (already file-local).
- **(B) Duplicate per plugin**: rejected — two copies of the WMI COM layer drifting is exactly the
  class of defect the tree's guards exist to prevent.

(A) keeps one source of truth while still compiling the code into each binary, which is allowed:
"All code in the loadable module must be compiled with C/C++ compiler options compatible with native
AOT static libraries" is a *static-library* caveat; for shared libraries the sample explicitly says
"These problems don't exist when you build a shared library."

The source-shared SDK also carries the ABI file itself, so host and plugins share one `Abi.cs`.

---

## 5. Packaging, distribution and runtime update

**Owner decisions (2026-09-27):** plugins are **not bundled in the MSI/AppImage**. Each is published as a
GitHub Release asset per OS/arch plus a machine-readable manifest, and the host **downloads the matching
plugin at runtime**, verifies it, caches it per-user, and offers a **restart** to enable it. The shipped
host binary contains no vendor plugin.

### 5.1 Project layout

```
AcerHelper.csproj                         (host; unchanged TFMs, no plugin reference)
plugins/
  AcerHelper.Vendor.acer-nitro/
    AcerHelper.Vendor.acer-nitro.csproj   (PublishAot, OutputType=Library, NativeLib=Shared implicit)
    ...moved Acer sources, *.Windows.cs / *.Linux.cs kept...
    acer-models.json                      (EmbeddedResource, moves with it — see §1.4)
  AcerHelper.Vendor.dell-xps/             (later phase)
  AcerHelper.Vendor.asus-rog/             (later phase)
  AcerHelper.Vendor.Proof/                (Phase 0 only)
```

Plugin csproj, per OS/TFM:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0-windows;net10.0</TargetFrameworks>
    <PublishAot>true</PublishAot>
    <NativeLib>Shared</NativeLib>
    <IsAotCompatible>true</IsAotCompatible>          <!-- enables the AOT/trim/single-file analyzers -->
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <AssemblyName>AcerHelper.Vendor.acer-nitro</AssemblyName>
    <!-- The id the manifest and the cache folder use; must equal ah_plugin_id()'s string. -->
    <PluginId>acer-nitro</PluginId>
  </PropertyGroup>
  <ItemGroup>
    <!-- same file-name OS split as the host -->
    <Compile Remove="**/*.Linux.cs" Condition="$(TargetFramework.Contains('windows'))" />
    <Compile Remove="**/*.Windows.cs" Condition="!$(TargetFramework.Contains('windows'))" />
    <Compile Include="..\..\Infrastructure\Plugins\Sdk\**\*.cs" />   <!-- Abi.cs + transports -->
  </ItemGroup>
</Project>
```

### 5.2 Published artefacts: one asset per OS/arch, plus a manifest

The release no longer carries plugins *inside* the app artefacts. It carries:

| Asset | Built by | Notes |
|---|---|---|
| `AcerHelper.Vendor.acer-nitro-win-x64.dll` | Windows job | Native AOT shared library; symbols dropped |
| `AcerHelper.Vendor.acer-nitro-linux-x64.so` | Linux image | same |
| `AcerHelper.Vendor.acer-nitro-win-x64.dll.sig` | Windows job | detached signature (§5.4) |
| `AcerHelper.Vendor.acer-nitro-linux-x64.so.sig` | Linux image | same |
| `vendor-plugins.json` | a small job that aggregates each plugin job's output | the manifest, below |

**Asset naming** is `<PluginId>-<os>-<arch>.<ext>` prefixed with the assembly name, so a human scanning the
release page and the host's parser agree on the shape. `os ∈ {win, linux}`, `arch ∈ {x64}` today;
`System.Runtime.InteropServices.RuntimeInformation` (`RuntimeInformation.ProcessArchitecture`,
`RuntimeInformation.IsOSPlatform`) gives the host the two tokens it matches manifest entries on — it does
**not** guess the file name: the manifest names the exact `asset`, so a wrong-extension or renamed upload is
a manifest bug caught at download, not a path the host assembles from string parts.

**The manifest** (`vendor-plugins.json`), one JSON document attached to the same release:

```jsonc
{
  "schema": 1,
  "plugins": [
    {
      "id": "acer-nitro",
      "vendor": "acer",
      "os": "win",              // "win" | "linux"
      "arch": "x64",
      "version": "1.2.0",       // plugin's own version (see 5.3 on staleness)
      "api": { "major": 2, "minor": 0 },  // the plugin API the ASSET was built for; selection key (§3.8.4)
      "minHost": "0.38.0",      // oldest app <Version> that can load it (checked against AppInfo.Version)
      "matchHint": {            // ADVISORY pre-filter so the host does not load every plugin (§3.2)
        "manufacturer": ["Acer"],
        "product": ["Nitro", "AN18"]
      },
      "asset": "AcerHelper.Vendor.acer-nitro-win-x64.dll",
      "url": "https://github.com/Sanchous98/acer-helper/releases/download/v0.38.0/AcerHelper.Vendor.acer-nitro-win-x64.dll",
      "sha256": "…64 hex…",     // over the asset bytes; the signature is over this string + version+id (§5.4)
      "sig": "https://…/AcerHelper.Vendor.acer-nitro-win-x64.dll.sig",
      "sigAlg": "ES256",        // "ES256" primary; "Ed25519" only if IsSupported on the shipped runtime (§5.4)
      "keyId": "2026-09-release" // which embedded public key signed it (rotation, §7.3 item 2)
    }
  ]
}
```

The host reads this with a source-generated `JsonSerializerContext` (`GithubJsonContext` precedent,
`UpdateChecker.cs:100-102`) — no reflection, AOT-safe. **It is fetched from the release the app is already
told about**: `UpdateChecker.CheckAsync` resolves `releases/latest` and returns `UpdateInfo.Assets`
(`UpdateChecker.cs:17,45-52`); the plugin path simply asks for `vendor-plugins.json` among those assets
instead of adding a second GitHub API call, so the plugin and app update checks share one network path.

Entries are indexed by **`pluginId + os + arch + api.major`** and filtered by `api.major ∈ SupportedMajors`
and `minHost ≤ AppInfo.Version` before selection (§3.8.4 gate 1). During the deprecation window a release
carries both majors' assets, so a host still on the deprecated major downloads its own build rather than one
it cannot load; an entry whose major is neither current nor deprecated is never downloaded and, if it was
expected, is surfaced as `status.plugin_incompatible`.

### 5.3 Distribution and runtime update (reusing the existing update machinery)

The whole runtime-update flow is the existing self-update machinery, re-pointed at a new asset kind. It
adds **no** new schedule, **no** new notification surface and **no** new download primitive:

| Concern | Existing type (reused) | Plugin path |
|---|---|---|
| **When** to check | `UpdateSchedule` — `Start()` = check now, then every 6 h, on resume, and on window-shown (`UpdateSchedule.cs:44,85-111`) | the *same* schedule drives the plugin check; no second timer |
| **What** release/assets | `UpdateChecker.CheckAsync` + `UpdateInfo.Assets` (`UpdateChecker.cs:20-55`) | find `vendor-plugins.json`, parse it |
| **Which** action | `UpdateRouter.Route` (`UpdateRouter.cs:36-47`) is a pure function of assets + OS facts | add a `PluginAsset` pick, chosen by `id + os + arch + api.major` (§5.2) |
| **Download** | `HttpClient` with a 5-min timeout + UA, off the UI thread — `WindowsUpdater.DownloadAsync` (`WindowsUpdater.cs:61-80`), `AppImageUpdater.ReplaceAsync` (`AppImageUpdater.cs:27-54`) | one `PluginCache.DownloadAsync`, same shape: temp file in the target dir then atomic `File.Move` |
| **Notify / restart** | `NotificationCenter.Raise` + the expandable-body entry (`NotificationCenter.cs:99-110`, `NotificationViewModel.cs:76-96`); restart text `update.installing`/reboot-pending precedent (`AppController.cs:503`, `NotificationCenter.HardwareAccessRebootPending`) | a `plugin:<id>` notification whose click runs the download, then raises "... restart to enable" |
| **Single-flight** | `AppController._updating` guard taken on the UI thread before `Task.Run` (`AppController.cs:464-483`) | the same `StartUpdate` guard wraps the plugin download |

**Flow.** On startup and on each `UpdateSchedule` tick:

1. The host resolves the release and its assets (already happening for the app; the plugin check rides it).
2. It determines **this machine's line**. If a candidate plugin is already cached, this is the same
   scan/`ah_matches` mechanism as `DeviceFactory` (§4.1) — "which plugin do I want" is answered by the same
   code that will later load one. On a machine with **no** plugin cached yet there is nothing to call
   `ah_matches` on, so the host falls back to the manifest's advisory `matchHint` (§3.2, §5.2) to choose
   which candidate to download; `ah_matches` then confirms it against the real DMI before install (step 4).
3. For each manifest entry whose `os`/`arch` match and whose `api.major` is in `SupportedMajors`
   (§3.8.4 gate 1): if the cache (§5.3.1) has no copy, or a copy older than the manifest's `version`
   (or than the host's `PluginApi.Major`, which makes it non-loadable), it is **missing/outdated**. The
   cache is keyed by `pluginId` **and** major, so both majors' builds can coexist during the window.
4. The host **downloads the candidate in the background**, verifies it (§5.4), and if it matches this machine
   (`ah_matches`) installs it into the cache atomically.
5. It raises a notification offering a **restart to enable it**. On the next launch, `DeviceFactory.Create`
   finds the plugin in the cache, verifies it again (cheap, local), loads it **synchronously**, and the line
   backend is active.

**The hard constraint, stated plainly: a freshly downloaded plugin cannot take effect until restart.**
`DeviceFactory.Create()` runs once, synchronously, at composition, and `LaptopService`'s constructor copies
the declared-settings set and captures the port set (`DeviceFactory.cs:22-27`, `LaptopService.cs:85`). The
session is created once and lives for the process; AOT plugins cannot be unloaded (§2.3), so even if the
host re-ran the scan it could not retire the running one. That is why the notification is a **restart**
offer, not a "turn it on" toggle — the same posture the app already takes for a MKL access grant
("restart to use the unlocked controls", `AppController.cs:543-548`).

Alternative considered and rejected: **a late, second `DeviceFactory` pass after the download**, building a
second `Device`/`LaptopService` and swapping it into the running UI. Rejected because (a) `LaptopService`
holds `_state`, subscriptions and schedules created against the first device — swapping it means tearing
down and rebuilding the whole composition and UI, which is exactly a restart performed worse; (b) two
plugins' runtimes would be resident for the rest of the process (§2.3, §7.1 risks 1 and 4), doubling the
two-runtime surface; (c) it makes "the device changed under a running UI" a state the UI must handle
everywhere, for a one-time event. A restart is the honest, and the smaller, change.

#### 5.3.1 Cache location (per-user, writable; the install is read-only)

| OS | Path |
|---|---|
| Windows | `%AppData%\AcerHelper\plugins\<plugin-id>\` |
| Linux | `~/.local/share/acer-helper/plugins/<plugin-id>/` |
| dev override | `%ACERHELPER_PLUGIN_DIR%` (scanned first, §4.1.1) |

**Why not next to the binary.** The Windows install lives under Program Files and the Linux artefact is an
AppImage — both **read-only** (the AppImage is a mounted squashfs; `WindowsUpdater.IsSupported` exists
precisely because the exe under Program Files is locked, `WindowsUpdater.cs:17-36`). The app already keeps
its writable state per-user (the `acer-models.json` override at `%AppData%\AcerHelper\`,
`AcerModel.cs:96-100`; the portable-update staging dir at `%LOCALAPPDATA%\AcerHelper\update`,
`WindowsUpdater.cs:50-57`), so the plugin cache follows the same rule. This is the deliberate contrast with
the app's *bundled* files, which are read from `AppContext.BaseDirectory` because they ship **inside** the
artefact (`HardwareAccess.Bundled`, `HardwareAccess.cs:463-467`; the four Linux files carried by the csproj
at `AcerHelper.csproj:48-70`): bundled = read-only install, downloaded = per-user cache.

The cache is keyed by plugin id and API major, and each install writes `<id>/<asset>` plus a small
`<id>/installed.json` (version, `api.major`/`api.minor`, sha256, keyId) so startup verification is a local
hash check, no network, and so a host that later moves majors knows which cached build it has. Installs are
atomic (temp name in the same directory, then `File.Move`, as `AppImageUpdater.ReplaceAsync` does at
`AppImageUpdater.cs:31,46`) so a crash mid-download never leaves a half-plugin that later loads.

### 5.4 Code signing and trust

**Owner decision: add code signing.** The threat is specific and worse than a normal app update: the asset
is native code that the host will `NativeLibrary.Load` **into its own process**, it is not executable by the
OS (so nothing else will vouch for it), and it arrives **over the network at runtime**. Integrity is
therefore the gate, and the host must verify it itself.

**Recommendation: a detached signature over the plugin binary, verified by the host against a public key
embedded in the host binary. Primitive: ECDSA P-256 via `System.Security.Cryptography` (primary), Ed25519
if and only if it proves available on the shipped runtime.** Rationale against the alternatives:

- **ECDSA P-256** (`ECDsa.Create(ECCurve.NamedCurves.nistP256)` + `VerifyData`) is the recommendation
  because it is available on **every** .NET target, is AOT-safe (no `RequiresDynamicCode`), and `SHA256`
  is in-box. Its only real cost versus Ed25519 is a marginally larger/looser encoding (a DER ECDSA
  signature is variable-length, ~70–72 bytes, versus Ed25519's fixed 64), which is irrelevant at one
  download per machine.
- **Ed25519** is preferred *on the merits* — smallest, fastest, deterministic, fixed-length, no parameter
  pitfalls — but its availability is the open risk: the BCL type `System.Security.Cryptography.Ed25519`
  traces to dotnet/runtime#63174 and its `IsSupported` is platform-backed (OpenSSL on Linux, the OS
  elsewhere); it is **not guaranteed present through .NET 10**. **This must be checked with a one-line
  probe on the Windows AOT runtime before it is chosen**; if `Ed25519.IsSupported` is false there, use
  P-256, which is what the host will ship by default unless the probe changes the answer.
- **RSA** works but is the largest key/signature; not chosen.
- **Third-party (BouncyCastle/NSec/libsodium)** is rejected: it adds an unmanaged or large managed
  dependency to the AOT host for a primitive the BCL already provides.

**What is signed.** Sign over the **manifest entry**, not the bare bytes, so the metadata cannot be
tampered with: a canonical string containing `id`, `os`, `arch`, `version`, `api.major`, `api.minor`,
`minHost`, `sha256` (the binary's SHA-256), signed by the private key. The host then (1) checks the
manifest's `sha256` equals the downloaded binary's SHA-256, and (2) verifies the signature over that
canonical string with the embedded public key.
This chains signature → manifest hash → binary, so neither the binary nor its metadata (its API major
included) can be swapped.
The alternative — a detached raw signature over the binary only — is simpler but leaves `version`/`api`
forgeable; rejected for that reason.

**Key material.** The private signing key is a **CI secret** (GitHub Actions secret; no key on the build
host beyond the signing step). The **public key is embedded in the host binary** (a compile-time constant,
like `AppInfo.Version`, `AcerHelper.csproj:220-237`), so verification needs no second download and no
trust-on-first-use. `keyId` in the manifest selects which embedded key signed it, so rotation is possible
without breaking old clients (a client accepts the keys it was built with; see §7.3 item 2).

**Authenticode is a separate, optional layer and does NOT replace this.** Windows Authenticode signs an
**exe the OS launches**; a DLL the application loads itself is **not** subject to Windows' signature
enforcement — the OS never checks it. So the app's own verification above is the real gate on both OSes,
and an Authenticode certificate would only add (a) a familiar badge on the release asset and (b) a second,
independent signature Windows tooling can inspect. It costs a code-signing certificate (an OV/EV cert, a
recurring purchase, or Azure Trusted Signing) and would not run for the Linux `.so` at all. **Owner
question: buy an Authenticode cert, or rely on the app's own Ed25519/ECDSA verification?** (Recommendation:
rely on the app's own verification for v1; revisit Authenticode if SmartScreen/reputation on the *app*
artefacts becomes a problem, which is a separate concern from plugin trust.)

**Precedent in the repo (reused): `build/fetch-ryzensmu.ps1`.** The RyzenSMU fetch already implements the
same trust model at build time — resolve the release, download the asset, **verify its published SHA-256
`digest`** (`fetch-ryzensmu.ps1:266-273`), cache keyed by tag with a recorded hash
(`:209-240`), offline fallback to the verified cache (`:284-304`), and optional hard pinning
(`-PinnedSha256`/`-PinnedVersion`, `:39-52,176-191`). The plugin path **reuses** that shape (release asset
+ SHA-256 + cache + offline fallback + pinning) and **adds** what the build-time fetch does not need: a
**detached signature verified by the running host against an embedded key**, because at runtime there is no
trusted CI in the loop — the network is the only channel and the host is the only verifier.

### 5.5 CI

`build.yml` has two jobs by necessity (`build.yml:3-11`, `docs/build-image.md:99-150`): Native AOT cannot
cross-compile, so the Windows plugin is built on Windows and the Linux plugin inside the release image.

- **windows job** (`build.yml:36-187`): add, before the MSI/release steps, a `dotnet publish
  plugins/AcerHelper.Vendor.acer-nitro -f net10.0-windows -r win-x64 -p:PublishAot=true -o out/plugins`
  step, then a sign step (Ed25519/ECDSA over the canonical manifest string, private key from the Actions
  secret) producing the `.dll` + `.dll.sig`. No plugin is copied into `publish/` — the MSI harvest
  (`AcerHelper.wxs:39`) is untouched and the MSI now contains no vendor plugin.
- **linux job** (`build.yml:189-291`): the plugin is built **inside the release image** the same way the
  app is (the container that pins SDK/clang, `docs/build-image.md`), signed in the same run, and exported
  beside the AppImage — but **as release assets, not inside the AppImage** (`build.yml:277-287`).
- **aggregate job**: after both, a small job reads each plugin job's artefact + its recorded hash, writes
  `vendor-plugins.json` (§5.2), and attaches it (with the `.sig` files) to the same release the two jobs
  already attach to (`softprops/action-gh-release`, `build.yml:183-187` / `:290+`).

Every plugin is built in its own OS job, per arch (x64 only today). Signing runs in the job that produced
the bytes, so the hash in the manifest and the signature over it are computed from the exact artefact
attached to the release.

**The deprecated major is a CI leg, and it is deleted with the adapter.** While the compatibility window
(§3.8.3) is open, each plugin is built **twice** — once against `PluginApiVersion` (current) and once
against the deprecated major — producing two assets per OS/arch whose manifest entries differ only in
`api.major` (and `minHost`). The aggregate job writes both entries. When the deprecated major reaches its
removal trigger, the same PR deletes the deprecated **build leg here**, its manifest entries, its adapter
(§3.8.2) and its test fixtures (§6) — one change, cited to the trigger, so the CI matrix shrinks back to one
leg per plugin rather than growing forever.

---

## 6. Phased migration plan

Each phase is independently verifiable and keeps the shipped Acer/Dell/Generic behaviour byte-for-byte
until the phase that explicitly moves it.

### Phase 0 — ABI + loader + adapters + proof plugin, zero behaviour change

Deliverables:

1. `Infrastructure/Plugins/VendorAbi.cs` — the shared ABI declarations (exports as `[UnmanagedCallersOnly]`
   in the plugin copy; `delegate* unmanaged[Cdecl]` signatures + structs in the host copy), including the
   `ah_plugin_id` / `ah_matches` contract (§3.2) and the `ah_abi_version` / handshake contract (§3.8).
2. `Infrastructure/Plugins/VendorPluginLoader.cs`, `PluginSession.cs`, `PluginCache.cs`, the generated
   `PluginApi.cs` (§3.8.1), and `Abi/` with `IPluginAbiAdapter`, `PluginAbiRegistry`, and at least the
   current-major adapter (§3.8.2).
3. Adapters in `Infrastructure/Plugins/Adapters/` **not yet wired into `DeviceFactory`**.
4. `plugins/AcerHelper.Vendor.Proof/` — a trivial plugin that returns a fixed manifest (one profile,
   one fan capability, no settings), a fixed `ah_plugin_id`, and a fixed `ah_matches`; used to measure load,
   two-runtime behaviour, startup cost and file size, and to exercise the loader's scan/score/select path.
   Because Phase 0 has only one shipped major, this step **also builds a "deprecated-major" fixture** (a
   second proof build pinned to a synthetic previous ABI) so the two-adapter path of §3.8.2 exists to test
   before any real major bump.
5. The signing primitive and the manifest schema (§5.2, §5.4): a host-side verifier against an embedded
   public key, and a test fixture signed with a test key.
6. A hidden host flag (e.g. `--plugin=<path>`) or a test-only composition path that builds a
   `PluginVendorDevice` from the proof plugin, so the adapter path is exercised without changing the
   default `DeviceFactory` branch.

Verification:

- Tests: an ABI round-trip test that loads **the built proof plugin** and asserts every export
  returns the documented status; a test that `ah_free`/`ah_dispose` are called exactly once; a
  loader test that two proof copies at 80 and 50 confidence select the 80, and equal confidences fall
  back to `GenericDevice`.
- A one-off measurement (documented in this file, not a test) of: plugin binary size, host `Load`
  latency, and a 30-minute soak with the host's background pass running to check the two-runtime
  signal hazard (§7.1 risk 1).
- The 1123 existing test methods stay green by construction: nothing in `DeviceFactory` changed.

**Gate to proceed**: the soak must not hang or crash, and load latency must be acceptable (target
< 50 ms per plugin; measure, do not assume).

### Phase 1 — Acer, Windows first

**Owner decision: Acer is the first plugin, not Dell.** The reasoning is that Acer is the machine the
owner has, so it is the only line whose behaviour can actually be verified on hardware; Dell would exercise
the adapters but on a machine nobody can test. The cost is accepted honestly: Acer is 19 files / 2 403 lines
and drags the HID writer-thread machinery (`AcerEcHidController`, `EneHidController`), which Dell would not.
The transport SDK extraction from §4.4 is therefore due **in this phase**, not a later one.

Phase 1 steps, file level:

1. Extract the generic transports into `Infrastructure/Plugins/Sdk/` and source-include them in both the
   plugin and the host (the list and sizes are in §4.4). This is its own verifiable slice (§7.1 risk 3).
2. Create `plugins/AcerHelper.Vendor.acer-nitro/` and move `Infrastructure/Vendors/Acer/*` into it; add
   `AcerHelper.Vendor.acer-nitro.csproj` (PublishAot, Shared) and move `acer-models.json` (§1.4).
3. Implement `ah_create` in the plugin by rewriting `AcerDevice.InitVendor` (`AcerDevice.Windows.cs:27-115`)
   to fill the manifest and register ops instead of writing into a `Device`; `ah_plugin_id` returns
   `"acer-nitro"`, and `ah_matches` discriminates on the `acer-models.json` `Match` substrings.
4. `DeviceFactory.Create` gains the plugin branch; the Acer `if` is deleted. The `Dell` branch stays until
   Phase 3.
5. Move Acer-only tests or re-point them; add a plugin-manifest test asserting the Acer plugin declares
   exactly the settings/ports the old `AcerDevice` filled.

Order inside Acer: **Windows first**, because the Linux half depends on sysfs/hwmon transports shared with
the generic OS backend and needs the SDK extraction (step 1) exercised first. Move files in dependency
order: pure data (`AcerProfiles`, `AcerModel` + resource, `AcerProfilePorts`, `AcerFanPort`, `AcerGpuMux`,
`AcerHotkeyReports`, `RgbEffects`) → codecs (`EneHidController`, `AcerEcHidController`) → OS transports →
wiring (`AcerDevice.*`, `AcerBattery.Windows`) → hotkeys. The `StatusMessage` strings move as keys
(`AcerDevice.Windows.cs:32`, `.Linux.cs:94-104`).

### Phase 2 — Acer, Linux

The Linux half of the same `acer-nitro` plugin: hidraw (RGB + EC envelope), mainline `acer-wmi`
platform-profile and hwmon, and the evdev/vendor-HID hotkeys (`AcerDevice.Linux.cs`). It reuses the SDK
extraction from Phase 1 and adds the `--plugin` path's Linux transport cases. Its hardware verification is
the weaker of the two (§7.1 risk 1 must be measured here too), so it ships only once the Phase 0 soak passes
on Linux.

### Phase 3 — Dell, ASUS, and other lines

Dell then ASUS. Dell is the **smallest and maps to the adapters already specified** (profiles, battery
properties, declared flag/choice, keyboard brightness) — it exercises every adapter kind, including the
`ChoiceSetting` and `BatteryChoice` paths, without the HID writer-thread machinery. When a second Acer line
gets its own plugin, the `acer-models.json` split (§1.4) decides whether it shares the file or embeds a
slice; the host needs no change because the loader is already line-agnostic. When ASUS arrives, "the ASUS
build" is simply the set of release assets a user downloads — there is no build-time vendor selection at
all. `DeviceFactory` only knows `GenericDevice` + "load the best-matching downloaded plugin".

### Keeping the suite green, and the guards

- **"Host core contains no vendor type names"** — extend `ArchitectureMapTests`
  (`tests/.../ArchitectureMapTests.cs:122-135` shows the existing file-name/layer guard style): a
  source-text rule over `Domain/`, `Application/`, `Infrastructure/` (excluding `Infrastructure/Plugins/Adapters`)
  and `UI/` that forbids `Acer`, `Dell`, `Ene`, `AcerEc`, `DellBios`, `acer-models` outside the
  plugin subtree. Mutation-verify it by adding `using AcerHelper.Infrastructure.Vendors.Acer;` to a
  host file.
- **ABI round-trip** — a test that publishes the proof plugin (or loads a prebuilt one) and drives
  every op, asserting status codes and JSON shapes; plus a version-mismatch test.
- **API versioning (§3.8)** — the tests the two-major design needs:
  - the ABI round-trip driven through **both** the current and the deprecated adapter, against a plugin
    fixture of each major (a version-pinned fixture per major, so a fixture never silently follows the
    current ABI);
  - a plugin fixture whose `ah_abi_version` major is unknown/removed is refused with
    `status.plugin_incompatible` (§3.8.4 gate 2);
  - a manifest entry with an `api.major` outside `SupportedMajors` (or `minHost` above the host) is **not
    selected** (§3.8.4 gate 1);
  - an `ah_create` refused with `-2 AbiMismatch` by a "newer" fixture is surfaced as the status line
    (§3.8.4 gate 3).
  - When a major is finally removed (§3.8.3), its fixture and its adapter's tests are deleted **with it**,
    in the same PR that cites the removal trigger — the test matrix shrinks by exactly one major, it does
    not accumulate.
- **"Each plugin exports the ABI entry points"** — a test (or CI step) that runs `nm`/`dumpbin` on
  each built plugin and asserts the export names (`ah_abi_version`, `ah_plugin_id`, `ah_matches`, …) are
  present. Cheapest correct version: `NativeLibrary.Load` + `GetExport` for each name in a smoke test on
  the build host.
- **Manifest ↔ asset consistency** — a CI step that, for every plugin built, re-hashes the attached asset
  and asserts it equals the `sha256` in `vendor-plugins.json`, and verifies the `.sig` with the public key.
  The same verifier the host runs (§5.4), compiled into the test, so the host's trust path is tested on the
  real artefacts and not only on a fixture.
- **Cache/update** — a test that a plugin placed in a fake `%ACERHELPER_PLUGIN_DIR%` (or a temp
  `~/.local/share` root) is discovered, hash-verified, loaded, and that a tampered byte makes the loader
  fall back to `GenericDevice` **with** `status.plugin_incompatible` (§4.1.1).
- **Domain neutrality** stays (`DomainNeutralityTests.cs`), because the ABI carries Domain vocabulary
  only (`PerformanceProfile`, `ChoiceOption`, `ProfileKind` stays host-side in the manifest `kind`
  string → the host parses it into its own `ProfileKind`).
- The 1123 test methods: tests that currently reach vendor internals via `InternalsVisibleTo`
  (`AcerHelper.csproj:87-91`) will not see the moved code. Each such test must either move to a
  plugin-side test project or drive the plugin through the public ABI. This is explicit work in
  Phases 1–2, not an accident.

---

## 7. Risks, resolved questions, and what remains open

### 7.1 Risks (unchanged, or updated where a decision moved them)

1. **Two Native AOT runtimes in one process (highest risk) — MEASURED CLEAN, 2026-09-27.** The host and
   every plugin each carry a runtime with its own GC and signal handlers. `dotnet/runtime#121345` is a
   real hang caused by exactly this interaction. Mitigations in the design: no callbacks except the one
   event sink, all plugin threads internal, no plugin P/Invoke back into host code. This was
   **measured with the Phase 0 proof plugin on both OSes** — run `plugin-soak` 36319990062,
   `conclusion=success`, both jobs (`win-x64`, `linux-x64`) `success`:
   - smoke: all eight `ah_*` exports resolved; `ah_abi_version` `0x00010000`; `ah_matches` true for a
     "Proof" descriptor and false otherwise; `ah_create` returned a 482-byte manifest + handle 1;
     `ah_invoke(Power.Current)` → `{"id":"proof"}`; `ah_dispose` called.
   - soak: **30 minutes each, ~161.4 M (Linux) / ~164.1 M (Windows) `ah_invoke` iterations with GC
     churn every iteration**, managed heap steady (32–82 MB), `clean finish — no hang, no crash`.
   This retires the empirical unknown that gated the approach: a single AOT host loading a single AOT
   plugin is stable over a sustained soak on both OSes. It does NOT prove a two-runtime hang is
   impossible in general (the field report stays real), so the mitigations above remain the design's
   posture, and `plugin-soak` stays as a weekly regression net. If a future run destabilises, the
   fallback is reverting to build-time vendor selection — a decision for the owner.
2. **No unload, no isolation.** Learn and `dotnet/runtime#64629`: Native AOT libraries cannot be
   unloaded. A plugin is trusted in-process code; a bug crashes the app, and a plugin can never be
   hot-swapped. This is *why* the runtime-update path offers a **restart** rather than a live enable
   (§5.3): a downloaded plugin cannot be retired either, so swapping it in-place is not available. The
   plugin boundary is *not* a security boundary — which is exactly why the **integrity** boundary is
   enforced by the host's own signature check (§5.4), not by the OS.
3. **The vendor/transport entanglement costs more than expected.** Acer/Dell depend on generic
   transports (`WmiSession`, `SysfsInvoker`, `FirmwareAttributes`, `Hwmon`) and on `GenericDevice`
   inheritance. Phase 1 must extract a source-shared SDK, and that extraction touches files the
   existing tests read as text (`AcerLinuxWiringTests`, `ArchitectureMapTests`). Budget this as its
   own verifiable slice; do not fold it into the vendor move.
4. **Startup cost and size, now with a download.** Each AOT plugin is multi-MB and carries a runtime;
   startup loads and initialises it. With runtime download the first launch on a matching machine has
   no line backend until the plugin is fetched and the app restarted (§5.3), so the "day one" cost is
   a download plus one restart, not zero. Measure in Phase 0.
5. **AOT analyzer / IL warnings inside plugins.** `IsAotCompatible=true` on each plugin surfaces
   warnings the host build may not have shown for the same code. `HidSharp` is already rooted for the
   host (`AcerHelper.csproj:172-174`) and must be rooted in the Acer plugin too, or the Windows RGB
   backend can be trimmed away silently.
6. **Debugging across the boundary.** A managed exception inside the plugin cannot cross to the host
   as an exception; it becomes `-1 Internal` + JSON. Native debuggers work (`Native AOT diagnostic
   support`), but managed stack fidelity across the ABI does not. The JSON capture is the compensation.
7. **`InternalsVisibleTo` and the text-reading tests.** Moving vendor code out of the main assembly
   breaks the test project's access to `internal` vendor encodings and invalidates source-text guards
   that read vendor files. This is the main source of test churn.
8. **Resources.** `acer-models.json` moves with the Acer plugin and keeps its `%AppData%` override
   path (`AcerModel.cs:94-105`) — **decided in §1.4**. **RyzenSMU.bin must NOT move** — it is the
   generic AMD axis (`GenericDevice.Windows.cs:21-25`), and the LGPL notice travels with the host build
   that still embeds it.
9. **Hotkeys need a callback (plugin → host).** This is the one place the ABI goes two-way. It is the
   riskiest part of the ABI w.r.t. risk 1 (a foreign thread entering host code). Phase 0 should
   include the event sink and soak it, or hotkeys should be the last capability moved.
10. **Runtime fetch failure modes.** A plugin download can fail (offline, rate-limited, no matching
    manifest entry). This degrades to "no line backend this session", which is the current no-plugin
    fallback and never a crash — but it must not be noisy (the app already checks silently,
    `UpdateChecker.cs:30-54`). The notification is raised only when a plugin was *found and
    installed* and needs a restart, never for "could not check".

### 7.2 Resolved questions (former §7 open items 10–12)

- **Granularity: per line, not per vendor** — §1.4. The mechanism is per-line; the *shipping* decision
  is to start with one Acer plugin and split when hardware evidence demands it. The former "one per
  vendor or one per capability?" question is answered: the ABI is capability-grained inside the
  envelope, but the **unit of distribution is a line plugin**, because the whole point is that the
  artefact matching one machine's model family need not carry another's. Splitting further (per
  capability) is not pursued: it multiplies runtimes (§7.1 risk 1/4) for no measured benefit.
- **`DeviceFactory` fallback on a matched-but-incompatible plugin** — §4.1.1: fall back to
  `GenericDevice` **with** `status.plugin_incompatible` on the status line (`Device.StatusMessage`,
  `Device.cs:40`, rendered via `Loc.T` at `AppController.cs:880`). A matched-but-broken plugin is a
  visible broken install, not the neutral unknown-vendor case; silent fallback stays only for
  *no match / no plugin*.
- **Where the plugin search path lives** — §4.1.1/§5.3.1: the per-user cache
  (`%AppData%\AcerHelper\plugins\<id>\`, `~/.local/share/acer-helper/plugins/<id>/`) plus a
  `%ACERHELPER_PLUGIN_DIR%` dev override. **Not** `AppContext.BaseDirectory`: the MSI/AppImage install
  is read-only and the plugin arrives over the network. This also answers the third-party-plugin
  question by construction: the cache is user-writable, so a user-placed plugin is loadable, and it is
  subject to the same signature gate (§5.4) — there is no unsigned-plugin path.
- **API version ownership and the compatibility window** — §3.8: a host-owned `PluginApiVersion`
  separate from the app `<Version>`; the host holds the current major and the immediately-previous
  (deprecated) one via per-major adapters; three gates (**manifest** before download, **load** with
  `ah_abi_version` authoritative, **handshake** at `ah_create`); additive-only within a major, any
  breaking change is a new major. The removal trigger is **decided as a mechanism** (§3.8.3); its exact
  interval remains a knob (see §7.3 item 7).

### 7.3 Open questions for the owner (remaining)

1. **Authenticode certificate: buy one or not?** (§5.4) The app's own Ed25519/ECDSA verification is the
   real gate because Windows does not check a self-loaded DLL. An Authenticode cert is optional
   reputation/defence-in-depth for the *plugin* assets (and does nothing for the Linux `.so`).
   Recommendation: no for v1.
2. **Key management and rotation.** Which key store holds the private signing key (Actions secret vs
   a KMS/HW-backed signer), and how `keyId` rotation is handled: a client accepts only the key(s) it
   was built with, so a rotated key reaches old clients only after an app update. Needs a policy:
   how many old keys a host embeds, and the deprecation timeline.
3. **Exact `acer-nitro` vs `acer-predator` discrimination.** §1.4 found **no in-tree evidence** that
   the two differ in WMI calls or EC envelopes; the differences the tree records (`acer-models.json`)
   are name + RGB layout only. This must be **verified on Predator hardware** (or upstream evidence)
   before shipping two plugins. Until then, ship one Acer plugin.
4. **`acer-models.json` per plugin or shared.** §1.4 leaves open whether each line plugin embeds its
   own slice or the Acer line plugins source-include one shared file. The former keeps match rules
   with the line; the latter stops the substrings drifting. Owner's call.
5. **Manifest cadence vs app release.** Should `vendor-plugins.json` be re-published between app
   releases (so a plugin can be fixed without an app release), or only attached to app releases? The
   former makes plugins independently updatable — the stated benefit of this whole approach — but
   needs a release/CI path that does not bump the app version.
6. **Architectures.** x64 only today (`build.yml:157,290`). Does a future arm64 need a manifest entry
   per arch from the start (the schema already has `arch`), or is x64 a hard scope for v1?
7. **Exact major-removal cadence.** §3.8.3 fixes the *mechanism* (a deprecated adapter is removed at a
   recorded trigger, in the PR that cites it) but leaves the interval to the owner: "first app release
   after N minor releases of grace" vs a fixed duration (6 months) — and, tied to it, whether a host
   ever embeds **three** majors briefly during a transition, or strictly two as decided.

