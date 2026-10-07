using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// DEDUPLICATING REPEATED PROFILE WRITES. The owner reported «подключил зарядку и быстро поменял на turbo режим и
/// подсветка моргнула дважды» — plugging in the charger triggers the per-source restore (which may itself switch to
/// the remembered Turbo), and an immediate manual switch to the SAME Turbo makes the firmware flash the palette a
/// second time.
///
/// THE MECHANISM, reproduced by <see cref="ASourceRestoreThenAnImmediateManualPickOfTheSameMode_WritesOnce"/>: the
/// only dedup that existed was the "already in that profile" guard in <c>LaptopService.ApplyStoredMode</c>, and it
/// reads the PORT. Between two LOCAL writes the ~1 s/3 s refresh poll has not caught up, so the second write sees
/// the same old profile and re-sends the target — and each Acer profile <c>Set</c> makes the firmware repaint the
/// keyboard/lightbar palette (docs/lighting-an18-61.md), so the user sees two flashes for one intended state.
///
/// THE FIX is at the PROFILE-WRITE SEAM (<c>Application/ProfileSwitch.cs</c>): <c>SwitchProfile.Run</c> records the
/// profile it last WROTE and coalesces a second switch to the SAME target inside ~5 s — no port write, no
/// announcement (no second flash), but a success result. It covers every writer — pick, tray, hotkey, Turbo switch,
/// the sweep's transient force/restore and the per-source restore — because they all go through this one use case.
///
/// WHY THE ASSERTIONS ARE ON THE PORT'S CALL LOG AND THE ANNOUNCEMENT COUNT. One <c>IPowerProfiles.Set</c> is one
/// firmware palette repaint (docs/lighting-an18-61.md), and the announcement is the app's own palette send, so
/// <c>FakePowerProfiles.SetCallIds</c> plus the recorder ARE the write/flash timeline on the wire. The tests drive
/// the real path — <c>SyncPowerSource</c>/<c>ApplyStoredMode</c> and <c>ApplyProfile</c> — over the real service,
/// not a copy of the ordering.
/// </summary>
public class ProfileWriteDedupTests
{
    private static readonly BatteryInfoSnapshot OnAc = new() { State = BatteryState.Charging };

    /// <summary>Records the switch use case's announcement — the app's own palette send — so a test can count the
    /// palette repaints the app drove for a sequence of switches.</summary>
    private sealed class RecordingAnnouncer : IProfileAnnouncer
    {
        public List<PerformanceProfile> Announced { get; } = [];
        public void OnProfileApplied(PerformanceProfile applied) => Announced.Add(applied);
    }

