using System.Threading;
using AcerHelper.Domain;

namespace AcerHelper.Application;

/// <summary>What switching the performance profile needs from whoever owns the port and the per-source memory —
/// the contract for one of the things the owner's model applies («смена профиля»), declared here and implemented
/// by the layer that owns both (Infrastructure/Composition/LaptopService.Profiles.cs).
///
/// WHAT IT CARRIES. The profile is a Domain value (<see cref="PerformanceProfile"/>) and it is the SAME value on
/// both sides of the wall: unlike an axis whose stored form is a per-mode preset, a profile is not remembered
/// per mode — it is remembered per POWER SOURCE, and the record itself is what the port takes. So the contract
/// names no container type and Application can state the operation without the graph
/// (<c>ArchitectureMapTests.ApplicationPointsAtDomainNotInfrastructure</c>).
///
/// THREE MEMBERS, and the split is the point rather than tidiness. <see cref="CanApply"/> and <see cref="Apply"/>
/// are the two halves of a write that the old single call held under ONE lock: "may this source take it" and
/// "does the port take it". <see cref="Remember"/> is the memory, and it is separate because this axis remembers
/// the opposite way round from the GPU and Curve-Optimizer axes: THOSE remember before they write (a refused
/// write still leaves the user's preset in the file), THIS one remembers only what ACTUALLY LANDED, because the
/// remembered value here is not a setting the user typed — it is a record of the mode the machine is in, and a
/// mode the port refused is not one it is in. That order is the use case's rule (see <see cref="SwitchProfile"/>),
/// and it can only be the use case's while the two halves are separately callable.</summary>
public interface IProfileTarget
{
    /// <summary>Whether the LIVE power source offers <paramref name="profile"/> right now. A profile the source
    /// does not offer must never be written (the battery set has no Turbo; the AC set has no Eco), so this is the
    /// gate the use case consults BEFORE anything is written. Reads the effective source, so it is the service's
    /// to answer — the same question <c>LaptopService.ApplyProfile</c> has always asked. It is ALSO the capability
    /// member for the absent-port case: false when this machine has no port, so the no-op a portless machine has
    /// always had is preserved without ever reaching <see cref="Apply"/> (an absent port is a capability fact,
    /// NOT the <see cref="PortWriteFailedException"/> a present port's refusal throws).</summary>
    bool CanApply(PerformanceProfile profile);

    /// <summary>Write <paramref name="profile"/> to the port and return the profile that LANDED, or null when this
    /// machine has NO power-profiles port at all (a capability fact, and the same no-op a portless machine has
    /// always given). A port that is PRESENT but refuses or fails THROWS
    /// <see cref="PortWriteFailedException"/> — the refusal the old <c>(null, error)</c> pair carried — because a
    /// user action reaching here has already passed the UI gate (the segment was enabled) and a refusal is a
    /// violated precondition, not normal operation; unlike a pair, the thrown exception always carries its own
    /// reason (docs/open-decisions.md §2). <see cref="CanApply"/> is the capability member the use case consults
    /// FIRST, so the absent-port case is decided without calling this. This is a port call and nothing else; the
    /// memory is <see cref="Remember"/>. It does NOT re-check availability: the use case asked
    /// <see cref="CanApply"/> first, and a caller that skips that gate gets the port's own refusal, which is the
    /// honest answer.</summary>
    PerformanceProfile? Apply(PerformanceProfile profile);

    /// <summary>Remember <paramref name="profile"/> as the live source's mode — a graph write, under the lock,
    /// persisted (the plain pick's memory: this base, Turbo off). Called by the use case ONLY for a profile that
    /// landed, ONLY when the caller did not ask for a transient switch, and NOT for the Turbo switch, whose memory
    /// is a FLAG over a preserved base rather than this profile id — that action remembers itself around a
    /// transient write.</summary>
    void Remember(PerformanceProfile profile);
}

