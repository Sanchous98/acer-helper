using System.Runtime.CompilerServices;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// THE LIGHTING PANEL'S CONTROLS WHILE A HOST OWNS THE PUBLISHED SURFACE — greyed out, not silently dropped.
///
/// The panel's controls write STRAIGHT to the RGB zones (<c>LightViewModel</c>'s <c>applyAll</c>/<c>applyZone</c>
/// delegates), which is not the path <c>LightingCoordinator.Paint</c> serves, so nothing in the app's own yield
/// covers them. Left alone they produce the defect the owner reported: the edit lands, the bridge's dedupe
/// mirror (<c>LampArrayBridge._written</c>) is never told, and against a STATIC host frame — the common case — the
/// host's re-sends compare as unchanged, are skipped, and the app's colour stays on the keyboard.
///
/// The fix is a VISIBLE refusal rather than a swallowed write (the owner's ruling, 2026-09-18): a check in the
/// delegates would leave the slider moving over a keyboard that does not change. So the section carries
/// <c>ControlsEnabled</c>, every panel carries its own copy, and the views bind IsEnabled to them — the whole
/// LightView panel greys at once, and so does the follows-profile switch, which is a write path too (turning it
/// off BUILDS a panel, and a panel's construction re-applies its zone).
///
/// WHAT IS PINNED HERE is the flag: the polarity of the ownership→availability mapping, the seeding that makes a
/// section BUILT under a host come up greyed (a language rebuild mid-session), the fan-out to the panels, the
/// seeding of a panel built LATER, and — through the coordinator's injectable poster — the one call that greys
/// the LIVE section out when the surface's event fires. The views' IsEnabled bindings are pinned as source wiring
/// (nothing in this suite can execute Avalonia) and the name inside them is checked harder than that by the
/// compiler: both views declare x:DataType and the project compiles bindings
/// (AvaloniaUseCompiledBindingsByDefault), so a binding to a property that does not exist fails the BUILD — which
/// the mutation "rename the binding to ControlsEnabledX" was run to confirm (Avalonia error AVLN2000).
/// </summary>
public class LightingControlGateTests
{
    private const string ZoneName = "Keyboard";

    // ---------------------------------------------------------------- the flag, at the section

    /// <summary>A section built while a host holds the surface comes up greyed, panels and all. This is the
    /// language-rebuild case: the section that replaces the live one is constructed with the surface's flag
    /// already true, and would otherwise present live controls over a keyboard the host is painting.
    ///
    /// MUTATION that reddens it: seed the constructor the other way — <c>HostOwnershipChanged(false)</c> there,
    /// or assign the field <c>_controlsEnabled = true</c> — and the panel is built live.</summary>
    [Fact]
    public void ASectionBuiltUnderAHost_ComesUpWithItsControlsGreyedOut()
    {
        var vm = Lighting(Zone(new List<byte>()), Brightness(40), new FakeHostLighting { HostOwnsLighting = true });

        Assert.NotEmpty(vm.Panels);                    // Control: there IS a control the flag is about
        Assert.False(vm.ControlsEnabled);
        Assert.False(vm.Panels[0].ControlsEnabled);
    }

    /// <summary>The differential control for the test above, so neither the flag nor its polarity is assumed: the
    /// same build with a surface that nobody holds, and with no surface at all (a machine where the driver is not
    /// installed — <c>LampArrayHost.Create</c> returns null there), comes up LIVE.
    ///
    /// MUTATION that reddens it: seed the constructor unconditionally false (`ControlsEnabled = false`), the
    /// shape that would grey the panel on every machine.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void ASectionBuiltWithoutAHost_ComesUpLive(bool? hostOwns)
    {
        var host = hostOwns is { } owns ? new FakeHostLighting { HostOwnsLighting = owns } : null;
        var vm = Lighting(Zone(new List<byte>()), Brightness(40), host);

        Assert.NotEmpty(vm.Panels);
        Assert.True(vm.ControlsEnabled);
        Assert.True(vm.Panels[0].ControlsEnabled);
    }

