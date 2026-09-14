using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// <see cref="OptionsAssembler"/> — the one place that knows the device's option ports and funnels every
/// setter through <c>RunSet</c> so a failed hardware write reaches the user instead of dying silently
/// (OptionsAssembler.cs:140-149).
///
/// WHY THESE TESTS CAN EXIST AT ALL: <c>RunSet</c> is <c>private</c> (InternalsVisibleTo grants access to
/// <c>internal</c> only), so nothing here calls it. Its only observable effect used to be a
/// <c>Dispatcher.UIThread.Post</c> — unreachable from a headless test — which is exactly why the post is now
/// an injected delegate. Every failure assertion below therefore drives a PUBLIC model's <c>OnChange</c> and
/// captures what <c>RunSet</c> handed to <c>post</c>, then RUNS it: the message is built inside the posted
/// action, on the UI thread, so until it is run the user has been told nothing.
///
/// NO REAL HARDWARE: the fixture builds <see cref="LaptopService"/> over <see cref="FakeDevice"/> with no
/// ports, and each test assigns the port its row reads (ports are read lazily). <c>LampArray</c> is never
/// built — the fixture passes no transport — so the "Windows Dynamic Lighting" row does not exist in these
/// tests, and it is the one row whose failure path is NOT covered here.
///
/// LOCALIZATION: labels and the failure message are looked up with <c>Loc.T</c>. Rows are found by
/// <c>Loc.T(englishKey)</c> so the lookups survive a translation table; the MESSAGES are asserted in English
/// source text, which is what <see cref="Loc"/> returns until a language is activated (<c>Loc.Use</c> is
/// called only by AppController, never by the assembler). NOTE that <see cref="Loc"/> is process-wide static
/// state: if a test anywhere ever activates a language, these message assertions stop being independent of
/// which tests ran first.
/// </summary>
public class OptionsAssemblerFailureTests
{
    /// <summary>A refused write with an error text must produce exactly ONE notification, and it must name
    /// the control AND carry the port's own words — "something failed" is not actionable, and the port's
    /// <c>LastError</c> is the only explanation the user will ever get.</summary>
    [Fact]
    public void AFailedSet_PostsOneNotification_WithTheControlNameAndThePortsError()
    {
        var h = new OptionsAssemblerHarness();
        h.F.Device.LcdOverdrive = new FakeFlagPort { SetResult = false, LastError = "EC refused the write" };

        AssemblerRows.Toggle(h, "LCD overdrive").OnChange(true);

        Assert.Single(h.Posted);       // one post, and it is a post — not a direct call
        Assert.Empty(h.Notices);       // ...so nothing has been shown yet: the message is built on the UI thread
        Assert.Equal("LCD overdrive failed: EC refused the write", Assert.Single(h.RunPosted()));
    }

    /// <summary>PINNED BEHAVIOUR: a refused write with NO error text still reports — the suffix is
    /// conditional (<c>e != null ? $": {e}" : ""</c>, OptionsAssembler.cs:148), so the user gets
    /// "LCD overdrive failed" and not "…failed: " with a dangling colon. Both halves are pinned by the exact
    /// equality: dropping the suffix would let a silent EC look like a success, while making it unconditional
    /// is what a naive tidy-up produces.</summary>
    [Fact]
    public void AFailedSet_WithNoErrorText_SaysOnlyThatItFailed()
    {
        var h = new OptionsAssemblerHarness();
        h.F.Device.LcdOverdrive = new FakeFlagPort { SetResult = false, LastError = null };

        AssemblerRows.Toggle(h, "LCD overdrive").OnChange(true);

        Assert.Equal("LCD overdrive failed", Assert.Single(h.RunPosted()));
    }

    /// <summary>A write that lands must not notify at all. Pinned because the failure branch is one `if`
    /// away from firing on every write, and because <c>LastError</c> is only read AFTER a set has failed —
    /// see the stale-error test below.</summary>
    [Fact]
    public void ASuccessfulSet_PostsNothing_AndNotifiesNothing()
    {
        var h = new OptionsAssemblerHarness();
        var lcd = new FakeFlagPort { SetResult = true };
        h.F.Device.LcdOverdrive = lcd;

        AssemblerRows.Toggle(h, "LCD overdrive").OnChange(true);

        Assert.Equal([true], lcd.SetCalls);      // the write did happen
        Assert.Empty(h.Posted);
        Assert.Empty(h.Notices);
    }

