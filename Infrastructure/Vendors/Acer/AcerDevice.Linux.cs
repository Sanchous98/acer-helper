using System.IO;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Vendors.Acer;

// Linux: create the Acer transports and wire the generic feature holders (DelegatePorts.cs) to the per-feature
// encodings below. All the Acer-on-Linux encoding lives here (node names + trivial formats).
//
// TWO tiers, and BOTH are module-free by construction:
//   1. the two HID interfaces — RGB and the EC performance-envelope channel — over raw hidraw, module-free
//      (RGB reuses the cross-platform EneHidController brick: same ENE device and packets as Windows; on recent
//      models it is HID-over-I2C, not USB);
//   2. mainline acer-wmi, opened by the module parameter the installer writes (predator_v4=1 — the flag upstream
//      added for models outside its DMI table): the profile set off the acer-wmi handler's own platform-profile
//      class node, and fan telemetry, fan control and the CPU/GPU temperatures off its `acer` hwmon chip.
//
// The Linuwu-Sense tier that used to sit between them is DELETED (owner decision, 2026-09-20) rather than left
// unwired: this backend is module-free BY CONSTRUCTION, so a machine running mainline acer-wmi and no out-of-tree
// module gets the whole of what this backend knows how to offer. The five controls that only the module carried —
// LCD overdrive, the battery limiter/calibration, the keyboard-backlight timeout, USB charging and the
// keyboard-brightness read-back — have NO interface in mainline at all, and are declared absent with one honest
// status line instead of a module check (see InitVendor).
//
// WHAT IS NOT HERE ANY MORE, and cannot come back by accident: the module path constant, its sysfs nodes, their
// write-permission gate and the AC/battery walk that fed the deleted profile gate are all guarded by
// AcerLinuxWiringTests, which reads this tree as text (the only way this suite can reach a *.Linux.cs file).
public sealed partial class AcerDevice
{
    // The EC HID performance-envelope channel; null when this model doesn't expose it. See AcerEcHidController.
    private AcerEcHidController? _ec;

    // The `acer` hwmon chip's directory, once found. Its driver can register after this process starts (a module
    // reload, or a boot race with the app's own autostart), so a miss is re-probed on every read rather than
    // remembered as "no chip"; a hit is remembered.
    private string? _acerHwmon;

    // The generic temperature paths the chip's own nodes fall back to when they are absent. Probed once, on first
    // use: they belong to drivers (k10temp/coretemp, amdgpu/nvidia) that register at boot, and a fallback is not a
    // moving target. They are the paths the generic sensors port would have read, so a machine without the acer
    // chip reads exactly as it did before this override existed.
    private string? _cpuTempFallback, _gpuTempFallback;
    private bool _tempFallbacksProbed;

    // The two rows' own memories of the last real EC reading (see AcerTemperatureHold — the GPU channel answers 0
    // intermittently, and without this the row would flicker between the EC's value and the generic substitute).
    // One per channel on purpose: a shared hold would let the CPU's reading stand in for a GPU channel that has
    // never answered at all. The sensor read runs on the background loop, so these are touched from there only
    // after WireMainline has created them.
    private readonly AcerTemperatureHold _cpuTemp = new();
    private readonly AcerTemperatureHold _gpuTemp = new();

    // Whether the wiring actually produced the Acer fan port. The status line is chosen from it, and it cannot be
    // read off FanControl instead: with the acer chip ABSENT the inherited generic port may well be non-null (it
    // belongs to whatever other chip the generic path found), while with the chip PRESENT the port is nulled on
    // purpose when ours could not be built.
    private bool _fanPortBuilt;

    partial void InitVendor()
    {
        // The mainline wiring runs FIRST and is one call with no early return on the way to it. Everything inside
        // is independent of everything else — hidraw, the hwmon chip and platform-profile are three separate
        // kernel interfaces — and the shape this file once had (a module probe, then a `return` before the
        // profiles) is how the EC channel and the profile port got thrown away together on a machine that had the
        // mainline driver and no module.
        WireMainline();

        SetStatusLine();
    }

