using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// ONE PALETTE REPAINT PER POWER-SOURCE CHANGE. The owner's report — on resume from sleep, if the power source
/// changed, the keyboard/lightbar palette flashes TWICE, once for the old power profile and once for the new —
/// was a third instance of the double-flash class, this time on the per-source restore
/// (<c>LaptopService.ApplyStoredMode</c>). It wrote the port DIRECTLY:
///
/// <list type="number">
/// <item><b>The intermediate base write.</b> Restoring a source whose remembered mode was Turbo-over-a-base did
/// <c>pp.Set(base)</c> and then <c>pp.Set(turbo)</c> — two port writes, and each Acer profile Set makes the
/// firmware re-flash the palette, so the machine visibly dropped out of Turbo and back (the "old profile"
/// flash, then the "new" one).</item>
/// <item><b>The unannounced write.</b> Neither Set went through <c>SwitchProfile</c>, so nothing recorded the
/// light claim (<c>LightingCoordinator._pendingId</c>) and the ~1 s refresh pass repainted the palette again —
/// the same mechanism already closed for user picks.</item>
/// </list>
///
/// The fix routes the restore through the switch use case (as a TRANSIENT — the slot already holds the mode, so
/// it must not re-remember) and drops the intermediate base write: the Acer EC takes the Turbo byte directly
/// (<c>AcerDevice.Windows.SetProfile</c> writes one gmInput), so the base under Turbo is slot bookkeeping, not a
/// platform precondition. Every source change now produces exactly ONE port write — and so ONE firmware palette
/// repaint — the target's.
///
/// WHY THE ASSERTIONS ARE ON THE PORT'S OWN CALL LOG. The coordinator needs a desktop lifetime to drive (the
/// limit <c>ReconcileScheduleTests</c> records), so the repaint count cannot be observed on the fake RGB device
/// from a unit test. It does not need to be: one <c>IPowerProfiles.Set</c> is exactly one firmware palette
/// repaint (docs/lighting-an18-61.md), so <c>FakePowerProfiles.SetCalls</c> IS the repaint count on the wire,
/// and the announced profile is the app's own palette send. The two are asserted together.
///
/// MUTATION-VERIFIED. Putting the <c>pp.Set(baseP)</c> back ahead of the Turbo write reddens
/// <see cref="ATurboSourceChangeWritesOnlyTheTarget_NotTheIntermediateBase"/>; reverting the restore to a raw
/// <c>pp.Set</c> reddens the announcement count in the same test (and in
/// <see cref="ABaseSourceChangeWritesAndAnnouncesOnce"/>); removing the "already in that profile" guard reddens
/// <see cref="ASourceChangeToTheProfileItsAlreadyIn_FlashesNothing"/>.
/// </summary>
public class SourceChangeFlashTests
{
    private static readonly BatteryInfoSnapshot OnAc = new() { State = BatteryState.Charging };
    private static readonly BatteryInfoSnapshot OnBattery = new() { State = BatteryState.Discharging };

    /// <summary>Records the switch use case's announcement — the app's own palette send — so a test can count
    /// how many palette repaints the app drove for one source change.</summary>
    private sealed class RecordingAnnouncer : IProfileAnnouncer
    {
        public List<PerformanceProfile> Announced { get; } = [];
        public void OnProfileApplied(PerformanceProfile applied) => Announced.Add(applied);
    }

    /// <summary>(a) A source change onto a source whose remembered mode is Turbo-over-a-base — the resume case.
    /// The machine sits in Quiet; the AC slot remembers Turbo over Balanced; the source becomes AC. EXACTLY ONE
    /// port write (the Turbo target), and so ONE firmware palette repaint: NOT the intermediate base first.</summary>
    [Fact]
    public void ATurboSourceChangeWritesOnlyTheTarget_NotTheIntermediateBase()
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

        f.SyncPowerSource.Run(OnAc);           // the source-change restore (the resume path, via the use case)

