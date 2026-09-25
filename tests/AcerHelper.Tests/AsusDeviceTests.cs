using System.Runtime.CompilerServices;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Infrastructure.Vendors.Asus;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The ASUS backend's COMPOSITION AND FALLBACK behaviour, and the one claim that matters most for Phase 1: a
/// machine without asusd must be invisible — every generic port the base <c>GenericDevice</c> wired stays exactly
/// where it was, and an all-null ASUS machine still flows through <c>LaptopService</c> without an exception.
///
/// The wiring decision lives in the un-suffixed <c>AsusWiring</c> for this reason: <c>AsusDevice.Linux.cs</c> is
/// not compiled into the test TFM, so a fallback rule written only there could not be held to anything. The
/// source-text guard at the end pins the one line this suite cannot execute — the composition root's vendor
/// branch — the same technique AcerLinuxWiringTests uses for the Acer Linux wiring.
/// </summary>
public class AsusDeviceTests
{
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    // ---- the ownership policy: asusd replaces, never the reverse ----

    [Theory]
    [InlineData(false, true,  true,  false, false)]   // asusd not on the bus: nothing is taken over
    [InlineData(true,  false, true,  false, true)]    // on the bus; profiles unusable, charge still taken
    [InlineData(true,  true,  false, true,  false)]   // on the bus; charge unusable, profiles still taken
    [InlineData(true,  true,  true,  true,  true)]    // fully usable: both are taken over
    public void AsusdTakesOverOnlyThePortsItCanDrive(
        bool present, bool profiles, bool charge, bool expectProfiles, bool expectCharge)
    {
        var facts = new AsusdPlatformFacts(present, profiles, charge);

        Assert.Equal(expectProfiles, AsusdOwnership.TakeOverProfiles(facts));
        Assert.Equal(expectCharge, AsusdOwnership.TakeOverChargeLimit(facts));
    }

    /// <summary>THE FALLBACK ITSELF, with ports present but the machine not owning them: the generic port is NOT
    /// displaced. This is the line a half-built vendor port would slip past if the decision lived at the call
    /// site.</summary>
    [Fact]
    public void AnAsusdlessMachineKeepsTheGenericPorts()
    {
        var genericProfiles = new FakePowerProfiles(TestProfiles.All);
        var genericCharge = new BatteryToggle(() => false, _ => (true, null));
        var device = new FakeDevice { PowerProfiles = genericProfiles };
        device.Battery.ChargeLimit = genericCharge;

        // Ports exist (a daemon that just left, say) but asusd is not present: neither may be applied.
        AsusWiring.Apply(device, new AsusdPlatformFacts(false, true, true),
                         new FakePowerProfiles([TestProfiles.Balanced]), new BatteryToggle(() => true, _ => (true, null)));

        Assert.Same(genericProfiles, device.PowerProfiles);
        Assert.Same(genericCharge, device.Battery.ChargeLimit);
    }

    /// <summary>THE OTHER DIRECTION: when asusd is present and its ports usable, both replace the generic ones.
    /// Without this half the policy could be "never take over" and the test above would still pass.</summary>
    [Fact]
    public void AsusdReplacesOnlyWhenItIsPresentAndUsable()
    {
        var device = new FakeDevice();
        var asusProfiles = new FakePowerProfiles([TestProfiles.Balanced]);
        var asusCharge = new BatteryToggle(() => true, _ => (true, null));

        AsusWiring.Apply(device, new AsusdPlatformFacts(true, true, true), asusProfiles, asusCharge);

        Assert.Same(asusProfiles, device.PowerProfiles);
        Assert.Same(asusCharge, device.Battery.ChargeLimit);
    }

    // ---- an all-null machine still serves ----

