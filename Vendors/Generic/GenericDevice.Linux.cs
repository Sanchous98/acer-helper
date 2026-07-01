using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

// Generic Linux common capabilities: performance profiles via power-profiles-daemon (or the sysfs
// platform_profile fallback), temperature/RPM monitoring via hwmon, and — where the kernel allows it —
// fan PWM control. (Battery telemetry + autostart are wired cross-platform in GenericDevice.cs.) A vendor
// backend extends this and overrides/adds its own.
public partial class GenericDevice
{
    partial void InitPlatform()
    {
        var (_, product) = MachineInfo.Read();
        if (!string.IsNullOrWhiteSpace(product)) VendorName = product!;

        IPowerProfiles? profiles = null;
        var ppd = new PpdPowerProfiles();          // preferred: switches via polkit (no root)
        if (ppd.Available) profiles = ppd;
        else { var sysfs = new SysfsPowerProfiles(); if (sysfs.Available) profiles = sysfs; }
        PowerProfiles = profiles;
        if (profiles == null && StatusMessage == null)
            StatusMessage = "No power-profile interface found — limited controls.";

        Sensors = HwmonSensors.TryCreate();        // RPM + temps (read-only, universal)

        var fans = HwmonFanControl.TryCreate();    // PWM control only where the kernel allows it (usually null)
        if (fans != null) { FanControl = fans; Own(fans); }

        // Standard-Linux extras (no vendor tool needed):
        BatteryChargeLimit = SysfsChargeLimit.TryCreate();   // charge_control_end_threshold (many laptops)

        var tint = new DisplayTint();                        // X11 gamma blue-light (null-op on Wayland)
        if (tint.Available) DisplayTint = tint;

        // No clamshell on Linux: the DE's power manager owns the lid (e.g. KDE PowerDevil holds a block
        // inhibitor on handle-lid-switch and suspends per its own config), so there's no DE-agnostic lever
        // to override it — the port stays null and the UI hides it. (Windows keeps clamshell.)
    }
}