/// <summary>What the profile switch needs from whoever owns the LIGHT claim: the single announcement that a
/// profile LANDED, so the port write and the lighting repaint are one action rather than two steps a caller can
/// separate. Implemented by <c>UI/LightingCoordinator.cs</c>, which is the one place that knows the post-switch
/// re-apply state (<c>OnProfileApplied</c> records <c>_pendingId</c>, so the ~1 s refresh poll does not repaint
/// the palette the firmware has already flashed).
///
/// THE PROFILE THAT LANDED is what the implementation is handed — never the one the caller asked for — so the
/// announcement cannot be made for a switch that did not happen. The implementation is handed the Domain value
/// and nothing else, which is why this contract can live in Application.
///
/// WHICH SWITCHES ANNOUNCE. Every landed switch does, transient or not: the sweep's forced profile and its
/// restore are the cases the old code missed, and there is deliberately no "quiet" mode a future caller could
/// use to repeat that omission.</summary>
public interface IProfileAnnouncer
{
    /// <summary>A profile was just applied by us; paint the lighting from it now, in the same instant as the
    /// firmware's own palette flash — the palette, then the LANDED profile's stored zones over it — and record
    /// the claim that suppresses the stale refresh passes. The landed profile is what the implementation is
    /// handed, so it can resolve that profile's own mode door without a hardware read; repainting the section's
    /// cache instead would put the mode being LEFT back over the flash (a target saved at brightness 0 staying
    /// lit).</summary>
    void OnProfileApplied(PerformanceProfile applied);
}

