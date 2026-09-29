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
/// NOT DEFERRED, WHERE IT IS TAKEN AT ALL: an RGB zone's brightness. That value is also what the startup
/// re-apply SENDS to the device, so a placeholder would change what the hardware is told. The plan prescribed 0
/// there; 0 is the one value that must not go in, because it would make a configured keyboard come up dark and
/// never be re-applied (the prime re-reads the slider, it does not write). The gate below pins the behaviour that
/// would have broken.
///
/// AND NOT TAKEN FOR A CONFIGURED ZONE AT ALL (2026-09-29): a zone the user has already set takes its
/// brightness from STORAGE — no read at construction, and none of the read-wins behaviour this file used to
/// pin. A read is not an author of intent (docs/state-and-events.md); the register lies after a profile flash
/// (docs/lighting-an18-61.md), and letting it win is the owner's «настройки яркости не пишутся» / «при смене
/// профиля сохранённые настройки не применяются правильно». The read survives for an UNCONFIGURED zone (a
/// fresh install) and on the Fn-key event path (<c>AdoptFromHardware</c>), where its guards still apply.
/// </summary>
public class LightingPrimeTests
{
    // ---------------------------------------------------------------- not deferred: the RGB zone

    /// <summary>A CONFIGURED zone's stored brightness is what the panel shows AND what the startup re-apply
    /// sends — the hardware read does NOT override it, even when it answers with a different non-zero value.
    /// This is the owner's bug ("настройки яркости не пишутся" / a stored 100 shown as 0 when the register lies):
    /// the old shape read the wire first and let it win, so a configured zone's slider and the value the device
    /// was told came from the register rather than from the user's saved intent (docs/state-and-events.md: a read
    /// is not an author of intent except on the event path, <c>AdoptBrightness</c>).
    ///
    /// THE READ IS NOT EVEN TAKEN for a configured zone, so the assertion counts it: fewer EC round-trips at
    /// <c>BuildUi</c>, and — the structural half — nothing can be believed off the wire for a zone the user owns.
    ///
    /// MUTATIONS THAT REDDEN IT: restoring the unconditional read
    /// (<c>readBrightness?.Invoke() ?? state.Brightness</c>), or the read-wins branch this replaced for a
    /// non-zero stored value.</summary>
    [Fact]
    public void AConfiguredZonesStoredBrightness_WinsOverTheRead_AndIsWhatStartupWrites()
    {
        var written = new List<byte>();
        var mode = FakeLightZones.Of("Keyboard", new LightZoneState(true, 0, 60, 5, 1, 0xFF0000, []));
        var reads = 0;

        var panel = Panel(mode, written, readBrightness: () => { reads++; return 30; });

        Assert.Equal(60, panel.Brightness);   // the stored intent, not the read's 30...
        Assert.Equal([60], written);          // ...and it is exactly what the re-apply sends
        Assert.Equal(0, reads);               // a configured zone is not read at all
        Assert.Empty(mode.Writes);            // the stored value is not rewritten while the read would work
    }

    /// <summary>An UNCONFIGURED zone is the one shape the construction read still seeds: there is no stored
    /// intent to apply (a fresh install), so the firmware's own level is the honest thing to show. A null read
    /// falls back to the stored value (what a failed read would have given). This is the path that keeps
    /// out-of-band discovery alive without letting a read author intent for a zone the user owns.
    ///
    /// MUTATION THAT REDDENS IT: taking the read's null as a zero, or skipping the read for an unconfigured
    /// zone too (the slider would sit at the stored default rather than the firmware's level).</summary>
    [Fact]
    public void AnUnconfiguredZonesBrightness_IsSeededFromTheRead_FallingBackToTheStoredValue()
    {
        var written = new List<byte>();
        var mode = FakeLightZones.Of("Keyboard", new LightZoneState(false, 0, 60, 5, 1, 0xFF0000, []));

        var seeded = Panel(mode, written, readBrightness: () => 30);
        Assert.Equal(30, seeded.Brightness);   // the firmware's level seeds a fresh install
        Assert.Empty(written);                 // ...and it applies nothing (unconfigured)

        var fallback = Panel(mode, written, readBrightness: () => null);
        Assert.Equal(60, fallback.Brightness); // an unreadable zone falls back to the stored value
        Assert.Empty(written);
    }

    /// <summary>The specific lie the owner hit: a configured zone whose register reads a SPURIOUS zero (the
    /// OPMODE flash zeroes it while the keyboard is lit) keeps the stored brightness on the slider and on the
    /// wire. The old construction path let that 0 win, so a keyboard stored at 60 came up DARK and the decrease
    /// control had nowhere to go. The read is not consulted for a configured zone at all, so the lie cannot land.
    ///
    /// MUTATION THAT REDDENS IT: restoring the read-wins branch (the panel shows 0 and writes 0), or running only
    /// the <c>AdoptBrightness</c> guard here rather than making the stored value authoritative.</summary>
    [Fact]
    public void ASpuriousZeroAtConstruction_DoesNotOverrideTheStoredBrightness()
    {
        var written = new List<byte>();
        var mode = FakeLightZones.Of("Keyboard", new LightZoneState(true, 0, 60, 5, 1, 0xFF0000, []));

        var panel = Panel(mode, written, readBrightness: () => 0);

        Assert.Equal(60, panel.Brightness);   // stored wins over the lying register...
        Assert.Equal([60], written);          // ...and the device is told the user's value, never the spurious 0
    }

    /// <summary>A configured 0 is the app's stored intent and is applied as 0 — a stale/non-zero read does NOT
    /// resurrect the light, exactly as a configured non-zero is not overridden by a lying 0. This is the same
    /// rule as the siblings above, on the value that started it (switching to a profile stored at 0 left the
    /// backlight on because the construction read won over the stored value, docs/lighting-an18-61.md). The
    /// app's settings are the source of truth (docs/state-and-events.md), so a configured zero is authoritative.
    ///
    /// MUTATION THAT REDDENS IT: restoring the unconditional read
    /// (<c>readBrightness?.Invoke() ?? state.Brightness</c>).</summary>
    [Fact]
    public void AConfiguredZero_IsAppliedAtConstruction_NotOverriddenByAStaleRead()
    {
        var written = new List<byte>();
        var mode = FakeLightZones.Of("Keyboard", new LightZoneState(true, 0, 0, 5, 1, 0xFF0000, []));

        var panel = Panel(mode, written, readBrightness: () => 100);   // the register still holds the old 100

        Assert.Equal(0, panel.Brightness);    // the stored 0 stands...
        Assert.Equal([0], written);           // ...and it is what the device is told
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
    /// (<c>AppController.BuildUi</c> passes <c>lvl =&gt; _actions.KeyboardBrightness.Run(lvl).ok</c> — the delegate
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
        Assert.Equal(Loc.T("level.off"), vm.LevelName);

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
        Assert.Equal(Loc.T("level.dim"), vm.LevelName);

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
               applyZone: null, mode["Keyboard"], s => new ApplyLightZone(mode).Run("Keyboard", s), readBrightness);
}
