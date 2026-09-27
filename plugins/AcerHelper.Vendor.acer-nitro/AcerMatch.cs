using System.Text.Json;
using System.Text.Json.Nodes;
using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Vendor.AcerNitro;

/// <summary>
/// THE <c>ah_matches</c> DMI GATE (docs/vendor-plugins.md §3.2, §1.4) — pure, hardware-free and testable, so the
/// export thunk is only a marshal (the proof plugin's posture, §6 Phase 0 item 4).
///
/// THE LINE IS THE <c>acer-models.json</c> <c>Match</c> SUBSTRING SET. A plugin's identity is a MODEL LINE, not a
/// vendor (§1.4), and the only per-line data the tree records is that DB's <c>Match</c> substrings (the friendly
/// name and RGB layout are the other two fields). So the decision is exactly "does this descriptor's product name
/// contain one of my line's substrings" — the same test <c>AcerModels.Detect</c> uses to pick quirks
/// (<c>AcerModel.cs:53-62</c>), reused rather than re-implemented so the two cannot disagree.
///
/// CONFIDENCE IS THE HOST'S SELECTION KEY (§3.2) and the calibration is mechanical, per the doc's table:
/// <list type="bullet">
/// <item><b>0</b> — the manufacturer is not Acer: not this machine.</item>
/// <item><b>50</b> — Acer, but the product names no line entry: the default model row answered.</item>
/// <item><b>80</b> — the product contains a line entry's substring (the doc's "product substring").</item>
/// <item><b>100</b> — the product AND the board-product both carry the line substring, i.e. two independent DMI
/// fields agree on the line (the doc's "product + board, no ambiguity"). The current single-entry DB will not
/// usually produce this on real hardware — the board product is an ODM code, not the retail name — and that is
/// honest: 100 is reserved for the unambiguous case rather than granted for one field.</item>
/// </list>
/// </summary>
internal static class AcerMatch
{
    /// <summary>The decision for the four DMI fields (§3.2). A missing field is simply absent input; an empty
    /// <c>Match</c> set (the DB's <c>default</c> row) is "Acer, line unknown" and never a false substring hit —
    /// the same empty-pattern guard <c>AcerModels.Detect</c> relies on (<c>AcerModel.cs:55-59</c>).</summary>
    internal static (bool Match, int Confidence, string Reason) Decide(
        string? manufacturer, string? product, string? board, string? boardProduct)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)
            || !manufacturer.Contains("Acer", StringComparison.OrdinalIgnoreCase))
            return (false, 0, "manufacturer is not Acer");

        // Detect picks the entry OR the default; both carry a Match set, but only a real entry's is non-empty.
        // Reading it back is how "did a line entry match" is answered without a second DB walk.
        var model = AcerModels.Detect(product);
        var substrings = model.Match.Where(s => !string.IsNullOrEmpty(s)).ToArray();
        if (substrings.Length == 0)
            return (true, 50, "Acer manufacturer, line unknown");

        var p = product ?? string.Empty;
        var bp = boardProduct ?? string.Empty;
        var productHit = substrings.Any(s => p.Contains(s, StringComparison.OrdinalIgnoreCase));
        var boardHit = bp.Length > 0 && substrings.Any(s => bp.Contains(s, StringComparison.OrdinalIgnoreCase));

        if (productHit && boardHit)
            return (true, 100, "product and board product both name this line");
        if (productHit)
            return (true, 80, $"product '{p}' names this line");
        return (true, 50, "Acer manufacturer, line unknown");
    }

    /// <summary>The decision for a raw descriptor JSON string (§3.2). Malformed bytes are "not this machine",
    /// never a throw: the host treats a failed <c>ah_matches</c> as a discard (§4.3).
    /// The four keys are read case-insensitively so the host's camelCase wire form is all this relies on.</summary>
    internal static (bool Match, int Confidence, string Reason) DecideFromJson(string descriptorJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(descriptorJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (false, 0, "descriptor is not an object");
            return Decide(String(root, "manufacturer"), String(root, "product"),
                          String(root, "board"), String(root, "boardProduct"));
        }
        catch (JsonException)
        {
            return (false, 0, "malformed descriptor");
        }
    }

    /// <summary>The <c>product</c> DMI field of a descriptor, or null. Used by <c>ah_create</c> to pick the model
    /// quirks (the same product <c>ah_matches</c> saw), so the two phases agree on the machine's identity.</summary>
    internal static string? Product(string descriptorJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(descriptorJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? String(doc.RootElement, "product")
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The host's plugin API MAJOR from the <c>ah_create</c> handshake body (<c>{"abi":{"major":…}}</c>,
    /// §3.8.4 gate 3), or null when the body carries none (an older host: treated as compatible). Split out so the
    /// gate is testable without a thunk.</summary>
    internal static int? HostMajor(string descriptorJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(descriptorJson);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("abi", out var abi)
                && abi.ValueKind == JsonValueKind.Object
                && abi.TryGetProperty("major", out var major)
                && major.ValueKind == JsonValueKind.Number
                && major.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The <c>{"match":…,"confidence":…,"reason":"…"}</c> body (§3.2). Built with a node tree so the
    /// reason string cannot break the JSON, and AOT-safe (no reflection, §2.2).</summary>
    internal static string BuildResponse(bool match, int confidence, string reason) =>
        new JsonObject
        {
            ["match"] = match,
            ["confidence"] = confidence,
            ["reason"] = reason,
        }.ToJsonString();

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
