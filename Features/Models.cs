namespace AcerHelper;

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

/// <summary>Live sensor readings. A field of -1 means unavailable/unsupported.</summary>
public struct SensorSnapshot
{
    public int CpuTempC;
    public int GpuTempC;
    public int CpuFanRpm;
    public int GpuFanRpm;
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
public sealed record RgbModeInfo(string Name, bool HasColor, bool HasSpeed, object Handle);

/// <summary>A single labelled choice with a stable id (for dropdowns whose set is vendor-defined).</summary>
public sealed record ChoiceOption(string Id, string DisplayName);