    /// <summary>
    /// The ONE line the user is shown, chosen from what the wiring actually produced. Three states, because the
    /// honest sentence differs — and one of them is ACTIONABLE, which is why it gets a string of its own:
    ///
    /// - the Acer fan port was built: the whole set this backend knows how to offer is live, so the line names
    ///   what came from the driver and what mainline has no interface for at all;
    /// - the chip is THERE but we could not drive its fans (the pwm nodes are not writable by this user, or this
    ///   kernel does not expose them): the fans are read-only and the missing piece is access the app can grant,
    ///   so the line says exactly that. Claiming a fan section here is the lie this replaces — and it is told in
    ///   the one case where the user needs to be told what to do;
    /// - no chip AT ALL (the installer's module parameters are not in place): nothing Acer-specific is live, and
    ///   the line must not promise fans either — the profile set falls back to the generic source and the
    ///   temperatures to whatever other chips the generic path found.
    ///
    /// It is a startup snapshot: a machine whose condition changes while the app runs (the installer granting
    /// access, a module reload) gets the new sentence on the next start, which is also when the grant takes
    /// effect — see the "restart to use the unlocked controls" notice. A localization key, not a sentence to
    /// translate at the call site (AppController looks it up).
    /// </summary>
    private void SetStatusLine()
    {
        if (_fanPortBuilt)
        {
            StatusMessage = "Linux: profiles, fans and temperatures come from the mainline acer-wmi driver (the installer's module parameters enable them) — it has no interface for LCD overdrive, the battery limiter/calibration, the keyboard-backlight timeout, USB charging or the keyboard-brightness read-back, so those stay unavailable.";
            return;
        }

        if (_acerHwmon == null)
        {
            StatusMessage = "Linux: the five Acer profiles, fan control and the Acer temperature chip need the installer's module parameters — until they are in place the profile set is the generic one; LCD overdrive, the battery limiter/calibration, the keyboard-backlight timeout, USB charging and the keyboard-brightness read-back have no mainline interface at all.";
            return;
        }

        StatusMessage = "The fans are read-only — fan control needs the hardware-access grant and the driver's PWM interface; grant access from the app and restart.";
    }

    private void WireMainline()
    {
        // RGB is the ENE HID controller — the very same device (and packets) as on Windows, reached via hidraw.
        // It is independent of everything below (an HID interface, not a platform driver), so it is wired first
        // and unconditionally. No brightness read-back here: the gaming-WMI getter that provides it on Windows
        // has no mainline equivalent (the ENE channel is write-only).
        WireRgb(readKeyboardBrightness: null);

        // The EC HID performance-envelope channel is likewise independent of the platform interfaces. UNTESTED on
        // Linux hardware: absent or unwritable just means Available = false and the profile path behaves exactly
        // as it did before.
        var ec = new AcerEcHidController();
        if (ec.Available) Own(_ec = ec); else ec.Dispose();

        // Nitro key -> toggle the window (evdev; needs the udev "uaccess" rule, otherwise stays hidden), and the
        // Turbo key -> HotkeyAction.TogglePerformance, decoded by THIS port from the vendor HID input report the
        // firmware puts on the Acer HID device (AcerHotkeys.TurboLoop, the two bytes shared with the Windows
        // decoder in AcerHotkeyReports). Turbo is NOT mainline acer-wmi's key, and this comment used to say it was
        // — that mainline owns it through cycle_gaming_thermal_profile and the action is unreachable here — which
        // is a claim a maintainer would delete working behaviour on. It was MEASURED false with the key pressed on
        // 2026-09-21: the module parameter is real and defaults to Y, but no evdev key arrives on the AT keyboard,
        // no acer_wmi line reaches the journal and the ACPI interrupt counters do not move, while every press
        // arrives as the vendor report 04 85 ff on hidraw. AcerHotkeys.Linux.cs records the same measurement
        // beside the decode it justifies.
        if (AcerHotkeys.TryCreate() is { } keys) Own(Hotkeys = keys);

        WireProfiles();
        WireSensors();

        // Fan control through the same hwmon chip, where the kernel exposes it AND this user may write it (the
        // nodes are root-only without the udev rule — see packaging/60-acer-helper.rules).
        //
        // AN ACER CHIP IS ONLY EVER DRIVEN BY AcerFanPort, so when the chip is there but the port could not be
        // built (a model that exposes one fan, a partial udev rule, a channel we may not write), the INHERITED
        // port is not left standing: FanControl goes explicitly null. The reason is that the generic
        // HwmonFanControl can bind to this very chip — on this kernel the duty node really is pwmN, which is
        // exactly what that class looks for — and its encoding means something else here. Its Max writes
        // pwm_enable = 1, which acer-wmi reads as CUSTOM, so the fan would be dropped into manual mode while the
        // UI said Max; and its Dispose writes enable 2 (auto) into a latch the user set. Both are silent, and
        // both are audible in the one place where being wrong is. Null is the honest answer: this machine offers
        // no fan control, and the generic port is only a fallback for a chip that is not ours.
        //
        // The chip-ABSENT case is deliberately left alone: the inherited port then belongs to whatever other
        // chip the generic path found, and its encoding is that chip's own.
        //
        // The sharper alternative — null it only when the inherited port actually bound to the acer chip — would
        // mean asking HwmonFanControl which chip it picked, i.e. an API on a *.Linux.cs class; and what it would
        // save is a second writable PWM channel on an Acer laptop (the dGPU's own fan, or an unrelated chip),
        // which the Acer UI would label as this machine's fans anyway.
        if (TryAcerFanPort() is { } fans) { FanControl = fans; _fanPortBuilt = true; Own(fans); }
        else if (AcerChip() != null) FanControl = null;
    }

