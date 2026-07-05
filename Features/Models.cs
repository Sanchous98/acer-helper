namespace AcerHelper.Features;

// The ubiquitous language of the app: features expressed as vendor- and OS-agnostic
// value objects. Infrastructure maps its own encodings to/from these at the boundary.

/// <summary>Neutral RGB triple (no dependency on System.Drawing or Avalonia).</summary>
public readonly record struct AccentColor(byte R, byte G, byte B);

/// <summary>Coarse class of a performance profile — drives the tray-icon colour and the
/// generic "toggle performance" hotkey semantics without naming any vendor profile.</summary>
public enum ProfileKind { Quiet, Eco, Balanced, Performance, Turbo, Other }

/// <summary>A performance/platform profile, as the app sees it. <paramref name="Id"/> is an
/// opaque, stable key the owning backend understands (e.g. an Acer EC byte).</summary>
public sealed record PerformanceProfile(string Id, string DisplayName, ProfileKind Kind, AccentColor? Accent = null);

/// <summary>Fan behaviour. Values are arbitrary to the app; a backend maps them to its own
/// encoding. (They coincide with Acer's WMI values, and are persisted in settings.)</summary>
public enum FanMode : byte
{
    Auto   = 1,
    Max    = 2,
    Custom = 3,
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
/// full-charge ÷ design capacity; <see cref="CycleCount"/> is often unsupported by the EC.</summary>
public sealed record BatteryInfoSnapshot
{
    public int Percent { get; init; } = -1;
    public BatteryState State { get; init; } = BatteryState.Unknown;
    public int HealthPercent { get; init; } = -1;
    public int CycleCount { get; init; } = -1;
}
