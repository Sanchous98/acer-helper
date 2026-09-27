using System.Text.Json.Nodes;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Plugins.Abi;

namespace AcerHelper.Infrastructure.Plugins.Adapters;

/// <summary>
/// Builds the host's <see cref="FlagSetting"/>/<see cref="ChoiceSetting"/> from the manifest's
/// <c>declaredSettings</c> list (docs/vendor-plugins.md §3.4, §3.5, §4.2). Settings travel as DECLARATIONS,
/// not as ports: the manifest carries the key, the shape and (for a choice) the options, while the VALUE is read
/// and written through <c>ah_invoke(Capability.Settings, Op.Read/Set, {"key":…})</c>. That is what preserves the
/// host settings model — <c>Settings.cs:108-198</c> still holds the set and still does <c>Apply</c>/<c>Remember</c>
/// — and it leaves <c>OptionsAssembler</c> untouched: it consumes the same declarations it always did.
///
/// ONLY THE TRANSPORT IS NEW. <see cref="FlagSetting.Refuse"/>/<see cref="ChoiceSetting.Refuse"/>,
/// <c>Apply</c>, <c>ReadbackVerifiesWrite</c> and the persisted value encoding all stay host-side and unchanged
/// (§4.2: "Refuse/Apply/Remember stay host-side"). The adapter supplies exactly the <see cref="IFlagPort"/> /
/// <see cref="IChoicePort"/> those types read/write through, implemented over the Settings capability — the same
/// division the in-host <c>FlagPort</c>/<c>ChoicePort</c> occupy (Infrastructure/Plugins/Sdk/DelegatePorts.cs).
///
/// A <c>Refused</c> write is reported through <c>LastError</c>, matching the port contract: the host's
/// <c>FlagSetting.Write</c>/<c>ChoiceSetting.Write</c> read it only after a write that RETURNED false, so the
/// reason belongs to THIS call and a throw leaves it holding an earlier call's words
/// (Domain/DeclaredSetting.cs:96-116, 163-172).
/// </summary>
internal static class PluginDeclaredSetting
{
    /// <summary>Build the declaration for one manifest entry, or null when the entry is malformed (a missing key,
    /// or a shape this build does not know). A null is dropped by the caller rather than declared: a setting with
    /// no key or no shape is not a setting.</summary>
    public static SettingDeclaration? Build(IPluginSession session, DeclaredSettingManifest entry)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Key is not { Length: > 0 } key) return null;

        return entry.Shape switch
        {
            "flag" => new FlagSetting
            {
                Key = key,
                ReadbackVerifiesWrite = entry.ReadbackVerifiesWrite,
                Port = new PluginFlagPort(session, key),
            },
            "choice" => new ChoiceSetting
            {
                Key = key,
                Port = new PluginChoicePort(session, key, MapOptions(entry.Options)),
            },
            // An unknown shape is a plugin bug (or a newer minor's shape, §3.8.5): there is no honest host
            // declaration to build, so it is dropped rather than guessed into one of the two shapes.
            _ => null,
        };
    }

    private static IReadOnlyList<ChoiceOption> MapOptions(List<ChoiceOptionManifest>? options)
    {
        var list = new List<ChoiceOption>(options?.Count ?? 0);
        if (options is not null)
            foreach (var option in options)
                list.Add(new ChoiceOption(option.Id ?? "", option.DisplayName ?? ""));
        return list;
    }
}

/// <summary>
/// The <see cref="IFlagPort"/> over <c>Capability.Settings</c> for one key
/// (docs/vendor-plugins.md §3.5, §4.2). <see cref="Get"/> is <c>Op.Read → {"value":"1"}</c> and
/// <see cref="Set"/> is <c>Op.Set → {}</c> or <c>{"error":…}</c>. The value encoding is the host's own flag
/// encoding — the strings "1"/"0" (Domain/DeclaredSetting.cs:72-84) — carried through verbatim, so a declared
/// flag needs no second spelling.
/// </summary>
internal sealed class PluginFlagPort : IFlagPort
{
    private readonly IPluginSession _session;
    private readonly string _key;

    public PluginFlagPort(IPluginSession session, string key)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _key = key;
    }

    public string? LastError { get; private set; }

    /// <summary>Read the key and decode the host flag form: anything other than "1" is off. A refusal or a bad
    /// body reads as off, the flag's own false.</summary>
    public bool Get()
    {
        var request = new JsonObject { ["key"] = _key }.ToJsonString();
        var result = _session.Invoke(Capability.Settings, Operation.Settings.Read, request);
        return result.Status == AbiStatus.Ok
            && PluginBodies.StringProperty(result.Body, "value") == "1";
    }

    /// <summary>Write the host flag form and report both halves of the outcome: whether it took, and THIS call's
    /// reason when it did not. The reason is parsed from this result, never read off a field afterwards.</summary>
    public bool Set(bool on)
    {
        var request = new JsonObject { ["key"] = _key, ["value"] = on ? "1" : "0" }.ToJsonString();
        var result = _session.Invoke(Capability.Settings, Operation.Settings.Set, request);
        LastError = result.Status == AbiStatus.Ok ? null : PluginBodies.ErrorOf(result);
        return result.Status == AbiStatus.Ok;
    }
}

/// <summary>
/// The <see cref="IChoicePort"/> over <c>Capability.Settings</c> for one key (§3.5, §4.2). The option set comes
/// from the MANIFEST (the declaration carried it); only reading and writing the current id cross the boundary.
/// As with the in-host <c>ChoicePort</c>, the ids are the vendor's stable keys and the display names are opaque
/// to this layer.
/// </summary>
internal sealed class PluginChoicePort : IChoicePort
{
    private readonly IPluginSession _session;
    private readonly string _key;

    public PluginChoicePort(IPluginSession session, string key, IReadOnlyList<ChoiceOption> options)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _key = key;
        Options = options;
    }

    public string? LastError { get; private set; }

    public IReadOnlyList<ChoiceOption> Options { get; }

    /// <summary><c>Op.Read → {"value":"id"}</c>, or null when unreadable.</summary>
    public string? Get()
    {
        var request = new JsonObject { ["key"] = _key }.ToJsonString();
        var result = _session.Invoke(Capability.Settings, Operation.Settings.Read, request);
        return result.Status == AbiStatus.Ok ? PluginBodies.StringProperty(result.Body, "value") : null;
    }

    /// <summary><c>Op.Set → {}</c> or <c>{"error":…}</c>; the reason, when there is one, belongs to this call.</summary>
    public bool Set(string id)
    {
        var request = new JsonObject { ["key"] = _key, ["value"] = id }.ToJsonString();
        var result = _session.Invoke(Capability.Settings, Operation.Settings.Set, request);
        LastError = result.Status == AbiStatus.Ok ? null : PluginBodies.ErrorOf(result);
        return result.Status == AbiStatus.Ok;
    }
}
