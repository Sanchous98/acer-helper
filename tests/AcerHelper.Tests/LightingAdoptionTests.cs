using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// Wave 1 of the domain refactor: <b>discovery is not acceptance</b> (docs/state-and-events.md). A read may
/// notice that something changed outside the app, but only an EVENT changes state — and in a moment of doubt the
/// value is RE-APPLIED, not read.
///
/// THE BUG THIS PINS. Switching to a performance mode whose stored keyboard brightness is 0 left the hardware at
/// the previous mode's brightness, and switching back applied neither. Mechanism: the refresh pass read the
/// brightness register while our own write was still in flight, so it read the value the PREVIOUS profile had
/// left there, took the wire's lag for the user's choice, and persisted it — the mode being switched to lost its
/// stored brightness. The fix is not to synchronise the read (there is nothing to wait for: the write is queued,
/// and "the writer finished" is not "the EC applied it") but to take acceptance out of the read.
///
/// WHAT IS ASSERTED, at the two levels the app actually calls: the composite paths <see cref="LightingViewModel.Prime"/>
/// (startup, and the live language rebuild) and <see cref="LightingViewModel.Reapply"/> (the doubt moment — the
/// Lighting drawer opening), and the event path <see cref="LightViewModel.AdoptFromHardware"/> (a special key,
/// where a read IS an author of intent and IS persisted). A read port that keeps reporting the old value after we
/// wrote a new one cannot move the slider, the stored value or the device unless it came through the event path,
/// and the event path is the only place the guards are allowed to say no.
///
/// THE CONTROL IN EVERY CASE. "Nothing changed" passes vacuously against a panel that never wrote, or a port that
/// was never asked, so every negative assertion is paired with proof that the write really went out, that the
/// register really would have reported the stale number, and that the event really was delivered. Delivery is
/// counted through the injectable poster (see <see cref="Eventually.Poster"/>) — the same seam the option rows
/// take, and for the same measured reason: the real dispatcher is thread-affine and a bare xUnit process creates
/// it from whichever thread first touches it, so a headless test cannot pump it (see <see cref="Eventually"/>).
///
/// MUTATION-VERIFIED — each of these was run against this file, and each reddened the test named. The mutations
/// are the six ways the rule can be broken, not six spellings of one:
/// <list type="bullet">
/// <item><b>read and adopt again on the paths the app calls</b> — the two composite methods put back to the old
/// <c>Sync()</c> behaviour (<c>foreach panel: AdoptFromHardware()</c> before the re-apply). This is the closest
/// reachable stand-in for the plan's "restore the poller", and it reddens
/// <c>AStaleRead_ChangesNothing_OnEitherPathTheAppCalls</c> — the mode's stored brightness comes back as 100, the
/// register's stale value, which is the reported bug exactly;</item>
/// <item><b>drop the persistence</b> (<c>_state.Brightness = value; _save();</c> removed) — reddens
/// <c>AnOutOfBandInput_IsAdopted_AndStored</c> and <c>AnAdoption_NeverClaimsTheZoneAsConfigured</c>;</item>
/// <item><b>drop the spurious-zero guard</b> — reddens <c>ASpuriousZeroRead_IsIgnored_AndNotStored</c>;</item>
/// <item><b>drop the user-edit guard</b> (<c>_debounce.IsEnabled</c>) — reddens
/// <c>AUserEditInFlight_BeatsTheEvent</c>, which also proves the guard's precondition is real without a
/// dispatcher: the debounce IS pending after a slider move, it just never ticks headless;</item>
/// <item><b>let the adoption write to the device</b> (<c>ApplyNow()</c> added after the save) — reddens the two
/// adoption tests: the key already moved the device, and re-writing it is not this path's job;</item>
/// <item><b>let the adoption claim the zone</b> (<c>_state.Configured = true</c> added) — reddens
/// <c>AnAdoption_NeverClaimsTheZoneAsConfigured</c>.</item>
/// </list>
///
/// THE GAP, NAMED. The plan's literal mutation — restore the poller at <c>AppController.cs:617</c> — is NOT
/// reachable by this suite: <c>AppController</c> needs a live desktop lifetime and a refresh loop, which is the
/// limit <c>ReconcileScheduleTests</c> and <c>CpuPrimeTests</c> already record. So what is unproven here is that
/// the PASS no longer polls; what is proven is that the two paths that pass fed (<c>Prime</c> and <c>Reapply</c>)
/// and the event path behave as the rule requires. The call-site change itself — the poller's removal from
/// <c>UiPass</c> — is covered by nothing in this suite.
///
/// One consequence of the wave is outside this file and is named rather than left implicit: opening the Lighting
/// drawer on a device whose only control is the PLAIN backlight now does nothing at all (the drawer path re-applies
/// RGB panels, and a backlight has no stored value to push), where it used to re-read the level. That slider is
/// primed at startup and on a rebuild, and re-read on an input event — both pinned in <c>LightingPrimeTests</c> —
/// but the drawer command itself is not exercised anywhere, here or there.
/// </summary>
public class LightingAdoptionTests
{
    private const string ZoneName = "Keyboard";   // the settings key and the panel title

