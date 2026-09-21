using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// THE BLUE-LIGHT APPLY SCHEDULE, driven through injected I/O (<c>Infrastructure/Vendors/Generic/TintApplyPolicy.cs</c>).
///
/// WHY IT IS WORTH ITS OWN FILE: the defect this schedule removes is a ~2 s freeze of the UI thread that no fake
/// could have caught, because a fake sees the CALL a port received and never the THREAD it arrived on. So the
/// three rules the schedule owns are asserted here as properties of the schedule itself, over a delegate that has
/// no port behind it at all:
///
///   * <b>off the caller's thread</b> — asserted without a stopwatch, by a caller that returns while the apply is
///     provably still running and blocked (<see cref="AnApplyDoesNotHoldTheCaller"/>, <see cref="Drain_AnswersWhetherItWaitedItOut"/>);
///   * <b>one at a time, in order, last one wins</b> — asserted as a TRACE, so an overlap cannot hide behind a
///     final value that happens to be right (<see cref="RapidChanges_AreAppliedOneAtATime_InOrder"/>, the shape
///     <c>CurveOptimizerPolicyTests</c> uses for its mailbox sequence);
///   * <b>only the newest request reports</b> — the rule the owner's complaint turns into, because a slow apply
///     that has been superseded must not tell the user their screen failed while the level they are actually on is
///     still being applied (<see cref="ASupersededApply_IsNotReported"/>).
///
/// WHAT IS NOT PROVED HERE, said plainly so a green suite is not read as more: the schedule knows nothing about
/// KWin, the config route or the verification read-back. That a slow level change really is what the compositor
/// costs, and that a failed verification really arrives as <c>false</c>, are measured facts of the link, recorded
/// in <c>KwinTint.cs</c>/<c>KwinTint.Linux.cs</c> and pinned by <c>KwinTintTests</c>. What is proved here is that
/// the app can no longer wait for them on the UI thread and can no longer report the wrong one.
/// </summary>
public class TintApplyPolicyTests
{
    /// <summary>Generous on purpose: every wait below is a rendezvous, not a duration — a value that has to be
    /// reached AT ALL, so a loaded machine must not be able to fail a correct schedule.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Rule 1, asserted WITHOUT a clock. The apply blocks on a gate we hold, so if <c>Submit</c> had made the
    /// apply on this thread it could not have returned at all — and the gate would never be released, because the
    /// release below is what the blocked line is waiting for. Reaching the assertion therefore proves the apply is
    /// on another thread; the latch proves it was still RUNNING when <c>Submit</c> came back, which is the half a
    /// fast apply would hide.
    /// </summary>
    [Fact]
    public void AnApplyDoesNotHoldTheCaller()
    {
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var policy = new TintApplyPolicy();

        // Long enough that no plausible pause between the two lines below can outlive it.
        policy.Submit(() => { release.Wait(TimeSpan.FromSeconds(5)); finished.Set(); return true; });

        Assert.False(finished.IsSet, "Submit did not return until the apply had finished — it ran on the caller's thread");

        release.Set();
        Assert.True(policy.Drain(Patience), "the apply never finished");
        Assert.True(finished.IsSet, "the apply never ran at all, so the assertion above proved nothing");
    }

    /// <summary>
    /// Rules 2 and 3 together, as a trace. The first apply is IN FLIGHT and blocked when the second is submitted —
    /// which is the situation the whole schedule exists for, two picks inside one slow apply — and what follows is
    /// the literal order of events: never overlapping, never out of order, and the level the user chose LAST is the
    /// one applied last, so the compositor is left on their level and not on the one they left.
    /// </summary>
    [Fact]
    public void RapidChanges_AreAppliedOneAtATime_InOrder()
    {
        using var release = new ManualResetEventSlim();
        var trace = new List<string>();
        var policy = new TintApplyPolicy();

        policy.Submit(() => { trace.Add("enter 3"); release.Wait(Patience); trace.Add("exit 3"); return true; });
        policy.Submit(() => { trace.Add("enter 5"); trace.Add("exit 5"); return true; });

        release.Set();
        Assert.True(policy.Drain(Patience), "the queue never drained");

        Assert.Equal(["enter 3", "exit 3", "enter 5", "exit 5"], trace);
    }

