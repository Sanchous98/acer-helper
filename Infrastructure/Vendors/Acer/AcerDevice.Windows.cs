using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Vendors.Acer;

// Windows: create the Acer transports (WMI) and wire the generic feature holders (DelegatePorts.cs) to the
// per-feature encoding methods below. All the Acer-on-Windows encoding lives here (indices, bit-packing,
// magic values) as named methods + a few WMI call helpers; there are no per-feature classes. If the gaming
// WMI isn't accessible (not elevated) we keep the inherited generic ports and say why. RGB is assembled from
// the EneHidController brick (the Infrastructure/Lighting RGB framework); hotkeys stay their own class.
public sealed partial class AcerDevice
{
    private const ulong LcdOn = 0x1000000000010, LcdOff = 0x10, LcdGetBit = 0x1000000000000;
    private const ulong UsbQuery = 0x4, UsbOff = 663300, UsbAt10 = 659204, UsbAt20 = 1314564, UsbAt30 = 1969924;
    private const ulong BlQuery = 0x88401, BlGetOn = 0x1E0000080000, BlGetOff = 0x80000, BlSetOn = 0x1E0000088402, BlSetOff = 0x88402;

    // GetGamingKBBacklight (method id 21): gmInput = 1; out gmReturn = 0 on success, gmOutput[2] = brightness.
    private const uint KbBacklightQuery = 1;
    private const int KbBrightnessByte = 2;

    private WmiInvoker _gaming = null!, _battery = null!, _apge = null!;

    // The EC HID channel that carries the real performance envelope on models that expose it; null elsewhere
    // (then SetProfile keeps doing WMI alone, exactly as before). See AcerEcHidController.
    private AcerEcHidController? _ec;

    partial void InitVendor()
    {
        Own(_gaming = new WmiInvoker("AcerGamingFunction"));
        if (!_gaming.Available)
        {
            StatusMessage = _gaming.LastError ?? "Acer WMI unavailable — run as administrator.";
            return;   // keep the inherited generic ports only
        }

        // Probe the EC HID interface before wiring profiles: on models that have it, a profile switch has to
        // drive it too or the power envelope never moves (see AcerEcHidController). Not owned when absent.
        var ec = new AcerEcHidController();
        if (ec.Available) Own(_ec = ec); else ec.Dispose();

        // The profile set: Acer's WMI is the richer source on Windows, so it replaces the generic overlay port.
        // The traits come off the SAME table, handed over as its lookup — so the rows the UI shows and the class
        // and colours each row carries are one list rather than two that could drift (see ProfileTraits).
        PowerProfiles = new ProfilesPort(AcerProfiles.All, SelectableProfiles, CurrentProfile, SetProfile,
                                         new ProfileTraitsLookup(AcerProfiles.TraitsOf), AcerProfiles.IsAvailable);

        // Boot sync, EC-ONLY: the profile byte survives a reboot but the EC usage mode behind it does not, so
        // the machine can report Turbo while running the lowest power row. Push the mode matching whatever
        // profile the hardware reports — deliberately NOT a profile switch. Driving a full pp.Set() here (as
        // 0.28.0 did from LaptopService.ApplyStartupState) re-flashed the lightbar and raced the power-source
        // restore, producing a visible Balanced->Turbo->Eco cascade at every boot. This write is invisible: no
        // WMI write, no palette flash, and it cannot disagree with what the UI shows. See docs/power-an18-61.md.
        if (_ec != null && CurrentProfile() is { } cur) _ec.Apply(AcerProfiles.TraitsOf(cur).Kind);

        // The GPU-mode (MUX) switch, over the SAME gaming-WMI helpers the profiles use. The recovered interface
        // is selector 9 = capability, selector 2 = read, `(mode << 8) | 2` = write (see AcerGpuMux.cs for the
        // evidence and the safety posture). Built from the existing GmGet/GmSet so the port opens no WMI path of
        // its own; if selector 9 does not answer, the port comes back unsupported and the shared card says so.
        GpuMux = AcerGpuMux.Create(selector => GmGet(_gaming, "GetGamingMiscSetting", selector),
                                   packed => GmSet(_gaming, "SetGamingMiscSetting", packed));

        Sensors       = new SensorsPort(new AcerSysInfoSensors(Sensor).Read);
        FanControl    = new FanPort(new FanCapability(HasMax: true, HasCustom: true, HasGpuFan: true), SetFanMode, SetFanSpeeds);
        // The three settings this backend owns, DECLARED rather than parked in a slot of their own. Their keys
        // are the names the backend has always known them by — they were the Linuwu-Sense node names, which is
        // why they read like paths — so both OSes declare the same key for the same setting and a settings.json
        // written on one reads on the other. Windows talks WMI and has no node of that name; the key is the
        // backend's name for the setting, not a path.
        //
        // ONLY THIS HALF DECLARES THEM ANY MORE, and that is parity rather than a gap waiting to be filled:
        // mainline acer-wmi gives Linux the profiles, the fan/temperature telemetry and fan control (via the
        // installer's module parameters), and the Turbo key arrives there as a vendor HID report this app decodes
        // itself rather than as anything acer-wmi's cycle_gaming_thermal_profile handles (measured — see
        // AcerHotkeys.Linux.cs), but it has NO interface for LCD overdrive, the
        // keyboard-backlight timeout or USB charging in any configuration — so there is nothing on that side to
        // bind these three rows to, and the Linux backend says exactly that in one status line instead of
        // probing for a module. The keys stay HERE because they are the cross-OS settings.json contract: the bag
        // is persisted whole, so a settings.json written on Windows still carries them on Linux, where the rows
        // simply do not appear — and a Linux build that invented its own name for one of these settings would
        // break the pair. See docs/acer-linux.md.
        //
        // LCD overdrive is the one setting with no readback: its write (SetGamingProfile) returns a status byte,
        // so a row already knows whether it took, and reading the setting back would be a second EC transaction
        // the user hears as a second "click". What that costs is one read at startup — the row's prime — and
        // none per write.
        Declare(new FlagSetting { Key = "lcd_override", ReadbackVerifiesWrite = false,
                                  Port = new FlagPort(GetLcd, SetLcd) });

        Own(_battery = new WmiInvoker("BatteryControl"));
        // The firmware's own capability MASK decides which of the two properties this battery has: ReadStatus
        // reports uFunctionList, a bitmask of the modes the machine supports. Until wave 4b each bit gated a
        // nullable port of its own, which expressed the same presence — what changes is that it is now stated
        // as a property of the battery, so a battery that lacks one is visible as such rather than as a null
        // slot beside three others.
        var bm = _battery.Available ? BatteryWmi.ReadStatus(_battery, out _) : null;
        if (bm?.HealthAvail == true) Battery.ChargeLimit = new(GetChargeLimit, SetChargeLimit);
        if (bm?.CalibAvail  == true) Battery.Calibration = new(GetCalibration, SetCalibration);

        Own(_apge = new WmiInvoker("APGeAction"));
        if (_apge.Available && UsbDecode(UiGet(_apge, UsbQuery)) >= 0)
            Declare(new ChoiceSetting { Key = "usb_charging", Port = new ChoicePort(UsbLevels, GetUsb, SetUsb) });
        var bl = _apge.Available ? UiGet(_apge, BlQuery) : ulong.MaxValue;
        if (bl is BlGetOn or BlGetOff)
            Declare(new FlagSetting { Key = "backlight_timeout", Port = new FlagPort(GetBacklight, SetBacklight) });

        WireRgb(ReadKbBrightness);

        Own(Hotkeys = new AcerHotkeys());
    }

