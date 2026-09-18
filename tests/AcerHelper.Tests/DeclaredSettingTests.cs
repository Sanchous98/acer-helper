using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The declared-settings contract, and the two halves of it (docs/domain-refactoring-plan.md §5, waves 5 + 9).
///
/// WHAT IT IS. A backend declares which settings this machine has, each with its own opaque key and its own
/// shape — a flag or a pick-one-of-N choice (Domain/DeclaredSetting.cs). Applying one either works or THROWS
/// <see cref="SettingNotAppliedException"/>, and the exception carries information rather than a sentence: the
/// message the user reads is composed by the UI from the row's own label, which is why the domain can hold no
/// setting names at all. What the value MEANS once applied is the row's business
/// (<c>OptionsAssemblerTests</c>), and what survives a restart is the settings bag.
///
/// WHERE THE SET LIVES. The declarations are NOT a port on the machine: the settings model is CONSTRUCTED with the
/// set of options this machine declares (<c>Settings</c>'s constructor) and carries the logic of switching one,
/// so these tests arrange them through the fixture's fake backend — which declares before the fixture builds the
/// service, exactly as a vendor backend declares inside <c>InitVendor</c> — and read them back off the service.
///
/// WHY THESE CAN EXIST. <c>LaptopService</c>, its settings store and a declared setting are all reachable
/// without hardware: the setting's transport is a fake, and the row's failure path was already made observable
/// by the injected <c>post</c> delegate (OptionsAssemblerTests says why). Nothing here needs a real knob.
/// </summary>
public class DeclaredSettingTests
{
    // ---------------------------------------------------------------- the domain half: refusal is an exception

    /// <summary>A refused apply throws, and the exception names the setting by its key and carries the
    /// transport's own words. Both halves matter: the key is what lets a caller connect the failure to the row
    /// it came from, and the reason is the only explanation the user will ever get.</summary>
    [Fact]
    public void ARefusedWrite_Throws_WithTheKeyAndTheTransportsReason()
    {
        var setting = new FlagSetting
        {
            Key = "FnLock",
            Port = new FakeFlagPort { SetResult = false, LastError = "Access Denied" },
        };

        var ex = Assert.Throws<SettingNotAppliedException>(() => setting.Apply("1"));

        Assert.Equal("FnLock", ex.Key);
        Assert.Equal("Access Denied", ex.Reason);
    }

    /// <summary>The other half: a write that lands does not throw, and the value's two spellings mean what the
    /// settings bag means by them — <c>"1"</c> is on, <c>"0"</c> is off (<c>LaptopService.GetDeviceFlag</c>
    /// compares against exactly that string). Pinned because a declaration that encoded a flag some other way
    /// would round-trip through the bag as "off" without anything else noticing.</summary>
    [Fact]
    public void AnAcceptedWrite_DoesNotThrow_AndTheTwoSpellingsTurnTheFlagOnAndOff()
    {
        var port = new FakeFlagPort();
        var setting = new FlagSetting { Key = "FnLock", Port = port };

        Assert.Null(Record.Exception(() => setting.Apply(FlagSetting.Value(true))));
        Assert.Equal([true], port.SetCalls);

        setting.Apply(FlagSetting.Value(false));
        Assert.Equal([true, false], port.SetCalls);
    }

