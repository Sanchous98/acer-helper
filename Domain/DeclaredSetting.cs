namespace AcerHelper.Domain;

// The CONTRACT half of the declared-settings model, and the half the owner left in Domain.
//
// The domain knows no hardware-specific setting names — not "LCD overdrive", not "acer.lightbarFollowsProfile".
// A backend declares which settings THIS machine has, each under its own opaque key, and this file declares the
// two halves of what that means: the FORM of a declaration (a flag or a choice, and for a choice its legal
// values) and what APPLYING one is (a write, whose refusal is an exception rather than a returned message).
//
// THE OTHER HALF IS INFRASTRUCTURE. The object that HOLDS the set and switches it — Settings.Apply plus its
// Remember, and the persisted graph those calls sit on — is the shape settings.json has, so it lives in
// Infrastructure/Composition/Settings.cs beside the store that writes it. It is one object rather than two
// because switching an option is the apply AND the remember, and both need the same instance; what crosses the
// boundary is these declarations, which the backend builds and the model is constructed with.
//
// Nothing here interprets a key or names a setting, and nothing here names the container. The row's label, its
// localization and the sentence the user reads on a failure all belong to the UI, which is the only layer that
// knows what to call a `lcd_override`.

/// <summary>One setting a machine's backend declares, so the row that drives it can be built from the
/// declaration instead of from a hard-coded port slot and a hard-coded name.
///
/// Subtypes ARE the fork a row needs: <see cref="FlagSetting"/> carries a bool and <see cref="ChoiceSetting"/> a
/// pick-one-of-N set with the ids the backend accepts. A <c>Dictionary&lt;string,string&gt;</c> cannot express
/// that fork, which is why the bag holds this setting's VALUE while the declaration holds its shape.</summary>
public abstract record SettingDeclaration
{
    /// <summary>The backend's own name for this setting, OPAQUE to the domain: nothing above reads it except to
    /// match it with a row label and to record the value. It is also the key this setting's value is recorded
    /// under in <c>Settings.DeviceSettings</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Whether a row verifies a write by reading the setting back afterwards. TRUE where the transport
    /// can accept a write and silently not take it — the row then corrects its own switch instead of leaving it
    /// lying. FALSE where the write itself reports the outcome (Acer's LCD overdrive returns a status byte),
    /// because there the readback would be a second hardware transaction the user hears as a second click.</summary>
    public bool ReadbackVerifiesWrite { get; init; } = true;

    /// <summary>Apply <paramref name="value"/> to the hardware, or throw. A refusal is
    /// <see cref="SettingNotAppliedException"/> — information about what happened, and nothing else: the
    /// sentence the user reads is composed by the UI, which is where setting names live.
    ///
    /// A VALUE THIS SETTING DOES NOT ADMIT IS REFUSED BEFORE THE TRANSPORT IS TOUCHED, which is why the check is
    /// here and not inside either <see cref="Write"/>. Each shape knows its own legal values
    /// (<see cref="Refuse"/>) and neither transport can be asked to enforce them: a flag write reads its value as
    /// <c>value == "1"</c>, so ANY other string silently means OFF — a write to the hardware that the caller
    /// never asked for — and a choice write hands the string to a port that only knows its own option ids. The
    /// guard's absence was a hole in the MODEL, not in the transports, so it is closed in the model.</summary>
    public void Apply(string value)
    {
        if (Refuse(value) is { } refused) throw new SettingNotAppliedException(Key, refused);

        var (ok, error) = Write(value);
        if (!ok) throw new SettingNotAppliedException(Key, error);
    }

    /// <summary>Why <paramref name="value"/> is not a value this setting admits, or null when it is one. The
    /// reason is this layer's own words, sitting exactly where a transport's words sit when it refuses a write —
    /// the UI composes "&lt;row label&gt; failed: &lt;reason&gt;" from both (<c>OptionsAssembler.RunSet</c>).
    ///
    /// THE DEFAULT ADMITS EVERYTHING, because a declaration that names no legal values has none to enforce; both
    /// shapes in this file override it. It is a string and not a bool because a refusal has to SAY which value
    /// was wrong and what the setting takes instead — a bare false would leave the row showing a failure with
    /// nothing to correct.</summary>
    protected virtual string? Refuse(string value) => null;

