using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Infrastructure.Plugins.Sdk;
using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Vendor.AcerNitro;

// WINDOWS PROBE (docs/vendor-plugins.md §6 Phase 1 step 3, "Windows first"). This is a PORT of
// AcerDevice.Windows.cs's InitVendor (:27-115) and its encoding helpers into a plugin-owned file — the host file
// itself is untouched. It is a port and not a move because AcerDevice.Windows.cs is a partial of the host-only
// AcerDevice : GenericDevice (AcerDevice.cs:15) and cannot compile without the host; the logic it carries is
// reproduced here against the session's capability fields instead of a Device. Every method below cites the host
// line it came from so the two can be diffed.
//
// WHAT IS DELIBERATELY *NOT* PORTED: AcerHotkeys (the raw-input message window must be created on the host's
// Avalonia UI thread, which the plugin does not own, and it needs the event sink §7.1 risk 9 defers). The hotkey
// FILES still compile (they are in the csproj list), but the hotkeys capability is ABSENT from the manifest —
// "a smaller honest manifest beats a larger lying one".
internal sealed partial class AcerSession
{
    // Acer-on-Windows magic values, verbatim from AcerDevice.Windows.cs:14-20.
    private const ulong LcdOn = 0x1000000000010, LcdOff = 0x10, LcdGetBit = 0x1000000000000;
    private const ulong UsbQuery = 0x4, UsbOff = 663300, UsbAt10 = 659204, UsbAt20 = 1314564, UsbAt30 = 1969924;
    private const ulong BlQuery = 0x88401, BlGetOn = 0x1E0000080000, BlGetOff = 0x80000, BlSetOn = 0x1E0000088402, BlSetOff = 0x88402;

    private const uint KbBacklightQuery = 1;
    private const int KbBrightnessByte = 2;

    // USB-charging levels (ids = battery-threshold percentages, "0" = off), verbatim from AcerDevice.cs:43-44.
    private static readonly ChoiceOption[] UsbLevels =
        [new("0", "level.off"), new("10", "10%"), new("20", "20%"), new("30", "30%")];

    private WmiInvoker _gaming = null!, _battery = null!, _apge = null!;

    partial void ConnectPlatform()
    {
        // AcerDevice.Windows.cs:30-35. No gaming WMI (not elevated / not an Acer gaming model) -> the honest
        // minimum: the status key only, every capability absent.
        _gaming = new WmiInvoker("AcerGamingFunction");
        if (!_gaming.Available)
        {
            _statusMessage = _gaming.LastError ?? "status.acer_wmi_unavailable";
            return;
        }

        // AcerDevice.Windows.cs:39-40: the EC HID envelope channel, probed before wiring profiles.
        var ec = new AcerEcHidController();
        if (ec.Available) _ec = ec; else ec.Dispose();

        // AcerDevice.Windows.cs:45-46: the profile set + its own traits/availability, off one table.
        _profilesAll = AcerProfiles.All;
        _profilesSelectable = SelectableProfiles;
        _profilesCurrent = CurrentProfile;
        _profilesSet = SetProfile;

        // AcerDevice.Windows.cs:54: boot sync, EC-only (see the host comment for why a full pp.Set() here is wrong).
        if (_ec != null && CurrentProfile() is { } cur) _ec.Apply(AcerProfiles.TraitsOf(cur).Kind);

        // AcerDevice.Windows.cs:60: the power source rides the EC channel, declared only where it exists.
        if (_ec != null) _powerSource = () => _ec.ReadPowerSource() ?? PowerSource.Unknown;

        // AcerDevice.Windows.cs:66-67: the MUX over the same gaming-WMI helpers. Always attached so the host's
        // shared card can state an unsupported refusal instead of the slot going null (AcerDevice.cs:26-29).
        _mux = AcerGpuMux.Create(
            selector => GmGet(_gaming, "GetGamingMiscSetting", selector),
            packed => GmSet(_gaming, "SetGamingMiscSetting", packed));

        // AcerDevice.Windows.cs:69-70.
        _sensors = new AcerSysInfoSensors(Sensor).Read;
        _fan = new FanCapability(HasMax: true, HasCustom: true, HasGpuFan: true);
        _fanMode = SetFanMode;
        _fanSpeeds = SetFanSpeeds;

        // AcerDevice.Windows.cs:93-94: lcd_override with NO readback verification (its write returns a status).
        _settings.Add(new SettingEntry("lcd_override", "flag", null, ReadbackVerifiesWrite: false,
            Read: () => GetLcd() ? "1" : "0", Write: on => SetLcd(on == "1")));

        // AcerDevice.Windows.cs:96-104: the firmware's capability mask decides which battery properties exist.
        _battery = new WmiInvoker("BatteryControl");
        var bm = _battery.Available ? AcerBatteryWmi.ReadStatus(_battery, out _) : null;
        if (bm?.HealthAvail == true) { _chargeLimitRead = GetChargeLimit; _chargeLimitWrite = SetChargeLimit; }
        if (bm?.CalibAvail == true) { _calibRead = GetCalibration; _calibWrite = SetCalibration; }

        // AcerDevice.Windows.cs:106-111: APGeAction's USB charging and keyboard-backlight timeout.
        _apge = new WmiInvoker("APGeAction");
        if (_apge.Available && UsbDecode(UiGet(_apge, UsbQuery)) >= 0)
            _settings.Add(new SettingEntry("usb_charging", "choice", UsbLevels, ReadbackVerifiesWrite: true,
                Read: GetUsb, Write: SetUsb));
        var bl = _apge.Available ? UiGet(_apge, BlQuery) : ulong.MaxValue;
        if (bl is BlGetOn or BlGetOff)
            _settings.Add(new SettingEntry("backlight_timeout", "flag", null, ReadbackVerifiesWrite: true,
                Read: () => GetBacklight() ? "1" : "0", Write: on => SetBacklight(on == "1")));

        // AcerDevice.cs:51-56 / AcerDevice.Windows.cs:113: the ENE RGB device, with the WMI keyboard-brightness read.
        WireRgb(ReadKbBrightness);
    }

