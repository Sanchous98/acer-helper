using AcerHelper.Features;
using AcerHelper.Vendors.Generic;

namespace AcerHelper.Vendors.Acer;

// Windows: create the Acer transports (WMI) and wire the generic feature holders (DelegatePorts.cs) to the
// per-feature encoding methods below. All the Acer-on-Windows encoding lives here (indices, bit-packing,
// magic values) as named methods + a few WMI call helpers; there are no per-feature classes. If the gaming
// WMI isn't accessible (not elevated) we keep the inherited generic ports and say why. RGB is assembled from
// the EneHidController brick (Rgb.cs framework); hotkeys stay their own class.
public sealed partial class AcerDevice
{
    private const ulong LcdOn = 0x1000000000010, LcdOff = 0x10, LcdGetBit = 0x1000000000000;
    private const ulong UsbQuery = 0x4, UsbOff = 663300, UsbAt10 = 659204, UsbAt20 = 1314564, UsbAt30 = 1969924;
    private const ulong BlQuery = 0x88401, BlGetOn = 0x1E0000080000, BlGetOff = 0x80000, BlSetOn = 0x1E0000088402, BlSetOff = 0x88402;

    // GetGamingKBBacklight (method id 21): gmInput = 1; out gmReturn = 0 on success, gmOutput[2] = brightness.
    private const uint KbBacklightQuery = 1;
    private const int KbBrightnessByte = 2;

    private WmiInvoker _gaming = null!, _battery = null!, _apge = null!;

    partial void InitVendor()
    {
        Own(_gaming = new WmiInvoker("AcerGamingFunction"));
        if (!_gaming.Available)
        {
            StatusMessage = _gaming.LastError ?? "Acer WMI unavailable — run as administrator.";
            return;   // keep the inherited generic ports only
        }

        // Profiles + sensors: Acer's WMI is the richer/only source on Windows -> override the generic ones.
        PowerProfiles = new ProfilesPort(AcerProfiles.All, SelectableProfiles, CurrentProfile, SetProfile);
        Sensors       = new SensorsPort(ReadSensors);
        FanControl    = new FanPort(new FanCapability(HasMax: true, HasCustom: true, HasGpuFan: true), SetFanMode, SetFanSpeeds);
        LcdOverdrive  = new FlagPort(GetLcd, SetLcd);

        Own(_battery = new WmiInvoker("BatteryControl"));
        var bm = _battery.Available ? BatteryWmi.ReadStatus(_battery, out _) : null;
        if (bm?.HealthAvail == true) BatteryChargeLimit = new FlagPort(GetChargeLimit, SetChargeLimit);
        if (bm?.CalibAvail  == true) BatteryCalibration = new FlagPort(GetCalibration, SetCalibration);

        Own(_apge = new WmiInvoker("APGeAction"));
        if (_apge.Available && UsbDecode(UiGet(_apge, UsbQuery)) >= 0)
            UsbCharging = new ChoicePort(UsbLevels, GetUsb, SetUsb);
        var bl = _apge.Available ? UiGet(_apge, BlQuery) : ulong.MaxValue;
        if (bl is BlGetOn or BlGetOff) KeyboardBacklight = new FlagPort(GetBacklight, SetBacklight);

        var rgb = new EneHidController(_model.Zones, _model.Lightbar, ReadKbBrightness);
        if (rgb.Zones.Count > 0) { var dev = new RgbDevice(rgb); Lighting = dev; Own(dev); }
        else rgb.Dispose();

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

    private (bool, string?) SetProfile(PerformanceProfile p)
        => GmSet(_gaming, "SetGamingMiscSetting", 0x0B | ((ulong)AcerProfiles.ToByte(p) << 8));

    // ---- sensors ----
    private SensorSnapshot ReadSensors() => new()
    {
        CpuTempC = Sensor(_gaming, 0x01, word: false),
        GpuTempC = Sensor(_gaming, 0x0A, word: false),
        Fans = [new FanReading("CPU", Sensor(_gaming, 0x02, word: true)),
                new FanReading("GPU", Sensor(_gaming, 0x06, word: true))],
    };

    // ---- fans (GPU uses different ids for behaviour 0x08 vs speed 0x04) ----
    private (bool, string?) SetFanMode(FanMode m)
        => GmSet(_gaming, "SetGamingFanBehavior", 0x09 | ((ulong)(byte)m << 16) | ((ulong)(byte)m << 22));

    private (bool, string?) SetFanSpeeds(byte cpu, byte gpu)
    {
        var a = GmSet(_gaming, "SetGamingFanSpeed", 0x01 | ((ulong)cpu << 8));
        return a.ok ? GmSet(_gaming, "SetGamingFanSpeed", 0x04 | ((ulong)gpu << 8)) : a;
    }

    // ---- LCD overdrive ----
    private bool GetLcd() => (GmGet(_gaming, "GetGamingProfile", 0x00) & LcdGetBit) != 0;
    private (bool, string?) SetLcd(bool on) => GmSet(_gaming, "SetGamingProfile", on ? LcdOn : LcdOff);

    // ---- battery health (charge limit / calibration; differ only by mask) ----
    private bool GetChargeLimit() => BatteryWmi.ReadStatus(_battery, out _)?.HealthOn ?? false;
    private (bool, string?) SetChargeLimit(bool on) => BatteryWmi.SetControl(_battery, BatteryWmi.HealthMode, on);
    private bool GetCalibration() => BatteryWmi.ReadStatus(_battery, out _)?.CalibOn ?? false;
    private (bool, string?) SetCalibration(bool on) => BatteryWmi.SetControl(_battery, BatteryWmi.CalibrationMode, on);

    // ---- USB charging / keyboard-backlight timeout (APGeAction) ----
    // Ids are the battery-threshold percentages ("0" = off): charging stops once the battery drops there.
    private static readonly ChoiceOption[] UsbLevels =
        [new("0", "Off"), new("10", "10%"), new("20", "20%"), new("30", "30%")];

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
    // uiInput -> uiOutput (GetFunction returns a raw magic value; SetFunction has no status).
    private static ulong GmGet(WmiInvoker w, string method, ulong gmInput)
    {
        try { return w.Invoke(method, "gmInput", gmInput, "gmOutput"); } catch { return ulong.MaxValue; }
    }

    private static (bool ok, string? error) GmSet(WmiInvoker w, string method, ulong gmInput)
    {
        try
        {
            var o = w.Invoke(method, "gmInput", gmInput, "gmOutput");
            return (o & 0xFF) == 0 ? (true, null) : (false, $"{method} status={o & 0xFF}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static int Sensor(WmiInvoker w, ulong id, bool word)
    {
        var o = GmGet(w, "GetGamingSysInfo", 0x0001 | (id << 8));
        return (o & 0xFF) != 0 ? -1 : (int)((o >> 8) & (word ? 0xFFFFUL : 0xFFUL));
    }

    private static ulong UiGet(WmiInvoker w, ulong uiInput)
    {
        try { return w.Invoke("GetFunction", "uiInput", uiInput, "uiOutput"); } catch { return ulong.MaxValue; }
    }

    private static (bool ok, string? error) UiSet(WmiInvoker w, ulong uiInput)
    {
        try { w.Invoke("SetFunction", "uiInput", uiInput, "uiOutput"); return (true, null); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static int UsbDecode(ulong r) => r switch { UsbOff => 0, UsbAt10 => 10, UsbAt20 => 20, UsbAt30 => 30, _ => -1 };
}
