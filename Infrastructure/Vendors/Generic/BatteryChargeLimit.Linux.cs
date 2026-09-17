using System.IO;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

/// <summary>Generic Linux battery charge limit via the standard power_supply node
/// <c>charge_control_end_threshold</c> — supported by many laptops through the kernel (Dell, Lenovo, ASUS,
/// …), independent of any vendor tool. On = cap at 80% (battery-health), Off = 100%. Reading works as the
/// user; writing needs root or a udev rule (surfaced as the write's own error, not thrown).
///
/// This is a factory rather than a port: the probe's whole result IS the property, and the node it found is
/// captured by the ops it returns, so there is no instance left to hold one (Domain/Battery.cs).
/// </summary>
public static class SysfsChargeLimit
{
    private const int LimitPercent = 80;

    /// <summary>The property, or null when the kernel exposes no end-threshold node for a battery OR it is not
    /// writable by the current user. Like the hwmon fan PWM, the node is usually root-owned, so without a udev
    /// rule this is absent and the UI omits the toggle (rather than offering one that fails with
    /// "access denied" on every write).</summary>
    public static BatteryToggle? TryCreate()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories("/sys/class/power_supply"))
            {
                if (!Path.GetFileName(d).StartsWith("BAT", StringComparison.Ordinal)) continue;
                string node = Path.Combine(d, "charge_control_end_threshold");
                if (File.Exists(node) && CanWrite(node)) return new BatteryToggle(() => Get(node), on => Set(node, on));
            }
        }
        catch { /* none */ }
        return null;
    }

    // Probe write permission without writing (sysfs attrs don't act until an actual write happens).
    private static bool CanWrite(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Write); return true; }
        catch { return false; }
    }

    private static bool Get(string node)
    {
        try { return int.TryParse(File.ReadAllText(node).Trim(), out int v) && v is > 0 and < 100; }
        catch { return false; }
    }

    private static (bool ok, string? error) Set(string node, bool on)
    {
        try { File.WriteAllText(node, (on ? LimitPercent : 100).ToString()); return (true, null); }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