    /// <summary>THE REPORTED TIMELINE. The AC slot remembers Turbo over Balanced while the machine sits in Quiet;
    /// the source becomes AC (the per-source restore writes Turbo), and the user IMMEDIATELY picks Turbo again.
    /// Before the fix that was TWO <c>Set</c> calls — and so two firmware palette flashes — for one target; after
    /// it, ONE write and ONE announcement, with the manual pick still reporting success.</summary>
    [Fact]
    public void ASourceRestoreThenAnImmediateManualPickOfTheSameMode_WritesOnce()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
                      },
            current: TestProfiles.Quiet);
        var announcer = new RecordingAnnouncer();
        f.Service.ProfileAnnouncer = announcer;

        f.SyncPowerSource.Run(OnAc);                       // plug in: the per-source restore lands Turbo (1 flash)
        var applied = f.Service.ApplyProfile(TestProfiles.Turbo);   // the rapid manual Turbo pick (must not flash)

        Assert.True(applied);                                            // "already in it" is a success
        Assert.Equal(["turbo"], f.Pp!.SetCallIds);                       // ONE port write, the target's
        Assert.Single(announcer.Announced);                              // ONE app palette send
        Assert.Equal(TestProfiles.Turbo, announcer.Announced[0]);
        Assert.Equal(TestProfiles.Turbo, f.Pp.CurrentProfile);           // the machine really is in Turbo
    }

    /// <summary>The differential control: a pick of a DIFFERENT target always writes. The dedup is keyed on the
    /// target, so it can never swallow a genuine mode change — only a repeat of the mode just written.</summary>
    [Fact]
    public void ASwitchToADifferentTarget_AlwaysWrites()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
                      },
            current: TestProfiles.Quiet);
        var announcer = new RecordingAnnouncer();
        f.Service.ProfileAnnouncer = announcer;

        f.SyncPowerSource.Run(OnAc);                       // restores Turbo
        f.Service.ApplyProfile(TestProfiles.Performance);  // a different target: a real switch

        Assert.Equal(["turbo", "performance"], f.Pp!.SetCallIds);
        Assert.Equal(2, announcer.Announced.Count);
    }

    /// <summary>A second switch to the same target AFTER the window is a deliberate re-switch and writes. The
    /// window is proved with the injected clock, so the edge is exercised without sleeping five seconds.</summary>
    [Fact]
    public void ASwitchToTheSameTarget_AfterTheWindow_WritesAgain()
    {
        var now = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        var target = new RecordingTarget();
        var announcer = new RecordingAnnouncer();
        var sw = new SwitchProfile(target, announcer, clock: () => now);

        sw.Run(TestProfiles.Turbo);                        // the first write, recorded at `now`
        now = now.AddSeconds(6);                           // past the 5 s window
        sw.Run(TestProfiles.Turbo);

        Assert.Equal(["turbo", "turbo"], target.Applied);  // both wrote
        Assert.Equal(2, announcer.Announced.Count);
    }

    /// <summary>Inside the window the same target coalesces: no second write and no second announcement, and the
    /// caller still gets the landed profile back as a success. This is the window edge opposite the one above.</summary>
    [Fact]
    public void ASwitchToTheSameTarget_InsideTheWindow_Coalesces()
    {
        var now = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        var target = new RecordingTarget();
        var announcer = new RecordingAnnouncer();
        var sw = new SwitchProfile(target, announcer, clock: () => now);

        var first = sw.Run(TestProfiles.Turbo);
        now = now.AddSeconds(4);                           // still inside 5 s
        var second = sw.Run(TestProfiles.Turbo);

        Assert.Equal(TestProfiles.Turbo, first);
        Assert.Equal(TestProfiles.Turbo, second);          // "already in it" == success, not null
        Assert.Equal(["turbo"], target.Applied);           // ONE write
        Assert.Single(announcer.Announced);                // ONE announcement (so ONE flash)
    }

    /// <summary>A TRANSIENT FORCE/RESTORE STILL BEHAVES. A forced-and-restored sequence changes target twice (Turbo
    /// then Balanced), so the dedup — keyed on the target — leaves both writes and both announcements intact; this
    /// is the edge the window must not swallow, asserted through the real service's switch.</summary>
    [Fact]
    public void ATransientForceAndRestore_AreDifferentTargets_AndBothWrite()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "balanced" },
                      },
            current: TestProfiles.Balanced);
        var announcer = new RecordingAnnouncer();
        f.Service.ProfileAnnouncer = announcer;

        // Force Turbo (transient) then restore the pre-force profile (different target) in quick succession.
        f.Service.ProfileSwitch.Run(TestProfiles.Turbo, transient: true);
        var restored = f.Service.ProfileSwitch.Run(TestProfiles.Balanced, transient: true);

        Assert.Equal(TestProfiles.Balanced, restored);
        Assert.Equal(["turbo", "balanced"], f.Pp!.SetCallIds);   // neither is deduplicated
        Assert.Equal(2, announcer.Announced.Count);
    }

    /// <summary>A TRANSIENT switch followed by a PERSISTENT switch of the same target inside the window still
    /// coalesces the write/flash, but the persistent half records the mode — the memory is a record of the mode the
    /// machine is IN, and the deduplicated write put it there. So the slot ends naming the target, exactly as the
    /// un-deduplicated persistent switch would have left it.</summary>
    [Fact]
    public void ATransientThenAPersistentSwitchOfTheSameTarget_Coalesces_ButStillRemembers()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = "quiet" } },
            current: TestProfiles.Balanced);
        var announcer = new RecordingAnnouncer();
        f.Service.ProfileAnnouncer = announcer;

        f.Service.ProfileSwitch.Run(TestProfiles.Turbo, transient: true);   // a transient force
        var applied = f.Service.ApplyProfile(TestProfiles.Turbo);  // a persistent pick of the same target

        Assert.True(applied);
        Assert.Equal(["turbo"], f.Pp!.SetCallIds);             // ONE write
        Assert.Single(announcer.Announced);                    // ONE flash
        Assert.Equal("turbo", f.Store.Settings.OnAc.BaseId);   // the persistent half still remembered it
        Assert.False(f.Store.Settings.OnAc.Turbo);
    }

    /// <summary>AN ABSENT PORT STAYS A VALUE and a refusal still throws: dedup sits after the capability gate, so
    /// with no port nothing is written and nothing throws (the same no-op as before); and a refusing port is not
    /// masked by a stale dedup record (a present port's refusal is exceptional and must reach the caller).</summary>
    [Fact]
    public void WithNoPort_TheDedupDoesNotTurnTheNoOpIntoAWrite_AndARefusalStillThrows()
    {
        var target = new RecordingTarget { CanApplyResult = false };
        var sw = new SwitchProfile(target, new RecordingAnnouncer());

        Assert.Null(sw.Run(TestProfiles.Turbo));
        Assert.Empty(target.Applied);                       // absent port: never reached Apply

        var refusing = new RecordingTarget { Refusal = new PortWriteFailedException("profile switch", "EC refused") };
        var sw2 = new SwitchProfile(refusing, new RecordingAnnouncer());
        Assert.Throws<PortWriteFailedException>(() => sw2.Run(TestProfiles.Turbo));
    }

    /// <summary>The pure dedup rule, asserted directly against literal timestamps (the same shape as the
    /// coordinator's <c>IsWakeTail</c>): same id inside the window coalesces, same id at/after the boundary does
    /// not, a different id never does, and no prior write never does.</summary>
    [Fact]
    public void TheDedupRule_IsSameTargetInsideTheWindow()
    {
        var wrote = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(SwitchProfile.IsCoalesced(TestProfiles.Turbo, wrote, wrote.AddSeconds(4), TestProfiles.Turbo));
        Assert.False(SwitchProfile.IsCoalesced(TestProfiles.Turbo, wrote, wrote.AddSeconds(5), TestProfiles.Turbo));
        Assert.False(SwitchProfile.IsCoalesced(TestProfiles.Turbo, wrote, wrote.AddSeconds(4), TestProfiles.Performance));
        Assert.False(SwitchProfile.IsCoalesced(null, wrote, wrote.AddSeconds(1), TestProfiles.Turbo));
    }

    /// <summary>A target that records every write it is handed, for the use-case-level window tests.</summary>
    private sealed class RecordingTarget : IProfileTarget
    {
        public bool CanApplyResult { get; set; } = true;
        public PortWriteFailedException? Refusal { get; set; }
        public List<string> Applied { get; } = [];

        public bool CanApply(PerformanceProfile profile) => CanApplyResult;

        public PerformanceProfile? Apply(PerformanceProfile profile)
        {
            if (Refusal is { } refused) throw refused;
            Applied.Add(profile.Id);
            return profile;
        }

        public void Remember(PerformanceProfile profile) { }
    }
}
