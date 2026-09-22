# PawnIO — the ring-0 gateway, and why it is the one this app uses

The CPU-undervolt feature (`docs/curve-optimizer-strix-point.md`) needs to read and write three SMU mailbox
registers. That is kernel-side hardware access, so it needs a driver. This document is why the driver is
**PawnIO** (pawnio.eu), why the two obvious alternatives were rejected, what the wire protocol with the driver
is, and the licence rules the app must not break.

## Why not WinRing0 or inpoutx64

Both are what this class of tooling has historically shipped, and both fail here for a concrete reason:

- **WinRing0** — which **RyzenAdj ships** — is named in Microsoft's **vulnerable-driver blocklist** and carries
  an **active Defender signature**. It therefore loads only while the blocklist and HVCI happen to be off: a
  posture one Windows policy push can revoke. An undervolt feature that dies at the next servicing update is
  worse than one that was never offered.
- **inpoutx64** cannot do the job **at all**:
  - its own author documents `DlPortWritePortUlong` as **not working as expected** — and a **32-bit write to the
    PCI `CONFIG_ADDRESS` port at `0xCF8` is exactly what an SMN transaction needs**. PCI does not treat a
    byte/word write there as an address-latch update, so the write **cannot be split** into smaller pieces.
  - `MapPhysToLin` / `GetPhysLong` are documented as limited to physical addresses **under 2 GB**, which excludes
    any plausible MMCFG base.

PawnIO is a kernel driver that executes small **verified bytecode modules**. A module declares which hardware it
may touch, so the driver grants **narrow** access instead of the blanket "read/write any port, any MMIO" hole the
other two are.

## Protocol: one device, two IOCTLs

```
device      \\?\GLOBALROOT\Device\PawnIO
LOAD_BINARY 0xA1B22084   CTL_CODE(41394, 0x821, METHOD_BUFFERED, FILE_ANY_ACCESS)
EXECUTE_FN  0xA1B22104   CTL_CODE(41394, 0x841, METHOD_BUFFERED, FILE_ANY_ACCESS)
```

Both are **plain buffered** IOCTLs. The control codes are spelled as literals because the device type is
PawnIO's own (`0xA1B2`), not a Windows one: `(0xA1B2 << 16) | (fn << 2)`. Both codes were **verified
byte-for-byte against an installed PawnIO 2.2.0.0** — they appear in `PawnIOLib.dll` and adjacently in
`PawnIO.sys`'s dispatch switch — so these are the driver's real numbers, not values copied out of a write-up.

- `LOAD_BINARY` takes the module blob. **One loaded module per open handle**, so a second module needs a second
  handle.
- `EXECUTE_FN` calls a function the module exports, **by name**.

The execute buffer layout is a **32-byte ASCII function name (zero padded)** followed by **packed little-endian
64-bit arguments**; the output is packed 64-bit values. Sizes are validated **strictly** by the module — PawnIO's
`DEFINE_IOCTL_SIZED` returns `STATUS_INVALID_PARAMETER` on any mismatch — so the caller must pass **exactly** the
argument and result counts the function declares.

Everything is blittable (plain `byte[]` written with `BitConverter`-free span helpers), so there is no marshalling
stub and this stays Native-AOT-safe.

The driver and its module blobs **ship separately from the app**: PawnIO is installed by its own signed
installer, and the module is a signed binary only its author can produce. (The LampArray driver used to be the
other example of that arrangement — see [lamparray.md](lamparray.md) — until that whole feature was removed on
2026-09-22.) So this class never installs anything — it **probes**, and every consumer treats "absent"
as "feature unavailable" rather than as an error.

A PawnIO handle carries **one loaded module** and the modules used here drive a **stateful hardware mailbox**, so
overlapping executes on one handle are never valid — requests on a handle are serialised.

`PawnIo.Available` is a cheap open-and-close probe that never throws, so it can gate a feature's visibility. It
is **false when the caller is not elevated** — the device requires elevation.

