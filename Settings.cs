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
}

/// <summary>Port for persisting <see cref="Settings"/>. Implemented in Infrastructure.</summary>
public interface ISettingsStore
{
    Settings Load();
    void Save(Settings settings);
}
