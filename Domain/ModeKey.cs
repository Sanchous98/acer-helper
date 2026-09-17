namespace AcerHelper.Domain;

/// <summary>The identity a per-mode preset is filed under — the value behind the string
/// <c>LaptopService.CurrentModeKey()</c> returns.
///
/// It exists as a type for one reason and it is worth stating plainly, because the temptation is to expect
/// more of it: this does NOT validate the key. A key can name a profile the device no longer offers (a
/// remembered Turbo base the firmware stopped reporting — the "ghost" case pinned in
/// <c>LaptopServiceModeTests</c>), and that stays true here. Making it impossible would be a change of
/// BEHAVIOUR, not of type: the stale key is what the per-mode dictionaries are already filed under, so
/// rejecting it would move which preset reaches the hardware.
///
/// <see cref="For"/> states what a profile switch means to the DOMAIN and nothing more: the presets of a mode
/// are filed under the profile that mode switched to, and under <see cref="None"/> when the port reports no
/// profile at all. Turbo is a profile like any other here — the kind is never consulted — because that is what
/// the firmware offers and what switching to it does. The rule that a Turbo engaged as a SWITCH shares the key
/// of the base it sits over is NOT here: it is a property of that switch, and its inputs
/// (<c>Settings.TurboToggles</c> and the remembered <see cref="ProfileMemory"/>) belong to the layer that owns
/// the switch — see <c>LaptopService.ModeKeyFor</c>. That is also why <see cref="From"/> exists: a key decided
/// outside the domain still has to be wrapped in the type the preset dictionaries are keyed by.</summary>
public readonly record struct ModeKey
{
    /// <summary>The string the preset dictionaries are keyed by.</summary>
    public string Value { get; }

    private ModeKey(string value) => Value = value;

    /// <summary>The device exposes no profiles at all, so there is no mode to name. The string is the one the
    /// settings file has always contained for that case, and it is kept because it is PERSISTED: changing it
    /// would orphan whatever the user has filed under it.</summary>
    public static readonly ModeKey None = new("default");

    /// <summary>Wrap a key derived outside the domain. The switch's remembered base is the one caller, and it
    /// is passed verbatim: a base the device no longer offers still keys the presets, exactly as
    /// <see cref="For"/> refuses to validate its own answer.</summary>
    public static ModeKey From(string value) => new(value);

    /// <summary>The key of the mode <paramref name="cur"/> names: the profile's own id, or <see cref="None"/>
    /// when there is no profile to name. <see cref="ProfileKind"/> is deliberately not consulted — a mode is
    /// keyed by the profile the user is on, not by the class that profile belongs to.</summary>
    public static ModeKey For(PerformanceProfile? cur)
        => cur == null ? None : new ModeKey(cur.Id);

    /// <summary>The string form, so a key can be used directly as the dictionary/JSON key it stands for.</summary>
    public override string ToString() => Value;
}
