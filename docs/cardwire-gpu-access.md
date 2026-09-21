# Using the discrete GPU from this app (cardwire)

A capability of the app itself, and the only one in it that is not about hardware this laptop's firmware owns:
**Acer Helper asks a third-party daemon — cardwire — to stop hiding the machine's discrete GPU from its own
process.** Off by default, consented to explicitly, asked for again on every start, and asked for nothing on a
machine where there is nothing to ask.

- the rule and the port: `Infrastructure/Vendors/Generic/CardwireGpuAccess.cs`
- the Linux half (the one call, the two probes): `Infrastructure/Vendors/Generic/CardwireGpuAccess.Linux.cs`
- the Windows half (never offers anything): `Infrastructure/Vendors/Generic/CardwireGpuAccess.Windows.cs`
- the graph and the order: `Infrastructure/Composition/LaptopService.Cardwire.cs`
- the words the user agrees to: `UI/CardwireGpuAccessConsent.cs`
- the row: `Infrastructure/Composition/OptionsAssembler.cs` (`Toggles`)
- the tests: `tests/AcerHelper.Tests/CardwireGpuAccessTests.cs`

---

## 1. What cardwire does

[cardwire](https://github.com/OpenGamingCollective/cardwire) (Fedora 44 package `cardwire-0.12.1`, Bazzite's
default GPU switcher) hides the discrete GPU from programs that have not been allowed to use it. The hiding is
**not** a driver unbind and not a permission on `/dev`: `cardwired` loads an eBPF LSM program that answers
`-ENOENT` for syscalls touching the gated devices — `inode_permission` and `inode_getattr` on the device nodes,
and `getdents64` on the sysfs directories that list them.

Measured on the AN18-61 (`10de:2f58` = the RTX 5070 Ti, `10de:2f80` = its HDMI audio function):

| where | hidden |
|---|---|
| `/proc/bus/pci/devices` | 44 entries, both `10de` functions **present** |
| `/sys/bus/pci/devices` | 42 entries, both `10de` functions **absent** |
| `lspci`, `nvidia-smi` | nothing |
| root | the same 42 — so the block is per-**client**, by pid, not by uid |

That asymmetry is the whole reason this feature can be built at all: the daemon hooks the *sysfs* inodes, so one
inventory (`/proc/bus/pci/devices`) still names a GPU that the other (`/sys`) will not show. The app's gate is
built on exactly that difference (§4).

The daemon owns `org.opengamingcollective.cardwire` on the **system** bus, object
`/org/opengamingcollective/cardwire`. Its shipped D-Bus policy (`/usr/share/dbus-1/system.d/
org.opengamingcollective.cardwire.conf`) grants send/receive to group `sudo` and group `wheel`, and there is **no
polkit action** — so an ordinary user in `wheel` can call it with no password prompt. (Verified: the call below
succeeds as `alexander`, uid 1000, group `wheel`, with no elevation.)

The policy interface, as the daemon itself describes it:

```
org.opengamingcollective.cardwire.SmartPolicy
  .GetAppPolicies        method -           a{s(sasasu)}   # the persistent per-app table
  .GetProcessStatus      method u           sau            # "check if the process is inside PID or FORCED map,
                                                            #  and return the map type with the gpu_id"
  .RequestProcessAccess  method usu         -              # pid, policy, gpu_id
  .SetAppPolicy          method si          -              # app_id, policy  (the persistent form)
  .NewAppAdded           signal (s(sasasu)) -
```

## 2. Why the runtime grant and not the persistent row

Two ways in, and only one of them is right for this app:

| | `RequestProcessAccess(pid, "Allow_dGPU", gpu_id)` | `SetAppPolicy(app_id, policy)` |
|---|---|---|
| what it changes | an already-running pid, in the daemon's in-kernel map, now | a row in cardwire's SQLite `app_policies` table |
| when it takes effect | immediately | every future run |
| keyed by | the pid | the **lowercase binary name** — a spoofable string |
| survives | until that process execs or exits | until someone edits the table |
| who else is affected | nobody | every binary with that name, for every user |

