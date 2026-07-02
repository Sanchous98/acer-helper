using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcerHelper.Vendors.Acer;

/// <summary>
/// Per-model Acer quirks, loaded from config (<c>acer-models.json</c>) rather than hardcoded.
///
/// Most capabilities are PROBED at runtime by the composition root (RGB device present? EC
/// supported-profile mask? nullable WMI getters?), mirroring how G-Helper detects Asus features —
/// so unknown/future models work without a code change. This descriptor only carries what CANNOT
/// be probed: a friendly display name and the keyboard RGB layout (zone count + whether a lightbar
/// is present). Profiles and fan topology are NOT per-model on Acer (shared profile enum, dual
/// fan — confirmed against Linuwu-Sense), so they are not here.
/// </summary>
public sealed class AcerModel
{
    /// <summary>Case-insensitive substrings matched against the DMI product name.</summary>
    public string[] Match { get; set; } = [];
    public string Name { get; set; } = "Acer";
    public int Zones { get; set; } = 4;
    public bool Lightbar { get; set; } = true;
}

/// <summary>Root of acer-models.json: known models + the fallback used when none match.</summary>
public sealed class AcerModelConfig
{
    public List<AcerModel> Models { get; set; } = [];
    public AcerModel Default { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(AcerModelConfig))]
internal partial class AcerModelJsonContext : JsonSerializerContext;

/// <summary>Loads the model quirks (embedded default DB + optional user override) and matches by
/// DMI product name.</summary>
public static class AcerModels
{
    private static AcerModelConfig? _cache;

    /// <summary>Pick the quirks for a product name (manufacturer already known Acer). User config
    /// entries take precedence over the built-in DB; unmatched products use the default entry.</summary>
    public static AcerModel Detect(string? product)
    {
        var cfg = Load();
        var p = product ?? string.Empty;
        foreach (var m in cfg.Models)
            if (m.Match.Any(s => !string.IsNullOrEmpty(s) && p.Contains(s, StringComparison.OrdinalIgnoreCase)))
                return m;
        return cfg.Default;
    }

    private static AcerModelConfig Load()
    {
        if (_cache != null) return _cache;
        var cfg = LoadEmbedded() ?? new AcerModelConfig();
        MergeUserOverride(cfg);
        return _cache = cfg;
    }

    private static AcerModelConfig? LoadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var res = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("acer-models.json", StringComparison.OrdinalIgnoreCase));
            if (res == null) return null;
            using var stream = asm.GetManifestResourceStream(res);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize(reader.ReadToEnd(), AcerModelJsonContext.Default.AcerModelConfig);
        }
        catch { return null; }
    }

    // Optional user-editable override at the settings dir (%AppData%/AcerHelper or ~/.config/AcerHelper):
    // add or correct a model without rebuilding. Its models are checked before the built-ins.
    private static void MergeUserOverride(AcerModelConfig cfg)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcerHelper", "acer-models.json");
            if (!File.Exists(path)) return;
            var user = JsonSerializer.Deserialize(File.ReadAllText(path), AcerModelJsonContext.Default.AcerModelConfig);
            if (user == null) return;
            cfg.Models.InsertRange(0, user.Models);
            if (user.Default.Match.Length == 0 && user.Default.Name != "Acer") cfg.Default = user.Default;
        }
        catch { /* ignore a bad user config */ }
    }
}
