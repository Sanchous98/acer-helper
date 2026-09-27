# Task: expose the power source (barrel vs USB-C PD) in the battery card

**Status:** ready to implement. Owner: (assign). Written 2026-09-27 from a live investigation on the
reference machine (Nitro AN18-61, Windows 11 26H2, BIOS V1.53).

**Do not touch anything else in the tree** — this is one feature. The reconnaissance that proved the
mechanism is in `docs/power-an18-61.md` §"Power source (AC / USB-C PD) on the same channel" and the
read-only tools are in `tools/echid-*.ps1`. Read that section first; it is the evidence base for this task.

---

## 1. What was proven (do not re-derive)

Acer's own Windows service (`AcerSysHardwareService.exe`) reads the power source over the **EC HID channel
this app already opens** (`AcerEcHidController`, VID `0x1025` / PID `0x174B`, usage page `0xFF05`, 65-byte
feature reports). NitroSense only talks to that service over TCP `127.0.0.1:46933`. The method is
`acer::KYD100::EcHID::GetACStatus`.

**The field, measured by differential read (Type-C vs barrel vs unplugged), cross-checked against
`BatteryStatus` with a charge-rate trace:**

```
EC HID: feature 0x0000, command 0x03, reply byte[7]
    0x00 = no external power   (on battery)
    0x01 = barrel / DC-in      (measured ~43-48 W into the battery)
    0x04 = USB-C / Power Delivery  (measured ~17-18 W)
```

Sequence that pinned it (user-driven): `USB-C → battery → AC → battery → USB-C → battery` produced
`0x04 → 0x00 → 0x01 → 0x00 → 0x04 → 0x00`.

Neighbouring bytes in the same `0x0000` status block also track the barrel (`cmd 0x02` byte[7] = `0x01`
only on the barrel; `cmd 0x06` byte[7] = `0x00` only on the barrel), but **`cmd 0x03` byte[7] is the full
source enum and the one to use.**

Wire format (from `AcerEcHidController.cs`): a read on this channel is **SEND-then-read** —
`HidD_SetFeature(report)` then `HidD_GetFeature(reply)`. A get with no preceding send returns a stale
buffer. Frame: `A0 00 A0 <feature:LE16> <cmd> <params…>`, zero-padded to 65; success marker is
`reply[2] == 0xE0`.

## 2. What to build

Show, in the battery card, **which source is powering the machine** — "Мережа (адаптер)" / "USB-C (PD)" /
"Від батареї" (exact strings — follow the localization convention, §4). On this platform the read is a
direct EC read; on machines without the EC channel, or on any read failure, the row must simply not appear
(never a guessed value).

### 2.1 Domain (`Domain/`)

- Add a vendor-agnostic enum in `Domain/Models.cs` beside `BatteryState`:

  ```csharp
  /// <summary>Where the machine is drawing power from right now.</summary>
  public enum PowerSource { Unknown, Battery, Barrel, UsbC }
  ```

  (Names: `Barrel` = the DC-in barrel/plaque charger; `UsbC` = USB Power Delivery over Type-C.)

- Add a property to `Battery` in `Domain/Battery.cs`, following the **exact shape of the other optional
  battery properties** (a null property = "this machine does not expose it"; that is how presence is
  declared here, see the class doc):

  ```csharp
  /// <summary>The live power source, when the firmware exposes one. Null on a machine whose OS/firmware
  /// reports only "AC vs battery" (the generic path) rather than the adapter TYPE.</summary>
  public Func<PowerSource>? PowerSource { get; internal set; }
  ```

  Keep `Telemetry` and `Read()` untouched. **Do not** fold this into `BatteryInfoSnapshot`: that record is
  the cheap 1 Hz OS-gauge snapshot (see §3), and this read is a different transport with a different cost.

### 2.2 Infrastructure — Acer EC reader

- Add a **read** to `AcerEcHidController` (the shared class) + both partials:
  - shared: `public PowerSource? ReadPowerSource()` (returns `null` when `!Available` or on any failure).
    Build the frame with the existing `FrameFor(0x0000, 0x03, 0x00)` and map `reply[7]` →
    `PowerSource`. Guard the reply: require `reply[2] == 0xE0` before trusting byte 7.
  - Windows partial: add `ReadFeature(byte[] send, byte[] reply)` using `HidStream.SetFeature` +
    `GetFeature` (same `_device`/`_stream` lazily-opened shape as `WriteFeature`; drop the stream on
    failure exactly like `WriteFeature` does).
  - Linux partial: same via hidraw `HIDIOCGFEATURE` (`_IOC(WRITE|READ,'H',0x07,len)`) after
    `HIDIOCSFEATURE`. **Untested on Linux hardware** — keep `Available=false` degradation; if the read
    fails, return null.
  - **Threading:** the read is SEND+GET on the HID-over-I2C bus and can block on a contended bus. It must
    NOT run on the UI thread and must NOT run at 1 Hz (see §3). Add it as a synchronous method the caller
    invokes off the UI thread; do not reuse the single-slot writer thread for reads.