    // ---- performance profiles (misc-setting indices 0x0B current / 0x0A supported-mask) ----
    private IReadOnlyList<PerformanceProfile> SelectableProfiles()
    {
        var o = GmGet(_gaming, "GetGamingMiscSetting", 0x0A);
        return (o & 0xFF) != 0 ? AcerProfiles.All : AcerProfiles.FromMask((byte)((o >> 8) & 0xFF));
    }

    private PerformanceProfile? CurrentProfile()
    {
        var o = GmGet(_gaming, "GetGamingMiscSetting", 0x0B);
        return (o & 0xFF) != 0 ? null : AcerProfiles.ToDomain((byte)((o >> 8) & 0xFF));
    }

    // TWO channels, both needed. The WMI byte is what the EC reports back as "current profile", so the tray,
    // the per-mode presets and the lightbar palette all follow it. The EC HID usage mode is what actually moves
    // the power envelope (GPU TGP/CTGP + CPU limits) on models that expose it — on the AN18-61 the WMI byte
    // alone leaves the dGPU at its bare vBIOS default. They are independent; the EC write is only enqueued
    // (it lands on the controller's writer thread), so it cannot slow this call down. See docs/power-an18-61.md.
    private (bool, string?) SetProfile(PerformanceProfile p)
    {
        _ec?.Apply(AcerProfiles.TraitsOf(p).Kind);
        return GmSet(_gaming, "SetGamingMiscSetting", 0x0B | ((ulong)AcerProfiles.ToByte(p) << 8));
    }

    // ---- sensors ----
    // The four channels, read as the EC's own sysinfo ids and assembled by AcerSysInfoSensors — which owns the
    // rules (the temperature hold, one per channel, and the absent fallback) in an un-suffixed, tested file. THE
    // FAN ROWS ARE NOT HELD: 0 rpm is a real reading (the fans stop at idle) and the UI must show it, while a 0
    // on a TEMPERATURE row means the channel is not answering — the owner's "GPU часто показывает 0 градусов".
    // There is no generic substitute to fall back to on Windows (the generic base wires no sensors at all), so the
    // row carries -1 until the channel answers. See AcerSysInfoSensors.
    private int Sensor(ulong id, bool word)
    {
        var o = GmGet(_gaming, "GetGamingSysInfo", 0x0001 | (id << 8));
        return (o & 0xFF) != 0 ? -1 : (int)((o >> 8) & (word ? 0xFFFFUL : 0xFFUL));
    }

