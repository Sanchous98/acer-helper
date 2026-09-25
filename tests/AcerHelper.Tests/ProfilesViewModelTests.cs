using AcerHelper.Domain;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// The Performance section's OPTIMISTIC selection. The section used to show nothing until the refresh pass
/// rediscovered the new profile over the EC/WMI gate (up to the 3 s tick, plus whatever a contended read
/// queued behind it), so a pick looked ignored. It now moves the selected segment — and the Turbo switch, and
/// the header's displayed profile — the moment the user acts, runs the write, and rolls the display back when
/// the write refuses.
///
/// WHAT IS PINNED HERE, and what each pin is for:
///  * the pick shows DURING the write (the delegate observes the selection already moved);
///  * a refused write puts the previous display back (and the delegate already owns the error text, on the
///    transient status line — that surface is not this section's to double up on);
///  * a read-back that PREDATES the write cannot flicker the selection back while the write is in flight,
///    while the first matching read-back commits it;
///  * once committed, a genuine change from elsewhere (another actor on the hardware) does update the UI;
///  * the same optimistic/rollback contract for the Turbo switch in "Turbo toggles" mode;
///  * the integration path through the real <see cref="LaptopService"/>: a successful write is confirmed by
///    the service's own read-back, and the service's current profile then drives the UI again.
///
/// The delegates are the shape <c>AppController</c> wires (<c>_svc.ApplyProfile(p).ok</c> and
/// <c>_svc.SetTurbo(on).applied != null</c>), so these tests stand where that wiring stands, without a
/// live desktop.
/// </summary>
public class ProfilesViewModelTests
{
    /// <summary>Records the writes and lets each be accepted or refused; an optional hook runs while the write
    /// is "in flight", which is what lets a test see the display the delegate itself is looking at.</summary>
    private sealed class Fake
    {
        public List<PerformanceProfile> Applied { get; } = [];
        public bool ApplyResult { get; set; } = true;
        public Action<PerformanceProfile>? OnApply { get; set; }

        public List<bool> TurboCalls { get; } = [];
        public bool TurboResult { get; set; } = true;

        public bool Apply(PerformanceProfile p) { Applied.Add(p); OnApply?.Invoke(p); return ApplyResult; }
        public bool SetTurbo(bool on) { TurboCalls.Add(on); return TurboResult; }
    }

    private static ProfilesViewModel Vm(Fake f, bool turboAsToggle = false, Action<PerformanceProfile?>? onDisplay = null)
        => new(TestProfiles.All, TestProfiles.TraitsOf, f.Apply, turboAsToggle, f.SetTurbo, onDisplay);

    /// <summary>The button carrying <paramref name="p"/>'s label, addressed the way the UI shows it.</summary>
    private static ProfileButtonViewModel Button(ProfilesViewModel vm, PerformanceProfile p)
        => vm.Profiles.Single(b => b.Name == Loc.T(p.DisplayName));

    /// <summary>The profile whose segment is selected, or null when none is (e.g. a segment set that omits it).</summary>
    private static PerformanceProfile? Selected(ProfilesViewModel vm)
        => TestProfiles.All.SingleOrDefault(p => vm.Profiles.Any(b => b.IsSelected && b.Name == Loc.T(p.DisplayName)));

    // ---- segment picks ----

    /// <summary>The pick is already selected INSIDE the write delegate, i.e. before it returns — not after a
    /// later refresh. This is the whole bug: the display no longer waits for the hardware.</summary>
    [Fact]
    public void APickIsDisplayedBeforeTheWriteReturns()
    {
        var f = new Fake();
        ProfilesViewModel? vm = null;
        PerformanceProfile? seenDuringWrite = null;
        f.OnApply = _ => seenDuringWrite = Selected(vm!);
        vm = Vm(f);
        vm.Update(TestProfiles.Quiet, TestProfiles.All, turboAsToggle: false, baseHighlight: TestProfiles.Quiet);

        Button(vm, TestProfiles.Performance).ApplyCommand.Execute(null);

        Assert.Equal(TestProfiles.Performance, seenDuringWrite);
        Assert.Equal(TestProfiles.Performance, Selected(vm));
        Assert.Equal(TestProfiles.Performance, vm.DisplayedProfile);   // the header chip follows too
        Assert.Equal([TestProfiles.Performance], f.Applied);
    }

