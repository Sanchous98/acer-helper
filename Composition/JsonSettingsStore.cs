using System.Text.Json;
using System.Text.Json.Serialization;
using AcerHelper;

namespace AcerHelper.Composition;

/// <summary>Persists <see cref="Settings"/> as JSON under the per-user app-data folder
/// (%AppData%\AcerHelper on Windows, ~/.config/AcerHelper on Linux). Source-generated
/// (de)serialization keeps it trimming/AOT-safe.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcerHelper", "settings.json");

    public Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.Settings) ?? new Settings();
        }
        catch { /* ignore corrupt/locked settings */ }
        return new Settings();
    }

    public void Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.Settings));
        }
        catch { /* best effort */ }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
internal partial class SettingsJsonContext : JsonSerializerContext;
