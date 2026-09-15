namespace AcerHelper.Domain;

/// <summary>The identity a per-mode preset is filed under — the value behind the string
/// <c>LaptopService.CurrentModeKey()</c> returns.
///
/// It exists as a type for one reason and it is worth stating plainly, because the temptation is to expect
/// more of it: this does NOT validate the key. A key can name a profile the device no longer offers (a
/// remembered Turbo base the firmware stopped reporting — the "ghost" case pinned in
/// <c>LaptopServiceModeTests</c>), and that stays true here. Making it impossible would be a change of
/// BEHAVIOUR, not of type: the stale key is what the per-mode dictionaries are already filed under, so
/// rejecting it would move which preset reaches the hardware. The type carries the RULE for deriving a key;
/// it does not rule on whether the result exists.
///
/// <see cref="Value"/> is that string, and the derivation is deliberately verbatim from the method it came
/// from — including the sentinel and including the Turbo-shares-its-base's-key rule. The differential test
/// (<c>ModeKeyTests</c>) pins the two against each other, which is what makes the move a move rather than a
/// rewrite.</summary>
public readonly record struct ModeKey
{
    /// <summary>The string the preset dictionaries are keyed by.</summary>
    public string Value { get; }

    private ModeKey(string value) => Value = value;

    /// <summary>The device exposes no profiles at all, so there is no mode to name. The string is the one the
    /// settings file has always contained for that case, and it is kept because it is PERSISTED: changing it
    /// would orphan whatever the user has filed under it.</summary>
    public static readonly ModeKey None = new("default");

    /// <summary>Derive the key for <paramref name="cur"/>.
    ///
    /// <paramref name="slot"/> is passed in rather than read here, and that is not a style choice: the slot
    /// lives behind <c>LaptopService._state</c> (it is the remembered mode of the LIVE power source), so the
    /// caller reads it under the lock and hands the value over. Reading it here would either need the lock
    /// inside a domain type or would read it unguarded — and hoisting a guarded read out of its lock is the
    /// hazard recorded in docs/open-decisions.md §3.
    ///
    /// Turbo used as a switch shares its base profile's key, so presets do not fragment when the user toggles
    /// Turbo over the same base; a Turbo with no remembered base keys by its own id, because there is nothing
    /// to share with.</summary>
    public static ModeKey For(PerformanceProfile? cur, bool turboToggles, ProfileMemory slot)
        => cur == null ? None
         : turboToggles && cur.Kind == ProfileKind.Turbo && slot.BaseId.Length > 0
             ? new ModeKey(slot.BaseId)
             : new ModeKey(cur.Id);

    /// <summary>The string form, so a key can be used directly as the dictionary/JSON key it stands for.</summary>
    public override string ToString() => Value;
}
