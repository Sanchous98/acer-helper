namespace AcerHelper.Infrastructure.Vendors.Asus;

// The ASUS model quirks table — the same shape as Acer's `AcerModel`/`AcerModels`, kept deliberately small.
//
// WHAT ACTUALLY NEEDS A QUIRK HERE, and why it is only the Vivo flag: every feature on the Windows ATK path is
// CAPABILITY-PROBED (a `DSTS` reply with the presence bit set), so most device differences resolve themselves.
// The one thing a probe cannot decide is which of two equivalent device banks to PREFER — the ROG ids
// (`0x00120075`/`0x00090016`) and the VivoBook/Zenbook ids (`0x00110019`/`0x00090026`). Both are still tried;
// this flag only orders them, so a wrong entry costs one extra probe rather than a wrong write.
//
// Unlike `acer-models.json` there is no embedded JSON override here yet: the table is code-resident because it
// carries no RGB layout or other per-model data. If a model ever needs an id that is neither bank, it is added
// with its source (never invented) and this table is where it goes.

/// <summary>Per-model ASUS quirks. Matching is case-insensitive substring against the DMI product name.</summary>
public sealed class AsusModel
{
    /// <summary>Case-insensitive substrings matched against the DMI product name.</summary>
    public string[] Match { get; init; } = [];
    public string Name { get; init; } = "ASUS";

    /// <summary>Prefer the VivoBook/Zenbook device bank (0x0011xxxx/0x00090026) over the ROG one. A preference
    /// only: both banks are probed either way.</summary>
    public bool Vivo { get; init; }
}

/// <summary>Loads/selects the quirks for a product name.</summary>
public static class AsusModels
{
    private static readonly AsusModel[] Table =
    [
        new() { Match = ["VivoBook", "Vivobook", "ZenBook", "Zenbook", "ASUSLaptop"], Name = "ASUS VivoBook/ZenBook", Vivo = true },
        new() { Match = ["ROG", "TUF"], Name = "ASUS ROG/TUF" },
    ];

    private static readonly AsusModel Default = new() { Name = "ASUS" };

    /// <summary>The quirks for a DMI product name. An empty match list never shadows the fallback, and an empty
    /// product name simply takes the default.</summary>
    public static AsusModel Detect(string? product)
    {
        var p = product ?? string.Empty;
        foreach (var model in Table)
            if (model.Match.Any(s => !string.IsNullOrEmpty(s) && p.Contains(s, StringComparison.OrdinalIgnoreCase)))
                return model;
        return Default;
    }
}