    /// <summary>...and a STALE <c>LastError</c> left over from an earlier failure must not resurrect the
    /// message: the branch is on the set's result, not on the error text. <c>LaptopService</c>'s LastError is
    /// a property of the SERVICE, not of the write (LaptopService.cs:56), so it can be non-null while the
    /// current write is perfectly fine.</summary>
    [Fact]
    public void ASuccessfulSet_AfterAFailure_DoesNotNotifyAgain()
    {
        var h = new OptionsAssemblerHarness();
        h.F.Device.FnLock = new FakeFlagPort { SetResult = false, LastError = "old news" };
        AssemblerRows.Toggle(h, "Fn lock").OnChange(true);
        Assert.Single(h.RunPosted());

        h.F.Device.LcdOverdrive = new FakeFlagPort();           // succeeds
        AssemblerRows.Toggle(h, "LCD overdrive").OnChange(true);

        Assert.Single(h.Posted);                                // still only the one from the Fn-lock failure
    }

    /// <summary>PINNED BEHAVIOUR: a port whose <c>Set</c> THROWS is a failed write, not a crashed row. This is
    /// the behaviour that keeps one bad port from taking its switch down with it — the exception must not
    /// escape the <c>OnChange</c> the UI calls, and the user must still be told.
    ///
    /// WHERE the throw is absorbed is worth being precise about, because it is not where it looks: the write
    /// goes through <c>LaptopService.SetFlag</c> -> <c>Run</c>, which already has
    /// <c>try { ok = set(svc); } catch { ok = false; }</c> (LaptopService.cs:122-125). So this case exercises
    /// <c>Run</c>'s catch, and <c>RunSet</c>'s own catch is the second line of defence behind it —
    /// which <see cref="AThrowingProfileSet_IsCaughtByRunSetItself_AndStillReported"/> reaches instead.
    /// Either way <c>LastError</c> is read after the failure, so the port's message still reaches the user.</summary>
    [Fact]
    public void AThrowingPort_IsReportedAsAFailure_AndDoesNotEscapeTheRow()
    {
        var h = new OptionsAssemblerHarness();
        var lcd = new FakeFlagPort { ThrowOnSet = true, LastError = "transport gone" };
        h.F.Device.LcdOverdrive = lcd;

        var row = AssemblerRows.Toggle(h, "LCD overdrive");

        Assert.Null(Record.Exception(() => row.OnChange(true)));   // the throw must not leave the row
        Assert.Equal([true], lcd.SetCalls);               // the write was attempted, and only once
        Assert.Equal("LCD overdrive failed: transport gone", Assert.Single(h.RunPosted()));
    }

    /// <summary>PINNED BEHAVIOUR: the throw that reaches <c>RunSet</c>'s OWN catch. Only a profile write can
    /// get there — <c>ApplyProfile</c> calls the port directly (LaptopService.Profiles.cs:46) with no
    /// <c>Run</c> in between — so this is the one arrangement in which <c>RunSet</c>'s
    /// <c>catch { ok = false; }</c> is the thing standing between a broken port and an exception thrown out of
    /// an Options row. The service's <c>LastError</c> was never assigned on this path, so the message is bare.</summary>
    [Fact]
    public void AThrowingProfileSet_IsCaughtByRunSetItself_AndStillReported()
    {
        var h = new OptionsAssemblerHarness(LaptopServiceFixture.WithProfiles(current: TestProfiles.Quiet));
        var pp = new FakeThrowingPowerProfiles();
        h.F.Device.PowerProfiles = pp;

        var row = AssemblerRows.Choice(h, "Profile on AC power:");

        Assert.Null(Record.Exception(() => row.OnChange(1)));      // caught by RunSet itself, not by the caller
        Assert.Single(pp.SetCalls);                        // the write was attempted once, then threw
        Assert.Equal("Power-source profile failed", Assert.Single(h.RunPosted()));
    }