    // ---------------------------------------------------------------- a read that is not an event

    /// <summary>
    /// THE ACCEPTANCE CRITERION OF THE WAVE. The controllable read port answers with the value the PREVIOUS mode
    /// left in the register — the wire's lag, frozen, exactly as it is when our write of 0 has been sent but not
    /// applied. Both composite paths that used to read (the prime at startup/rebuild, and the drawer opening) must
    /// leave everything where it is: the slider, the mode's stored brightness, and the device, which is
    /// re-applied with OUR value rather than questioned.
    /// </summary>
    [Fact]
    public void AStaleRead_ChangesNothing_OnEitherPathTheAppCalls()
    {
        var register = new StaleRegister(100);   // the register still holds the previous mode's brightness
        var written = new List<byte>();
        var saves = 0;
        var poster = new Eventually.Poster();    // the same seam AppController builds the section with (default:
                                                 // the real dispatcher) — without it a read-and-adopt restored on
                                                 // these paths would be invisible here, which is the whole mutation
        var zone = Zone(register, written);
        var modeA = Mode(100);
        var modeB = Mode(0);
        var vm = Lighting(zone, modeA, () => saves++, poster.Post);

        // Controls: the construction read is the value the startup re-apply SENDS (pinned in LightingPrimeTests),
        // and it is the only read so far.
        Assert.Equal([100], written);
        Assert.Equal(1, register.Reads);

        vm.Reload(modeB);                        // the reported bug: the user switches to a mode stored at 0
        Assert.Equal([100, 0], written);         // Control: our 0 really did go out to the device
        Assert.Equal(1, register.Reads);         // ...and the switch itself asks the wire nothing

        vm.Prime();                              // startup / live language rebuild
        vm.Reapply();                            // the doubt moment: the Lighting drawer opens
        Settle();                                // a read-and-adopt restored on these paths arrives asynchronously

        Assert.Equal(0, vm.Panels[0].Brightness);       // the slider still shows OUR value
        Assert.Equal(0, modeB[ZoneName].Brightness);    // the lag was NOT persisted as the user's choice
        Assert.Equal(0, saves);                         // nothing here was an intent, so nothing was saved
        Assert.Equal(0, poster.Delivered);              // ...and no read was even delivered to the UI thread
        Assert.Equal([100, 0, 0], written);             // the doubt moment re-applied OUR value, nothing else
        Assert.Equal(1, register.Reads);                // neither path asked the device: still only the construction read

        // Control: the register really is stale — asked directly, it reports the previous mode's 100.
        Assert.Equal(100, zone.ReadBrightness());
    }

    // ---------------------------------------------------------------- the event path: a read that IS an intent

