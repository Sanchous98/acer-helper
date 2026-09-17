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
/// TWO THINGS THE OPEN OWES, and they pull in opposite directions, which is why they are pinned together:
///
///  * it must NOT paint while a host owns the surface. The panels write STRAIGHT to the zones (this path does
///    not go through <c>LightingCoordinator.Paint</c>, which is where everything else yields), so an ungated
///    re-apply lands the app's frame on Windows Dynamic Lighting's keyboard — and lands it out of band: the
///    bridge's dedupe mirror (<c>LampArrayBridge._written</c>) is never told, so the host's own next frame of
///    the same colours is judged unchanged and skipped, and the app's colour stays on the keyboard. Measured
///    here as the write COUNT: the build re-applies a configured zone once, and an open must not add a second
///    write when a host owns the surface. The differential control — the same open with
///    <c>HostOwnsLighting</c> false — is the test below it, so neither the gate nor its polarity is assumed.
///
///  * it MUST settle the plain backlight slider. That control exists only where the device has NO RGB panels;
///    its level can move with no event the app ever sees (another tool, a BIOS hotkey that raises no raw input,
///    an Fn press made while the drawer was shut); and it is settled by a READ, because it has no stored value
///    to push (its slider is a deferred placeholder whose write is verified by read-back). Leaving it out left
///    the slider on its startup value for the whole session.
///
/// WHAT THIS FILE DOES NOT COVER, named rather than left implicit: the drawer's INTERACTIVE writes (a drag of a
/// panel's brightness or colour) still reach the zones while a host owns the surface, which is a recorded
/// decision (<c>docs/lamparray.md</c>, "the Lighting panel's controls are not disabled while a host owns the
/// surface"). Only the open is gated.
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

    /// <summary>The other reported regression. A device whose only lighting control is the plain backlight has
    /// no panels at all, so the drawer-open's panel walk reaches nothing: the level is settled by a read, and
    /// that read has to happen on the open or an out-of-band change sits unreflected for the whole session.
    ///
    /// The signal is the slider, not the clock: the settle is enqueued on the backlight's serial worker and
    /// posts its correction (see <c>VerifiedHwValue</c>), so the poster is injected and waited on. The
    /// placeholder assertion first is what makes the change observable — the slider really does start at 0.</summary>
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
        Assert.True(Eventually.Until(() => vm.Backlight!.Level == 1), "the startup prime never landed");

        port.Level = 2;                                           // out-of-band, with no event of ours to say so

        Drawer(vm).OpenLightingCommand.Execute(null);

        Assert.True(Eventually.Until(() => vm.Backlight!.Level == 2), "the drawer never settled the slider");
        Assert.Empty(port.SetCalls);                              // a settle reads; it must never write
    }

    /// <summary>The open still writes nothing on a backlight-only device that a host owns — asserted together
    /// with the read, because the gate and the settle sit in the same method and an order that read BEFORE the
    /// gate would be invisible in the tests above (no host is set there at all).</summary>
    [Fact]
    public void TheDrawerOpen_DoesNotEvenReadTheBacklight_WhileAHostOwnsTheSurface()
    {
        var port = new FakeKeyboardBrightness { Level = 2 };
        var poster = new Eventually.Poster();
        var vm = new LightingViewModel(null, new Dictionary<string, LightSettings>(), EnsureZone,
                                       save: () => { }, followsProfile: false, _ => { },
                                       backlight: port, applyBacklight: l => port.Set(l), post: poster.Post,
                                       host: new FakeHostLighting { HostOwnsLighting = true });

        Drawer(vm).OpenLightingCommand.Execute(null);

        Assert.Equal(0, port.GetCount);     // the gate is the FIRST thing Reapply does: not even a read
        Assert.Equal(0, vm.Backlight!.Level);
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

    /// <summary>A mode's per-zone state for the one zone below, already configured — so the panel's
    /// construction re-apply is the write the host gate has to stop.</summary>
    private static Dictionary<string, LightSettings> Brightness(int brightness)
        => new() { [ZoneName] = new LightSettings { Brightness = brightness, Configured = true } };

    /// <summary>One static zone that records every brightness it is sent. No brightness READ: this file is
    /// about writes, and a read would add a hardware round-trip to every construction for nothing.</summary>
    private static RgbZone Zone(List<byte> written)
        => new(ZoneName, 1, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
               (_, brightness, _, _, _) => { written.Add(brightness); return true; });

    /// <summary>Same contract as <c>LaptopService.EnsureLightZone</c>: the mode's per-zone entry, created on
    /// first sight under the service's lock. Never reached here — the backlight-only device builds no panels —
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