    /// <summary>AcerDevice.cs:46-56: assemble the RGB device from the ENE controller, adopt it only if it has zones
    /// (else it is disposed so no HID handle leaks). On Windows the keyboard zone gets the gaming-WMI brightness
    /// read-back, which is what <c>readBrightness</c> in the manifest advertises.</summary>
    private void WireRgb(Func<int?>? kbBrightness)
    {
        var rgb = new EneHidController(_model.Zones, _model.Lightbar, kbBrightness);
        if (rgb.Zones.Count > 0) { _rgb = new RgbDevice(rgb); _rgbReadBrightness = kbBrightness is not null; }
        else rgb.Dispose();
    }

    partial void DisposePlatform()
    {
        _ec?.Dispose();
        _gaming.Dispose();
        _battery.Dispose();
        _apge.Dispose();
    }

    // ---- profiles (misc-setting indices 0x0B current / 0x0A supported-mask) — AcerDevice.Windows.cs:119-140 ----

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
    {
        _ec?.Apply(AcerProfiles.TraitsOf(p).Kind);
        return GmSet(_gaming, "SetGamingMiscSetting", 0x0B | ((ulong)AcerProfiles.ToByte(p) << 8));
    }

    // ---- sensors — AcerDevice.Windows.cs:149-153 -----------------------------------------------------------------

    private int Sensor(ulong id, bool word)
    {
        var o = GmGet(_gaming, "GetGamingSysInfo", 0x0001 | (id << 8));
        return (o & 0xFF) != 0 ? -1 : (int)((o >> 8) & (word ? 0xFFFFUL : 0xFFUL));
    }

    // ---- fans — AcerDevice.Windows.cs:156-163 --------------------------------------------------------------------

    private (bool, string?) SetFanMode(FanMode m)
        => GmSet(_gaming, "SetGamingFanBehavior", 0x09 | ((ulong)(byte)m << 16) | ((ulong)(byte)m << 22));

    private (bool, string?) SetFanSpeeds(byte cpu, byte gpu)
    {
        var a = GmSet(_gaming, "SetGamingFanSpeed", 0x01 | ((ulong)cpu << 8));
        return a.ok ? GmSet(_gaming, "SetGamingFanSpeed", 0x04 | ((ulong)gpu << 8)) : a;
    }

    // ---- LCD overdrive — AcerDevice.Windows.cs:166-173 -----------------------------------------------------------

