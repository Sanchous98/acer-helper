using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="IPowerProfiles"/>. Holds <see cref="All"/> and <see cref="Selectable"/> as two
/// INDEPENDENT lists, because the whole point of the profile-cycling rule is that they can disagree (Turbo
/// drops out of <c>Selectable</c> on battery while staying in <c>All</c>).
///
/// <see cref="Current"/> is whatever the last successful <see cref="Set"/> landed, so a test can drive a
/// whole AC/DC flow through the service and let the fake behave like the hardware would.
/// </summary>
public sealed class FakePowerProfiles : IPowerProfiles
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

    /// <summary>What <see cref="Current"/> reports. Set it directly to arrange a scenario.</summary>
    public PerformanceProfile? CurrentProfile { get; set; }

    public PerformanceProfile? Current() => CurrentProfile;

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
