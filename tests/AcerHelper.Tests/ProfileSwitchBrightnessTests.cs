using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// THE PROFILE'S STORED KEYBOARD BRIGHTNESS SURVIVES THE SWITCH — INCLUDING A STORED ZERO. The owner's report:
/// «сейчас у меня Эко режим сохранил яркость подсветки на нуле, но при смене профиля подсветка горит» — switching
/// to a profile whose stored brightness is 0 left the keyboard LIT — and the clarified rule: «profile flash должен
/// быть, но после flash подсветка должна потухнуть как сохранено в профиле» — the palette flash stays, but AFTER
/// it the keyboard must return to the TARGET profile's stored brightness (0 → dark).
///
/// THE MECHANISM, reproduced below rather than asserted from memory. The switch use case owns the port write and
/// the lighting announcement as one action (<c>Application/ProfileSwitch.cs</c>), so <c>OnProfileApplied</c> fires
/// in the same instant as the firmware's own palette flash. But the refresh poll has not read the new mode's door
/// yet, so at that instant the section (<see cref="LightingViewModel"/>) is still bound to the PREVIOUS mode: a
/// plain repaint re-applies the mode we are LEAVING. So the write that followed the flash carried the old (lit)
/// brightness, and the palette-free burst it kicked kept re-asserting that stale value. The later
/// <c>OnStateChanged</c> that finally reports the new mode did rebind to the target — but only a poll (seconds)
/// later, after the user had already seen the light stay on. That is the reported «подсветка горит».
///
/// THE FIX. <c>OnProfileApplied</c> now resolves the LANDED profile's own door
/// (<c>LaptopService.LightsForCurrentMode(applied)</c>, the overload that takes an already-read profile and so
/// costs no hardware read) and REBINDS the section through it before painting: the palette flash, then the target
/// mode's saved zones painted over it (0 → dark). The follow-up pass that finally reports the same profile is
/// still consumed (it clears the suppression claim and rebinds to the same door, idempotently), and a stale pass
/// describing the previous profile is still dropped by that claim — so there is exactly ONE palette flash per
/// action, as the rest of the suite pins.
///
/// WHY THIS FILE DRIVES THE REAL COORDINATOR. The defect only exists on the ordering between the switch instant,
/// the cached section state and the refresh pass, so a test that copied that ordering into itself would be
/// asserting its own copy. This drives the real <see cref="LightingCoordinator"/>, the real
/// <see cref="LightingViewModel"/> over a real <see cref="RgbDevice"/>, and the real <see cref="SwitchProfile"/>
/// over the real service, then replays the two refresh passes (stale, then caught-up) as
/// <c>AppController.UiPass</c> does. The keyboard write and the palette flash are recorded into ONE ordered log,
/// so "after the flash" is a fact about the sequence and not about a call count.
///
/// THE COORDINATOR BUILDS ITS WATCHERS (a resume hook and a lid window), which is why the rest of the suite
/// documents it as needing a desktop lifetime. That is a lifetime cost on this one path, not a dependency of the
/// assertions: nothing here waits on a watcher, and <see cref="System.IDisposable.Dispose"/> releases them. It was
/// measured to construct and dispose cleanly in this host before being kept.
///
/// MUTATION-VERIFIED. Reverting <c>ApplyProfileApplied</c>'s <c>Paint(rebind: …)</c> to the old parameterless
/// <c>Paint()</c> reddens the first zone assertion in
/// <see cref="SwitchingToAProfileStoredAtZero_LeavesTheKeyboardDark_AfterTheFlash"/> (the write after the flash
/// carries the previous mode's 100) and the non-zero row (it comes back 100, not the target's 42).
/// </summary>
public class ProfileSwitchBrightnessTests
{
    private const string Keyboard = "Keyboard";
    private const string Lightbar = "Lightbar";
    private static readonly AccentColor EcoFlash = new(0, 220, 16);

