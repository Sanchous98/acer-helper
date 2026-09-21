# The CPU undervolt on Linux — the Curve Optimizer over the `smn` node

The Linux port of the AMD Curve Optimizer axis (the `ICurveOptimizer` port, `Domain/Ports.cs`). It is the same
feature the Windows backend drives through PawnIO — three rails on one package, SMU-resident, volatile, re-applied
per performance mode — and it presents exactly the same domains, so `UI/ViewModels/CoViewModel.cs`,
`Infrastructure/Composition/LaptopService.Tuning.cs` and the settings graph needed **no change at all**: they are
keyed off `device.CurveOptimizer`, and the drawer looks the same on both OSes because the port declares the same
three rails. The mechanism, the measurements and the dead ends are in
[curve-optimizer-strix-point.md](curve-optimizer-strix-point.md); this file is the Linux-specific half — the
transport, the gate, the privilege path, and what has and has not been validated on this machine.

| rail | domain key | mailbox | opcodes | write | read back |
|---|---|---|---|---|---|
| Zen 5 (CCD 0) | `ccd:0` | MP1 | `0x4B` per slot | 8 messages | — none |
| Zen 5c (CCD 1) | `ccd:1` | MP1 | `0x4B` per slot | 8 messages | — none |
| iGPU (Radeon 880M) | `gfx` | RSMU | `0x1F` / `0x20` | 1 message | confirmed by `0x20` |

## Where the code is

| file | what it owns |
|---|---|
| `Infrastructure/Vendors/Generic/CurveOptimizerPolicy.cs` | everything above the transport: the mailboxes, the opcodes, the three encodings, the slot topology, the domains, the response-code mapping, the availability gate's decision, and the transaction as a sequence over two injected delegates |
| `Infrastructure/Vendors/Generic/RyzenCurveOptimizer.Linux.cs` | I/O only: the node paths, the driver's identity reads, the write-permission probe, the byte-level `smn` payloads and `TryCreate` |
| `Infrastructure/Vendors/Generic/RyzenCurveOptimizer.Windows.cs` | the same policy over PawnIO: the module blob, the register ioctls, the PCI access mutex |
| `Infrastructure/Vendors/Generic/GenericDevice.Linux.cs` | the one wiring line, in the shape the Windows `InitPlatform` uses |
| `tests/AcerHelper.Tests/CurveOptimizerPolicyTests.cs` | the sequence's order, the encodings, the refusals, the read-back asymmetry, the gate, the interlock |

The split is not cosmetic: the test project targets `net10.0-windows`, and `AcerHelper.csproj`'s `<Compile Remove>`
keeps `**/*.Linux.cs` out of that TFM, so **anything left in the Linux file cannot be tested**. Both OS files
therefore hold I/O and nothing else, and both hand their transport to the same policy as two delegates
(`Func<uint,uint?> read`, `Func<uint,uint,bool> write`).

## Why the transport is the `smn` node and not the driver's command nodes

The `ryzen_smu` driver exposes command nodes that speak MP1 and RSMU by name (`mp1_smu_cmd`, `rsmu_cmd`) and would
be the obvious thing to use. They cannot tell the truth about a refusal, and that was measured on this machine:
sending the graphics box's *wrong* opcode (`PSMU 0xB7`, the one inherited from the Rembrandt…Hawk Point lineage)
leaves `rsmu_cmd` reporting `0x01` — OK — while the mailbox's real answer, read over `smn`, is **`0xFD`**
("prerequisites not met"). The driver's poll loop has an unreachable timeout branch, so any non-zero answer reads as
success there. A port built on those nodes would report an undervolt the SMU refused, which breaks the port's own
contract: **`true` means the SMU accepted the message**.

`smn` is the raw SMN window the driver also offers, which is where the response register can be read for what it
is. It needs no driver of our own: on Linux that window is reachable from userspace through an already-loaded
kernel module — the same access RyzenAdj and ZenStates-Core make — so, unlike Windows, there is nothing to install
and no `IDriverSetup` offer here. `docs/pawnio.md` is the Windows half of that story.

## The `smn` node's ABI, byte for byte

Measured against the driver's own `smn_store`/`smn_show` on this machine (`ryzen_smu` 0.1.7, the `d298366`
snapshot):

| bytes written | what the driver does | what the node then reads back |
|---|---|---|
| exactly 4 | an SMN **read** of the address in the payload | the 32-bit value, little-endian, as raw bytes |
| exactly 8 | an SMN **write**: address in the first word, value in the second | `0x01` (the SMU call got through) or `0xF6` (the PCI access failed) |
| anything else | **nothing at all** — and it still reports the count as if it had worked | the previous result |

