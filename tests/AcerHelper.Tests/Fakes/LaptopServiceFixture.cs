using AcerHelper.Application;
using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// A <see cref="LaptopService"/> wired to the hand-written fakes, with no port assigned until a test asks
/// for one. Ports are read lazily on every call, so a test may assign <c>Device.&lt;Port&gt;</c> after
/// construction. <c>LampArray</c> is never built: the fixture passes no transport, so nothing P/Invokes.
/// </summary>
public sealed class LaptopServiceFixture
{
    public FakeDevice Device { get; } = new();
    public FakeSettingsStore Store { get; }
    public LaptopService Service { get; }

    /// <summary>The power-profiles port, when <see cref="Power"/> or <see cref="WithProfiles"/> created one.</summary>
    public FakePowerProfiles? Pp { get; private set; }

    public LaptopServiceFixture(Settings? settings = null)
    {
        Store = new FakeSettingsStore(settings);
        Service = new LaptopService(Device, Store);
    }

    /// <summary>Attach a <see cref="FakePowerProfiles"/> and return it, for arranging and asserting.</summary>
    public FakePowerProfiles Power(IEnumerable<PerformanceProfile> all,
                                   IEnumerable<PerformanceProfile>? selectable = null,
                                   PerformanceProfile? current = null)
    {
        Pp = new FakePowerProfiles(all, selectable, current);
        Device.PowerProfiles = Pp;
        return Pp;
    }

    public static LaptopServiceFixture WithProfiles(Settings? settings = null,
                                                    IEnumerable<PerformanceProfile>? all = null,
                                                    IEnumerable<PerformanceProfile>? selectable = null,
                                                    PerformanceProfile? current = null)
    {
        var fixture = new LaptopServiceFixture(settings);
        fixture.Power(all ?? TestProfiles.All, selectable, current);
        return fixture;
    }
}

/// <summary>The canonical five-profile device. Ids are the lowercase kind name, so an assertion reads as
/// the mode it means ("balanced") rather than as an opaque backend byte.</summary>
public static class TestProfiles
{
    public static PerformanceProfile Quiet       => new("quiet",       "Quiet",       ProfileKind.Quiet);
    public static PerformanceProfile Eco         => new("eco",         "Eco",         ProfileKind.Eco);
    public static PerformanceProfile Balanced    => new("balanced",    "Balanced",    ProfileKind.Balanced);
    public static PerformanceProfile Performance => new("performance", "Performance", ProfileKind.Performance);
    public static PerformanceProfile Turbo       => new("turbo",       "Turbo",       ProfileKind.Turbo);

    /// <summary>A fresh array each call; records compare by value, so equality assertions still work.</summary>
    public static PerformanceProfile[] All => [Quiet, Eco, Balanced, Performance, Turbo];

    /// <summary>The canonical profile with the given id.</summary>
    public static PerformanceProfile ById(string id) => All.Single(p => p.Id == id);
}
