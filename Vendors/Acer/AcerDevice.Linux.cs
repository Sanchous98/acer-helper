using System.IO;
using AcerHelper.Features;
using AcerHelper.Vendors.Generic;

namespace AcerHelper.Vendors.Acer;

// Linux: create the Linuwu-Sense sysfs transport and wire the generic feature holders (DelegatePorts.cs) to
// the per-feature encoding methods below. All the Acer-on-Linux encoding lives here (node names + trivial
// formats). If the module isn't loaded we keep the inherited generic ports and say why. Profiles come from
// platform_profile (the full Acer firmware set) rather than the PPD collapse; sensors stay generic hwmon.
// RGB reuses the cross-platform EneHidController brick (same ENE device and packets as Windows; here it is
// reached via raw hidraw — on recent models it's HID-over-I2C, not USB).
public sealed partial class AcerDevice
{
    private const string OFF = "0";
    private const string ON = "1";
    private const string LinuwuRoot = "/sys/module/linuwu_sense/drivers/platform:acer-wmi/acer-wmi";

    private SysfsInvoker _sense = null!;

    partial void InitVendor()
    {
        // RGB is the ENE HID controller — the very same device (and packets) as on Windows, reached via
        // hidraw. It's independent of Linuwu-Sense (a WMI/EC driver, not the HID interface), so wire it first
        // regardless of whether the sysfs module is loaded. No brightness read-back here (that's gaming WMI).
        var rgb = new EneHidController(_model.Zones, _model.Lightbar, readKeyboardBrightness: null);
        if (rgb.Zones.Count > 0) { var dev = new RgbDevice(rgb); Lighting = dev; Own(dev); }
        else rgb.Dispose();

        var senseDir = FirstExistingDir($"{LinuwuRoot}/predator_sense", $"{LinuwuRoot}/nitro_sense");
        if (senseDir == null)
        {
            StatusMessage = "Linuwu-Sense module not loaded — install/load it for Acer controls.";
            return;   // keep the inherited generic ports (+ any RGB wired above)
        }

        // The full Acer BIOS profile set — but only when this process can actually switch it (root/udev);
        // otherwise the generic polkit-authorised PPD port stays (working beats richer-but-broken).
        var profiles = new SysfsPowerProfiles();
        if (profiles is { Available: true, Writable: true }) PowerProfiles = new BatteryGatedProfiles(profiles);

        _sense = new SysfsInvoker(senseDir);

        // The nodes are 0660 root:<module group> — existing but unusable to us means EVERY control would
        // fail silently, so gate each port on write access and say what to do (a group added to /etc/group
        // only reaches NEW login sessions).
        string[] nodes = ["fan_speed", "lcd_override", "battery_limiter", "battery_calibration", "backlight_timeout", "usb_charging"];
        if (nodes.Any(_sense.Has) && !nodes.Any(Usable))
        {
            StatusMessage = "Linuwu-Sense is loaded but its files aren't accessible — add your user to the module's group (or install the udev rule) and log in again.";
            return;
        }

        if (Usable("fan_speed"))
            FanControl = new FanPort(new FanCapability(HasMax: true, HasCustom: true, HasGpuFan: true), SetFanMode, SetFanSpeeds);
        if (Usable("lcd_override"))        LcdOverdrive       = new FlagPort(GetLcd, SetLcd);
        if (Usable("battery_limiter"))     BatteryChargeLimit = new FlagPort(GetChargeLimit, SetChargeLimit);
        if (Usable("battery_calibration")) BatteryCalibration = new FlagPort(GetCalibration, SetCalibration);
        if (Usable("backlight_timeout"))   KeyboardBacklight  = new FlagPort(GetBacklight, SetBacklight);
        if (Usable("usb_charging"))        UsbCharging        = new ChoicePort(UsbLevels, GetUsb, SetUsb);
    }

    private bool Usable(string node) => _sense.Has(node) && _sense.CanWrite(node);

