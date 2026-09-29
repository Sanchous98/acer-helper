namespace AcerHelper.Domain;

/// <summary>The fan axis as a mode leaves it: which behaviour that mode is in, and each fan's own data. The
/// DOMAIN's vocabulary for the pair, and deliberately not the stored preset's field layout — a fan's data is
/// <see cref="FanSettings"/> (introduced for exactly this neutrality), and the halves are named CPU and GPU
/// only in the STORED form (Infrastructure/Composition/Settings.cs), which one reader translates
/// (<c>LaptopService.FanSettingsOf</c>).
///
/// It exists because a re-apply has to REPORT what it applied to the calling site's UI pass, and the report
/// crosses into Application — which may not name the container, because the container is Infrastructure
/// (<c>ArchitectureMapTests.ApplicationPointsAtDomainNotInfrastructure</c>). A domain value is what can cross,
/// and this one is not a second spelling of the preset: the mode is the domain's <see cref="FanMode"/> rather
/// than the stored integer, and each half is the type the fan models already take.</summary>
public readonly record struct FanAxisState(FanMode Mode, FanSettings Cpu, FanSettings Gpu);

/// <summary>The fixed GPU power envelope rows the EC exposes on models that route TGP through the EC HID
/// channel (measured on the Nitro AN18-61 — see docs/power-an18-61.md). These are the ONLY selectable power
/// levels: the EC holds a five-row table and there is no arbitrary wattage on this platform. The names are the
/// app's own profile vocabulary rather than the EC's wire bytes; the encoding lives in
/// <c>AcerEcHidController.ModeForLevel</c>.
///
/// IT LIVES IN THE DOMAIN because a chosen level crosses the Application boundary as a value
/// (<see cref="GpuAxisState.Power"/>), exactly as the fan mode and the clock offsets do — the layer that maps it
/// to a byte, and the layer that owns the EC channel, both sit above Domain.
///
/// THE PERSISTED ENCODING IS THE ENUM'S OWN NUMBER, not the EC byte, and the two must not be conflated: the EC
/// byte is an Acer wire fact and lives with the controller, while this number is settings.json's shape. They
/// happen to agree today for the four rows, which is exactly why the distinction is stated here rather than
/// left to coincidence — a future reorder of this enum must not move the EC envelope.
///
/// FOUR ROWS, NOT FIVE: the EC's fifth usage row (Eco, byte 4) enforces the same envelope as Quiet (both ~77 W
/// measured), so offering it as a separate level would be a selector entry that moves nothing. It is left out of
/// this axis deliberately; the profile axis keeps its own low-power class, which is a different surface and is
/// classified above Domain. A persisted 4 from an older build therefore names no level here and reads as "follow
/// the profile" — the same posture the schema already takes for any value this build no longer offers.</summary>
public enum GpuPowerLevel
{
    Turbo,
    Performance,
    Balanced,
    Quiet,
}

/// <summary>One selectable GPU power level as the EC channel offers it: the level, the localization key for its
/// row name, and the steady dGPU limit it was measured at (docs/power-an18-61.md §"Mode byte → measured dGPU
/// power"). The wattage is VENDOR data — measured, not computed — so it is declared by the port that owns the
/// channel rather than by the UI, and it is a display figure only: the write carries the level, never a
/// number.</summary>
public sealed record GpuPowerOption(GpuPowerLevel Level, string LabelKey, int Watts);

/// <summary>The reading of the PERSISTED GPU-power field — the <c>int?</c> a <c>GpuOcPreset</c> holds — into the
/// domain enum, and the reason it is a function rather than a cast: a cast is total, so a hand-edited or older
/// settings.json holding 7 would enter the model as a level nobody offered and be written to the EC as an
/// out-of-range row the firmware silently ignores. The exact shape <see cref="FanModes.FromStored"/> has on the
/// fan axis, and for the same reason.
///
/// NULL AND 0 ARE DIFFERENT ONES: null is "no override — the mode follows the performance profile", the default
/// for a file that predates this field, while 0 is Turbo as an EXPLICIT choice. The distinction is the whole
/// point of the axis, so an absent field and a Turbo choice must never collapse into one another — which is why
/// the persisted form is nullable rather than a 0-sentinel.
///
/// A value outside the enum names no level and reads as null — "follow the profile" — NOT as an error, because a
/// file that merely carries a value this build no longer offers must still load the rest of the user's settings
/// (the same posture <c>JsonUnmappedMemberHandling.Skip</c> takes at the JSON layer).</summary>
public static class GpuPowerLevels
{
    /// <summary>Every level, most power first — the EC's own row order, which the UI list and the persisted
    /// table both follow. Declared here so a caller needing "all the levels in order" does not restate the
    /// enum's order a second time.</summary>
    public static readonly IReadOnlyList<GpuPowerLevel> All =
        [GpuPowerLevel.Turbo, GpuPowerLevel.Performance, GpuPowerLevel.Balanced, GpuPowerLevel.Quiet];

    /// <summary>The level <paramref name="stored"/> names, or null when it names none — no override (the field
    /// absent), or a value the enum does not define.</summary>
    public static GpuPowerLevel? FromStored(int? stored)
        => stored is { } s && Enum.IsDefined((GpuPowerLevel)s) ? (GpuPowerLevel)s : null;

    /// <summary>The persisted form of <paramref name="level"/>: its own number, or null for "follow the
    /// profile". The encoding is the enum's number and deliberately NOT the EC byte — the byte is an Acer wire
    /// fact and lives with the controller (see <c>AcerEcHidController.ModeForLevel</c>).</summary>
    public static int? ToStored(GpuPowerLevel? level) => (int?)level;
}

/// <summary>The GPU axis as a mode leaves it: the two clock offsets in MHz plus the mode's chosen POWER level
/// (<see cref="GpuPowerLevel"/>), or null when the envelope follows the performance profile — the default, and
/// exactly the old behaviour. It crosses the same border as <see cref="FanAxisState"/> and for the same reason.
///
/// <see cref="Power"/> is nullable rather than a sentinel member because "not overridden" is a genuine third
/// state: the envelope is not stock and not a chosen row, it is the profile's own class-derived row, which only
/// the layer that can classify the profile can produce.
///
/// THE THIRD MEMBER HAS A DEFAULT SO THE CLOCK-ONLY CALL SITES KEEP COMPILING and keep meaning what they always
/// meant: <c>new GpuAxisState(core, mem)</c> is "follow the profile", which is what every such site wanted
/// before this axis grew.</summary>
public readonly record struct GpuAxisState(int Core, int Mem, GpuPowerLevel? Power = null)
{
    /// <summary>True when no level is pinned and the EC envelope is the performance profile's own row. The
    /// explicit form is a <see cref="Power"/> value.</summary>
    public bool FollowsProfile => Power is null;
}
