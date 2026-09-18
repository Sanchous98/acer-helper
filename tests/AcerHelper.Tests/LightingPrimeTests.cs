using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// Wave 6, step 2 — the lighting section. One of its two construction-time reads is deferred and one is NOT,
/// and the whole point of these gates is that the difference is a decision rather than an oversight.
///
/// DEFERRED: the plain (non-RGB) backlight's level. Its constructor writes nothing to the device, so a
/// placeholder is provably invisible, and <c>SyncFromHardware</c> — wired to Fn-key changes and called once at
/// startup and once per language rebuild — is its prime.
///
/// NOT DEFERRED: an RGB zone's brightness. That value is also what the startup re-apply SENDS to the device,
/// so a placeholder would change what the hardware is told. The plan prescribed 0 there; 0 is the one value
/// that must not go in, because it would make a configured keyboard come up dark and never be re-applied (the
/// prime re-reads the slider, it does not write). The gate below pins the behaviour that would have broken.
/// </summary>
public class LightingPrimeTests
{
    // ---------------------------------------------------------------- not deferred: the RGB zone

    /// <summary>The construction read works exactly as it did before this wave, and the value it produces is
    /// what the startup re-apply sends — the fact that makes a placeholder unsafe here.
    ///
    /// THE THIRD ASSERTION WAS REPAIRED RATHER THAN TRANSLATED. It used to read "the stored value is not
    /// consulted while the read works" off <c>state.Brightness</c>, where <c>state</c> was the stored OBJECT the
    /// panel held; the state is now a VALUE the panel copies, so that line could no longer fail whatever the
    /// panel did — the sixth-of-six failure mode this suite keeps finding. What it was protecting is that the
    /// construction path does not PERSIST, and that is asserted against the door instead.
    ///
    /// MUTATIONS THAT REDDEN IT: sending the stored brightness instead of the read one (<c>written</c>); storing
    /// on the construction path (<c>Writes</c>).</summary>
    [Fact]
    public void AnRgbZonesBrightness_IsReadAtConstruction_AndIsWhatStartupWrites()
    {
        var written = new List<byte>();
        var mode = FakeLightZones.Of("Keyboard", new LightZoneState(true, 0, 60, 5, 1, 0xFF0000, []));

        var panel = Panel(mode, written, readBrightness: () => 30);

        Assert.Equal(30, panel.Brightness);   // the hardware value wins over the stored one...
        Assert.Equal([30], written);          // ...and it is exactly what the re-apply sends
        Assert.Empty(mode.Writes);            // the stored value is not rewritten while the read works
    }

    /// <summary>The placeholder rule, on the path where it is already the live behaviour: an unreadable zone
    /// falls back to the stored value. This is what the wave's "placeholder = the answer a failed read would
    /// have given" means for this row — and it is why 0 could never have been the answer here.
    ///
    /// MUTATION THAT REDDENS IT: taking the read's null as a zero.</summary>
    [Fact]
    public void AnRgbZonesBrightness_FallsBackToTheStoredValue_WhenTheReadReportsNothing()
    {
        var written = new List<byte>();
        var mode = FakeLightZones.Of("Keyboard", new LightZoneState(true, 0, 60, 5, 1, 0xFF0000, []));

        var panel = Panel(mode, written, readBrightness: () => null);

        Assert.Equal(60, panel.Brightness);
        Assert.Equal([60], written);
    }

    /// <summary>PINNED AS-IS, and the reason the read above was left alone rather than deferred. A reader that
    /// reports a SPURIOUS zero — the OPMODE profile flash zeroes the EC's brightness register while the keyboard
    /// is lit, which is the bug the spurious-zero guard documents — makes the panel write 0, so a configured
    /// keyboard comes up dark. That is today's behaviour on the one path the guard does not cover, and this wave
    /// preserves it verbatim: putting a placeholder in that write's place would be a device-visible change with
    /// no way to check it without the machine.
    ///
    /// MUTATION THAT REDDENS IT: running the spurious-zero guard on the construction path (nothing would be
    /// sent, or the stored 60 would be).</summary>
    [Fact]
    public void ASpuriousZeroAtConstruction_IsWrittenAsIs_BecauseThatIsTodaysBehaviour()
    {
        var written = new List<byte>();
        var mode = FakeLightZones.Of("Keyboard", new LightZoneState(true, 0, 60, 5, 1, 0xFF0000, []));

        var panel = Panel(mode, written, readBrightness: () => 0);

        Assert.Equal(0, panel.Brightness);
        Assert.Equal([0], written);           // the guard in AdoptBrightness does NOT run on the construction path
    }

    /// <summary>Nothing is re-applied while the user has never set this zone: a fresh install must not override
    /// whatever the firmware was showing. Unchanged by this wave, pinned because the tests above turn
    /// <c>Configured</c> on to reach the re-apply at all.
    ///
    /// MUTATION THAT REDDENS IT: applying whatever the read gave, <c>Configured</c> or not.</summary>
    [Fact]
    public void AnUnconfiguredZone_WritesNothingAtAll()
    {
        var written = new List<byte>();
        var mode = FakeLightZones.Of("Keyboard", new LightZoneState(false, 0, 60, 5, 1, 0xFF0000, []));

        _ = Panel(mode, written, readBrightness: () => 30);

        Assert.Empty(written);
    }

    // ---------------------------------------------------------------- deferred: the plain backlight

