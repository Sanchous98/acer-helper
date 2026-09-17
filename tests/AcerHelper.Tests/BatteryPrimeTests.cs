using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// Wave 4a: the battery section's three charging rows are PRIMED. They were built with placeholders like the
/// Options drawer's rows and — unlike those — were never settled: a comment at <c>AppController.cs</c> claimed
/// <c>OptionsViewModel.Prime()</c> reached them, and it does not (the Options view-model is built from
/// <c>OptionsSection</c> only, and <c>BatteryViewModel</c> had no <c>Prime</c> at all). So the charge-limit and
/// calibration switches showed <c>Initial = false</c> and the charge-mode dropdown index 0 — on Dell, "Adaptive"
/// — for the whole process lifetime until the user clicked them. On a machine whose ~80% limiter is ON, the row
/// said OFF. A settings row showing a value it never read is the same class of defect as the false comment that
/// hid it.
///
/// Same three gates as <see cref="OptionsPrimeTests"/>, in the same order:
/// <list type="number">
/// <item>building the section calls NO battery port, and the rows show the placeholder even when the ports would
/// report the opposite;</item>
/// <item>the value DOES arrive: <c>Prime()</c> → the row's serial worker → a post back onto the UI thread —
/// with no write along the way;</item>
/// <item>a read that fails (throws) or reports nothing leaves the placeholder rather than a wrong value, the
/// absent ports still produce no rows, and the calibration row's confirm dialog is not on the prime's path.</item>
/// </list>
///
/// WHY A PRIME IS LEGITIMATE HERE AND NOT AN ADOPTION (docs/state-and-events.md). These three rows have no
/// stored value anywhere — nothing in <c>Settings</c> holds a charge limit, a calibration flag or a charge mode
/// — so there is no intent of ours to re-apply and the firmware is the only source. That is exactly the case
/// <c>LightingViewModel.Prime</c> covers for the plain backlight, and it is not the case the rule was written
/// against (a read on a schedule re-taking a value we had just written).
///
/// THE GAP, NAMED. That the rows are primed in the running app rests on <c>AppController</c> calling
/// <c>MainViewModel.Battery?.Prime()</c> — at startup and on a language rebuild — and those two call sites are
/// NOT reachable by this suite: <c>AppController</c> needs a live desktop lifetime and a refresh loop. What is
/// proven here is the operation; what is not is that the site calls it. (The same gap, for the same reason, is
/// recorded for <c>BackgroundPass</c>'s binding to the reconciler in <c>HardwareReconcilerTests</c>.)
///
/// What these gates cannot show, and nothing here claims they do: that the port really returns what the row now
/// shows on a live machine (no test constructs <c>AcerDevice</c>/<c>DellDevice</c> — wave 4b's finding), and
/// that the placeholder is never visible at real startup timings.
/// </summary>
public class BatteryPrimeTests
{
    // ---------------------------------------------------------------- gate 1: nothing is read while building

    /// <summary>
    /// The subject of the change. The ports here report the OPPOSITE of the placeholders (limiter on, a charge
    /// mode other than the first), so a row that had captured its port's value — or a section that settled its
    /// rows on the way in — shows up twice over: as a read count above zero, and as a row that is already
    /// "right" for the wrong reason.
    ///
    /// The sleep is what makes the second failure mode visible rather than a coin flip: a read queued from the
    /// constructor runs on the row's own worker and may not have landed by the next line, so an immediate
    /// assertion could read zero from the very implementation this test exists to catch. It cannot make the test
    /// flaky in the other direction — correct code enqueues nothing at all, so any wait gives the same answer.
    /// </summary>
    [Fact]
    public void BuildingTheSection_ReadsNoBatteryPort_AndShowsThePlaceholder()
    {
        var (h, limit, cal, mode) = Arranged(limiterOn: true, calibrating: true, chargeModeId: "express");

        var vm = Section(h);
        Thread.Sleep(50);

        // The arrangement really does produce three rows: without this the zero counts below would also hold for
        // a device with no battery ports at all, i.e. the test would pass by not testing anything.
        Assert.True(vm.ShowLimit);
        Assert.True(vm.ShowCalibration);
        Assert.True(vm.ShowMode);

        Assert.Equal(0, limit.GetCount);
        Assert.Equal(0, cal.GetCount);
        Assert.Equal(0, mode.GetCount);

        // ...and what the rows show until the prime is exactly what a FAILED read would have shown, so the
        // deferral introduces no new kind of lie: a flag port answers false when the EC won't answer, and
        // IndexOf answers 0 for "no such option" as well as for the first one.
        Assert.False(vm.Limit!.IsOn);
        Assert.False(vm.Calibration!.IsOn);
        Assert.Equal(0, vm.Mode!.SelectedIndex);
    }