**The runtime grant is the one this app asks for.** The persistent row is system state changed on the user's
behalf, keyed by a name any process can take for itself by being renamed, and it would outlive the consent that
created it. The runtime grant is one process, one run, and nothing to clean up afterwards — and the owner's
standing rule is that permissions are consented to and minimal. The persistent form is deliberately not wrapped,
not exposed in the UI and not mentioned in the consent prompt.

## 3. The exact call

One method call on the system bus, made through `busctl` (`Busctl.Call`, the app's existing system-bus runner —
see `PowerProfiles.Linux.cs`; it prefixes `--system` itself):

```
busctl --system call \
  org.opengamingcollective.cardwire \
  /org/opengamingcollective/cardwire \
  org.opengamingcollective.cardwire.SmartPolicy \
  RequestProcessAccess usu <pid> Allow_dGPU 0
```

- **`usu`** is passed **explicitly**, and that is a measured requirement, not tidiness: `busctl` infers a
  signature from bare arguments only for the simplest shapes, and this call is refused with
  `Too many parameters for signature` without it (verified on the owner's machine: the inline form failed, the
  explicit one reaches the daemon).
- **`Allow_dGPU`** is one of four accepted policy words and is **case-sensitive**: `Default` (a documented
  no-op), `Allow_dGPU`, `Force_dGPU`, `Force_GPU`. Verified: a wrong word is answered
  `Call failed: invalid arg: NotAPolicy`; `Default` returns success and changes nothing.
- **`0`** is the gpu id, and 0 is the daemon's own "every GPU it gates" value — the hook's allow-map lookup keys
  on the pid alone and its per-GPU comparison treats 0 as a match. Passing an index would mean this app owning a
  numbering it does not own: on the AN18-61 the dGPU is cardwire's GPU 1 (the index its log lines use), but that
  is the daemon's bookkeeping, not a fact the app should hard-code.
- **`<pid>`** is the app's own pid. The grant is inserted *for* that pid, so a wrong one grants a stranger (or
  nothing).

The app's exact argument list is pinned by a test (`TheRequestIsThisExactCall`), spelled out rather than read
back from the constants, because a wrong word here is not a refusal — it is a different permission.

### Children inherit

The hook's allow-list test is

```
if CW_ALLOWED_PID.get(pid).is_some() || CW_ALLOWED_PID.get(ppid).is_some() { allow }
```

so a child process **whose real parent is allowed is allowed too** — which is why the app can spawn a probe or a
helper and have it see the GPU, and why the grant has to be understood as belonging to the process tree rather
than to a single syscall.

### What cannot be undone, and what does not survive

- **There is no revoke.** `SmartPolicy` has no method that takes a grant back; the daemon's four methods are the
  table in §1. The grant is in force until the process execs or exits, and nothing shorter. This is the one fact
  a user would otherwise reasonably assume a switch can undo, so the consent prompt states it in those words.
- **It does not survive a restart.** The grant lives in a kernel map keyed by pid, so a new run of the app has
  nothing and must ask again. That is why the remembered consent is an instruction to *ask*, not a permission
  being restored: `Settings.CardwireGpuAccess` is read at startup, and the request is made then — once.

## 4. The gate: when the app may offer, and when it must stay out of the way

`CardwireGpuAccessFacts` has four fields, and the offer requires all four in the right state:

| fact | how it is read | why it matters |
|---|---|---|
| `IsLinux` | `OperatingSystem.IsLinux()` | cardwire is a Linux-side daemon; on Windows the port is built but can never offer (and the rule, not a null, is what says so) |
| `CardwirePresent` | `busctl --system status org.opengamingcollective.cardwire`, exit 0 | the *service* must be on the bus, not merely installed: a crashed daemon is asked nothing |
| `DgpuPresent` | `/proc/bus/pci/devices`, a `10de` vendor field | the machine has an NVIDIA GPU at all — hidden or not (§1) |
| `DgpuVisible` | `/sys/bus/pci/devices/*/vendor` = `0x10de` | **the important one**: if the GPU is already visible there is nothing to ask for |

`DutyFor(facts, enabled)` is the single rule; `ShouldOffer`, `RowBelongs` and the request's own re-check are
readings of it, so the row, the startup apply and the call cannot come to disagree. The four shut cases:

