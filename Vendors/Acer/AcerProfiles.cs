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
    {
        new(0x00, "Quiet",       ProfileKind.Quiet,       new(0x42, 0x85, 0xF4)),
        new(0x01, "Balanced",    ProfileKind.Balanced,    new(0x2E, 0x7D, 0x32)),
        new(0x04, "Performance", ProfileKind.Performance, new(0xF5, 0x7C, 0x00)),
        new(0x05, "Turbo",       ProfileKind.Turbo,       new(0xD3, 0x2F, 0x2F)),
        new(0x06, "Eco",         ProfileKind.Eco,         new(0x00, 0x89, 0x7B)),
    };

    /// <summary>All standard profiles, display order.</summary>
    public static readonly IReadOnlyList<PerformanceProfile> All = Table.Select(Make).ToList();

    private static PerformanceProfile Make(Entry e) => new(e.Byte.ToString(), e.Name, e.Kind, e.Accent);

    public static PerformanceProfile ToDomain(byte b)
    {
        foreach (var e in Table) if (e.Byte == b) return Make(e);
        return new PerformanceProfile(b.ToString(), $"0x{b:X2}", ProfileKind.Other);
    }

    public static byte ToByte(PerformanceProfile p) => byte.Parse(p.Id);

    /// <summary>Profiles whose bit is set in the EC supported-mask (index 0x0A). 0 = unknown → all.</summary>
    public static IReadOnlyList<PerformanceProfile> FromMask(byte mask)
    {
        if (mask == 0) return All;
        var list = new List<PerformanceProfile>();
        foreach (var e in Table) if ((mask & (1 << e.Byte)) != 0) list.Add(Make(e));
        return list;
    }
}
