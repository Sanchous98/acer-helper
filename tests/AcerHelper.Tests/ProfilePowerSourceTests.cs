using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// Profile availability by POWER SOURCE — the NitroSense parity the owner asked for: on battery only Eco and
/// Balanced, on AC Quiet, Balanced, Performance and Turbo. The policy is the vendor's, declared as data
/// (<see cref="IProfileAvailability"/>) and applied here, in <see cref="LaptopService"/> — which is what keeps
/// it testable without the laptop and keeps a backend that declares no policy (ASUS, Dell, the generic ports)
/// exactly as it was.
///
/// WHAT EACH GROUP PINS:
/// <list type="bullet">
/// <item>the SELECTABLE set for each source, and that it updates live when the source changes;</item>
/// <item>what happens when the remembered or current profile is not offered on the new source: the machine is
/// moved to the fallback (Balanced) and the fallback is remembered, matching NitroSense's own behaviour —
/// the dead end (staying in a disabled mode) is the bug this closes;</item>
/// <item>the writes are REFUSED, not silently applied, for a disabled profile (the tray/hotkey backstop);</item>
/// <item>the hotkey cycle runs over the filtered set;</item>
/// <item>a port with no policy is not gated at all (the non-Acer path).</item>
/// </list>
///
/// The fake's policy is assigned in each test; <c>FakePowerProfiles.Available == null</c> (the default, and
/// what every pre-existing test uses) means "everything on both sources", so nothing else in the suite moves.
/// </summary>
public class ProfilePowerSourceTests
{
    private static readonly BatteryInfoSnapshot OnAc = new() { State = BatteryState.Charging };
    private static readonly BatteryInfoSnapshot OnBattery = new() { State = BatteryState.Discharging };

    /// <summary>The Acer table's policy, expressed over the canonical fake's ids by kind. It is the same rule
    /// <c>AcerProfiles.IsAvailable</c> states for the vendor table; spelled here so a service-level test can
    /// stand in for an Acer port without the Acer decorators.</summary>
    private static Func<PerformanceProfile, bool, bool> Policy => (p, onAc) => TestProfiles.TraitsOf(p).Kind switch
    {
        ProfileKind.Balanced                                          => true,
        ProfileKind.Eco                                               => !onAc,
        ProfileKind.Quiet or ProfileKind.Performance or ProfileKind.Turbo => onAc,
        _                                                             => false,
    };

    private static LaptopServiceFixture Gated(Settings? settings = null, PerformanceProfile? current = null)
    {
        var f = LaptopServiceFixture.WithProfiles(settings, current: current);
        f.Device.PowerProfiles = new GatedProfiles(f.Pp!, Policy);
        return f;
    }

    /// <summary>A port that DECLARES a per-source policy over a canonical fake, forwarding everything else —
    /// the shape <c>AcerMappedProfiles</c>/<c>ProfilesPort</c> take in production. The fake underneath still
    /// records the writes, so assertions read <c>f.Pp</c>.</summary>
    private sealed class GatedProfiles(IPowerProfiles inner, Func<PerformanceProfile, bool, bool> policy)
        : IPowerProfiles, IProfileTraits, IProfileAvailability
    {
        public string? LastError => inner.LastError;
        public IReadOnlyList<PerformanceProfile> All => inner.All;
        public IReadOnlyList<PerformanceProfile> Selectable() => inner.Selectable();
        public PerformanceProfile? Current() => inner.Current();
        public bool Set(PerformanceProfile profile) => inner.Set(profile);
        public ProfileTraits Traits(PerformanceProfile profile) => ProfileTraits.Of(inner, profile);
        public IReadOnlyList<PerformanceProfile> AvailableOn(bool onAc)
            => inner.All.Where(p => policy(p, onAc)).ToList();
    }

    private static string[] Selectable(LaptopServiceFixture f)
        => [.. f.Service.SelectableProfiles().Select(p => p.Id)];

    // ---- the availability sets ----

    [Fact]
    public void OnBattery_OnlyEcoAndBalancedAreSelectable()
    {
        var f = Gated(current: TestProfiles.Balanced);

        f.Service.SyncPowerSource(OnBattery);

        Assert.Equal(["eco", "balanced"], Selectable(f));
    }

    [Fact]
    public void OnAc_QuietBalancedPerformanceAndTurboAreSelectable()
    {
        var f = Gated(current: TestProfiles.Balanced);

        f.Service.SyncPowerSource(OnAc);

        Assert.Equal(["quiet", "balanced", "performance", "turbo"], Selectable(f));
    }

    /// <summary>The source change updates the offered set in the same pass — no rebuild, no second watcher.</summary>
    [Fact]
    public void ChangingThePowerSource_UpdatesTheSelectableSetLive()
    {
        var f = Gated(current: TestProfiles.Balanced);

        f.Service.SyncPowerSource(OnBattery);
        Assert.Equal(["eco", "balanced"], Selectable(f));

        f.Service.SyncPowerSource(OnAc);
        Assert.Equal(["quiet", "balanced", "performance", "turbo"], Selectable(f));
    }

