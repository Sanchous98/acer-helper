using AcerHelper.Domain;

namespace AcerHelper.Application;

/// <summary>What the Turbo switch needs from whoever owns the per-source slot and the profile port — the contract
/// for one of the things the owner's model applies («Turbo как переключатель»), declared here and implemented by
/// Infrastructure/Composition/LaptopService.Profiles.cs.
///
/// THE MEMBERS ARE THE GRAPH HALVES THE SWITCH ITSELF MUST DO, and they are four because a Turbo switch is not one
/// graph write: it must DECIDE whether there is a Turbo this source offers (a port read plus the availability
/// gate), CAPTURE the base it takes off (a graph write that must happen BEFORE the port write, because after it
/// the live profile is Turbo), and — only when the write LANDED — record the flag. The port write itself is
/// <see cref="SwitchProfile"/>'s, which the use case owns, so nothing here writes a profile.
///
/// <see cref="BeginTurbo"/> RETURNS THE PROFILE rather than a bool, and that is the ordering rule made
/// unrepresentable to get wrong: it both finds the Turbo and captures the base under one hold, so a caller cannot
/// capture after the write. Null means "no Turbo this source offers" (or no port) and the switch is a no-op.</summary>
public interface ISetTurboTarget
{
    /// <summary>Whether this machine has a power-profiles port at all. False is the same no-op a machine with no
    /// profiles has always had for the Turbo key.</summary>
    bool HasProfiles { get; }

    /// <summary>Find the Turbo profile the LIVE source offers and, under the SAME hold, capture the base it is
    /// about to sit over (the current profile, when that is not itself Turbo). Returns null when there is no Turbo
    /// profile or the source does not offer it, in which case nothing was captured and nothing may be written.</summary>
    PerformanceProfile? BeginTurbo();

    /// <summary>The base (non-Turbo) profile to return to when switching off, or null when this source has none to
    /// return to — read under the graph lock, from the remembered slot with the usual Balanced/first-non-Turbo
    /// fallbacks.</summary>
    PerformanceProfile? BaseProfileOrNull();

    /// <summary>Record that Turbo is engaged over the preserved base (or clear it). A graph write under the lock,
    /// persisted; called by the use case ONLY for a write that landed (on) or for the base switch that landed
    /// (off).</summary>
    void RememberTurbo(bool on);
}

/// <summary>The Turbo switch as a use case: on = write Turbo over the current base as a TRANSIENT (the switch must
/// not overwrite the remembered base with Turbo) and then flip the Turbo flag; off = switch back to the remembered
/// base, which clears the flag as an ordinary persistent switch.
///
/// WHAT IT DECIDES, and every half is what the hand-written method used to get right by care:
/// <list type="number">
/// <item><b>Turbo availability is the source's own gate.</b> On a battery, or any source that does not offer
/// Turbo — or on a machine with no power-profiles port at all (<see cref="ISetTurboTarget.HasProfiles"/>, the
/// absence checked first) — the switch is a no-op reporting nothing: the backstop for the hotkey, which has no
/// other way to learn it failed. A MISSING PORT IS A CAPABILITY FACT and never a throw.</item>
/// <item><b>The base is captured before the write.</b> After the port write the live profile IS Turbo, so a
/// capture placed after it would remember Turbo as its own base and switching off would have nowhere to go.</item>
/// <item><b>Only a landed write flips the flag.</b> A refused transient — now a
/// <see cref="PortWriteFailedException"/> from the switch use case — leaves the slot exactly as it was, so the
/// next source change restores a mode the machine actually had.</item>
/// <item><b>BOTH WRITES GO THROUGH THE SWITCH USE CASE</b>, so the port write and its lighting announcement are one
/// action — the Turbo key used to be one of the hand-written writes that bypassed the light claim.</item>
/// </list>
///
/// WHAT IT DOES NOT CATCH: <see cref="PortWriteFailedException"/> from a present profile port escapes to the
/// caller (the UI's Turbo action catches it and shows the message).</summary>
public sealed class SetTurbo(ISetTurboTarget target, SwitchProfile switchProfile)
{
    public PerformanceProfile? Run(bool on)
    {
        if (!target.HasProfiles) return null;

        if (on)
        {
            if (target.BeginTurbo() is not { } turbo) return null;
            var applied = switchProfile.Run(turbo, transient: true);
            if (applied is null) return null;
            target.RememberTurbo(true);
            return applied;
        }

        if (target.BaseProfileOrNull() is not { } baseProfile) return null;
        return switchProfile.Run(baseProfile);   // a persistent switch clears the Turbo flag
    }
}

