using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// THE PROFILE SWITCH AS A USE CASE (<c>Application/ProfileSwitch.cs</c>), pinned through its two contracts with
/// stubs — no <c>LaptopService</c>, no lighting coordinator, no port.
///
/// WHY THIS FILE EXISTS. The switch used to be a lock inside <c>LaptopService.ApplyProfile</c>, and the lighting
/// announcement was a SEPARATE step at each call site (AppController's pick, tray, hotkey and Turbo paths, and —
/// the bug — NOT at all in the guided sweep's forced profile). Splitting the write from the announcement is what
/// let the sweep flash the palette twice: the firmware repainted on the port write, and the ~1 s refresh pass
/// repainted again because nothing had recorded the light claim. The use case now owns BOTH halves, so the rules
/// to pin are: the source gate runs first, only what LANDED is remembered, a transient switch skips the memory,
/// and EVERY landed switch is announced exactly once.
///
/// THE STUBS RECORD WHAT THEY WERE ASKED, IN ORDER, because the order is the guarantee: a switch that announced
/// before it knew whether the write landed would claim a profile the machine is not in.
/// </summary>
public class ProfileSwitchUseCaseTests
{
    private sealed class StubTarget : IProfileTarget
    {
        public bool CanApplyResult { get; set; } = true;
        public bool HasPort { get; set; } = true;

        /// <summary>The profile the port reports for what was requested. Lets a force/restore pair land distinct
        /// profiles from one stub.</summary>
        public Func<PerformanceProfile, PerformanceProfile?>? AppliedFor { get; set; }

        /// <summary>The last write's refusal, when a test arranges one: <see cref="Apply"/> THROWS it (a present
        /// port's refusal is exceptional), which is the shape the real target now has.</summary>
        public PortWriteFailedException? Refusal { get; set; }

        public List<string> Calls { get; } = [];
        public List<PerformanceProfile> Remembered { get; } = [];

        public bool CanApply(PerformanceProfile profile)
        {
            Calls.Add("can:" + profile.Id);
            return HasPort && CanApplyResult;   // no port is the same no-op the gate has always given
        }

        public PerformanceProfile? Apply(PerformanceProfile profile)
        {
            Calls.Add("apply:" + profile.Id);
            if (Refusal is { } refused) throw refused;
            return AppliedFor is { } f ? f(profile) : profile;
        }

        public void Remember(PerformanceProfile profile) { Calls.Add("remember:" + profile.Id); Remembered.Add(profile); }
    }

    private sealed class StubAnnouncer : IProfileAnnouncer
    {
        public List<PerformanceProfile> Announced { get; } = [];
        public void OnProfileApplied(PerformanceProfile applied) => Announced.Add(applied);
    }

    /// <summary>A persistent switch writes the port, remembers ONLY what landed, and announces it once.</summary>
    [Fact]
    public void APersistentSwitch_WritesRemembersAndAnnouncesOnce()
    {
        var target = new StubTarget { AppliedFor = _ => TestProfiles.Balanced };
        var announcer = new StubAnnouncer();

        var applied = new SwitchProfile(target, announcer).Run(TestProfiles.Performance);

        Assert.Equal(TestProfiles.Balanced, applied);           // what the port REPORTED, not what was asked for
        Assert.Equal(["can:performance", "apply:performance", "remember:balanced"], target.Calls);
        Assert.Equal([TestProfiles.Balanced], announcer.Announced);
    }

    /// <summary>A TRANSIENT switch is the same act without the memory — the sweep's force/restore — but it is
    /// still announced, which is exactly what the old sweep bypassed.</summary>
    [Fact]
    public void ATransientSwitch_SkipsTheMemoryButStillAnnounces()
    {
        var target = new StubTarget { AppliedFor = _ => TestProfiles.Balanced };
        var announcer = new StubAnnouncer();

        var applied = new SwitchProfile(target, announcer).Run(TestProfiles.Turbo, transient: true);

        Assert.Equal(TestProfiles.Balanced, applied);
        Assert.Equal(["can:turbo", "apply:turbo"], target.Calls);   // no remember
        Assert.Equal([TestProfiles.Balanced], announcer.Announced);
    }

