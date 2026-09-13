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

## Safety caps, and the raw-vs-effective memory figure

```
CoreCap = 300 MHz     MemCap = 1500 MHz
```

The caps are applied **even if the driver reports more headroom**, because a single slider drag to an extreme
offset can **hang or corrupt the GPU** (NVIDIA **XID 62** — a GPU-side error class that has been observed from
over-aggressive memory offsets).

The memory value is the **RAW memory-clock offset**, matching **G-Helper's convention** (it writes the number
as-is, with no GDDR6 doubling). An **Afterburner "effective" figure is ~2× this** — so the same physical offset
looks half as large here. That difference is a units convention, not a different limit.

## Availability

Windows-only, and effectively NVIDIA-only: `nvapi64.dll` ships with the NVIDIA driver and is simply **absent on
AMD/Intel-only laptops**, where `TryCreate` returns null and the UI hides the GPU section.