    /// <summary>A transport whose write THROWS is a refusal, not a crash — and it carries NO reason, because the
    /// only one available belongs to another call. This is the rule <c>LaptopService.Attempt</c> stated for the
    /// tuple channel, restated on the side of the exception: a throw is a failed write, and the failure it
    /// reports is its own. The assembler-side twin of this test asserts the message the user ends up reading.
    ///
    /// THE ARRANGEMENT IS THE TEST, and both halves of it are asserted. The SAME port is first REFUSED — which is
    /// what leaves a refusal's words on it, and the exception reports them, so the field really is in play — and
    /// then made to THROW, which is a call that never assigns it. Under the old
    /// <c>catch { ok = false; }</c> the declaration read the field after the throw and the second refusal came
    /// back wearing the first one's words ("Access Denied"); now the reason is absent.
    ///
    /// MUTATION that reddens it: put the field read back on the throw path (replace the early
    /// <c>return (false, null)</c> with <c>catch { ok = false; }</c>) — measured, it reddens TWO tests, not this
    /// one alone: this test and its assembler-side twin
    /// <c>OptionsAssemblerFailureTests.AThrowingPort_IsReportedAsAFailure_AndDoesNotEscapeTheRow</c>, which
    /// asserts the sentence the user ends up reading. Nothing else in the suite throws from a flag port — every
    /// other test here reaches the field through a write that RETURNED.</summary>
    [Fact]
    public void AThrowingTransport_IsARefusal_ThatCarriesNoReasonOfItsOwn()
    {
        var port = new FakeFlagPort { SetResult = false, LastError = "Access Denied" };
        var setting = new FlagSetting { Key = "lcd_override", Port = port };

        var refused = Assert.Throws<SettingNotAppliedException>(() => setting.Apply("1"));
        Assert.Equal("Access Denied", refused.Reason);   // the refusal reports the port's words...

        port.ThrowOnSet = true;                          // ...and now the transport dies instead
        var threw = Assert.Throws<SettingNotAppliedException>(() => setting.Apply("1"));

        Assert.Equal("lcd_override", threw.Key);
        Assert.Null(threw.Reason);                       // NOT the previous call's "Access Denied"
        Assert.Equal([true, true], port.SetCalls);       // Control: both writes were attempted
    }

    /// <summary>The same two facts for the other shape: <see cref="ChoiceSetting.Write"/> absorbs a throw the
    /// way <see cref="FlagSetting.Write"/> does, and it too reports no reason of its own. Pinned separately
    /// because the two overrides are separate code, and a fix applied to one of them is invisible here.
    ///
    /// MUTATION that reddens it: restore <c>catch { ok = false; }</c> in <c>ChoiceSetting.Write</c> (the field
    /// read after a throw) — measured, this test alone, since nothing else in the suite throws from a choice
    /// port (the twin above is the one mutation that reaches further, and it does so through
    /// <c>FlagSetting.Write</c>).</summary>
    [Fact]
    public void AThrowingChoiceTransport_CarriesNoReasonOfItsOwnEither()
    {
        var port = new FakeChoicePort("5s", "30s") { SetResult = false, LastError = "Access Denied" };
        var setting = new ChoiceSetting { Key = "stop_timeout", Port = port };

        Assert.Equal("Access Denied",
                     Assert.Throws<SettingNotAppliedException>(() => setting.Apply("30s")).Reason);

        port.ThrowOnSet = true;
        var threw = Assert.Throws<SettingNotAppliedException>(() => setting.Apply("30s"));

        Assert.Equal("stop_timeout", threw.Key);
        Assert.Null(threw.Reason);
        Assert.Equal(["30s", "30s"], port.SetCalls);   // Control: both writes were attempted
    }

    /// <summary>A choice maps the id the hardware reports to the dropdown INDEX the row needs — and answers 0
    /// for an id this build does not offer as well as for an unreadable transport, which is the value the row
    /// showed before the mapping lived on the declaration.</summary>
    [Theory]
    [InlineData("5s", 0)]
    [InlineData("1h", 2)]
    [InlineData("nope", 0)]      // PINNED: an unrecognised id reads as index 0
    [InlineData(null, 0)]        // PINNED: an unreadable transport reads as index 0
    public void AChoiceSetting_MapsTheHardwareIdToADropdownIndex(string? id, int expected)
    {
        var setting = new ChoiceSetting
        {
            Key = "stop_timeout",
            Port = new FakeChoicePort("5s", "30s", "1h") { CurrentId = id },
        };

        Assert.Equal(expected, setting.IndexOf(setting.Read()));
    }

    // ---------------------------------------------------------------- the settings half: the value is recorded