    /// <summary>The hardware write behind <see cref="Apply"/>: whether it took, and the transport's own reason
    /// when it did not. Reached only by a value <see cref="Refuse"/> admitted.</summary>
    protected abstract (bool ok, string? error) Write(string value);
}

/// <summary>An on/off setting. Its values are the strings <c>Settings.DeviceSettings</c> already uses for
/// a flag — "1" and "0" — so a declared flag needs no second encoding. THOSE TWO ARE THE ONLY VALUES IT ADMITS:
/// see <see cref="Refuse"/>.</summary>
public sealed record FlagSetting : SettingDeclaration
{
    /// <summary>The transport this setting is read and written through.</summary>
    public required IFlagPort Port { get; init; }

    /// <summary>What the hardware holds now.</summary>
    public bool Read() => Port.Get();

    /// <summary>The stored form of a flag, for callers that hold a bool.</summary>
    public static string Value(bool on) => on ? "1" : "0";

    /// <summary>Anything that is not "1" or "0" is refused, and the reason is not pedantry: the port takes a
    /// BOOL, and this declaration's own write reads the string as <c>value == "1"</c>, so every other string
    /// means OFF. A caller that passed "true", "on", "0 " or a stored value read from somewhere else would
    /// switch the setting OFF while believing it had switched it on — a write to the hardware nobody asked for,
    /// and the exact shape (<see cref="Write"/>) that cannot report it, since the port's write succeeds. The two
    /// legal strings are this type's own encoding (<see cref="Value"/>) and are what
    /// <c>Settings.DeviceSettings</c> persists, so nothing in the tree has a third spelling to lose.</summary>
    protected override string? Refuse(string value)
        => value is "1" or "0" ? null : $"\"{value}\" is not one of its values — it is a switch, so it takes \"1\" or \"0\"";

    /// <summary>A port that THROWS is a failed write, not a crash — and it reports NO reason, deliberately.
    /// <see cref="IFlagPort.LastError"/> is a field the PORT owns, written by a call that runs to completion, so
    /// a write that THREW never assigned it: reading it here would hand the user whatever an EARLIER call left
    /// behind, which is the failure mode docs/open-decisions.md §2 exists to remove ("a reader picking up
    /// ANOTHER call's error"). Measured on the tree rather than assumed: the one production <see cref="IFlagPort"/>
    /// (<c>Infrastructure/Vendors/Generic/DelegatePorts.cs</c>) sets <c>LastError</c> only after its write
    /// delegate returns, and every delegate the backends hand it (Acer's <c>GmSet</c>/<c>UiSet</c>, Dell's
    /// WmiSession calls, the Linux sysfs and firmware-attributes writers) catches its own failures and returns
    /// them as a pair — so nothing in the tree establishes the old claim that a throwing transport reports its
    /// cause through <c>LastError</c>, and a throw can only leave a stale value there. Absent is also how the
    /// other shape reports the same event (<c>LaptopService.Attempt</c> over a tuple, and
    /// <c>OptionsAssembler.RunSet</c>'s catch): a throwing transport reads the same way wherever it is caught,
    /// and the UI shows "&lt;row label&gt; failed" with no invented cause rather than a real failure wearing
    /// somebody else's words.</summary>
    protected override (bool ok, string? error) Write(string value)
    {
        bool ok;
        try { ok = Port.Set(value == "1"); }
        catch { return (false, null); }   // a throw assigns nothing: LastError still belongs to an earlier call
        return (ok, ok ? null : Port.LastError);
    }
}