    // ---- profiles: the acer-wmi handler's own class node, in the app's vocabulary, with the EC envelope ----
    private void WireProfiles()
    {
        // The vendor token makes this source look ONLY at the acer-wmi handler's own class node
        // (/sys/class/platform-profile/platform-profile-1 → .../acer-wmi/platform-profile/...) and never at the
        // legacy /sys/firmware/acpi/platform_profile alias. The alias is unusable here for two measured reasons: a
        // write to it fans out to EVERY registered handler (on this machine that is the AMD one as well, so one
        // profile click would move two unrelated profile sources), and it reads back as "custom" once a vendor
        // handler has written, so it cannot say which profile the machine is in. See SysfsPowerProfiles.
        var sysfs = new SysfsPowerProfiles("acer-wmi");

        // The full Acer BIOS profile set is adopted only when this process can actually switch it (root/udev);
        // otherwise the generic polkit-authorised PPD port the base ctor wired stays (working beats
        // richer-but-broken — the same rule DellDevice.Linux follows).
        //
        // THE FALLBACK KEEPS ITS OWN MIS-MAPPING, deliberately, and reading that as the bug above is the mistake
        // to avoid. The raw sysfs port classifies "performance" as ProfileKind.Performance and so sends the EC
        // envelope mode 1 (93 W) — which is WRONG on the Acer node, where "performance" is Turbo, and RIGHT on a
        // three-choice PPD/generic source, which has no Turbo to name at all: there, "performance" is the top of
        // that source's own set and mode 1 is the honest translation of it. The fix therefore belongs on the Acer
        // branch only (AcerMappedProfiles, which hands the envelope the byte's OWN kind), and the alternative —
        // forcing ProfileKind.Turbo here — would ask the EC for 108 W on behalf of a source that never claimed to
        // be able to request it.
        //
        // AcerMappedProfiles is what fixes the LIVE BUG this wiring used to have: the raw sysfs port classifies
        // the kernel token "performance" as ProfileKind.Performance, but the kernel's "performance" is Acer TURBO
        // (0x05, 108 W) and its "balanced-performance" is the app's Performance (0x04, 93 W). So the EC envelope
        // was told mode 1 (93 W) on a machine the user had put in Turbo, which wants mode 0 (108 W) — and the
        // decorator also puts the EC bytes back into the ids the presets, the tray and settings.json key off.
        var port = sysfs is { Available: true, Writable: true } ? new AcerMappedProfiles(sysfs) : PowerProfiles;

        // The EC usage mode has to move with whichever port we ended up with, or the envelope never changes on an
        // EC-HID model — including when the port is PPD's collapsed three-choice set. Enqueue-only and
        // best-effort (see EcSyncedProfiles); a null delegate means this model has no EC channel and the port
        // behaves exactly as it did before. The class the delegate is handed comes off the port itself
        // (ProfileTraits), so whichever branch above produced it is the branch that answers for it.
        var envelope = _ec is { } ecDev ? (Func<ProfileKind, bool>)ecDev.Apply : null;
        PowerProfiles = port is null ? null : new EcSyncedProfiles(port, envelope);

        // Boot sync, EC-only — same reasoning as AcerDevice.Windows.cs: the profile survives a reboot but the EC
        // usage mode behind it does not, so the machine can report Turbo while running the lowest power row. It
        // reads THE PORT WE ENDED UP WITH, not the raw sysfs one: reading sysfs here meant the same mis-mapping as
        // above, but at startup, where it silently pushed mode 1 (93 W) at a machine the firmware had left in
        // Turbo. Deliberately still NOT a profile switch — no palette flash, exactly as Windows does it — and it
        // cannot disagree with what the UI shows, because it asks the port the UI reads.
        if (envelope != null && port?.Current() is { } cur) envelope(ProfileTraits.Of(port, cur).Kind);
    }

