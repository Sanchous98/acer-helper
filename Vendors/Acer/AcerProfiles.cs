namespace AcerHelper.Vendors.Acer;

/// <summary>
/// Acer performance / platform profile values, as written to / read from
/// SET/GET_GAMING_MISC_SETTING index 0x0B. Verified on Acer Nitro 18 (AN18-61).
/// </summary>
public enum AcerProfile : byte
{
    Quiet       = 0x00,
    Balanced    = 0x01,
    Performance = 0x04,
    Turbo       = 0x05,
    Eco         = 0x06,
}

/// <summary>Maps Acer profile bytes to the vendor-neutral <see cref="PerformanceProfile"/> the
/// app binds to (display order, names, accent colours, kind). The byte is carried as the
/// profile <c>Id</c>, so the controller can map back without a lookup table.</summary>
public static class AcerProfiles
{
    private sealed record Entry(string DisplayName, ProfileKind Kind, AccentColor Accent);

    // Display order matches the previous AcerProfileInfo.All.
    private static readonly (AcerProfile p, Entry e)[] Table =
    {
        (AcerProfile.Quiet,       new("Quiet",       ProfileKind.Quiet,       new(0x42, 0x85, 0xF4))),
        (AcerProfile.Balanced,    new("Balanced",    ProfileKind.Balanced,    new(0x2E, 0x7D, 0x32))),
        (AcerProfile.Performance, new("Performance", ProfileKind.Performance, new(0xF5, 0x7C, 0x00))),
        (AcerProfile.Turbo,       new("Turbo",       ProfileKind.Turbo,       new(0xD3, 0x2F, 0x2F))),
        (AcerProfile.Eco,         new("Eco",         ProfileKind.Eco,         new(0x00, 0x89, 0x7B))),
    };

    /// <summary>All Acer profiles as domain descriptors, in display order.</summary>
    public static readonly IReadOnlyList<PerformanceProfile> All =
        Table.Select(t => ToDomain(t.p)).ToList();

    /// <summary>Domain descriptor for an Acer profile byte.</summary>
    public static PerformanceProfile ToDomain(AcerProfile p)
    {
        foreach (var (cand, e) in Table)
            if (cand == p) return new PerformanceProfile(((byte)p).ToString(), e.DisplayName, e.Kind, e.Accent);
        return new PerformanceProfile(((byte)p).ToString(), $"0x{(byte)p:X2}", ProfileKind.Other);
    }

    /// <summary>The Acer byte encoded in a domain profile's Id.</summary>
    public static AcerProfile ToByte(PerformanceProfile profile) => (AcerProfile)byte.Parse(profile.Id);

    /// <summary>Profiles whose bit is set in the supported-profiles mask (index 0x0A).
    /// An all-zero mask means "unknown" → treat every profile as available.</summary>
    public static IReadOnlyList<PerformanceProfile> FromMask(byte mask)
    {
        if (mask == 0) return All;
        var list = new List<PerformanceProfile>();
        foreach (var (p, _) in Table)
            if ((mask & (1 << (byte)p)) != 0) list.Add(ToDomain(p));
        return list;
    }
}