    private bool GetLcd()
    {
        var o = GmGet(_gaming, "GetGamingProfile", 0x00);
        return (o & 0xFF) == 0 && (o & LcdGetBit) != 0;
    }

    private (bool, string?) SetLcd(bool on) => GmSet(_gaming, "SetGamingProfile", on ? LcdOn : LcdOff);

    // ---- battery health — AcerDevice.Windows.cs:176-179 ----------------------------------------------------------

    private bool GetChargeLimit() => AcerBatteryWmi.ReadStatus(_battery, out _)?.HealthOn ?? false;
    private (bool, string?) SetChargeLimit(bool on) => AcerBatteryWmi.SetControl(_battery, AcerBatteryWmi.HealthMode, on);
    private bool GetCalibration() => AcerBatteryWmi.ReadStatus(_battery, out _)?.CalibOn ?? false;
    private (bool, string?) SetCalibration(bool on) => AcerBatteryWmi.SetControl(_battery, AcerBatteryWmi.CalibrationMode, on);

    // ---- USB charging / backlight timeout — AcerDevice.Windows.cs:184-187 ----------------------------------------

    private string? GetUsb() => Math.Max(0, UsbDecode(UiGet(_apge, UsbQuery))).ToString();
    private (bool, string?) SetUsb(string id) => UiSet(_apge, id switch { "10" => UsbAt10, "20" => UsbAt20, "30" => UsbAt30, _ => UsbOff });
    private bool GetBacklight() => UiGet(_apge, BlQuery) == BlGetOn;
    private (bool, string?) SetBacklight(bool on) => UiSet(_apge, on ? BlSetOn : BlSetOff);

    // ---- keyboard brightness read-back — AcerDevice.Windows.cs:191-201 -------------------------------------------

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

    // ---- WMI call helpers — AcerDevice.Windows.cs:208-242 --------------------------------------------------------

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
            return w.Invoke("SetFunction", "uiInput", uiInput, "uiOutput") != null
                ? (true, null) : (false, w.LastError ?? "SetFunction failed (WMI)");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static int UsbDecode(ulong r) => r switch { UsbOff => 0, UsbAt10 => 10, UsbAt20 => 20, UsbAt30 => 30, _ => -1 };
}

/// <summary>
/// The Windows <c>BatteryControl</c> WMI encoding, PORTED from <c>AcerBattery.Windows.cs</c> (which cannot be
/// source-included: the scaffold guard <c>AcerPluginScaffoldTests.AcerPluginDoesNotIncludeTheWiringFiles</c> pins
/// that file as a wiring file the plugin must not include). The methods mirror the Acer firmware structs; the
/// bodies and constants are byte-identical to the host's <c>BatteryWmi</c>, only the type name differs so the two
/// do not collide in a P4 build that might carry both.
/// </summary>
internal static class AcerBatteryWmi
{
    private const byte BatteryNo = 1;
    public const byte HealthMode = 0x01;
    public const byte CalibrationMode = 0x02;

    public static (bool HealthAvail, bool HealthOn, bool CalibAvail, bool CalibOn)? ReadStatus(
        WmiInvoker wmi, out string? error)
    {
        error = null;
        try
        {
            using var outp = wmi.Invoke("GetBatteryHealthControlStatus", new Dictionary<string, object>
            {
                ["uBatteryNo"] = BatteryNo, ["uFunctionQuery"] = (byte)0x1, ["uReserved"] = new byte[2],
            });
            if (outp == null) { error = wmi.LastError; return null; }
            var list = outp.GetByte("uFunctionList");
            var status = outp.GetBytes("uFunctionStatus");
            var ha = (list & HealthMode) != 0;
            var ca = (list & CalibrationMode) != 0;
            return (ha, ha && status.Length > 0 && status[0] > 0,
                    ca, ca && status.Length > 1 && status[1] > 0);
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    public static (bool ok, string? error) SetControl(WmiInvoker wmi, byte mask, bool on)
    {
        try
        {
            using var outp = wmi.Invoke("SetBatteryHealthControl", new Dictionary<string, object>
            {
                ["uBatteryNo"] = BatteryNo, ["uFunctionMask"] = mask,
                ["uFunctionStatus"] = (byte)(on ? 1 : 0), ["uReservedIn"] = new byte[5],
            });
            return outp != null ? (true, null) : (false, wmi.LastError);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
