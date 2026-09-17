using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The Turbo switch's rule, asserted DIRECTLY: <see cref="LaptopService.ModeKeyFor"/> must produce exactly the
/// string the per-mode dictionaries are filed under, for every combination of inputs.
///
/// WHY THIS FILE EXISTS AT ALL, given that <c>LaptopServiceModeTests</c> already pins the service's key
/// thoroughly, with literals of its own (null current, unreadable current, no port, non-Turbo ids, Turbo ×
/// toggles-on/off × base-present/absent, a stale base, and the power-source slot). Those cases go through the
/// SERVICE: each one has to be arranged as state the service can reach — a live slot, a port, a power-source
/// sync — so the branches it can cover are the ones that arrangement produces. Here the four inputs are handed
/// to the rule as arguments, which is what makes a full cross-product writable at all, and it is what lets one
/// theory enumerate "non-Turbo profile with the toggles ON and a base remembered" — a branch the service tests
/// reach but do not pin (`TheTwoOverloadsAgree` has the row, and only compares the service with itself), and the
/// one a dropped kind check moves.
///
/// The cases are enumerated as the cross-product of the four inputs the rule actually depends on: the current
/// profile (readable/not, Turbo or not), the Turbo-toggles setting, the remembered base of the live slot, and
/// WHICH slot is live. The expected values are the literal strings the settings file already contains — not
/// what the rule returns — so a change of rule shows up as a changed expectation rather than as agreement.
///
/// THE RULE MOVED HERE FROM Domain/ModeKey (2026-09-17), which is why the calls below name the service's static
/// rule instead of a domain factory: the sharing branch reads the Turbo switch, which the domain does not own.
/// The expected values did NOT move with it — that is what makes the relocation a relocation rather than a
/// rewrite, and it is the one thing this file has to say that the service-level tests cannot.
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
    /// the rule is a faithful COPY of whatever else answers; the literal is what pins the rule itself.</summary>
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
        Assert.Equal(expected, LaptopService.ModeKeyFor(cur, f.Service.TurboToggles, settings.OnAc).Value);
        // The service's own answer against the rule fed the same slot. Since the service CALLS this rule, the
        // derivation is agreed by construction; what this line can still catch is the slot the service chose —
        // no reading has happened here, so the live slot is the AC one and the assertion is on that choice.
        Assert.Equal(f.Service.CurrentModeKey(cur), LaptopService.ModeKeyFor(cur, f.Service.TurboToggles, settings.OnAc).Value);
    }

    /// <summary>The same agreement on the BATTERY slot, which is the one case where the two could plausibly
    /// disagree about WHICH remembered base they are reading: the slot is chosen by the live power source, and
    /// a rule handed the wrong <see cref="ProfileMemory"/> would produce a perfectly well-formed key for the
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

        Assert.Equal(f.Service.CurrentModeKey(cur), LaptopService.ModeKeyFor(cur, true, settings.OnBattery).Value);
        Assert.NotEqual(f.Service.CurrentModeKey(cur), LaptopService.ModeKeyFor(cur, true, settings.OnAc).Value);   // not the AC one
    }

    /// <summary>The "ghost" case, and the reason the rule deliberately does NOT validate: a remembered base the
    /// device no longer offers still keys the presets. That is today's behaviour, pinned as a fact in
    /// <c>LaptopServiceModeTests</c>; what is added here is that the rule reproduces it rather than quietly
    /// repairing it — a rule that fell back to a known profile would move which preset reaches the hardware,
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
        Assert.Equal("ghost", LaptopService.ModeKeyFor(cur, true, settings.OnAc).Value);
    }

    /// <summary>The sentinel itself: one instance, the same string the settings file has always used, and what
    /// the rule answers when there is no profile to name — including when the switch is on and a base is
    /// remembered, because there is no profile to key at all.</summary>
    [Fact]
    public void NoneIsTheSentinelTheSettingsFileAlreadyContains()
    {
        Assert.Equal("default", ModeKey.None.Value);
        Assert.Equal("default", ModeKey.None.ToString());
        Assert.Equal(ModeKey.None, LaptopService.ModeKeyFor(null, turboToggles: true, new ProfileMemory { BaseId = "quiet" }));
        Assert.Equal(ModeKey.None, LaptopService.ModeKeyFor(null, turboToggles: false, new ProfileMemory()));
    }

    /// <summary>The other half of the relocation, and the reason it needs its own assertion: the DOMAIN must
    /// not know the switch. Handed a Turbo profile and nothing else, <see cref="ModeKey.For"/> answers that
    /// profile's own id — the sharing branch is not reachable from <c>Domain/</c> at all, because the setting
    /// and the slot that decide it are not there. Re-adding the branch to the domain type reddens this.</summary>
    [Fact]
    public void TheDomainTypeDoesNotKnowTheSwitch_ATurboProfileKeysByItsOwnId()
    {
        Assert.Equal("turbo", ModeKey.For(TestProfiles.Turbo).Value);
        Assert.Equal(ModeKey.None, ModeKey.For(null));
    }
}
