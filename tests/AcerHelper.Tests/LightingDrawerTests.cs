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
///  * it MUST re-apply the RGB panels. The panels write STRAIGHT to the zones — this path does not go through
///    <c>LightingCoordinator.Paint</c> — so the open is the doubt moment at the panel's own level: the write we
///    sent may not have landed, and re-applying is idempotent on a last-write-wins device. Measured here as the
///    write COUNT, with the build's own re-apply as the inline control: a panel that never wrote would make
///    "no third write" hold for the wrong reason.
///
///  * it MUST settle the plain backlight slider. That control exists only where the device has NO RGB panels;
///    its level can move with no event the app ever sees (another tool, a BIOS hotkey that raises no raw input,
///    an Fn press made while the drawer was shut); and it is settled by a READ, because it has no stored value to
///    push (its slider is a deferred placeholder whose write is verified by read-back). So the drawer does for it
///    what <see cref="LightingViewModel.Prime"/> does at startup — and on a device whose RGB zone is the
///    firmware's (no panel is built), the open is the ONLY moment the level can be picked up.
///
///  * a click on the button of the drawer ALREADY SHOWING closes it (<c>OpenDrawer</c>'s toggle) and must not
///    re-apply on the way out: the re-apply re-writes the panels and queues the backlight's read, and that read
///    is a device transaction on a serial worker that does not coalesce, so every toggle click would queue one
///    more of them.
///
/// WHAT THIS FILE DOES NOT COVER, named rather than left implicit. The drawer's INTERACTIVE writes (a drag of a
/// panel's brightness or colour) are the panels' own path, exercised where a panel is built directly; this file
/// is about the drawer's OPEN. What a panel does with a value the mode does not hold yet is
/// <c>LightingAdoptionTests</c>' business.
/// </summary>
public class LightingDrawerTests
{
    private const string ZoneName = "Keyboard";

    // ---------------------------------------------------------------- the open, and the panels it drives

    /// <summary>The doubt moment at the panel level: opening the Lighting drawer pushes OUR stored value at the
    /// device again, so a write that never landed is retried rather than believed. The write count is the witness,
    /// and the build's own re-apply of the configured zone is the inline control — without it, "no second write"
    /// would hold for a panel that never wrote at all.</summary>
    [Fact]
    public void TheDrawerOpen_ReappliesTheConfiguredZone()
    {
        var written = new List<byte>();
        var vm = Lighting(Zone(written), Brightness(40));

        Assert.Equal([40], written);            // Control: a configured zone IS re-applied when it is built

        Drawer(vm).OpenLightingCommand.Execute(null);

        Assert.Equal([40, 40], written);        // the doubt moment re-applied OUR value, as it always did
    }

    // ---------------------------------------------------------------- the open, on a plain-backlight device

    /// <summary>The other owed settle, on the device shape where the backlight is the only control: a device with
    /// no RGB panels at all, so the drawer-open's panel walk reaches nothing and the level is settled by a read
    /// that has to happen on the open or an out-of-band change sits unreflected for the whole session.
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
        var vm = new LightingViewModel(null, FakeLightZones.Empty(),
                                       followsProfile: false, saveFollowsProfile: _ => { },
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

    /// <summary>The settle on the shape where the panel walk is genuinely empty for a DIFFERENT reason: an RGB
    /// device whose only zone is one the firmware follows, so the section builds no panel for it (the follow
    /// switch), beside a plain backlight. That is the shape <c>AppController.BuildUi</c> describes — it hands the
    /// view-model BOTH <c>d.Lighting</c> and <c>d.KeyboardBrightness</c> — so it is buildable rather than
    /// assembled to make a branch fire.
    ///
    /// NO BACKEND IN TODAY'S TREE EXPOSES BOTH (Acer publishes the RGB device and reads its keyboard brightness
    /// through the zone; Dell and the generic Linux backend publish the brightness port and have no RGB device),
    /// so this arrangement is LATENT — reachable only when a backend wires a plain backlight beside an RGB one.
    /// It is pinned because the settle must not depend on the RGB device being absent, only on the control being
    /// there: the drawer's walk reaches no panel either way, and the level is still settled by the open.
    ///
    /// MUTATION that reddens it: drop the <c>Backlight?.SyncFromHardware()</c> line from
    /// <see cref="LightingViewModel.Reapply"/> — the slider then stays on its placeholder, 0, the level a failed
    /// read gives, for the whole session, which is the failure this read exists to close.</summary>
    [Fact]
    public void TheDrawerOpen_SettlesThePlainBacklight_WhenNoPanelIsBuilt()
    {
        var port = new FakeKeyboardBrightness { Level = 2 };
        var poster = new Eventually.Poster();
        var vm = BacklightDevice(port, poster);

        Assert.Empty(vm.Panels);                  // Control: the RGB surface is there, and no panel is built for it
        Assert.NotNull(vm.Backlight);
        Assert.Equal(0, vm.Backlight!.Level);     // the deferred placeholder, not a reading

        Drawer(vm).OpenLightingCommand.Execute(null);

        Assert.True(Eventually.Until(() => poster.Delivered >= 1), "the drawer never settled the slider");
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
        var vm = Lighting(Zone(written), Brightness(40));

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
            // The same values the absent presets stand for: Auto at the schema's default 70/70, the default ramp
            // in each fan's unused curve field, and stock 0/0 offsets. This file is about the lighting section;
            // these two are fillers either way. (An EMPTY curve would not do as the filler: a FanSettings is one
            // duty% per anchor or it is not a FanSettings — Domain/Fan.cs.)
            new FanSection(new FanAxisState(FanMode.Auto, new FanSettings(false, Fan.DefaultDuties(), 70),
                                                         new FanSettings(false, Fan.DefaultDuties(), 70)),
                           (_, _, _) => { }, (_, _, _) => { }, _ => Task.CompletedTask),
            new GpuSection(new GpuAxisState(0, 0), (_, _) => { }),
            new CpuSection([], null, _ => { }),
            new CoSection([], [], _ => { }),
            new BatterySection(d.Battery, null, null, null),
            new OptionsSection([], [], [], TurboToggles: false, _ => { }, _ => { }, _ => { },
                               AppLanguage.System, _ => { })), lighting);
    }

    /// <summary>The section as <c>AppController.BuildUi</c> builds it: one device and one mode's lighting as the
    /// service's door.</summary>
    private static LightingViewModel Lighting(RgbZone zone, FakeLightZones mode)
        => new(new RgbDevice(new FakeRgbController { Zones = [zone] }),
               mode, followsProfile: false, _ => { });

    /// <summary>The same build for the other device shape: an RGB device whose only zone is the firmware's while
    /// the follow switch is on (so no panel is built and the backlight is), plus that backlight.</summary>
    private static LightingViewModel BacklightDevice(FakeKeyboardBrightness port, Eventually.Poster poster)
        => new(new RgbDevice(new FakeRgbController { Zones = [FollowZone()] }),
               FakeLightZones.Of("Lightbar", LightZoneState.Default),
               followsProfile: true, _ => { },
               backlight: port, applyBacklight: l => port.Set(l), post: poster.Post);

    /// <summary>A mode whose one zone is already configured — so the panel's construction re-apply is the write
    /// the open adds to.</summary>
    private static FakeLightZones Brightness(int brightness)
        => FakeLightZones.Of(ZoneName, new LightZoneState(true, 0, brightness, 5, 1, 0xFF0000, []));

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
}
