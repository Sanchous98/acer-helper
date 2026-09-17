using AcerHelper.Domain;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// Wave 6, step 1: the option rows no longer read the hardware while the UI is being BUILT. Each row is
/// constructed with a PLACEHOLDER — the value a FAILED read would have produced — and the real value arrives
/// from its prime, which runs on the row's own serial worker (<see cref="VerifiedHwValue{T}"/>, the same
/// machinery the readback after a write already used).
///
/// Three gates, in the order they matter:
/// <list type="number">
/// <item>Building the rows calls NO port (the assembler's six groups, plus the shell's autostart row) — the
/// strongest one, and it needs neither Avalonia nor <c>MainViewModel</c>;</item>
/// <item>the value DOES arrive: <c>Prime()</c> → the row's serial worker → a post back onto the UI thread;</item>
/// <item>a click that lands between the build and that arrival is not lost.</item>
/// </list>
///
/// HOW THE CORRECTION GETS BACK: the rows post it through <c>Dispatcher.UIThread.Post</c>, and these tests
/// replace that with a synchronous poster (<see cref="Eventually.Sync"/>) — the seam
/// <see cref="VerifiedHwValue{T}"/> already had, now carried by the rows too. Driving the real dispatcher with
/// <c>RunJobs</c> was tried FIRST and failed: it is thread-affine, and the bare xUnit process creates it from
/// whichever thread first touches it — a worker thread here, not the test thread — so every <c>RunJobs</c>
/// from a test throws. See <see cref="Eventually"/> for the full account. The read still runs on the row's own
/// serial worker, so the tests still WAIT for it; what is gone is Avalonia's queue, not the asynchrony.
///
/// These are the gates WITHOUT hardware. What they cannot show is listed in the wave-6 notes: that the
/// deferred reads return what the synchronous ones did, and that the placeholder is never visible at real
/// startup timings.
/// </summary>
public class OptionsPrimeTests
{
    // ---------------------------------------------------------------- gate 1: nothing is read while building

    /// <summary>
    /// The subject of the change: building every option row the assembler produces must touch no port. Each
    /// fake counts its reads, so this is a direct assertion rather than an inspection of the source.
    ///
    /// A regression here is not a style problem — it is the app freezing at logon, which is what the wave is
    /// about: the autostart row alone used to block the UI thread for up to 5 s inside `schtasks.exe`.
    /// </summary>
    [Fact]
    public void BuildingTheAssemblerRows_ReadsNoPort()
    {
        var (lcd, limit, cal, fn, kbd) = (new FakeFlagPort(), new FakeFlagPort(), new FakeFlagPort(),
                                          new FakeFlagPort(), new FakeFlagPort());
        var usb = new FakeChoicePort("off", "10", "20", "30");
        var timeout = new FakeChoicePort("5", "30", "60");
        var mode = new FakeChoicePort("adaptive", "express");
        // The declared settings are declared BEFORE the service exists, as a backend's probe is; the battery's
        // properties are not declarations at all — the battery object answers for them whenever they are set.
        var h = new OptionsAssemblerHarness(declare: d =>
        {
            d.Declare(Keys.Lcd, lcd, readbackVerifiesWrite: false);
            d.Declare(Keys.FnLock, fn);
            d.Declare(Keys.BacklightTimeout, kbd);
            d.Declare(Keys.Usb, usb);
            d.Declare(Keys.Timeout, timeout);
        });
        h.F.Device.Battery.ChargeLimit = limit.AsBatteryToggle();
        h.F.Device.Battery.Calibration = cal.AsBatteryToggle();
        h.F.Device.Battery.ChargeMode = mode.AsBatteryChoice();

        var toggles = AssemblerRows.Toggles(h).ToList();
        var choices = AssemblerRows.Choices(h).ToList();

        // The arrangement above really does produce rows: without this the zero-counts below would also hold
        // for a device with no ports at all, i.e. the test would pass by not testing anything.
        Assert.NotEmpty(toggles);
        Assert.NotEmpty(choices);

        Assert.Equal(0, lcd.GetCount);
        Assert.Equal(0, limit.GetCount);
        Assert.Equal(0, cal.GetCount);
        Assert.Equal(0, fn.GetCount);
        Assert.Equal(0, kbd.GetCount);
        Assert.Equal(0, usb.GetCount);
        Assert.Equal(0, timeout.GetCount);
        Assert.Equal(0, mode.GetCount);
    }

