using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
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
///
/// WHAT CHANGED HERE, AND WHY THE GUARDS COULD NOT STAY AS THEY WERE. These tests used to compare the two forms
/// with <c>Assert.Same</c> on the dictionary they returned, and that assertion pinned the ALIASING — the returned
/// dictionary WAS the stored one, which is what the UI held and edited in place. The owner has overruled that
/// design, so the door (<see cref="ILightZoneMode"/>) is a different object every time and the old guard cannot
/// hold. The rule underneath it survives and is what these tests pin now: both forms name THE SAME MODE, seen by
/// writing through one door and reading through the other. That edit is DELIBERATE and forced by the ruling, not
/// a convenience — the same three tests would otherwise have to be deleted.
/// </summary>
public class LightsForCurrentModeTests
{
    /// <summary>The parameterless form is the contract's reference: the overload must key off the SAME mode it
    /// would have read, or a caller that amortized the read would key its lighting off one mode and its presets off
    /// another. The two doors are different OBJECTS — that is the ruling — and they reach one mode's state, which
    /// is the part that matters: a value written through one is the value the other reads back.
    ///
    /// MUTATION THAT REDDENS IT: keying the overload off anything but the profile handed in — e.g. constructing
    /// the door for <c>ModeKey.None</c> — puts the write in a second bucket and the read below finds nothing.</summary>
    [Fact]
    public void WithTheCurrentProfile_BothFormsNameTheSameMode()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        var cur = f.Service.CurrentProfile();

        var withProfile = f.Service.LightsForCurrentMode(cur);
        var withoutProfile = f.Service.LightsForCurrentMode();

        withProfile.Write("keyboard", Zone(37));
        Assert.Equal(37, Assert.Single(withoutProfile.Stored()).Value.Brightness);
        Assert.Single(f.Store.Settings.LightPresets);   // both keyed off "balanced" -> one entry, not two
    }

    /// <summary>Half one: no hidden read. The profile is passed in, so the port is not consulted — this is the
    /// round-trip the step exists to remove, and it is the only thing distinguishing the overload from a
    /// convenience wrapper.
    ///
    /// MUTATION THAT REDDENS IT: making the overload delegate to the parameterless form (which reads the port).</summary>
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
    /// pass while nothing was amortized on the real machine. The state is asked for rather than the bucket, so
    /// the assertion is about the door's actual MODE and not about how a dictionary happens to be stored.
    ///
    /// MUTATION THAT REDDENS IT: as above — the door would be built over "balanced" (the profile the hardware
    /// reports), the write would land there, and the read below would find nothing.</summary>
    [Fact]
    public void WithAProfileThatIsNotTheCurrentOne_ItKeysOffThatProfile()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        var readsBefore = f.Pp!.CurrentCount;

        var performance = f.Service.LightsForCurrentMode(TestProfiles.Performance);

        Assert.Empty(performance.Stored());                          // a mode nobody has configured yet
        Assert.Equal(["performance"], f.Store.Settings.LightPresets.Keys);
        performance.Write("keyboard", Zone(11));
        Assert.Equal(11, f.Store.Settings.LightPresets["performance"].Zones["keyboard"].Brightness);
        Assert.Equal(readsBefore, f.Pp.CurrentCount);   // and it did not read to find that out
    }

    /// <summary>The Turbo fold has to survive the overload: with Turbo-as-a-switch on, Turbo shares its base
    /// profile's key, so a caller that read Turbo and then amortized must land on the BASE's lighting, not on a
    /// "turbo" entry of its own. This is the case where using the wrong half of the pair would be visible to the
    /// user — a switched-to Turbo profile painting the wrong per-zone colours.
    ///
    /// MUTATION THAT REDDENS IT: as on the test above (the door keyed off the raw profile id rather than the
    /// folded mode key).</summary>
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

        withProfile.Write("keyboard", Zone(23));
        Assert.Equal(23, Assert.Single(withoutProfile.Stored()).Value.Brightness);
        Assert.Equal(["balanced"], f.Store.Settings.LightPresets.Keys);   // the base's key, not "turbo"
    }

    /// <summary>An unreadable profile is not an error: both forms key off "default". The UI hits this on a device
    /// with no profiles at all, and the overload must not turn the null into a read of its own.
    ///
    /// MUTATION THAT REDDENS IT: making the overload read the port when its argument is null (the counter
    /// assertion), or keying the null case off anything but "default" (the bucket assertion).</summary>
    [Fact]
    public void WithNoReadableProfile_BothFormsKeyOffDefault()
    {
        var f = LaptopServiceFixture.WithProfiles(current: null);
        var cur = f.Service.CurrentProfile();          // the caller's read — counted, and the last one expected
        var readsBefore = f.Pp!.CurrentCount;

        var mode = f.Service.LightsForCurrentMode(cur);

        Assert.Equal(["default"], f.Store.Settings.LightPresets.Keys);
        Assert.Empty(mode.Stored());
        Assert.Equal(readsBefore, f.Pp.CurrentCount);
    }

    /// <summary>A configured zone, as the value the contract carries.</summary>
    private static LightZoneState Zone(int brightness)
        => new(Configured: true, EffectIndex: 0, Brightness: brightness, Speed: 5, Direction: 1, Color: 0xFF0000,
               ZoneColors: []);
}
