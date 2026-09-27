using System.Text.Json;
using System.Text.Json.Nodes;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Plugins.Abi;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Plugins.Adapters;

/// <summary>
/// The power-profiles adapter (docs/vendor-plugins.md §3.4, §3.5, §4.2): ONE object implementing
/// <see cref="IPowerProfiles"/>, <see cref="IProfileTraits"/> and <see cref="IProfileAvailability"/>, exactly as
/// the in-host <c>ProfilesPort</c> does (Infrastructure/Vendors/Generic/DelegatePorts.cs:59-64). The three
/// interfaces are one object because a backend's profile table is one table: the list the UI shows and the
/// classification it paints cannot come from two places, and the design cites that pairing as the shape to
/// mirror (§4.2).
///
/// WHAT IS MANIFEST AND WHAT IS A CALL. <see cref="All"/> and <see cref="Traits"/> are read from the decoded
/// probe manifest and never touch the plugin (§4.2: "All/Traits come from the manifest (immutable, no call)").
/// <see cref="Current"/>, <see cref="Set"/>, <see cref="Selectable"/> and <see cref="AvailableOn"/> are
/// <c>ah_invoke(Capability.Power, Operation.Power.*)</c> calls (§3.5), because they are the ones that have to ask
/// the hardware. That split is why the constructor takes the <see cref="PowerProfilesManifest"/> and not the
/// whole manifest: the caller has already decided this capability is present.
///
/// THE <c>kind</c> STRING IS PARSED HERE, IN THE HOST (§3.4, §6 "Domain neutrality"). The ABI carries the coarse
/// class as one of the <c>ProfileKind</c> names — "Quiet", "Eco", "Balanced", "Performance", "Turbo", "Other" —
/// and <see cref="ParseKind"/> maps that string domain onto the host enum
/// (Infrastructure/Vendors/Generic/ProfileKind.cs:28). An unrecognised string is <see cref="ProfileKind.Other"/>,
/// the enum's own "nobody classifies it" answer, never a nearby mode.
///
/// LASTERROR BELONGS TO THIS CALL (§4.2, LaptopService.cs:121-149). Every forwarding member replaces
/// <see cref="LastError"/> — with the refusal's own words when the plugin refused, with null when it did not — so
/// a caller can never read another call's failure. The error text is parsed straight out of the refusal body
/// rather than read off the session's own <c>LastError</c>, for the same reason: a concurrent call on another
/// adapter could otherwise overwrite it between the invoke and the read.
/// </summary>
internal sealed class PluginPowerProfiles : IPowerProfiles, IProfileTraits, IProfileAvailability
{
    private readonly IPluginSession _session;
    private readonly PerformanceProfile[] _all;
    private readonly Dictionary<string, PerformanceProfile> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProfileTraitsManifest> _traitsById = new(StringComparer.Ordinal);

    public PluginPowerProfiles(IPluginSession session, PowerProfilesManifest manifest)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(manifest);

        var all = new List<PerformanceProfile>(manifest.All?.Count ?? 0);
        if (manifest.All is { } declared)
            foreach (var profile in declared)
            {
                var mapped = new PerformanceProfile(profile.Id ?? "", profile.DisplayName ?? "");
                all.Add(mapped);
                // First declaration wins for a duplicated id: the manifest is display-ordered data, and a
                // duplicate is a plugin bug the lookup must not make order-dependent.
                _byId.TryAdd(mapped.Id, mapped);
            }
        _all = [.. all];