    /// <summary>The placeholder is not invented: it is what the row shows TODAY when a read fails — a flag
    /// port answers false, and <c>IndexOf</c> answers 0 for "no such option" as well as for the first one
    /// (OptionsAssembler.cs:162-168). So the deferral introduces no new kind of lie; it only moves the honest
    /// answer a few milliseconds later.</summary>
    [Fact]
    public void EveryPlaceholder_IsTheAnswerAFailedReadWouldHaveGiven()
    {
        // Settings that would report non-default values, so a placeholder that copied them would show up here.
        var h = new OptionsAssemblerHarness(declare: d =>
        {
            d.Declare(Keys.Lcd, new FakeFlagPort { State = true }, readbackVerifiesWrite: false);
            d.Declare(Keys.FnLock, new FakeFlagPort { State = true });
            d.Declare(Keys.Usb, new FakeChoicePort("off", "10", "20") { CurrentId = "20" });
        });

        Assert.All(AssemblerRows.Toggles(h), t => Assert.False(t.Initial));
        Assert.All(AssemblerRows.Choices(h), c => Assert.Equal(0, c.InitialIndex));
    }

    /// <summary>
    /// The app-level rows, which <c>OptionsAssembler</c> does not build. Only the autostart row can defer
    /// anything: <c>Autostart.IsEnabled()</c> shells out to schtasks.exe, while <c>Clamshell.Enabled</c> is a
    /// field the port already holds (Clamshell.cs:24) — so the clamshell row builds from it inline, and a test
    /// says so instead of a comment claiming it.
    /// </summary>
    [Fact]
    public void BuildingTheShellRows_AsksSchtasksNothing()
    {
        var auto = new FakeAutostart { Enabled = true };
        var clam = new FakeClamshell { Enabled = true };

        var vm = Shell(auto, clam);

        Assert.False(AutostartRow(vm, auto).IsOn);   // the placeholder...
        Assert.Equal(0, auto.IsEnabledCalls);        // ...bought by not asking schtasks
        Assert.True(ClamshellRow(vm, clam).IsOn);    // a field read, so it is not deferred and is already real
        Assert.Equal(1, clam.EnabledReads);
    }

    // ---------------------------------------------------------------- gate 2: the value arrives

    /// <summary>A readable row's prime is its own <c>Read</c> — no separate delegate, because the value a
    /// write is verified against is exactly the value the row should start on.</summary>
    [Fact]
    public void ThePrime_OnAReadableRow_BringsTheRealValue()
    {
        var fn = new FakeFlagPort { State = true };
        var h = new OptionsAssemblerHarness(declare: d => d.Declare(Keys.FnLock, fn));
        var row = new ToggleRowViewModel(AssemblerRows.Toggle(h, "Fn lock"), Eventually.Sync);

        Assert.False(row.IsOn);
        Assert.Equal(0, fn.GetCount);

        row.Prime();

        Assert.True(Eventually.Until(() => row.IsOn), "the deferred read never reached the row");
        Assert.Equal(1, fn.GetCount);                // once — a prime reads, it does not poll
    }

    /// <summary>The row the placeholder rule is most visible on: LCD overdrive has no <c>Read</c> at all (its
    /// write self-confirms through a status byte, and a readback would be a second EC transaction the user
    /// hears as a second click), so it is the one row carrying an explicit <c>Prime</c>. The reason the
    /// readback is absent must survive: the prime costs ONE transaction at startup and ZERO per write.</summary>
    [Fact]
    public void TheLcdRow_IsPrimed_ByTheDelegateItCarriesForExactlyThatReason()
    {
        var lcd = new FakeFlagPort { State = true };
        var h = new OptionsAssemblerHarness(declare: d => d.Declare(Keys.Lcd, lcd, readbackVerifiesWrite: false));
        var toggle = AssemblerRows.Toggle(h, "LCD overdrive");

        Assert.Null(toggle.Read);                    // still NO readback — that is a deliberate, audible choice
        Assert.NotNull(toggle.Prime);

        var row = new ToggleRowViewModel(toggle, Eventually.Sync);
        Assert.False(row.IsOn);

        row.Prime();

        Assert.True(Eventually.Until(() => row.IsOn), "the prime never reached the row");
        Assert.Equal(1, lcd.GetCount);
        Assert.Empty(lcd.SetCalls);                  // a prime reads; it must never write
    }

    /// <summary>A dropdown's prime brings the INDEX the hardware is in, and must not fire the pick on the way
    /// — the readback guard (<c>_syncing</c>) is what keeps a correction from looking like a user action, and
    /// a prime that wrote would be an EC transaction nobody asked for.</summary>
    [Fact]
    public void ThePrime_OnAChoiceRow_BringsTheIndexWithoutWriting()
    {
        var usb = new FakeChoicePort("off", "10", "20", "30") { CurrentId = "20" };
        var h = new OptionsAssemblerHarness(declare: d => d.Declare(Keys.Usb, usb));
        var row = new ChoiceRowViewModel(AssemblerRows.Choice(h, "USB charging when off:"), Eventually.Sync);

        Assert.Equal(0, row.SelectedIndex);

        row.Prime();

        Assert.True(Eventually.Until(() => row.SelectedIndex == 2), "the deferred read never reached the row");
        Assert.Empty(usb.SetCalls);
    }