    /// <summary>Every row that funnels through <c>RunSet</c> must name ITSELF in the message — an error that
    /// does not say which control failed is the bug this file guards, and a copy-paste that leaves the wrong
    /// name in one row's <c>what</c> is invisible until it fires.
    ///
    /// NOTE the two rows whose message names something the UI never shows: the row labelled "Keyboard
    /// backlight timeout" fails as "Backlight timeout", and the row labelled "Charge limit (~80%)" fails as
    /// "Battery limit". Pinned as shipped — the internal name is what reaches the user, and it is not the row
    /// label.</summary>
    [Theory]
    [InlineData("LCD overdrive", "LCD overdrive")]                    // Toggles()
    [InlineData("Keyboard backlight timeout", "Backlight timeout")]
    [InlineData("Fn lock", "Fn lock")]
    [InlineData("USB charging when off:", "USB charging")]            // Choices()
    [InlineData("Charge mode", "Charge mode")]                        // BatteryChargeMode()
    [InlineData("Charge limit (~80%)", "Battery limit")]              // BatteryLimit()
    [InlineData("Calibration (full cycle)", "Battery calibration")]   // BatteryCalibration()
    public void AFailedSet_TellsTheUserWhichControlFailed(string locKey, string reportedName)
    {
        var h = AssemblerRows.AllRefusing();

        AssemblerRows.Change(h, locKey);

        Assert.Single(h.Posted);
        Assert.Equal($"{reportedName} failed", Assert.Single(h.RunPosted()));
    }
}

/// <summary>
/// The readback wiring: which rows carry a <c>Read</c> delegate, and what that delegate answers. The row's
/// readback is what corrects a switch (or a dropdown) after a write the firmware accepted but did not apply,
/// so a <c>Read</c> that answers from a copy taken at build time would leave the UI lying about the hardware
/// — the exact bug the readback exists to remove (Options.cs:8-11).
///
/// WAVE 6 MOVED WHERE THE FIRST READ HAPPENS: these delegates are now also what the row's PRIME calls, and the
/// build itself reads nothing — <c>Initial</c>/<c>InitialIndex</c> are placeholders. The assertions below
/// therefore start their counts at 0 and get the live value through <c>Read()</c> rather than out of the row's
/// construction-time value; the delegate's contract (ask the port, every time, answer live) is unchanged, and
/// that is the point — the deferral reused this machinery instead of adding a second one.
/// </summary>
public class OptionsAssemblerReadbackTests
{
    /// <summary>A readback must re-ask the PORT, on every call.</summary>
    [Fact]
    public void AFlagRowsReadback_AsksThePortAgain_AndReportsItsLiveAnswer()
    {
        var h = new OptionsAssemblerHarness();
        var fn = new FakeFlagPort { State = true };
        h.F.Device.FnLock = fn;

        var row = AssemblerRows.Toggle(h, "Fn lock");
        Assert.False(row.Initial);           // placeholder: what a FAILED read would have shown
        Assert.Equal(0, fn.GetCount);        // ...and the build cost no transaction at all

        Assert.True(row.Read!());            // the live answer, asked for on demand
        Assert.Equal(1, fn.GetCount);

        fn.State = false;                    // the hardware changed under us (a hotkey, another app)
        Assert.False(row.Read!());
        Assert.Equal(2, fn.GetCount);

        fn.State = true;
        Assert.True(row.Read!());
        Assert.Equal(3, fn.GetCount);        // ...and again: the delegate reads, it does not remember
    }

    /// <summary>LCD overdrive deliberately has NO readback: its write is <c>SetGamingProfile</c>, which
    /// returns a status byte, so the row self-confirms and skips the second EC transaction the user would hear
    /// as a second "click" (OptionsAssembler.cs:23-27). Pinned because "add Read everywhere" looks like a
    /// tidy-up and is not — it would be an audible regression on every toggle.</summary>
    [Fact]
    public void TheLcdRowHasNoReadback_BecauseItsWriteSelfConfirms()
    {
        var h = new OptionsAssemblerHarness();
        var lcd = new FakeFlagPort { State = true };
        h.F.Device.LcdOverdrive = lcd;

        var row = AssemblerRows.Toggle(h, "LCD overdrive");
        Assert.Null(row.Read);

        row.OnChange(true);

        Assert.Equal([true], lcd.SetCalls);  // the write went through
        Assert.Equal(0, lcd.GetCount);       // ...and read nothing back: the write self-confirms, and the build defers
        Assert.NotNull(row.Prime);           // so the row carries the one explicit Prime instead
        Assert.True(row.Prime!());           // which is exactly the port's Get
        Assert.Equal(1, lcd.GetCount);       // one transaction — and only because a prime was asked for
    }

