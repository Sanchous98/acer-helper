# NVIDIA dGPU clock offsets via NvAPI

The discrete-GPU overclock axis: a signed core- and memory-clock offset written into the **P0 (max-performance)
state's** frequency delta, which shifts the whole voltage/frequency boost curve. Same mechanism G-Helper and MSI
Afterburner use. It is the GPU-side twin of the CPU Curve Optimizer (`docs/curve-optimizer-strix-point.md`): same
shape, same volatility, same re-apply duty.

Measured/verified on the AN18-61 (RTX 5070 Ti Laptop); the mechanism is driver-level and not model-specific.

## How NvAPI is called, and why it is done by hand

`nvapi64.dll` exposes **exactly one real export**: `nvapi_QueryInterface`. Every other function is reached by
passing that export a **stable hex ID**, which resolves to the function's pointer. Those pointers are then invoked
through unmanaged function pointers (`delegate* unmanaged`) over **blittable structs** — no runtime-generated
marshalling and no COM, so the whole thing is **Native-AOT-safe**. This is the same rule that forces the
hand-rolled WMI interop (see `docs/wmi-interop.md`): `System.Management` and classic COM interop are unavailable
under AOT.

| function | ID |
|---|---|
| `Initialize` | `0x0150E828` |
| `Unload` | `0xD22BDD7E` |
| `EnumPhysicalGPUs` | `0xE5AC921F` |
| `GetFullName` | `0xCEEE8E9F` |
| `GetPstates20` | `0x6FF81213` |
| `SetPstates20` | `0x0F4DAE6B` |

Clock domains: `CLOCK_GRAPHICS = 0` (core), `CLOCK_MEMORY = 4`. The state tuned is `PSTATE_P0 = 0`
(`NVAPI_GPU_PERF_PSTATE_P0`) — the **only editable/meaningful** performance state for this purpose.

## Volatility

The offset is **VOLATILE**. The driver **zeroes it on every reboot, driver reload, and dGPU power-cycle** — which
on an Optimus laptop happens routinely: the dGPU going **D3-cold** drops the offset. So the app is the source of
truth: `LaptopService` persists the user's choice **per performance mode** and re-applies it at **startup, on
resume, and on each mode switch**.

Writing the offset requires the process to be **elevated** (the app already runs as admin for the EC/WMI
controls).

## The exposed range is the driver's own (2026-10-01)

The slider's bounds come from the range the driver reports for the P0 frequency delta, **unclipped**. There used to
be an app-side safety cap (`CoreCap = 300` / `MemCap = 1500`) that clipped the exposed range even when the driver
offered more; it is **gone** — the owner asked to be able to reach the driver's bound.

Measured on this machine (AN18-61, RTX 5070 Ti Laptop, driver 615.x), read live through NvAPI on P0:

```
core range:   -1000 .. +1000 MHz
memory range: -1000 .. +3000 MHz
```

So a core offset of **+1000 MHz** and a memory offset of **+3000 MHz** are now reachable from the slider; a slider
drag can no longer be stopped at ±300 by the app.

**The one remaining guard is the read-back, not a cap.** `SetPstates20` is followed by a `GetPstates20` that must
report back exactly what was asked (`Held`), so a value the vBIOS or driver silently clamps surfaces as a failure
rather than as a number the app cannot vouch for. On a powered-off / D3-cold dGPU the range reads come back `0..0`;
that degenerate answer — and only that — is replaced by a guessed ±300 / ±1500 envelope so the slider is not dead.

The memory value is still the **RAW memory-clock offset**, matching **G-Helper's convention** (the number as
written, with no GDDR6/GDDR7 doubling). An **Afterburner "effective" figure is ~2× this** — so the same physical
offset looks half as large here. That difference is a units convention, not a different limit.

## Availability

Windows-only, and effectively NVIDIA-only: `nvapi64.dll` ships with the NVIDIA driver and is simply **absent on
AMD/Intel-only laptops**, where `TryCreate` returns null and the UI hides the GPU section.

## The GPU power level beside the clock offsets (2026-09-29)

The GPU section also hosts a **power-level selector** — which of the EC's fixed TGP rows the dGPU runs at —
remembered **per performance mode** beside `Core`/`Mem` in `GpuOcPreset.Power`. It is a separate port
(`IGpuPowerEnvelope`) and a separate axis from the clock offsets: the offsets are NvAPI `SetPstates20` on the
P0 frequency delta, while the envelope is the EC's "system usage mode" over HID. Both are volatile and both are
re-applied at startup, on resume and on each mode switch.

**The only selectable levels are the EC's four distinct rows** (Turbo 115 W … Quiet 75 W) plus "Follow profile" —
there is **no arbitrary wattage**. The EC's fifth usage row (Eco) enforces the same GPU envelope as Quiet and is
deliberately not offered. The mechanism, the measurements and why a free-form number is out of scope (it would
need a kernel driver) are in `docs/power-an18-61.md` §"Selecting the envelope ON ITS OWN". The section hides the
selector on a machine without the EC channel, so an NVIDIA dGPU without Acer's EC HID interface shows the clock
offsets alone.

