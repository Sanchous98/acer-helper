using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The seam the LampArray move opened: <c>LaptopService</c> reaches its lighting surface through
/// <see cref="IDynamicLightingFactory"/> instead of naming the bridge and its transport, which is what keeps
/// Application off Infrastructure (see
/// <c>ArchitectureMapTests.ApplicationPointsAtDomainNotInfrastructure</c>).
///
/// The seam is worth its own test because the compiler cannot see it: a factory that quietly returned null, or a
/// service that ignored the one it was handed, compiles and leaves the Options row absent on a machine where the
/// driver IS installed — the failure is "a feature that never appears", not an error. Nothing else in the suite
/// passes a lighting factory at all, so every other test takes the null branch and would keep passing.
///
/// No hardware: the transport is <see cref="FakeLampArrayTransport"/> and the device is the hand-written
/// <see cref="FakeRgbDevice"/>. The one live thread is the bridge's worker, which the service's own
/// <c>Dispose</c> tears down through the contract (Disable -> Stop -> join).
///
/// NOT covered here, and it is the same gap the plan records for composition sites B and C:
/// <c>DeviceFactory.CreateDynamicLightingFactory()</c> itself. It returns null unless the LampArray driver is
/// installed on the machine running the test, so a test can only pin the wiring below it.
///
/// MUTATION-VERIFIED — each was run against this file, and each reddened the test named, not a sibling:
/// <list type="bullet">
/// <item><b>the factory answers nothing</b> (<c>Create</c> returned <c>null!</c>) — reddens
/// <c>TheFactorysSurfaceIsWhatTheServicePublishes</c> and <c>TheSurfaceIsBuiltOverTheZonesTheAppMayDrive</c>, and
/// leaves <c>WithoutAFactoryThereIsNoSurfaceAndNothingToSwitch</c> GREEN. That last part is the point of the
/// third test having its own case: it cannot tell "no factory was handed over" from "the factory built nothing",
/// and a single test covering both would have been the assertion-that-cannot-fail this suite has been bitten by
/// twice;</item>
/// <item><b>the include filter is dropped</b> (<c>new LampArrayBridge(rgb, transport)</c>) — reddens
/// <c>TheSurfaceIsBuiltOverTheZonesTheAppMayDrive</c> alone: the lightbar is published to the host while the
/// firmware still owns it;</item>
/// <item><b>a missing surface reports success</b> (<c>if (la == null) return (on, null)</c>) — reddens
/// <c>WithoutAFactoryThereIsNoSurfaceAndNothingToSwitch</c>: the Options row would claim a device that is not
/// there, which is the failure that row's own docstring says it exists to prevent.</item>
/// </list>
/// </summary>
public class DynamicLightingSeamTests
{
    /// <summary>A keyboard zone with a REAL effect list. <c>LampArrayLayout.Build</c> drops an effect-less zone,
    /// and a layout with no lamps is exactly what makes <c>Enable</c> fail — so the fixture's minimal zone
    /// (<c>FakeRgbDevice.Zone</c>, no effects) would turn these tests into assertions about the wrong failure:
    /// "unavailable" instead of "published".</summary>
    private static RgbZone Keyboard() =>
        new("Kbd", 4, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: 0x02)],
            (_, _, _, _, _) => true, (_, _, _) => true);

    /// <summary>The profile-indicator lightbar: the zone the app must NOT drive while it follows the firmware's
    /// own palette (its <c>CanFollowProfile</c> flag plus the device flag decide that — see
    /// <c>LaptopService.ZoneAvailableToHost</c>).</summary>
    private static RgbZone Lightbar() =>
        new("Lightbar", 1, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: 0x02)],
            (_, _, _, _, _) => true, canFollowProfile: true);

    private static LaptopService Service(IDynamicLightingFactory? factory, out FakeSettingsStore store)
    {
        var device = new FakeDevice
        {
            Lighting = new FakeRgbDevice { ProfileFollowKey = "follow", Zones = [Keyboard(), Lightbar()] },
        };
        store = new FakeSettingsStore();
        return new LaptopService(device, store, device.DeclaredSettings, factory);
    }

    /// <summary>The whole point of the seam: what composition hands over is what the service publishes, and
    /// switching it on reaches the OS transport.</summary>
    [Fact]
    public void TheFactorysSurfaceIsWhatTheServicePublishes()
    {
        var transport = new FakeLampArrayTransport();
        var svc = Service(new LampArrayBridgeFactory(transport), out var store);
        using (svc)
        {
            var lamps = svc.LampArray;
            Assert.NotNull(lamps);
            // Built, not published: the surface is created on first read, and creating it must not open the
            // driver channel on its own — that is Enable's job, and it waits for the user's setting.
            Assert.False(lamps.Enabled);
            Assert.Equal(0, transport.StartCalls);

            var (ok, error) = svc.SetDynamicLighting(true);

            Assert.True(ok);
            Assert.Null(error);                               // no failure to report, so no LastError to read
            Assert.True(lamps.Enabled);
            Assert.Equal(1, transport.StartCalls);
            Assert.True(store.Settings.DynamicLighting);      // the choice is persisted
        }
    }

    /// <summary>The zones the transport is told about are the device's, MINUS the ones the app may not drive.
    /// The filter is the service's own (<c>ZoneAvailableToHost</c>, handed to the factory), so this pins the
    /// include delegate's wiring too: a factory that dropped it would publish the lightbar while the firmware
    /// owns it, and the host would paint a zone the app has just handed over.</summary>
    [Fact]
    public void TheSurfaceIsBuiltOverTheZonesTheAppMayDrive()
    {
        var transport = new FakeLampArrayTransport();
        var svc = Service(new LampArrayBridgeFactory(transport), out _);
        using (svc)
        {
            svc.SetDeviceFlag("follow", true);                // the lightbar is the firmware's now

            var (ok, _) = svc.SetDynamicLighting(true);

            Assert.True(ok);
            var layout = transport.StartedLayout;
            Assert.NotNull(layout);
            Assert.Equal(["Kbd"], layout.Zones.Select(z => z.Name));
        }
    }

    /// <summary>The other half, and the one every other fixture in the suite relies on: with no factory (no
    /// transport for this OS, or no driver installed) there is no surface, so the Options row cannot appear and
    /// the switch has no outcome to report.</summary>
    [Fact]
    public void WithoutAFactoryThereIsNoSurfaceAndNothingToSwitch()
    {
        var svc = Service(null, out _);
        using (svc)
        {
            Assert.Null(svc.LampArray);
            Assert.Equal((false, (string?)null), svc.SetDynamicLighting(true));
        }
    }
}