    /// <summary>
    /// Rule 3, and the failure the rule is shaped around: the first level CHANGE FAILS, but by the time it fails
    /// the user has already picked another level. That failure is about a screen they have left, and the apply
    /// that decides the screen they are on has not run yet — so reporting it would be a lie AND a lie the user
    /// could act on. It reports nothing; the newest apply reports, and it is the newest regardless of whether it
    /// SUCCEEDED (a superseded success is equally uninteresting — the row is silent on success either way).
    /// </summary>
    [Fact]
    public void ASupersededApply_IsNotReported()
    {
        using var release = new ManualResetEventSlim();
        var applied = new List<int>();
        var reported = new List<(int Level, bool Ok)>();
        var policy = new TintApplyPolicy();

        policy.Submit(() => { applied.Add(1); release.Wait(Patience); return false; }, ok => reported.Add((1, ok)));
        policy.Submit(() => { applied.Add(2); return true; }, ok => reported.Add((2, ok)));

        release.Set();
        Assert.True(policy.Drain(Patience), "the queue never drained");

        // Both ran — a superseded apply is not SKIPPED, it is only quiet, which is what keeps rule 2's "each apply
        // starts from what the previous one left" true and the compositor on the level the user picked.
        Assert.Equal([1, 2], applied);
        Assert.Equal([(2, true)], reported);
    }

    /// <summary>...and with nothing superseding it, a failed apply is reported — the half that must not be lost
    /// to rule 3. The reason is absent rather than invented: <c>IDisplayTint.Apply</c> answers a bare bool, so
    /// there is no reason to carry (the same "false, null" a port shape with no LastError produces).</summary>
    [Fact]
    public void AFailedApply_IsReported()
    {
        var reported = new List<bool>();
        var policy = new TintApplyPolicy();

        policy.Submit(() => false, ok => reported.Add(ok));
        Assert.True(policy.Drain(Patience), "the queue never drained");

        Assert.Equal([false], reported);
    }

    /// <summary>
    /// A THROWING apply is a FAILED apply, and the schedule survives it. Both halves matter: a port that throws
    /// must reach the user as a failure rather than as an exception on a pool thread (nothing catches one there,
    /// and the process ends), and the chain must still run afterwards — rule 2 is worth nothing if one bad step
    /// leaves the blue-light row silently dead for the rest of the run.
    /// </summary>
    [Fact]
    public void AThrowingApply_IsAFailure_AndTheScheduleSurvivesIt()
    {
        var reported = new List<bool>();
        var applied = new List<int>();
        var policy = new TintApplyPolicy();

        policy.Submit(() => throw new InvalidOperationException("the compositor refused"), ok => reported.Add(ok));
        Assert.True(policy.Drain(Patience), "the queue never drained");
        Assert.Equal([false], reported);

        policy.Submit(() => { applied.Add(1); return true; }, ok => reported.Add(ok));
        Assert.True(policy.Drain(Patience), "the queue never drained");

        Assert.Equal([1], applied);
        Assert.Equal([false, true], reported);
    }

    /// <summary>
    /// Rule 1's other half, and the one <c>LaptopService.Dispose</c> leans on: <see cref="TintApplyPolicy.Drain"/>
    /// answers WHETHER it waited the work out. Teardown must let an in-flight apply finish before it restores the
    /// user's settings out of the same configuration (the apply and the release would otherwise interleave, which
    /// is how a tint is left behind), and it is bounded — so it has to be able to tell "done" from "still going"
    /// rather than assume either.
    /// </summary>
    [Fact]
    public void Drain_AnswersWhetherItWaitedItOut()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var policy = new TintApplyPolicy();

        policy.Submit(() => { entered.Set(); release.Wait(Patience); return true; });

        Assert.True(entered.Wait(Patience), "the apply never started");
        Assert.False(policy.Drain(TimeSpan.Zero), "Drain claimed an apply it had not waited for had finished");

        release.Set();
        Assert.True(policy.Drain(Patience), "the apply never finished");
    }
}