    /// <summary>The polarity, both ways, on a section that is already built and live — the shape the live flip
    /// arrives in. A takeover greys the controls; a hand-back brings them back. This is the one place the negation
    /// lives (the surface's flag reads "a host owns it", the controls' reads "the controls take input"), which is
    /// why it is pinned in both directions rather than only where the interesting case happens to be.
    ///
    /// MUTATION that reddens it: drop the negation (<c>ControlsEnabled = hostOwns</c>) — the first assertion
    /// fails; or leave the panels out of the fan-out
    /// (<c>OnControlsEnabledChanged</c> made a no-op) — the panel assertions fail while the section's passes,
    /// which is exactly the vacuity this test would otherwise hide.</summary>
    [Fact]
    public void AHostTakingTheSurface_GreysTheControlsOut_AndHandingItBack_BringsThemBack()
    {
        var vm = Lighting(Zone(new List<byte>()), Brightness(40), new FakeHostLighting { HostOwnsLighting = false });
        Assert.True(vm.ControlsEnabled);               // Control: it starts live, so both flips are real moves

        vm.HostOwnershipChanged(true);
        Assert.False(vm.ControlsEnabled);
        Assert.False(vm.Panels[0].ControlsEnabled);    // the panel is what the view actually binds

        vm.HostOwnershipChanged(false);
        Assert.True(vm.ControlsEnabled);
        Assert.True(vm.Panels[0].ControlsEnabled);
    }

    /// <summary>A panel built AFTER the grey-out is seeded from the section rather than defaulted. This is the
    /// follows-profile switch: turning it off builds a panel, and a panel's construction re-applies its zone
    /// (<c>LightViewModel</c>'s <c>if (state.Configured) ApplyNow()</c>), so a panel that came up live there would
    /// be a write the flag never had a chance to prevent — and the switch itself is greyed for that reason.
    ///
    /// The zone is configured so the construction re-apply really is a write: the write count is the witness that
    /// the panel built here is the real path and not a stub.
    ///
    /// MUTATION that reddens it: drop <c>panel.ControlsEnabled = ControlsEnabled</c> from
    /// <c>LightingViewModel.BuildPanel</c> — the panel takes <c>LightViewModel</c>'s default, true.</summary>
    [Fact]
    public void APanelBuiltAfterTheGreyOut_ComesUpGreyedToo()
    {
        var written = new List<byte>();
        var vm = new LightingViewModel(new RgbDevice(new FakeRgbController { Zones = [FollowZone(written)] }),
                                       FakeLightZones.Of("Lightbar", new LightZoneState(true, 0, 40, 5, 1, 0xFF0000, [])),
                                       followsProfile: true, saveFollowsProfile: _ => { },
                                       host: new FakeHostLighting { HostOwnsLighting = true });

        Assert.Empty(vm.Panels);                       // Control: the follow switch is what builds one
        vm.HostOwnershipChanged(true);                 // ...and the host takes the surface in the meantime

        vm.FollowsProfile = false;                     // the user's flip — greyed in the view, forced here

        var panel = Assert.Single(vm.Panels);
        Assert.False(panel.ControlsEnabled);
        Assert.Equal([40], written);                   // Control: the built panel IS the writing path
    }

    // ---------------------------------------------------------------- the flag, from the surface's event

