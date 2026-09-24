namespace AcerHelper.Domain;

// The ubiquitous language of the app: features expressed as vendor- and OS-agnostic
// value objects. Infrastructure maps its own encodings to/from these at the boundary.

/// <summary>Neutral RGB triple (no dependency on System.Drawing or Avalonia).</summary>
public readonly record struct AccentColor(byte R, byte G, byte B);

/// <summary>A performance/platform profile, as the app sees it. <paramref name="Id"/> is an
/// opaque, stable key the owning backend understands (e.g. an Acer EC byte), and <paramref name="DisplayName"/>
/// is the label handed in from outside.
///
/// THAT IS THE WHOLE RECORD, and it is the owner's criterion for this layer applied literally: "только
/// значения, важные для логики" — only values the logic depends on. The class a profile belongs to and the
/// colours it is painted with are the BACKEND's knowledge, and nothing below Infrastructure ever reads them:
/// every branch on the class is Infrastructure's (the Turbo switch, the EC usage-mode byte, the vendor tables)
/// or the UI's (the segment colours, the Turbo row), and every reader of a colour paints it. Both left on
/// 2026-09-22 — the class as the enum it always was, plus the per-backend lookup that answers with it, under
/// Infrastructure/Vendors/Generic/; the colours stayed in the vendor tables they were already written in. That
/// file's own docstring records the decision, and the type is deliberately not spelled by name here: the guard
/// in tests/AcerHelper.Tests/DomainNeutralityTests is a source-text rule, so this layer must not name it at all.</summary>
public sealed record PerformanceProfile(string Id, string DisplayName);

/// <summary>Fan behaviour. Values are arbitrary to the app; a backend maps them to its own
/// encoding. (They coincide with Acer's WMI values, and are persisted in settings.)</summary>
public enum FanMode : byte
{
    Auto   = 1,
    Max    = 2,
    Custom = 3,
}

/// <summary>The one conversion from the PERSISTED form of <see cref="FanMode"/> — the <c>int</c> a preset holds —
/// into the enum, and the reason it is a function rather than a cast.
///
/// A cast is what the tree used, at three sites in Infrastructure/Composition/LaptopService.Fans.cs, and it is
/// not a conversion: <c>(FanMode)</c> of an <c>int</c> is legal for EVERY value of that <c>int</c>, including
/// the ones no <see cref="FanMode"/> names. A stored 7 therefore entered the model as a mode, and what happened
/// next depended on the platform: the Windows backend shifts it straight into the WMI argument
/// (<c>AcerDevice.Windows.cs</c>, <c>SetFanMode</c> — the behaviour byte the EC is asked for), while the Linux
/// backend's switch treats every unlisted value as Custom and writes nothing. Neither is a decision anybody
/// made; both are what a cast does. <see cref="IsDefined"/> is the door an undefined value cannot come through,
/// so no caller holds a mode the enum does not name.
///
/// IT IS NOT A VALIDATING FACTORY OVER A NEW TYPE, deliberately: the persisted value is an <c>int</c> and stays
/// one — settings.json's shape is a compatibility surface — so what the domain offers is the reading of it.
///
/// A value of 300 is refused by the same test that refuses 7, and that is the point of asking before casting:
/// <see cref="FanMode"/>'s underlying type is <c>byte</c>, so a cast would wrap 300 to 44 first and the check
/// would then be asked about a different number than the file holds.</summary>
public static class FanModes
{
    /// <summary>The mode <paramref name="stored"/> names, or null when it names none — a value outside the
    /// enum's own set, which is what a hand-edited or older settings.json can hold. The caller decides what to
    /// do with "none"; the reading itself refuses to invent a mode.</summary>
    public static FanMode? FromStored(int stored)
        => IsDefined(stored) ? (FanMode)stored : null;

    private static bool IsDefined(int stored)
        => stored is (int)FanMode.Auto or (int)FanMode.Max or (int)FanMode.Custom;
}

/// <summary>Which fan controls a backend offers (Auto is always implied).</summary>
public sealed record FanCapability(bool HasMax, bool HasCustom, bool HasGpuFan);

/// <summary>One fan's live reading. <paramref name="Rpm"/> of -1 means the speed is unreadable.</summary>
public readonly record struct FanReading(string Label, int Rpm);

/// <summary>Live sensor readings. Temperatures of -1 mean unavailable/unsupported; <see cref="Fans"/>
/// lists 0..N fans (a laptop typically has 1–3), each labelled, in display order.</summary>
public sealed record SensorSnapshot
{
    public int CpuTempC { get; init; } = -1;
    public int GpuTempC { get; init; } = -1;
    public IReadOnlyList<FanReading> Fans { get; init; } = [];
}

/// <summary>Generic actions a special key can trigger. A backend's hotkey source maps its
/// physical keys (e.g. Acer Turbo/Nitro) to these — the app decides what each action does.</summary>
public enum HotkeyAction
{
    /// <summary>Cycle profiles, or toggle the top profile (per user setting).</summary>
    TogglePerformance,
    /// <summary>Show/hide the main window.</summary>
    ToggleWindow,
}

/// <summary>A lighting effect/mode the UI can present, with its capability flags. The
/// vendor-specific encoding (mode byte, effect flag, …) is carried opaquely in
/// <paramref name="Handle"/> and resolved back by the owning lighting backend.</summary>
public sealed record RgbModeInfo(string Name, bool HasColor, bool HasSpeed, object Handle, bool HasDirection = false);

/// <summary>A single labelled choice with a stable id (for dropdowns whose set is vendor-defined).</summary>
public sealed record ChoiceOption(string Id, string DisplayName);

/// <summary>Charge/discharge state of the battery.</summary>
public enum BatteryState { Unknown, Charging, Discharging, Idle }

/// <summary>Live battery readings. -1 means unknown/unsupported. <see cref="HealthPercent"/> is
/// full-charge ÷ design capacity; <see cref="CycleCount"/> is often unsupported by the EC.
///
/// <see cref="PowerWatts"/> is the one field that is NULLABLE rather than -1-sentinel, and that is deliberate:
/// the integer fields above can spare -1 because no reading of theirs is ever negative, but a power rate is
/// signed by nature — -1 W is a perfectly plausible draw — so a sentinel there would collide with a real
/// measurement. Null is a third value the others do not have ("this battery reports no rate"), which is a
/// different fact from zero watts.</summary>
public sealed record BatteryInfoSnapshot
{
    public int Percent { get; init; } = -1;
    public BatteryState State { get; init; } = BatteryState.Unknown;
    public int HealthPercent { get; init; } = -1;
    public int CycleCount { get; init; } = -1;

    /// <summary>Live power flow through the battery, in watts, SIGNED: positive while the battery takes
    /// energy in (charging from the wall), negative while it gives energy out (the machine running on the
    /// battery). Null when this OS/firmware reports no rate for this battery — a different fact from zero.
    /// The encoding on each wire (Windows milliwatts, Linux microwatts) is the backend's; this is the neutral
    /// value it maps to.</summary>
    public double? PowerWatts { get; init; }
}
