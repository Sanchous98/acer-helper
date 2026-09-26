using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// The two battery switches are SEPARATE flags on the SAME firmware control (<c>BatteryControl</c>,
/// Domain/Battery.cs): the ~80% cap is <c>uFunctionStatus[0]</c> under the health mask, calibration is
/// <c>uFunctionStatus[1]</c> under the calibration mask. Enabling calibration wants a full 100% charge, so the
/// firmware drops the cap — a real hardware move the charge-limit switch was showing and nothing re-read. The
/// limit row's only reads were its prime (so the switch was right at startup) and its own write's read-back, so
/// after toggling calibration the switch kept showing the old cap until the next app start's prime.
///
/// These tests pin the fix: a calibration write re-reads the charge-limit row on its own serial worker, the
/// same verified read-back (<see cref="VerifiedHwValue{T}"/>) path the row already uses for out-of-band
/// changes. Two gates, because either alone can pass a broken implementation:
/// <list type="number">
/// <item>enabling calibration with the cap already lifted by the firmware moves the switch, and turning
/// calibration back off moves it the other way — the symmetric half;</item>
/// <item>a DECLINED confirmation writes nothing and re-reads nothing, so the re-read belongs to the write and
/// not to the click.</item>
/// </list>
///
/// WHAT THE FAKES CANNOT SHOW, stated rather than implied: the firmware itself is not here. The test simulates
/// the cap dropping by mutating the limit fake's <c>State</c> before the toggle; what is proven is that the
/// app, having been told the hardware moved, looks again — not that a real Acer firmware moves it (which no
/// test in this tree constructs a vendor device for, see <see cref="BatteryPrimeTests"/>'s own note).
///
/// THE OTHER HALF, added after the same class of bug on the OTHER end of a cycle: a calibration can run for
/// hours and FINISH BY ITSELF, clearing the firmware's flag and restoring the cap with no write of ours. The
/// row's reads were still only its prime and its own write's read-back, so the switch stayed "calibrating"
/// forever. The periodic one-second battery refresh now drives the calibration row's read-back too
/// (<c>BatteryViewModel.ReconcileCalibration</c>, called from <c>Update</c>), and the tests below pin that:
/// the switch returns to off when the firmware reports the cycle over; the read is skipped while the row is off
/// (the fast path must stay cheap) and while a click's confirmation/write is still pending (the refresh must not
/// fight the user); and the firmware's end re-reads the restored cap, the same pairing as the write path.
/// </summary>
public class BatteryCalibrationResyncTests
{
    /// <summary>
    /// Turning calibration ON lifts the cap on the device; the UI must stop claiming the cap is on without a
    /// restart. Before the fix the switch stayed ON: the calibration write reached one property while the limit
    /// row kept displaying the value its prime had read at startup.
    /// </summary>
    [Fact]
    public void EnablingCalibration_ReReadsTheChargeLimit_WithoutARestart()
    {
        var (h, limit, cal) = Arranged(limiterOn: true, calibrating: false);
        var vm = Section(h);

        vm.Prime();
        Assert.True(Eventually.Until(() => vm.Limit!.IsOn), "the prime never brought the limit switch on");
        Assert.Equal(1, limit.GetCount);

        // The firmware drops the cap the moment calibration is enabled (calibration wants 100%).
        limit.State = false;

        vm.Calibration!.IsOn = true;

        Assert.True(Eventually.Until(() => !vm.Limit!.IsOn),
                    "the limit switch kept showing a cap the firmware had already lifted");
        Assert.Equal(2, limit.GetCount);          // the construction/prime read, then the re-read after the write
        Assert.Equal([true], cal.SetCalls);       // and the calibration write itself still happened once
    }

