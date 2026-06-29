using AcerHelper.Features;

namespace AcerHelper.Os;

/// <summary>
/// Vendor-agnostic battery telemetry. The common logic — caching the slow-changing health and cycle
/// count once at construction, and assembling the live snapshot — lives here; the OS data sources
/// (Windows WMI + power status, Linux /sys/class/power_supply) are the partial methods in the
/// matching BatteryInfo.*.cs file.
/// </summary>
public sealed partial class BatteryInfo : IBatteryInfo
{
    private readonly int _health;   // full / design * 100, -1 if unknown
    private readonly int _cycles;   // -1 if unsupported

    private BatteryInfo(int health, int cycles) { _health = health; _cycles = cycles; }

    /// <summary>Probe for a battery; null on a desktop / when none is present.</summary>
    public static BatteryInfo? TryCreate()
    {
        if (!HasBattery()) return null;
        var (health, cycles) = ReadStatic();
        return new BatteryInfo(health, cycles);
    }

    public BatteryInfoSnapshot Read()
    {
        var (percent, state) = ReadLive();
        return new BatteryInfoSnapshot
        {
            Percent = percent,
            State = state,
            HealthPercent = _health,
            CycleCount = _cycles,
        };
    }

    // OS-specific data sources:
    private static partial bool HasBattery();
    private static partial (int Health, int Cycles) ReadStatic();
    private static partial (int Percent, BatteryState State) ReadLive();
}
