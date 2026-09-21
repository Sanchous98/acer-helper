# NVIDIA dGPU clock offsets on Linux, via NVML

The Linux half of the GPU overclock axis. The **mechanism is the one `docs/nvidia-gpu-oc.md` describes** — a
signed core- and memory-clock offset layered on the P0 max-performance state's frequency delta, shifting the whole
voltage/frequency curve, volatile, re-applied by the app per performance mode — and this document does not restate
it. What it adds is the Linux transport: which library, which symbols, what they actually answer on this machine,
and the one finding that decides whether the feature can work at all.

Everything below was measured on the AN18-61 (RTX 5070 Ti Laptop) against the driver in place on 2026-09-20 —
NVRM `615.71.09`, the NVIDIA open kernel module. Commands used are `nm -D --defined-only`, `objdump -p`, `strings`
and a read-only `ctypes` probe; nothing here was inferred from documentation.

## Where it lives

| file | what it owns |
|---|---|
| `Infrastructure/Vendors/Generic/NvidiaGpuPolicy.cs` | un-suffixed: the availability gate, the safety caps, the range rule, the clamping, the write/confirm order and the NVML return-code vocabulary |
| `Infrastructure/Vendors/Generic/NvidiaGpu.Linux.cs` | the library, the P/Invoke declarations, the device handle, the probe |
| `Infrastructure/Vendors/Generic/NvidiaGpu.Windows.cs` | the NvAPI transport; now consumes `NvidiaGpuPolicy`'s caps and clamp rather than keeping a second copy |
| `tests/AcerHelper.Tests/NvidiaGpuPolicyTests.cs` | 36 tests over the policy, driven through a recording fake |
| `Infrastructure/Vendors/Generic/GenericDevice.Linux.cs` | one wiring line: `if (NvidiaGpu.TryCreate() is { } gpu) { GpuOverclock = gpu; Own(gpu); }` |

The policy is un-suffixed for the reason `AcerFanPort.cs` and `CurveOptimizerPolicy.cs` state at length: the test
project targets `net10.0-windows` and `AcerHelper.csproj`'s `<Compile Remove>` keeps `**/*.Linux.cs` out of that
TFM, so anything left in the Linux file cannot be compiled by the suite at all. The UI is OS-agnostic and needed no
change: `NvidiaGpu.Linux.cs` presents exactly the surface its Windows twin does.

**One rule was moved rather than duplicated.** The safety caps (`CoreCap`/`MemCap`) and `ClampOffset` now live in
`NvidiaGpuPolicy` and both OS ports call them. That is why `NvidiaGpu.Windows.cs` changed: two OS-local copies of a
safety rule are the thing that drifts while both look correct.

## The library

```
ldconfig -p | grep nvidia-ml
  libnvidia-ml.so.1 (libc6,x86-64) => /usr/lib64/libnvidia-ml.so.1
/usr/lib64/libnvidia-ml.so.1 -> libnvidia-ml.so.615.71.09
  SONAME   libnvidia-ml.so.1
  NEEDED   libpthread, libm, libdl, libc, librt
```

Runtime identity, read through the library itself:

| call | answer |
|---|---|
| `nvmlSystemGetDriverVersion` | `615.71.09` |
| `nvmlSystemGetNVMLVersion` | `13.615.71.09` |
| `/proc/driver/nvidia/version` | `NVIDIA UNIX Open Kernel Module for x86_64 615.71.09` |

431 exported symbols. The port binds the SONAME (`libnvidia-ml.so.1`), not the unversioned `libnvidia-ml.so`
development symlink.

## Symbols verified present

Every one of these exists in this build of the library (`nm -D --defined-only`):

| symbol | used for |
|---|---|
| `nvmlInit_v2`, `nvmlShutdown` | library bring-up / release |
| `nvmlDeviceGetCount_v2` | is a device visible |
| `nvmlDeviceGetHandleByIndex_v2` | the device handle |
| `nvmlDeviceGetName` | the section header |
| `nvmlDeviceGetGpcClkVfOffset` / `nvmlDeviceSetGpcClkVfOffset` | core offset |
| `nvmlDeviceGetMemClkVfOffset` / `nvmlDeviceSetMemClkVfOffset` | memory offset |
| `nvmlDeviceGetGpcClkMinMaxVfOffset` / `nvmlDeviceGetMemClkMinMaxVfOffset` | the two legal ranges |
| `nvmlDeviceGetClockInfo` | live clocks |
| `nvmlDeviceGetMinMaxClockOfPState` | per-P-state clock envelope |
| `nvmlDeviceGetSupportedMemoryClocks` / `…GraphicsClocks` | the supported-clock tables |
| `nvmlDeviceGetClockOffsets` / `nvmlDeviceSetClockOffsets` | the newer combined API (see below) |

