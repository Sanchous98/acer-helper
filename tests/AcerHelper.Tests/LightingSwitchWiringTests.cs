using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The two things the lighting SWITCH does beyond calling the bridge, and neither is visible to a stub: whether
/// the choice reaches the settings file, and what is filed when publishing fails.
///
/// WHY THESE EXIST. <c>AppliedEditUseCasesTests</c> pins the rule through <c>ILightingSwitchTarget</c> with a
/// stub, and <c>DynamicLightingSeamTests</c> pins that a factory's surface is what gets published — but the seam
/// tests read the stored value off the LIVE graph, which the service mutates in memory, so "the choice is
/// persisted" was asserted by nothing: deleting the <c>Save()</c> from <c>ILightingSwitchTarget.Store</c> left
/// the WHOLE suite green, measured before this file existed. The user's switch would then be applied, shown as
/// on, and silently gone after a restart.
///
/// No hardware: the transport is <see cref="FakeLampArrayTransport"/> and the device is the hand-written
/// <see cref="FakeRgbDevice"/>. The one live thread is the bridge's worker, which the service's own
/// <c>Dispose</c> tears down.
/// </summary>
public class LightingSwitchWiringTests
{
    private static RgbZone Keyboard() =>
        new("Kbd", 4, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: 0x02)],
            (_, _, _, _, _) => true, (_, _, _) => true);

    private static LaptopService Service(IReadOnlyList<RgbZone> zones, out FakeSettingsStore store)
    {
        var device = new FakeDevice { Lighting = new FakeRgbDevice { Zones = zones } };
        store = new FakeSettingsStore();
        return new LaptopService(device, store, new LampArrayBridgeFactory(new FakeLampArrayTransport()));
    }

    /// <summary>Switching the surface on PERSISTS the choice, in the same breath as publishing it. The value
    /// being right in the live graph is not the same claim: the graph is what the running app reads, and the file
    /// is what the next boot reads.</summary>
    [Fact]
    public void TheLightingChoiceReachesTheFile()
    {
        var svc = Service([Keyboard()], out var store);
        using (svc)
        {
            var (ok, _) = svc.SetDynamicLighting(true);

            Assert.True(ok);
            Assert.Equal(1, store.SaveCount);
            Assert.True(store.LastSaved!.DynamicLighting);
        }
    }

    /// <summary>A publish that did NOT take is filed as OFF, and the caller is told why. The zone below carries no
    /// effects, so the layout it builds has no lamps and the bridge cannot publish it — which is the reachable
    /// shape of "the driver is there but the surface cannot come up". The stored false is what makes the Options
    /// row snap back on its next read instead of claiming a device that is not there.</summary>
    [Fact]
    public void APublishThatDidNotTake_IsFiledAsOff_AndTheFailureIsReported()
    {
        var svc = Service([], out var store);
        using (svc)
        {
            var (ok, error) = svc.SetDynamicLighting(true);

            Assert.False(ok);
            Assert.NotNull(error);                            // the bridge's own words, which the row shows
            Assert.False(store.Settings.DynamicLighting);      // filed as what the surface IS, not what was asked
            Assert.Equal(1, store.SaveCount);                  // ...and the correction reaches the file too
        }
    }
}