    /// <summary>The symmetric half: turning calibration OFF must re-read the cap too, whether the firmware
    /// restores it or leaves it lifted. The switch follows the hardware either way, because the re-read is not
    /// hard-coded to "on".</summary>
    [Fact]
    public void DisablingCalibration_ReReadsTheChargeLimit_Too()
    {
        var (h, limit, cal) = Arranged(limiterOn: false, calibrating: true);
        var vm = Section(h);

        vm.Prime();
        Assert.True(Eventually.Until(() => vm.Calibration!.IsOn), "the prime never brought calibration on");
        Assert.False(vm.Limit!.IsOn);

        // Turning calibration off restores the ~80% cap.
        limit.State = true;

        vm.Calibration!.IsOn = false;

        Assert.True(Eventually.Until(() => vm.Limit!.IsOn),
                    "the limit switch did not pick up the restored cap after calibration was turned off");
        Assert.Equal(2, limit.GetCount);
        Assert.Equal([false], cal.SetCalls);
    }

    /// <summary>
    /// The control that keeps the re-read attached to the WRITE rather than to the click. Calibration is gated
    /// behind a confirmation, and a declined confirmation never reaches <c>Apply</c> — so it must not touch the
    /// limit row. Without this, a fix that hooked <c>IsOn</c> instead of the write would still pass the two
    /// gates above while re-reading (and briefly misreporting) on a switch the user cancelled.
    /// </summary>
    [Fact]
    public void DecliningCalibration_ReReadsNothing()
    {
        var (h, limit, cal) = Arranged(limiterOn: true, calibrating: false,
            confirmCalibration: () => Task.FromResult(false));
        var vm = Section(h);

        vm.Prime();
        Assert.True(Eventually.Until(() => vm.Limit!.IsOn), "the prime never brought the limit switch on");
        var readsAfterPrime = limit.GetCount;

        vm.Calibration!.IsOn = true;   // the user clicks; the dialog says no

        Assert.True(Eventually.Until(() => !vm.Calibration!.IsOn), "the declined switch stayed on");
        Thread.Sleep(50);              // let any trailing read land before the count is asserted
        Assert.Equal(readsAfterPrime, limit.GetCount);
        Assert.Empty(cal.SetCalls);
    }

    // ------------------------------------------- the OTHER end: the firmware ends the cycle on its own

    /// <summary>
    /// THE REPORTED BUG. A calibration the user started runs to completion on the hardware with no write of
    /// ours; the firmware clears its flag and restores the cap. The calibration switch must stop saying it is
    /// still calibrating on the next periodic refresh, and the cap it lifted must show as restored on the same
    /// pass (the pairing the write path already performs).
    /// </summary>
    [Fact]
    public void FirmwareEndingCalibration_ReturnsTheSwitchToOff_AndRestoresTheCap_OnTheNextRefresh()
    {
        var (h, limit, cal) = Arranged(limiterOn: false, calibrating: false);
        var vm = Section(h);

        vm.Prime();
        Assert.True(Eventually.Until(() => cal.GetCount == 1), "the calibration prime never read");
        Assert.False(vm.Calibration!.IsOn);

        // The user starts a cycle; the write lands and the row shows ON (the cap was off to begin with).
        vm.Calibration.IsOn = true;
        Assert.True(Eventually.Until(() => vm.Calibration.IsOn && cal.SetCalls.Count == 1),
                    "the calibration write never happened");
        Assert.True(Eventually.Until(() => cal.GetCount >= 2 && !vm.Calibration.IsPending),
                    "the write's read-back never settled");

        // Hours later the firmware finishes ON ITS OWN: it clears its flag and restores the ~80% cap.
        cal.State = false;
        limit.State = true;

        vm.Update(new BatteryInfoSnapshot());   // one tick of the existing one-second battery refresh

        Assert.True(Eventually.Until(() => !vm.Calibration.IsOn),
                    "the calibration switch stayed on after the firmware ended the cycle");
        Assert.True(Eventually.Until(() => vm.Limit!.IsOn),
                    "the limit switch did not pick up the cap the firmware restored");
    }

    /// <summary>
    /// The cost guard, and the reason the refresh does not simply read the port every second forever: while the
    /// row is settled OFF there is nothing a WMI read would buy, and the fast battery poll is deliberately free
    /// of WMI transactions. A refresh must therefore not touch the calibration read at all in that state.
    /// </summary>
    [Fact]
    public void ARefreshWhileCalibrationIsOff_DoesNotReadTheCalibrationPort()
    {
        var (h, _, cal) = Arranged(limiterOn: false, calibrating: false);
        var vm = Section(h);

        vm.Prime();
        Assert.True(Eventually.Until(() => cal.GetCount == 1), "the calibration prime never read");
        Assert.False(vm.Calibration!.IsOn);
        var readsAfterPrime = cal.GetCount;

        vm.Update(new BatteryInfoSnapshot());

        Thread.Sleep(50);   // let any read the refresh (wrongly) queued land before the count is asserted
        Assert.Equal(readsAfterPrime, cal.GetCount);
    }

