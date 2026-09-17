using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// <see cref="OptionsAssembler"/> — the one place that knows the device's option ports and funnels every
/// setter through <c>RunSet</c> so a failed hardware write reaches the user instead of dying silently.
///
/// WHY THESE TESTS CAN EXIST AT ALL: <c>RunSet</c> is <c>private</c> (InternalsVisibleTo grants access to
/// <c>internal</c> only), so nothing here calls it. Its only observable effect used to be a
/// <c>Dispatcher.UIThread.Post</c> — unreachable from a headless test — which is exactly why the post is now
/// an injected delegate. Every failure assertion below therefore drives a PUBLIC model's <c>OnChange</c> and
/// captures what <c>RunSet</c> handed to <c>post</c>, then RUNS it: the message is built inside the posted
/// action, on the UI thread, so until it is run the user has been told nothing.
///
/// NO REAL HARDWARE: the fixture builds <see cref="LaptopService"/> over <see cref="FakeDevice"/> with nothing
/// declared, and each test declares the setting its row reads (<c>FakeDevice.Declare</c>, which is what a
/// backend's <c>InitVendor</c> does with its own key) — through the harness's <c>declare</c> argument, because
/// the declaration has to be in place before the fixture builds the service: the settings model copies the
/// declared set at construction (Infrastructure/Composition/Settings.cs). <c>LampArray</c> is never built — the fixture passes no
/// transport — so the "Windows Dynamic Lighting" row does not exist in these tests, and it is the one row whose
/// failure path is NOT covered here.
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
    /// the control AND carry the transport's own words — "something failed" is not actionable, and the port's
    /// <c>LastError</c> is the only explanation the user will ever get.</summary>
    [Fact]
    public void AFailedSet_PostsOneNotification_WithTheControlNameAndThePortsError()
    {
        var h = new OptionsAssemblerHarness(declare: d =>
            d.Declare(Keys.Lcd, new FakeFlagPort { SetResult = false, LastError = "EC refused the write" },
                      readbackVerifiesWrite: false));

        AssemblerRows.Toggle(h, "LCD overdrive").OnChange(true);

        Assert.Single(h.Posted);       // one post, and it is a post — not a direct call
        Assert.Empty(h.Notices);       // ...so nothing has been shown yet: the message is built on the UI thread
        Assert.Equal("LCD overdrive failed: EC refused the write", Assert.Single(h.RunPosted()));
    }

    /// <summary>PINNED BEHAVIOUR: a refused write with NO error text still reports — the suffix is
    /// conditional (<c>OptionsAssembler.Fail</c>), so the user gets "LCD overdrive failed" and not "…failed: "
    /// with a dangling colon. Both halves are pinned by the exact equality: dropping the suffix would let a
    /// silent EC look like a success, while making it unconditional is what a naive tidy-up produces.</summary>
    [Fact]
    public void AFailedSet_WithNoErrorText_SaysOnlyThatItFailed()
    {
        var h = new OptionsAssemblerHarness(declare: d =>
            d.Declare(Keys.Lcd, new FakeFlagPort { SetResult = false, LastError = null },
                      readbackVerifiesWrite: false));

        AssemblerRows.Toggle(h, "LCD overdrive").OnChange(true);

        Assert.Equal("LCD overdrive failed", Assert.Single(h.RunPosted()));
    }

    /// <summary>A write that lands must not notify at all. Pinned because the failure branch is one `if`
    /// away from firing on every write — a successful set returns without throwing, so there is no reason to
    /// carry and nothing to read back afterwards.</summary>
    [Fact]
    public void ASuccessfulSet_PostsNothing_AndNotifiesNothing()
    {
        var lcd = new FakeFlagPort { SetResult = true };
        var h = new OptionsAssemblerHarness(declare: d => d.Declare(Keys.Lcd, lcd, readbackVerifiesWrite: false));

        AssemblerRows.Toggle(h, "LCD overdrive").OnChange(true);

        Assert.Equal([true], lcd.SetCalls);      // the write did happen
        Assert.Empty(h.Posted);
        Assert.Empty(h.Notices);
    }

    /// <summary>...and a STALE <c>LastError</c> left over from an earlier failure must not resurrect the
    /// message: the branch is on the write's outcome, not on the error text. The port's <c>LastError</c> is
    /// a property of the PORT, not of the write (Domain/Ports.cs's <c>IFlagPort.LastError</c>), so it can be
    /// non-null while the current write is perfectly fine.
    ///
    /// THE STALE ERROR HAS TO SIT ON THE PORT THE SECOND WRITE REACHES, or the test does not arrange the
    /// scenario it names. It used to declare a FRESH, clean port for that second write — a port whose
    /// <c>LastError</c> is null — so the stale error it describes was never in play and the assertion held for
    /// a reason the name did not mention (measured: under a mutation that branches on the error text, that form
    /// stayed green and this one reddens). The SAME port now carries the failure into the success: its
    /// <c>SetResult</c> is flipped while <c>LastError</c> is left exactly as the refusal left it.
    ///
    /// The second <c>SetCalls</c> assertion is the control: "no second notification" would also hold for a
    /// click whose write never happened.</summary>
    [Fact]
    public void ASuccessfulSet_AfterAFailure_DoesNotNotifyAgain()
    {
        var fn = new FakeFlagPort { SetResult = false, LastError = "old news" };
        var h = new OptionsAssemblerHarness(declare: d => d.Declare(Keys.FnLock, fn));
        AssemblerRows.Toggle(h, "Fn lock").OnChange(true);
        Assert.Single(h.Posted);                                // the refusal reported...

        fn.SetResult = true;                                    // ...and the SAME port now takes the write,
        AssemblerRows.Toggle(h, "Fn lock").OnChange(true);      // with "old news" still sitting on it

        Assert.Equal([true, true], fn.SetCalls);                // Control: the second write really went out
        Assert.Equal("Fn lock failed: old news",                // and only the FIRST failure spoke
                     Assert.Single(h.RunPosted()));
    }

    /// <summary>PINNED BEHAVIOUR: a transport whose <c>Set</c> THROWS is a failed write, not a crashed row. This
    /// is the behaviour that keeps one bad port from taking its switch down with it — the exception must not
    /// escape the <c>OnChange</c> the UI calls, and the user must still be told.
    ///
    /// WHERE the throw is absorbed, and WHAT IT MAY CARRY. The write goes through
    /// <c>LaptopService.ApplySetting</c> -> <c>SettingDeclaration.Apply</c> -> <c>FlagSetting.Write</c>, whose
    /// catch absorbs it. That layer deliberately reports NO reason, and the message asserted below is bare
    /// because of it: the port's <c>LastError</c> is assigned by a call that runs to COMPLETION, so a throw
    /// leaves whatever an earlier call put there, and reading it would pin another call's words on this failure
    /// (Domain/DeclaredSetting.cs). The port below carries exactly such a leftover — an earlier refusal's
    /// "transport gone" — and the assertion is that it does NOT appear. That is also why the reason genuinely
    /// crossing the layer is pinned where it is real: a write that returns false (the test above, "...: EC
    /// refused the write"). A declaration that let the throw through would report a bare name too, from
    /// <c>RunSet</c>'s own catch.</summary>
    [Fact]
    public void AThrowingPort_IsReportedAsAFailure_AndDoesNotEscapeTheRow()
    {
        var lcd = new FakeFlagPort { ThrowOnSet = true, LastError = "transport gone" };   // an earlier call's words
        var h = new OptionsAssemblerHarness(declare: d => d.Declare(Keys.Lcd, lcd, readbackVerifiesWrite: false));

        var row = AssemblerRows.Toggle(h, "LCD overdrive");

        Assert.Null(Record.Exception(() => row.OnChange(true)));   // the throw must not leave the row
        Assert.Equal([true], lcd.SetCalls);               // the write was attempted, and only once
        Assert.Equal("LCD overdrive failed", Assert.Single(h.RunPosted()));   // reported, with no invented cause
    }

    /// <summary>PINNED BEHAVIOUR: a throwing PROFILE port is reported as a failure and does not escape the row,
    /// and the message is BARE here because <see cref="FakeThrowingPowerProfiles"/> carries no reason. The
    /// sibling below arranges a STALE reason on the same fake, which is the sharper form of the same assertion
    /// — read them together: this one pins the outcome, that one pins what may not be read to produce it.
    ///
    /// WHICH LAYER ABSORBS THIS THROW: the write goes <c>SetSourceProfile</c> -> <c>ApplyStoredMode</c> ->
    /// <c>Attempt</c>, whose <c>try { ok = write(); } catch { ok = false; }</c> catches it
    /// (<c>LaptopService.Attempt</c>), NOT <c>RunSet</c>'s own catch — the power-source row is one of the rows
    /// that is not a declared setting, so it still hands back a pair and <c>RunSet</c> sees no exception at all
    /// (docs/open-decisions.md, «Известные особенности» 4, where the opposite claim was withdrawn). What can
    /// still reach <c>RunSet</c>'s own catch on that overload is a throw from OUTSIDE a wrapped call — a profile
    /// port's read (unwrapped in <c>ApplyStoredMode</c>) or the row's own index arithmetic.
    ///
    /// THE NAME THEREFORE CLAIMS NO ABSORBER, and it must not: THIS test cannot tell <c>Attempt</c> from
    /// <c>RunSet</c>. Its fake carries no reason, so the message is bare whichever layer caught the throw. The
    /// witness for the absorber is the flag-row test above, whose assertion ends in the port's own reason.
    /// What THIS test pins is the OUTCOME the row owes the user: reported exactly once, and not escaped.
    /// Renamed from <c>AThrowingProfileSet_IsCaughtByRunSetItself_AndStillReported</c>, which asserted the wrong
    /// absorber.</summary>
    [Fact]
    public void AThrowingProfileSet_DoesNotEscapeThePowerSourceRow_AndIsStillReported()
    {
        var h = new OptionsAssemblerHarness(LaptopServiceFixture.WithProfiles(current: TestProfiles.Quiet));
        var pp = new FakeThrowingPowerProfiles();
        h.F.Device.PowerProfiles = pp;

        var row = AssemblerRows.Choice(h, "Profile on AC power:");

        Assert.Null(Record.Exception(() => row.OnChange(1)));      // the throw must not leave the row
        Assert.Single(pp.SetCalls);                        // the write was attempted once, then threw
        Assert.Equal("Profile on AC power: failed", Assert.Single(h.RunPosted()));
    }

    /// <summary>A throwing PROFILE port must not report an EARLIER call's reason, and this is the test that
    /// can tell — the one above cannot, because its fake carries no reason at all.
    ///
    /// <c>Attempt</c> fetches its reason ONLY from a write that RETURNED (<c>LaptopService.Attempt</c>):
    /// <see cref="IPowerProfiles.LastError"/> is a field the port assigns at the end of a call that runs to
    /// completion, so a write that THREW never assigned it, and whatever is in it belongs to a previous call.
    /// The fake below is arranged exactly that way — a leftover "old news" beside a write that blows up — so a
    /// caller that read the field after catching would print "...: old news": a real failure wearing another
    /// call's words, which is the failure mode docs/open-decisions.md §2 exists to remove.
    ///
    /// THE CONTROL IS THE SERVICE-LEVEL SIBLING, not a second case here: <c>LaptopServiceModeTests</c>'
    /// <c>AFailedApply_ReturnsFalse_AndPropagatesTheError</c> pins a refusal that RETURNS and asserts that the
    /// port's words DO cross (<c>r.error == "EC refused"</c>), which is how every real transport in this tree
    /// reports. Without it, "no reason is reported" and "no reason ever crosses this layer" would be the same
    /// observation.</summary>
    [Fact]
    public void AThrowingProfileSet_CarriesNoReasonOfAnEarlierCall()
    {
        var h = new OptionsAssemblerHarness(LaptopServiceFixture.WithProfiles(current: TestProfiles.Quiet));
        var pp = new FakeThrowingPowerProfiles { LastError = "old news" };   // an earlier call's words
        h.F.Device.PowerProfiles = pp;

        var row = AssemblerRows.Choice(h, "Profile on AC power:");

        row.OnChange(1);

        Assert.Single(pp.SetCalls);                        // the write was attempted once, then threw
        Assert.Equal("Profile on AC power: failed", Assert.Single(h.RunPosted()));   // the stale words are not read
    }

    /// <summary>Every row that reports a failure must name ITSELF — an error that does not say which control
    /// failed is the bug this file guards, and a copy-paste that leaves the wrong name in one row is invisible
    /// until it fires.
    ///
    /// THE NAME IS THE ROW'S OWN LABEL, and that is the whole of this theory now: the message used to be
    /// composed from a second, English name per setting, which disagreed with the label on five of these rows
    /// ("Keyboard backlight timeout" failed as "Backlight timeout", "Charge limit (~80%)" as "Battery limit", and
    /// so on) — so the user was told about a control the interface never showed them
    /// (docs/open-decisions.md, «Известные особенности» 5). Those five texts changed here by design, and each
    /// expected value below is now the label.</summary>
    [Theory]
    [InlineData("LCD overdrive")]                                     // a declared flag
    [InlineData("Keyboard backlight timeout")]                        // a declared flag (Acer)
    [InlineData("Fn lock")]                                           // a declared flag (Dell)
    [InlineData("USB charging when off:")]                            // a declared choice
    [InlineData("Charge mode")]                                       // the battery object
    [InlineData("Charge limit (~80%)")]                               // the battery object
    [InlineData("Calibration (full cycle)")]                          // the battery object
    public void AFailedSet_TellsTheUserWhichControlFailed(string locKey)
    {
        var h = AssemblerRows.AllRefusing();

        AssemblerRows.Change(h, locKey);

        Assert.Single(h.Posted);
        Assert.Equal($"{Loc.T(locKey)} failed", Assert.Single(h.RunPosted()));
    }
}

