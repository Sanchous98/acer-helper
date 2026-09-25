using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// The ASUS Windows performance-profile port, behind the SAME Domain contract as the Linux/asusd path
// (IPowerProfiles + IProfileTraits) so profiles, traits, the tray and settings.json behave identically on both
// OSes.
//
// THE WIRE (sources in AsusAtk.cs): the ATK device `ASUS_WMI_DEVID_THROTTLE_THERMAL_POLICY` (0x00120075; the
// VivoBook/Zenbook variant is 0x00110019). Read = DSTS(device), write = DEVS(device, mode). The values are the
// firmware's own: 0 Balanced, 1 Turbo, 2 Silent (G-Helper PerformanceBalanced/Turbo/Silent).
//
// IDS AND KINDS MIRROR THE LINUX TABLE on purpose: Turbo carries the id the kernel calls "performance" and the
// Performance class, Silent the id "quiet" and the Quiet class, so a preset written on one OS reads on the
// other. There is no separate Turbo class here because ASUS has no Turbo profile — its top mode IS Performance.
//
// UNVERIFIED ON HARDWARE (see AsusAtk.cs).

/// <summary>The ASUS Windows profiles: three modes, mapped to the app's vocabulary.</summary>
internal sealed class AsusWindowsProfiles : IPowerProfiles, IProfileTraits
{
    private sealed record Row(int Mode, string Id, string Name, ProfileKind Kind, AccentColor Accent);

    private static readonly Row[] Table =
    [
        new(AsusAtk.ModeSilent,   "quiet",       "Quiet",       ProfileKind.Quiet,       new AccentColor(0x42, 0x85, 0xF4)),
        new(AsusAtk.ModeBalanced, "balanced",    "Balanced",    ProfileKind.Balanced,    new AccentColor(0x2E, 0x7D, 0x32)),
        new(AsusAtk.ModeTurbo,    "performance", "Performance", ProfileKind.Performance, new AccentColor(0xD3, 0x2F, 0x2F)),
    ];

    private readonly AsusAtkDevice _atk;
    private readonly uint _device;

    private AsusWindowsProfiles(AsusAtkDevice atk, uint device)
    {
        _atk = atk;
        _device = device;
    }

    /// <summary>Build the port, preferring the ROG device and falling back to the VivoBook one (or the reverse
    /// when the model is known Vivo). Null when neither responds.</summary>
    internal static AsusWindowsProfiles? TryCreate(AsusAtkDevice atk, bool vivo)
    {
        var first = vivo ? AsusAtk.ThermalPolicyVivo : AsusAtk.ThermalPolicy;
        var second = vivo ? AsusAtk.ThermalPolicy : AsusAtk.ThermalPolicyVivo;
        if (atk.Supported(first)) return new AsusWindowsProfiles(atk, first);
        return atk.Supported(second) ? new AsusWindowsProfiles(atk, second) : null;
    }

    /// <summary>True once one of the two devices answered (see <see cref="TryCreate"/>).</summary>
    public bool Available => true;

    public string? LastError { get; private set; }

    public IReadOnlyList<PerformanceProfile> All => Table.Select(r => new PerformanceProfile(r.Id, r.Name)).ToList();

    public IReadOnlyList<PerformanceProfile> Selectable() => All;

    /// <summary>The live mode, or null when the firmware reports a value this table does not name (the app then
    /// applies no presets rather than guessing a nearby mode).</summary>
    public PerformanceProfile? Current()
    {
        var value = _atk.Get(_device);
        return value >= 0 && Find(value) is { } row ? new PerformanceProfile(row.Id, row.Name) : null;
    }

    public bool Set(PerformanceProfile profile)
    {
        if (Find(profile.Id) is not { } row)
        {
            LastError = $"\"{profile.DisplayName}\" is not one of the machine's performance profiles";
            return false;
        }

        var result = _atk.Set(_device, row.Mode);
        if (result == 1) { LastError = null; return true; }

        LastError = result < 0
            ? "the machine did not answer the profile write"
            : $"the firmware refused the profile write (status {result})";
        return false;
    }

    public ProfileTraits Traits(PerformanceProfile profile)
    {
        var row = Table.FirstOrDefault(r => r.Id == profile.Id);
        return row == null ? ProfileTraits.Unknown : new ProfileTraits(row.Kind, row.Accent);
    }

    private static Row? Find(int mode) => Table.FirstOrDefault(r => r.Mode == mode);
    private static Row? Find(string id) => Table.FirstOrDefault(r => r.Id == id);
}