    /// <summary>The reported case: switching from a lit profile to one stored at brightness 0 must leave the
    /// keyboard DARK. The witness is the ordered event log — the palette flash, then the target mode's zone write
    /// carrying 0 — plus the facts that the stale pass cannot put the old value back, the burst keeps 0, and the
    /// caught-up pass keeps it too.</summary>
    [Fact]
    public void SwitchingToAProfileStoredAtZero_LeavesTheKeyboardDark_AfterTheFlash()
    {
        using var h = Arrange(previousBrightness: 100, targetBrightness: 0);

        h.Switch.Run(TestProfiles.Eco);

        // CONTROL: the flash and the 0 both went out, flash first — the wanted sequence, not a silent no-op.
        Assert.Single(h.Flashes);
        Assert.Equal(EcoFlash, h.Flashes[0]);
        var flash = h.Events.IndexOf("flash");
        var firstZone = h.Events.FindIndex(flash + 1, e => e.StartsWith("zone:", StringComparison.Ordinal));
        Assert.True(flash >= 0 && firstZone == flash + 1, string.Join(" | ", h.Events));
        Assert.Equal("zone:0", h.Events[firstZone]);

        // The stale pass (the poll has not caught up, still describing the previous profile) is dropped by the
        // suppression claim and cannot resurrect the previous mode's 100.
        h.StalePass();
        Assert.Equal("zone:0", h.LastZone);

        // The burst re-asserts the section's own values — now the target's 0 — and sends no second palette.
        h.BurstTick();
        Assert.Equal("zone:0", h.LastZone);
        Assert.Single(h.Flashes);

        // The caught-up pass (the poll finally reporting Eco) keeps 0 and flashes nothing.
        h.CaughtUpPass(profileChanged: true);
        Assert.Equal("zone:0", h.LastZone);
        Assert.Single(h.Flashes);
    }

    /// <summary>The differential control: a target stored at a non-zero brightness is restored to THAT value
    /// after the flash — so the fix is not "paint 0 at every switch". Same path, same passes, only the stored
    /// value differs; the flash is still the target's own palette, sent once.</summary>
    [Fact]
    public void SwitchingToAProfileStoredAtNonZero_RestoresThatBrightness_AfterTheFlash()
    {
        using var h = Arrange(previousBrightness: 100, targetBrightness: 42);

        h.Switch.Run(TestProfiles.Eco);

        Assert.Single(h.Flashes);
        Assert.Equal(EcoFlash, h.Flashes[0]);
        var flash = h.Events.IndexOf("flash");
        var firstZone = h.Events.FindIndex(flash + 1, e => e.StartsWith("zone:", StringComparison.Ordinal));
        Assert.Equal("zone:42", h.Events[firstZone]);   // the previous profile's 100 was the build, not this

        h.StalePass();
        h.BurstTick();
        h.CaughtUpPass(profileChanged: true);
        Assert.Equal("zone:42", h.LastZone);
        Assert.Single(h.Flashes);
    }

    /// <summary>The control that makes the first test meaningful: BEFORE any pass, the section really was bound
    /// to the previous mode — its construction applied the previous profile's brightness. Without this the "0
    /// after the switch" could hold for a section that never held the old value at all.</summary>
    [Fact]
    public void TheSectionStartsBoundToThePreviousMode_SoTheSwitchIsWhatChangesIt()
    {
        using var h = Arrange(previousBrightness: 100, targetBrightness: 0);

        Assert.Equal("zone:100", h.LastZone);   // the build applied the mode it was built for
        Assert.Empty(h.Flashes);
    }

    // ---------------------------------------------------------------- the harness

    /// <summary>The live rig: the real service with the two modes' lighting stored, the real controller whose zone
    /// records every brightness it is sent and whose flash records every palette paint into the SAME ordered log,
    /// the real coordinator over the real section, and the real switch use case.</summary>
    private sealed class Rig : IDisposable
    {
        public required SwitchProfile Switch { get; init; }
        public required LightingCoordinator Coordinator { get; init; }
        public required LightingViewModel Section { get; init; }
        public required LaptopServiceFixture Fixture { get; init; }
        public required List<string> Events { get; init; }
        public required List<AccentColor> Flashes { get; init; }

        public string LastZone =>
            Events.LastOrDefault(e => e.StartsWith("zone:", StringComparison.Ordinal)) ?? "none";

        /// <summary>The stale pass: the poll has not caught up, so it still describes the PREVIOUS profile. It
        /// must be dropped — no repaint, no flash — which is the suppression claim (<c>_pendingId</c>).</summary>
        public void StalePass() => Coordinator.OnStateChanged(
            profileChanged: false, profileId: TestProfiles.Performance.Id, flash: null,
            mode: Fixture.Service.LightsForCurrentMode(TestProfiles.Performance));

        /// <summary>One burst tick, at the panel level: the coordinator's re-apply calls
        /// <c>LightingViewModel.Repaint</c> (palette off), which is exactly this. Driven directly because the real
        /// burst is a 400 ms timer a headless test cannot step; the "reads nothing" property of Repaint is pinned
        /// in <c>LightingAdoptionTests</c>.</summary>
        public void BurstTick() => Section.Repaint();

        /// <summary>The pass that finally reports the TARGET profile, as <c>AppController.UiPass</c> hands it: the
        /// landed profile's flash colour and its mode door. It must keep the target's values and send no second
        /// palette.</summary>
        public void CaughtUpPass(bool profileChanged) => Coordinator.OnStateChanged(
            profileChanged, TestProfiles.Eco.Id, EcoFlash,
            Fixture.Service.LightsForCurrentMode(TestProfiles.Eco));

