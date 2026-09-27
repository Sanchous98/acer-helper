using System.IO;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Infrastructure.Plugins.Sdk;
using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Vendor.AcerNitro;

// LINUX PROBE (docs/vendor-plugins.md §6 Phase 2, delivered here to the extent it needs no new host dependency).
// This is a PORT of AcerDevice.Linux.cs's WireMainline/WireProfiles/WireSensors/TryAcerFanPort/AcerChip into a
// plugin-owned file; the host file itself is untouched.
//
// WHAT IS IN THIS SLICE: the ENE RGB controller over hidraw, the EC HID channel (power-source read), the acer
// hwmon chip's sensors and fan control — everything whose transport is already either the source-shared SDK
// (Hwmon, SysfsLink) or an included Acer file (AcerFanPort, AcerEcHidController, EneHidController).
//
// WHAT IS DELIBERATELY OUT (and therefore ABSENT from the Linux manifest): PROFILES. AcerDevice.Linux's profile
// source is SysfsPowerProfiles (Infrastructure/Vendors/Generic/PowerProfiles.Linux.cs) wrapped by
// AcerMappedProfiles/EcSyncedProfiles. None of those is in the plugin's allowed dependency set: SysfsPowerProfiles
// is generic host code (it moves to the SDK in Phase 2, per §6), and AcerProfilePorts.cs is host-coupled by Loc.T
// (this slice's constraint 3). Re-implementing the platform-profile discovery here would be exactly the second
// copy §4.4 rejects, so profiles are honestly absent on Linux rather than a duplicated transport. The hotkeys are
// absent for the same reason as on Windows (no event sink, §7.1 risk 9; the evdev/hidraw loops need an owner).
internal sealed partial class AcerSession
{
    private string? _acerHwmon;
    private AcerFanPort? _fanPort;
    private readonly AcerTemperatureHold _cpuTemp = new();
    private readonly AcerTemperatureHold _gpuTemp = new();

    partial void ConnectPlatform()
    {
        // RGB (AcerDevice.Linux.cs:114): the same ENE HID brick, over hidraw. No brightness read-back on Linux —
        // the gaming-WMI getter that provides it on Windows has no mainline equivalent.
        WireRgb(kbBrightness: null);

        // The EC HID performance-envelope channel (AcerDevice.Linux.cs:119-120). Its power-source read is the one
        // op we expose from it here (there is no profile transport to drive its usage mode).
        var ec = new AcerEcHidController();
        if (ec.Available) _ec = ec; else ec.Dispose();
        if (_ec != null) _powerSource = () => _ec.ReadPowerSource() ?? PowerSource.Unknown;

        // Sensors + fans off the mainline acer-wmi hwmon chip (AcerDevice.Linux.cs:218-297). Declared only when
        // the chip is present NOW: without a profile port there is nothing else Acer-specific to offer, and a
        // manifest that promised an override it cannot read would be the lie this slice forbids. (The host's port
        // re-probes a late-registering chip; that nicety is Phase 2's, once the port lives here.)
        if (AcerChip() is not null)
        {
            _sensors = ReadAcerSensors;
            if (TryAcerFanPort() is { } fans)
            {
                _fanPort = fans;
                _fan = fans.Capability;
                _fanMode = m => (fans.SetMode(m), fans.LastError);
                _fanSpeeds = (cpu, gpu) => (fans.SetCustomSpeeds(cpu, gpu), fans.LastError);
            }
        }

        _statusMessage = _fanPort is not null ? "status.linux_mainline_full"
                       : AcerChip() is not null ? "status.fans_read_only"
                       : "status.linux_mainline_limited";
    }

    private void WireRgb(Func<int?>? kbBrightness)
    {
        var rgb = new EneHidController(_model.Zones, _model.Lightbar, kbBrightness);
        if (rgb.Zones.Count > 0) { _rgb = new RgbDevice(rgb); _rgbReadBrightness = kbBrightness is not null; }
        else rgb.Dispose();
    }

    partial void DisposePlatform()
    {
        _fanPort?.Dispose();
        _ec?.Dispose();
    }

    // ---- sensors — AcerDevice.Linux.cs:227-259 -------------------------------------------------------------------

    private SensorSnapshot ReadAcerSensors()
    {
        if (AcerChip() is not { } chip) return new SensorSnapshot();

        // The generic temperature paths the host's port falls back to are NOT available here: the plugin has no
        // generic sensor port. The hold is therefore handed -1, i.e. "no substitute" — the same honest fallback
        // the Windows half documents (AcerFanPort.cs:190-197). One hold per channel, as there.
        return new SensorSnapshot
        {
            CpuTempC = _cpuTemp.Reading(Hwmon.Milli(Path.Combine(chip, "temp1_input")), -1),
            GpuTempC = _gpuTemp.Reading(Hwmon.Milli(Path.Combine(chip, "temp2_input")), -1),
            Fans = AcerFanChannels.All
                .Select(c => new FanReading(c.Label, Hwmon.ReadInt(Path.Combine(chip, $"fan{c.Number}_input")) ?? -1))
                .ToList(),
        };
    }

    // ---- fans — AcerDevice.Linux.cs:276-324 ----------------------------------------------------------------------

    private AcerFanPort? TryAcerFanPort()
    {
        if (AcerChip() is not { } chip) return null;

        var cpu = AcerFanChannels.Cpu;
        var gpu = AcerFanChannels.Gpu;
        var cpuEnable = Path.Combine(chip, $"pwm{cpu.Number}_enable");
        var gpuEnable = Path.Combine(chip, $"pwm{gpu.Number}_enable");
        var cpuDuty = DutyNode(chip, cpu.Number);
        var gpuDuty = DutyNode(chip, gpu.Number);
        if (cpuDuty == null || gpuDuty == null) return null;
        if (!Hwmon.CanWrite(cpuEnable) || !Hwmon.CanWrite(gpuEnable)) return null;

        return new AcerFanPort(new AcerFanNodes(cpuEnable, cpuDuty, gpuEnable, gpuDuty), WriteNode);
    }

    private static string? DutyNode(string chipDir, int channel)
    {
        HashSet<string> names;
        try
        {
            names = Directory.EnumerateFiles(chipDir).Select(Path.GetFileName).OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
        }
        catch { return null; }

        foreach (var name in new[] { $"pwm{channel}_input", $"pwm{channel}" })
        {
            if (!names.Contains(name)) continue;
            var path = Path.Combine(chipDir, name);
            if (Hwmon.CanWrite(path)) return path;
        }
        return null;
    }

    private static (bool ok, string? error) WriteNode(string path, string value)
    {
        try { File.WriteAllText(path, value); return (true, null); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ---- chip discovery — AcerDevice.Linux.cs:338-352 ------------------------------------------------------------

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