    /// <summary>A dropdown's <c>Read</c> answers with the INDEX of what the port reports now, not the id
    /// (Options.cs:16-20). The unknown-id and null cases are PINNED: <c>IndexOf</c> returns 0 both for "no
    /// such option" and for the first option (OptionsAssembler.cs:162-168), so a port reporting something
    /// this build does not offer reads as the first entry — the row then shows a setting the hardware is not
    /// in, and a subsequent pick would start from a lie.</summary>
    [Theory]
    [InlineData("off", 0)]
    [InlineData("20", 2)]
    [InlineData("30", 3)]
    [InlineData("never-heard-of-it", 0)]     // PINNED: an unrecognised id reads as index 0
    [InlineData(null, 0)]                    // PINNED: an unreadable port reads as index 0
    [InlineData("OFF", 0)]                   // PINNED: ids are matched exactly — no case folding
    public void AChoiceRowsReadback_MapsThePortsLiveIdToADropdownIndex(string? currentId, int expected)
    {
        var h = new OptionsAssemblerHarness();
        var usb = new FakeChoicePort("off", "10", "20", "30") { CurrentId = currentId };
        h.F.Device.UsbCharging = usb;

        var row = AssemblerRows.Choice(h, "USB charging when off:");
        Assert.Equal(0, row.InitialIndex);   // placeholder — the build does not ask, so it cannot know
        Assert.Equal(0, usb.GetCount);

        Assert.Equal(expected, row.Read!()); // the mapping, asked for on demand
        Assert.Equal(1, usb.GetCount);

        usb.CurrentId = "10";                // the hardware moved after the row was built
        Assert.Equal(1, row.Read!());
        Assert.Equal(2, usb.GetCount);       // ...because the delegate asks the port, every time
    }

    /// <summary>The power-source rows read back from the SERVICE's remembered slot, not from the port's live
    /// profile: the row means "the profile this source uses", which is stored intent — the port only knows
    /// what is on right now and cannot answer for the other source at all
    /// (OptionsAssembler.cs:103-106).</summary>
    [Fact]
    public void APowerSourceRowsReadback_AnswersFromTheRememberedSlot()
    {
        var h = new OptionsAssemblerHarness(LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced));
        var row = AssemblerRows.Choice(h, "Profile on AC power:");

        Assert.Equal(0, row.InitialIndex);                       // placeholder, not a reading — see wave 6

        h.F.Store.Settings.OnAc.BaseId = "performance";
        Assert.Equal(3, row.Read!());

        h.F.Store.Settings.OnAc.BaseId = "ghost";                // a profile this machine no longer advertises
        Assert.Equal(0, row.Read!());
    }
}

/// <summary>
/// Which rows exist. A null port is the capability model's "this machine cannot do that"
/// (Features/Ports.cs:265-269), so a row that appears without its port promises the user a control that
/// cannot work — and one that fails to appear hides a feature the machine does have.
/// </summary>
public class OptionsAssemblerPresenceTests
{
    /// <summary>The degenerate device: no ports, so nothing at all is offered.</summary>
    [Fact]
    public void WithNoPortsAtAll_NothingIsOffered()
    {
        var h = new OptionsAssemblerHarness();

        Assert.Empty(h.Assembler.Toggles());
        Assert.Empty(h.Assembler.Choices());
        Assert.Empty(h.Assembler.PowerSourceProfiles());
        Assert.Null(h.Assembler.BatteryLimit());
        Assert.Null(h.Assembler.BatteryChargeMode());
        Assert.Null(h.Assembler.BatteryCalibration());
    }

    /// <summary>Every row appears on exactly its own port, and none of them on any other.</summary>
    [Theory]
    [InlineData("LCD overdrive")]
    [InlineData("Keyboard backlight timeout")]      // the on/off row
    [InlineData("Fn lock")]
    [InlineData("USB charging when off:")]
    [InlineData("Charge mode")]
    [InlineData("Charge limit (~80%)")]
    [InlineData("Calibration (full cycle)")]
    public void ARowAppearsExactlyWhenItsPortIsPresent(string locKey)
    {
        var absent = new OptionsAssemblerHarness();
        Assert.DoesNotContain(Loc.T(locKey), AssemblerRows.Labels(absent));

        var present = new OptionsAssemblerHarness();
        AssemblerRows.Wire(present, locKey, new FakeFlagPort(), new FakeChoicePort("off", "on"));
        Assert.Contains(Loc.T(locKey), AssemblerRows.Labels(present));
    }