/// <summary>What the performance hotkey needs from whoever owns the profile list and the graph — implemented by
/// Infrastructure/Composition/LaptopService.Profiles.cs. Three reads, and each is a question the cycle asks and the
/// use case cannot answer: the Turbo-key preference, whether the machine is in Turbo, and which profile the cycle
/// should land on next (a port read plus the availability filter plus the pure wrap rule).</summary>
public interface ITogglePerformanceTarget
{
    /// <summary>Whether the hotkey behaves as a Turbo switch (<c>Settings.TurboToggles</c>).</summary>
    bool TurboToggles { get; }

    /// <summary>Whether the hardware is currently in the Turbo profile.</summary>
    bool IsTurboOn();

    /// <summary>The next profile the cycle should land on, from the LIVE source's selectable set and the current
    /// profile, or null when this machine offers no profiles to cycle (no port, or none listed).</summary>
    PerformanceProfile? NextSelectableProfile();
}

/// <summary>The performance hotkey as a use case: cycle to the next selectable profile, or — when the Turbo-key
/// preference is on — flip Turbo. Two branches, one entry point, and the preference is read fresh each time so a
/// flip takes effect without a rebuild.
///
/// WHAT IT DECIDES: WHICH of the two branches, and that the cycle runs over the FILTERED set — so the hotkey
/// cannot land on a profile the live source disables (NitroSense parity). Both branches go through the shared
/// switch use case, so neither can bypass the lighting announcement.</summary>
public sealed class TogglePerformance(ITogglePerformanceTarget target, SetTurbo setTurbo, SwitchProfile switchProfile)
{
    public PerformanceProfile? Run()
    {
        if (target.TurboToggles) return setTurbo.Run(!target.IsTurboOn());

        var next = target.NextSelectableProfile();
        return next is not null ? switchProfile.Run(next) : null;
    }
}

/// <summary>What pinning a profile to a power source needs from whoever owns the two remembered slots and the
/// live-source answer — implemented by Infrastructure/Composition/LaptopService.Profiles.cs.
///
/// THE SPLIT IS THE DECISION. A source profile is stored as a base id plus a Turbo flag (Turbo is a switch over a
/// base, not a mode of its own), and the two storage shapes are separate members so the use case can say WHICH one
/// an edit is; the seeding of a base under Turbo is a third, because it is what stops "drop Turbo later" from
/// having nothing to return to. <see cref="SourceIsLive"/> is the graph's own answer to "is this the source we are
/// on right now" — a reference comparison against the live slot, which also covers "the source is still unknown"
/// (the AC slot is then the live one).</summary>
public interface ISourceProfileTarget
{
    /// <summary>Whether this machine has a power-profiles port at all.</summary>
    bool HasProfiles { get; }

    /// <summary>The Turbo-key preference, read under the lock.</summary>
    bool TurboToggles { get; }

    /// <summary>Whether <paramref name="profile"/> is this backend's Turbo mode, as the port classifies it.</summary>
    bool IsTurbo(PerformanceProfile profile);

    /// <summary>The profile to seed as the base under Turbo when the source has never held a base (Balanced if this
    /// backend offers it, else the first non-Turbo profile), or null when there is none.</summary>
    PerformanceProfile? TurboSeed();

    /// <summary>Store <paramref name="profile"/> as the source's base, clearing the Turbo flag — the plain pick's
    /// memory, under the lock, persisted.</summary>
    void RememberBase(bool onAc, PerformanceProfile profile);

