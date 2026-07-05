using System.Runtime.InteropServices;
using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

// Windows sources: GetSystemPowerStatus for charge %/state; the standard root\WMI smart-battery
// classes for design/full capacity (health) and cycle count.
public sealed partial class BatteryInfo
{
    private static partial bool HasBattery()
        => GetSystemPowerStatus(out SYSTEM_POWER_STATUS s) && (s.BatteryFlag & 128) == 0;   // 128 = no battery

    private static partial (int Health, int Cycles) ReadStatic()
    {
        int design = WmiUint(@"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity");
        int full   = WmiUint(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity");
        int health = design > 0 && full > 0 ? Math.Min(100, (int)((long)full * 100 / design)) : -1;
        int cycles = WmiUint(@"root\WMI", "SELECT CycleCount FROM BatteryCycleCount", "CycleCount");
        return (health, cycles > 0 ? cycles : -1);
    }

    private static partial (int Percent, BatteryState State) ReadLive()
    {
        if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS s)) return (-1, BatteryState.Unknown);
        int percent = s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent;
        BatteryState state =
            (s.BatteryFlag & 8) != 0 ? BatteryState.Charging     // 8 = charging
            : s.ACLineStatus == 1    ? BatteryState.Idle         // plugged in, not charging
            :                          BatteryState.Discharging;
        return (percent, state);
    }

    private static int WmiUint(string scope, string query, string property)
    {
        using var session = WmiSession.Connect(scope, out _);
        if (session == null) return -1;
        using var row = session.QueryFirst(query, out _);   // class/property unsupported on this battery => null
        return row == null ? -1 : row.GetInt(property);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus; public byte BatteryFlag; public byte BatteryLifePercent;
        public byte SystemStatusFlag; public uint BatteryLifeTime; public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