    /// <summary>The dropdown variant of the same device: only the timeout CHOICE row appears when the
    /// duration port is the one present, and the on/off row of the same name stays away.</summary>
    [Fact]
    public void TheTimeoutRowsAreIndependent_OneForTheFlagPort_OneForTheChoicePort()
    {
        var h = new OptionsAssemblerHarness();
        h.F.Device.KeyboardBacklightTimeout = new FakeChoicePort("5", "30", "60");

        Assert.Contains(Loc.T("Keyboard backlight timeout:"), AssemblerRows.Labels(h));
        Assert.DoesNotContain(Loc.T("Keyboard backlight timeout"), AssemblerRows.Labels(h));
    }

    /// <summary>The blue-light row is suppressed when the tint port has no levels to offer — the presence
    /// test is <c>tint.Levels &gt; 0</c> and nothing else (OptionsAssembler.cs:75) — and when it IS offered
    /// it lists exactly as many entries as the port has: the fixed "Off".."Long-use" names truncated to what
    /// this display can do, so no dropdown offers the user a level the hardware does not have.</summary>
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, true, 1)]
    [InlineData(3, true, 3)]
    [InlineData(5, true, 5)]
    public void TheBlueLightRow_ExistsOnlyWhenThePortHasLevels_AndListsExactlyThatMany(
        int levels, bool present, int options)
    {
        var h = new OptionsAssemblerHarness();
        h.F.Device.DisplayTint = new FakeDisplayTint(levels);

        var row = AssemblerRows.Choices(h).FirstOrDefault(c => c.Label == Loc.T("Blue-light filter:"));

        Assert.Equal(present, row != null);
        Assert.Equal(options, row?.Options.Count ?? 0);
    }

    /// <summary>The blue-light row is the ONE Options row that does not funnel through <c>RunSet</c>:
    /// <c>SetBlueLight</c> applies and persists inline and returns nothing (`LaptopService.Toggles.cs` `SetBlueLight`),
    /// so a tint that fails to apply is silent — there is no message and no post for this row by construction,
    /// and no failure test for it can exist. Pinned so that stays a decision rather than an oversight.</summary>
    [Fact]
    public void ABlueLightPick_AppliesAndPersists_AndPostsNothing()
    {
        var h = new OptionsAssemblerHarness();
        var tint = new FakeDisplayTint(5);
        h.F.Device.DisplayTint = tint;

        AssemblerRows.Change(h, "Blue-light filter:");      // picks the second entry

        Assert.Equal([1], tint.ApplyCalls);
        Assert.Equal(1, h.F.Store.Settings.Bluelight);
        Assert.Equal(1, h.F.Store.SaveCount);
        Assert.Empty(h.Posted);
    }

    /// <summary>A device with no profile port, or one that advertises no profiles, has no per-source rows:
    /// the pair only means something when there is a profile to pick (OptionsAssembler.cs:98). The two halves
    /// are different devices — the first has no <c>PowerProfiles</c> at all, the second has one that lists
    /// nothing — and both must give an empty list rather than two rows over an empty dropdown.</summary>
    [Theory]
    [InlineData(true)]      // no power-profiles port at all
    [InlineData(false)]     // a port that advertises no profiles
    public void WithoutAnyProfiles_ThereAreNoPowerSourceRows(bool assignPort)
    {
        var f = assignPort ? new LaptopServiceFixture() : LaptopServiceFixture.WithProfiles(all: []);
        var h = new OptionsAssemblerHarness(f);

        Assert.Empty(h.Assembler.PowerSourceProfiles());
    }

    /// <summary>...and with profiles present there are exactly two rows, one per source, offering the
    /// port's own names in the port's own order.</summary>
    [Fact]
    public void WithProfiles_ThereIsOneRowPerSource_OfferingThePortsProfiles()
    {
        var h = new OptionsAssemblerHarness(LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced));

        var rows = h.Assembler.PowerSourceProfiles();

        Assert.Equal([Loc.T("Profile on AC power:"), Loc.T("Profile on battery:")], rows.Select(r => r.Label));
        Assert.Equal(TestProfiles.All.Select(p => Loc.T(p.DisplayName)), rows[0].Options);
        Assert.Equal(TestProfiles.All.Select(p => Loc.T(p.DisplayName)), rows[1].Options);
    }
}