    // ---- sensors: the acer chip, read exactly as Windows reads it ----
    private void WireSensors()
    {
        // The generic hwmon port the base ctor wired stays the fallback for a machine whose acer chip is absent
        // (or not there yet) — including the null case, where the snapshot is empty exactly as an absent generic
        // port's would be.
        var generic = Sensors;
        Sensors = new SensorsPort(() => ReadAcerSensors(generic));
    }

    private SensorSnapshot ReadAcerSensors(ISensors? generic)
    {
        if (AcerChip() is not { } chip) return generic?.Read() ?? new SensorSnapshot();

        var (cpuFallback, gpuFallback) = GenericTempPaths();
        return new SensorSnapshot
        {
            // temp1/temp2 are the kernel's predator_v4_sensor_id {1, 10} — which are exactly the Windows
            // sysinfo ids this backend reads on the other OS (0x01 CPU / 0x0A GPU), so the two OSes report the
            // same two sensors. WHICH READING WINS is the hold's rule, and it is not "the EC's value, faithfully":
            // this channel answers 0 intermittently (measured: six real readings then two zeros, 4 s apart; and
            // earlier a ≥60 s stretch of zeros), so a 0 holds the last real value instead of flickering to the
            // generic substitute. See AcerTemperatureHold.
            CpuTempC = _cpuTemp.Reading(
                Hwmon.Milli(Path.Combine(chip, "temp1_input")), Hwmon.Milli(cpuFallback)),
            GpuTempC = _gpuTemp.Reading(
                Hwmon.Milli(Path.Combine(chip, "temp2_input")), Hwmon.Milli(gpuFallback)),

            // BOTH fans are read UNCONDITIONALLY, and this is the sensors override's whole reason to exist. The
            // generic path (Hwmon.Fans) drops a channel that reads 0 rpm with no label — and the acer chip
            // publishes no fanN_label at all, so an idle laptop loses a fan from the list. What happens then is
            // not a cosmetic problem: the UI's index fallback re-labels the remaining fan as "Fan 1", i.e. the
            // GPU fan's 0 rpm is shown as the CPU's, and the machine reads as one fan that is spinning when the
            // other is standing still. That is a wrong READING, not a wrong label. Idle fans legitimately read 0
            // (measured on the AN18-61: the fans stop at idle), and 0 is a reading the UI can show.
            //
            // The labels come from the shared channel table and are the strings Windows shows, because that
            // pairing is a cross-OS display contract; the channel numbers are the kernel's (fan1_input / fan2_input).
            Fans = AcerFanChannels.All
                .Select(c => new FanReading(c.Label, Hwmon.ReadInt(Path.Combine(chip, $"fan{c.Number}_input")) ?? -1))
                .ToList(),
        };
    }

    private (string? Cpu, string? Gpu) GenericTempPaths()
    {
        if (!_tempFallbacksProbed)
        {
            var chips = Hwmon.Chips();
            (_cpuTempFallback, _gpuTempFallback) = (Hwmon.CpuTempPath(chips), Hwmon.GpuTempPath(chips));
            _tempFallbacksProbed = true;
        }
        return (_cpuTempFallback, _gpuTempFallback);
    }