- Wire it in `AcerDevice.Windows.cs` `InitVendor` (and the Linux equivalent where the channel exists):
  ```csharp
  if (_ec != null) Battery.PowerSource = () => _ec.ReadPowerSource() ?? PowerSource.Unknown;
  ```
  Only assign when the EC channel is present. On the generic backend leave the property null.

### 2.3 Where to call it (the cadence question — decide and document)

The battery card is written ONLY by the fast 1 Hz path (`AppController.RefreshBattery` →
`BatteryPollSchedule`), which is deliberately **OS-syscall-only and avoids the WMI/EC gate**
(`Infrastructure/BatteryPollSchedule.cs`). Reading EC HID every second would put a HID-over-I2C
transaction on that path — exactly what that file says not to do.

Recommended: read the power source on the **slower full pass** (`AppController.BackgroundPass` /
`PollSchedule`, ~3 s) and post it to the same `BatteryViewModel`, OR add it as a **separate, slower
schedule** (mirror `BatteryPollSchedule`'s single-flight pattern). Whatever you choose:
- Do not add an EC read to the 1 Hz tick.
- Single-flight it (one read in flight; a slow read skips the next).
- Document the cadence and the reason in the class doc, matching the existing tone.

### 2.4 UI (`UI/`)

- `BatteryViewModel`: add an observable row (e.g. `SourceLabel` / `ShowSource`) filled from the domain
  enum, mirroring how `PowerLabel`/`ShowPower` work. Only show the row when the device exposes
  `Battery.PowerSource` AND the read is not `Unknown`.
- `BatteryView.axaml`: add one row next to the existing Power/Health rows (same `Grid ColumnDefinitions="*,Auto"`
  + `Classes="muted"` label pattern; gate with `IsVisible="{Binding ShowSource}"`).
- Strings go in `Localization/Strings.resx` (neutral English) + `Strings.ru.resx` (Russian), keyed by a NEUTRAL
  key, not the English text — e.g. `bat.power_source`, `bat.source_ac`, `bat.source_usbc`,
  `bat.source_battery`. Add the key to BOTH files (English in one, Russian in the other) and resolve it with
  `Loc.T(...)`.

## 3. Hard constraints (the architecture tests will fail you otherwise)

- Dependency arrow is **Infrastructure → Application → Domain**; the UI binds to Application/Domain only.
  `ArchitectureMapTests` asserts it. The domain change above is safe; do not import Infrastructure types
  into Domain.
- Domain must stay vendor-agnostic: `PowerSource` is neutral; the `0x0000/0x03/0x07` encoding lives in
  Infrastructure only.
- No blocking HW read on the UI thread (the whole `AcerEcHidController` doc explains why).
- Never invent a value: a failed/absent read leaves the row hidden.

## 4. Tests (the repo is dense with pinned behaviour — match it)

- A `Battery` with `PowerSource = null` shows no source row.
- `BatteryViewModel` maps each `PowerSource` to its label and hides on `Unknown`.
- `AcerEcHidController` reply decode: `0xE0` + byte7 `0x00/0x01/0x04` → `Battery/Barrel/UsbC`; a
  non-`0xE0` marker or a short reply → `null`. Pin the frame bytes sent (`A0 00 A0 00 00 03 00 …`).
- If you add a schedule, pin its period vs the 1 Hz battery poll (see `BatteryPollScheduleTests` for the
  shape).

## 5. Out of scope / open

- **Linux:** mainline exposes `/sys/class/typec/*` (`power_role`, PD) and
  `/sys/class/power_supply/*` (`usb_type`). Prefer those over the hidraw read if they are present and
  give the same distinction; otherwise use the hidraw partial. Whichever you choose, note it in
  `docs/acer-linux.md`. **Do not claim it works without a measurement.**
- PD wattage / contract details (`GetPDAdaptorCap` proper) — the service has it, we only decoded the
  source TYPE. If a later task wants volts/amps, extend the same block; keep it separate.
- The `cmd 0x02` / `cmd 0x06` neighbours are NOT needed; leave them undocumented as facts unless you test
  them.

## 6. Evidence / tools left in the tree

- `tools/echid-probe.ps1` — feature-id sweep (read-only).
- `tools/echid-cmd-sweep.ps1` — command-id sweep (read-only).
- `tools/echid-snapshot.ps1` — fixed read-set snapshot.
- `tools/echid-watch.ps1` — prints only changed fields (how the `0x0000:03` byte was found).
- `tools/echid-powerlog.ps1` — power-source byte + `BatteryStatus` together (how it was labelled).
- `docs/power-an18-61.md` §"Power source (AC / USB-C PD) on the same channel".
