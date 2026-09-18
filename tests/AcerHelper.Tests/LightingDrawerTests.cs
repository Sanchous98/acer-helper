using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// THE LIGHTING DRAWER'S OPEN — the footer/tray command, <c>MainViewModel.OpenLighting</c>, down its one
/// interesting call: <see cref="LightingViewModel.Reapply"/>. <c>LightingAdoptionTests</c> named this path as an
/// uncovered gap in its own words ("the drawer command itself is not exercised anywhere, here or there"), and
/// these tests drive the REAL command on a real <see cref="MainViewModel"/>, so what is pinned is the path the
/// button and the tray use rather than a method that merely looks like it.
///
/// THREE THINGS THE OPEN OWES, and they pull in different directions, which is why they are pinned together:
///
///  * it must NOT paint the RGB zones while a host owns the surface. The panels write STRAIGHT to the zones
///    (this path does not go through <c>LightingCoordinator.Paint</c>, which is where everything else yields),
///    so an ungated re-apply lands the app's frame on Windows Dynamic Lighting's keyboard — and lands it out of
///    band: the bridge's dedupe mirror (<c>LampArrayBridge._written</c>) is never told, so the host's own next
///    frame of the same colours is judged unchanged and skipped, and the app's colour stays on the keyboard.
///    Measured here as the write COUNT: the build re-applies a configured zone once, and an open must not add a
///    second write when a host owns the surface. The differential control — the same open with
///    <c>HostOwnsLighting</c> false — is the test below it, so neither the gate nor its polarity is assumed.
///
///  * it MUST settle the plain backlight slider, host or no host. That control exists only where the device has
///    NO RGB panels; its level can move with no event the app ever sees (another tool, a BIOS hotkey that raises
///    no raw input, an Fn press made while the drawer was shut); and it is settled by a READ, because it has no
///    stored value to push (its slider is a deferred placeholder whose write is verified by read-back). The host
///    gate above is about the SURFACE a host holds — the LampArray is built from the RGB device, and no host
///    claims an <see cref="IKeyboardBrightness"/> — so it must not decide the backlight's read.
///
///  * a click on the button of the drawer ALREADY SHOWING closes it (<c>OpenDrawer</c>'s toggle) and must not
///    re-apply on the way out: the re-apply re-writes the panels and queues the backlight's read, and that read
///    is a device transaction on a serial worker that does not coalesce, so every toggle click would queue one
///    more of them.
///
/// WHAT THIS FILE DOES NOT COVER, named rather than left implicit: the drawer's INTERACTIVE writes (a drag of a
/// panel's brightness or colour) still reach the zones while a host owns the surface — the same straight-to-zone
/// path, ungated, which is a decision the owner has not made yet (<c>docs/lamparray.md</c>, and the record in
/// <c>docs/domain-refactoring-plan.md</c> §7 that asks for it). Only the open is gated.
/// </summary>
public class LightingDrawerTests
{
    private const string ZoneName = "Keyboard";

    // ---------------------------------------------------------------- the open, while a host owns the surface

    /// <summary>The reported regression, at the level the app calls it: with a host holding the surface, opening
    /// the Lighting drawer writes NOTHING. The write count is the witness in both directions — the build's own
    /// re-apply of a configured zone has to be there (or "no second write" would hold for a panel that never
    /// wrote at all), and the open must not add to it.</summary>
    [Fact]
    public void TheDrawerOpen_WritesNothing_WhileAHostOwnsTheSurface()
    {
        var written = new List<byte>();
        var vm = Lighting(Zone(written), Brightness(40), new FakeHostLighting { HostOwnsLighting = true });

        Assert.Equal([40], written);            // Control: a configured zone IS re-applied when it is built

        var main = Drawer(vm);
        main.OpenLightingCommand.Execute(null);

        Assert.True(main.IsLightingPage);       // Control: the command ran and really opened the drawer
        Assert.Equal([40], written);            // ...and the host's surface was left alone
    }

