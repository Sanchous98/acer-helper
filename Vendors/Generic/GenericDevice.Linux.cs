using AcerHelper.Os;
using AcerHelper.Composition;

namespace AcerHelper.Vendors.Generic;

/// <summary>
/// The generic Linux device: performance profiles via power-profiles-daemon (or sysfs fallback),
/// fan/temperature monitoring via hwmon, and run-at-login. Used until a vendor-specific Linux
/// backend (e.g. Acer via sysfs) exists; a future vendor backend would be detected first and fall
/// back to this.
/// </summary>
public static class GenericDevice
{
    public static IDevice Create(string? status = null)
    {
        var (_, product) = MachineInfo.Read();

        IPowerProfiles? profiles = null;
        var ppd = new PpdPowerProfiles();          // preferred: 3 modes, switches via polkit (no root)
        if (ppd.Available) profiles = ppd;
        else { var sysfs = new SysfsPowerProfiles(); if (sysfs.Available) profiles = sysfs; }

        var sensors = HwmonSensors.TryCreate();    // RPM + temps (read-only, universal)
        var fans = HwmonFanControl.TryCreate();    // PWM control only where the kernel allows it (usually null)

        return new CompositeDevice
        {
            VendorName    = string.IsNullOrWhiteSpace(product) ? "Generic" : product!,
            StatusMessage = status ?? (profiles == null ? "No power-profile interface found — limited controls." : null),
            PowerProfiles = profiles,
            Sensors       = sensors,
            FanControl    = fans,
            BatteryInfo   = BatteryInfo.TryCreate(),
            Autostart     = new Autostart(),
            Owned         = fans != null ? [fans] : [],
        };
    }
}
