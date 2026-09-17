using AcerHelper.Domain;
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
    /// what the startup re-apply sends — the fact that makes a placeholder unsafe here.</summary>
    [Fact]
    public void AnRgbZonesBrightness_IsReadAtConstruction_AndIsWhatStartupWrites()
    {
        var written = new List<byte>();
        var state = new LightSettings { Brightness = 60, Configured = true };

        var panel = Panel(state, written, readBrightness: () => 30);

        Assert.Equal(30, panel.Brightness);   // the hardware value wins over the stored one...
        Assert.Equal([30], written);          // ...and it is exactly what the re-apply sends
        Assert.Equal(60, state.Brightness);   // the stored value is not consulted while the read works
    }

    /// <summary>The placeholder rule, on the path where it is already the live behaviour: an unreadable zone
    /// falls back to the stored value. This is what the wave's "placeholder = the answer a failed read would
    /// have given" means for this row — and it is why 0 could never have been the answer here.</summary>
    [Fact]
    public void AnRgbZonesBrightness_FallsBackToTheStoredValue_WhenTheReadReportsNothing()
    {
        var written = new List<byte>();
        var state = new LightSettings { Brightness = 60, Configured = true };

        var panel = Panel(state, written, readBrightness: () => null);

        Assert.Equal(60, panel.Brightness);
        Assert.Equal([60], written);
    }

    /// <summary>PINNED AS-IS, and the reason the read above was left alone rather than deferred. A reader that
    /// reports a SPURIOUS zero — the OPMODE profile flash zeroes the EC's brightness register while the keyboard
    /// is lit, which is the bug the spurious-zero guard documents — makes the panel write 0, so a configured
    /// keyboard comes up dark. That is today's behaviour on the one path the guard does not cover, and this wave
    /// preserves it verbatim: putting a placeholder in that write's place would be a device-visible change with
    /// no way to check it without the machine.</summary>
    [Fact]
    public void ASpuriousZeroAtConstruction_IsWrittenAsIs_BecauseThatIsTodaysBehaviour()
    {
        var written = new List<byte>();
        var state = new LightSettings { Brightness = 60, Configured = true };

        var panel = Panel(state, written, readBrightness: () => 0);

        Assert.Equal(0, panel.Brightness);
        Assert.Equal([0], written);           // the guard in AdoptBrightness does NOT run on the construction path
    }

    /// <summary>Nothing is re-applied while the user has never set this zone: a fresh install must not override
    /// whatever the firmware was showing. Unchanged by this wave, pinned because the tests above turn
    /// <c>Configured</c> on to reach the re-apply at all.</summary>
    [Fact]
    public void AnUnconfiguredZone_WritesNothingAtAll()
    {
        var written = new List<byte>();
        var state = new LightSettings { Brightness = 60 };   // Configured stays false

        _ = Panel(state, written, readBrightness: () => 30);

        Assert.Empty(written);
    }

    // ---------------------------------------------------------------- deferred: the plain backlight

    /// <summary>The backlight's level is a placeholder, and it is safe because a backlight's constructor writes
    /// nothing: the value has nowhere to leak. `SyncFromHardware` then brings the real one off the UI thread.</summary>
    [Fact]
    public void ABacklightsLevel_IsAPlaceholder_ThePrimeReplaces()
    {
        var port = new FakeKeyboardBrightness { Level = 2 };

        var vm = new BacklightViewModel(port, _ => true, Eventually.Sync);

        Assert.Equal(0, vm.Level);                       // placeholder: what a failed read would give
        Assert.Equal(0, port.GetCount);                  // ...and the build cost no read at all
        Assert.Equal(Loc.T("Off"), vm.LevelName);

        vm.SyncFromHardware();                           // the prime

        Assert.True(Eventually.Until(() => vm.Level == 2), "the deferred read never reached the slider");
        Assert.Equal(1, port.GetCount);
        Assert.Empty(port.SetCalls);                     // a prime reads; it must never write
    }

    /// <summary>A backlight with nothing readable keeps its placeholder and stays quiet: the prime must not
    /// throw, and the slider must not move.</summary>
    [Fact]
    public void ABacklightsPrime_DoesNotWrite_EvenWhenItChangesTheSlider()
    {
        var port = new FakeKeyboardBrightness { Level = 1 };
        var vm = new BacklightViewModel(port, _ => true, Eventually.Sync);

        vm.SyncFromHardware();
        Thread.Sleep(50);

        Assert.Equal(1, vm.Level);
        Assert.Equal(Loc.T("Dim"), vm.LevelName);
        Assert.Empty(port.SetCalls);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A one-zone static panel over the given state; <paramref name="written"/> collects every
    /// brightness the panel sends to the device, which is the whole subject of this file.</summary>
    private static LightViewModel Panel(LightSettings state, List<byte> written, Func<int?>? readBrightness)
        => new("Keyboard", [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
               zones: 1,
               applyAll: (_, _, brightness, _, _) => written.Add(brightness),
               applyZone: null, state, save: () => { }, readBrightness);
}