- **already visible** → offer nothing, say nothing. This is the ordinary state of a cardwire in Hybrid mode, and
  a message about it would be noise on every start.
- **no daemon on the bus** → offer nothing; and if the user's remembered choice is on, *report* it once, because
  a permission they granted themselves has stopped being honoured.
- **no discrete GPU** → offer nothing (there is nothing hidden to reveal).
- **not Linux** → offer nothing, do nothing.

The `10de` rule is deliberately NVIDIA-only: on an x86 laptop there is no integrated NVIDIA GPU, so the vendor id
alone identifies the discrete one, while an AMD or Intel display device cannot be told from an integrated one by
its vendor. A machine with an AMD discrete GPU is therefore not offered the grant; the daemon's own inventory
(the `Gpu/N` objects) is where a broader rule would read it from.

**The row's existence and the offer are not the same question.** The offer needs `ShouldOffer`; the Options row
also stays while the user's own choice is on — otherwise a choice made while the GPU was hidden would become
unreachable (no switch to turn it off) the moment the machine stopped hiding it. The prompt and the call still
follow `ShouldOffer` alone.

## 5. Consent, and what is remembered

Turning the row on shows a confirm dialog first (`CardwireGpuAccessConsent` → `FlyoutCoordinator.
ConfirmCardwireGpuAccessAsync` → `ConfirmDialog`). It is its own prompt rather than a row in the hardware-access
consent (`UI/HardwareAccessConsent.cs`), because that prompt lists **files an installer places** and is held to
the installer's table by a drift guard; this action writes nothing anywhere. The words state the four facts that
make this grant different from an ordinary setting:

1. who is asked and for **which** process — this one, not the machine and not the user;
2. nothing is written to disk, no file permission changes;
3. the grant is per-process and **disappears when the app exits**, and it **cannot be revoked while the app
   runs** (cardwire offers no revoke) — so turning the switch off stops the next start, and quitting the app is
   what ends the grant in force now;
4. **every other application stays blocked**.

Order of operations on a flip, and the reason for it:

- **on** → ask the daemon first; remember the choice **only if it agreed**. A file that says "this app may use the
  discrete GPU" while the daemon refused would be a preference the app cannot keep. A refusal is reported through
  the row's own failure path, in the daemon's words, and the switch snaps back.
- **off** → forget the choice; **make no call** (there is no un-ask to make).
- **startup** → if the choice is on and there is something to ask for, ask; report a missing daemon; stay quiet
  when the GPU is already visible. The choice is **not** cleared by a refusal: a startup failure is environmental
  (the daemon is down, the interface moved), while the preference is the user's and still means what it meant.
- nothing here retries. The duty is consulted at startup and on a flip, never from the 3-second refresh pass, so
  a daemon that is down produces one message and no traffic.

## 6. What was verified on hardware, and what was not

### Verified

- The interface and its methods exist as described (`busctl --system introspect … SmartPolicy`) and the shipped
  D-Bus policy grants group `wheel` access with no polkit action.
- The call **without** the explicit `usu` signature fails with `Too many parameters for signature`; with it, the
  daemon parses the arguments.
- The daemon validates the pid: `RequestProcessAccess usu 999999 Allow_dGPU 0` →
  ``Call failed: process doesn't exist``.
- The daemon validates the policy word: `… usu <live pid> NotAPolicy 0` → ``Call failed: invalid arg: NotAPolicy``.
- `RequestProcessAccess … Default 0` → **exit 0** (the documented no-op), which is what proves the bus path, the
  group permission and the signature are right end to end.
- The app's own probe inputs, read on this machine: `busctl --system status` exit 0; `/proc/bus/pci/devices`
  names both `10de` functions; `/sys/bus/pci/devices` currently lists **44** entries including them. So on this
  machine, today, the gate is **shut** and the app correctly offers nothing and calls nothing.