**What does not exist:** there is no `nvmlDeviceSetClockOffset` (singular) and no `nvmlDeviceSetClockInfo`. The
"clock offset family" in NVML is a set of *absolute-clamp* APIs rather than offsets —
`nvmlDeviceSetGpuLockedClocks`, `nvmlDeviceSetMemoryLockedClocks`, `nvmlDeviceSetApplicationsClocks`,
`nvmlDeviceSetAutoBoostedClocksEnabled`, `nvmlDeviceSetPowerMizerMode_v1` — none of which is a signed delta on the
VF curve. The signed delta is only ever the `…ClkVfOffset` pair (and the newer `…ClockOffsets`, below).

### Two NVML families offer the same axis, and one of them was rejected

`nvmlDeviceGetClockOffsets`/`SetClockOffsets` is the modern interface and **is present and works on this device** for
the two domains that matter. The port uses the `…ClkVfOffset` pair instead, because it takes no struct: the
combined API's `nvmlClockOffset_t` carries a `version` word the caller must compute as
`sizeof(struct) | (1 << 24)`, which is a layout contract the four plain-`int` entry points do not have. The
**cross-check is the useful part**: asked the same question, the two families agree exactly —

| domain, P0 | `…ClkMinMaxVfOffset` | `GetClockOffsets` |
|---|---|---|
| graphics / core | `-1000 .. +1000` MHz | `min=-1000 max=1000` |
| memory | `-2000 .. +6000` MHz | `min=-2000 max=6000` |

`GetClockOffsets` for `SM` and `VIDEO` answers `NVML_ERROR_INVALID_ARGUMENT` — only the GPC and memory clocks carry
a VF offset, which is why the port exposes exactly two domains.

### Two ABI traps, both hit while measuring

1. **`nvmlDeviceGetMinMaxClockOfPState` takes TWO out-pointers, not a struct** —
   `(device, type, pstate, unsigned int *minClockMHz, unsigned int *maxClockMHz)`. Called with a single struct
   pointer, the second argument is written through an address the caller never provided; the first probe
   segfaulted on exactly this. The port's `…ClkMinMaxVfOffset` declarations are separate `out int` parameters for
   the same reason.
2. **`nvmlClockOffset_t` has a LEADING `version` field.** Omitting it does not produce a "not supported" answer —
   it produces `25`, which is `NVML_ERROR_ARGUMENT_VERSION_MISMATCH` and nothing like what it looks like. The
   return-code vocabulary in `NvidiaGpuPolicy` names `25` explicitly because of this.

## What the device reports, read live and unprivileged

Read as `uid=1000` with `CapEff=0`:

| item | value |
|---|---|
| device count | `1` |
| name | `NVIDIA GeForce RTX 5070 Ti Laptop GPU` |
| UUID | `GPU-47c28e90-7967-513c-3f83-2ff633b02096` |
| `GpcClkVfOffset` | `0` |
| `MemClkVfOffset` | `0` |
| core range | `-1000 .. +1000` MHz |
| memory range | `-2000 .. +6000` MHz |
| P0 graphics / SM | `180 .. 3090` MHz |
| P0 memory | `14001` MHz |
| P8 graphics | `180 .. 885` MHz |
| live clocks | graphics 180, SM 180, memory 405, video 600 MHz |
| supported memory clocks | `405, 810, 9001, 10821, 14001` |

**The memory range is why the caps exist on this side.** The driver offers `+6000` MHz of headroom against
`NvidiaGpuPolicy.MemCap = 1500`, so the slider's top is set by the app's safety rule, not by the driver. The core
range (`±1000`) is likewise intersected with `CoreCap = 300`. Without the caps this port would expose four times
the offset the Windows port does for the same physical effect.

