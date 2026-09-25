using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Vendors.Acer;

/// <summary>
/// The standard Acer gaming performance-profile map (EC byte ↔ UI descriptor ↔ kernel choice name). Shared
/// across the whole Acer gaming line — Predator and Nitro use the same profile enum (verified on Nitro 18,
/// confirmed against Linuwu-Sense). The available SET is discovered at runtime from the EC supported-mask
/// (index 0x0A), so it is NOT per-model data.
///
/// The <c>Choice</c> column is the second vocabulary for these same five profiles: the name mainline's
/// <c>acer-wmi</c> reports in <c>platform-profile-1/profile</c> once the module is loaded with
/// <c>predator_v4=1</c>. The two vocabularies do NOT agree, and one of the disagreements is a trap — see the
/// warning on the table. <see cref="ToChoiceName"/>/<see cref="FromChoiceName"/> are the only door between
/// them, deliberately, so the translation is written down once.
/// </summary>
public static class AcerProfiles
{
    private sealed record Entry(byte Byte, string Choice, string Name, ProfileKind Kind, AccentColor Accent, AccentColor Flash);

    private static readonly Entry[] Table =
    [
        // Choice = the name the kernel's platform-profile handler reports for this EC byte. acer_predator_v4_
        // platform_profile_{get,set} is a switch over the byte rather than a table of names, and this column is
        // that switch, transcribed (verified on AN18-61 against platform-profile-1/profile — the same five names
        // the hardware offers, and the same five bytes this table writes; see docs/acer-linux.md).
        //
        // THE TRAP: the kernel's word for byte 0x05 is "performance", and 0x05 is TURBO (108 W) — while 0x04,
        // the app's "Performance" (93 W), is "balanced-performance". So the obvious name-keyed lookup is wrong in
        // BOTH directions: reading sysfs "performance" as Performance reports the wrong mode (and re-sends the
        // wrong envelope through EcSyncedProfiles), and writing Performance by sending the token "performance"
        // asks the kernel for Turbo instead. No caller may match these words itself; ToChoiceName/FromChoiceName
        // exist so that the pairing lives here, next to the bytes, once.
        //
        // Accent = the UI accent (tray/highlight). Flash = the fixed colour the EC firmware paints the
        // "operating mode" indicator (lightbar + keyboard flash) for this profile — the only colours the
        // firmware accepts on the OPMODE write; sending anything else there reverts to amber. These are the
        // per-profile lightbar colours (verified on Nitro AN18-61 via the OPMODE HID report; see
        // docs/lighting-an18-61.md). Given as true RGB — EneHidController.SetProfileFlash serialises these to the
        // OPMODE wire order (B,G,R), which differs from the R,G,B order of the arbitrary-colour writes.
        new(0x06, "low-power",            "Eco",         ProfileKind.Eco,         new AccentColor(0x43, 0xA0, 0x47), new AccentColor(0x00, 0xDC, 0x10)),
        new(0x00, "quiet",                "Quiet",       ProfileKind.Quiet,       new AccentColor(0x1B, 0x5E, 0x20), new AccentColor(0xFF, 0xFF, 0xFF)),
        new(0x01, "balanced",             "Balanced",    ProfileKind.Balanced,    new AccentColor(0xF5, 0x7C, 0x00), new AccentColor(0xC7, 0xAE, 0x00)),
        new(0x04, "balanced-performance", "Performance", ProfileKind.Performance, new AccentColor(0xD3, 0x2F, 0x2F), new AccentColor(0xC7, 0x09, 0x2E)),
        new(0x05, "performance",          "Turbo",       ProfileKind.Turbo,       new AccentColor(0x9C, 0x27, 0xB0), new AccentColor(0xFF, 0x00, 0xC7))
    ];

    /// <summary>All standard profiles, display order.</summary>
    public static readonly IReadOnlyList<PerformanceProfile> All = Table.Select(Make).ToList();

