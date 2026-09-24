using System.Runtime.InteropServices;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

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
        // 255 means "unknown" for BOTH fields, and Windows really does report it — notably for a while during
        // boot, before the power drivers have settled. It must map to Unknown, never to Discharging: callers
        // treat Discharging as "on battery" (LaptopService.SyncPowerSource restores that source's remembered
        // mode), so a transient unknown used to look like an unplug-then-replug and set off a visible cascade
        // of profile switches at every boot — on-battery slot, then on-AC slot, each re-flashing the lightbar.
        // Unknown is explicitly ignored by those callers, which is exactly the wanted behaviour here.
        BatteryState state =
            s.ACLineStatus == 255                                  ? BatteryState.Unknown
            : s.BatteryFlag != 255 && (s.BatteryFlag & 8) != 0      ? BatteryState.Charging     // 8 = charging
            : s.ACLineStatus == 1                                  ? BatteryState.Idle         // plugged in, not charging
            :                                                        BatteryState.Discharging;
        return (percent, state);
    }

    private static partial double? ReadPowerWatts(BatteryState state)
    {
        if (state == BatteryState.Unknown) return null;   // no battery / boot before the power drivers settled
        // CallNtPowerInformation(SystemBatteryState) is one powrprof call and no WMI transaction — the WMI
        // gate is process-wide and shared with the Acer EC, so the per-tick power read stays off it. The API
        // reports Rate in milliwatts; its own sign is not trusted, the caller's State orients the magnitude.
        if (CallNtPowerInformation(SystemBatteryState, 0, 0, out SYSTEM_BATTERY_STATE s,
                                   (uint)Marshal.SizeOf<SYSTEM_BATTERY_STATE>()) != 0)
            return null;
        if (s.BatteryPresent == 0 || s.Rate == 0) return null;
        double watts = Math.Abs((double)s.Rate) / 1000.0;
        return state == BatteryState.Discharging ? -watts : watts;
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

    // The power-rate source. CallNtPowerInformation with SystemBatteryState fills SYSTEM_BATTERY_STATE, whose
    // Rate IS the battery's instantaneous power (the API's unit is milliwatts; docs call it a discharge rate,
    // but its sign varies, so the magnitude is used and the caller's State orients it). The Spare bytes are the
    // struct's own `BOOLEAN Spare1[3]` padding; naming them separately keeps the layout explicit.
    private const int SystemBatteryState = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_BATTERY_STATE
    {
        public byte AcOnLine, BatteryPresent, Charging, Discharging;
        public byte Spare0, Spare1, Spare2, Spare3;   // Spare1[3] plus the alignment pad before MaxCapacity
        public uint MaxCapacity, RemainingCapacity;
        public int Rate;                              // milliwatts
        public uint EstimatedTime, DefaultAlert1, DefaultAlert2;
    }

    [DllImport("powrprof.dll")]
    private static extern int CallNtPowerInformation(int informationLevel, nint inputBuffer, uint inputBufferLength,
                                                     out SYSTEM_BATTERY_STATE outputBuffer, uint outputBufferLength);
}