    // ---- fans (GPU uses different ids for behaviour 0x08 vs speed 0x04) ----
    private (bool, string?) SetFanMode(FanMode m)
        => GmSet(_gaming, "SetGamingFanBehavior", 0x09 | ((ulong)(byte)m << 16) | ((ulong)(byte)m << 22));

    private (bool, string?) SetFanSpeeds(byte cpu, byte gpu)
    {
        var a = GmSet(_gaming, "SetGamingFanSpeed", 0x01 | ((ulong)cpu << 8));
        return a.ok ? GmSet(_gaming, "SetGamingFanSpeed", 0x04 | ((ulong)gpu << 8)) : a;
    }

    // ---- LCD overdrive ----
    private bool GetLcd()
    {
        // Status byte first: the failure sentinel (all-ones) has LcdGetBit set, so an unchecked read
        // would report overdrive "on" whenever the WMI call fails.
        var o = GmGet(_gaming, "GetGamingProfile", 0x00);
        return (o & 0xFF) == 0 && (o & LcdGetBit) != 0;
    }
    private (bool, string?) SetLcd(bool on) => GmSet(_gaming, "SetGamingProfile", on ? LcdOn : LcdOff);

    // ---- battery health (charge limit / calibration; differ only by mask) ----
    private bool GetChargeLimit() => BatteryWmi.ReadStatus(_battery, out _)?.HealthOn ?? false;
    private (bool, string?) SetChargeLimit(bool on) => BatteryWmi.SetControl(_battery, BatteryWmi.HealthMode, on);
    private bool GetCalibration() => BatteryWmi.ReadStatus(_battery, out _)?.CalibOn ?? false;
    private (bool, string?) SetCalibration(bool on) => BatteryWmi.SetControl(_battery, BatteryWmi.CalibrationMode, on);

    // ---- USB charging / keyboard-backlight timeout (APGeAction) ----
    // Ids are the battery-threshold percentages ("0" = off): charging stops once the battery drops there.
    // The UsbLevels list itself is shared (see AcerDevice.cs).
    private string? GetUsb() => Math.Max(0, UsbDecode(UiGet(_apge, UsbQuery))).ToString();
    private (bool, string?) SetUsb(string id) => UiSet(_apge, id switch { "10" => UsbAt10, "20" => UsbAt20, "30" => UsbAt30, _ => UsbOff });
    private bool GetBacklight() => UiGet(_apge, BlQuery) == BlGetOn;
    private (bool, string?) SetBacklight(bool on) => UiSet(_apge, on ? BlSetOn : BlSetOff);

    // ---- RGB keyboard brightness read-back (via the gaming WMI; the RGB itself is a write-only HID
    // controller — see EneHidController). Injected into the keyboard RgbZone so the UI can sync to Fn keys. ----
    private int? ReadKbBrightness()
    {
        try
        {
            using var o = _gaming.Invoke("GetGamingKBBacklight", new Dictionary<string, object> { ["gmInput"] = KbBacklightQuery });
            if (o == null || o.GetByte("gmReturn") != 0) return null;
            var d = o.GetBytes("gmOutput");
            return d.Length > KbBrightnessByte ? Math.Clamp(d[KbBrightnessByte], (byte)0, (byte)100) : null;
        }
        catch { return null; }
    }

    // ---- WMI call helpers ----
    // Gaming: one gmInput -> gmOutput; low byte = status (0 = ok), the rest is the value. APGeAction uses
    // uiInput -> uiOutput (GetFunction returns a raw magic value; SetFunction has no status). Invoke returns
    // null when the WMI call itself failed — that maps to the all-ones sentinel (status byte 0xFF), NEVER to
    // 0, which the protocol would read as success-with-value-0 (e.g. "current profile is Quiet").
    private static ulong GmGet(WmiInvoker w, string method, ulong gmInput)
    {
        try { return w.Invoke(method, "gmInput", gmInput, "gmOutput") ?? ulong.MaxValue; }
        catch { return ulong.MaxValue; }
    }

    private static (bool ok, string? error) GmSet(WmiInvoker w, string method, ulong gmInput)
    {
        try
        {
            var o = w.Invoke(method, "gmInput", gmInput, "gmOutput");
            if (o == null) return (false, w.LastError ?? $"{method} failed (WMI)");
            return (o & 0xFF) == 0 ? (true, null) : (false, $"{method} status={o & 0xFF}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static ulong UiGet(WmiInvoker w, ulong uiInput)
    {
        try { return w.Invoke("GetFunction", "uiInput", uiInput, "uiOutput") ?? ulong.MaxValue; }
        catch { return ulong.MaxValue; }
    }

    private static (bool ok, string? error) UiSet(WmiInvoker w, ulong uiInput)
    {
        try
        {
            // SetFunction has no status in its output, but a null result means the WMI call itself failed.
            return w.Invoke("SetFunction", "uiInput", uiInput, "uiOutput") != null
                ? (true, null) : (false, w.LastError ?? "SetFunction failed (WMI)");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static int UsbDecode(ulong r) => r switch { UsbOff => 0, UsbAt10 => 10, UsbAt20 => 20, UsbAt30 => 30, _ => -1 };
}
