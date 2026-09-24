using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

/// <summary>
/// Vendor-agnostic battery telemetry. The common logic — caching the slow-changing health and cycle
/// count once at construction, and assembling the live snapshot — lives here; the OS data sources
/// (Windows WMI + power status, Linux /sys/class/power_supply) are the partial methods in the
/// matching BatteryInfo.*.cs file.
///
/// It is no longer a port: the battery object holds this instance's <see cref="Read"/> as its telemetry op
/// (Domain/Battery.cs), because "how this machine reads the battery" and "what else this battery can do" are
/// one subject and the object is where that is stated.
/// </summary>
public sealed partial class BatteryInfo
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
            PowerWatts = ReadPowerWatts(state),
        };
    }

    // OS-specific data sources:
    private static partial bool HasBattery();
    private static partial (int Health, int Cycles) ReadStatic();
    private static partial (int Percent, BatteryState State) ReadLive();

    /// <summary>The battery's live power rate for the direction <paramref name="state"/> names (positive =
    /// charging, negative = discharging, null = this OS reports none). Taken as an argument rather than
    /// re-derived because the sign is the DOMAIN's convention (Domain/Models.cs) while each wire has its own
    /// (Windows milliwatts, Linux microwatts) — the OS half maps its magnitude and lets the caller's state
    /// orient it, so one place decides what "in" and "out" mean.</summary>
    private static partial double? ReadPowerWatts(BatteryState state);
}