    // ---------------------------------------------------------------- gate 2: the value arrives

    /// <summary>
    /// The defect this wave is about, stated as an assertion: a firmware whose limiter is ON must land as ON,
    /// the calibration flag likewise, and the charge mode must land on the INDEX its id has in the dropdown —
    /// not on 0, which is what the row showed forever.
    ///
    /// "Once" matters as much as "arrives": a prime reads, it does not poll, and it must never write — a write
    /// here would be the app switching a ~80% charge limit (or starting a multi-hour calibration cycle) on a
    /// machine whose user never asked.
    /// </summary>
    [Fact]
    public void ThePrime_BringsTheRealValues()
    {
        var (h, limit, cal, mode) = Arranged(limiterOn: true, calibrating: true, chargeModeId: "express");

        var vm = Section(h);
        vm.Prime();

        Assert.True(Eventually.Until(() => vm.Limit!.IsOn), "the charge-limit prime never reached the row");
        Assert.True(Eventually.Until(() => vm.Calibration!.IsOn), "the calibration prime never reached the row");
        Assert.True(Eventually.Until(() => vm.Mode!.SelectedIndex == 1),
                    "the charge-mode prime never reached the row");

        Assert.Equal(1, limit.GetCount);
        Assert.Equal(1, cal.GetCount);
        Assert.Equal(1, mode.GetCount);
        Assert.Empty(limit.SetCalls);
        Assert.Empty(cal.SetCalls);
        Assert.Empty(mode.SetCalls);
    }

    /// <summary>
    /// The other half of "arrives": a firmware that says OFF must land as OFF, and the first charge mode must
    /// land on index 0 — through the prime, not by being the placeholder. The read count is what separates the
    /// two, and without it this test would pass against a section that never reads at all.
    /// </summary>
    [Fact]
    public void ThePrime_AlsoBringsAValueThatMatchesThePlaceholder()
    {
        var (h, limit, cal, mode) = Arranged(limiterOn: false, calibrating: false, chargeModeId: "adaptive");

        var vm = Section(h);
        vm.Prime();

        Assert.True(Eventually.Until(() => limit.GetCount == 1), "the charge-limit prime never read");
        Assert.True(Eventually.Until(() => cal.GetCount == 1), "the calibration prime never read");
        Assert.True(Eventually.Until(() => mode.GetCount == 1), "the charge-mode prime never read");

        Assert.False(vm.Limit!.IsOn);
        Assert.False(vm.Calibration!.IsOn);
        Assert.Equal(0, vm.Mode!.SelectedIndex);
    }

    // ------------------------------------- gate 3: failure, absence and the one row with a dialog

    /// <summary>
    /// A port whose READ throws — the EC refusing rather than answering. The row must keep its placeholder: the
    /// port never said anything, and the alternative (treating a throw as "off"/"the first option") would show
    /// the user a value nothing measured. That is a different case from <c>State = false</c>, which is a real
    /// answer the row must show, and it is the case the read's own failure path is for — the throw is swallowed
    /// on the row's serial worker (<c>HwSerial</c>), so nothing reaches the UI thread at all.
    /// </summary>
    [Fact]
    public void AReadThatThrows_LeavesThePlaceholder()
    {
        var (h, limit, _, _) = Arranged(limiterOn: true);
        limit.ThrowOnGet = true;

        var vm = Section(h);
        vm.Prime();

        Assert.True(Eventually.Until(() => limit.GetCount == 1), "the prime never attempted the read");
        Assert.False(vm.Limit!.IsOn);      // the placeholder, not the port's (unreachable) State
        Assert.Empty(limit.SetCalls);
    }

    /// <summary>A port that answers with nothing. The charge-mode port reporting <c>null</c> is the live case
    /// (<c>IChoicePort.Get</c> is documented as "the active option's id, or null if it can't be read"), and
    /// <c>IndexOf</c> folds it to 0 — the placeholder — rather than inventing an index.</summary>
    [Fact]
    public void AReadThatReportsNothing_LeavesThePlaceholder()
    {
        var (h, limit, cal, mode) = Arranged(chargeModeId: null);

        var vm = Section(h);
        vm.Prime();

        Assert.True(Eventually.Until(() => mode.GetCount == 1), "the charge-mode prime never read");
        Assert.Equal(0, vm.Mode!.SelectedIndex);

        // A flag port's "nothing" IS false, so the assertion above would be vacuous on its own; the calibration
        // row is read out here to keep the counts honest rather than to claim a second behaviour.
        Assert.True(Eventually.Until(() => limit.GetCount == 1 && cal.GetCount == 1),
                    "the flag-row primes never read");
        Assert.False(vm.Limit!.IsOn);
        Assert.False(vm.Calibration!.IsOn);
    }

