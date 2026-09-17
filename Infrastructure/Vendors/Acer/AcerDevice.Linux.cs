using System.IO;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Vendors.Acer;

// Linux: probe each Acer channel INDEPENDENTLY and wire the generic feature holders (DelegatePorts.cs) to the
// per-feature encoding methods below. All the Acer-on-Linux encoding lives here (node names + trivial formats).
//
// Three tiers, and only the third needs a kernel module:
//   1. the two HID interfaces — RGB and the EC performance-envelope channel — over raw hidraw, module-free
//      (RGB reuses the cross-platform EneHidController brick: same ENE device and packets as Windows; on recent
//      models it is HID-over-I2C, not USB);
//   2. the profile set, from platform_profile — a MAINLINE acer-wmi interface, so also module-free;
//   3. the Linuwu-Sense sysfs nodes (fans, LCD override, battery limiter/calibration, backlight timeout, USB
//      charging), which are the only thing that goes away when the module isn't loaded.
// Profiles come from platform_profile (the full Acer firmware set) rather than the PPD collapse; sensors stay
// generic hwmon.
public sealed partial class AcerDevice
{
    private const string LinuwuRoot = "/sys/module/linuwu_sense/drivers/platform:acer-wmi/acer-wmi";

    private SysfsInvoker _sense = null!;

    // The EC HID performance-envelope channel; null when this model doesn't expose it. See AcerEcHidController.
    private AcerEcHidController? _ec;

    partial void InitVendor()
    {
        // RGB is the ENE HID controller — the very same device (and packets) as on Windows, reached via
        // hidraw. It's independent of Linuwu-Sense (a WMI/EC driver, not the HID interface), so wire it first
        // regardless of whether the sysfs module is loaded. No brightness read-back here (that's gaming WMI).
        WireRgb(readKeyboardBrightness: null);

        // The EC HID performance-envelope channel is independent of Linuwu-Sense (that is a WMI/EC platform
        // driver; this is the HID interface), so probe it here regardless of whether the module is loaded.
        // UNTESTED on Linux hardware: absent or unwritable just means Available = false and the profile path
        // behaves exactly as it did before.
        var ec = new AcerEcHidController();
        if (ec.Available) Own(_ec = ec); else ec.Dispose();

        // Nitro key -> toggle the window (evdev; needs the udev "uaccess" rule, otherwise stays hidden).
        if (AcerHotkeys.TryCreate() is { } keys) Own(Hotkeys = keys);

        // ---- profiles: independent of Linuwu-Sense, so wired OUTSIDE the module gate below ----
        // platform_profile is a mainline acer-wmi interface (the legacy ACPI alias, plus per-handler class nodes
        // on 6.14+), NOT a Linuwu-Sense feature. Probing it behind the gate, as this did until now, meant a
        // machine with mainline acer-wmi and no module threw the profile port away TOGETHER with the EC channel —
        // and that is precisely the configuration with no other way to move the power envelope.
        //
        // The full Acer BIOS profile set is adopted only when this process can actually switch it (root/udev);
        // otherwise the generic polkit-authorised PPD port the base ctor wired stays (working beats
        // richer-but-broken — the same rule DellDevice.Linux follows).
        var sysfs = new SysfsPowerProfiles();
        IPowerProfiles? port = sysfs is { Available: true, Writable: true }
            ? new BatteryGatedProfiles(sysfs, OnAc)
            : PowerProfiles;

        // The EC usage mode has to move with whichever port we ended up with, or the envelope never changes on an
        // EC-HID model — including when the port is PPD's collapsed three-choice set. Enqueue-only and
        // best-effort (see EcSyncedProfiles); a null delegate means this model has no EC channel and the port
        // behaves exactly as it did before.
        var envelope = _ec is { } ecDev ? (Func<ProfileKind, bool>)ecDev.Apply : null;
        PowerProfiles = port is null ? null : new EcSyncedProfiles(port, envelope);

        // Boot sync, EC-only — same reasoning as AcerDevice.Windows.cs: the profile survives a reboot but the EC
        // usage mode behind it does not, so the machine can report Turbo while running the lowest power row. Push
        // the mode matching whatever profile the PLATFORM reports — the sysfs node is the hardware truth even when
        // it is not writable by us — deliberately NOT a profile switch.
        if (envelope != null && sysfs.Current() is { } cur) envelope(cur.Kind);

        var senseDir = FirstExistingDir($"{LinuwuRoot}/predator_sense", $"{LinuwuRoot}/nitro_sense");
        if (senseDir == null)
        {
            StatusMessage = "Linuwu-Sense module not loaded — profiles and RGB work without it; the fan, LCD, battery and backlight controls need it.";
            return;   // keep every port wired above (+ the inherited generic ones)
        }

        // The full Acer BIOS profile set is probed ABOVE, outside this gate — do not move it back in here.
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

        // Plain on/off sysfs knobs collapse to the shared flag factory, which DECLARES each one under the node's
        // own name — the name this backend already knows the setting by (on/off = "1"/"0", the factory
        // defaults). USB stays bespoke — its read normalises the raw node value to an option id — so it is
        // declared here with that same node name as its key. The battery's two knobs take the same node as an
        // op pair instead of a declared setting, because the battery object's properties carry their reason out
        // of the write (Domain/Battery.cs).
        if (Usable("fan_speed"))
            FanControl = new FanPort(new FanCapability(HasMax: true, HasCustom: true, HasGpuFan: true), SetFanMode, SetFanSpeeds);
        // LCD overdrive is declared with no readback, as it is on the Windows side of this backend: the app has
        // never read that row back, and here the node write also reports its own failure, so there is nothing a
        // readback would add. The row still PRIMES from the node, which is one read at startup and none per write.
        if (Usable("lcd_override"))
            Declare(_sense.Flag("lcd_override") with { ReadbackVerifiesWrite = false });
        if (Usable("battery_limiter"))     Battery.ChargeLimit = _sense.BatteryToggle("battery_limiter");
        if (Usable("battery_calibration")) Battery.Calibration = _sense.BatteryToggle("battery_calibration");
        if (Usable("backlight_timeout"))   Declare(_sense.Flag("backlight_timeout"));
        if (Usable("usb_charging"))
            Declare(new ChoiceSetting { Key = "usb_charging", Port = new ChoicePort(UsbLevels, GetUsb, SetUsb) });
    }

    private bool Usable(string node) => _sense.Has(node) && _sense.CanWrite(node);

    // AC = any Mains-class power supply reporting online; no Mains node at all -> assume AC (desktops, and any
    // model whose supply doesn't advertise the class). The policy it feeds — which profiles to grey out on
    // battery — lives in BatteryGatedProfiles, which is cross-platform and testable; this walk is the part that
    // needs a Linux box, so it stays here and is injected.
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

    // (Plain bool sysfs toggles — LCD override, battery limiter/calibration, backlight timeout — are wired
    // directly via _sense.Flag in InitVendor; no per-feature Get/Set methods needed.)

    // ---- USB charging (ids = battery-threshold percentages, "0" = off; UsbLevels shared in AcerDevice.cs) ----
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