Two consequences the port is built around:

- **The payload length is the operation.** A 5-byte or 12-byte write is a silent no-op on a live mailbox, so the
  two payloads are built by named functions in the policy file (`SmnReadRequest`, `SmnWriteRequest`) and pinned by
  tests, rather than assembled at the I/O site.
- **A failed SMN read is indistinguishable from a fresh one.** The node answers with the driver's *last* result, and
  an access that fails leaves that previous value in place. The port therefore only reads where a stale value cannot
  be mistaken for an answer: the poll loop, and argument 0 — which is read only after the response register has said
  `0x01`, and whose failure is reported as a failure rather than as a number. The residual hazard is stated rather
  than papered over: because an SMN *write* leaves `0x01` in the node, a read that fails while the response register
  is being polled can hand back a cached `0x01`, i.e. report a refusal as accepted. Nothing on this side can detect
  that — the node exposes no status for the read itself — and it is one more reason the port's contract says a true
  result means *the message was accepted*, not that the curve moved.

The file offset is reset before every operation (`lseek(0)`): the node's read side answers from offset 0, and the
write that performs the operation leaves the offset at the end of the payload.

## The transaction

1. Wait for the mailbox to be idle — **a zero response means a message is still in flight** (someone else's, or a
   leftover) — with a 200 ms budget.
2. Write `0` to the response register.
3. Publish all six arguments at `arg base + 4n` (24 bytes), the caller's value in argument 0 and zero in the rest.
4. Write the message to the `cmd` register — the SMU latches on *this* write, which is why the order above matters.
5. Poll the response, treating `0xFC` ("busy with another command") as in flight rather than as a verdict, and
   reading argument 0 only once the answer is `0x01`.

Response codes: `0x00` still executing · `0x01` OK · `0xFC` busy, polled through · `0xFD` valid command whose
prerequisites are unmet, reported with the documented cause (the platform sits in an energy-saving power mode, so
"try a performance power mode") · `0xFE` this firmware has no such command, **latched** so a mode switch cannot
spam a dead mailbox · `0xFF` ran and failed. An unrecognised value is reported raw, because AMD defines these per
ASIC. `uint.MaxValue` is the separate sentinel for "never reached a response" (a failed register access, a busy
mailbox, no answer), so those reasons are never confused with a register's own value.

**No cross-process interlock is taken on Linux, deliberately.** Windows holds `Global\Access_PCI` across each
transaction because the PCI config index/data pair is shared with HWiNFO, CPU-Z, Ryzen Master and RyzenAdj. On
Linux the same window is reached through the driver, which serialises its own SMU traffic, and the transaction's
own idle wait covers a message already in flight. The policy takes an interlock only where an OS supplies one, and
its three outcomes (held / could not be created, proceed unlocked / someone else holds it, abandon) are tested.

## Availability: probed, never required

`RyzenCurveOptimizer.TryCreate()` returns **null** — so the port stays null and the Tuning drawer hides the
undervolt section — unless all three hold:

1. **the CPU is one whose mailbox layout is known**: CPUID vendor `AuthenticAMD`, family `0x1A`, model `0x20` or
   `0x24` (Strix Point). Krackan Point (`0x60`) and Strix Halo (`0x70`) are deliberately *not* accepted: they share
   the family case group in the reverse-engineering projects but the published results diverge.
2. **the driver agrees about the part**: `/sys/kernel/ryzen_smu_drv/codename` is **22** (Strix Point). On the
   AN18-61 it is — and `mp1_if_version` is **4**, the interface version the register addresses were measured on.
3. **the node the port drives is writable**: `/sys/kernel/ryzen_smu_drv/smn`, probed by opening it for write and
   writing nothing (the technique `Hwmon.CanWrite` established — a sysfs attribute does not act until a `write()`
   happens, so the probe has no side effect on the SMU).

The probe is cheap because composition runs on the UI thread, and it never throws: a missing node, an unreadable
one, a driver built for another part and a node this user cannot write are all "no port here". Requiring *write*
access is what keeps the section honest — without the installer's grant the sliders would appear and then refuse
every write. The gate probes `smn` alone although the install grants four nodes: the other three are the driver's
command nodes, which this port does not use, and a half-granted driver should not cost the undervolt.

