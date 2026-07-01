using System.IO;
using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>Generic Linux battery charge limit via the standard power_supply node
/// <c>charge_control_end_threshold</c> — supported by many laptops through the kernel (Dell, Lenovo, ASUS,
/// …), independent of any vendor tool. On = cap at 80% (battery-health), Off = 100%. Reading works as the
/// user; writing needs root or a udev rule (surfaced via LastError, not thrown).</summary>
public sealed class SysfsChargeLimit : IBatteryChargeLimit
{
    private const int LimitPercent = 80;

    private readonly string _node;
    private SysfsChargeLimit(string node) => _node = node;

    public string? LastError { get; private set; }

    /// <summary>Present only if the kernel exposes the end-threshold node for a battery AND it is writable
    /// by the current user. Like the hwmon fan PWM, the node is usually root-owned, so without a udev rule
    /// this returns null and the UI omits the toggle (rather than offering one that fails with
    /// "access denied" on every write).</summary>
    public static SysfsChargeLimit? TryCreate()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories("/sys/class/power_supply"))
            {
                if (!Path.GetFileName(d).StartsWith("BAT", StringComparison.Ordinal)) continue;
                string node = Path.Combine(d, "charge_control_end_threshold");
                if (File.Exists(node) && CanWrite(node)) return new SysfsChargeLimit(node);
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

    public bool Get()
    {
        try { return int.TryParse(File.ReadAllText(_node).Trim(), out int v) && v is > 0 and < 100; }
        catch { return false; }
    }

    public bool Set(bool on)
    {
        try { File.WriteAllText(_node, (on ? LimitPercent : 100).ToString()); LastError = null; return true; }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }
}