    /// <summary>The source gate is asked FIRST: a profile the live source does not offer is never written,
    /// remembered or announced. It is ALSO the absent-port gate: <c>CanApply</c> false on a portless machine
    /// returns null without ever reaching the port, so a missing port stays a no-op — never a throw.</summary>
    [Fact]
    public void AnUnavailableProfile_IsNotWrittenRememberedOrAnnounced()
    {
        var target = new StubTarget { CanApplyResult = false };
        var announcer = new StubAnnouncer();

        var applied = new SwitchProfile(target, announcer).Run(TestProfiles.Eco);

        Assert.Null(applied);
        Assert.Equal(["can:eco"], target.Calls);
        Assert.Empty(announcer.Announced);
    }

    /// <summary>AN ABSENT PORT IS A CAPABILITY FACT, NOT A REFUSAL: with no port the gate answers false, the
    /// port is never called, and nothing is thrown — the same no-op a portless machine has always shown.</summary>
    [Fact]
    public void WithNoPort_IsANoOp_AndDoesNotThrow()
    {
        var target = new StubTarget { HasPort = false };
        var announcer = new StubAnnouncer();

        var applied = new SwitchProfile(target, announcer).Run(TestProfiles.Performance);

        Assert.Null(applied);
        Assert.Equal(["can:performance"], target.Calls);
        Assert.Empty(announcer.Announced);
    }

    /// <summary>A PRESENT port that refuses THROWS <see cref="PortWriteFailedException"/> carrying the port's own
    /// words: nothing is remembered and nothing is announced, and the refusal escapes to the caller's boundary.</summary>
    [Fact]
    public void ARefusedWrite_Throws_AndIsNeitherRememberedNorAnnounced()
    {
        var target = new StubTarget { Refusal = new PortWriteFailedException("profile switch", "EC refused") };
        var announcer = new StubAnnouncer();

        var ex = Assert.Throws<PortWriteFailedException>(
            () => new SwitchProfile(target, announcer).Run(TestProfiles.Performance));

        Assert.Equal("profile switch", ex.Operation);
        Assert.Equal("EC refused", ex.Reason);
        Assert.Equal(["can:performance", "apply:performance"], target.Calls);
        Assert.Empty(target.Remembered);
        Assert.Empty(announcer.Announced);
    }

    /// <summary>A port that THROWS is the same exceptional shape: the target converts it to a
    /// <see cref="PortWriteFailedException"/> with NO reason (a throw assigns nothing, so there are no words of
    /// its own to report), it is never remembered or announced, and it escapes to the caller.</summary>
    [Fact]
    public void AThrowingPort_ThrowsTheSameExceptionShape_WithNoReason()
    {
        var target = new StubTarget { Refusal = new PortWriteFailedException("profile switch", null) };
        var announcer = new StubAnnouncer();

        var ex = Assert.Throws<PortWriteFailedException>(
            () => new SwitchProfile(target, announcer).Run(TestProfiles.Performance));

        Assert.Null(ex.Reason);
        Assert.Empty(target.Remembered);
        Assert.Empty(announcer.Announced);
    }

    /// <summary>THE SWEEP'S FORCE+RESTORE IS TWO SWITCHES, EACH ANNOUNCED ONCE. This is the double-flash rule
    /// at the use-case level: the force announces the forced profile, the restore announces the pre-sweep one,
    /// and neither is swallowed — but neither is the SECOND repaint the bug produced (the refresh pass's), which
    /// the announcer's claim suppresses rather than this use case.</summary>
    [Fact]
    public void AForceAndRestore_AnnouncesExactlyOncePerSwitch()
    {
        var target = new StubTarget();                            // the port reports what it was given
        var announcer = new StubAnnouncer();
        var sw = new SwitchProfile(target, announcer);

        sw.Run(TestProfiles.Turbo, transient: true);              // the sweep forces a performance profile
        sw.Run(TestProfiles.Balanced, transient: true);           // ...and restores the previous one

        Assert.Equal(2, announcer.Announced.Count);
        Assert.Equal([TestProfiles.Turbo, TestProfiles.Balanced], announcer.Announced);
        Assert.Empty(target.Remembered);                          // transient throughout: the slot never moved
    }
}
