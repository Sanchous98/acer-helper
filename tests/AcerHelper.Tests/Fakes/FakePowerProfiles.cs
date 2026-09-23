using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="IPowerProfiles"/>. Holds <see cref="All"/> and <see cref="Selectable"/> as two
/// INDEPENDENT lists, because the whole point of the profile-cycling rule is that they can disagree (Turbo
/// drops out of <c>Selectable</c> on battery while staying in <c>All</c>).
///
/// <see cref="Current"/> is whatever the last successful <see cref="Set"/> landed, so a test can drive a
/// whole AC/DC flow through the service and let the fake behave like the hardware would.
///
/// IT ALSO CLASSIFIES ITS OWN PROFILES (<see cref="IProfileTraits"/>), because a real port does: the class each
/// mode belongs to is the backend's table's answer, not the profile's, since 2026-09-22. The default is
/// <see cref="TestProfiles.TraitsOf"/> — the canonical id→kind table — and a test that stands this fake in for
/// a source with a different reading of the same ids (the kernel's, where "performance" is Performance rather
/// than Turbo) assigns <see cref="TraitsOf"/> itself.
/// </summary>
public sealed class FakePowerProfiles : IPowerProfiles, IProfileTraits
{
    private readonly List<PerformanceProfile> _all;
    private readonly List<PerformanceProfile> _selectable;

    /// <param name="all">The full profile set, in display order.</param>
    /// <param name="selectable">The selectable subset. Null means "the whole of <paramref name="all"/>"
    /// (the normal device); pass an EMPTY list to exercise the "reports no subset" branch.</param>
    /// <param name="current">What the hardware reports as active right now, or null for "unreadable".</param>
    public FakePowerProfiles(IEnumerable<PerformanceProfile> all,
                             IEnumerable<PerformanceProfile>? selectable = null,
                             PerformanceProfile? current = null)
    {
        _all = [.. all];
        _selectable = selectable == null ? [.. _all] : [.. selectable];
        CurrentProfile = current;
    }

    public IReadOnlyList<PerformanceProfile> All => _all;
    public IReadOnlyList<PerformanceProfile> Selectable() => _selectable;

    /// <summary>This fake machine's reading of a profile it offers. Defaults to the canonical table
    /// (<see cref="TestProfiles.TraitsOf"/>), which is what a source spelling its ids "quiet"/"balanced"/… would
    /// answer; assign it to stand in for a source that classifies the same ids differently.</summary>
    public Func<PerformanceProfile, ProfileTraits> TraitsOf { get; set; } = TestProfiles.TraitsOf;

    public ProfileTraits Traits(PerformanceProfile profile) => TraitsOf(profile);

    /// <summary>What <see cref="Current"/> reports. Set it directly to arrange a scenario.</summary>
    public PerformanceProfile? CurrentProfile { get; set; }

    /// <summary>How many times <see cref="Current"/> has been read. On the real port each read is an EC
    /// round-trip, so a test uses this to assert that a caller which ALREADY has the profile does not read it
    /// again — the whole point of the <c>CurrentModeKey(cur)</c> / <c>LightsForCurrentMode(cur)</c> pair.</summary>
    public int CurrentCount { get; private set; }

    public PerformanceProfile? Current()
    {
        CurrentCount++;
        return CurrentProfile;
    }

    public string? LastError { get; set; }

    /// <summary>When false, <see cref="Set"/> records the call, leaves <see cref="CurrentProfile"/>
    /// unchanged and returns false — the EC-refused case.</summary>
    public bool SetResult { get; set; } = true;

    /// <summary>Every profile handed to <see cref="Set"/>, in order — including refused ones.</summary>
    public List<PerformanceProfile> SetCalls { get; } = [];

    /// <summary>The ids of <see cref="SetCalls"/>, for readable assertions and failure messages.</summary>
    public List<string> SetCallIds => [.. SetCalls.Select(p => p.Id)];

    public bool Set(PerformanceProfile profile)
    {
        SetCalls.Add(profile);
        if (!SetResult) return false;
        CurrentProfile = profile;
        return true;
    }
}
