namespace AcerHelper;

/// <summary>User preferences. Persisted by an <see cref="ISettingsStore"/> (Infrastructure).
/// Values are vendor-neutral so they survive a hardware/vendor change.</summary>
public sealed class Settings
{
    public bool TurboToggles { get; set; }
    public bool Clamshell    { get; set; }

    // Last fan selection (restored into the UI on startup).
    public int FanMode { get; set; } = 1;    // 1=Auto, 2=Max, 3=Custom (FanMode enum)
    public int CpuFan  { get; set; } = 70;
    public int GpuFan  { get; set; } = 70;

    public int Bluelight { get; set; }       // 0=off, 1=Low, 2=Medium, 3=High, 4=Long-use

    // Last applied lighting (the app is the source of truth — the ENE controller is write-only and
    // can't report its current state). Restored into the UI and re-applied to the device on startup.
    public LightSettings Keyboard { get; set; } = new();
    public LightSettings Lightbar { get; set; } = new();
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
    public int Color       { get; set; } = 0xFF0000;
    public int[] ZoneColors { get; set; } = [];
}

/// <summary>Port for persisting <see cref="Settings"/>. Implemented in Infrastructure.</summary>
public interface ISettingsStore
{
    Settings Load();
    void Save(Settings settings);
}
