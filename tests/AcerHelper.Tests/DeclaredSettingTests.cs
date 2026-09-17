using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The declared-settings contract, and the two halves of it (docs/domain-refactoring-plan.md §5, waves 5 + 9).
///
/// WHAT IT IS. A backend declares which settings this machine has, each with its own opaque key and its own
/// shape — a flag or a pick-one-of-N choice (Domain/Settings.cs). Applying one either works or THROWS
/// <see cref="SettingNotAppliedException"/>, and the exception carries information rather than a sentence: the
/// message the user reads is composed by the UI from the row's own label, which is why the domain can hold no
/// setting names at all. What the value MEANS once applied is the row's business
/// (<c>OptionsAssemblerTests</c>), and what survives a restart is the settings bag.
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

    /// <summary>A transport whose write THROWS is a refusal, not a crash, and the reason still crosses: the
    /// declaration catches it and reads the port's <c>LastError</c>. This is the rule
    /// <c>LaptopService.Attempt</c> stated for the tuple channel, restated on the side of the exception so the
    /// same writes are reported with the same words. The assembler-side twin of this test asserts the message
    /// the user ends up reading.</summary>
    [Fact]
    public void ATransportThatThrows_IsARefusal_NotACrash()
    {
        var setting = new FlagSetting
        {
            Key = "lcd_override",
            Port = new FakeFlagPort { ThrowOnSet = true, LastError = "transport gone" },
        };

        var ex = Assert.Throws<SettingNotAppliedException>(() => setting.Apply("1"));

        Assert.Equal("transport gone", ex.Reason);
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
    /// exactly what they saw before.</summary>
    [Fact]
    public void AnAppliedSetting_IsRecordedUnderItsOwnKey_AndSaved()
    {
        var f = new LaptopServiceFixture();
        var port = new FakeFlagPort();
        var setting = f.Device.Declare("lcd_override", port, readbackVerifiesWrite: false);

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
        var f = new LaptopServiceFixture();
        var setting = f.Device.Declare("stop_timeout", new FakeChoicePort("5s", "30s", "1h"));

        f.Service.ApplySetting(setting, "30s");

        Assert.Equal("30s", f.Store.Settings.DeviceSettings["stop_timeout"]);
    }

    /// <summary>A REFUSED apply records nothing and saves nothing: the bag holds what the hardware took, so a
    /// value the firmware rejected must not be remembered as if it were in force. Pinned as one test with two
    /// assertions rather than two tests, because "nothing" is the same fact about the same call.</summary>
    [Fact]
    public void ARefusedSetting_RecordsNothing_AndSavesNothing()
    {
        var f = new LaptopServiceFixture();
        var setting = f.Device.Declare("lcd_override", new FakeFlagPort { SetResult = false, LastError = "EC said no" },
                                       readbackVerifiesWrite: false);

        Assert.Throws<SettingNotAppliedException>(() => f.Service.ApplySetting(setting, "1"));

        Assert.Empty(f.Store.Settings.DeviceSettings);
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>The hardware write runs OUTSIDE <c>_state</c> — docs/domain-refactoring-plan.md §4 forbids
    /// holding the graph lock across a blocking hardware operation, and a setting is an EC/WMI write. A fake
    /// observes the CALL and never the lock around it, so <see cref="LaptopService.StateHeld"/> is the only way
    /// to assert this at all; without it the invariant would be untestable on this path.</summary>
    [Fact]
    public void TheHardwareWrite_HappensOutsideTheStateLock()
    {
        var f = new LaptopServiceFixture();
        var probe = new LockProbe(f.Service);
        var setting = f.Device.Declare("lcd_override", probe, readbackVerifiesWrite: false);

        f.Service.ApplySetting(setting, "1");

        Assert.True(probe.SetCalled);        // non-vacuity: the probe really was written through
        Assert.False(probe.HeldDuringSet);   // the lock never spanned that write
    }

    // ---------------------------------------------------------------- both halves through a row

    /// <summary>The row's click reaches the hardware THROUGH the declared setting and is recorded, so the two
    /// halves are one path rather than two that happen to agree. Pinned because the cheap alternative — a row
    /// that writes straight to the transport — would leave the bag empty and the settings.json missing every
    /// value the user chose, with nothing in the UI to show for it.</summary>
    [Fact]
    public void ARowsClick_ReachesTheHardware_AndIsRecorded()
    {
        var h = new OptionsAssemblerHarness();
        var fn = new FakeFlagPort();
        h.F.Device.Declare("FnLock", fn);

        AssemblerRows.Toggle(h, "Fn lock").OnChange(true);

        Assert.Equal([true], fn.SetCalls);
        Assert.Equal("1", h.F.Store.Settings.DeviceSettings["FnLock"]);
        Assert.Empty(h.Posted);              // and a write that landed says nothing to the user
    }

    /// <summary>A transport whose write records the lock state it was called under.</summary>
    private sealed class LockProbe(LaptopService svc) : IFlagPort
    {
        public string? LastError { get; private set; }
        public bool SetCalled { get; private set; }
        public bool HeldDuringSet { get; private set; }

        public bool Get() => false;

        public bool Set(bool on)
        {
            SetCalled = true;
            HeldDuringSet = svc.StateHeld;
            return true;
        }
    }
}