/// <summary>A pick-one-of-N setting. Its values are option ids verbatim: the ids are the backend's own stable
/// keys, and <see cref="Options"/> is the display set in the order the dropdown shows it — which is what makes
/// the id-to-index mapping a row needs expressible here (<see cref="IndexOf"/>) and not in the bag.</summary>
public sealed record ChoiceSetting : SettingDeclaration
{
    /// <summary>The transport this setting is read and written through.</summary>
    public required IChoicePort Port { get; init; }

    /// <summary>The legal values, in display order.</summary>
    public IReadOnlyList<ChoiceOption> Options => Port.Options;

    /// <summary>The id the hardware is in now, or null when it would not answer.</summary>
    public string? Read() => Port.Get();

    /// <summary>The dropdown index of <paramref name="id"/>, or 0 when the hardware reports an id this build
    /// does not offer — the same answer the port being unreadable gives, and the same one the row showed before
    /// the mapping lived here.</summary>
    public int IndexOf(string? id)
    {
        for (var i = 0; i < Options.Count; i++)
            if (Options[i].Id == id)
                return i;
        return 0;
    }

    /// <summary>A value that is not one of <see cref="Options"/> is refused, and the check is here because
    /// nothing else could make it: the KEY is guarded by <c>Settings.Apply</c> against the declared set, but the
    /// VALUE went straight to the port, which accepts any string it does not recognise as "leave the hardware
    /// alone" at best and encodes it as a mode byte at worst. A dropdown can only offer what the device
    /// advertises, so the UI cannot produce one — but a stored value replayed, an id from a different machine's
    /// list, or a future caller reading the bag has nothing stopping it, and the failure would be silent in
    /// exactly the way this axis is worst at.
    ///
    /// MEMBERSHIP IS THE SET THE DEVICE ADVERTISES (<see cref="Options"/>, which is the port's own list), not a
    /// second copy of the ids: the ids are the backend's stable keys and this type never learns what they mean,
    /// so the only honest answer to "may this value be written" is whether the device offered it.</summary>
    protected override string? Refuse(string value)
    {
        foreach (var option in Options)
            if (option.Id == value)
                return null;
        return $"\"{value}\" is not one of its options";
    }

    /// <summary>A port that THROWS is a failed write, not a crash, and it reports no reason for the same reason
    /// <see cref="FlagSetting.Write"/> does not: a throw leaves the port's own <c>LastError</c> holding an
    /// earlier call's words.</summary>
    protected override (bool ok, string? error) Write(string value)
    {
        bool ok;
        try { ok = Port.Set(value); }
        catch { return (false, null); }
        return (ok, ok ? null : Port.LastError);
    }
}

/// <summary>Why a declared setting could not be applied, whichever of the two failures it was: the transport
/// refused the write (and gave its own words), or the option is not one this machine declares at all (and the
/// MODEL gave the words — <c>Settings.Apply</c>). Carries INFORMATION — which setting, and the reason —
/// and deliberately not a sentence for the user: the domain knows no setting names, so the message the user
/// reads is composed by the UI from the row's own label (see <c>OptionsAssembler.RunSet</c>).
/// <see cref="Exception.Message"/> exists for a log or a stack trace and is not what the UI shows.
///
/// ONE TYPE FOR BOTH, deliberately. The two failures differ in what happened, not in what the caller must do
/// about it, and the whole channel is one name: the UI catches this and composes "&lt;label&gt; failed: &lt;reason&gt;"
/// for both, so a second type would buy a programmatic distinction no caller in this tree wants — the
/// not-declared case cannot be reached from the UI, since every row is built from the declared set.</summary>
public sealed class SettingNotAppliedException(string key, string? reason)
    : Exception($"{key} was not applied" + (reason != null ? $": {reason}" : ""))
{
    /// <summary>The declared setting's key — its backend's own name, opaque here.</summary>
    public string Key { get; } = key;

    /// <summary>The transport's own words when it refused the write, or the model's when it refused an option it
    /// does not declare; null when the transport gave none — it never set <c>IFlagPort.LastError</c> on this
    /// call, or the write THREW, and then no reason belonging to this call exists to report
    /// (<see cref="FlagSetting.Write"/>).</summary>
    public string? Reason { get; } = reason;
}