    /// <summary>The backlight's level is a placeholder, and it is safe because a backlight's constructor writes
    /// nothing: the value has nowhere to leak. `SyncFromHardware` then brings the real one off the UI thread.
    ///
    /// The port's own <c>Set</c> is the apply delegate here, which is the shape the app wires it in
    /// (<c>AppController.BuildUi</c> passes <c>lvl =&gt; _svc.SetKeyboardBrightness(lvl).ok</c> — the delegate
    /// IS the device write). With the old <c>_ =&gt; true</c> stand-in, <c>SetCalls</c> was a list nothing
    /// could ever add to, so "a prime never writes" was a claim no mutation could falsify.
    ///
    /// BOTH halves wait on the counted poster rather than on the clock, and the read count is taken only after
    /// the SECOND delivery: the follow-up read is queued behind whatever the prime queued, and the worker is
    /// FIFO, so a write the snap-back had wrongly queued has run by then. Asserting either straight after the
    /// correction races that worker — measured: this test stayed GREEN against a mutant that makes the
    /// snap-back write, on a loaded machine, while reddening on an idle one (the extra read-back had not
    /// landed yet). Same signal and same reason as
    /// <see cref="ABacklightsPrime_DoesNotWrite_EvenWhenItChangesTheSlider"/>.</summary>
    [Fact]
    public void ABacklightsLevel_IsAPlaceholder_ThePrimeReplaces()
    {
        var port = new FakeKeyboardBrightness { Level = 2 };
        var poster = new Eventually.Poster();

        var vm = new BacklightViewModel(port, l => port.Set(l), poster.Post);

        Assert.Equal(0, vm.Level);                       // placeholder: what a failed read would give
        Assert.Equal(0, port.GetCount);                  // ...and the build cost no read at all
        Assert.Equal(Loc.T("Off"), vm.LevelName);

        vm.SyncFromHardware();                           // the prime

        Assert.True(Eventually.Until(() => poster.Delivered >= 1), "the deferred read never reached the slider");
        Assert.Equal(2, vm.Level);

        // Ask for one more read and wait for it: by then the prime's own read, and anything it queued, are done.
        vm.SyncFromHardware();
        Assert.True(Eventually.Until(() => poster.Delivered >= 2), "the follow-up read never ran");

        Assert.Equal(2, port.GetCount);                  // the prime's read and the follow-up's — nothing else
        Assert.Empty(port.SetCalls);                     // a prime reads; it must never write
    }

    /// <summary>The prime's read really does move the slider (0 -&gt; 1, the level the hardware reports), and
    /// that must not make the slider's change look like a user edit and write the level straight back. The prime
    /// reads; it never writes. The port's <c>Set</c> is the apply delegate (see
    /// <see cref="ABacklightsLevel_IsAPlaceholder_ThePrimeReplaces"/>), so <c>SetCalls</c> is the app's real
    /// write path and not a list nothing can add to.
    ///
    /// BOTH halves wait for a signal, because neither is decided by the clock. The correction is one delivery
    /// through the counted poster: until it lands, the slider is still on its placeholder — which is what a
    /// fixed sleep got wrong, measured, on a busy machine (this test failed on
    /// <c>Assert.Equal(1, vm.Level)</c>, reading 0). And "the prime did not write" is a claim about a non-event:
    /// see the draining read below.</summary>
    [Fact]
    public void ABacklightsPrime_DoesNotWrite_EvenWhenItChangesTheSlider()
    {
        var port = new FakeKeyboardBrightness { Level = 1 };
        var poster = new Eventually.Poster();
        var vm = new BacklightViewModel(port, l => port.Set(l), poster.Post);

        vm.SyncFromHardware();

        // The signal: the worker reads, then posts, and the post is this delivery. Wait for it rather than for
        // 50 ms of wall clock — the read and its post are one unit apart, and that unit is the whole race.
        Assert.True(Eventually.Until(() => poster.Delivered >= 1), "the prime's correction was never delivered");
        Assert.Equal(1, vm.Level);
        Assert.Equal(Loc.T("Dim"), vm.LevelName);

        // The other half is a non-event, so it needs a signal of its own or it is decided by elapsed time. The
        // control's worker is FIFO, so a write the snap-back had wrongly queued would run BEFORE whatever is
        // queued next: ask the worker for one more read and wait for that read. By the time it lands, anything
        // the prime queued has already run, and the answer is decided rather than hoped for.
        vm.SyncFromHardware();
        Assert.True(Eventually.Until(() => port.GetCount >= 2), "the follow-up read never ran");

        Assert.Empty(port.SetCalls);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A one-zone static panel over the mode's stored state; <paramref name="written"/> collects every
    /// brightness the panel sends to the device, which is the whole subject of this file. Edits go through the
    /// real store the section uses (<c>LightingViewModel.Store</c>), so a test can see whether the panel STORED
    /// anything as well as whether it applied — and so "the construction path stores nothing" is a claim that
    /// can fail.</summary>
    private static LightViewModel Panel(FakeLightZones mode, List<byte> written, Func<int?>? readBrightness)
        => new("Keyboard", [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
               zones: 1,
               applyAll: (_, _, brightness, _, _) => written.Add(brightness),
               applyZone: null, mode["Keyboard"], s => ApplyLightZone.Run("Keyboard", s, mode), readBrightness);
}
