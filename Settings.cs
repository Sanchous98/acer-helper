namespace AcerHelper;

/// <summary>User preferences. Persisted by an <see cref="ISettingsStore"/> (Infrastructure).
/// Values are vendor-neutral so they survive a hardware/vendor change.</summary>
public sealed class Settings
{
    public bool TurboToggles { get; set; }
    public bool Clamshell    { get; set; }

    // Remembered performance mode per power source, re-applied when the source changes and at startup.
    // Each slot holds the base (non-Turbo) profile plus a Turbo flag, so Turbo is treated as a switch over
    // the base (never a standalone mode) — matching the "Turbo toggles" behaviour. BaseId also drives the
    // UI highlight while in Turbo, and survives a restart taken in Turbo.
    public ProfileMemory OnAc      { get; set; } = new();
    public ProfileMemory OnBattery { get; set; } = new();

    // Fan selection remembered PER performance mode (keyed by profile id; "default" when the device has no
    // profiles). Switching mode re-applies that mode's fan preset. A mode with no entry here = never set, so
    // its fans are left untouched (we can't read the hardware fan mode back to seed it).
    public Dictionary<string, FanPreset> FanPresets { get; set; } = new();

    public int Bluelight { get; set; }       // 0=off, 1=Low, 2=Medium, 3=High, 4=Long-use

    // Applied lighting remembered PER performance mode (same key scheme as FanPresets), each holding the
    // per-RGB-zone state. Switching mode re-applies that mode's lighting. The app is the source of truth —
    // the controllers are largely write-only and can't report their state — so this is restored on startup
    // and on mode switch.
    public Dictionary<string, LightPreset> LightPresets { get; set; } = new();

    // Vendor-specific device flags, keyed by an opaque string the owning backend defines (e.g. Acer's lightbar
    // "follows performance profile"). Kept as a neutral bag so Settings stays vendor-agnostic — on different
    // hardware the unused keys just sit inert. The key + its meaning live in the backend (Vendors/*); access
    // via LaptopService.GetDeviceFlag / SetDeviceFlag.
    public Dictionary<string, string> DeviceSettings { get; set; } = new();
}

/// <summary>One light's persisted state. Colours are packed 0xRRGGBB. <see cref="Configured"/> is
/// false until the user changes something, so a fresh install doesn't clobber the firmware default
/// on first launch.</summary>
public sealed class LightSettings
{
    public bool Configured { get; set; }
    public int EffectIndex { get; set; }
    public int Brightness  { get; set; } = 100;
    public int Speed       { get; set; } = 5;
    public int Direction   { get; set; } = 1;   // directional effects (Wave): 1 or 2
    public int Color       { get; set; } = 0xFF0000;
    public int[] ZoneColors { get; set; } = [];
}

/// <summary>Remembered performance mode for one power source: the base (non-Turbo) profile id, plus
/// whether Turbo was layered on top. Empty <see cref="BaseId"/> = nothing remembered yet.</summary>
public sealed class ProfileMemory
{
    public string BaseId { get; set; } = "";
    public bool Turbo { get; set; }
}

/// <summary>A performance mode's remembered fan selection: mode (1=Auto/2=Max/3=Custom) + custom speeds.
/// When <see cref="Curve"/> is on, the app ignores the fixed speeds and instead drives Custom speeds from a
/// duty% curve at fixed temperature anchors (Acer has no native fan curves — this emulates them via the
/// sensor loop; anchors live in LaptopService). <see cref="CpuCurve"/>/<see cref="GpuCurve"/> hold one duty%
/// per anchor; empty = use the built-in default ramp.</summary>
public sealed class FanPreset
{
    public int Mode { get; set; } = 1;
    public int Cpu  { get; set; } = 70;
    public int Gpu  { get; set; } = 70;

    // Per-fan curve (only meaningful in Custom mode): when UseCurve is on for a fan, the app drives that
    // fan's Custom speed from its curve (duty% per temperature anchor) instead of the fixed speed above.
    public bool  CpuUseCurve { get; set; }
    public bool  GpuUseCurve { get; set; }
    public int[] CpuCurve    { get; set; } = [];
    public int[] GpuCurve    { get; set; } = [];
}

/// <summary>A performance mode's remembered lighting: per-RGB-zone state, keyed by zone name.</summary>
public sealed class LightPreset
{
    public Dictionary<string, LightSettings> Zones { get; set; } = new();
}

/// <summary>Port for persisting <see cref="Settings"/>. Implemented in Infrastructure.</summary>
public interface ISettingsStore
{
    Settings Load();
    void Save(Settings settings);
}