    /// <summary>The differential control for the test above, and the reason that one is not vacuous: the same
    /// open on the same panel, with the host's flag off (it never took the surface, or it let go), DOES
    /// re-apply. Mutation that reddens this one while its twin stays green: gate on the presence of a surface
    /// rather than on its ownership (<c>if (_host != null) return;</c>).</summary>
    [Fact]
    public void TheDrawerOpen_Reapplies_WhenTheHostHasLetGo()
    {
        var written = new List<byte>();
        var vm = Lighting(Zone(written), Brightness(40), new FakeHostLighting { HostOwnsLighting = false });

        Assert.Equal([40], written);            // Control: as above

        Drawer(vm).OpenLightingCommand.Execute(null);

        Assert.Equal([40, 40], written);        // the doubt moment re-applied OUR value, as it always did
    }

    // ---------------------------------------------------------------- the open, on a plain-backlight device

    /// <summary>The other reported regression, on the device shape where the backlight is the only control: a
    /// device with no RGB panels at all, so the drawer-open's panel walk reaches nothing and the level is settled
    /// by a read that has to happen on the open or an out-of-band change sits unreflected for the whole session.
    ///
    /// The signal is the POSTER, not the clock: the settle is enqueued on the backlight's serial worker and
    /// posts its correction (see <c>VerifiedHwValue</c>), so the poster is injected and waited on — and it is
    /// the delivered post, rather than the slider it moves, that orders the drain below (the post is where a
    /// correction could queue a write). The placeholder assertion first is what makes the change observable —
    /// the slider really does start at 0.</summary>
    [Fact]
    public void TheDrawerOpen_SettlesThePlainBacklightSlider()
    {
        var port = new FakeKeyboardBrightness { Level = 1 };
        var poster = new Eventually.Poster();
        var vm = new LightingViewModel(null, new Dictionary<string, LightSettings>(), EnsureZone,
                                       save: () => { }, followsProfile: false, _ => { },
                                       backlight: port, applyBacklight: l => port.Set(l), post: poster.Post);

        Assert.Empty(vm.Panels);                                  // the device shape this control exists on
        Assert.NotNull(vm.Backlight);
        Assert.Equal(0, vm.Backlight!.Level);                     // the deferred placeholder, not a reading

        vm.Prime();                                               // startup: the level is read once here
        Assert.True(Eventually.Until(() => poster.Delivered >= 1), "the startup prime never landed");
        Assert.Equal(1, vm.Backlight!.Level);

        port.Level = 2;                                           // out-of-band, with no event of ours to say so

        Drawer(vm).OpenLightingCommand.Execute(null);

        Assert.True(Eventually.Until(() => poster.Delivered >= 2), "the drawer never settled the slider");
        Assert.Equal(2, vm.Backlight!.Level);

        // A settle reads; it must never write — and that is a claim about a NON-event, so it is drained rather
        // than asserted on the clock: the follow-up read is queued behind whatever the settle queued, and the
        // worker is FIFO, so a write it had wrongly queued has run by the time that read's post is delivered.
        // Same signal, same reason, as LightingPrimeTests.ABacklightsPrime_DoesNotWrite_EvenWhenItChangesTheSlider:
        // "the write has not happened yet" and "no write was ever queued" look identical from this thread.
        vm.Backlight!.SyncFromHardware();
        Assert.True(Eventually.Until(() => poster.Delivered >= 3), "the follow-up read never ran");

        Assert.Empty(port.SetCalls);
    }