    /// <summary>Record Turbo over the source's preserved base, seeding the base from
    /// <paramref name="seed"/> only when the source has never held one, under the lock, persisted.</summary>
    void RememberTurbo(bool onAc, PerformanceProfile? seed);

    /// <summary>Whether <paramref name="onAc"/> names the LIVE source (the slot the machine is on right now).</summary>
    bool SourceIsLive(bool onAc);

    /// <summary>Apply the live source's remembered mode to the port. "Nothing to do" — already in that mode, or
    /// nothing remembered — returns normally; a PRESENT profile port's refusal THROWS
    /// <see cref="PortWriteFailedException"/>. The per-source restore is reached both as a USER action (the Options
    /// row) and as a SYSTEM path (the refresh loop's source change), so the catch belongs to each caller: the row
    /// shows the message, the loop swallows it.</summary>
    void ApplyStoredMode();
}

/// <summary>Pinning a profile to a power source as a use case: remember the choice for that source, and apply it
/// right away when that source is the live one.
///
/// WHAT IT DECIDES:
/// <list type="number">
/// <item><b>Turbo is stored as a flag over a preserved (or seeded) base</b>, never as a base id, when the Turbo
/// switch is on — so the mode the machine returns to when Turbo is dropped is still there. With the switch off
/// Turbo is an ordinary profile and is stored as the base itself, which is what lets the UI highlight it.</item>
/// <item><b>Setting the source the machine is NOT on persists without touching the hardware</b> — the stored mode
/// is applied later, when that source becomes live. Setting the LIVE source applies immediately, through the same
/// restore path a real source change uses (<see cref="ISourceProfileTarget.ApplyStoredMode"/>), which is what
/// keeps the two from disagreeing.</item>
/// <item><b>A failed apply still leaves the choice remembered</b> — the store happens before the apply, and the
/// source's row reports the failure. The apply's refusal is a <see cref="PortWriteFailedException"/> and escapes
/// this use case (the row catches it); it is NOT caught here, so only the store has happened when it throws.</item>
/// </list></summary>
public sealed class SetSourceProfile(ISourceProfileTarget target)
{
    public bool Run(bool onAc, PerformanceProfile profile)
    {
        if (!target.HasProfiles) return false;

        if (target.TurboToggles && target.IsTurbo(profile)) target.RememberTurbo(onAc, target.TurboSeed());
        else target.RememberBase(onAc, profile);

        if (target.SourceIsLive(onAc)) target.ApplyStoredMode();
        return true;
    }
}

/// <summary>What keeping the per-source memory in sync with telemetry needs from whoever owns the effective
/// source — implemented by Infrastructure/Composition/LaptopService.Profiles.cs. ONE member: hand over the OS's
/// own "external power" reading, and the layer that owns the two-input effective source (OS + typed EC adapter)
/// folds it in and seeds or restores as the old code did. The USB-C-demotion rule is that layer's, and stating it
/// a second time here would be the drift the single member avoids.</summary>
public interface IPowerSourceSyncTarget
{
    /// <summary>Record the OS's "is external power connected" reading and recompute the effective source, seeding
    /// or restoring the source's remembered mode when the effective source actually changes. Under the graph
    /// lock.</summary>
    void ObserveOsPower(bool onAc);
}

/// <summary>Keeping the per-source mode in sync as a use case: a known battery state that says whether the OS sees
/// external power, handed to the layer that owns the effective source.
///
/// WHAT IT DECIDES: <b>only a KNOWN state is acted on.</b> An unreadable battery reports nothing to correct, so a
/// transient failed read must not flap the profiles — the same "do not guess" rule the typed-adapter read keeps.
/// "Charging or idle" is external power; discharging is not. That is all the use case knows, deliberately: the
/// demotion of USB-C Power Delivery to the battery set is a fact about the TYPED source, which this use case does
/// not carry.</summary>
public sealed class SyncPowerSource(IPowerSourceSyncTarget target)
{
    public void Run(BatteryInfoSnapshot battery)
    {
        if (battery.State == BatteryState.Unknown) return;
        target.ObserveOsPower(battery.State != BatteryState.Discharging);
    }
}