    /// <summary>
    /// The control that keeps the periodic read from FIGHTING THE USER. Calibration is gated behind a modal
    /// confirmation, and the switch flips ON optimistically while that dialog is open — but nothing has been
    /// written to the hardware yet, so a refresh that read it then would see the old OFF and snap the switch
    /// back, yanking the click out from under the dialog. A pending change (confirming or writing) therefore
    /// suppresses the reconcile entirely, read and all.
    /// </summary>
    [Fact]
    public void ARefreshWhileTheCalibrationConfirmationIsOpen_DoesNotClobberTheClick()
    {
        var confirm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (h, _, cal) = Arranged(limiterOn: false, calibrating: false, confirmCalibration: () => confirm.Task);
        var vm = Section(h);

        vm.Prime();
        Assert.True(Eventually.Until(() => cal.GetCount == 1), "the calibration prime never read");
        Assert.False(vm.Calibration!.IsOn);

        vm.Calibration.IsOn = true;   // the user clicks; the dialog is open and NOTHING is written yet
        Assert.True(vm.Calibration.IsOn, "the optimistic flip did not show");
        var readsBefore = cal.GetCount;

        vm.Update(new BatteryInfoSnapshot());   // a one-second refresh arrives mid-flight

        Thread.Sleep(50);   // let any read it (wrongly) queued land
        Assert.True(vm.Calibration.IsOn, "the refresh clobbered the click while the confirmation was open");
        Assert.Equal(readsBefore, cal.GetCount);   // and it did not even ask the hardware
        Assert.Empty(cal.SetCalls);

        confirm.SetResult(true);   // the user confirms; the write now happens
        Assert.True(Eventually.Until(() => cal.SetCalls.Count == 1 && vm.Calibration.IsOn),
                    "the confirmed calibration write never reached the hardware");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A device with a charge-limit and a calibration toggle, arranged by the caller — the two rows
    /// these tests are about. Mirrors <c>BatteryPrimeTests.Arranged</c>, narrowed to the pair (no charge mode:
    /// the two switches are the whole subject, and an absent third row keeps the section free of a dropdown).</summary>
    private static (OptionsAssemblerHarness H, FakeFlagPort Limit, FakeFlagPort Cal)
        Arranged(bool limiterOn = false, bool calibrating = false, Func<Task<bool>>? confirmCalibration = null)
    {
        var h = new OptionsAssemblerHarness(confirmCalibration: confirmCalibration);
        var limit = new FakeFlagPort { State = limiterOn };
        var cal = new FakeFlagPort { State = calibrating };
        h.F.Device.Battery.ChargeLimit = limit.AsBatteryToggle();
        h.F.Device.Battery.Calibration = cal.AsBatteryToggle();
        return (h, limit, cal);
    }

    /// <summary>
    /// The section, built the way <c>MainViewModel</c> builds it from the <c>BatterySection</c> that
    /// <c>AppController.BuildUi</c> assembles — through the assembler's own accessors, so the rows carry the
    /// real read/write delegates rather than ones a test invented. <see cref="Eventually.Sync"/> is the poster,
    /// the same seam <c>BatteryPrimeTests</c> uses and for the same reason (there is no dispatcher a bare xUnit
    /// thread may drive); the read still runs on the row's own serial worker, so these tests still wait for it.
    /// </summary>
    private static BatteryViewModel Section(OptionsAssemblerHarness h)
    {
        var bat = new BatterySection(h.F.Device.Battery,
                                     h.Assembler.BatteryLimit(), h.Assembler.BatteryCalibration(),
                                     h.Assembler.BatteryChargeMode());
        return new BatteryViewModel(bat.HasInfo, bat.Limit, bat.Calibration, bat.ChargeMode, Eventually.Sync);
    }
}
