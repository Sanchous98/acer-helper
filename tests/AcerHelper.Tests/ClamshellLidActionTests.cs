using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// The two pure rules inside clamshell's lid-action take-over: what gets written back when the take-over is
/// given up (<c>RestoreAction</c>), and whether a lid action found with nothing captured this session is our
/// own crash leftover (<c>IsCrashLeftover</c>).
///
/// Worth a file because both are safety invariants whose failure is invisible to this suite AND expensive on
/// the machine: the write they feed is a real <c>PowerWriteACValueIndex</c> + <c>PowerSetActiveScheme</c>, so
/// a wrong answer silently rewrites the owner's lid-close policy. Getting <c>RestoreAction</c> wrong in the
/// obvious way — writing the capture back verbatim — makes every disable a no-op and leaves the lid unable to
/// sleep at all, which is the exact failure the source comment spends four lines warning about.
///
/// <c>RestoreAction</c>/<c>IsCrashLeftover</c> and the two action constants were widened from
/// <c>private</c> to <c>internal</c> for this file — visibility only, following the
/// <c>RyzenCurveOptimizer.Encode</c>/<c>CoreArg</c>/<c>GpuMargin</c> precedent.
///
/// THE CLASS MUST NEVER BE INSTANTIATED HERE. <c>new Clamshell()</c> subscribes to this machine's real
/// display-settings and power-mode events (its constructor is literally <c>=> Subscribe()</c>), and the
/// <c>Evaluate()</c> reachable from those callbacks writes the lid action. Only the static members are
/// touched, and a static call never runs the instance constructor — that is what makes this file safe. The
/// state machine itself (enable/disable, the <c>_applied</c> dedupe, the AC-and-external-display predicate)
/// is deliberately NOT covered: it has no injection point, so covering it needs the <c>IClamshellOs</c>
/// hook-set the survey records as the next, larger step. This file is the part that needs no such seam.
/// </summary>
public class ClamshellLidActionTests
{
    // The powrprof AC lid-action values. Repeated here as literals on purpose: 2 and 3 are the user's own
    // settings (Hibernate / Shut down), and the point of these tests is that they survive the round trip.
    private const uint DoNothing = 0;   // what we write to take over
    private const uint Sleep     = 1;
    private const uint Hibernate = 2;
    private const uint ShutDown  = 3;

    // ================= RestoreAction — what goes back when we give up the take-over =================

    /// <summary>The capture is written back verbatim for every real setting, because the take-over must be
    /// invisible to the user: leave Sleep as Sleep, but also leave Hibernate and Shut down alone rather than
    /// normalizing them to Sleep. The comment above the helper claims 2 and 3 are preserved; this is where
    /// that claim is load-bearing.</summary>
    [Theory]
    [InlineData(Sleep, Sleep)]
    [InlineData(Hibernate, Hibernate)]
    [InlineData(ShutDown, ShutDown)]
    public void ACapturedActionIsRestoredVerbatim(uint captured, uint expected)
    {
        Assert.Equal(expected, Clamshell.RestoreAction(captured));
    }

    /// <summary>The one exception, and the class's whole safety invariant: a capture of DO_NOTHING means the
    /// lid was already not-doing-anything when we took over (nothing we wrote, or a previous session that died
    /// and left it). Writing it back would make the disable a silent no-op and pin the lid open forever, so it
    /// is mapped to Sleep instead. Reddened by reverting to <c>captured</c>, and by mapping to DO_NOTHING.</summary>
    [Fact]
    public void ACapturedStayAwakeBecomesSleep_NeverStayAwake()
    {
        Assert.Equal(Sleep, Clamshell.RestoreAction(DoNothing));
    }

    /// <summary>Stated as the invariant rather than as one case, so it still holds if the mapping is ever
    /// re-pointed at some other value: whatever is restored, it is never our own stay-awake.</summary>
    [Theory]
    [InlineData(DoNothing)]
    [InlineData(Sleep)]
    [InlineData(Hibernate)]
    [InlineData(ShutDown)]
    public void ARestoredActionIsNeverOurOwnStayAwake(uint captured)
    {
        Assert.NotEqual(Clamshell.LID_DO_NOTHING, Clamshell.RestoreAction(captured));
    }

    /// <summary>The constants themselves, since the helpers are defined in terms of them and a swap would keep
    /// every relation above intact: <c>RestoreAction(DoNothing) == Sleep</c> still holds if both are flipped to
    /// 1 and 0. These are wire values from powrprof, not free choices, so they get pinned directly.</summary>
    [Fact]
    public void TheActionConstantsAreThePowerPolicyValues()
    {
        Assert.Equal(DoNothing, Clamshell.LID_DO_NOTHING);
        Assert.Equal(Sleep, Clamshell.LID_SLEEP);
    }

    // ================= IsCrashLeftover — the uncaptured path's single write =================

    /// <summary>Finding our own stay-awake with nothing captured this session means a previous session died
    /// before restoring: the lid is stuck, and this is the one case where writing on the way out is correct.
    /// Reddened by inverting the comparison or by returning false.</summary>
    [Fact]
    public void OurOwnStayAwakeWithNothingCapturedIsACrashLeftover()
    {
        Assert.True(Clamshell.IsCrashLeftover(DoNothing));
    }

    /// <summary>The reason the other branch exists at all: a real user setting found on the way out is NOT
    /// ours to touch, and reporting it as a leftover is what would clobber it (the source comment records that
    /// writing Sleep here did exactly that on a startup take-over while undocked).</summary>
    [Theory]
    [InlineData(Sleep)]
    [InlineData(Hibernate)]
    [InlineData(ShutDown)]
    public void AUsersOwnSettingIsNotOursToRepair(uint current)
    {
        Assert.False(Clamshell.IsCrashLeftover(current));
    }

    /// <summary>A scheme we could not read — or no active scheme — is not a leftover. The nullable parameter
    /// exists so an unreadable power policy reaches this rule instead of being defaulted to 0 somewhere above,
    /// which is the accident that would turn "cannot read" into "our DO_NOTHING is stuck, repair it" and write
    /// to a machine we could not even inspect.</summary>
    [Fact]
    public void AnUnreadableActionIsNotALeftover()
    {
        Assert.False(Clamshell.IsCrashLeftover(null));
    }

    /// <summary>Both rules agree on the one input that matters, and this is the invariant to keep if either is
    /// ever re-pointed: whatever a stale DO_NOTHING is repaired to, it is a value that would itself be restored
    /// verbatim — the repair cannot leave a second stay-awake behind for the next session to trip over.</summary>
    [Fact]
    public void TheRepairValueIsOneThatWouldItselfBeRestored()
    {
        Assert.True(Clamshell.IsCrashLeftover(DoNothing));
        Assert.Equal(Sleep, Clamshell.RestoreAction(DoNothing));
        Assert.Equal(Sleep, Clamshell.RestoreAction(Sleep));
    }
}