    // ---- fans: mainline's pwmN_enable / pwmN on the acer chip, the same EC channel as Windows' WMI ----
    /// <summary>The fan port, or null when the kernel doesn't expose both channels to this process. BOTH channels
    /// or nothing: one controllable fan of two would offer a Max that moves half the machine while the capability
    /// this port reports (<c>HasGpuFan</c>) claimed both.</summary>
    private AcerFanPort? TryAcerFanPort()
    {
        if (AcerChip() is not { } chip) return null;

        // The channel numbers come from the shared table (AcerFanChannels), the same one the sensors read, so the
        // two cannot disagree about which number is which fan.
        var cpu = AcerFanChannels.Cpu;
        var gpu = AcerFanChannels.Gpu;
        var cpuEnable = Path.Combine(chip, $"pwm{cpu.Number}_enable");
        var gpuEnable = Path.Combine(chip, $"pwm{gpu.Number}_enable");
        var cpuDuty = DutyNode(chip, cpu.Number);
        var gpuDuty = DutyNode(chip, gpu.Number);
        if (cpuDuty == null || gpuDuty == null) return null;

        // Both nodes of both channels must be writable by this process: the files are root:root 0644 until the
        // udev rule grants the group access, and offering a slider whose every write is EACCES is worse than
        // hiding the section. Hwmon.CanWrite probes with an open-for-write and writes nothing (sysfs attributes
        // do not act until a write happens).
        if (!Hwmon.CanWrite(cpuEnable) || !Hwmon.CanWrite(gpuEnable)) return null;

        return new AcerFanPort(new AcerFanNodes(cpuEnable, cpuDuty, gpuEnable, gpuDuty), WriteNode);
    }

    /// <summary>The duty node of one channel, by NAME-DISCOVERY rather than a hard-coded name: the enable node's
    /// spelling is the kernel's own (pwmN_enable), but the duty node's is not stable across builds — this kernel
    /// exposes the write side as <c>pwmN</c>, other builds name the same attribute <c>pwmN_input</c>. So the
    /// directory is listed and the first of the two spellings that exists AND is writable is taken; a name the
    /// tree does not carry is simply not found, instead of being assumed and written into the void.</summary>
    private static string? DutyNode(string chipDir, int channel)
    {
        HashSet<string> names;
        try
        {
            names = Directory.EnumerateFiles(chipDir)
                             .Select(Path.GetFileName)
                             .OfType<string>()
                             .ToHashSet(StringComparer.Ordinal);
        }
        catch { return null; }   // the chip vanished (module reload) -> no port

        // pwmN_input first: where a build exposes both spellings, that is the documented hwmon one.
        foreach (var name in new[] { $"pwm{channel}_input", $"pwm{channel}" })
        {
            if (!names.Contains(name)) continue;
            var path = Path.Combine(chipDir, name);
            if (Hwmon.CanWrite(path)) return path;
        }
        return null;
    }

    /// <summary>One sysfs write, in DelegatePorts' (ok, error) shape so the port can surface the failure.</summary>
    private static (bool ok, string? error) WriteNode(string path, string value)
    {
        try { File.WriteAllText(path, value); return (true, null); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>The <c>acer</c> hwmon chip's directory, or null while the driver hasn't registered it.
    /// <see cref="AcerHwmonChip"/> holds the two conditions (the name AND the driver behind the device path) and
    /// why the BestFanSource heuristic is not used for a control. Re-probed while missing: the chip appears when
    /// acer-wmi loads, which can be after the app started (the module is reloaded by the installer, and an
    /// autostarting app can win the race with it).</summary>
    private string? AcerChip()
    {
        if (_acerHwmon is { } cached) return cached;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Hwmon.Root))
            {
                if (!AcerHwmonChip.Matches(Hwmon.ReadText(Path.Combine(dir, "name")),
                                           SysfsLink.RealPath(Path.Combine(dir, "device")))) continue;
                return _acerHwmon = dir;
            }
        }
        catch { /* no hwmon class -> no chip */ }
        return null;
    }
}