    /// <summary>The autostart row takes the same route as any other row, and it is the one the wave exists
    /// for: the worst case stops being "the app freezes for 5 s at logon" and becomes "the switch fills in
    /// 5 s late". <c>Prime()</c> must also queue rather than read inline — a prime that read on the calling
    /// thread would put the freeze straight back.</summary>
    [Fact]
    public void ThePrime_OnTheAutostartRow_QueuesTheSchtasksCallInsteadOfMakingItInline()
    {
        var auto = new FakeAutostart { Enabled = true };
        var vm = Shell(auto, new FakeClamshell());

        vm.Prime();

        Assert.Equal(0, auto.IsEnabledCalls);        // still nothing: the read is on the row's worker
        Assert.True(Eventually.Until(() => AutostartRow(vm, auto).IsOn), "the deferred read never reached the row");
        Assert.Equal(1, auto.IsEnabledCalls);
    }

    /// <summary>A row that cannot be read at all — no <c>Read</c> and no <c>Prime</c> — must make the prime a
    /// no-op rather than throw or enqueue a read of nothing. The clamshell row is the live example: it has no
    /// readback because it never needed one.</summary>
    [Fact]
    public void ThePrime_OnARowWithNoReadback_IsANoOp()
    {
        var clam = new FakeClamshell { Enabled = true };
        var vm = Shell(new FakeAutostart(), clam);

        vm.Prime();
        Thread.Sleep(50);

        Assert.Equal(1, clam.EnabledReads);          // the construction read, and nothing since
        Assert.True(ClamshellRow(vm, clam).IsOn);
    }

    // ---------------------------------------------------------------- gate 3: a click in flight is not lost

    /// <summary>
    /// The ordering hazard of a deferred read: the click is queued AFTER the prime's read on the row's serial
    /// worker, so the two corrections reach the dispatcher in that same order — and the dispatcher is FIFO.
    /// The switch therefore ends on what the USER asked for, not on the stale reading that was taken before
    /// their click. If the order ever inverted, the final state here would be OFF.
    /// </summary>
    [Fact]
    public void AClickBetweenTheBuildAndThePrimesArrival_IsNotLost()
    {
        var fn = new FakeFlagPort { State = false };
        var h = new OptionsAssemblerHarness(declare: d => d.Declare(Keys.FnLock, fn));
        var row = new ToggleRowViewModel(AssemblerRows.Toggle(h, "Fn lock"), Eventually.Sync);

        row.Prime();        // reads the hardware — it is off, and the row shows the placeholder
        row.IsOn = true;    // the user clicks while that read is still in flight

        Assert.True(Eventually.Until(() => fn.SetCalls.Count == 1 && fn.GetCount >= 2),
                    "the click's write and its readback never ran");
        Thread.Sleep(50);   // let any trailing correction land

        Assert.True(row.IsOn);                       // the click won
        Assert.Equal([true], fn.SetCalls);           // ...and it won by writing, not by being ignored
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>An Options drawer over the given device, with none of the assembler's hardware rows — this
    /// file is about the app-level rows and about rows built directly, so the section carries nothing.</summary>
    private static OptionsViewModel Shell(IAutostart auto, IClamshell clam)
    {
        var vm = OptionsViewModel.TryCreate(new FakeDevice { Autostart = auto, Clamshell = clam },
                                            Section(auto, clam), Eventually.Sync);
        Assert.NotNull(vm);
        return vm;
    }

    /// <summary>The two shell rows' write delegates point at the fake the row reads from — the same shape
    /// <c>AppController.BuildUi</c> wires (<c>b =&gt; _svc.SetClamshell(b)</c>, <c>b =&gt; _svc.SetAutostart(b)</c>).
    /// With a no-op in their place the fakes' <c>SetCalls</c> lists were a destination nothing could reach, so a
    /// click on either row could not have been observed by any assertion — finding (2)'s hazard, here in the
    /// harness rather than in an assertion. The Turbo-toggles and language delegates stay no-ops because this
    /// Shell has no service or AppController behind them to point at; nothing in this file clicks those rows.
    ///
    /// Hardening, not a repair: no assertion in this file reads those two lists today, so no expected value
    /// changes and no test's outcome changes with this wiring.</summary>
    private static OptionsSection Section(IAutostart auto, IClamshell clam) => new(
        HwToggles: [], HwChoices: [], ProfileChoices: [],
        TurboToggles: false, SetTurboToggles: _ => { },
        SetClamshell: b => clam.SetEnabled(b), SetAutostart: b => auto.SetEnabled(b),
        Language: AppLanguage.System, SetLanguage: _ => { });

    private static ToggleRowViewModel AutostartRow(OptionsViewModel vm, FakeAutostart auto)
        => vm.Rows.OfType<ToggleRowViewModel>().Single(r => r.Label == Loc.T(auto.Label));

    private static ToggleRowViewModel ClamshellRow(OptionsViewModel vm, FakeClamshell clam)
        => vm.Rows.OfType<ToggleRowViewModel>().Single(r => r.Label == Loc.T(clam.Label));
}