The driver itself (`ryzen_smu`, a distribution package here) is **not** something the app installs or offers: there
is nothing to fetch and nothing to sign. Where it is missing, the section is simply absent.

## The privilege path

The four writable attributes (`smu_args`, `mp1_smu_cmd`, `rsmu_cmd`, `smn`) are root-owned, and the app must not
need root at runtime. **Three** mechanisms, the app's existing pkexec install rather than a new privilege path:

1. **A udev rule** in `packaging/60-acer-helper.rules`, matched on the PCI device the driver binds:

   ```udev
   ACTION=="bind", SUBSYSTEM=="pci", DRIVER=="ryzen_smu", \
     RUN+="/bin/sh -c 'i=0; while [ ! -e /sys/kernel/ryzen_smu_drv/smn ] && [ $$i -lt 50 ]; do sleep 0.1; i=$$((i+1)); done; for f in smu_args mp1_smu_cmd rsmu_cmd smn; do chgrp wheel /sys/kernel/ryzen_smu_drv/$$f 2>/dev/null && chmod g+w /sys/kernel/ryzen_smu_drv/$$f 2>/dev/null; done'"
   ```

   It hangs off the **bind** event because the driver's own sysfs tree is a *bare kobject*: not a device, no
   uevent, so udev's `MODE`/`GROUP` — which only ever reach device nodes — cannot touch those attribute files at
   all. The PCI device does emit events, so the rule matches that and reaches the files by absolute path in `RUN`.

   The wait is part of the rule rather than decoration: the bind event does not promise the driver's attribute
   files exist yet, and every `chgrp` in the loop fails into `2>/dev/null` — silently, which is how a rule that
   looks installed can grant nothing. The loop polls for `smn` (the node the app gates on) for up to ~5 s in 0.1 s
   steps and then grants whether or not it arrived.

   **This rule is NOT the boot grant**, and that is measured rather than reasoned: see (2).
2. **A systemd unit** — `packaging/acer-helper-smu-perms.service`, installed to
   `/etc/systemd/system/` — which is the deterministic mechanism, and the only one that is guaranteed to run after
   the module is up:

   ```ini
   After=systemd-modules-load.service
   ConditionPathExists=/sys/kernel/ryzen_smu_drv
   Type=oneshot
   RemainAfterExit=yes
   ExecStart=/bin/sh -c 'for f in smu_args mp1_smu_cmd rsmu_cmd smn; do chgrp wheel /sys/kernel/ryzen_smu_drv/$$f …; done'
   ```

   **Why the udev rule above cannot do this job, on this machine.** `ryzen_smu` is inserted from the
   **initramfs**: the journal records `systemd-modules-load[335]: Inserted module 'ryzen_smu'` during dracut's
   run, i.e. before the real `systemd-udevd` exists. The bind uevent is therefore emitted onto a bus nobody is
   listening to, and `systemd-udev-trigger.service`'s coldplug replays `add`/`change` events — never a bind. The
   four attributes are absent from `/etc/tmpfiles.d/acer-helper.conf` too (they do not exist at the tmpfiles
   pass). The first version of this grant was the udev rule alone, and this is exactly how it failed: at the next
   boot the nodes came up `-rw-r--r-- root root`, the port's writability gate therefore returned null, the
   **Tuning tab disappeared**, and every file in `/etc` was still byte-identical to the bundled one — so the app
   could not even offer to repair it. Measured, 2026-09-21.
3. **The installer's single pkexec script** (`Infrastructure/HardwareAccess.cs`), which is where the unit reaches
   `/etc`: after the files are copied it runs `systemctl daemon-reload && systemctl enable --now
   acer-helper-smu-perms.service`. That is ONE mechanism covering both cases at once — the unit is not yet
   active, so `--now` applies the grant immediately (the case the inline chmod used to cover: the user grants
   access while the driver is already bound, and no further bind event fires until the module is reloaded), and
   `enable` leaves it to run at every boot (the case only a unit can cover).

   The step is wrapped as a unit that always succeeds — a bare `|| true` inside that one `&&` chain would
   re-associate everything after it — and it sits after the tmpfiles pass and before the module reload, which
   stays last. The inline `chgrp`/`chmod` of the four nodes that used to be here is **gone**: a script that
   chmods its own machine cannot make a permission survive a boot, and two mechanisms would have been two places
   for the grant to be half-applied.

