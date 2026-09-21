namespace AcerHelper.Infrastructure.Vendors.Generic;

// The blue-light APPLY SCHEDULE, in an UN-SUFFIXED file for the reason KwinTint.cs, AcerFanPort.cs,
// AcerProfilePorts.cs and DisplayTintChain.cs all state at length: the test project targets net10.0-windows while
// AcerHelper.csproj excludes **/*.Linux.cs from that TFM, so a rule left beside the Linux I/O cannot be compiled
// by the suite at all. It names no port, no compositor and no toolkit — the apply arrives as a delegate — so what
// is asserted here is the rule itself, not an imitation of it.
//
// WHY THERE IS A SCHEDULE TO KEEP. A level change is not a cheap write. KWin applies a CHANGED night temperature
// by walking to it (50 K per step over QUICK_ADJUST_DURATION = 2000 ms), and the apply VERIFIES what the
// compositor did by requiring its CURRENT temperature to equal the request (NightLightState.Holds, KwinTint.cs) —
// so the verification cannot succeed until that walk has finished and the link waits it out
// (KwinConfigFile.Commit, KwinTint.Linux.cs: 100 ms poll, 2500 ms ceiling).
//
// MEASURED CONSEQUENCE, AND THE DEFECT THIS FILE REMOVES: that wait used to run on the UI thread. The blue-light
// row is a ChoiceRowViewModel with no read-back, and a pick on such a row is applied INLINE
// (OptionsViewModel.OnSelectedIndexChanged's `_read == null` branch), so `svc.SetBlueLight` was reached — and held
// — on the UI thread. Every level change while the filter was already on could therefore freeze the window for up
// to the link's commit timeout, ~2 s, which is exactly what the owner saw. The first apply of a run is fast (the
// feature is being switched on, not re-aimed) and this schedule does not slow it down.

/// <summary>
/// Runs display-tint applies ONE AT A TIME, away from the caller's thread, and reports the outcome of the NEWEST
/// request only.
///
/// THE THREE RULES. They are separate, and dropping any one of them is a different bug:
///
///   1. OFF THE CALLER'S THREAD. The apply is the slow half and the caller is the UI thread. Nothing about the
///      level the user picked needs the UI thread to be free of it: the row already shows their choice, and the
///      result is reported back through a callback the caller marshals itself. (This class deliberately does NOT
///      marshal — posting is the toolkit's, and Infrastructure may not name it; see OptionsAssembler's `post`.)
///
///   2. ONE AT A TIME. An apply is a read-modify-write of state the PORT owns — KwinConfigTint records the user's
///      prior values in the config, writes ours, and rolls the config back when the verification fails — so two
///      applies running at once can interleave those writes, and the loser's roll-back is what the compositor is
///      left holding. Serialized, they cannot: each apply begins after the previous one has finished, so the state
///      the last one reads is the state the one before it left. That is also what makes rule 3 meaningful — the
///      newest request is genuinely applied LAST, so its outcome IS the state the user is left in.
///
///   3. THE NEWEST REQUEST IS THE ONE THAT REPORTS. Rule 2 alone would still report a superseded apply's failure:
///      a slow apply(1) that fails AFTER the user has already picked 2 says nothing about the level they are now
///      on, and telling them "failed" would be a lie about a level they have left — while the apply that actually
///      decided their screen is still in flight. So an apply whose id is no longer the newest reports NOTHING, and
///      the newest is always evaluated (never skipped), which is what keeps the compositor on the user's level.
/// </summary>
internal sealed class TintApplyPolicy
{
    /// <summary>Orders the applies and carries the identity of the newest request. ONE lock for both, because the
    /// two are read together: "am I still the newest" is answered as the next request is appended, so an apply
    /// cannot be superseded between deciding it is current and being reached by the chain.</summary>
    private readonly Lock _gate = new();

    /// <summary>The tail of the chain: every apply is appended to the one before it, so no two ever overlap
    /// (rule 2). The same shape as <c>HwSerial</c> (OptionsViewModel.cs), which serializes one control's writes
    /// for exactly this reason.</summary>
    private Task _tail = Task.CompletedTask;

    /// <summary>The id of the newest submitted request — the only one whose outcome is reported (rule 3).</summary>
    private long _newest;

    /// <summary>Queue one apply. Returns as soon as it is queued: the caller's thread is never held for the
    /// apply (rule 1), and the level asked for is whatever <paramref name="apply"/> was built over.</summary>
    /// <param name="apply">The apply itself, answering whether the screen actually changed — <c>IDisplayTint.Apply</c>
    /// in production. Injected rather than named so this class carries the SCHEDULE and no transport.</param>
    /// <param name="onApplied">Where the outcome goes, or null to stay silent (the startup re-apply does: it has
    /// no reader, and the app has never reported that one). Called on this schedule's own thread and ONLY for the
    /// newest request, so a caller that wants to touch a control MUST marshal it itself.</param>
    internal void Submit(Func<bool> apply, Action<bool>? onApplied = null)
    {
        long id;
        lock (_gate)
        {
            id = ++_newest;
            _tail = _tail.ContinueWith(_ => Run(id, apply, onApplied), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Wait for everything submitted SO FAR to finish, up to <paramref name="timeout"/>.
    ///
    /// FOR TEARDOWN, and it exists because of rule 1: while an apply ran on the UI thread it could not be in
    /// flight when the app was torn down — the same thread did both. Off the thread, it can, and the two reach
    /// the same state: the apply writes the config and verifies it, the release (the port's own
    /// <c>Dispose</c>, reached through <c>Device.Dispose</c>) restores the user's values and verifies THAT. Left
    /// to interleave they could leave the compositor holding a rolled-back config with the app's markers gone,
    /// which is the one thing the tint link promises never to leave behind.
    ///
    /// The timeout is the caller's because the bound belongs to the transport: the link's own commit wait is
    /// 2500 ms, and this is only the ceiling on top of it. It also bounds what teardown can cost.
    /// </summary>
    /// <returns>True when everything queued so far finished, false when the wait ran out. A step never faults —
    /// <see cref="Run"/> absorbs its own throws — so there is nothing else this could mean.</returns>
    internal bool Drain(TimeSpan timeout)
    {
        Task tail;
        lock (_gate) tail = _tail;      // copied inside, waited outside: a drain must not block the submits it
        return tail.Wait(timeout);      // does not own (a test drains while another thread may still be queuing)
    }

    /// <summary>
    /// One step of the chain: the apply, then the report — the report only if nothing has been submitted since
    /// (rule 3).
    ///
    /// NOTHING ESCAPES THIS METHOD, and that is rule 2's other half rather than politeness: a step that threw
    /// would fault the chain's tail, and a wedged chain is a blue-light row that has silently stopped working for
    /// the rest of the run. A throwing apply is therefore a FAILED apply, exactly as <c>LaptopService.Attempt</c>
    /// reads one for every other port in this tree — and the reason is reported as absent, because a throw is not
    /// a call that ran to completion and <c>IDisplayTint</c> carries no reason at all.
    /// </summary>
    private void Run(long id, Func<bool> apply, Action<bool>? onApplied)
    {
        bool ok;
        try { ok = apply(); }
        catch { ok = false; }

        if (onApplied is null) return;
        lock (_gate)
        {
            // Superseded: the user has already picked another level, and this outcome is about the one they left.
            // The apply still RAN — rule 2 applies everything in order, so the compositor is walked to the level
            // they chose and never left short of it — it is only the message that is dropped.
            if (id != _newest) return;
        }

        try { onApplied(ok); } catch { /* the caller's report, the caller's problem — see the class docs */ }
    }
}
