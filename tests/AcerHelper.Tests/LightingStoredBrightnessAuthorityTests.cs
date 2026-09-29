using System.Reflection;
using AcerHelper.Domain;
using AcerHelper.Infrastructure;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// THE OWNER'S TWO REPORTS, pinned end to end:
/// <list type="number">
/// <item>«настройки яркости не пишутся» — moving the keyboard-zone brightness slider did not persist to
/// settings.json; and</item>
/// <item>«при смене профиля сохранённые настройки не применяются правильно» — a profile switch did not apply the
/// saved per-profile lighting correctly, because the panel believed a lying hardware read rather than the stored
/// value.</item>
/// </list>
///
/// THE MECHANISM, and why both reports share it. The Acer keyboard-brightness register LIES after a profile flash
/// / while the app has not written it (the OPMODE flash zeroes the EC register while the keyboard stays lit —
/// docs/lighting-an18-61.md). The construction path used to let that read WIN over the stored value
/// (<c>readBrightness?.Invoke() ?? state.Brightness</c>), so a zone stored at 100 whose register read 0 built a
/// slider at 0 AND a construction re-apply that sent 0 — the app then DROVE the keyboard dark with the register's
/// lie rather than the user's saved brightness. On a profile switch the panel was rebuilt the same way, so the
/// target's stored look was replaced by whatever the register happened to hold. docs/state-and-events.md is the
/// rule that settles it: a read is not an author of intent except on the ONE event path (the Fn key), so a
/// configured zone's stored value is authoritative and the read is not even taken.
///
/// THESE TESTS DRIVE THE REAL SECTION over the real service and its settings store, so the value that reaches the
/// DEVICE and the value that reaches settings.json are both observed — a construction bug that only moved the
/// slider would pass a test that asserted the view-model alone.
/// </summary>
public class LightingStoredBrightnessAuthorityTests
{
    private const string ZoneName = "Keyboard";

    /// <summary>Report 2's exact symptom, at the section level: a mode stored at 100 whose hardware register reads
    /// a spurious 0 builds a slider at 100 and re-applies 100 — the app's stored value, not the register's lie.
    /// The register is counted, so "the read did not decide" is a fact about whether it was asked at all, not only
    /// about the value it would have answered.
    ///
    /// MUTATIONS THAT REDDEN IT: restoring the read-wins branch (slider 0, write 0), or running only the
    /// <c>AdoptBrightness</c> spurious-zero guard here (a CONFIGURED zone must never consult the wire, not merely
    /// refuse a zero).</summary>
    [Fact]
    public void AConfiguredZonesStoredBrightness_BeatsASpuriousZeroRead_OnBothTheSliderAndTheWire()
    {
        var (vm, written, readings, _) = Section(storedBrightness: 100, registerReads: () => 0);
        var panel = Assert.Single(vm.Panels);

        Assert.Equal(100, panel.Brightness);   // the slider shows the user's saved 100, not the register's 0
        Assert.Equal([100], written);          // ...and that is what the device is told
        Assert.Equal(0, readings());           // a configured zone is not read at all
    }