    /// <summary>Applying a setting is not only a hardware write: the value is recorded under the setting's own
    /// key, in the bag the backend-owned flags already live in, and the graph is saved. That is the whole of
    /// what the contract adds to persistence — no new property, no renamed key, so the settings.json guards see
    /// exactly what they saw before.
    ///
    /// The declaration applied here is read back OFF THE MODEL, which is what the UI does: every row is built
    /// from <see cref="LaptopService.DeclaredSettings"/> and hands that very declaration back on a click.</summary>
    [Fact]
    public void AnAppliedSetting_IsRecordedUnderItsOwnKey_AndSaved()
    {
        var port = new FakeFlagPort();
        var f = new LaptopServiceFixture(declare: d => d.Declare("lcd_override", port, readbackVerifiesWrite: false));
        var setting = f.Service.DeclaredSettings.OfType<FlagSetting>().Single();

        f.Service.ApplySetting(setting, FlagSetting.Value(true));

        Assert.Equal([true], port.SetCalls);                              // the hardware write happened
        Assert.Equal("1", f.Store.Settings.DeviceSettings["lcd_override"]); // ...and the value was recorded
        Assert.True(f.Service.GetDeviceFlag("lcd_override", false));       // read back through the bag's own accessor
        Assert.Equal(1, f.Store.SaveCount);
    }

    /// <summary>A choice records the option id VERBATIM — which is the point of the bag having to learn more
    /// than "1"/"0" (a duration, a battery threshold and a BIOS enum are not booleans, and a
    /// <c>Dictionary&lt;string,string&gt;</c> holding only flags could not carry them).</summary>
    [Fact]
    public void AChoiceSettingsValue_IsRecordedVerbatim_BecauseTheBagCarriesMoreThanOneAndZero()
    {
        var f = new LaptopServiceFixture(declare: d => d.Declare("stop_timeout", new FakeChoicePort("5s", "30s", "1h")));
        var setting = f.Service.DeclaredSettings.Single();

        f.Service.ApplySetting(setting, "30s");

        Assert.Equal("30s", f.Store.Settings.DeviceSettings["stop_timeout"]);
    }

