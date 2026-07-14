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
        catch (JsonException)
        {
            // Corrupt content: set it aside instead of leaving it in place, where the next Save()
            // would silently overwrite it with the defaults we're about to return. The .bad copy
            // keeps the user's data recoverable; the rescue itself is best-effort.
            try { File.Move(FilePath, FilePath + ".bad", overwrite: true); } catch { }
        }
        catch { /* locked/unreadable — fall back to defaults */ }
        return new Settings();
    }

    public void Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Write-to-temp + rename, never truncate in place: Save() runs on every profile/fan/light
            // change, and a laptop is exactly the machine that loses power mid-write. flushToDisk
            // before the rename so the swap can't be reordered ahead of the data hitting disk
            // (the classic zero-length-file-after-crash failure); same-directory rename = same volume,
            // so the replace is atomic on both NTFS and POSIX.
            var tmp = FilePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs))
            {
                w.Write(JsonSerializer.Serialize(settings, SettingsJsonContext.Default.Settings));
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* best effort */ }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
internal partial class SettingsJsonContext : JsonSerializerContext;