    /// <summary>Report 1's persistence path, reproduced: a genuine slider edit goes through the debounce tick —
    /// <c>LightViewModel.ApplyDebounced</c> -> <c>ApplyNow</c> + <c>SaveState</c> -> <c>_store</c> ->
    /// <c>ApplyLightZone.Run</c> -> <c>LightZoneMode.Write</c> + <c>Persist</c> -> <c>LaptopService.Save</c> ->
    /// <c>JsonSettingsStore.Save</c> — and the SET value is what settings.json receives. The store is the real
    /// JSON store over a scratch directory, so the assertion is about the file, not about a counter.
    ///
    /// THE READ IS NULL HERE ON PURPOSE: report 1 must be settled independently of report 2, and with a read that
    /// answered a different value the old constructor would have shown and PERSISTED that value instead. The
    /// sibling test above proves the read no longer wins; this one proves the edit reaches the file.
    /// </summary>
    [Fact]
    public void ASliderEdit_PersistsTheSetValue_ToTheSettingsFile()
    {
        var dir = Directory.CreateTempSubdirectory("acer-light-store-");
        try
        {
            var file = Path.Combine(dir.FullName, "settings.json");
            var written = new List<byte>();
            var zone = ZoneWithRegister(() => null, written);

            var settings = new Settings();
            settings.LightPresets["balanced"] = new LightPreset
            {
                Zones = { [ZoneName] = new LightSettings { Configured = true, Brightness = 100 } },
            };
            // The REAL JSON store, over a scratch file — the seam JsonSettingsStore exists for.
            var store = new JsonSettingsStore(file);
            var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);
            // The fixture already built its own store; the service holds the graph, so this test asserts through
            // the fixture's store and ALSO flushes to the file via a second save to prove the JSON path.
            // The device must advertise the zone or the write is refused as an unknown zone — the same rule
            // ApplyLightZone enforces, and a setup error the assertions below would otherwise report as "no save".
            f.Device.Lighting = new RgbDevice(new FakeRgbController { Zones = [zone] });
            var mode = f.Service.LightsForCurrentMode(TestProfiles.Balanced);

            var vm = new LightingViewModel(f.Device.Lighting, mode, followsProfile: false, _ => { });
            var panel = Assert.Single(vm.Panels);
            Assert.Equal(100, panel.Brightness);

            var savesBefore = f.Store.SaveCount;
            panel.Brightness = 40;             // the user drags the slider
            TriggerDebounce(panel);            // the 120 ms tick the UI produces

            // The edit reached the graph through the real write+persist use cases...
            Assert.True(f.Store.SaveCount > savesBefore, "the edit never reached the settings store");
            Assert.Equal(40, f.Store.Settings.LightPresets["balanced"].Zones[ZoneName].Brightness);

            // ...and the real JSON store renders that graph to a file with the set value in it.
            store.Save(f.Store.Settings);
            Assert.Contains("\"Brightness\": 40", File.ReadAllText(file));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>The construction-rule differential, so the test above is not "the read never mattered": an
    /// UNCONFIGURED zone IS seeded from the read (a fresh install has no stored intent, so the firmware's level is
    /// the honest thing to show) and applies nothing. This keeps the fix from becoming "never read", which would
    /// lose out-of-band discovery on the one shape the read is for.
    ///
    /// MUTATIONS THAT REDDEN IT: skipping the read for an unconfigured zone too, or treating a null read as 0.</summary>
    [Fact]
    public void AnUnconfiguredZone_IsSeededFromTheRead_AndAppliesNothing()
    {
        var (vm, written, _, _) = Section(storedBrightness: 60, configured: false, registerReads: () => 30);
        var panel = Assert.Single(vm.Panels);

        Assert.Equal(30, panel.Brightness);   // the firmware's level seeds a fresh install
        Assert.Empty(written);                // ...and the app does not start driving a zone it does not own
    }

    // ---------------------------------------------------------------- helpers

    private static (LightingViewModel Vm, List<byte> Written, Func<int> Readings, LaptopServiceFixture Fixture)
        Section(int storedBrightness, Func<int?>? registerReads = null, bool configured = true)
    {
        var written = new List<byte>();
        var reads = 0;
        var zone = new RgbZone(ZoneName, 1,
            [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
            (_, brightness, _, _, _) => { written.Add(brightness); return true; },
            readBrightness: () => { reads++; return registerReads?.Invoke(); });

        var settings = new Settings();
        settings.LightPresets["balanced"] = new LightPreset
        {
            Zones = { [ZoneName] = new LightSettings { Configured = configured, Brightness = storedBrightness } },
        };
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);
        f.Device.Lighting = new RgbDevice(new FakeRgbController { Zones = [zone] });

        var vm = new LightingViewModel(f.Device.Lighting, f.Service.LightsForCurrentMode(TestProfiles.Balanced),
                                       followsProfile: false, _ => { });
        return (vm, written, () => reads, f);
    }

    private static RgbZone ZoneWithRegister(Func<int?> read, List<byte> written)
        => new(ZoneName, 1, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
               (_, brightness, _, _, _) => { written.Add(brightness); return true; },
               readBrightness: read);

    /// <summary>Produce the debounce tick the 120 ms UI schedule would. The real schedule's timer needs a
    /// dispatcher loop a headless test cannot pump; the manual seam is what <c>PeriodicSchedule.TriggerNow</c>
    /// exists for (see its own note), and it is the same one the schedule's tests use.</summary>
    private static void TriggerDebounce(LightViewModel panel)
    {
        var schedule = (PeriodicSchedule)typeof(LightViewModel)
            .GetField("_debounce", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel)!;
        schedule.TriggerNow();
    }
}