    // The EC rejects everything except balanced/low-power while on battery (EOPNOTSUPP straight from the
    // driver — verified on AN18-61), mirroring the Windows supported-mask behaviour (Turbo drops out when
    // unplugged). Grey those profiles out rather than letting every click fail.
    private sealed class BatteryGatedProfiles(SysfsPowerProfiles inner) : IPowerProfiles
    {
        private static readonly string[] BatterySafe = ["balanced", "low-power"];

        public string? LastError => inner.LastError;
        public IReadOnlyList<PerformanceProfile> All => inner.All;
        public PerformanceProfile? Current() => inner.Current();
        public bool Set(PerformanceProfile profile) => inner.Set(profile);

        public IReadOnlyList<PerformanceProfile> Selectable()
            => OnAc() ? inner.Selectable() : inner.Selectable().Where(p => BatterySafe.Contains(p.Id)).ToList();

        // AC = any Mains-class power supply reporting online; no Mains node at all -> assume AC.
        private static bool OnAc()
        {
            try
            {
                var mainsSeen = false;
                foreach (var d in Directory.EnumerateDirectories("/sys/class/power_supply"))
                {
                    if (Hwmon.ReadText(Path.Combine(d, "type")) != "Mains") continue;
                    mainsSeen = true;
                    if (Hwmon.ReadText(Path.Combine(d, "online")) == "1") return true;
                }
                return !mainsSeen;
            }
            catch { return true; }
        }
    }

    // ---- fans ("0,0" = auto, "100,100" = max, "cpu,gpu" = custom) ----
    private (bool, string?) SetFanMode(FanMode m) => m switch
    {
        // The driver parses a "cpu,gpu" pair — a bare "0" is rejected with EINVAL (verified on AN18-61).
        FanMode.Auto => Wr("fan_speed", "0,0"),
        FanMode.Max  => Wr("fan_speed", "100,100"),
        _            => (true, null)   // Custom is applied by SetCustomSpeeds
    };

    private (bool, string?) SetFanSpeeds(byte cpu, byte gpu)
        => Wr("fan_speed", $"{Math.Clamp((int)cpu, 0, 100)},{Math.Clamp((int)gpu, 0, 100)}");

    // ---- bool sysfs toggles ----
    private bool GetLcd() => _sense.Read("lcd_override") == ON;
    
    private (bool, string?) SetLcd(bool on) => Wr("lcd_override", on ? ON : OFF);
    
    private bool GetChargeLimit() => _sense.Read("battery_limiter") == ON;
    
    private (bool, string?) SetChargeLimit(bool on) => Wr("battery_limiter", on ? ON : OFF);
    
    private bool GetCalibration() => _sense.Read("battery_calibration") == ON;
    
    private (bool, string?) SetCalibration(bool on) => Wr("battery_calibration", on ? ON : OFF);
    
    private bool GetBacklight() => _sense.Read("backlight_timeout") == ON;
    
    private (bool, string?) SetBacklight(bool on) => Wr("backlight_timeout", on ? ON : OFF);

    // ---- USB charging (ids = battery-threshold percentages, "0" = off) ----
    private static readonly ChoiceOption[] UsbLevels = 
    [
        new("0", "Off"), 
        new("10", "10%"), 
        new("20", "20%"), 
        new("30", "30%")
    ];

    private string GetUsb() => int.TryParse(_sense.Read("usb_charging"), out var v) ? v.ToString() : "0";

    private (bool, string?) SetUsb(string id) => Wr("usb_charging", id);

    private (bool, string?) Wr(string node, string value)
    {
        var ok = _sense.Write(node, value, out var e);
        return (ok, e);
    }

    private static string? FirstExistingDir(params string[] dirs)
    {
        foreach (var d in dirs) { try { if (Directory.Exists(d)) return d; } catch { /* ignore */ } }
        return null;
    }
}