**Units: NVML speaks MHz on both sides** — the getters, the setters and the range getters all take and return a
plain `int` in MHz. There is therefore **no kHz conversion** here, unlike the Windows port whose
`NV_GPU_PERF_PSTATES20_PARAM_DELTA` is in kHz and divides by 1000. `NvidiaGpuPolicyTests` pins the literal MHz
values that reach the transport so an accidental `/1000` (or a doubling into the Afterburner "effective"
convention) cannot survive.

## Privileges — the finding that decides the feature

**Reads work unprivileged. Writes do not.** Measured as `uid=1000`, `gid=1000`, `CapEff=0`, on a device NVML can
see:

| call | NVML answer |
|---|---|
| `nvmlDeviceSetGpcClkVfOffset(handle, 0)` | **`4` = `NVML_ERROR_NO_PERMISSION`** |
| `nvmlDeviceSetMemClkVfOffset(handle, 0)` | **`4` = `NVML_ERROR_NO_PERMISSION`** |
| `nvmlDeviceSetClockOffsets(handle, {unchanged})` | **`4` = `NVML_ERROR_NO_PERMISSION`** |

The value written was the value just read (`0 → 0`), so the attempts were inert by construction and the offsets
were confirmed still `0`/`0` afterwards. This is the analogue of the CPU port's "margin-0 (stock) write is inert"
— it establishes the permission answer without moving any clock.

### A udev rule is not the fix, and that is the important part

The obvious guess is a permissions problem on `/dev/nvidia*`. It is not:

```
crw-rw-rw-. 1 root root 195,   0 /dev/nvidia0
crw-rw-rw-. 1 root root 195, 255 /dev/nvidiactl
NVreg_DeviceFileMode = 438            # 0o666
```

The nodes are **already world-read/write** and the write is refused anyway. The gate is a privilege check inside
NVML/RM, not a file permission, so **no udev `MODE=`/`GROUP=` rule and no `chmod` can grant it.** `nvidia-smi`'s
own help agrees on the class of operation: `-lgc/--lock-gpu-clocks` "Requires administrator privileges", `-lmc`
"Requires root", and the one non-root permission toggle it offers (`--auto-boost-permission`) is explicitly scoped
to *auto boost* and not to VF offsets.

### What this means for the app, plainly

- **The feature cannot work from an unprivileged app.** Root is known to be in the fix set; the exact minimum could
  not be pinned here, because distinguishing "root" from "root or `CAP_SYS_ADMIN`" requires an elevated test that
  this work deliberately did not run. Treat it as: needs root, possibly `CAP_SYS_ADMIN`.
- **The app's existing hardware-access grant cannot carry it.** That install (`Infrastructure/HardwareAccess.cs`,
  run under one `pkexec`) grants *file permissions* to a group — node `chmod`s and udev rules for hwmon, the SMU
  `smn` node and friends. It does not and cannot hand out a capability, so the GPU write cannot ride it.
- **So the write path needs a mechanism the app does not have yet**: a small privileged helper that performs the
  two sets, or running the app elevated. That is a decision for the owner, not a detail of this port.
- The **UI consequence** is that the section can legitimately appear and then refuse every write with
  `NVML_ERROR_NO_PERMISSION` on a stock desktop Linux install. `NvidiaGpuPolicy.Describe` renders that as words the
  user can act on rather than a bare number.

## Volatility

The offset is a **runtime RM/driver parameter, not firmware** — the same class of state as the NvAPI P0
`freqDelta_kHz` that `docs/nvidia-gpu-oc.md` measured as lost on reboot, driver reload and dGPU D3-cold. It is
**not re-measured here**, and the reason is the privilege finding above: observing a lost offset requires writing
one, and the write is refused. What this port asserts about volatility is therefore inherited, not verified — said
plainly because a port that re-applies "because it is volatile" should be honest about which half of that sentence
it has seen.

Two things were confirmed that bear on it:

- `nvidia-smi -q` exposes **no clock-offset field at all** on this part; `Applications Clocks` reports "Requested
  functionality has been deprecated", and `Deferred Clocks` is `N/A`. There is no driver-side persistence surface
  to inspect.