        if (manifest.Traits is { } traits)
            foreach (var trait in traits)
                if (trait.Id is { } id)
                    _traitsById[id] = trait;
    }

    public string? LastError { get; private set; }

    /// <summary>The full set as the manifest declared it, in display order — no call (§4.2).</summary>
    public IReadOnlyList<PerformanceProfile> All => _all;

    /// <summary><c>Op.Selectable</c> (§3.5): the plugin answers with the ids selectable right now. On a refusal
    /// the whole set is returned rather than none, matching the in-host Acer delegate's "cannot tell this moment,
    /// so do not hide everything" reading (AcerDevice.Windows.cs:118-122) — and <see cref="LastError"/> carries
    /// the reason.</summary>
    public IReadOnlyList<PerformanceProfile> Selectable()
    {
        var result = Invoke(Operation.Power.Selectable, PluginBodies.Empty);
        return result.Status == AbiStatus.Ok ? FilterByIds(PluginBodies.Ids(result.Body)) : _all;
    }

    /// <summary><c>Op.Current</c> (§3.5): <c>{"id":"1"}</c>, or <c>{"id":null}</c> when unreadable. An id the
    /// manifest does not list is treated as "unknown" (null) rather than invented.</summary>
    public PerformanceProfile? Current()
    {
        var result = Invoke(Operation.Power.Current, PluginBodies.Empty);
        if (result.Status != AbiStatus.Ok) return null;
        var id = PluginBodies.StringProperty(result.Body, "id");
        return id is not null && _byId.TryGetValue(id, out var profile) ? profile : null;
    }

    /// <summary><c>Op.Set</c> (§3.5): <c>{"id":"4"}</c>. Returns whether it took; a refusal sets
    /// <see cref="LastError"/> to the plugin's own reason.</summary>
    public bool Set(PerformanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Invoke(Operation.Power.Set, PluginBodies.Id(profile.Id)).Status == AbiStatus.Ok;
    }

    /// <summary><c>Op.AvailableOn</c> (§3.5): <c>{"onAc":true}</c>. The subset is the manifest's own list
    /// intersected with the returned ids, so an id the plugin names that it did not offer is dropped. On a
    /// refusal the whole set is returned, as in <see cref="Selectable"/>.</summary>
    public IReadOnlyList<PerformanceProfile> AvailableOn(bool onAc)
    {
        var request = new JsonObject { ["onAc"] = onAc }.ToJsonString();
        var result = Invoke(Operation.Power.AvailableOn, request);
        return result.Status == AbiStatus.Ok ? FilterByIds(PluginBodies.Ids(result.Body)) : _all;
    }

    /// <summary>The manifest's classification for a profile, or <see cref="ProfileTraits.Unknown"/> for a profile
    /// it does not classify — a foreign id, or one it has no trait row for. No call (§4.2).</summary>
    public ProfileTraits Traits(PerformanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _traitsById.TryGetValue(profile.Id, out var trait)
            ? new ProfileTraits(ParseKind(trait.Kind), ParseColor(trait.Accent), ParseColor(trait.Flash))
            : ProfileTraits.Unknown;
    }

    /// <summary>Map the manifest's <c>kind</c> STRING onto the host <see cref="ProfileKind"/>. The string domain
    /// is exactly the enum's own names, lower-cased here so a plugin's casing choice cannot change the meaning;
    /// anything else is <see cref="ProfileKind.Other"/>. Internal so the mapping is testable without a session.
    /// </summary>
    internal static ProfileKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "quiet"       => ProfileKind.Quiet,
        "eco"         => ProfileKind.Eco,
        "balanced"    => ProfileKind.Balanced,
        "performance" => ProfileKind.Performance,
        "turbo"       => ProfileKind.Turbo,
        "other"       => ProfileKind.Other,
        _             => ProfileKind.Other,
    };

    /// <summary>An <c>[r,g,b]</c> triple, or null when the trait carries no colour (or a malformed one). The
    /// components are clamped into a byte rather than allowed to wrap, so a wire value out of range cannot turn
    /// 300 into 44 (the failure Domain/Models.cs:48-50 names for casts).</summary>
    internal static AccentColor? ParseColor(int[]? rgb)
        => rgb is { Length: 3 } ? new AccentColor(Clamp(rgb[0]), Clamp(rgb[1]), Clamp(rgb[2])) : null;

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private IReadOnlyList<PerformanceProfile> FilterByIds(IReadOnlyList<string> ids)
    {
        var list = new List<PerformanceProfile>(ids.Count);
        foreach (var id in ids)
            if (_byId.TryGetValue(id, out var profile))
                list.Add(profile);
        return list;
    }

    /// <summary>Run one Power op and fold its status into <see cref="LastError"/>: null on Ok, the refusal's own
    /// words otherwise. See the class note on why the reason is parsed from THIS result.</summary>
    private PluginCallResult Invoke(uint op, string request)
    {
        var result = _session.Invoke(Capability.Power, op, request);
        LastError = result.Status == AbiStatus.Ok ? null : PluginBodies.ErrorOf(result);
        return result;
    }
}

/// <summary>
/// The small readers the capability adapters share over the JSON bodies of §3.5. It lives in this file because
/// task T5 owns no file of its own for it and every adapter consumes it; it is deliberately a plain
/// <see cref="JsonDocument"/>/<see cref="JsonObject"/> reader rather than a generated <c>JsonSerializerContext</c>
/// type, because the v1 bodies are a handful of scalars and the tree's generated context
/// (<c>PluginJsonContext</c>) is T1's file, which this task does not touch. Both paths are AOT-safe (§2.2, §3.3):
/// no reflection, no <c>RequiresDynamicCode</c>.
///
/// A malformed body is treated as ABSENT, never as a throw: a plugin that returns garbage is a broken plugin, and
/// the adapters' contract is to fall back to the port's "unreadable" value rather than take the host down (§4.3).
/// </summary>
internal static class PluginBodies
{
    /// <summary>The conventional no-payload request body (§3.5).</summary>
    public const string Empty = "{}";

    /// <summary>The <c>{"id":"…"}</c> request body several ops take (Power.Set, GpuMux.Request, …).</summary>
    public static string Id(string id) => new JsonObject { ["id"] = id }.ToJsonString();

    /// <summary>Pull <c>error</c> out of a <c>{"error":"…"}</c> refusal body (the <c>(ok, error)</c> port shape,
    /// AbiStatus.Refused). Null when the body is absent or not that shape.</summary>
    public static string? ErrorOf(PluginCallResult result)
    {
        if (string.IsNullOrEmpty(result.Body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(result.Body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e)
                && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The string value of a top-level property, or null when the body is absent, malformed, or the
    /// property is not a string.</summary>
    public static string? StringProperty(string? body, string name)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The integer value of a top-level property, or <paramref name="fallback"/>.</summary>
    public static int IntProperty(string? body, string name, int fallback)
    {
        if (string.IsNullOrEmpty(body)) return fallback;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var number)
                ? number
                : fallback;
        }
        catch (JsonException) { return fallback; }
    }

    /// <summary>The boolean value of a top-level property, or <paramref name="fallback"/>.</summary>
    public static bool BoolProperty(string? body, string name, bool fallback)
    {
        if (string.IsNullOrEmpty(body)) return fallback;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;
        }
        catch (JsonException) { return fallback; }
    }

    /// <summary>The <c>{"ids":[…​]}</c> string array several Power ops answer with (§3.5), empty on absence or
    /// malformation.</summary>
    public static IReadOnlyList<string> Ids(string? body)
    {
        if (string.IsNullOrEmpty(body)) return [];
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ids", out var ids)
                || ids.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<string>(ids.GetArrayLength());
            foreach (var element in ids.EnumerateArray())
                if (element.ValueKind == JsonValueKind.String && element.GetString() is { } id)
                    list.Add(id);
            return list;
        }
        catch (JsonException) { return []; }
    }
}
