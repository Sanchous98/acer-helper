namespace AcerHelper;

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

public static class AcerProfileInfo
{
    /// <summary>Display order for the UI.</summary>
    public static readonly AcerProfile[] All =
    {
        AcerProfile.Quiet,
        AcerProfile.Balanced,
        AcerProfile.Performance,
        AcerProfile.Turbo,
        AcerProfile.Eco,
    };

    public static string DisplayName(AcerProfile p) => p switch
    {
        AcerProfile.Quiet       => "Quiet",
        AcerProfile.Balanced    => "Balanced",
        AcerProfile.Performance => "Performance",
        AcerProfile.Turbo       => "Turbo",
        AcerProfile.Eco         => "Eco",
        _                       => $"0x{(byte)p:X2}",
    };

    /// <summary>True if the supported-profiles bitmask (index 0x0A) includes this profile.</summary>
    public static bool IsSupported(byte mask, AcerProfile p) => (mask & (1 << (byte)p)) != 0;
}