- `nvidia-smi`'s `-gvfd --get-vf-derate-info`, which does report a VF offset range, is documented as "Only
  supported on Rubin and newer architectures". This RTX 5070 Ti is Blackwell, so **NVML's getters are the only read
  path available here** — which is also why this port's read half was worth verifying live.

The app's re-apply duty is unchanged and already implemented: `LaptopService` persists the pair per performance
mode and re-applies at startup, on resume and on every mode switch, through `IGpuOverclock.Set`.

## The gate, and the hidden-device case

`NvidiaGpuPolicy.Available` is a conjunction, and any missing term keeps the port null so the UI hides the section:

```
library loaded ∧ NVML initialised ∧ visibleDevices > 0 ∧ all three call shapes resolved
```

plus a successful read of **both** ranges — a driver that will not report its bounds cannot yield an honest
slider, so that is probe-never-require too.

**"No device visible" is not "this machine has no GPU."** On a laptop whose dGPU is handed out by an access broker,
a probe without the grant sees `nvmlDeviceGetCount_v2` answer zero — the identical answer a machine with no dGPU
gives. The port cannot and must not distinguish them: it returns null either way. What makes that correct rather
than lossy is that **the probe holds no state between runs**, so the section appears on the next composition the
moment the device becomes visible. That is why the gate is a per-run decision in the policy rather than a
once-and-for-all "is there a GPU" flag resolved at startup.

### What this means for the cardwire blocker

The brief anticipated that the dGPU would be hidden by the `cardwire` daemon during this work, so that no device
query could succeed. **That was not what was measured.** With `cardwired` running, `nvidia-smi -L` listed
`GPU 0: NVIDIA GeForce RTX 5070 Ti Laptop GPU (UUID: GPU-47c28e90-…)` and NVML answered `count = 1` with a valid
handle and name. The read half of this port was therefore verified **live against the real device**, not against a
mock.

Two consequences, stated so the result is not over-read:

- The gate's hidden-device branch is **verified by construction and by test**, not by observation on this machine —
  the machine never presented the hidden case while being measured.
- Because the device was visible, the privilege finding above is a **real measurement against the real GPU**, not a
  prediction. That is the strongest result in this document.

## What was verified, and what still needs the grant

**Verified live, on this machine:** the library's identity and its 431 exports; the existence of all eleven
symbols the port binds; the two ranges, the two offsets, the device name and the clock tables; that reads succeed
unprivileged; that all three setter forms answer `NVML_ERROR_NO_PERMISSION` (`4`) unprivileged and leave the
offsets untouched; that the `…ClkVfOffset` and `…ClockOffsets` families agree on the ranges; that `SM`/`VIDEO` carry
no offset.

**Verified by test (36 tests, `NvidiaGpuPolicyTests`):** the gate's decision table; that the probe writes nothing;
the caps and the degenerate-range fallback; that no `Cap` result can be a crossed range (so `Math.Clamp` cannot
throw on a slider drag); clamping; the write order (core → memory → confirm core → confirm memory) with fail-fast;
that a read-back mismatch or a failed read-back is a failure; and the return-code vocabulary.

**Not verified here, and not claimable from this work:**

- **A real offset has never been written through this path.** The write is refused to an unprivileged process, so
  the end-to-end write — and the read-back confirmation of a *non-zero* offset — remains the owner's first real
  write from the finished UI. Every non-zero value in the trace above is a test fake's.
- **The exact privilege required.** Root or possibly `CAP_SYS_ADMIN`; pinning it needs an elevated run, which this
  work did not perform.
- **Volatility.** Inherited from the Windows measurement, for the reason given above.
- **Whether a privileged helper is the shape the owner wants.** The port is structured so that whichever mechanism
  is chosen only has to make two `nvmlDeviceSet…ClkVfOffset` calls succeed; nothing in `NvidiaGpuPolicy` assumes
  how the privilege is obtained.

## Test-suite position

`podman run … dotnet test tests/AcerHelper.Tests` → **29 failed / 1392 passed / 1421 total**. The 29 are the
pre-existing Windows-only API failures (28 × `WmiVariantConversionTests`, which needs `oleaut32.dll`; 1 ×
`LightingControlGateTests`, which needs `SystemEvents`) and the count is **unchanged from the baseline**. The
passing count rose because of this port's 36 tests and other work present in the tree.
