using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// Wave 6, step 4 — the reads AROUND the ones the wave deferred. The startup path read the current hardware
/// profile three times before this step (once for <c>BuildUi</c>'s lighting lookup, once for the <c>_lastModeKey</c>
/// seed, once for the follow-lighting hand-off), and the refresh pass claimed to read it once and did not: the
/// lighting lookup inside it re-read, hidden behind <c>LightsForCurrentMode()</c>'s parameterless form.
/// <see cref="LaptopService.LightsForCurrentMode(PerformanceProfile?)"/> is the missing half of the pair whose
/// other half (<see cref="LaptopService.CurrentModeKey(PerformanceProfile?)"/>) already existed for exactly this
/// reason.
///
/// Two halves, and both are needed — neither alone says anything:
/// <list type="number">
/// <item>the overload does NOT read the port (asserted with the fake's read counter, which stands in for the EC
/// round-trip); and</item>
/// <item>it uses the profile it was HANDED, not the one it could have read. Without this the first half is
/// satisfied by an overload that ignores its parameter — a false green that a mutation check confirms is real.</item>
/// </list>
/// </summary>
public class LightsForCurrentModeTests
{
    /// <summary>The parameterless form is the contract's reference: the overload must key off the SAME profile it
    /// would have read, or a caller that amortized the read would key its lighting off one mode and its presets off
    /// another. Same instance, not merely an equal one — the whole design rests on the zones dict being shared.</summary>
    [Fact]
    public void WithTheCurrentProfile_ItIsTheSameInstanceTheParameterlessCallReturns()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        var cur = f.Service.CurrentProfile();

        var withProfile = f.Service.LightsForCurrentMode(cur);
        var withoutProfile = f.Service.LightsForCurrentMode();

        Assert.Same(withoutProfile, withProfile);
        Assert.Single(f.Store.Settings.LightPresets);   // both keyed off "balanced" -> one entry, not two
    }

    /// <summary>Half one: no hidden read. The profile is passed in, so the port is not consulted — this is the
    /// round-trip the step exists to remove, and it is the only thing distinguishing the overload from a
    /// convenience wrapper.</summary>
    [Fact]
    public void WithAProfileInHand_ThePortIsNotRead()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        var cur = f.Service.CurrentProfile();
        var readsBefore = f.Pp!.CurrentCount;

        _ = f.Service.LightsForCurrentMode(cur);

        Assert.Equal(readsBefore, f.Pp.CurrentCount);
    }

    /// <summary>Half two: the argument is USED. A profile that is deliberately not the current one must key the
    /// lookup — otherwise the overload could quietly be the parameterless one, and the test above would still
    /// pass while nothing was amortized on the real machine.</summary>
    [Fact]
    public void WithAProfileThatIsNotTheCurrentOne_ItKeysOffThatProfile()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        var readsBefore = f.Pp!.CurrentCount;

        var zones = f.Service.LightsForCurrentMode(TestProfiles.Performance);

        Assert.Equal(["performance"], f.Store.Settings.LightPresets.Keys);
        Assert.Same(f.Store.Settings.LightPresets["performance"].Zones, zones);
        Assert.Equal(readsBefore, f.Pp.CurrentCount);   // and it did not read to find that out
    }

    /// <summary>The Turbo fold has to survive the overload: with Turbo-as-a-switch on, Turbo shares its base
    /// profile's key, so a caller that read Turbo and then amortized must land on the BASE's lighting, not on a
    /// "turbo" entry of its own. This is the case where using the wrong half of the pair would be visible to the
    /// user — a switched-to Turbo profile painting the wrong per-zone colours.</summary>
    [Fact]
    public void WithTurboTogglesOn_TheOverloadFoldsTurboToItsBase_ExactlyAsTheParameterlessFormDoes()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { TurboToggles = true },
            current: TestProfiles.Balanced);
        f.Service.ApplyProfile(TestProfiles.Balanced);          // remember Balanced as the base for this source
        f.Pp!.CurrentProfile = TestProfiles.Turbo;              // ...and Turbo is now what the hardware reports
        var cur = f.Service.CurrentProfile();

        var withProfile = f.Service.LightsForCurrentMode(cur);
        var withoutProfile = f.Service.LightsForCurrentMode();

        Assert.Same(withoutProfile, withProfile);
        Assert.Equal(["balanced"], f.Store.Settings.LightPresets.Keys);   // the base's key, not "turbo"
    }

    /// <summary>An unreadable profile is not an error: both forms key off "default". The UI hits this on a device
    /// with no profiles at all, and the overload must not turn the null into a read of its own.</summary>
    [Fact]
    public void WithNoReadableProfile_BothFormsKeyOffDefault()
    {
        var f = LaptopServiceFixture.WithProfiles(current: null);
        var cur = f.Service.CurrentProfile();          // the caller's read — counted, and the last one expected
        var readsBefore = f.Pp!.CurrentCount;

        var zones = f.Service.LightsForCurrentMode(cur);

        Assert.Equal(["default"], f.Store.Settings.LightPresets.Keys);
        Assert.Empty(zones);
        Assert.Equal(readsBefore, f.Pp.CurrentCount);
    }
}