        public void Dispose() => Coordinator.Dispose();
    }

    /// <summary>Build the rig. The previous mode is left at <paramref name="previousBrightness"/> and the target
    /// at <paramref name="targetBrightness"/>; the hardware's current profile is the previous one, exactly as it
    /// is when the user clicks the target segment.</summary>
    private static Rig Arrange(int previousBrightness, int targetBrightness)
    {
        var controller = new RecordingController();
        var keyboard = new RgbZone(Keyboard, 1,
            [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
            (_, brightness, _, _, _) => { controller.Zone(brightness); return true; });
        var lightbar = new RgbZone(Lightbar, 1,
            [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
            (_, _, _, _, _) => true, canFollowProfile: true);
        controller.Zones = [keyboard, lightbar];

        var settings = new Settings();
        settings.LightPresets["performance"] = new LightPreset
        {
            Zones = { [Keyboard] = new LightSettings { Configured = true, Brightness = previousBrightness } },
        };
        settings.LightPresets["eco"] = new LightPreset
        {
            Zones = { [Keyboard] = new LightSettings { Configured = true, Brightness = targetBrightness } },
        };

        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Performance);
        f.Device.Lighting = new RgbDevice(controller);
        f.Pp!.TraitsOf = p => p.Id switch
        {
            "eco" => new ProfileTraits(ProfileKind.Eco, FlashColor: EcoFlash),
            "performance" => new ProfileTraits(ProfileKind.Performance, FlashColor: new AccentColor(199, 9, 46)),
            _ => ProfileTraits.Unknown,
        };

        var vm = new LightingViewModel(f.Device.Lighting, f.Service.LightsForCurrentMode(TestProfiles.Performance),
                                       followsProfile: true, saveFollowsProfile: _ => { });
        var coordinator = new LightingCoordinator(f.Service, f.Service.Reapply, f.Battery, f.SyncPowerSource,
                                                  // Inline the UI marshaller, the same seam the section's panels take
                                                  // for their adopted reads: the real dispatcher is thread-affine and
                                                  // a bare xUnit process creates it from whichever test thread touches
                                                  // it first, so the announced repaint would otherwise be posted to a
                                                  // loop nobody pumps and the assertions would see no write.
                                                  uiPost: Eventually.Sync);
        coordinator.Attach(Drawer(vm), vm);
        var sw = new SwitchProfile(f.Service, coordinator);

        return new Rig
        {
            Switch = sw, Coordinator = coordinator, Section = vm, Fixture = f,
            Events = controller.Events, Flashes = controller.Flashes,
        };
    }

    /// <summary>A real controller that records, INTO ONE ORDERED LOG, every zone brightness and every palette
    /// flash — the flash and the zone paint are then comparable by position, which is the whole point of the
    /// "after the flash" assertions.</summary>
    private sealed class RecordingController : IRgbController
    {
        public IReadOnlyList<RgbZone> Zones { get; set; } = [];
        public string? ProfileFollowKey => "follow";
        public List<string> Events { get; } = [];
        public List<AccentColor> Flashes { get; } = [];

        public void Zone(byte brightness) => Events.Add($"zone:{brightness}");

        public bool SetProfileFlash(AccentColor color)
        {
            Events.Add("flash");
            Flashes.Add(color);
            return true;
        }

        public void Dispose() { }
    }

    /// <summary>The real drawer host for the section, as <c>LightingDrawerTests</c> builds it: every other section's
    /// delegate is a filler, because this file is about the lighting hand-off.</summary>
    private static MainViewModel Drawer(LightingViewModel lighting)
    {
        var d = new FakeDevice();
        return new MainViewModel(d, new UiActions(
            new ProfileActions(_ => true, TurboToggles: false, _ => true, _ => ProfileTraits.Unknown),
            new FanSection(new FanAxisState(FanMode.Auto, new FanSettings(false, Fan.DefaultDuties(), 70),
                                                         new FanSettings(false, Fan.DefaultDuties(), 70)),
                           (_, _, _) => { }, (_, _, _) => { }, _ => Task.CompletedTask),
            new GpuSection(new GpuAxisState(0, 0), (_, _) => { }, [], _ => { }),
            new GpuMuxSection(_ => Task.FromResult(false), _ => new GpuMuxChange(false, false, null)),
            new CpuSection([], null, _ => { }),
            new CoSection([], [], _ => { }),
            new BatterySection(d.Battery, null, null, null),
            new OptionsSection([], [], [], TurboToggles: false, _ => { }, _ => { }, _ => { },
                               AppLanguage.System, _ => { })), lighting, new NotificationCenter());
    }
}