    /// <summary>A refused write puts the previous selection back — the optimistic display is not left claiming
    /// a profile the hardware never took.</summary>
    [Fact]
    public void ARefusedWriteRollsTheSelectionBack()
    {
        var f = new Fake { ApplyResult = false };
        var vm = Vm(f);
        vm.Update(TestProfiles.Eco, TestProfiles.All, turboAsToggle: false, baseHighlight: TestProfiles.Eco);

        Button(vm, TestProfiles.Performance).ApplyCommand.Execute(null);

        Assert.Equal(TestProfiles.Eco, Selected(vm));
        Assert.Equal(TestProfiles.Eco, vm.DisplayedProfile);
    }

    /// <summary>A read-back that predates the write (a pass already in flight when the user clicked) may not
    /// flicker the selection off the pick; the first read-back that matches commits it, and only after that
    /// does a genuine change from elsewhere win again.</summary>
    [Fact]
    public void AStaleReadBackDoesNotFlickerAJustAppliedProfile()
    {
        var f = new Fake();
        var vm = Vm(f);
        vm.Update(TestProfiles.Quiet, TestProfiles.All, turboAsToggle: false, baseHighlight: TestProfiles.Quiet);

        Button(vm, TestProfiles.Performance).ApplyCommand.Execute(null);   // write accepted, awaiting read-back
        vm.Update(TestProfiles.Quiet, TestProfiles.All, turboAsToggle: false, baseHighlight: TestProfiles.Quiet);

        Assert.Equal(TestProfiles.Performance, Selected(vm));             // stale read ignored: no flicker

        vm.Update(TestProfiles.Performance, TestProfiles.All, turboAsToggle: false, baseHighlight: TestProfiles.Performance);   // confirms
        Assert.Equal(TestProfiles.Performance, Selected(vm));

        vm.Update(TestProfiles.Eco, TestProfiles.All, turboAsToggle: false, baseHighlight: TestProfiles.Eco);   // a genuine change elsewhere
        Assert.Equal(TestProfiles.Eco, Selected(vm));
    }

    /// <summary>After a refusal the display is back on the old value, and the next read-backs are in charge
    /// again — a later genuine change is not swallowed by a pending write that never landed.</summary>
    [Fact]
    public void AfterARefusalLaterReadBacksDriveTheUiAgain()
    {
        var f = new Fake { ApplyResult = false };
        var vm = Vm(f);
        vm.Update(TestProfiles.Eco, TestProfiles.All, turboAsToggle: false, baseHighlight: TestProfiles.Eco);

        Button(vm, TestProfiles.Performance).ApplyCommand.Execute(null);
        vm.Update(TestProfiles.Quiet, TestProfiles.All, turboAsToggle: false, baseHighlight: TestProfiles.Quiet);

        Assert.Equal(TestProfiles.Quiet, Selected(vm));
    }

    /// <summary>The section REPORTS the displayed profile as it changes, so the header chip is driven by the
    /// same optimistic state rather than by the delayed refresh pass: the pick, then the snap-back on refusal.</summary>
    [Fact]
    public void TheDisplayCallbackReportsTheOptimisticPickAndItsRollback()
    {
        var f = new Fake { ApplyResult = false };
        var shown = new List<PerformanceProfile?>();
        var vm = Vm(f, onDisplay: shown.Add);
        vm.Update(TestProfiles.Eco, TestProfiles.All, turboAsToggle: false, baseHighlight: TestProfiles.Eco);   // confirms Eco
        shown.Clear();

        Button(vm, TestProfiles.Performance).ApplyCommand.Execute(null);

        Assert.Equal([TestProfiles.Performance, TestProfiles.Eco], shown);   // pick first, then the rollback
    }

    // ---- the Turbo switch (toggle mode) ----

    /// <summary>Flipping Turbo on shows it at once: the switch is on immediately, the header takes Turbo, and
    /// the base it sits over stays the highlighted segment. A read-back from before the write can't flip it back;
    /// the read-back that reports Turbo commits it.</summary>
    [Fact]
    public void TurningTurboOnShowsItImmediatelyAndSurvivesAStaleReadBack()
    {
        var f = new Fake();
        var vm = Vm(f, turboAsToggle: true);
        vm.Update(TestProfiles.Balanced, TestProfiles.All, turboAsToggle: true, baseHighlight: TestProfiles.Balanced);

        vm.TurboOn = true;

        Assert.Equal([true], f.TurboCalls);
        Assert.True(vm.TurboOn);
        Assert.Equal(TestProfiles.Turbo, vm.DisplayedProfile);
        Assert.Equal(TestProfiles.Balanced, Selected(vm));               // the base under Turbo stays highlighted

        vm.Update(TestProfiles.Balanced, TestProfiles.All, turboAsToggle: true, baseHighlight: TestProfiles.Balanced);   // stale
        Assert.True(vm.TurboOn);

        vm.Update(TestProfiles.Turbo, TestProfiles.All, turboAsToggle: true, baseHighlight: TestProfiles.Balanced);       // confirms
        Assert.True(vm.TurboOn);
        Assert.Equal(TestProfiles.Balanced, Selected(vm));
    }