/// <summary>
/// The readback wiring: which rows carry a <c>Read</c> delegate, and what that delegate answers. The row's
/// readback is what corrects a switch (or a dropdown) after a write the firmware accepted but did not apply,
/// so a <c>Read</c> that answers from a copy taken at build time would leave the UI lying about the hardware
/// — the exact bug the readback exists to remove (<c>OptionToggle.Read</c>).
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
        var fn = new FakeFlagPort { State = true };
        var h = new OptionsAssemblerHarness(declare: d => d.Declare(Keys.FnLock, fn));

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

    /// <summary>LCD overdrive deliberately has NO readback, and the declaration says so rather than this file:
    /// its write (<c>SetGamingProfile</c> on Windows, the <c>lcd_override</c> node on Linux) reports whether it
    /// took, so the row self-confirms and skips the second hardware transaction the user would hear as a second
    /// "click" (<see cref="SettingDeclaration.ReadbackVerifiesWrite"/>). Pinned because "add Read everywhere"
    /// looks like a tidy-up and is not — it would be an audible regression on every toggle. The row still needs
    /// a way to show its value, and a row with no Read gets it from an explicit <c>Prime</c>.</summary>
    [Fact]
    public void TheLcdRowHasNoReadback_BecauseItsWriteSelfConfirms()
    {
        var lcd = new FakeFlagPort { State = true };
        var h = new OptionsAssemblerHarness(declare: d => d.Declare(Keys.Lcd, lcd, readbackVerifiesWrite: false));

        var row = AssemblerRows.Toggle(h, "LCD overdrive");
        Assert.Null(row.Read);

        row.OnChange(true);

        Assert.Equal([true], lcd.SetCalls);  // the write went through
        Assert.Equal(0, lcd.GetCount);       // ...and read nothing back: the write self-confirms, and the build defers
        Assert.NotNull(row.Prime);           // so the row carries the one explicit Prime instead
        Assert.True(row.Prime!());           // which is exactly the port's Get
        Assert.Equal(1, lcd.GetCount);       // one transaction — and only because a prime was asked for
    }

    /// <summary>The other polarity of <see cref="SettingDeclaration.ReadbackVerifiesWrite"/>, and the one every
    /// other setting uses: a declaration that does NOT self-confirm hands the row the same read as both its
    /// readback and its prime (the view-model resolves <c>Prime ?? Read</c>), so no declaration needs to supply
    /// two delegates. Together with the LCD test above this pins the fork the declaration carries — a mutation
    /// that ignores the flag, in either direction, reddens one of the two.</summary>
    [Fact]
    public void ADeclarationThatDoesNotSelfConfirm_CarriesAReadback_AndNoSeparatePrime()
    {
        var h = new OptionsAssemblerHarness(declare: d => d.Declare(Keys.FnLock, new FakeFlagPort { State = true }));

        var row = AssemblerRows.Toggle(h, "Fn lock");

        Assert.NotNull(row.Read);      // the readback exists...
        Assert.Null(row.Prime);        // ...and is what the prime falls back on, so there is no second delegate
    }

    /// <summary>A dropdown's <c>Read</c> answers with the INDEX of what the port reports now, not the id
    /// (<c>OptionChoice.Read</c>). The unknown-id and null cases are PINNED: <c>ChoiceSetting.IndexOf</c> returns
    /// 0 both for "no such option" and for the first option, so a port reporting something this build does not
    /// offer reads as the first entry — the row then shows a setting the hardware is not in, and a subsequent
    /// pick would start from a lie.</summary>
    [Theory]
    [InlineData("off", 0)]
    [InlineData("20", 2)]
    [InlineData("30", 3)]
    [InlineData("never-heard-of-it", 0)]     // PINNED: an unrecognised id reads as index 0
    [InlineData(null, 0)]                    // PINNED: an unreadable port reads as index 0
    [InlineData("OFF", 0)]                   // PINNED: ids are matched exactly — no case folding
    public void AChoiceRowsReadback_MapsThePortsLiveIdToADropdownIndex(string? currentId, int expected)
    {
        var usb = new FakeChoicePort("off", "10", "20", "30") { CurrentId = currentId };
        var h = new OptionsAssemblerHarness(declare: d => d.Declare(Keys.Usb, usb));

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
    /// (<c>OptionsAssembler.PowerSourceProfiles</c>).</summary>
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
/// Which rows exist. A setting's PRESENCE is now the declaration itself — the backend adds one for each knob its
/// probe found (Infrastructure/Composition/Settings.cs) — so a row that appears without a declaration would promise the user a
/// control that cannot work, and one that fails to appear hides a setting the machine does have.
///
/// The three battery rows are the exception and stay so: they exist exactly when the battery OBJECT has the
/// property (Domain/Battery.cs), which is the same rule stated one level down. This file's variant of it
/// covers them one at a time; <c>BatteryTests</c> covers the whole present/absent combination.
/// </summary>
public class OptionsAssemblerPresenceTests
{
    /// <summary>The degenerate device: nothing declared, so nothing at all is offered.</summary>
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

    /// <summary>Every row appears on exactly its own declaration, and none of them on any other. Renamed from
    /// <c>ARowAppearsExactlyWhenItsPortIsPresent</c>: the thing a row's presence now tests is a declaration, and
    /// four of these seven rows no longer have a port of their own at all.</summary>
    [Theory]
    [InlineData("LCD overdrive")]
    [InlineData("Keyboard backlight timeout")]      // the on/off row
    [InlineData("Fn lock")]
    [InlineData("USB charging when off:")]
    [InlineData("Charge mode")]
    [InlineData("Charge limit (~80%)")]
    [InlineData("Calibration (full cycle)")]
    public void ARowAppearsExactlyWhenTheBackendDeclaresIt(string locKey)
    {
        var absent = new OptionsAssemblerHarness();
        Assert.DoesNotContain(Loc.T(locKey), AssemblerRows.Labels(absent));

        var present = new OptionsAssemblerHarness(declare: d =>
            AssemblerRows.Wire(d, locKey, new FakeFlagPort(), new FakeChoicePort("off", "on")));
        Assert.Contains(Loc.T(locKey), AssemblerRows.Labels(present));
    }

    /// <summary>A declaration whose key this build has no label for still produces a row, named by the key.
    /// The alternative — dropping the row — would hide a control the machine has because a UI table is behind
    /// the backend, and the failure it would produce would be silence (the class of bug the whole channel
    /// exists to remove). The fallback is the only reason <c>LabelFor</c> has a default arm at all.</summary>
    [Fact]
    public void AnUnknownKey_StillGetsARow_AndFailsUnderTheKeyItself()
    {
        var h = new OptionsAssemblerHarness(declare: d =>
            d.Declare("acer.somethingNew", new FakeFlagPort { SetResult = false }));

        var row = AssemblerRows.Toggle(h, "acer.somethingNew");
        Assert.Equal("acer.somethingNew", row.Label);

        row.OnChange(true);

        Assert.Equal("acer.somethingNew failed", Assert.Single(h.RunPosted()));
    }

    /// <summary>The dropdown variant of the same device: only the timeout CHOICE row appears when the
    /// duration declaration is the one present, and the on/off row of the same name stays away.</summary>
    [Fact]
    public void TheTimeoutRowsAreIndependent_OneForTheFlagSetting_OneForTheChoiceSetting()
    {
        var h = new OptionsAssemblerHarness(declare: d => d.Declare(Keys.Timeout, new FakeChoicePort("5", "30", "60")));

        Assert.Contains(Loc.T("Keyboard backlight timeout:"), AssemblerRows.Labels(h));
        Assert.DoesNotContain(Loc.T("Keyboard backlight timeout"), AssemblerRows.Labels(h));
    }

    /// <summary>The blue-light row is suppressed when the tint port has no levels to offer — the presence
    /// test is <c>tint.Levels &gt; 0</c> and nothing else (<c>OptionsAssembler.Choices</c>) — and when it IS offered
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
    /// the pair only means something when there is a profile to pick
    /// (<c>OptionsAssembler.PowerSourceProfiles</c>). The two halves are different devices — the first has no
    /// <c>PowerProfiles</c> at all, the second has one that lists
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
/// starts a multi-hour charge/discharge cycle, so the row must not be able to run one from the click alone —
/// and no OTHER row may ask, because an extra dialog is invisible until it
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
        h.F.Device.Battery.Calibration = new FakeFlagPort().AsBatteryToggle();

        var row = h.Assembler.BatteryCalibration();

        Assert.NotNull(row);
        Assert.Same(confirm, row!.ConfirmAsync);
        Assert.Null(row.Confirm);        // the async form only: OptionToggle allows one of the two (Confirm/ConfirmAsync)
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
    /// <param name="declare">The fake backend's probe, run BEFORE the service (and so before the settings model)
    /// is built — the only order there is now, and the same one a vendor backend has. Omitted with a
    /// <paramref name="fixture"/>, which was built with its own.</param>
    public OptionsAssemblerHarness(LaptopServiceFixture? fixture = null, Func<Task<bool>>? confirmCalibration = null,
                                   Action<FakeDevice>? declare = null)
    {
        F = fixture ?? new LaptopServiceFixture(declare: declare);
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
/// Row lookup and wiring by LABEL, because the row — not the setting — is what the user sees, and one fake
/// serves several declarations (Domain/Ports.cs: every on/off transport is exactly <see cref="IFlagPort"/>).
///
/// Every helper takes the ENGLISH text that <c>Loc.T</c> looks up, not a translated label: the keys are
/// stable, the labels are not. <see cref="Keys"/> holds the other half of that vocabulary — the keys the
/// backends own, which a declaration must carry for the UI to know what to call it.
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
    /// Attach a fake to whichever row this key names, the way the machine's backend declares it. The switch is
    /// on the English KEY because that is what identifies the row independently of any translation table; the
    /// fakes of the other kind are ignored, so a caller may pass both without knowing which one the row wants.
    /// The battery's three properties are not declared settings, so they are still attached to the object.
    ///
    /// It takes the DEVICE rather than the harness because a declared setting has to be declared before the
    /// settings model is built (<see cref="OptionsAssemblerHarness"/>'s <c>declare</c> argument), and the
    /// device is what exists at that point.
    /// </summary>
    public static void Wire(FakeDevice device, string locKey, FakeFlagPort flag, FakeChoicePort choice)
    {
        switch (locKey)
        {
            case "LCD overdrive":                 device.Declare(Keys.Lcd, flag, readbackVerifiesWrite: false); break;
            case "Keyboard backlight timeout":    device.Declare(Keys.BacklightTimeout, flag); break;
            case "Fn lock":                       device.Declare(Keys.FnLock, flag); break;
            case "Charge limit (~80%)":           device.Battery.ChargeLimit = flag.AsBatteryToggle(); break;
            case "Calibration (full cycle)":      device.Battery.Calibration = flag.AsBatteryToggle(); break;
            case "USB charging when off:":        device.Declare(Keys.Usb, choice); break;
            case "Charge mode":                   device.Battery.ChargeMode = choice.AsBatteryChoice(); break;
            default:
                throw new ArgumentOutOfRangeException(nameof(locKey), locKey, "no setting or property is mapped to this row");
        }
    }

    /// <summary>The English keys of every row whose write is reported through <c>OptionsAssembler.RunSet</c>:
    /// the declared settings that are flags or choices, both battery rows, and the two pick-one-of-N battery
    /// rows. The blue-light row is absent on purpose — it does not report through <c>RunSet</c> at all
    /// (<c>OptionsAssembler.Choices</c>) — and so is the LampArray row, which needs a transport this fixture
    /// does not build.</summary>
    public static readonly string[] RunSetRowKeys =
    [
        "LCD overdrive", "Keyboard backlight timeout", "Fn lock",
        "USB charging when off:", "Charge mode", "Charge limit (~80%)", "Calibration (full cycle)",
    ];

    /// <summary>A device with every row present and every write REFUSED — the arrangement the per-row failure
    /// theory needs. One refusing fake serves all the on/off settings and one serves all the pick-one settings:
    /// the rows are built and invoked one case at a time, so sharing them cannot cross cases. No fake carries a
    /// reason (their <c>LastError</c> stays null), so the message is exactly "&lt;label&gt; failed" for every
    /// row.</summary>
    public static OptionsAssemblerHarness AllRefusing()
    {
        var flag = new FakeFlagPort { SetResult = false };
        var choice = new FakeChoicePort("off", "10", "20", "30") { SetResult = false };
        return new OptionsAssemblerHarness(declare: d =>
        {
            foreach (var key in RunSetRowKeys) Wire(d, key, flag, choice);
        });
    }

    /// <summary>Drop the nulls out of an optional row (the battery rows are nullable rather than absent).</summary>
    private static IEnumerable<T> Optional<T>(params T?[] rows) where T : class => rows.OfType<T>();
}

/// <summary>
/// The keys the fakes declare their settings under. A key is the BACKEND's own name for a setting and the UI
/// never invents one, so these are the names the shipped backends use: Acer's three are the Linuwu-Sense node
/// names its Linux half binds the same knobs through (AcerDevice.Linux.cs), declared by its Windows half too; of
/// Dell's, two are the BIOS-attribute names dell-wmi-sysman exposes on both OSes and the third is the LED node
/// the timeout lives on.
///
/// THEY ARE NOT CHECKED AGAINST THE BACKENDS, and that is a named gap rather than an oversight: no test
/// constructs <c>AcerDevice</c>/<c>DellDevice</c> (their probes need WMI and real firmware — see the wave-4b
/// notes), so a backend that renamed a key would leave the UI showing the key itself instead of a label, and
/// nothing here would redden. What IS pinned is that such a key still yields a row and a message
/// (<c>AnUnknownKey_StillGetsARow_AndFailsUnderTheKeyItself</c>) rather than silence.</summary>
internal static class Keys
{
    public const string Lcd              = "lcd_override";        // Acer: the on/off LCD-overdrive node
    public const string BacklightTimeout = "backlight_timeout";   // Acer: the on/off timeout node
    public const string Usb              = "usb_charging";        // Acer: battery-threshold choice
    public const string Timeout          = "stop_timeout";        // Dell: the duration choice
    public const string FnLock           = "FnLock";              // Dell: a BIOS attribute
}