/// <summary>Switching the performance profile as a use case: make the machine sit in <paramref name="profile"/>,
/// and — unless the switch is transient — remember it as the live source's mode; then announce the profile that
/// landed so the lighting repaints from it in the same instant.
///
/// WHAT IT DECIDES, and each half answers a failure the four hand-written call sites could each get wrong
/// (a pick, the tray, the hotkey, the Turbo switch — and, since it exists, the guided sweep's temporary force and
/// its restore):
/// <list type="number">
/// <item><b>An unavailable profile is not written.</b> The source gate is asked first, so a segment the UI has
/// greyed out cannot be written by a path that forgot to check — the same refusal the section's optimistic
/// selection rolls back on. THIS IS ALSO THE ABSENT-PORT GATE: <see cref="IProfileTarget.CanApply"/> answers
/// false when there is no port, so the no-op a portless machine has always had is preserved WITHOUT calling
/// <see cref="IProfileTarget.Apply"/>, and a missing port is never reported as a refusal.</item>
/// <item><b>Only what LANDED is remembered.</b> The memory records the mode the machine is in, so a write the
/// port refused (which now THROWS out of <see cref="IProfileTarget.Apply"/>) leaves the remembered mode untouched
/// rather than pointing the source at a profile it is not in. This is the opposite order from
/// <see cref="ApplyGpuOffsets"/> and <see cref="ApplyUndervolt"/>, and the difference is real: those remember a
/// SETTING, this remembers a STATE.</item>
/// <item><b>TRANSIENT is the same act without the memory.</b> The sweep sits the machine in Turbo for the
/// duration of a run and puts it back afterwards; it must switch the hardware for real (the probe measures the
/// real power envelope) and must NOT touch the user's remembered mode. Stating that here is what lets the sweep
/// reuse this operation instead of opening a second, unannounced write path.</item>
/// <item><b>THE ANNOUNCEMENT IS PART OF THE SWITCH, not a step the caller may forget.</b> The port write makes
/// the firmware repaint the lit zones the instant it lands, and the app has to record that claim so its own
/// ~1 s refresh poll does not repaint the palette a second time (~the double flash). Owning both halves here is
/// the whole reason this use case exists: the sweep's forced profile and its restore are switches like any
/// other, and routing them through the use case is what makes it impossible for a switch to bypass the claim —
/// which is exactly what the sweep used to do when it called the transient port write directly. The announcer
/// marshals to the UI thread itself, because the sweep runs off it.</item>
/// </list>
///
/// WHAT IT STILL DOES NOT OWN. The profile the switch must land is the caller's, and the DECISIONS that shape a
/// whole action — the Turbo base capture, the per-source restore on a power-source change — stay with the layer
/// that owns that state (<c>LaptopService</c>). This use case is one switch, not a scheduler.
///
/// WHAT IT DOES NOT CATCH. A <see cref="PortWriteFailedException"/> from <see cref="IProfileTarget.Apply"/> is a
/// present port's refusal and escapes to the caller: a USER-action caller (the pick, the tray, the hotkey, the
/// Turbo switch) catches it at the action boundary and shows its words, while a SYSTEM caller (the per-source
/// restore, the boot re-apply) catches it and turns it into its non-verdict outcome.</summary>
/// <remarks>
/// COALESCE WINDOW: how long a profile we JUST wrote suppresses a repeat write of that same target. The owner
/// reported «подключил зарядку и быстро поменял на turbo режим и подсветка моргнула дважды» — the per-source
/// restore on plug-in puts the machine in Turbo, and an immediate manual Turbo pick then re-sends the SAME target
/// and the firmware flashes the palette a second time. The refresh poll (~1 s battery / 3 s full pass, see
/// <c>PollSchedule.Period</c>/<c>BatteryPollSchedule</c>) has not read the port between the two local writes, so
/// the "already in that profile" guard in <c>LaptopService.ApplyStoredMode</c> — a PORT-read check — cannot see
/// the first write and dedup only a genuine no-op. This window is the missing record of "we just wrote X":
/// <see cref="Run"/> remembers the target it last WROTE, and a second switch to that same target inside the window
/// is treated as the user's action already being satisfied — the port is not written again and nothing is
/// announced, so the palette flashes once.
///
/// WHY 5 SECONDS, and why a named constant rather than a value at the call site. The lower bound is the
/// poll latency the dedup must cover: the full refresh pass is every 3 s (<c>PollSchedule.Period</c>), and a
/// source change is noticed by that pass, so the window must comfortably exceed one pass or the second write can
/// arrive after the poll already caught up and the port guard would have done the job anyway (in which case this
/// adds nothing but is harmless). The 5 s matches the lighting suppression windows that already bracket this
/// latency — <c>LightingCoordinator.WakeTailSeconds</c> and <c>PendingTimeoutSeconds</c> are both 5 s, and both are
/// justified there as "covers the poll interval so the tail lands inside it". Keeping the dedup window on the same
/// number means the profile-write seam and the light-claim seam agree about what "a short window" is; two
/// different constants would be two chances to disagree. The UPPER bound is a deliberate later re-switch: a user
/// who picks Turbo, and picks Turbo again more than 5 s later, gets a real write (the firmware flash is the
/// feedback that the pick was accepted). 5 s is short enough that a considered second press is not swallowed, and
/// long enough that the plug-in-then-pick race the owner hit (sub-second) always is.
///
/// KEYED ON THE RESOLVED TARGET and recorded WHEN THE WRITE IS TAKEN. This is the rule that keeps the dedup from
/// suppressing a NEEDED write: the sweep forces a performance profile and then restores the user's; if the two are
/// different ids the restore writes as it always did, and if they are the SAME id the restore would be a no-op on
/// the port guard anyway (see the sweep's own <c>restore.Id == forced.Id</c> short-circuit) — so the dedup can
/// never eat a genuine restore. A DIFFERENT target always writes. An ABSENT port stays a value (the gate answers
/// before this is consulted) and a present port's refusal still throws, because a deduplicated call returns the
/// already-applied profile as a SUCCESS without reaching the port.
/// </remarks>
public sealed class SwitchProfile(IProfileTarget target, IProfileAnnouncer announcer, Func<DateTime>? clock = null)
{
    /// <summary>The dedup window. 5 s: long enough to cover the poll latency the local writes outrun (~3 s full
    /// pass), and the same number the lighting coordinator already uses for its two suppression windows
    /// (<c>WakeTailSeconds</c>/<c>PendingTimeoutSeconds</c>), so the profile-write seam and the light-claim seam
    /// agree on what "a short window" means; short enough that a deliberate later re-switch still writes. The full
    /// justification is in the class remarks.</summary>
    private const double CoalesceWindowSeconds = 5;

    // The clock is a SEAM, not a dependency of the rule: the production value is UtcNow, and a test drives the
    // window edges (inside/outside) without sleeping five seconds, exactly as the coordinator's IsWakeTail takes
    // its timestamps as parameters. Kept behind a delegate so the rule below stays pure and directly assertable.
    private readonly Func<DateTime> _clock = clock ?? (static () => DateTime.UtcNow);