        Assert.Equal(["turbo"], f.Pp!.SetCallIds);                 // ONE write, the target's
        Assert.Single(announcer.Announced);                        // ONE announced landing
        Assert.Equal(TestProfiles.Turbo, announcer.Announced[0]);  // ...and it is the new profile
        Assert.Equal(TestProfiles.Turbo, f.Pp.CurrentProfile);     // the machine really landed there
    }

    /// <summary>(b) A plain base change (no Turbo): one port write and one announcement — the ordinary case,
    /// which the intermediate-base bug did not touch but which the unannounced-write half did.</summary>
    [Fact]
    public void ABaseSourceChangeWritesAndAnnouncesOnce()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = "performance" } },
            current: TestProfiles.Balanced);
        var announcer = new RecordingAnnouncer();
        f.Service.ProfileAnnouncer = announcer;

        f.SyncPowerSource.Run(OnAc);

        Assert.Equal(["performance"], f.Pp!.SetCallIds);
        Assert.Single(announcer.Announced);
        Assert.Equal(TestProfiles.Performance, announcer.Announced[0]);
    }

    /// <summary>(c) A source change where the target EQUALS the current profile: nothing is written and nothing
    /// is announced — the guard is what keeps the common same-mode source change flash-free, and it must survive
    /// the routing through the use case (a switch to the profile already on screen would still repaint).</summary>
    [Fact]
    public void ASourceChangeToTheProfileItsAlreadyIn_FlashesNothing()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = "balanced" } },
            current: TestProfiles.Balanced);
        var announcer = new RecordingAnnouncer();
        f.Service.ProfileAnnouncer = announcer;

        f.SyncPowerSource.Run(OnAc);

        Assert.Empty(f.Pp!.SetCalls);
        Assert.Empty(announcer.Announced);
    }

    /// <summary>The Turbo-remembered source change is a TRANSIENT switch: it must NOT re-remember the mode it
    /// restores. The slot already holds Balanced+Turbo; a persistent switch would clear the Turbo flag and
    /// overwrite the base with the Turbo id, so the next source cycle would have nowhere to return to.</summary>
    [Fact]
    public void TheTurboSourceChangeDoesNotReRememberTheSlot()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
                      },
            current: TestProfiles.Quiet);

        f.SyncPowerSource.Run(OnAc);

        Assert.Equal("balanced", f.Store.Settings.OnAc.BaseId);
        Assert.True(f.Store.Settings.OnAc.Turbo);
        Assert.Equal(0, f.Store.SaveCount);      // a transient switch writes no graph
    }

    // ---- the wake ordering (source guard) ----

    /// <summary>THE WAKE ITSELF IS RE-SYNCED BEFORE IT PAINTS. This is the <c>OnResume</c> half of the fix, and
    /// it is pinned by a source guard because the handler needs a desktop lifetime and a live ResumeWatcher to
    /// drive headlessly. Two things must hold together: the source is re-synced over the sleep (through the same
    /// SyncPowerSource use case the refresh pass uses) BEFORE the wake's paint, and the wake's paint is
    /// palette-free when that restore announced a landing — otherwise the cached OLD palette is sent over the
    /// NEW one the restore just painted, the owner's "old profile" flash. The stamp of <c>_lastResume</c> and
    /// the wake-tail suppression stay, so the pass that follows is still coalesced.</summary>
    [Fact]
    public void TheWakeResyncsTheSourceBeforeItPaints()
    {
        var source = Source("UI/LightingCoordinator.cs");

        // The restore runs through the injected use case, and the paint comes after it.
        var sync = source.IndexOf("_syncSource.Run(_readBattery.Run());", StringComparison.Ordinal);
        Assert.True(sync >= 0, "OnResume must re-sync the power source over the sleep");
        var paint = source.IndexOf("var restored = _pendingId != null && _pendingSince >= _lastResume;",
                                   StringComparison.Ordinal);
        Assert.True(paint > sync, "the source re-sync must precede the wake's paint decision");
        Assert.Contains("Paint(includeFlash: !restored);", source, StringComparison.Ordinal);

        // The wake still coalesces Windows' double Resume event and the pass that follows (wake tail).
        Assert.Contains("private void OnResume()", source, StringComparison.Ordinal);
        Assert.Contains("IsWakeTail(_lastResume, DateTime.UtcNow, flash, _flash)", source, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Source(string relative)
    {
        var path = Path.Combine(Root(), relative);
        Assert.True(File.Exists(path), $"expected a source file at {relative} — a guard that reads nothing passes for the wrong reason");
        return File.ReadAllText(path);
    }
}