/// <summary>
/// The one row gated behind a confirmation, and the delegate it is gated with. A single click on calibration
/// starts a multi-hour charge/discharge cycle, so the row must not be able to run one from the click alone
/// (OptionsAssembler.cs:128-134) — and no OTHER row may ask, because an extra dialog is invisible until it
/// appears in front of a user who did not expect one.
/// </summary>
public class OptionsAssemblerConfirmTests
{
    /// <summary>The assembler is handed the delegate; it must carry that very instance, not a wrapper of its
    /// own — the caller owns the dialog (AppController), and the row is only the place it is hung.</summary>
    [Fact]
    public void TheCalibrationRow_CarriesTheConfirmDelegateTheAssemblerWasBuiltWith()
    {
        Func<Task<bool>> confirm = () => Task.FromResult(false);
        var h = new OptionsAssemblerHarness(confirmCalibration: confirm);
        h.F.Device.BatteryCalibration = new FakeFlagPort();

        var row = h.Assembler.BatteryCalibration();

        Assert.NotNull(row);
        Assert.Same(confirm, row!.ConfirmAsync);
        Assert.Null(row.Confirm);        // the async form only: OptionToggle allows one of the two (Options.cs:4-11)
    }

    /// <summary>A device without the calibration port has no row at all — there is nothing to confirm.</summary>
    [Fact]
    public void WithoutTheCalibrationPort_ThereIsNoRow()
    {
        Assert.Null(new OptionsAssemblerHarness().Assembler.BatteryCalibration());
    }

    /// <summary>...and it is the ONLY gated row: everything else applies the moment it is clicked.</summary>
    [Fact]
    public void NoOtherRowAsksForConfirmation()
    {
        var h = AssemblerRows.AllRefusing();

        var gated = AssemblerRows.Toggles(h)
                                 .Where(t => t.ConfirmAsync != null || t.Confirm != null)
                                 .Select(t => t.Label);

        Assert.Equal([Loc.T("Calibration (full cycle)")], gated);
    }
}

/// <summary>
/// The assembler under test plus everything it hands out — what it POSTED, and the messages the posted
/// actions produced. Keeping those two apart is the point: <c>RunSet</c> reports failures through
/// <c>post</c>, so "the user was told" is only true after the posted action has run, and a test that skipped
/// <see cref="RunPosted"/> would pass against an implementation that never posted at all.
/// </summary>
internal sealed class OptionsAssemblerHarness
{
    public LaptopServiceFixture F { get; }
    public OptionsAssembler Assembler { get; }

    /// <summary>Every action handed to the injected <c>post</c> delegate.</summary>
    public List<Action> Posted { get; } = [];

    /// <summary>Every message handed to <c>notify</c> (empty until the posts are run).</summary>
    public List<string> Notices { get; } = [];

    /// <param name="fixture">The device to build over; a bare fixture (no ports) when omitted.</param>
    /// <param name="confirmCalibration">The dialog delegate the calibration row should carry; a
    /// delegate that always answers "yes" when omitted.</param>
    public OptionsAssemblerHarness(LaptopServiceFixture? fixture = null, Func<Task<bool>>? confirmCalibration = null)
    {
        F = fixture ?? new LaptopServiceFixture();
        Assembler = new OptionsAssembler(F.Service, Notices.Add,
                                         confirmCalibration ?? (() => Task.FromResult(true)),
                                         Posted.Add);
    }

    /// <summary>Play the posted actions, as the UI thread would, and return the messages they produced.</summary>
    public List<string> RunPosted()
    {
        foreach (var post in Posted) post();
        return [.. Notices];
    }
}

/// <summary>
/// Row lookup and port wiring by LABEL, because the row — not the port — is what the user sees, and one
/// fake serves several slots (Features/Ports.cs: every on/off port is exactly <see cref="IFlagPort"/>).
///
/// Every helper takes the ENGLISH text that <c>Loc.T</c> looks up, not a translated label: the keys are
/// stable, the labels are not.
/// </summary>
internal static class AssemblerRows
{
    /// <summary>Every on/off row the assembler produces, from all three of its entry points.</summary>
    public static IEnumerable<OptionToggle> Toggles(OptionsAssemblerHarness h)
        => h.Assembler.Toggles().Concat(Optional(h.Assembler.BatteryLimit(), h.Assembler.BatteryCalibration()));