    /// <summary>The port's own selectable subset is intersected with the policy, not replaced by it: a device
    /// that lists no Performance still has no Performance on AC.</summary>
    [Fact]
    public void ThePowerSourcePolicy_IsIntersectedWithThePortsOwnSelectableSubset()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced,
                                                  selectable: [TestProfiles.Eco, TestProfiles.Balanced, TestProfiles.Performance]);
        f.Device.PowerProfiles = new GatedProfiles(f.Pp!, Policy);

        f.Service.SyncPowerSource(OnBattery);

        Assert.Equal(["eco", "balanced"], Selectable(f));
    }

    // ---- a profile the source does not offer: fall back and apply ----

    /// <summary>A remembered battery mode that the battery does not offer (here written while on AC, or left
    /// by an older settings file) is replaced by Balanced and applied, and the slot is rewritten so it cannot
    /// keep pointing at a disabled profile.</summary>
    [Fact]
    public void AnUnavailableRememberedProfile_OnTheLiveSource_FallsBackAndApplies()
    {
        var f = Gated(new Settings { OnBattery = new ProfileMemory { BaseId = "performance" } },
                      current: TestProfiles.Performance);

        f.Service.SyncPowerSource(OnBattery);

        Assert.Equal(["balanced"], f.Pp!.SetCallIds);
        Assert.Equal("balanced", f.Store.Settings.OnBattery.BaseId);
        Assert.False(f.Store.Settings.OnBattery.Turbo);
    }

    /// <summary>The first sight of a source while the machine sits in a mode that source disables (Turbo on
    /// battery) moves it to the fallback and remembers Balanced — NitroSense's own behaviour.</summary>
    [Fact]
    public void AFirstSightInAnUnavailableMode_MovesToTheFallback()
    {
        var f = Gated(new Settings { TurboToggles = true }, current: TestProfiles.Turbo);

        f.Service.SyncPowerSource(OnBattery);

        Assert.Equal(["balanced"], f.Pp!.SetCallIds);
        Assert.Equal("balanced", f.Store.Settings.OnBattery.BaseId);
        Assert.False(f.Store.Settings.OnBattery.Turbo);
    }

    /// <summary>A source we have seen before is restored as usual, but a remembered mode the source forbids is
    /// again replaced rather than applied.</summary>
    [Fact]
    public void ReturningToBatteryWithATurboMode_AppliesTheFallback()
    {
        var f = Gated(current: TestProfiles.Turbo);
        f.Service.SyncPowerSource(OnAc);               // seen AC first; the machine is in Turbo
        f.Store.Settings.OnBattery.BaseId = "turbo";   // a later edit/older file left Turbo in the battery slot

        f.Service.SyncPowerSource(OnBattery);

        Assert.Equal(["balanced"], f.Pp!.SetCallIds);  // moved off the disabled Turbo...
        Assert.Equal("balanced", f.Store.Settings.OnBattery.BaseId);
    }

    // ---- the write backstops ----

    [Fact]
    public void ApplyProfile_RefusesAProfileTheSourceDisables()
    {
        var f = Gated(current: TestProfiles.Balanced);
        f.Service.SyncPowerSource(OnBattery);

        var r = f.Service.ApplyProfile(TestProfiles.Turbo);

        Assert.False(r.ok);
        Assert.Empty(f.Pp!.SetCalls);              // nothing was written
    }

    [Fact]
    public void SetTurboOn_OnBattery_DoesNothing()
    {
        var f = Gated(new Settings { TurboToggles = true }, current: TestProfiles.Balanced);
        f.Service.SyncPowerSource(OnBattery);

        var r = f.Service.SetTurbo(true);

        Assert.Null(r.applied);
        Assert.Empty(f.Pp!.SetCalls);
    }

    /// <summary>The hotkey cycles over the filtered set, so it can never land on a profile the live source
    /// disables — on battery the step from Balanced lands on Eco, skipping Performance/Turbo/Quiet.</summary>
    [Fact]
    public void TogglePerformance_CyclesOnlyTheAvailableProfiles()
    {
        var f = Gated(current: TestProfiles.Balanced);
        f.Service.SyncPowerSource(OnBattery);

        Assert.Equal(TestProfiles.Eco, f.Service.TogglePerformance());
    }

    // ---- the per-source read side ----

    /// <summary>The per-source row in Options reads through <see cref="LaptopService.SourceProfile"/>: a slot
    /// holding a profile the source disables (Turbo, in the battery slot) reports the fallback, so the row shows
    /// what the machine would actually use rather than a mode the source forbids. Nothing remembered still
    /// reports null — the read side does not invent a value.</summary>
    [Fact]
    public void SourceProfile_ReportsTheFallback_ForASlotTheSourceDisables()
    {
        var f = Gated(new Settings { OnBattery = new ProfileMemory { BaseId = "turbo" } },
                      current: TestProfiles.Balanced);

        Assert.Equal(TestProfiles.Balanced, f.Service.SourceProfile(false));   // battery forbids Turbo -> Balanced
        Assert.Null(f.Service.SourceProfile(true));                            // AC has nothing remembered
    }

    // ---- the capability is optional ----

    /// <summary>A port that does NOT declare a policy is not gated: the ASUS/generic shape returns its own set
    /// whatever the source. <see cref="FakeThrowingPowerProfiles"/> implements only
    /// <see cref="IPowerProfiles"/>, so it stands in for exactly that non-Acer path.</summary>
    [Fact]
    public void APortWithNoAvailabilityPolicy_IsNotGatedByThePowerSource()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Device.PowerProfiles = new FakeThrowingPowerProfiles();

        f.Service.SyncPowerSource(OnBattery);
        Assert.Equal(TestProfiles.All.Select(p => p.Id), Selectable(f));

        f.Service.SyncPowerSource(OnAc);
        Assert.Equal(TestProfiles.All.Select(p => p.Id), Selectable(f));
    }
}