    /// <summary>
    /// The one line of the rule where a read becomes intent: an out-of-band input (the Fn brightness key) is an
    /// author exactly like the slider is, it just delivers its value through a read instead of through the UI. The
    /// read is therefore adopted AND PERSISTED — storing is the half that used to be missing, which is why the app
    /// and the hardware disagreed until the user happened to touch the slider again. The value is NOT written back
    /// to the device: the key already did that.
    /// </summary>
    [Fact]
    public void AnOutOfBandInput_IsAdopted_AndStored()
    {
        var register = new StaleRegister(30);
        var written = new List<byte>();
        var saves = 0;
        var state = new LightSettings { Brightness = 60, Configured = true };
        var poster = new Eventually.Poster();
        var panel = Panel(state, written, register, () => saves++, poster.Post);

        // Controls: the panel is built from the hardware read rather than the stored 60, and that reading is what
        // the startup re-apply sends.
        Assert.Equal(30, panel.Brightness);
        Assert.Equal([30], written);

        register.Value = 20;                     // the Fn key dims it out-of-band; nothing told the app
        panel.AdoptFromHardware();

        Assert.True(Eventually.Until(() => poster.Delivered >= 1), "the event was never delivered");
        Assert.Equal(20, panel.Brightness);
        Assert.Equal(20, state.Brightness);      // stored — the half that used to be dropped
        Assert.Equal(1, saves);
        Assert.Equal([30], written);             // an adoption reads; it never writes
    }

    /// <summary>
    /// An adoption writes ONE field and claims nothing else. <c>Configured</c> stays off, so a special key that
    /// moves the brightness of a zone the user never configured (a fresh install) cannot hand us that zone and
    /// make the app start driving what it does not own — the reason the adoption path writes only
    /// <c>_state.Brightness</c> and leaves the rest of the slider to <c>SaveState()</c> on the user-edit path.
    /// </summary>
    [Fact]
    public void AnAdoption_NeverClaimsTheZoneAsConfigured()
    {
        var register = new StaleRegister(10);
        var written = new List<byte>();
        var saves = 0;
        var state = new LightSettings { Brightness = 10 };   // Configured stays off
        var poster = new Eventually.Poster();
        var panel = Panel(state, written, register, () => saves++, poster.Post);

        Assert.Empty(written);                   // Control: an unconfigured zone writes nothing at all

        register.Value = 40;
        panel.AdoptFromHardware();
        Assert.True(Eventually.Until(() => poster.Delivered >= 1), "the event was never delivered");

        Assert.Equal(40, panel.Brightness);
        Assert.Equal(40, state.Brightness);
        Assert.False(state.Configured);
        Assert.Empty(written);                   // ...and it still writes nothing
    }

    // ---------------------------------------------------------------- the guards the event path keeps

    /// <summary>
    /// The spurious zero, kept exactly as it was: the OPMODE profile flash zeroes the EC's brightness register
    /// while the keyboard stays lit, so a 0 read over a non-zero brightness is the register lying, not the user
    /// dimming. It is ignored, and — the half that matters here — the lie is not STORED either. The delivery count
    /// is the control: the read happened (the register was really asked, and answered 0) and was refused.
    /// </summary>
    [Fact]
    public void ASpuriousZeroRead_IsIgnored_AndNotStored()
    {
        var register = new StaleRegister(30);
        var written = new List<byte>();
        var saves = 0;
        var state = new LightSettings { Brightness = 60, Configured = true };
        var poster = new Eventually.Poster();
        var panel = Panel(state, written, register, () => saves++, poster.Post);

        register.Value = 0;                      // the profile flash zeroes the register under a lit keyboard
        panel.AdoptFromHardware();

        Assert.True(Eventually.Until(() => poster.Delivered >= 1), "the event was never delivered");
        Assert.Equal(2, register.Reads);         // Control: the register WAS asked, and answered 0
        Assert.Equal(30, panel.Brightness);      // the slider is not snapped to 0 (where it would stick)
        Assert.Equal(60, state.Brightness);      // ...and the stored value is not overwritten with the lie
        Assert.Equal(0, saves);
    }