    /// <summary>The settle is NOT gated by the host's flag, and the device arranged here is the one where that
    /// rule costs something: a lightbar the firmware follows (so the drawer really does have an empty panel walk)
    /// beside a plain backlight, on a machine whose surface a host holds. That is the shape
    /// <c>AppController.BuildUi</c> describes — it hands the view-model BOTH <c>d.Lighting</c> and
    /// <c>d.KeyboardBrightness</c>, and a host exists only because <c>d.Lighting</c> does — so it is buildable
    /// rather than assembled to make the gate fire. The RGB device is not decoration: it is what makes the host
    /// real, and the follow switch is what empties the panel walk.
    ///
    /// NO BACKEND IN TODAY'S TREE EXPOSES BOTH (Acer publishes the RGB device and reads its keyboard brightness
    /// through the zone; Dell and the generic Linux backend publish the brightness port and have no RGB device),
    /// so the gate this test pins is LATENT — reachable only when a backend wires a plain backlight beside an RGB
    /// one. It is pinned because the gate was wrong about WHICH surface it guarded, not because production takes
    /// it today.
    ///
    /// MUTATION that reddens it: widen the gate back over the whole method (the early
    /// <c>if (_host is { HostOwnsLighting: true }) return;</c> before the settle). The slider then stays on its
    /// placeholder — 0, the level a failed read gives — for the whole session, which is the failure this read
    /// exists to close.</summary>
    [Fact]
    public void TheDrawerOpen_SettlesThePlainBacklight_EvenWhileAHostOwnsTheSurface()
    {
        var port = new FakeKeyboardBrightness { Level = 2 };
        var poster = new Eventually.Poster();
        var vm = BacklightDevice(port, poster, new FakeHostLighting { HostOwnsLighting = true });

        Assert.Empty(vm.Panels);                  // Control: the RGB surface is there, and no panel is built for it
        Assert.NotNull(vm.Backlight);
        Assert.Equal(0, vm.Backlight!.Level);     // the deferred placeholder, not a reading

        Drawer(vm).OpenLightingCommand.Execute(null);

        Assert.True(Eventually.Until(() => poster.Delivered >= 1), "the host's flag gated the backlight settle");
        Assert.Equal(2, vm.Backlight!.Level);     // the out-of-band level reached the slider

        // ...and the settle reads rather than writes, drained as above: a non-event decided by a signal, with the
        // read count asserted once that signal is in (the settle's read is the first, this drain's the second).
        vm.Backlight!.SyncFromHardware();
        Assert.True(Eventually.Until(() => poster.Delivered >= 2), "the follow-up read never ran");

        Assert.Equal(2, port.GetCount);
        Assert.Empty(port.SetCalls);
    }

    // ---------------------------------------------------------------- the click that CLOSES the drawer