    /// <summary>NitroSense parity: which of these profiles the Acer firmware makes available on each power
    /// source — on battery only Eco and Balanced, on AC only Quiet, Balanced, Performance and Turbo (Eco is
    /// absent there, exactly as NitroSense hides it). Declared here, beside the table, because it is the same
    /// vendor data as the kind and the colours; a port that does not implement <see cref="IProfileAvailability"/>
    /// is never asked. A profile this table cannot classify is available on neither source.</summary>
    public static bool IsAvailable(PerformanceProfile profile, bool onAc) => TraitsOf(profile).Kind switch
    {
        ProfileKind.Balanced                                          => true,
        ProfileKind.Eco                                               => !onAc,
        ProfileKind.Quiet or ProfileKind.Performance or ProfileKind.Turbo => onAc,
        _                                                             => false,
    };

    private static PerformanceProfile Make(Entry e) => new(e.Byte.ToString(), e.Name);

    /// <summary>The row's own kind and colours, for a profile this table produced — the lookup the app reaches
    /// the per-profile presentation through (<see cref="ProfileTraits"/>), since the profile record no longer
    /// carries any of it. A byte the table does not know, an id that is not a decimal EC byte at all (another
    /// backend's profile, e.g. PPD's <c>power-saver</c>), and a profile that never came from here are all
    /// <see cref="ProfileTraits.Unknown"/> rather than a nearby row — the same answer <see cref="ToDomain"/>
    /// gives an unknown byte, and for the same reason: inventing a class for a mode this table cannot name is
    /// how the wrong envelope gets sent.</summary>
    public static ProfileTraits TraitsOf(PerformanceProfile profile)
        => byte.TryParse(profile.Id, out var b) && Table.FirstOrDefault(e => e.Byte == b) is { } e
            ? new ProfileTraits(e.Kind, e.Accent, e.Flash)
            : ProfileTraits.Unknown;

    public static PerformanceProfile ToDomain(byte b)
    {
        var e = Table.ToList().Find(e => e.Byte == b);
        return e == null ? new PerformanceProfile(b.ToString(), $"0x{b:X2}") : Make(e);
    }

    public static byte ToByte(PerformanceProfile p) => byte.Parse(p.Id);

    /// <summary>Profiles whose bit is set in the EC supported-mask (index 0x0A). 0 = unknown → all.</summary>
    public static IReadOnlyList<PerformanceProfile> FromMask(byte mask)
    {
        if (mask == 0) return All;
        return (from e in Table where (mask & (1 << e.Byte)) != 0 select Make(e)).ToList();
    }

    /// <summary>The <c>platform_profile</c> choice name the kernel reports for this profile, or null when the
    /// profile is not one of the five in the table. That includes a profile whose <c>Id</c> is not a decimal EC
    /// byte at all — another backend's profile, e.g. PPD's <c>power-saver</c>: unlike
    /// <see cref="ToByte"/>, this is a query rather than a write, so "not ours" is an answer and not a throw.
    ///
    /// Note the direction of the trap once more: <c>ToChoiceName(Turbo)</c> is <c>"performance"</c>.</summary>
    public static string? ToChoiceName(PerformanceProfile profile)
        => byte.TryParse(profile.Id, out var b) ? Table.FirstOrDefault(e => e.Byte == b)?.Choice : null;

    /// <summary>The table profile a <c>platform_profile</c> token stands for, or null for any token the kernel
    /// may report that this table does not name.
    ///
    /// The null cases are not decoration, they are two measured ones: <c>"custom"</c> is what the LEGACY ACPI
    /// alias reports after a vendor handler writes (the alias is a fan-out — it reads back as
    /// <c>custom</c> whichever handler actually took the write, including our own acer-wmi one), and
    /// <c>"cool"</c> is a real fourth name generic sysfs handlers expose (see PowerProfiles.Linux.cs) that has
    /// no Acer byte. A null here means "the app cannot name the mode the hardware is in", which callers must
    /// treat as exactly that rather than as a nearby profile.</summary>
    public static PerformanceProfile? FromChoiceName(string choice)
        => Table.FirstOrDefault(e => e.Choice == choice) is { } e ? Make(e) : null;
}