    /// <summary>Flipping Turbo off shows the base it sat over at once, as both the header and the highlighted
    /// segment.</summary>
    [Fact]
    public void TurningTurboOffShowsTheBaseImmediately()
    {
        var f = new Fake();
        var vm = Vm(f, turboAsToggle: true);
        vm.Update(TestProfiles.Turbo, TestProfiles.All, turboAsToggle: true, baseHighlight: TestProfiles.Balanced);

        vm.TurboOn = false;

        Assert.Equal([false], f.TurboCalls);
        Assert.False(vm.TurboOn);
        Assert.Equal(TestProfiles.Balanced, vm.DisplayedProfile);
        Assert.Equal(TestProfiles.Balanced, Selected(vm));

        vm.Update(TestProfiles.Balanced, TestProfiles.All, turboAsToggle: true, baseHighlight: TestProfiles.Balanced);   // confirms
        Assert.False(vm.TurboOn);
        Assert.Equal(TestProfiles.Balanced, Selected(vm));
    }

    /// <summary>A refused Turbo flip snaps the switch and the header back to where they were, and does not
    /// re-issue a write from the snap-back (which would otherwise loop).</summary>
    [Fact]
    public void ARefusedTurboFlipSnapsBack()
    {
        var f = new Fake { TurboResult = false };
        var vm = Vm(f, turboAsToggle: true);
        vm.Update(TestProfiles.Balanced, TestProfiles.All, turboAsToggle: true, baseHighlight: TestProfiles.Balanced);

        vm.TurboOn = true;

        Assert.Equal([true], f.TurboCalls);                              // exactly one write, no rebound write
        Assert.False(vm.TurboOn);
        Assert.Equal(TestProfiles.Balanced, vm.DisplayedProfile);
        Assert.Equal(TestProfiles.Balanced, Selected(vm));
    }

    // ---- integration through the real service ----

    /// <summary>The delegates exactly as <c>AppController</c> wires them: the section's optimistic display is
    /// confirmed by <see cref="LaptopService"/>'s read-back after a real (fake-backed) write, and a change made
    /// on the hardware directly is picked up once no write is in flight.</summary>
    [Fact]
    public void ASuccessfulServiceWriteIsConfirmedByTheReadBack()
    {
        var f = new LaptopServiceFixture();
        f.Power(TestProfiles.All, current: TestProfiles.Quiet);
        var vm = new ProfilesViewModel(TestProfiles.All, f.Service.TraitsOf,
            p => f.Service.ApplyProfile(p).ok, turboAsToggle: false,
            on => f.Service.SetTurbo(on).applied != null);
        vm.Update(f.Service.CurrentProfile(), f.Service.SelectableProfiles(), turboAsToggle: false,
                  baseHighlight: f.Service.BaseProfile());

        Button(vm, TestProfiles.Performance).ApplyCommand.Execute(null);   // optimistic
        Assert.Equal(TestProfiles.Performance, vm.DisplayedProfile);

        // The pass that follows a write reads the service's now-current profile: it confirms the pick.
        vm.Update(f.Service.CurrentProfile(), f.Service.SelectableProfiles(), turboAsToggle: false,
                  baseHighlight: f.Service.BaseProfile());
        Assert.Equal(TestProfiles.Performance, Selected(vm));

        // A genuine hardware change from elsewhere (the OS, a hotkey, another tool) drives the UI again.
        f.Pp!.CurrentProfile = TestProfiles.Eco;
        vm.Update(f.Service.CurrentProfile(), f.Service.SelectableProfiles(), turboAsToggle: false,
                  baseHighlight: f.Service.BaseProfile());
        Assert.Equal(TestProfiles.Eco, Selected(vm));
    }
}
