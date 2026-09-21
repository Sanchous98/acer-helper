# The five Acer features mainline cannot reach — the mechanism, the registers, and what userspace can actually touch

Reconnaissance, **2026-09-21**, on the Nitro AN18-61 (`sys_vendor=Acer`, kernel `7.2.4-ogc3.1.fc44`,
`acer-wmi` loaded with `predator_v4=1 force_caps=7200`). Written to answer one question: can LCD overdrive,
the battery limiter/calibration, the keyboard-backlight timeout and USB charging be reproduced **through a
raw EC window** instead of a driver — and if so, at which addresses?

Everything below carries its source. Claims read out of somebody else's source say so; claims measured on
**this** machine say so, with the command. **No address in this document has been written on this machine.**

## 0. The premise, corrected by measurement: there is no raw EC window in `acer-wmi`

The starting assumption was that `acer-wmi`'s `ec_raw_mode` parameter exposes a raw EC read/write window in
debugfs. **It does not.** Measured on this machine:

```
$ cat /sys/module/acer_wmi/parameters/ec_raw_mode          ->  N
$ ls -l /sys/module/acer_wmi/parameters/ec_raw_mode        ->  -r--r--r--  (0444: load-time only)
$ objdump -dr /usr/lib/modules/$(uname -r)/.../acer-wmi.ko | grep debugfs_create_...
      init_module:  d65: R_X86_64_PLT32  debugfs_create_dir-0x4     <- exactly one
      init_module:  d89: R_X86_64_PLT32  debugfs_create_u32-0x4     <- exactly one
$ strings -a <same .ko> | grep -i debugfs
      debugfs_create_dir / debugfs_create_u32 / debugfs_remove      <- no debugfs_create_file
$ ls /sys/kernel/debug/acer-wmi/  ->  does not exist (the parent /sys/kernel/debug is 0700 root, and the
                                       dir is created only when WMID_GUID2 is present — it is not on this
                                       machine, so create_debugfs() never ran)
```

One `debugfs_create_dir("acer-wmi")` and one `debugfs_create_u32("devices", S_IRUGO, …)`: a **read-only u32**,
the WMID device bitmap. There is no `debugfs_create_file`, no custom fops, no address/value pair, and no
"write `0xADDR 0xVALUE`" syntax anywhere in the driver — upstream, in this kernel, or in the `.ko` installed
here. The `ec_read`/`ec_write` symbols in the module are the **ACPI EC accessors** (`include/linux/acpi.h` →
`drivers/acpi/ec.c`), called by the legacy DMI-quirk backlight path; mapping the relocations in this `.ko`
shows them in `AMW0_get_u32`, `WMID_get_u32`, `get_u32`, `read_brightness` (`ec_read`) and `AMW0_set_u32`
(`ec_write`), i.e. EC addresses `0xA`, `0x71`, `0x78`, `0x7B`, `0x83` — Acer's **wireless/mail-LED/backlight**
quirk registers, none of them one of our five features.