    /// <summary>
    /// The calibration row is the only row in the app with a confirm dialog, because one click can kick off a
    /// multi-hour charge/discharge cycle. Priming must never put that dialog in front of the user: the prime is
    /// a read of what the firmware already holds, and the confirm belongs to the INTENT to change it.
    ///
    /// The confirmer here answers FALSE, so a prime that routed through the confirming setter would not merely
    /// be counted — it would leave the row OFF and the wait below would time out too.
    /// </summary>
    [Fact]
    public void ThePrime_OnTheCalibrationRow_NeverAsksForConfirmation()
    {
        var asked = 0;
        var (h, _, cal, _) = Arranged(calibrating: true,
            confirmCalibration: () => { asked++; return Task.FromResult(false); });

        var vm = Section(h);
        vm.Prime();

        Assert.True(Eventually.Until(() => vm.Calibration!.IsOn), "the calibration prime never reached the row");
        Assert.Equal(0, asked);       // a prime reads the firmware; it must not ask the user anything
        Assert.Empty(cal.SetCalls);   // ...nor write
    }

    /// <summary>
    /// The control for the guard above, so its <c>asked == 0</c> cannot be passing because the dialog is dead:
    /// the gating itself must be unchanged — a click still asks, and a declined click still writes nothing.
    /// </summary>
    [Fact]
    public void AClickTheDialogDeclines_StillAsksAndStillWritesNothing()
    {
        var asked = 0;
        var (h, _, cal, _) = Arranged(confirmCalibration: () => { asked++; return Task.FromResult(false); });

        var vm = Section(h);
        vm.Prime();
        Assert.True(Eventually.Until(() => cal.GetCount == 1), "the prime never read");

        vm.Calibration!.IsOn = true;   // the user clicks; the dialog says no

        Assert.True(Eventually.Until(() => asked == 1), "the click never reached the dialog");
        Assert.True(Eventually.Until(() => !vm.Calibration.IsOn), "the declined switch stayed on");
        Assert.Empty(cal.SetCalls);
    }

    /// <summary>Absent ports are unchanged: no port, no row, and priming a section with nothing to settle is a
    /// no-op rather than a throw.</summary>
    [Fact]
    public void WithNoBatteryPorts_ThereAreNoRows_AndPrimingIsANoOp()
    {
        var vm = Section(new OptionsAssemblerHarness());

        Assert.Null(vm.Limit);
        Assert.Null(vm.Calibration);
        Assert.Null(vm.Mode);
        Assert.False(vm.ShowLimit);
        Assert.False(vm.ShowCalibration);
        Assert.False(vm.ShowMode);

        vm.Prime();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A device with all three battery charging ports, arranged by the caller. The optional confirmer
    /// replaces the assembler harness's always-yes default (<c>OptionsAssemblerHarness</c>).</summary>
    private static (OptionsAssemblerHarness H, FakeFlagPort Limit, FakeFlagPort Cal, FakeChoicePort Mode)
        Arranged(bool limiterOn = false, bool calibrating = false, string? chargeModeId = null,
                 Func<Task<bool>>? confirmCalibration = null)
    {
        var h = new OptionsAssemblerHarness(confirmCalibration: confirmCalibration);
        var limit = new FakeFlagPort { State = limiterOn };
        var cal = new FakeFlagPort { State = calibrating };
        var mode = new FakeChoicePort("adaptive", "express") { CurrentId = chargeModeId };
        h.F.Device.BatteryChargeLimit = limit;
        h.F.Device.BatteryCalibration = cal;
        h.F.Device.BatteryChargeMode = mode;
        return (h, limit, cal, mode);
    }

    /// <summary>
    /// The section, built the way <c>MainViewModel</c> builds it from the <c>BatterySection</c> that
    /// <c>AppController.BuildUi</c> assembles — through the assembler's own three accessors, so the rows carry
    /// the real <c>Read</c> delegates rather than ones a test invented.
    ///
    /// The poster is <see cref="Eventually.Sync"/>, the same seam <c>OptionsPrimeTests</c> uses and for the same
    /// reason: the rows post their correction through <c>Dispatcher.UIThread.Post</c> by default, and a bare
    /// xUnit process has no dispatcher a test thread may drive (see <see cref="Eventually"/>). The read still
    /// runs on the row's own serial worker, so these tests still WAIT for it; what is gone is Avalonia's queue,
    /// not the asynchrony.
    /// </summary>
    private static BatteryViewModel Section(OptionsAssemblerHarness h)
    {
        var bat = new BatterySection(HasInfo: true,
                                     h.Assembler.BatteryLimit(), h.Assembler.BatteryCalibration(),
                                     h.Assembler.BatteryChargeMode());
        return new BatteryViewModel(bat.HasInfo, bat.Limit, bat.Calibration, bat.ChargeMode, Eventually.Sync);
    }
}