The app also asks whether the grant is **present**, not only whether the files match.
`HardwareAccess.RulesNeeded()` used to be a byte comparison alone, which is blind to a lost permission: identical
files, no offer, no way back. It now ends with `SmuMailboxAccess.GrantMissing` — a pure rule over
`SmuMailboxAccess.Nodes` (the one definition of the four names, which the packaging files are required by the
suite to agree with) — answering "some covered node exists here and this user cannot write it". **Absence is not
a lost grant**: on a machine with none of the four nodes (a Dell, an Acer without the driver) the answer is "no
offer", and the probe is never even asked about a file that is not there.

The user-facing consent prompt (`UI/HardwareAccessConsent.cs`) lists one row per installed file, built from the
installer's own table, so the grant now has **its own row**, in the installer's own words, for the unit that
carries it ("… re-applied at every boot by a systemd unit: the udev rule above can only grant them when the
driver's bind event reaches a running udev …"). This replaces the earlier shape, in which the SMU grant was an
extension of the udev entry's sentence because a fourth entry would have needed a bundled file of its own
(`Content` item in `AcerHelper.csproj`) — the second boot proved that a bundled file of its own was exactly what
the grant needed, and the table is one entry per bundled file, so the entry followed the file.

## What has been validated, and what has not

**Validated.** The transport, on this machine, by the feasibility study this port is built from: the driver's
command node reporting OK for a command the SMU refused while `smn` reported `0xFD`; the `smn` node's two payload
lengths and its `0x01`/`0xF6` verdict; and the inertness of margin-0 traffic (a stock write changes nothing, by
construction and by measurement). The port's own sequence, encodings, refusals, gate, per-rail clamps and read-back
asymmetry are pinned by `CurveOptimizerPolicyTests` — 59 tests, all offline.

**Not validated: no real offset has ever been written through this port on this machine.** The work that produced
it read the mailbox and never wrote one, by instruction — the owner deferred any real voltage change. So the write
path is *unexercised against hardware*: what is proved is that the messages go where they should, in the order the
SMU requires, and that every refusal is reported rather than swallowed. The first real offset is the owner's to
apply from the finished UI, and the standing warning applies: an undervolt is opt-in, the app defaults to stock,
and a too-aggressive offset on Zen 5 fails **hours later at idle** rather than under load — as machine-check errors
or silent corruption, not as an obvious crash.

**Not validated either, and it is the whole point of the privilege-path change: the grant surviving a reboot.**
The failure it was written for is measured (the nodes came back `root root` at the boot of 2026-09-21 with every
file byte-identical), but the FIX has not been through a boot — the environment this was written in cannot reboot,
and no unit was installed or started from it. What is proved offline: the rule and the unit name the same four
attributes, the unit's `After=`/`ConditionPathExists=`/`RemainAfterExit=`/`WantedBy=` are pinned by source-text
guards, the installer enables it instead of chmodding the nodes itself, and the offer now returns when the
writability probe says the grant is gone (every branch of that rule, including "no nodes here at all", is a unit
test). **What the owner should check after one reboot**, in this order:

1. `systemctl status acer-helper-smu-perms.service` — `active (exited)` if the driver was up at that point,
   `inactive (dead)` with `ConditionPathExists` unmet if it was not (the unit then did nothing, which is the
   designed outcome on a machine without the driver);
2. `ls -l /sys/kernel/ryzen_smu_drv/{smu_args,mp1_smu_cmd,rsmu_cmd,smn}` — all four `root wheel`,
   `-rw-rw-r--`;
3. that the Tuning tab is present without a single further pkexec prompt (it is shown when the port is non-null,
   and the port gates on `smn` being writable).

If (1) says active but (2) says root-only, the unit ran before the driver's attributes existed and `After=`
needs a stronger ordering than `systemd-modules-load.service` — that is the one failure mode this design has not
been able to test for, and the udev rule's bounded wait is what covers the manual-reload case meanwhile.

**What is inherently unverifiable from this side** is the stale-read hazard described above (a failed SMN read
returning a cached `0x01`), and the `0x4B` per-slot writes on a SKU whose populated slot set differs from this
die's: an unpopulated slot answers OK and changes nothing, so a wrong guess there is benign rather than dangerous,
and a core would simply get no slider.

## Recovery

The offset lives in **SMU state** and nothing is written to firmware, so a power cycle restores stock — and so does
selecting a performance mode with no saved offset, which the app applies actively (the re-apply path in
`LaptopService` is OS-agnostic and unchanged). The app holds no file descriptor on the SMU while idle: every
operation opens the node, does its one thing and closes it.