**What `ec_raw_mode` really does:** at load it calls `wmid3_set_function_mode()` with `function_num = 0x1`,
which is `wmi_evaluate_method(WMID_GUID3 "61EF69EA-865C-4BC3-A502-A0DEBA0CB531", 0, 0x1, …)`. It tells the EC
to stop touching the RF/power state of the communication devices itself, i.e. it picks **EC raw behaviour
instead of Acer's Launch Manager** for *rfkill*, and it makes `acer-wmi` poll rfkill itself
(`ec_raw_mode || !wmi_has_guid(ACERWMID_EVENT_GUID)` schedules `acer_rfkill_work`). The introducing commit
says so in its own message — *"disable the EC raw behavior for communication devices"*, the option exists
*"when anyone what reset it back"* — and none of the acks mention debugfs or EC registers.
Source: [`59ccf2f3d55c`](https://github.com/torvalds/linux/commit/59ccf2f3d55c06fd34613f1f78de0279436a7b35) (2011,
Lee Chun-Yi), [`acer-wmi.c`](https://raw.githubusercontent.com/torvalds/linux/master/drivers/platform/x86/acer-wmi.c).

**The generic raw-EC window is also absent here.** `ec_sys` (`drivers/acpi/ec_sys.c`,
`CONFIG_ACPI_EC_DEBUGFS`) is what would expose `/sys/kernel/debug/ec/ec0/io`, 256 bytes read/write, with the
`write_support` param whose own description reads *"Dangerous, reboot and removal of battery may be needed."*
Measured: **`# CONFIG_ACPI_EC_DEBUGFS is not set`** in this kernel's config, `modinfo ec_sys` fails, and
`/sys/kernel/debug/ec` does not exist. So the two windows one would reach for are both missing.

Also measured absent: `acpi_call` (`modinfo` fails, no `/proc/acpi/call`), and `CONFIG_ACPI_CUSTOM_METHOD`
(no AML-injection door). This is the same conclusion `docs/open-decisions.md` §7 reached for `acpi_call`,
which this document extends from "the WMI fallback" to "every raw-EC window".

**The one door that does exist on this machine: `/dev/port`.**

```
$ ls -l /dev/port        ->  crw-rw----+ 1 root kmem 1, 4     (CONFIG_DEVPORT is set)
$ cat /sys/kernel/security/lockdown  ->  [none] integrity confidentiality   (no lockdown)
$ mokutil --sb-state     ->  SecureBoot disabled
```

The ACPI EC is reached through I/O ports `0x62` (data) and `0x66` (command); upstream `/dev/port`
(`drivers/char/mem.c`) performs **no per-port allowlist check** on read/write — only `capable(CAP_SYS_RAWIO)`
and the lockdown check at open, and a `< 65536` bound in the loops. So raw EC transactions *are* drivable
from userspace here, as root. The cost is explicit: it **bypasses the ACPI EC driver's mutex and ACPI global
lock**, while the kernel is running its own EC transactions (battery/AC/thermal polling) on the same
controller. This route is **untested** — no byte has been written through it, and this document does not
recommend trying it before §8's read-only step has produced an address worth writing.

## 1. What the five features actually are: WMI methods, not EC registers

Every public project that implements these five on recent Acer hardware does it through **ACPI-WMI methods**.
The three classes the Windows app uses are real, and their GUIDs are **present on this machine** — measured
by listing `/sys/bus/wmi/devices/` and resolving each `driver` link:

| Windows class | GUID | Present on AN18-61 | Driver bound |
|---|---|---|---|
| `AcerGamingFunction` | `7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56` | yes (`…-11`) | none |
| `BatteryControl` | `79772EC5-04B1-4BFD-843C-61E7F77B6CC9` | yes (`…-7`) | none |
| `APGeAction` (= `WMID_GUID3`) | `61EF69EA-865C-4BC3-A502-A0DEBA0CB531` | yes (`…-5`) | none |
| calibration event | `676AA15E-6A47-4D9F-A2CC-1E6D18D14026` | yes (`…-3`) | none |

Measured: the only drivers registered on the WMI bus are `nvidia-wmi-ec-backlight` and `wmi-bmof`; the Acer
class GUIDs are enumerated and **unbound**. `acer-wmi` reaches them by GUID lookup rather than by binding a
WMI driver. `wmi-bmof` and `nvidia-wmi-ec-backlight` aside, none of the other 16 WMI devices on this machine
has a driver.

The consequence for the whole question: **Linux exposes no way for userspace to invoke a WMI method.** There
is no generic WMI sysfs call interface, `acpi_call` is absent, and `/sys/bus/wmi/devices/<guid>/` only ever
carries attributes a *driver* published. So wherever the mechanism is WMI, userspace reachability is **no**,
regardless of whether the GUID is present.

### The repository's own Windows encodings are confirmed by three independent projects

`Infrastructure/Vendors/Acer/AcerDevice.Windows.cs` and `AcerBattery.Windows.cs` already carry the payloads
the Windows path sends (measured working on this hardware). Reading the public projects against them:

| Our constant | Value | Public source agreeing |
|---|---|---|
| `LcdOn` / `LcdOff` | `0x1000000000010` / `0x10` | Linuwu-Sense `predator_lcd_override_store`; ASense `ASENSE_LCD_SET_ON`/`_OFF` |
| `BlQuery` / `BlGetOn` / `BlGetOff` / `BlSetOn` / `BlSetOff` | `0x88401` / `0x1E0000080000` / `0x80000` / `0x1E0000088402` / `0x88402` | Linuwu-Sense and ASense, constant for constant |
| `UsbOff` / `UsbAt10` / `UsbAt20` / `UsbAt30` | `663300` / `659204` / `1314564` / `1969924` | Linuwu-Sense and ASense, constant for constant |
| `BatteryWmi` masks | `HealthMode = 1`, `CalibrationMode = 2`, `uBatteryNo = 1` | `frederik-h/acer-wmi-battery` `HEALTH_MODE`/`CALIBRATION_MODE`/`ACER_BATTERY_INDEX` |
| `KbBrightnessByte = 2` | `gmOutput[2]` | the `GetGamingKBBacklight` ASL signature (method 21, `gmOutput[15]`) |

Sources: [`linuwu_sense.c`](https://raw.githubusercontent.com/0x7375646F/Linuwu-Sense/main/src/linuwu_sense.c),
[`asense_rgb.c`](https://github.com/fladirm/asense/blob/main/kernel/asense_rgb.c),
[`acer-wmi-battery.c`](https://raw.githubusercontent.com/frederik-h/acer-wmi-battery/main/acer-wmi-battery.c),
[the AcerGamingFunction ASL class](https://www.spinics.net/lists/platform-driver-x86/msg46467.html).
This is a cross-check of our Windows half, not new information about Linux.

### Method ids, once, for reference

`AcerGamingFunction` (GUID `7A4DDFE7…`, from the ACPI MOF dump): **1** `SetGamingProfile`,
**3** `GetGamingProfile`, 5 `GetGamingSysInfo`, 14/15 `Set/GetGamingFanBehavior`, 16/17 `Set/GetGamingFanSpeed`,
**20/21** `Set/GetGamingKBBacklight`, **22/23** `Set/GetGamingMiscSetting`.
`BatteryControl` (GUID `79772EC5…`): **19** battery information, **20** `GetBatteryHealthControlStatus`,
**21** `SetBatteryHealthControl`.
`APGeAction` (GUID `61EF69EA…`): **1** `SetFunction`, **2** `GetFunction`, both `[in] uint64 uiInput,
[out] uint64 uiOutput`.

## 2. Battery charge limit

**(a) Mechanism.** ACPI-WMI, class `BatteryControl`, method **21**. Not an EC write — in every project that
implements it for a gaming Acer, including the out-of-tree kernel module that exists solely for this feature
(`acer-wmi-battery`), which contains **zero `ec_read`/`ec_write` and zero register offsets**.

**(b) Encoding.** Input is a packed 8-byte struct, not a u64:
`{ u8 uBatteryNo; u8 uFunctionMask; u8 uFunctionStatus; u8 uReservedIn[5]; }` →
**`01 01 <0|1> 00 00 00 00 00`** (`uBatteryNo = 0x1`, `uFunctionMask = HEALTH_MODE = 1`, status `1` = limit on).
Read: method **20** with `{ uBatteryNo=0x1, uFunctionQuery=0x1, uReserved={0,0} }` (4 bytes), reply 8 bytes
`{ uFunctionList; uReturn[2]; uFunctionStatus[5] }`; usable = `uFunctionList & 1`, on = `uFunctionStatus[0] > 0`.
Not a read-modify-write; the limit is a single on/off ~80% cap, not a percentage.
**EC alternative, model-specific and not an equivalent:** Nitro **AN515-47** → `EC[0xDD]`, `0x00` off / `0x80`
on; Nitro **AN515-46/57/58** → `EC[0x03]`, a **composite** byte where the limit is base **+64 (0x40)** (so
`0x11` = fan control only, `0x51` = fan + limit). The value is not portable: AN515-44 uses `0x40`/`0x00`,
AN515-46 uses `0x51`/`0x11`.

**(c) Userspace reachability.** **WMI: no** — it needs a driver, and the driver is **not in mainline**:
`drivers/platform/x86/acer-wmi-battery.c` 404s on master, on the stable branches and on the pdx86
`for-next`/`fixes`/`testing` branches; it is an **8-revision submission still in review** (v8, 2026-09-13,
Jelle van der Waa, based on Frederik Harwath's out-of-tree driver; tested on an Aspire A315-510P). Locally:
`CONFIG_ACER_WMI_BATTERY` is not set and no `acer-wmi-battery.ko` exists anywhere in
`/usr/lib/modules/$(uname -r)/`. And even if it were built, its DMI whitelist does not contain any AN18 model
— it lists Aspire A315-510P/A315-24PT/A315-44P/A315-58G/A315-59, A715-42G; Nitro ANV15-51/AN515-57/AN515-58/
AN517-54; Predator PHN16-71; and several Swift models. **EC: only if the AN18-61 has a comparable byte at
`0x03`/`0xDD`, which nobody has published and which is unverified.**

**Upstream detail worth having.** The v8 patch adds `Documentation/wmi/devices/acer-wmi-battery.rst`, which
decodes the firmware MOF and settles the class name — `BatteryControl`, *"Class used to control smart
battery, Version 2.88"* — and confirms the method ids and structs above. It also reveals two methods **no
driver uses**: `WmiMethodId(22) GetBatteryFunctionData` and `WmiMethodId(23) SetBatteryFunctionData`, whose
output fields are `uBACStartTime[2]`, `uBACStopTime[2]`, `uBACStatus` ("BAC" = battery auto-calibration).
Calibration is deliberately **not** upstreamed — *"On my Acer Aspire A315-510P battery calibration did not
work as expected so for now this is left out."*
Sources: [v8 cover](https://lore.kernel.org/all/20260913143521.1190506-2-jelle@vdwaa.nl/),
[v3 diff](https://www.spinics.net/lists/platform-driver-x86/msg61530.html),
[why the param went away](https://github.com/frederik-h/acer-wmi-battery/issues/87).

**(d) Source and strength.** WMI encoding: measured — a PowerShell one-liner used to replace NitroSense on
real hardware (`$bat.SetBatteryHealthControl(1, 1, 1, [byte[]](0,0,0,0,0))`,
[stefnotch/acer-nitro-enable-smart-charging](https://github.com/stefnotch/acer-nitro-enable-smart-charging)),
and the same bytes in two drivers. EC values: user-measured EC dumps
([nbfc-linux#65](https://github.com/nbfc-linux/nbfc-linux/issues/65),
[#135](https://github.com/nbfc-linux/nbfc-linux/issues/135),
[openSUSE forum AN515-47](https://forums.opensuse.org/t/fan-control-in-acer-nitro-5-an515-47-nbfc-linux/178749)).
**Untested on AN18-61.**

**(e) Risk.** `0x03` is **shared with fan control** on the models where it works: writing the wrong composite
value costs fan control, and issue #135 exists precisely because a user had to set `0x03 = 81` to get fan
control *and* the limit together. Also a firmware-side failure mode with no error: on AN515-58 / BIOS V2.19
the WMI call is accepted and the sysfs node reads back `1`, while the battery still charges to 100%
([DAMX #184](https://github.com/PXDiv/Div-Acer-Manager-Max/issues/184)) — the call succeeding proves nothing.

## 3. Battery calibration

**(a) Mechanism.** The **same** WMI class, the **same** method **21** — only the mask differs. There is no
separate set-method id.

**(b) Encoding.** `01 02 <0|1> 00 00 00 00 00` (`uFunctionMask = CALIBRATION_MODE = 2`). Read via method 20;
on-state is `uFunctionStatus[1]`. Completion is signalled as **WMI event `0x0B`** on
`ACERWMID_EVENT_GUID "676AA15E-…"` (Linuwu-Sense handles `WMID_CALIBRATION_EVENT`; `acer-wmi-battery` notes
the event "is not yet handled"). **No EC address exists in any project** except the composite-byte claim for
AN515-57/58 (`EC[0x03]` base **+128 = 0x80**), from the same unverified user dump.

**(c) Userspace reachability.** **No** — same WMI wall as the charge limit, and unlike the charge limit there
is no plausible single-register EC form published anywhere.

**(d) Source and strength.** [Linuwu-Sense `preadtor_battery_calibration_store`](https://raw.githubusercontent.com/0x7375646F/Linuwu-Sense/main/src/linuwu_sense.c)
(RE'd, not measurement); [frederik-h/acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery)
(RE + a DMI list of tested models); ASense reports reading health/calibration from bytes 3/4 of the 8-byte
reply, matching a **PHN16-72 V1.18 readback** (measured, different model). Calibration is the one feature the
upstream submission dropped on purpose (§2, "Upstream detail"), and the only unexplored lead for it is the
unused firmware method pair **22/23** (`Get/SetBatteryFunctionData`, the `uBAC*` auto-calibration fields) —
present in the firmware MOF, called by no driver, and untested.

**(e) Risk.** The operation itself is the risk: Linuwu-Sense's README — *"It involves charging the battery to
100%, draining it to 0%, and recharging it back to 100%. **Do not unplug the laptop from AC power during
calibration.**"* It also interacts with the charge limit: calibration wants 100%, the limiter caps at 80%.
**Untested on AN18-61** — and this machine's `uFunctionList` is not known, so nobody has even confirmed the
AN18-61 offers calibration.

## 4. Keyboard-backlight timeout

**(a) Mechanism.** ACPI-WMI, class `APGeAction` (GUID `61EF69EA…`), method **2** to read and **1** to write,
with a packed little-endian u64 argument. **Not** the gaming class, despite the app grouping it with them.
The EC equivalent exists in the older Python projects as a plain byte.

**(b) Encoding.** Read: `GetFunction` with `0x88401`; reply `0x1E0000080000` = on, `0x80000` = off.
Write: `SetFunction` with `0x1E0000088402` (on) / `0x88402` (off) — `0x1E` = 30, i.e. the timeout value lives
in the high byte of the low 48 bits. ASense adds `ASENSE_TIMEOUT_UNINITIALIZED 0x0` with the note *"V1.18
returns zero until the setting has first been initialized."*
**EC:** `EC[0x06] = 0x1E` on / `0x00` off, a plain write, no read-modify-write. This is **the best-evidenced EC
claim in the whole survey**: the same address and the same two values in `Linux-PredatorSense`,
`PredatorNonSense` and `Packss/Linux-NitroSense`, and in the latter it is one of the entries that **both**
the AN515-46 and the AN515-44 table agree on (`KB_30_SEC_AUTO = "0x06"`, on `0x1E`, off `0x00`) — the entries
that differ between those two tables are the battery-limit values and the temperature addresses, not this
one.

**(c) Userspace reachability.** **WMI: no.** **EC: yes in principle** via `/dev/port` — and this is the one
feature where the address is consistent enough across models to be worth trying, *if* §8 shows the AN18-61's
AML touches the same byte. Still **unverified on this machine**.

**(d) Source and strength.** WMI: measured, twice independently — Linuwu-Sense and ASense carry the identical
constants, and they match our Windows path. EC: user-measured — an EC dump taken under Windows shows
`0x06 = 0x1E` where a cold-Linux dump shows `0x06 = 0x00`, in
[nbfc-linux#65](https://github.com/nbfc-linux/nbfc-linux/issues/65); the AN515-47 dump agrees
([openSUSE forum](https://forums.opensuse.org/t/fan-control-in-acer-nitro-5-an515-47-nbfc-linux/178749)).

**(e) Risk.** Low by comparison, but not zero: the value is a **duration**, so any value other than the ones
Acer's app writes is an unknown-timeout state the firmware may not have been tested in. The register sits
adjacent to the battery/USB bytes (`0x03`, `0x08`). No bricking report for this address in any source read.

## 5. USB charging while powered off

**(a) Mechanism.** ACPI-WMI, class `APGeAction`, methods 2/1. In the kernel modules it is WMI **only**. There
is an EC candidate in the Python projects, but unlike the keyboard timeout of §4 it has **no independent
EC-dump confirmation**: the nbfc configs and the published dumps do not list it, and no forum measurement of
this byte was found. Treat the EC form as project knowledge, not measurement.

**(b) Encoding.** Packed u64: selector `0x4` in bits 7..0, mode in bits 15..8 (`0x0F` enabled / `0x1F`
disabled), threshold in bits 23..16 (`0x0A`=10, `0x14`=20, `0x1E`=30; the disabled form encodes `0xA1` there).
So write = `0x4 | (mode << 8) | (threshold << 16)`: `0xA1F04` off, `0xA0F04` 10 %, `0x140F04` 20 %,
`0x1E0F04` 30 % — exactly our `UsbOff`/`UsbAt10`/`UsbAt20`/`UsbAt30`.
Read = `GetFunction` with `0x4`, and **the reply is not the value that was written**: Linuwu-Sense compares
against `0xA1F00`, `0xA0F00`, `0x140F00`, `0x1E0F00` — the low nibble (`0x04`, the command field) is **not
echoed back**. Worth noting for our own code: `UsbDecode()` compares the readback against the **write**
constants, so on a firmware that behaves the way Linuwu-Sense documents the readback would fall through to
`-1` and the row would read "Off". Our Windows path is measured working on the AN18-61, so this machine's
firmware evidently does answer with the write form — but the discrepancy is documented in a project that
tested on other models, and it is exactly the kind of thing that would look like a broken getter.

**EC candidate, in the Python projects only:** `EC[0x08] = 0x0F` on / `0x1F` off — a plain write, no
read-modify-write, and again identical in **both** of `Packss/Linux-NitroSense`'s model tables
(`POWEROFFUSBCHARGING = "0x08"`, on `0x0F`, off `0x1F`). Note it is **binary** here, while the WMI form is a
0/10/20/30 threshold — so the EC byte and the WMI call are not obviously the same underlying state, which is
one reason to trust the WMI form (measured, richer) over the EC form (project knowledge, no dump).

**(c) Userspace reachability.** **WMI: no.** **EC: a candidate exists** (`0x08`, §5 above) and would be
reachable via `/dev/port` — but it has no dump behind it, so it is the weakest of the three EC candidates,
weaker than the keyboard timeout's `0x06` and the battery's `0x03`.

**(d) Source and strength.** Measured encoding (two independent projects, identical constants; ASense's
bitfield masks `STATUS_MASK(7,0)` / `MODE_MASK(15,8)` / `THRESHOLD_MASK(23,16)` make the layout explicit):
[`linuwu_sense.c`](https://raw.githubusercontent.com/0x7375646F/Linuwu-Sense/main/src/linuwu_sense.c),
[`asense_rgb.c`](https://github.com/fladirm/asense/blob/main/kernel/asense_rgb.c). EC value: project
knowledge in `Linux-PredatorSense` and `Linux-NitroSense`, **inferred from PredatorSense, not measured** — no
EC dump in any source read shows `0x08` changing with this setting.

**(e) Risk.** None of the sources report any. The feature is a charging policy applied while the machine is
off, so a wrong write's failure mode would be silently draining or not charging a device — observable, not
destructive.

## 6. LCD overdrive

**(a) Mechanism.** ACPI-WMI, `AcerGamingFunction`, `SetGamingProfile` (**method 1**) / `GetGamingProfile`
(**method 3**) — Acer's own naming, and confusing: these are **not** the performance-profile methods (those
are `Set/GetGamingMiscSetting`, index `0x0B`). The single EC claim found is weak and contradicted; see (b).

**(b) Encoding.** Write a packed u64: **`0x10`** off, **`0x1000000000010`** on — bit 4 is present in both,
bit 48 is the state. Read: `GetGamingProfile` with `0x0`; the answer is a packed profile word where
**bit 24 (`0x1000000`) marks the LCD field valid and bit 48 (`0x1000000000000`) is its state** — ASense's
comment names a real reply, *"V1.18 returns a packed profile status (for example `0x0001ff000101ff00`)"*. On
bit 24 = 0 the field is not reported at all. Read-modify-write by the firmware, not by us.
**The one EC-adjacent lead, and it is weak:** the Python predecessors write `EC[0x21]` with bit 3 set/cleared
and call it LCD overdrive — but the *same file* in `Packss/Linux-NitroSense` uses `0x21` for **GPU fan mode**,
and `PredatorNonSense` uses `0x21` for fan control too. The address's meaning is model-specific; do not carry
it.

**(c) Userspace reachability.** **WMI: no.** **EC: no address worth trying** — the one candidate is
contradicted by two other projects on other models.

**(d) Source and strength.** Measured encoding: Linuwu-Sense and ASense carry identical constants, and they
match our Windows path; ASense cites a PHN16-72 V1.18 readback. The `0x1000000000000` bit our `GetLcd()` tests
is the same bit ASense calls the state — but ASense additionally requires bit 24 before trusting it, and our
check instead requires the status byte to be `0`. Both decode this machine correctly today; the ASense form is
the one that can tell "off" apart from "the firmware does not report this field".

**(e) Risk.** Not destructive, but this is the feature most often **absent**: Linuwu-Sense's `nitro_sense`
attribute group deliberately omits `lcd_override` and `boot_animation_sound`, issue #131 reports
`lcd_override = -1` (its "unknown" return) on an ANV15-52, and DAMX's FAQ tells users to switch to a different
quirk parameter when LCD override is unsupported. Its source comment is a warning about the whole family:
*"Think wisely before using any quirks validate your features."* Our own `GetLcd()` comment records the same
trap on the read side: the failure sentinel has the on-bit set, so an unchecked read reports overdrive "on"
whenever the call fails.

## 7. The keyboard backlight's brightness read

**(a) Mechanism.** ACPI-WMI, `AcerGamingFunction`, `GetGamingKBBacklight` (**method 21**), `[in] uint32 gmInput,
[out] uint8 gmReturn, [out] uint8 gmOutput[15]`. `gmInput = 1`, success is `gmReturn == 0`, brightness is
`gmOutput[2]`. **Not an EC feature and not an ENE feature** — the ENE controller is write-only, which
`docs/acer-linux.md` already records as measured (`HIDIOCGFEATURE` on report `0xA4` → `EINVAL` at every length
5..65; only `0xA1` and `0x00` read back, and they correlate with nothing).

**(b) Encoding.** Above. The 15-byte output array is why the read cannot be reconstructed from the ENE
descriptor's usage `0x43` / logical max `0x64`: those describe the HID field, not this WMI reply.

**(c) Userspace reachability.** **No** — WMI, and the only other channel is the write-only ENE device.

**(d) Source and strength.** The method id and signature come from the ACPI MOF dump (authoritative, not
hardware). The byte offset `gmOutput[2]` is **measured on this machine** by the Windows half of this app and
matched by no public project we found — neither Linuwu-Sense nor ASense implements this read.

**(e) Risk.** None — it is a read.

## 8. Where an EC address for the AN18-61 could still come from: its own DSDT

Nobody has published EC addresses for the AN18-61. But the gaming WMI methods are AML, and the AML reads and
writes **named EC fields**, which means the address is in this machine's DSDT and can be read out of it. The
kernel mailing list shows the shape exactly — for `SetGamingMiscSetting` (index `0x0B`):

```asl
\_SB.PC00.LPCB.EC0.OPMS = Local1          /* write the platform profile */
...
BHGK [One] = \_SB.PC00.LPCB.EC0.OPMS     /* read it back in GetGamingMiscSetting */
```

and `TKST` is read the same way for a neighbouring selector. The important part is the thread's conclusion
(Armin Wolf's explanation, as reported): the contributor's machine **uses a different EC address** for storing
the current platform profile than the one `acer-wmi` had been reading — i.e. **the EC field offsets are
model-specific, and the only authoritative source for the AN18-61's is the AN18-61's DSDT.**
Source: [platform-driver-x86, Dec 2024](https://www.spinics.net/lists/platform-driver-x86/msg49606.html).

Measured on this machine: `/sys/firmware/acpi/tables/DSDT` exists, `0400 root`, 40059 bytes. `acpidump`,
`acpixtract` and `iasl` are **not installed**. Nothing under `/sys/firmware/acpi/` is readable without root.

## 9. The patches that do not exist, and the one dormant patch that does carry EC knowledge

The task of looking for *unmerged upstream patches* that might carry register knowledge produced one clear
negative and one small positive.

**Negative, and it matters:** `backlight_timeout`, `usb_charging`, `lcd_override`, `battery_limiter` and
`battery_calibration` were **never proposed upstream in any form**. Those five node names exist only in the
out-of-tree Linuwu-Sense module (itself a patched `acer-wmi.c`) and its userspace consumers. A lore search for
`acer usb_charging` returns no relevant hits. So there is no in-flight kernel series to lift EC addresses
from — the only public implementations are Linuwu-Sense and ASense, both WMI-only, which is what §2–§7 above
records.

**Positive:** the only acer-wmi patch proposed in 2026 that touches the EC directly is
*"platform/x86: acer-wmi: support PH317-51 hwmon and kbd backlight"* (Lucas Gillard, 2026-09-18, 87 lines,
**thread dormant, no replies — proposed and not merged**). It is worth reading because it is the shape
upstream uses when there is *no* WMI method: it adds `ACER_CAP_KBD_BACKLIGHT BIT(13)` and drives the keyboard
backlight straight through the EC —

```c
/*
 * No WMI method drives the backlight on this model; the EC keeps the
 * state mirrored in both 0x30 and 0x31, so write both.
 */
ec_write(0x30, value);
ec_write(0x31, value);
/* read: ec_read(0x30, &result); return (result & 0x1) ? LED_ON : LED_OFF; */
```

i.e. **keyboard backlight = EC RAM byte `0x30`, mirrored in `0x31`, bit 0 = state; write both.** That is the
backlight *on/off*, not the 30-second timeout of §4, and it is a different model — so it is not a lead for the
AN18-61, but it is evidence for two things: the EC addresses in this family are plain EC RAM bytes, and a
mainline-style driver for one of these controls writes whole bytes without read-modify-write.
Source: [platform-driver-x86, 2026-09-18](https://ratatoskr.run/platform-driver-x86/2026/09/17605661).

One more EC offset seen in passing, recorded so it is not mistaken for one of our five:
Linuwu-Sense defines `ACER_SYSTEM_CONTROL_MODE_EC_OFFSET 0x45`, and `JafarAkhondali/…facer.c` reads the thermal
profile from `EC[0x54]` on pre-6.14 kernels. Neither is used by any of the five features.

## The next experiment

**Dump and disassemble this machine's DSDT and follow the five features' AML to EC field offsets — read-only,
as root, before any EC write is contemplated.**

1. Read `/sys/firmware/acpi/tables/DSDT` (and any SSDTs — the gaming methods may live in one) as root; no
   writes, no module loads.
2. Disassemble (`iasl -d`, or an equivalent AML parser). Locate the `AcerGamingFunction` / `WMID` /
   `APGeAction` method bodies — the same AMLs whose MOF the mailing list already published.
3. For each of the five, read which named EC field the AML touches and resolve it to an offset in the
   `\_SB.PC00.LPCB.EC0` OperationRegion. The field *names* are already known for the profile path (`OPMS`,
   `TKST`); the question is whether overdrive, USB charging, the keyboard timeout and the battery masks have
   their own named fields, and at which offsets.

**What would falsify it, and why that is worth knowing before touching `/dev/port`:** if the AML turns out to
reach these settings by calling *other AML methods* or through a non-`EC0` region (a shared-RAM window, an
`EC2`, or an SMI), then there is **no single-byte EC register to write** and the raw-EC route is dead for
those features — the honest answer would be that they need a driver. Equally falsifying: fields that resolve
into the range the ACPI EC driver is actively caching (the "cooked" battery/sensor bytes), where a write is
both ineffective and likely to race the kernel's own transactions. Only if a plausible offset comes out of
this read-only step does the `/dev/port` write — with all of §0's caveats, and never on a register another
feature shares — become worth discussing.

## What this document does not establish

- **No EC address has been measured on the AN18-61.** Every EC value in §2–§6 comes from a *different* model
  (AN515-44/46/47/57/58, PHN16-72) or from a project that never claimed measurement.
- **The `/dev/port` route is untested.** It is present and permitted (no lockdown, Secure Boot off) but it
  bypasses the ACPI EC driver's locking; no source found reports anyone using it for these features.
- **All five are WMI features in every modern implementation**, so the raw-EC route is a reconstruction of
  what the WMI methods *do to the EC*, not a documented interface. The WMI encodings in §2–§7 are solid — they
  are cross-confirmed — but they are unreachable from userspace without a driver or `acpi_call`.
- Negative results here are searches, not proofs: "no EC address found" for USB charging and LCD overdrive
  means no address found in the projects, dumps and threads listed below.

## Sources

Kernel and mailing list:
[`acer-wmi.c` (master)](https://raw.githubusercontent.com/torvalds/linux/master/drivers/platform/x86/acer-wmi.c) ·
[`drivers/acpi/ec.c`](https://raw.githubusercontent.com/torvalds/linux/master/drivers/acpi/ec.c) ·
[`drivers/acpi/ec_sys.c`](https://raw.githubusercontent.com/torvalds/linux/master/drivers/acpi/ec_sys.c) ·
[`drivers/char/mem.c`](https://raw.githubusercontent.com/torvalds/linux/master/drivers/char/mem.c) ·
[commit `59ccf2f3d55c` (EC raw mode / Launch Manager)](https://github.com/torvalds/linux/commit/59ccf2f3d55c06fd34613f1f78de0279436a7b35) ·
[AcerGamingFunction MOF, full method table](https://www.spinics.net/lists/platform-driver-x86/msg46467.html) ·
[the AML with `EC0.OPMS` / `TKST`](https://www.spinics.net/lists/platform-driver-x86/msg49606.html) ·
[RFC: helpers for misc gaming settings](https://www.spinics.net/lists/platform-driver-x86/msg49681.html)

Drivers and projects:
[`0x7375646F/Linuwu-Sense`](https://github.com/0x7375646F/Linuwu-Sense) ·
[`frederik-h/acer-wmi-battery`](https://github.com/frederik-h/acer-wmi-battery) ·
[`TenSeventy7/acer-wmi-ext` (calibration fork)](https://github.com/TenSeventy7/acer-wmi-ext) ·
[`fladirm/asense`](https://github.com/fladirm/asense) ·
[`JafarAkhondali/acer-predator-turbo-and-rgb-keyboard-linux-module`](https://github.com/JafarAkhondali/acer-predator-turbo-and-rgb-keyboard-linux-module) ·
[`snowyoneill/Linux-PredatorSense`](https://github.com/snowyoneill/Linux-PredatorSense) ·
[`kphanipavan/PredatorNonSense`](https://github.com/kphanipavan/PredatorNonSense) ·
[`Packss/Linux-NitroSense` per-model register table](https://github.com/Packss/Linux-NitroSense/blob/master/core/device_regs.py) ·
[`F0rth/acer_ec`](https://github.com/F0rth/acer_ec/blob/master/acer_ec.pl) ·
[nbfc EC probing tool](https://github.com/hirschmann/nbfc/wiki/EC-probing-tool)

Unmerged upstream work:
[`acer-wmi-battery` v8 (in review, 2026-09-13)](https://lore.kernel.org/all/20260913143521.1190506-2-jelle@vdwaa.nl/) ·
[v3 diff, with the MOF and Kconfig](https://www.spinics.net/lists/platform-driver-x86/msg61530.html) ·
[why the module param was dropped](https://github.com/frederik-h/acer-wmi-battery/issues/87) ·
[PH317-51 keyboard backlight via `EC[0x30]`/`0x31` — proposed, dormant](https://ratatoskr.run/platform-driver-x86/2026/09/17605661)

Measurements and failures:
[nbfc-linux#65 (EC dumps)](https://github.com/nbfc-linux/nbfc-linux/issues/65) ·
[nbfc-linux#135 (`0x03` composite)](https://github.com/nbfc-linux/nbfc-linux/issues/135) ·
[openSUSE forum, AN515-47 EC dump](https://forums.opensuse.org/t/fan-control-in-acer-nitro-5-an515-47-nbfc-linux/178749) ·
[stefnotch/acer-nitro-enable-smart-charging](https://github.com/stefnotch/acer-nitro-enable-smart-charging) ·
[DAMX #184 (WMI accepted, EC ignores)](https://github.com/PXDiv/Div-Acer-Manager-Max/issues/184) ·
[Linuwu-Sense #131](https://github.com/0x7375646F/Linuwu-Sense/issues/131) ·
[DAMX Compatibility.md](https://github.com/PXDiv/Div-Acer-Manager-Max/blob/main/Compatibility.md)

In this repository:
[`docs/acer-linux.md`](acer-linux.md) — the Linux backend reference, the EC HID reconnaissance that closes the
module-free route, and the gap table this document feeds ·
[`docs/power-an18-61.md`](power-an18-61.md) — the EC HID power envelope, wire format, measurement method ·
[`docs/open-decisions.md`](open-decisions.md) §7 — why `acpi_call` was rejected ·
`Infrastructure/Vendors/Acer/AcerDevice.Windows.cs`, `AcerBattery.Windows.cs` — the Windows encodings
cross-checked in §1
