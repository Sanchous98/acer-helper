# Dell firmware knobs — BIOS attributes over ACPI-WMI, and the Linux kernel ABI

The Dell backend extends the generic device with what Dell firmware exposes beyond the standard OS surface:
battery charge modes (Adaptive / Express charge / Primarily AC / Standard / Custom), USB PowerShare, Fn-lock,
keyboard-backlight timeout, and the full 4-mode thermal set (Optimized / Cool / Quiet / UltraPerformance).

Both operating systems drive the **same firmware knobs** through **different bindings**:

| OS | binding |
|---|---|
| Windows | Dell's **agentless** BIOS-attribute ACPI-WMI — `root\dcim\sysman\biosattributes`, stock firmware on 2018+ business models, **no Dell software needed** |
| Linux | the Dell kernel drivers' sysfs: `dell-laptop` / `dell-wmi-ddv` (power_supply extension), `dell-pc` (`platform_profile`), `dell-wmi-sysman` (firmware-attributes) |

Per-OS wiring lives in `DellDevice.{Linux,Windows}.cs`; unsupported surfaces simply stay generic.

> **Verification status.** This is the least-measured part of the project. The Windows path is exercised only
> through the shared `WmiSession` COM layer that *was* proven on Acer hardware, and has **not yet been verified on
> Dell-Windows hardware**. The Linux path is written against the kernel's documented ABI, not against a Dell
> machine. Treat both as reviewed-but-unproven.

## Windows: `root\dcim\sysman\biosattributes`

The classes are **published by Dell firmware itself** on 2018+ business models (Latitude / Precision / XPS /
OptiPlex) — present on stock Windows, no Dell Command software required.

**Do not confuse two namespaces:**

- `root\dcim\sysman\biosattributes` — the agentless interface used here; present on stock firmware.
- `root\dcim\sysman` (`DCIM_*` classes) — only exists **after** installing *Dell Command | Monitor*.

Reads are queries against the `*Attribute` classes — **one instance per BIOS setting**. Writes go through the
`BIOSAttributeInterface.SetAttribute` method, whose Status output is:

| Status | meaning |
|---|---|
| 0 | success |
| 1 | Failed |
| 2 | Invalid Parameter |
| 3 | Access Denied — e.g. a BIOS admin password is set, which this transport **does not supply** |

There is also a `root\dcim\sysman\wmisecurity` namespace, used for the password-gated path.

The class is a **thin accessor** — the lighting analogue of `WmiInvoker`: the attribute **names and values** live
in `DellDevice.Windows.cs`, not here. All the COM/AOT rules it depends on are in `docs/wmi-interop.md`
(in particular: `CIM_STRING` parameters go out as `VT_BSTR`).

## Linux: `/sys/class/firmware-attributes/<device>/`

A **vendor-neutral** ABI — the same one serves Dell (`dell-wmi-sysman`), Lenovo (`think-lmi`) and HP
(`hp-bioscfg`); the vendor device supplies the device name and the attribute names it understands. BIOS settings
appear as sysfs attributes with `current_value` / `possible_values` / `type` files, plus an `authentication/`
sub-tree.

**Access model — this is the part that shapes the UI:**

- Attribute **metadata is world-readable**.
- `current_value` is typically **root-only for BOTH read and write**. So a feature is **only offered when its
  current value is readable**.
- Writes are additionally gated by the firmware: when a **BIOS admin password** is configured, the kernel
  **rejects attribute writes** unless the password is supplied first — which this app does not do. So those
  controls must not be offered at all, rather than offered and then failing.
