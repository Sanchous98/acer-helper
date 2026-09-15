using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The differential test for <see cref="ModeKey"/>: the pure factory must produce EXACTLY the string
/// <see cref="LaptopService.CurrentModeKey()"/> produces, for every combination of inputs.
///
/// WHY THIS FILE EXISTS AT ALL, given that <c>LaptopServiceModeTests</c> already pins the service's key
/// thoroughly (null current, unreadable current, no port, non-Turbo ids, Turbo × toggles-on/off ×
/// base-present/absent, a stale base, and the power-source slot). Those tests protect the SERVICE. What they
/// cannot see is the second implementation: once the derivation exists in two places, the two can drift, and
/// the drift is silent — the per-mode dictionaries are keyed by whatever the service returns, while anything
/// holding a <see cref="ModeKey"/> would file under the factory's answer instead. The two would then disagree
/// about which mode the user is configuring, which is exactly the failure the pair of overloads was split to
/// avoid in the first place.
///
/// WRITTEN BEFORE THE SERVICE DELEGATED, on purpose. Every case below ran green while the service still owned
/// the derivation, so a green run proves the factory is a faithful COPY rather than merely a self-consistent
/// one. That ordering is the whole value: after the swap, a test like this can only prove the service agrees
/// with itself.
///
/// The cases are enumerated as the cross-product of the four inputs the rule actually depends on: the current
/// profile (readable/not, Turbo or not), the Turbo-toggles setting, the remembered base of the live slot, and
/// WHICH slot is live. The expected values are the literal strings the settings file already contains — not
/// what the factory returns — so a change of rule shows up as a changed expectation rather than as agreement.
/// </summary>
public class ModeKeyTests
{
    /// <summary>Every combination that reaches a different branch of the rule. <c>currentId</c> null means the
    /// port reports no current profile; empty <c>baseId</c> means the slot remembers nothing.
    ///
    /// <c>expected</c> is written out as a LITERAL and asserted on BOTH sides — that detail is the difference
    /// between a test and a tautology, and it was learned from a mutation: with only the two implementations
    /// compared to each other, a change to the rule moved both answers together and the case stayed green (the
    /// empty-base case below survived the removal of the <c>BaseId.Length</c> clause). An agreement test proves
    /// the factory is a faithful COPY; the literal is what pins the rule itself.</summary>
    [Theory]
    [InlineData(null, false, "", "default")]                 // no readable profile at all      -> "default"
    [InlineData(null, true, "performance", "default")]       // ...and the toggles don't matter -> "default"
    [InlineData("balanced", false, "", "balanced")]          // plain profile, no remembered base
    [InlineData("balanced", true, "performance", "balanced")]// a non-Turbo profile ignores the base
    [InlineData("performance", true, "quiet", "performance")]// ...including Performance
    [InlineData("turbo", false, "performance", "turbo")]     // toggles OFF: Turbo keys by itself
    [InlineData("turbo", true, "performance", "performance")]// toggles ON + a base: Turbo shares the base's key
    [InlineData("turbo", true, "quiet", "quiet")]            // ...whatever the base happens to be
    [InlineData("turbo", true, "", "turbo")]                 // toggles ON but nothing remembered: no base to share
    public void TheFactoryAgreesWithTheServiceOnTheAcSlot(string? currentId, bool turboToggles, string baseId,
                                                          string expected)
    {
        var settings = new Settings { TurboToggles = turboToggles };
        settings.OnAc.BaseId = baseId;   // no power-source reading has happened, so the AC slot is the live one
        var f = LaptopServiceFixture.WithProfiles(settings,
            current: currentId == null ? null : TestProfiles.ById(currentId));

        var cur = f.Service.CurrentProfile();

        Assert.Equal(expected, f.Service.CurrentModeKey(cur));   // the rule, pinned...
        Assert.Equal(expected, ModeKey.For(cur, f.Service.TurboToggles, settings.OnAc).Value);   // ...on both paths
        Assert.Equal(f.Service.CurrentModeKey(cur), ModeKey.For(cur, f.Service.TurboToggles, settings.OnAc).Value);
    }

    /// <summary>The same agreement on the BATTERY slot, which is the one case where the two could plausibly
    /// disagree about WHICH remembered base they are reading: the slot is chosen by the live power source, and a
    /// factory handed the wrong <see cref="ProfileMemory"/> would produce a perfectly well-formed key for the
    /// wrong mode.
    ///
    /// The battery slot remembers Turbo ON with base "quiet", so the AC↔battery sync re-applies nothing (the
    /// machine is already in Turbo) and the current profile stays Turbo for the assertion — the slot's base is
    /// the only thing that decides the key.</summary>
    [Fact]
    public void TheFactoryAgreesWithTheServiceOnTheBatterySlot()
    {
        var settings = new Settings { TurboToggles = true };
        settings.OnAc.BaseId = "performance";
        settings.OnBattery = new ProfileMemory { BaseId = "quiet", Turbo = true };
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Turbo);

        f.Service.SyncPowerSource(new BatteryInfoSnapshot { State = BatteryState.Discharging });

        var cur = f.Service.CurrentProfile();
        Assert.Equal(TestProfiles.Turbo.Id, cur?.Id);          // the sync left the machine in Turbo...
        Assert.Equal("quiet", f.Service.CurrentModeKey(cur));  // ...so the key comes from the battery slot's base

        Assert.Equal(f.Service.CurrentModeKey(cur), ModeKey.For(cur, true, settings.OnBattery).Value);
        Assert.NotEqual(f.Service.CurrentModeKey(cur), ModeKey.For(cur, true, settings.OnAc).Value);   // not the AC one
    }

    /// <summary>The "ghost" case, and the reason this type deliberately does NOT validate: a remembered base the
    /// device no longer offers still keys the presets. That is today's behaviour, pinned as a fact in
    /// <c>LaptopServiceModeTests</c>; what is added here is that the factory reproduces it rather than quietly
    /// repairing it — a factory that fell back to a known profile would move which preset reaches the hardware,
    /// and that is a change of feature, not of type.</summary>
    [Fact]
    public void TheFactoryAgreesOnAStaleBase_ItDoesNotRepairIt()
    {
        var settings = new Settings { TurboToggles = true };
        settings.OnAc = new ProfileMemory { BaseId = "ghost", Turbo = true };
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Turbo);

        var cur = f.Service.CurrentProfile();

        Assert.DoesNotContain(f.Pp!.All, p => p.Id == "ghost");     // the device does not offer it...
        Assert.Equal("ghost", f.Service.CurrentModeKey(cur));       // ...and it is the key anyway
        Assert.Equal("ghost", ModeKey.For(cur, true, settings.OnAc).Value);
    }

    /// <summary>The sentinel itself: one instance, the same string the settings file has always used, and what
    /// the factory answers when there is no profile to name.</summary>
    [Fact]
    public void NoneIsTheSentinelTheSettingsFileAlreadyContains()
    {
        Assert.Equal("default", ModeKey.None.Value);
        Assert.Equal("default", ModeKey.None.ToString());
        Assert.Equal(ModeKey.None, ModeKey.For(null, turboToggles: true, new ProfileMemory { BaseId = "quiet" }));
        Assert.Equal(ModeKey.None, ModeKey.For(null, turboToggles: false, new ProfileMemory()));
    }
}