    /// <summary>
    /// THE PHASE-1 ACCEPTANCE: an ASUS machine where the vendor probe found nothing — asusd absent, every vendor
    /// port null — must behave exactly like the generic backend and never throw. The service is exercised the way
    /// the UI and the refresh loop exercise it, with no ports attached at all.
    /// </summary>
    [Fact]
    public void AnAllNullAsusMachineFlowsThroughTheService()
    {
        var device = new FakeDevice();
        AsusWiring.Apply(device, new AsusdPlatformFacts(false, false, false), null, null);
        var service = new LaptopService(device, new FakeSettingsStore());

        Assert.Null(service.CurrentProfile());
        Assert.Empty(service.SelectableProfiles());
        Assert.Null(service.BaseProfile());
        Assert.False(service.IsTurboOn());
        Assert.Null(service.TogglePerformance());
        Assert.False(service.ApplyProfile(TestProfiles.Balanced).ok);
        service.SyncPowerSource(new BatteryInfoSnapshot { State = BatteryState.Discharging });
    }

    /// <summary>The realistic asusd-less machine: the generic profile port is in place and the service drives it
    /// as it always did. What this rules out is the vendor wiring having broken the inherited port — the base
    /// constructor's fallback is the point of graceful degradation.</summary>
    [Fact]
    public void AnAsusdlessMachineStillServesTheGenericProfiles()
    {
        var device = new FakeDevice();
        var generic = new FakePowerProfiles(TestProfiles.All, current: TestProfiles.Balanced);
        device.PowerProfiles = generic;
        AsusWiring.Apply(device, new AsusdPlatformFacts(false, false, false), null, null);

        var service = new LaptopService(device, new FakeSettingsStore());

        Assert.Equal("balanced", service.CurrentProfile()!.Id);
        Assert.Equal(TestProfiles.All.Select(p => p.Id), service.SelectableProfiles().Select(p => p.Id));
        Assert.True(service.ApplyProfile(TestProfiles.Performance).ok);
        Assert.Equal("performance", generic.SetCallIds.Single());
    }

    // ---- Phase 2: the Aura takeover follows the same one-directional rule ----

    [Theory]
    [InlineData(false, true, false)]   // asusd not present -> no takeover, however usable the device looks
    [InlineData(true, false, false)]   // present but no Aura device/interface -> no takeover
    [InlineData(true, true, true)]     // present and enumerated -> take over
    public void AuraIsTakenOverOnlyWhenPresentAndEnumerated(bool present, bool available, bool taken)
        => Assert.Equal(taken, AsusdOwnership.TakeOverAura(present, available));

    /// <summary>The Lighting slot is filled from the enumerated Aura device and — importantly — an asusd-less
    /// machine's slot is left exactly as the base device left it, even if a stale device object is handed in.</summary>
    [Fact]
    public void TheAuraDeviceFillsLightingOnlyWhenAsusdOwnsIt()
    {
        var aura = new RgbDevice();

        var absent = new FakeDevice();
        AsusWiring.ApplyAura(absent, present: false, auraAvailable: true, aura);
        Assert.Null(absent.Lighting);

        var present = new FakeDevice();
        AsusWiring.ApplyAura(present, present: true, auraAvailable: true, aura);
        Assert.Same(aura, present.Lighting);

        // ...and a present daemon that enumerated no device leaves the slot alone too.
        var empty = new FakeDevice();
        AsusWiring.ApplyAura(empty, present: true, auraAvailable: false, null);
        Assert.Null(empty.Lighting);
    }

    // ---- the composition root's vendor branch (read as text) ----

    /// <summary>The suite compiles the Windows TFM and cannot call the DMI-driven <c>DeviceFactory.Create</c> on
    /// this host, so the one line that decides ASUS gets the backend is pinned by reading it — the technique
    /// AcerLinuxWiringTests states for the same reason. "ASUSTeK COMPUTER INC." contains "ASUS", so one
    /// case-insensitive test covers both DMI spellings.</summary>
    [Fact]
    public void TheCompositionRootPicksTheAsusBackend()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "Infrastructure", "Composition", "DeviceFactory.cs"));

        Assert.Contains("new AsusDevice(product)", source, StringComparison.Ordinal);
        Assert.Contains("\"ASUS\"", source, StringComparison.Ordinal);
    }
}
