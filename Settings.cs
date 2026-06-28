using System.Text.Json;

namespace AcerHelper;

/// <summary>User preferences persisted to %AppData%\AcerHelper\settings.json.</summary>
public sealed class Settings
{
    public bool TurboToggles { get; set; }
    public bool Clamshell    { get; set; }

    // Last fan selection (restored into the UI on startup).
    public int FanMode { get; set; } = 1;    // 1=Auto, 2=Max, 3=Custom (FanMode enum)
    public int CpuFan  { get; set; } = 70;
    public int GpuFan  { get; set; } = 70;

    public int Bluelight { get; set; } = 0;  // 0=off, 1=Low, 2=Medium, 3=High, 4=Long-use

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcerHelper", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (System.IO.File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* ignore corrupt/locked settings */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            System.IO.File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
