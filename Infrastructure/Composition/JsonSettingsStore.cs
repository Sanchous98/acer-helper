using System.Text.Json;
using System.Text.Json.Serialization;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Composition;

/// <summary>Persists <see cref="Settings"/> as JSON under the per-user app-data folder
/// (%AppData%\AcerHelper on Windows, ~/.config/AcerHelper on Linux). Source-generated
/// (de)serialization keeps it trimming/AOT-safe.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string filePath;

    /// <summary>The real location. Kept as the parameterless constructor so the composition root reads
    /// <c>new JsonSettingsStore()</c> and nothing about production changes.</summary>
    public JsonSettingsStore() : this(DefaultFilePath) { }

    /// <summary>Point the store somewhere else — a scratch directory, in practice. This seam exists because
    /// <see cref="Load"/> MOVES a corrupt file aside, so pointing a test of that path at the real location
    /// would consume the user's actual settings.json; that is precisely why this file had no coverage. The
    /// path is a constructor argument rather than a constant for the same reason the publication delegate in
    /// <c>OptionsAssembler</c> is one: the behaviour worth testing is unreachable while the destination is
    /// fixed at a compile-time location.</summary>
    public JsonSettingsStore(string filePath) => this.filePath = filePath;

    private static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcerHelper", "settings.json");

    /// <summary>Read the file and build the session's model: the set of options this machine declares
    /// (<paramref name="declaredSettings"/>, which the service hands over from the backend's probe) plus whatever
    /// the file persisted. The two halves come from different places and only THIS call has both, which is why
    /// the set is a parameter here rather than a property of the store — the store holds no part of the model.
    ///
    /// The file itself is read through <see cref="Settings"/>'s parameterless constructor, which is the shape the
    /// file has: settings.json says what the user chose, never what the machine has.</summary>
    public Settings Load(IReadOnlyList<SettingDeclaration> declaredSettings)
    {
        try
        {
            if (File.Exists(filePath))
                return new Settings(declaredSettings,
                                    JsonSerializer.Deserialize(File.ReadAllText(filePath), SettingsJsonContext.Default.Settings));
        }
        catch (JsonException)
        {
            // Corrupt content: set it aside instead of leaving it in place, where the next Save()
            // would silently overwrite it with the defaults we're about to return. The .bad copy
            // keeps the user's data recoverable; the rescue itself is best-effort.
            try { File.Move(filePath, filePath + ".bad", overwrite: true); } catch { }
        }
        catch { /* locked/unreadable — fall back to defaults */ }
        return new Settings(declaredSettings);
    }

    public void Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            // Write-to-temp + rename, never truncate in place: Save() runs on every profile/fan/light
            // change, and a laptop is exactly the machine that loses power mid-write. flushToDisk
            // before the rename so the swap can't be reordered ahead of the data hitting disk
            // (the classic zero-length-file-after-crash failure); same-directory rename = same volume,
            // so the replace is atomic on both NTFS and POSIX.
            var tmp = filePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs))
            {
                w.Write(JsonSerializer.Serialize(settings, SettingsJsonContext.Default.Settings));
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, filePath, overwrite: true);
        }
        catch { /* best effort */ }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
internal partial class SettingsJsonContext : JsonSerializerContext;