    // The record of the profile WE last wrote, and when. Read and written under a small dedicated lock rather
    // than a bare field: the switch is called from the UI thread (picks, tray, hotkey) AND from the pool (the
    // sweep, the per-source restore in the refresh pass), so two threads can race here. The lock is NOT held
    // across the port call — it covers only the timestamp check and the record, so it can never add contention to
    // the EC/WMI transaction the way a lock spanning <see cref="IProfileTarget.Apply"/> would.
    private readonly Lock _gate = new();
    private PerformanceProfile? _lastApplied;   // the profile last WRITTEN, or null before any write
    private DateTime _lastWriteAt;              // when that write was taken

    /// <summary>Whether a switch to <paramref name="requested"/> should be COALESCED into the write that produced
    /// <paramref name="lastApplied"/>: the same target, and inside the window. Pure and static so the rule can be
    /// asserted directly against literal timestamps (a desktop lifetime is not needed and no clock is read), and
    /// the identity test is by ID because that is the Domain value's identity — a re-read of the profile yields an
    /// equal record (records compare by value), and an opaque id is all the port promises.</summary>
    internal static bool IsCoalesced(PerformanceProfile? lastApplied, DateTime lastWriteAt, DateTime now,
                                     PerformanceProfile requested)
        => lastApplied is { } last
           && last.Id == requested.Id
           && (now - lastWriteAt).TotalSeconds < CoalesceWindowSeconds;

    /// <summary>Run the switch and report the profile that landed, or null when this machine has no port (or the
    /// live source does not offer it) — the same no-op as before. <paramref name="transient"/> skips the memory
    /// (the sweep's force/restore); a persistent switch remembers the landed profile as the live source's mode. A
    /// switch that landed is announced exactly once, whether it was transient or not. A present port's refusal
    /// THROWS <see cref="PortWriteFailedException"/> and is announced nothing, remembered nothing.
    ///
    /// A switch to the SAME target within <see cref="CoalesceWindowSeconds"/> of the last write is COALESCED: it
    /// reports success and returns the already-applied profile WITHOUT writing the port or announcing (no second
    /// palette flash). This is the local-write dedup the port-read guard cannot provide; see the window's comment.
    /// A different target always writes. <paramref name="transient"/> is deliberately NOT part of the key: the
    /// per-source restore and a manual pick of the same target are the same physical write and must coalesce
    /// across it, which is exactly the owner's plug-in-then-pick race.</summary>
    public PerformanceProfile? Run(PerformanceProfile profile, bool transient = false)
    {
        if (!target.CanApply(profile)) return null;

        // Dedup BEFORE the port call: if we wrote this same target a moment ago, the machine is already (being
        // put) in it, so a second write only makes the firmware flash the palette again. The RESOLVED id is what
        // was recorded (what landed on the port), so a backend whose Apply maps a request to a different profile
        // still dedups against the real one. Returning the already-applied profile is the honest "already in it"
        // result — the caller sees success, no error, and no announcement.
        lock (_gate)
        {
            if (IsCoalesced(_lastApplied, _lastWriteAt, _clock(), profile))
            {
                // A PERSISTENT switch still records what landed. The memory is a record of the mode the machine
                // is IN, and it is in this one — the record was made by the write this call is deduplicating
                // against, but it must survive a pick that arrives as a persistent switch (a transient restore
                // does not Remember, and a user's later pick of the same mode should leave the slot naming it).
                // The ANNOUNCEMENT is deliberately skipped, which is the whole point: no second palette flash.
                if (!transient) target.Remember(_lastApplied!);
                return _lastApplied;
            }
        }

        var applied = target.Apply(profile);
        if (applied is null) return null;
        if (!transient) target.Remember(applied);

        // Record the write the instant it is TAKEN (not when a later read confirms it), so a second local switch
        // inside the window is deduplicated even though the poll has not caught up to the first.
        lock (_gate)
        {
            _lastApplied = applied;
            _lastWriteAt = _clock();
        }

        announcer.OnProfileApplied(applied);
        return applied;
    }
}