    /// <summary>
    /// A user edit in flight wins over the event: the read raced ahead of the apply we have not sent yet, so what
    /// it carries is the value the user is in the middle of replacing. Accepting it would yank the slider back
    /// mid-drag, and the pending debounce tick would then apply and persist the stale value over the user's
    /// choice. The differential control for this one is
    /// <see cref="AnOutOfBandInput_IsAdopted_AndStored"/>: the same shape WITHOUT the edit in flight does land.
    /// </summary>
    [Fact]
    public void AUserEditInFlight_BeatsTheEvent()
    {
        var register = new StaleRegister(30);
        var written = new List<byte>();
        var saves = 0;
        var state = new LightSettings { Brightness = 60, Configured = true };
        var poster = new Eventually.Poster();
        var panel = Panel(state, written, register, () => saves++, poster.Post);

        panel.Brightness = 80;                   // the user drags the slider: the 120 ms debounce is now pending
        register.Value = 50;                     // the hardware read lags behind that edit
        panel.AdoptFromHardware();

        Assert.True(Eventually.Until(() => poster.Delivered >= 1), "the event was never delivered");
        Assert.Equal(80, panel.Brightness);      // the user's value stands
        Assert.Equal(60, state.Brightness);      // ...and nothing was persisted over it
        Assert.Equal(0, saves);
    }

    // ---------------------------------------------------------------- the follow switch

