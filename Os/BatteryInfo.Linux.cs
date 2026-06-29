using System.IO;

namespace AcerHelper.Os;

// Linux source: /sys/class/power_supply/BAT*. capacity/status for the live reading;
// charge_full(_design) or energy_full(_design) for health; cycle_count when present.
public sealed partial class BatteryInfo
{
    private static string? BatDir()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories("/sys/class/power_supply"))
                if (Path.GetFileName(d).StartsWith("BAT", StringComparison.Ordinal)) return d;
        }
        catch { /* none */ }
        return null;
    }

    private static partial bool HasBattery() => BatDir() != null;

    private static partial (int Health, int Cycles) ReadStatic()
    {
        var dir = BatDir();
        if (dir == null) return (-1, -1);
        int full   = ReadInt(dir, "charge_full")        ?? ReadInt(dir, "energy_full")        ?? -1;
        int design = ReadInt(dir, "charge_full_design") ?? ReadInt(dir, "energy_full_design") ?? -1;
        int health = design > 0 && full > 0 ? Math.Min(100, (int)((long)full * 100 / design)) : -1;
        int cycles = ReadInt(dir, "cycle_count") ?? -1;
        return (health, cycles > 0 ? cycles : -1);
    }

    private static partial (int Percent, BatteryState State) ReadLive()
    {
        var dir = BatDir();
        if (dir == null) return (-1, BatteryState.Unknown);
        int percent = ReadInt(dir, "capacity") ?? -1;
        BatteryState state = (ReadText(dir, "status") ?? "") switch
        {
            "Charging"     => BatteryState.Charging,
            "Discharging"  => BatteryState.Discharging,
            "Full"         => BatteryState.Idle,
            "Not charging" => BatteryState.Idle,
            _              => BatteryState.Unknown,
        };
        return (percent, state);
    }

    private static int? ReadInt(string dir, string file) => int.TryParse(ReadText(dir, file), out var v) ? v : null;

    private static string? ReadText(string dir, string file)
    {
        try { var p = Path.Combine(dir, file); return File.Exists(p) ? File.ReadAllText(p).Trim() : null; }
        catch { return null; }
    }
}
