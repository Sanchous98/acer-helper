using System.Management;
using System.Runtime.InteropServices;

namespace AcerHelper.Os;

/// <summary>
/// Vendor-agnostic battery telemetry on Windows. Charge % and charge/discharge state come from
/// <c>GetSystemPowerStatus</c> (cheap, read every refresh); health (full ÷ design capacity) and
/// cycle count come from the standard <c>root\WMI</c> smart-battery classes and barely change, so
/// they are read once at construction and cached. Returns null via <see cref="TryCreate"/> when the
/// machine has no battery.
/// </summary>
public sealed class BatteryInfo : IBatteryInfo
{
    private readonly int _health;   // cached: full/design * 100, -1 if unknown
    private readonly int _cycles;   // cached: -1 if unsupported

    private BatteryInfo(int health, int cycles) { _health = health; _cycles = cycles; }

    /// <summary>Probe for a battery; null on a desktop / when no battery is present.</summary>
    public static BatteryInfo? TryCreate()
    {
        if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS s)) return null;
        if ((s.BatteryFlag & 128) != 0) return null;   // 128 = no system battery

        int design = WmiUint(@"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity");
        int full   = WmiUint(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity");
        int health = design > 0 && full > 0 ? Math.Min(100, (int)((long)full * 100 / design)) : -1;
        int cycles = WmiUint(@"root\WMI", "SELECT CycleCount FROM BatteryCycleCount", "CycleCount");

        return new BatteryInfo(health, cycles > 0 ? cycles : -1);
    }

    public BatteryInfoSnapshot Read()
    {
        if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS s))
            return new BatteryInfoSnapshot { HealthPercent = _health, CycleCount = _cycles };

        int percent = s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent;
        BatteryState state =
            (s.BatteryFlag & 8) != 0 ? BatteryState.Charging     // 8 = charging
            : s.ACLineStatus == 1    ? BatteryState.Idle         // plugged in, not charging
            :                          BatteryState.Discharging;

        return new BatteryInfoSnapshot
        {
            Percent = percent,
            State = state,
            HealthPercent = _health,
            CycleCount = _cycles,
        };
    }

    private static int WmiUint(string scope, string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementBaseObject o in searcher.Get())
            {
                var v = o[property];
                if (v != null) return Convert.ToInt32(v);
            }
        }
        catch { /* class/property unsupported on this battery */ }
        return -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus; public byte BatteryFlag; public byte BatteryLifePercent;
        public byte SystemStatusFlag; public uint BatteryLifeTime; public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