    /// <summary>THE LIVE FLIP, end to end: the surface's <c>OwnerChanged</c> reaches the section that is on
    /// screen and greys its controls out, and a hand-back brings them back. This is the coordinator's half of the
    /// rule — nothing else calls <see cref="LightingViewModel.HostOwnershipChanged"/> on a running app — and the
    /// two halves of it are asserted together because they are one line apart: the push to the section, and the
    /// status line the user reads while the controls are unavailable.
    ///
    /// The coordinator is constructed for real (its DispatcherTimer and its two watchers build in a bare xUnit
    /// process — measured) and the section is Attach-ed to it, which is what <c>AppController</c> does right after
    /// BuildUi. The POSTER is injected: the surface fires on the bridge's worker thread and the real one posts
    /// through a dispatcher this process cannot pump, so without the seam this test would assert that nothing
    /// happened — the shape that cannot fail. The poster here runs the post inline, which is also what makes the
    /// order "Attach, then flip" the order arranged below.
    ///
    /// MUTATION that reddens it: delete the push from <c>LightingCoordinator.OnHostOwnerChanged</c>, or drop the
    /// subscription in the constructor — the section stays live while the keyboard is the host's, with the whole
    /// rest of the suite green (measured: the coordinator's push was the one line here whose deletion left
    /// everything else passing, which is why this test exists).</summary>
    [Fact]
    public void TheSurfacesOwnershipEvent_GreysTheLiveSectionOut_AndBringsItBack()
    {
        var host = new FakeHostLighting();
        var poster = new Eventually.Poster();
        var store = new FakeSettingsStore();
        using var coord = new LightingCoordinator(
            new LaptopService(new FakeDevice { Lighting = new FakeRgbDevice { Zones = [Keyboard()] } },
                              store, new FakeHostFactory(host)),
            post: poster.Post);

        var written = new List<byte>();
        var section = new LightingViewModel(new FakeRgbDevice { Zones = [Zone(written)] },
                                            Brightness(40), followsProfile: false, _ => { }, host: host);
        var main = Drawer(section);
        coord.Attach(main, section);

        Assert.True(section.ControlsEnabled);          // Control: it starts live, so both flips are real moves

        host.Raise(true);                              // the host takes the surface
        Assert.False(section.ControlsEnabled);
        Assert.False(section.Panels[0].ControlsEnabled);
        Assert.Equal(Loc.T("Keyboard lighting is controlled by Windows Dynamic Lighting"), main.Status);

        host.Raise(false);                             // ...and hands it back
        Assert.True(section.ControlsEnabled);
        Assert.True(section.Panels[0].ControlsEnabled);
    }

    // ---------------------------------------------------------------- the bindings, as source wiring

