using AcerHelper.Domain;

namespace AcerHelper.Application;

/// <summary>What the per-mode lighting needs from whoever owns the settings graph — the contract for the ONE axis
/// the lighting UI edits, declared here and implemented by the layer that owns the graph
/// (Infrastructure/Composition/LaptopService.Lighting.cs).
///
/// IT REPLACES A LIVE REFERENCE, and that is the whole point of the type. Until this contract existed the path
/// handed the UI the current mode's zone dictionary ITSELF — <c>LightsForCurrentMode</c> in both forms and
/// <c>EnsureLightZone</c> — and the UI kept it and wrote into it in place, so every edit was an unguarded write
/// into the persisted graph, made from a thread that never took the lock that guards it. That was a RECORDED
/// decision («запись в граф и есть фича», docs/open-decisions.md §4) with <c>Assert.Same</c> guards pinning the
/// aliasing, and the owner has overruled it: «Это неверно с точки зрения архитектуры.» The state now crosses as
/// <see cref="LightZoneState"/> — the domain's own spelling of one zone's lighting — and the UI holds VALUES.
///
/// WHY IT IS MODE-SCOPED (<see cref="ILightZoneMode"/> and not a flat set of members). The mode is a fact the
/// MACHINE holds and only the EC can be asked, and the answer is wanted exactly where the answer already was
/// taken: once per UI build and once per refresh pass, both of which read the profile anyway
/// (<c>LightsForCurrentMode(PerformanceProfile?)</c>'s reason for existing). Resolving it per write instead
/// would put an EC round-trip on the UI thread behind every lighting edit — a slider drag would hitch — and it
/// would also CHANGE which mode an edit lands in. Today's write lands in the mode whose dictionary the read
/// handed over, and this contract keeps that: the mode is fixed when the door is taken, and both use cases
/// below read and write through the door they were given.
///
/// WHAT IS NOT HERE. The mode KEY is not a parameter and not a member — it is the graph's own business, as it is
/// for every other axis (<see cref="IGpuOffsetsTarget"/> states the same rule for the same reason) — and no
/// member returns a stored object or a way to reach one.</summary>
public interface ILightZoneMode
{
    /// <summary>Every zone this mode holds, in the domain's vocabulary, as a COPY — values the caller may keep
    /// and re-apply. This is the read the re-apply pass takes (off the UI thread, so the UI never opens the
    /// graph itself) and the one the UI rebinds its panels from when the mode changes. A mode with no zones yet
    /// gives an empty map, not null.
    ///
    /// THE COPY IS DEEP WHERE IT MATTERS: each entry's <see cref="LightZoneState.ZoneColors"/> is duplicated
    /// too. A snapshot that copied the fields but shared the array would leave the widest part of the state
    /// shared — the same depth rule the closed accessors' <c>Snapshot()</c> methods state for the presets.</summary>
    IReadOnlyDictionary<string, LightZoneState> Stored();

    /// <summary>Whether this machine advertises a zone by that name — the RGB device's own list, which is where
    /// every zone name in this path comes from. The question exists so that <c>ApplyLightZone</c> can refuse a
    /// write for a zone this machine does not have, and it is asked of the DEVICE rather than of the graph,
    /// because the graph is what must not acquire the entry.</summary>
    bool Advertises(string zone);

    /// <summary>Make <paramref name="state"/> this mode's stored lighting for <paramref name="zone"/>, creating
    /// the mode's bucket and the zone's entry when they are absent. Under the graph lock; does NOT persist —
    /// <see cref="Persist"/> is a separate act, on the same terms as
    /// <see cref="IDeclaredSettingTarget.Remember"/>'s, so the order two use cases state is a rule rather than a
    /// coincidence of which call was typed first.</summary>
    void Write(string zone, LightZoneState state);

    /// <summary>Write the graph out. Today this is the <c>Save()</c> the lighting path has always called after an
    /// edit (formerly <c>LaptopService.PersistLighting</c>), and it is a member of its own for the same reason
    /// <see cref="IDeclaredSettingTarget.Persist"/> is: folding it into <see cref="Write"/> would decide,
    /// silently, that a record is never observed before it is persisted.</summary>
    void Persist();
}

/// <summary>One zone's lighting for the mode the door was taken for, as a READ.
///
/// WHAT IT DECIDES, and it is the rule the old <c>EnsureLightZone</c> carried as plumbing: <b>a mode that has
/// never held this zone is not an absence to report — it is the zone in its DEFAULTS, and the read CREATES the
/// entry.</b> It has to be, because of what the UI does with the answer: the first act on a zone is to SHOW it,
/// and the first edit afterwards must update the very entry the user was looking at rather than race a second
/// creation. The creation is not persisted — a mode that has merely been looked at has been configured by
/// nobody, which is what the store's own assertion has always said (<c>0 saves</c> after a read).
///
/// A zone the machine does not advertise is CREATED BY NOTHING and answered with the defaults: the graph's
/// lighting may only hold zones this machine has, and a name that is not one of them has no state to read. The
/// caller reaching this has taken the name from the device, so the branch is unreachable from the tree's own
/// callers — it is stated rather than left to <c>TryGetValue</c>'s accident, and the same rule is what
/// <see cref="ApplyLightZone"/> refuses a write with.</summary>
public static class ReadLightZone
{
    public static LightZoneState Run(string zone, ILightZoneMode mode)
    {
        if (!mode.Advertises(zone)) return LightZoneState.Default;
        if (mode.Stored().TryGetValue(zone, out var stored)) return stored;
        mode.Write(zone, LightZoneState.Default);   // first sight of this zone in this mode
        return LightZoneState.Default;
    }
}

/// <summary>One zone's lighting for the mode the door was taken for, as an EDIT — what the UI calls after a
/// colour, an effect, a speed or a brightness changed.
///
/// WHAT IT DECIDES, in order, and each half is load-bearing:
/// <list type="number">
/// <item><b>A zone this machine does not advertise is REFUSED</b> — nothing is written and nothing is
/// persisted, and the caller is told so by the returned <c>false</c>. The rule is not decoration: the graph is
/// PERSISTED, so an entry for a zone this machine does not have is written to settings.json forever, is read
/// back on every launch, and reappears wholesale on a machine that does have a zone by that name — a stored
/// state nobody configured, outliving the session that invented it. Keeping the graph's zone keys inside the
/// device's list is what stops that, and the check is here rather than in the implementation so that the rule
/// is stated where it can be pinned without a machine to run it on.</item>
/// <item><b>The state is written as it was handed over</b>, no field of it interpreted: the whole zone crosses
/// at once, which is the shape the UI already had (it mutates every field it knows and then saves) and the
/// reason no per-field member exists here.</item>
/// <item><b>Then the graph is written out</b>, because the value is the user's choice rather than a session's
/// state.</item>
/// </list>
///
/// A WRITE DOES NOT HAVE TO CREATE ANYTHING: the entry is normally already there from the read, and
/// <see cref="ILightZoneMode.Write"/> creates it when it is not — the same "created on first write" rule every
/// other per-mode axis has, and the one that makes an edit on a mode that was never configured start from its
/// presets instead of being refused.</summary>
public static class ApplyLightZone
{
    public static bool Run(string zone, LightZoneState state, ILightZoneMode mode)
    {
        if (!mode.Advertises(zone)) return false;
        mode.Write(zone, state);
        mode.Persist();
        return true;
    }
}