- **The app itself, run against this machine** (a Linux build of the tree at `net10.0`, with a scratch
  `XDG_CONFIG_HOME` so the owner's settings, autostart entry and gate-stats are untouched, and a logging
  `busctl` wrapper first on `PATH` so every call the app makes is on the record). With the consent forced ON in
  that scratch `settings.json`, the app's **complete** busctl traffic over a 14-second run was:

  ```
  --system get-property net.hadess.PowerProfiles … Profiles     (the PPD port, at startup)
  --user   call org.freedesktop.DBus … ListNames               (the tint chain)
  --user   call org.kde.KWin … NightLight GetAll               (the tint chain)
  --system status org.opengamingcollective.cardwire            ← the gate, at UI build
  --system status org.opengamingcollective.cardwire            ← the gate, at the startup duty
  ```

  and **no `RequestProcessAccess`** — the gate held on real hardware, because the GPU is visible to the app
  here, so there was nothing to ask for. With the consent OFF (the default) the same run makes **one** call, the
  row's `status` probe, and the startup duty does not even probe. The app started and exited cleanly in both
  cases, with nothing on stdout/stderr.
- The probe does not disturb the machine's power state: reading the `vendor` attribute of **all 44** PCI
  devices (which is what the visibility half of the probe does) changed no device's
  `power/runtime_status`, and the discrete GPU stayed `suspended` across it — measured, because a config-space
  read on a suspended device is the obvious way this probe could have kept a dGPU awake on every UI build.
- Nothing was left changed: no cardwire config or database write (`GetAppPolicies` returned the same 30 rows
  before and after), no daemon restart, no leftover processes, and `GetProcessStatus` for every pid tried is
  still the empty answer it started as.

### NOT verified — and this is the honest limit of the above

- **The grant itself could not be reproduced in the machine's current state.** Every granting word
  (`Allow_dGPU`, `Force_dGPU`, `Force_GPU`) is answered
  ``Call failed: `bpf_map_delete_elem` failed``, and `GetProcessStatus` then still reports nothing for that pid.
  The read-only study that established this mechanism saw the opposite, and the difference is the daemon's
  **mode**: it is in Hybrid mode today (`Mode` = 1, both `Gpu/N.Block` = false, nothing hidden, `nvidia-smi -L`
  works for ungranted processes), while the study's measurements were taken with the dGPU hidden (Integrated).
  The granting path manipulates the eBPF maps (its first step is a delete), and in this state those operations
  fail. So the *visibility* consequence — "the card appears for the granted process" — is unproven here, and so
  is the ppid inheritance in production, which can only be observed while something is being blocked.
- **The app's own end-to-end path could not be run on hardware.** Two reasons, both environmental: the gate is
  shut here (correctly — the GPU is visible), so the app would make no call even with the consent forced on in
  `settings.json`; and a Linux publish of the tree at the time of writing does not compile because of another
  change in flight in `Infrastructure/Vendors/Generic/NvidiaGpu.Linux.cs`. The consequence of the first is that
  no cardwire journal line exists for this app's pid either way — the previous study saw
  `AcerHelper[169588] tried to access GPU 1 (blocked by cardwire)`, which requires the GPU to be hidden.
- Verified instead by test: the gate's four decisions, the exact argument list, the refusal path, the
  remembered-consent behaviour (including that a refusal is never written down as granted), the row's presence
  and its consent prompt, and the prompt's wording.

### Checking by hand

```bash
# is the daemon there, and what is it hiding?
busctl --system status org.opengamingcollective.cardwire; echo "rc=$?"
busctl --system introspect org.opengamingcollective.cardwire \
  /org/opengamingcollective/cardwire org.opengamingcollective.cardwire.SmartPolicy
grep -c 10de /proc/bus/pci/devices     # the GPU the machine has (survives the block)
ls /sys/bus/pci/devices | wc -l        # what this process can see

# the app's call, on a shell you own — it ends when that shell does
busctl --system call org.opengamingcollective.cardwire /org/opengamingcollective/cardwire \
  org.opengamingcollective.cardwire.SmartPolicy RequestProcessAccess usu $$ Allow_dGPU 0
busctl --system call org.opengamingcollective.cardwire /org/opengamingcollective/cardwire \
  org.opengamingcollective.cardwire.SmartPolicy GetProcessStatus u $$
nvidia-smi -L        # from the same shell, or from a child of it
```

`GetProcessStatus <pid>` is the daemon's own answer for a pid (`""` when it holds nothing), and
`GetAppPolicies` is the persistent table the app deliberately does not write to.