## The module: `RyzenSMU.bin`

The module used is `RyzenSMU.bin`, and the two functions driven are:

```
ioctl_read_smu_register    1 argument in,  1 value out
ioctl_write_smu_register   2 arguments in, nothing out
```

**The module's generic send-SMU-command entry point is deliberately NOT used.** Its internal table resolves
**Strix Point to the RSMU address triple**, so a `0x4C` (the all-core CPU opcode) sent that way would go to the
**wrong mailbox**. Hand-rolling the transaction over the raw register accessors is what G-Helper does, and it is
legal because the module's own range check admits the whole `0x3B10000`–`0x3B10FFF` window these three registers
live in.

### Why the blob is embedded

- PawnIO modules are **LGPL-2.1-or-later**.
- The author's integration guide says to take a release blob and *"include its contents in your software"*.
- The project states outright that **module APIs are NOT stable across releases** — so the version whose call
  shapes the code is written against has to **travel with it**.

The PawnIO **installer ships no modules at all**, so there is no shared system location to read one from. An
explicit override file is honoured for advanced use: `%APPDATA%\AcerHelper\RyzenSMU.bin`, then the file next to
the executable. The override exists because module APIs are explicitly unstable — if a future blob changes a call
shape, someone can pin their own without waiting for a build. It is deliberately an **explicit path** in the
app's own config folder rather than a scan of shared locations, so a stray file elsewhere can never silently
change which bytes get loaded into the kernel. A dev build without the fetched blob simply reports the feature as
unavailable.

## Installing it: the redistributable, and the three rules

The app ships `PawnIO_setup.exe` because the third-party licence allows exactly that:

- the signed edition is proprietary freeware, but its own binary carries an express grant —
  *"This installer can be redistributed unmodified."*
- the author's module documentation states the same: *"Official and unrestricted binary editions: Proprietary,
  however redistribution of installer is allowed."*

That permission covers **exactly one thing**: shipping `PawnIO_setup.exe` **byte-for-byte**. Unpacking
`PawnIO.sys` or `PawnIOLib.dll` out of it and laying them down as our own files is **NOT covered** — so the
payload is **invoked, never opened**. The author's stated preference is that apps merely point users at
pawnio.eu, so the offer names what it installs and where it comes from rather than burying it in a silent
download.

**Three rules the installer path will not break**, because PawnIO is a **shared** dependency — UXTU, ZenTimings,
FanControl and LibreHardwareMonitor use the same driver:

1. **Install only when it is absent.** (The installer refuses over an existing install anyway, and silently.)
2. **Never upgrade.** A newer driver swapped under another tool's feet is not ours to swap.
3. **Never uninstall** — including when AcerHelper itself is removed.

The invocation is `-install -silent`, the same one the author's own winget manifest uses for unattended
installs. `-unrestricted` must **never** be added: that installs the test-signed edition meant for module
development, which needs test-signing mode and a reboot.

Installation happens **only after the user says yes** — installing a kernel driver is the user's decision, it is
somebody else's software, and it may be shared with other tools on the machine.

### Detection: the ARP key, not the device handle

Presence is read from the **Add/Remove Programs registry key**, not from opening the device:

```
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO
```

The key name is the literal string `PawnIO` — the installer is not an MSI, so this is **not** a product GUID.
Both registry views are checked (64-bit first) because older installers wrote to the 32-bit view.
`DisplayVersion` gives the installed version (e.g. `2.2.0.0`); reading HKLM works unelevated and never throws.

Why not the device handle: opening `\\?\GLOBALROOT\Device\PawnIO` tells you the driver is **usable right now**
(it fails when not elevated, or when the node is stopped) — a different question from whether it is **installed**.
Answering that wrongly would fire the installer at a machine that already has PawnIO, where it fails with **no UI
at all**.

The installer binary is absent in a local dev build; CI fetches a pinned, checksum-verified copy into the publish
tree.