    /// <summary>Every view that must grey its controls, with the pattern that end needs. Nothing in this suite can
    /// execute Avalonia, so the claim is about source wiring and is checked the way
    /// <see cref="AppArgsWiringTests"/> and <see cref="ArchitectureMapTests"/> check theirs: by reading the tree
    /// from the compiler's path. Each file below names the binding exactly ONCE, so naming it is exact; a second
    /// control binding it would make the row file-level, which is the distinction AppArgsWiringTests measured on
    /// <c>Autostart.Windows.cs</c> and the reason this note is here.
    ///
    /// <c>LightView.axaml</c> is the whole panel (its root StackPanel), which is what covers the effect picker, the
    /// colour swatches and the brightness/speed/direction controls at once. <c>LightingView.axaml</c> is the
    /// follows-profile switch, which is a write path of its own (see the test above). The plain backlight slider
    /// is deliberately absent from both lists: it is separate hardware that no LampArray carries
    /// (<c>LightingViewModel.Reapply</c>).
    ///
    /// The NAME inside the binding is checked harder than a source grep can: both files declare x:DataType and the
    /// project sets AvaloniaUseCompiledBindingsByDefault, so <c>{Binding ControlsEnabled}</c> is compiled against
    /// the typed view-model and a misspelling fails the build. What this row adds is the thing the compiler cannot
    /// see — that the attribute is there at all, and on these two files.
    ///
    /// MUTATION that reddens a row: delete its <c>IsEnabled="{Binding ControlsEnabled}"</c> — the binding is
    /// gone and nothing else in this suite notices, which is why the row exists. (Measured: with the attribute
    /// removed from either file the whole suite stays green without this test.)</summary>
    [Theory]
    [InlineData("UI/Views/LightView.axaml")]
    [InlineData("UI/Views/LightingView.axaml")]
    public void EveryViewThatShowsAControlBoundToTheSurface_BindsIsEnabledToTheFlag(string relativePath)
    {
        Assert.Contains("IsEnabled=\"{Binding ControlsEnabled}\"", Source(relativePath), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The section as <c>AppController.BuildUi</c> builds it: one device, one mode's lighting as the
    /// service's door, and the surface a host may be holding (null where the machine has none).</summary>
    private static LightingViewModel Lighting(RgbZone zone, FakeLightZones mode, IDynamicLighting? host)
        => new(new RgbDevice(new FakeRgbController { Zones = [zone] }),
               mode, followsProfile: false, _ => { }, host: host);

    /// <summary>A mode whose one zone is already configured — so a panel built here re-applies on construction and
    /// the write count is a real witness.</summary>
    private static FakeLightZones Brightness(int brightness)
        => FakeLightZones.Of(ZoneName, new LightZoneState(true, 0, brightness, 5, 1, 0xFF0000, []));

    /// <summary>One static zone that records every brightness it is sent. No brightness READ: this file is about
    /// whether a control CAN write, and a read would add a hardware round-trip to every construction.</summary>
    private static RgbZone Zone(List<byte> written)
        => new(ZoneName, 1, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
               (_, brightness, _, _, _) => { written.Add(brightness); return true; });

    /// <summary>A lightbar: the one zone shape the follow switch exists for, so the constructor leaves it to the
    /// firmware and no panel is built until the switch is flipped.</summary>
    private static RgbZone FollowZone(List<byte> written)
        => new("Lightbar", 1, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
               (_, brightness, _, _, _) => { written.Add(brightness); return true; },
               canFollowProfile: true);

    /// <summary>The published surface, as this file needs it: an ownership flag and the event that announces a
    /// flip. The other members are stubs because nothing here drives it — re-asserting a clobbered frame belongs
    /// to <c>LightingCoordinator.Paint</c>, pinned by the LampArray tests. Unlike the same-shaped fake in
    /// <c>LightingDrawerTests</c> this one HOLDS its handler and can raise it: the coordinator subscribes to it
    /// here, and a field-like event nothing raises is a warning rather than a seam.</summary>
    private sealed class FakeHostLighting : IDynamicLighting
    {
        public bool Enabled { get; private set; }
        public string? LastError { get; private set; }
        public bool HostOwnsLighting { get; set; }

        public event Action<bool>? OwnerChanged;

        /// <summary>The bridge's worker announcing a takeover (true) or a hand-back (false), flag and event
        /// together as <c>LampArrayBridge.WorkerLoop</c> and <c>ReleaseOwnership</c> do it.</summary>
        public void Raise(bool hostOwns) { HostOwnsLighting = hostOwns; OwnerChanged?.Invoke(hostOwns); }

        public bool Enable() { Enabled = true; return true; }
        public void Disable() => Enabled = false;
        public void Reassert() { }
        public void Dispose() { }
    }

    /// <summary>Composition's lighting factory (<c>IDynamicLightingFactory</c>), handing back one already-built
    /// surface. What this file needs is that <c>LaptopService.LampArray</c> IS a surface the coordinator can
    /// subscribe to — the real factory would need the driver and a published device.</summary>
    private sealed class FakeHostFactory(IDynamicLighting host) : IDynamicLightingFactory
    {
        public IDynamicLighting Create(IRgbDevice rgb, Func<RgbZone, bool> include) => host;
    }

    /// <summary>The real drawer command over a device with no capabilities, for the section the coordinator
    /// drives: the coordinator writes to <c>MainViewModel.Status</c> on a flip, so a real one has to exist.
    /// Copied in shape from <c>LightingDrawerTests.Drawer</c>, whose fillers for the sections this file is not
    /// about are the same values the absent presets stand for.</summary>
    private static MainViewModel Drawer(LightingViewModel lighting)
    {
        var d = new FakeDevice();
        return new MainViewModel(d, new UiActions(
            new ProfileActions(_ => { }, TurboToggles: false, _ => { }),
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

    /// <summary>A multi-zone keyboard with a real effect list, the zone the surface's include filter and
    /// <c>LampArrayLayout.Build</c> both expect (DynamicLightingSeamTests states why the minimal effect-less zone
    /// would not do: such a layout is what makes Enable fail). Nothing here enables it — the coordinator only
    /// READS the surface for its ownership — but the service builds it over this device's zones.</summary>
    private static RgbZone Keyboard()
        => new("Kbd", 4, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
               (_, _, _, _, _) => true, (_, _, _) => true);

    /// <summary>The repository root, taken from the COMPILER's path rather than the current directory: the test
    /// host runs with its working directory set to the output folder, where a relative path would find either
    /// nothing or a stale copy of the tree.</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>A source file of this repository, read from the compiler's path.</summary>
    private static string Source(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }
}
