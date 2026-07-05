using AcerHelper.Features;

namespace AcerHelper.Vendors.Acer;

/// <summary>
/// The standard Acer gaming performance-profile map (EC byte ↔ UI descriptor). Shared across the
/// whole Acer gaming line — Predator and Nitro use the same profile enum (verified on Nitro 18,
/// confirmed against Linuwu-Sense). The available SET is discovered at runtime from the EC
/// supported-mask (index 0x0A), so it is NOT per-model data.
/// </summary>
public static class AcerProfiles
{
    private sealed record Entry(byte Byte, string Name, ProfileKind Kind, AccentColor Accent);

    private static readonly Entry[] Table =
    [
        // Accents mirror the NitroSense lightbar palette: Eco/Quiet green (two shades so they stay
        // distinguishable), Balanced orange, Performance red, Turbo purple.
        new(0x06, "Eco",         ProfileKind.Eco,         new AccentColor(0x43, 0xA0, 0x47)),
        new(0x00, "Quiet",       ProfileKind.Quiet,       new AccentColor(0x1B, 0x5E, 0x20)),
        new(0x01, "Balanced",    ProfileKind.Balanced,    new AccentColor(0xF5, 0x7C, 0x00)),
        new(0x04, "Performance", ProfileKind.Performance, new AccentColor(0xD3, 0x2F, 0x2F)),
        new(0x05, "Turbo",       ProfileKind.Turbo,       new AccentColor(0x9C, 0x27, 0xB0))
    ];

    /// <summary>All standard profiles, display order.</summary>
    public static readonly IReadOnlyList<PerformanceProfile> All = Table.Select(Make).ToList();

    private static PerformanceProfile Make(Entry e) => new(e.Byte.ToString(), e.Name, e.Kind, e.Accent);

    public static PerformanceProfile ToDomain(byte b)
    {
        var e = Table.ToList().Find(e => e.Byte == b);
        return e == null ? new PerformanceProfile(b.ToString(), $"0x{b:X2}", ProfileKind.Other) : Make(e);
    }

    public static byte ToByte(PerformanceProfile p) => byte.Parse(p.Id);

    /// <summary>Profiles whose bit is set in the EC supported-mask (index 0x0A). 0 = unknown → all.</summary>
    public static IReadOnlyList<PerformanceProfile> FromMask(byte mask)
    {
        if (mask == 0) return All;
        return (from e in Table where (mask & (1 << e.Byte)) != 0 select Make(e)).ToList();
    }
}
