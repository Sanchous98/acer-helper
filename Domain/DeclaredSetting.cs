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
    /// sentence the user reads is composed by the UI, which is where setting names live.</summary>
    public void Apply(string value)
    {
        var (ok, error) = Write(value);
        if (!ok) throw new SettingNotAppliedException(Key, error);
    }

    /// <summary>The hardware write behind <see cref="Apply"/>: whether it took, and the transport's own reason
    /// when it did not.</summary>
    protected abstract (bool ok, string? error) Write(string value);
}

/// <summary>An on/off setting. Its values are the strings <c>Settings.DeviceSettings</c> already uses for
/// a flag — "1" and "0" — so a declared flag needs no second encoding.</summary>
public sealed record FlagSetting : SettingDeclaration
{
    /// <summary>The transport this setting is read and written through.</summary>
    public required IFlagPort Port { get; init; }

    /// <summary>What the hardware holds now.</summary>
    public bool Read() => Port.Get();

    /// <summary>The stored form of a flag, for callers that hold a bool.</summary>
    public static string Value(bool on) => on ? "1" : "0";

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