    /// <summary>
    /// The follow-flip handler (<c>LightingViewModel.OnFollowsProfileChanged</c>), which nothing exercised
    /// before this test: the delegate the <c>Lighting</c> helper below passes for it is <c>_ =&gt; { }</c>, so
    /// dropping the persist, or transposing the two branches that add and remove a panel, was invisible to the
    /// whole suite.
    ///
    /// WHAT THE HANDLER OWES, and both halves are asserted because they fail independently. Flipping the switch
    /// OFF takes the follow-capable zone over from the firmware: a panel is built for it — from the current
    /// mode's stored state, through the same <c>BuildPanel</c> the constructor uses — so the user can drive it.
    /// Flipping it back ON hands the zone back, and the panel goes away so the app stops sending it anything
    /// while the firmware's per-profile palette shows. Either way the choice is PERSISTED through the
    /// follow-save delegate, which is the half that had no witness.
    ///
    /// MUTATION-VERIFIED, each named: dropping <c>_saveFollowsProfile(value)</c> reddens the two flip
    /// assertions; transposing the branches inside <c>OnFollowsProfileChanged</c> (so ON builds a panel and OFF
    /// removes it) reddens the two panel assertions. The zone is arranged as a lightbar is
    /// (<c>canFollowProfile: true</c>), and the switch is only offered at all when such a zone exists
    /// (<c>ShowFollowsProfile</c>) — asserted first, so the test cannot pass by having nothing to flip.
    /// </summary>
    [Fact]
    public void FlippingTheFollowSwitch_HandsTheZoneBackAndForth_AndPersistsTheChoice()
    {
        var flips = new List<bool>();
        var lights = new Dictionary<string, LightSettings>();
        var vm = new LightingViewModel(new RgbDevice(new FakeRgbController { Zones = [FollowZone()] }),
                                       lights, EnsureZone, save: () => { }, followsProfile: true,
                                       saveFollowsProfile: flips.Add);

        Assert.True(vm.ShowFollowsProfile);
        Assert.Empty(vm.Panels);              // Control: while following, the firmware owns the zone — no panel

        vm.FollowsProfile = false;            // the user takes it over
        Assert.Equal("Lightbar", Assert.Single(vm.Panels).Title);   // a panel for THAT zone, and only it
        Assert.Equal([false], flips);

        vm.FollowsProfile = true;             // ...and hands it back to the firmware
        Assert.Empty(vm.Panels);
        Assert.Equal([false, true], flips);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A lightbar: the one zone shape the follow switch exists for (<c>canFollowProfile: true</c>),
    /// with the single "Static" effect the other helpers use and no brightness read — this test never reads it,
    /// and the zone writes nothing on its own while the mode it is bound to has no stored state to apply.</summary>
    private static RgbZone FollowZone()
        => new("Lightbar", 1, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
               (_, _, _, _, _) => true,
               canFollowProfile: true);

    /// <summary>A per-mode zone store: the current mode's per-zone state, keyed by the panel's title. One dict per
    /// mode is exactly what <c>LaptopService.LightsForCurrentMode</c> hands the view-model, and
    /// <see cref="LightingViewModel.Reload"/> is the switch between them.</summary>
    private static Dictionary<string, LightSettings> Mode(int brightness)
        => new() { [ZoneName] = new LightSettings { Brightness = brightness, Configured = true } };

    /// <summary>The composite: one RGB zone over the given mode, whose brightness read is the controllable
    /// register. Built the way <c>AppController.BuildUi</c> builds it — no poster is passed for the panels, so
    /// nothing here can reach the event path (that is the point of the first test).</summary>
    private static LightingViewModel Lighting(RgbZone zone, Dictionary<string, LightSettings> lights, Action save,
                                              Action<Action> post)
        => new(new RgbDevice(new FakeRgbController { Zones = [zone] }),
               lights, EnsureZone, save, followsProfile: false, _ => { }, post: post);

    /// <summary>Give an asynchronous read-and-adopt — the shape a restored poll has — its chance to land before a
    /// NEGATIVE assertion is made. The event-path tests below need no window: their adoption arrives through a
    /// counted poster, which is an event.
    ///
    /// THIS ONE HAS NO SIGNAL TO WAIT FOR, AND THAT IS A GAP RATHER THAN A STYLE. The adoption is a
    /// <c>Task.Run</c> with no join handle, and the only thing it ever produces is a post — which the CORRECT
    /// code must never make, so there is nothing to wait for that is not also the failure. Unlike the prime in
    /// <c>LightingPrimeTests.ABacklightsPrime_DoesNotWrite_EvenWhenItChangesTheSlider</c> (which is settled
    /// through the counted poster) there is no serial worker here to ask for a draining read either. So this
    /// window is bounded by time and can only ever be too short: it cannot make the test fail, because correct
    /// code leaves nothing in flight, but a mutation that DOES leave a read in flight can slip past it on a busy
    /// machine. Closing it needs a completion signal the production code does not have — reported, not papered
    /// over with a longer wait.</summary>
    private static void Settle() => Thread.Sleep(50);

    /// <summary>Same contract as <c>LaptopService.EnsureLightZone</c>: the mode's per-zone entry, created on first
    /// sight under the service's lock.</summary>
    private static LightSettings EnsureZone(Dictionary<string, LightSettings> lights, string name)
    {
        if (!lights.TryGetValue(name, out var s)) lights[name] = s = new LightSettings();
        return s;
    }

    /// <summary>One static zone whose apply records every brightness it is sent
    /// (<paramref name="written"/>), and whose brightness read is the register.</summary>
    private static RgbZone Zone(StaleRegister register, List<byte> written)
        => new(ZoneName, 1, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
               (_, brightness, _, _, _) => { written.Add(brightness); return true; },
               readBrightness: register.Read);

    /// <summary>One panel over the given state, built the way <c>LightingViewModel.BuildPanel</c> builds it —
    /// with the poster injected so the event path can be observed without a dispatcher (see the class docs).</summary>
    private static LightViewModel Panel(LightSettings state, List<byte> written, StaleRegister register,
                                        Action save, Action<Action> post)
        => new(ZoneName, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
               zones: 1,
               applyAll: (_, _, brightness, _, _) => written.Add(brightness),
               applyZone: null, state, save, register.Read, post);

    /// <summary>A controllable brightness register that LIES: nothing we write changes what a read reports, which
    /// is the wire's lag after a profile switch — the write is sent, the EC has not applied it, and the register
    /// still holds the previous value. <see cref="Reads"/> is counted so "this path never asked the device" is an
    /// assertion rather than a claim about the source.</summary>
    private sealed class StaleRegister(int value)
    {
        /// <summary>What a read reports. A test moves it to model the Fn key (a real out-of-band change) or the
        /// profile flash (a spurious zero).</summary>
        public int Value { get; set; } = value;

        /// <summary>How many times the register was read. A path that must not read is checked against this.</summary>
        public int Reads { get; private set; }

        public int? Read()
        {
            Reads++;
            return Value;
        }
    }
}