    /// <summary>A click on the button of the drawer already showing closes it (the toggle in <c>OpenDrawer</c>),
    /// and that click must not run the re-apply on the way out. The panel's write is the witness because it is
    /// synchronous — the count is decided when the command returns — and the drawer's own state is asserted
    /// first, so "no re-apply" cannot hold for a click that did nothing at all.
    ///
    /// MUTATION that reddens it: run the re-apply before the toggle is consulted (the shape this file was fixed
    /// from: <c>_lighting?.Reapply()</c> unconditionally at the top of <c>OpenLighting</c>) — the closing click
    /// then writes a third time.</summary>
    [Fact]
    public void ReClickingTheOpenDrawer_ClosesIt_WithoutReapplyingThePanels()
    {
        var written = new List<byte>();
        var vm = Lighting(Zone(written), Brightness(40), new FakeHostLighting { HostOwnsLighting = false });

        var main = Drawer(vm);
        main.OpenLightingCommand.Execute(null);

        Assert.True(main.IsDrawerOpen);         // Control: the first click opened it...
        Assert.True(main.IsLightingPage);
        Assert.Equal([40, 40], written);        // ...and re-applied the configured zone

        main.OpenLightingCommand.Execute(null); // the same button, the same section: this one CLOSES

        Assert.False(main.IsDrawerOpen);        // Control: the click was a close, not a no-op
        Assert.Equal([40, 40], written);        // ...and no re-apply was run for it
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The real drawer command, over a device with no capabilities: this file is about the lighting
    /// section, and every other section would only add places for the setup to be wrong.</summary>
    private static MainViewModel Drawer(LightingViewModel lighting)
    {
        var d = new FakeDevice();
        return new MainViewModel(d, new UiActions(
            new ProfileActions(_ => { }, TurboToggles: false, _ => { }),
            new FanSection(new FanPreset(), (_, _, _) => { }, (_, _, _) => { }, _ => Task.CompletedTask),
            new GpuSection(new GpuOcPreset(), (_, _) => { }),
            new CpuSection([], null, _ => { }),
            new CoSection([], [], _ => { }),
            new BatterySection(d.Battery, null, null, null),
            new OptionsSection([], [], [], TurboToggles: false, _ => { }, _ => { }, _ => { },
                               AppLanguage.System, _ => { })), lighting);
    }

    /// <summary>The section as <c>AppController.BuildUi</c> builds it: one device, one mode's per-zone state, and
    /// the surface a host may be holding.</summary>
    private static LightingViewModel Lighting(RgbZone zone, Dictionary<string, LightSettings> lights,
                                              IDynamicLighting host)
        => new(new RgbDevice(new FakeRgbController { Zones = [zone] }),
               lights, EnsureZone, save: () => { }, followsProfile: false, _ => { }, host: host);

    /// <summary>The same build for the other device shape: an RGB device whose only zone is the firmware's while
    /// the follow switch is on (so no panel is built and the backlight is), plus that backlight.</summary>
    private static LightingViewModel BacklightDevice(FakeKeyboardBrightness port, Eventually.Poster poster,
                                                     IDynamicLighting host)
        => new(new RgbDevice(new FakeRgbController { Zones = [FollowZone()] }),
               new Dictionary<string, LightSettings>(), EnsureZone,
               save: () => { }, followsProfile: true, _ => { },
               backlight: port, applyBacklight: l => port.Set(l), post: poster.Post, host: host);

    /// <summary>A mode's per-zone state for the one zone below, already configured — so the panel's
    /// construction re-apply is the write the host gate has to stop.</summary>
    private static Dictionary<string, LightSettings> Brightness(int brightness)
        => new() { [ZoneName] = new LightSettings { Brightness = brightness, Configured = true } };

    /// <summary>One static zone that records every brightness it is sent. No brightness READ: this file is
    /// about writes, and a read would add a hardware round-trip to every construction for nothing.</summary>
    private static RgbZone Zone(List<byte> written)
        => new(ZoneName, 1, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
               (_, brightness, _, _, _) => { written.Add(brightness); return true; });

    /// <summary>A lightbar: the one zone shape the follow switch exists for (<c>canFollowProfile: true</c>), so
    /// the constructor leaves it to the firmware and the drawer's panel walk is genuinely empty. Writes nothing
    /// and reads nothing: this file is about the backlight on that shape.</summary>
    private static RgbZone FollowZone()
        => new("Lightbar", 1, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
               (_, _, _, _, _) => true, canFollowProfile: true);

    /// <summary>Same contract as <c>LaptopService.EnsureLightZone</c>: the mode's per-zone entry, created on
    /// first sight under the service's lock. Never reached by the backlight-only devices — they build no panels —
    /// but it is what the constructor is handed, so the fake matches the real call.</summary>
    private static LightSettings EnsureZone(Dictionary<string, LightSettings> lights, string name)
    {
        if (!lights.TryGetValue(name, out var s)) lights[name] = s = new LightSettings();
        return s;
    }

    /// <summary>The virtual LampArray surface, as the ONE thing this file needs from it: an ownership flag. Its
    /// other members are stubs because the app reads none of them here — the drawer never drives the surface,
    /// and the re-assertion of a clobbered frame belongs to <c>LightingCoordinator.Paint</c>, which is pinned
    /// by the LampArray tests. <c>OwnerChanged</c> is implemented without a field: nothing in this file models
    /// the flip, and a field-like event nobody raises is a warning rather than a seam.</summary>
    private sealed class FakeHostLighting : IDynamicLighting
    {
        public bool Enabled { get; private set; }
        public string? LastError { get; private set; }
        public bool HostOwnsLighting { get; set; }

        public event Action<bool>? OwnerChanged { add { } remove { } }

        public bool Enable() { Enabled = true; return true; }
        public void Disable() => Enabled = false;
        public void Reassert() { }
        public void Dispose() { }
    }
}