    /// <summary>A REFUSED apply records nothing and saves nothing: the bag holds what the hardware took, so a
    /// value the firmware rejected must not be remembered as if it were in force. Pinned as one test with two
    /// assertions rather than two tests, because "nothing" is the same fact about the same call.</summary>
    [Fact]
    public void ARefusedSetting_RecordsNothing_AndSavesNothing()
    {
        var f = new LaptopServiceFixture(declare: d =>
            d.Declare("lcd_override", new FakeFlagPort { SetResult = false, LastError = "EC said no" },
                      readbackVerifiesWrite: false));
        var setting = f.Service.DeclaredSettings.Single();

        Assert.Throws<SettingNotAppliedException>(() => f.Service.ApplySetting(setting, "1"));

        Assert.Empty(f.Store.Settings.DeviceSettings);
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>The hardware write runs OUTSIDE <c>_state</c> — docs/domain-refactoring-plan.md §4 forbids
    /// holding the graph lock across a blocking hardware operation, and a setting is an EC/WMI write. A fake
    /// observes the CALL and never the lock around it, so <see cref="LaptopService.StateHeld"/> is the only way
    /// to assert this at all; without it the invariant would be untestable on this path.
    ///
    /// The probe is handed the service AFTER the fixture is built, which is not sloppiness: the declaration it
    /// rides in has to exist before the service does (the model copies the set at construction), and the probe
    /// is only asked at WRITE time — so what it reports is the lock state then, which is the fact under test.</summary>
    [Fact]
    public void TheHardwareWrite_HappensOutsideTheStateLock()
    {
        var probe = new LockProbe();
        var f = new LaptopServiceFixture(declare: d => d.Declare("lcd_override", probe, readbackVerifiesWrite: false));
        probe.Service = f.Service;

        f.Service.ApplySetting(f.Service.DeclaredSettings.Single(), "1");

        Assert.True(probe.SetCalled);        // non-vacuity: the probe really was written through
        Assert.False(probe.HeldDuringSet);   // the lock never spanned that write
    }

    // ---------------------------------------------------------------- the model half: it holds the set, and switches it

    /// <summary>The set of declared options belongs to the MODEL and is supplied through its CONSTRUCTOR: it
    /// holds what it was constructed with and invents nothing, because what a machine HAS is what its backend's
    /// probe found and the machine carries it only so the service can hand it over. Pinned because
    /// the failure would be silent and would read as "this machine has no settings": a model that ignored the
    /// hand-off leaves every row unbuildable and every apply unreachable.</summary>
    [Fact]
    public void TheModelHoldsTheSetItWasConstructedWith_AndInventsNoneOfItsOwn()
    {
        var declared = new FlagSetting { Key = "lcd_override", Port = new FakeFlagPort() };

        var settings = new Settings([declared]);

        Assert.Equal([declared], settings.DeclaredSettings);
    }

    /// <summary>THE SET IS FIXED AT CONSTRUCTION, which is the rule the copy in <c>Settings</c>'s constructor
    /// exists for: a declaration made after the model exists does not reach it. Holding the backend's live list
    /// instead would let a probe keep adding options to a machine that had already been described — and it is
    /// what the fakes in this suite used to rely on, which is why they now declare before the fixture builds the
    /// service. Pinned as its own fact because every other test here arranges the two in the order that makes a
    /// live list look correct.
    ///
    /// Mutation that reddens it: hold the list by reference in the constructor (what this type did before the
    /// owner's ruling) — reddens this test alone.</summary>
    [Fact]
    public void ADeclarationMadeAfterTheModelExists_DoesNotReachIt()
    {
        var f = new LaptopServiceFixture();

        f.Device.Declare("lcd_override", new FakeFlagPort());

        Assert.Empty(f.Service.DeclaredSettings);
    }

    /// <summary>CHANGING A SETTING THAT DOES NOT EXIST IS AN ERROR — the other half of the owner's rule, and the
    /// one that is a behaviour change rather than a move. The model consults the set it holds and refuses,
    /// instead of writing to a transport the machine does not have.
    ///
    /// Three things are asserted, and all three are the claim: the refusal is the SAME exception the refusal
    /// channel already carries (one name — the UI composes "&lt;row label&gt; failed: &lt;reason&gt;" from it, so
    /// a second exception type would be a second channel), the reason is THIS layer's own words rather than a
    /// transport's, and nothing was written or recorded on the way out.
    ///
    /// It cannot fire through the UI — every row is built from the declared set — so it is pinned here, at the
    /// model, which is the only place it can be asked. That is also why no OptionsAssembler test covers it.
    ///
    /// Mutation that reddens it: delete the declared-set check in <c>Settings.Apply</c> — the undeclared write
    /// then lands, so this test alone reddens. Mutating the check to test the wrong polarity
    /// (<c>if (Declares(...)) throw</c>) reddens the whole positive path instead: every test above that applies
    /// a DECLARED setting.</summary>
    [Fact]
    public void AnUndeclaredSetting_IsRefusedBeforeTheTransportIsTouched()
    {
        var f = new LaptopServiceFixture(declare: d => d.Declare("FnLock", new FakeFlagPort()));
        var undeclaredPort = new FakeFlagPort();
        var undeclared = new FlagSetting { Key = "lcd_override", Port = undeclaredPort };

        var ex = Assert.Throws<SettingNotAppliedException>(() => f.Service.ApplySetting(undeclared, "1"));

        Assert.Equal("lcd_override", ex.Key);
        Assert.Equal("this machine does not declare it", ex.Reason);   // the clause the UI would append
        Assert.Empty(undeclaredPort.SetCalls);                        // the transport was never reached
        Assert.Empty(f.Store.Settings.DeviceSettings);                // nothing was recorded under its key
        Assert.Equal(0, f.Store.SaveCount);                           // ...and nothing was saved
    }

    /// <summary>The set is consulted by KEY, not by declaration INSTANCE, and that is a decision worth pinning
    /// where it can be seen: the key is the backend's own name for a setting — the identity this set holds, and
    /// the string a value is recorded under (<c>Settings.Remember</c>) — so a declaration rebuilt over a key
    /// this machine declares still describes a setting it HAS, and refusing it would be a false refusal. Only a
    /// key outside the set is an error (the test above).
    ///
    /// The difference is invisible on the production path, where every caller hands back the very declaration
    /// the model gave it, which is why it needs a test rather than a comment:
    ///
    /// Mutation that reddens it: match by instance instead (<c>_declaredSettings.Contains(option)</c>) — this
    /// test alone.</summary>
    [Fact]
    public void ADeclarationRebuiltOverADeclaredKey_IsStillSwitchable()
    {
        var f = new LaptopServiceFixture(declare: d => d.Declare("FnLock", new FakeFlagPort()));
        var rebuiltPort = new FakeFlagPort();
        var rebuilt = new FlagSetting { Key = "FnLock", Port = rebuiltPort };

        f.Service.ApplySetting(rebuilt, "1");

        Assert.Equal([true], rebuiltPort.SetCalls);                  // the rebuilt declaration's own transport ran
        Assert.Equal("1", f.Store.Settings.DeviceSettings["FnLock"]); // ...and the value is recorded under the key
    }

    /// <summary>The hand-off itself: the set a backend declared reaches the model through the service's
    /// constructor, which is the only route there is now. Pinned because a service that skipped the hand-off
    /// would still build, still save, and offer no hardware row at all — the failure would look like a machine
    /// with no settings rather than like a missing line. The fake backend declares before the fixture builds the
    /// service, as a vendor backend declares inside <c>InitVendor</c>.</summary>
    [Fact]
    public void TheDeclaredSetReachesTheModel_ThroughTheServicesConstructor()
    {
        var f = new LaptopServiceFixture(declare: d =>
            d.Declare("lcd_override", new FakeFlagPort(), readbackVerifiesWrite: false));

        Assert.Equal(["lcd_override"], f.Service.DeclaredSettings.Select(s => s.Key));
    }

    /// <summary>The switching logic, taken on the model directly: an apply goes through the option's OWN contract
    /// (so the write, and any refusal, are the option's — this layer adds no second channel), and remembering
    /// lands the value in the bag under the option's own key. The two are separate calls because the graph lock
    /// must not span the write; the row-level path that places the lock is pinned by
    /// <see cref="AnAppliedSetting_IsRecordedUnderItsOwnKey_AndSaved"/> and
    /// <see cref="TheHardwareWrite_HappensOutsideTheStateLock"/>.
    ///
    /// The model is CONSTRUCTED with the declaration it switches, which is what makes the switch legal at all —
    /// the refusal of an option outside that set is pinned by
    /// <see cref="AnUndeclaredSetting_IsRefusedBeforeTheTransportIsTouched"/>.</summary>
    [Fact]
    public void TheModelSwitchesThroughTheOptionsOwnContract_AndRemembersUnderItsOwnKey()
    {
        var port = new FakeFlagPort();
        var declared = new FlagSetting { Key = "lcd_override", Port = port };
        var settings = new Settings([declared]);

        settings.Apply(declared, FlagSetting.Value(true));
        settings.Remember(declared, FlagSetting.Value(true));

        Assert.Equal([true], port.SetCalls);                            // the option's own contract did the write
        Assert.Equal("1", settings.DeviceSettings["lcd_override"]);     // ...and the value is under its own key
    }

    // ---------------------------------------------------------------- both halves through a row

    /// <summary>The row's click reaches the hardware THROUGH the declared setting and is recorded, so the two
    /// halves are one path rather than two that happen to agree. Pinned because the cheap alternative — a row
    /// that writes straight to the transport — would leave the bag empty and the settings.json missing every
    /// value the user chose, with nothing in the UI to show for it.</summary>
    [Fact]
    public void ARowsClick_ReachesTheHardware_AndIsRecorded()
    {
        var fn = new FakeFlagPort();
        var h = new OptionsAssemblerHarness(declare: d => d.Declare("FnLock", fn));

        AssemblerRows.Toggle(h, "Fn lock").OnChange(true);

        Assert.Equal([true], fn.SetCalls);
        Assert.Equal("1", h.F.Store.Settings.DeviceSettings["FnLock"]);
        Assert.Empty(h.Posted);              // and a write that landed says nothing to the user
    }

    /// <summary>A transport whose write records the lock state it was called under. The SERVICE is attached after
    /// the fixture is built — the declaration this probe rides in has to exist before the service does, and the
    /// probe is only asked when a write happens — so what it reports is the lock state at WRITE time, which is
    /// the fact under test. <c>null!</c> rather than a default that answers <c>false</c>: an unwired probe must
    /// blow up, because <c>false</c> is precisely the value this test asserts.</summary>
    private sealed class LockProbe : IFlagPort
    {
        public LaptopService Service { get; set; } = null!;
        public string? LastError { get; private set; }
        public bool SetCalled { get; private set; }
        public bool HeldDuringSet { get; private set; }

        public bool Get() => false;

        public bool Set(bool on)
        {
            SetCalled = true;
            HeldDuringSet = Service.StateHeld;
            return true;
        }
    }
}