    /// <summary>Every dropdown row, likewise — including the per-source pair, which lives in its own
    /// accessor because the Battery/Options sections are built separately.</summary>
    public static IEnumerable<OptionChoice> Choices(OptionsAssemblerHarness h)
        => h.Assembler.Choices()
                     .Concat(h.Assembler.PowerSourceProfiles())
                     .Concat(Optional<OptionChoice>(h.Assembler.BatteryChargeMode()));

    /// <summary>Every row label, for presence assertions.</summary>
    public static IEnumerable<string> Labels(OptionsAssemblerHarness h)
        => Toggles(h).Select(t => t.Label).Concat(Choices(h).Select(c => c.Label));

    public static OptionToggle Toggle(OptionsAssemblerHarness h, string locKey)
        => Toggles(h).Single(t => t.Label == Loc.T(locKey));

    public static OptionChoice Choice(OptionsAssemblerHarness h, string locKey)
        => Choices(h).Single(c => c.Label == Loc.T(locKey));

    /// <summary>Push a value through whichever row carries <paramref name="locKey"/>: ON for an on/off row,
    /// the second entry for a dropdown (every choice port a test builds advertises at least two, so index 1
    /// always exists).</summary>
    public static void Change(OptionsAssemblerHarness h, string locKey)
    {
        if (Toggles(h).FirstOrDefault(t => t.Label == Loc.T(locKey)) is { } toggle) { toggle.OnChange(true); return; }
        Choice(h, locKey).OnChange(1);
    }

    /// <summary>
    /// Attach a port to whichever slot the row with this key reads. The switch is on the English KEY because
    /// that is what identifies the row independently of any translation table; the fakes of the other kind are
    /// ignored, so a caller may pass both without knowing which one the row wants.
    /// </summary>
    public static void Wire(OptionsAssemblerHarness h, string locKey, FakeFlagPort flag, FakeChoicePort choice)
    {
        switch (locKey)
        {
            case "LCD overdrive":                 h.F.Device.LcdOverdrive = flag; break;
            case "Keyboard backlight timeout":    h.F.Device.KeyboardBacklight = flag; break;
            case "Fn lock":                       h.F.Device.FnLock = flag; break;
            case "Charge limit (~80%)":           h.F.Device.BatteryChargeLimit = flag; break;
            case "Calibration (full cycle)":      h.F.Device.BatteryCalibration = flag; break;
            case "USB charging when off:":        h.F.Device.UsbCharging = choice; break;
            case "Charge mode":                   h.F.Device.BatteryChargeMode = choice; break;
            default:
                throw new ArgumentOutOfRangeException(nameof(locKey), locKey, "no port slot is mapped to this row");
        }
    }

    /// <summary>The English keys of every row whose write funnels through <c>RunSet</c>: the whole of
    /// <c>Toggles()</c>, both battery rows, and the two pick-one-of-N rows in <c>Choices()</c>. The blue-light
    /// row is absent on purpose — it does not go through <c>RunSet</c> at all (OptionsAssembler.cs:81) — and
    /// so is the LampArray row, which needs a transport this fixture does not build.</summary>
    public static readonly string[] RunSetRowKeys =
    [
        "LCD overdrive", "Keyboard backlight timeout", "Fn lock",
        "USB charging when off:", "Charge mode", "Charge limit (~80%)", "Calibration (full cycle)",
    ];

    /// <summary>A device with every option port present and every write REFUSED — the arrangement the
    /// per-row failure theory needs. One refusing fake serves all five on/off slots and one serves all three
    /// dropdown slots: the rows are built and invoked one case at a time, so sharing them cannot cross cases.
    /// No <c>LastError</c> is set, so the message is exactly "&lt;name&gt; failed" for every row.</summary>
    public static OptionsAssemblerHarness AllRefusing()
    {
        var h = new OptionsAssemblerHarness();
        var flag = new FakeFlagPort { SetResult = false };
        var choice = new FakeChoicePort("off", "10", "20", "30") { SetResult = false };
        foreach (var key in RunSetRowKeys) Wire(h, key, flag, choice);
        return h;
    }

    /// <summary>Drop the nulls out of an optional row (the battery rows are nullable rather than absent).</summary>
    private static IEnumerable<T> Optional<T>(params T?[] rows) where T : class => rows.OfType<T>();
}
