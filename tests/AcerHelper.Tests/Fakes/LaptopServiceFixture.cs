using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// A <see cref="LaptopService"/> wired to the hand-written fakes, with no port assigned until a test asks
/// for one. Ports are read lazily on every call, so a test may assign <c>Device.&lt;Port&gt;</c> after
/// construction. <c>LampArray</c> is never built: the fixture passes no lighting factory, so nothing P/Invokes.
/// A test that wants the surface built supplies one — see <c>DynamicLightingSeamTests</c>.
///
/// THE DECLARATIONS ARE MADE BEFORE THE SERVICE EXISTS, through <paramref name="declare"/> — the fake backend's
/// probe, run against the bare device. That order is the contract now: the settings model is CONSTRUCTED with the
/// set of options this machine declares and copies it (Infrastructure/Composition/Settings.cs), so a declaration that lands after the
/// service was built cannot reach the model, exactly as a vendor backend that declared after <c>InitVendor</c>
/// could not. A test that declares too late fails loudly rather than silently — the row it is about is simply
/// absent (<c>AssemblerRows.Toggle</c> finds rows by <c>Single</c>).
///
/// The declared set travels to the model the way composition sends a real device's: <c>DeviceFactory.Create</c>
/// hands the pair to this constructor, which passes the list to <see cref="LaptopService"/>, which passes it to
/// the store's Load — one hand-off, ending in the model's constructor.
/// </summary>
public sealed class LaptopServiceFixture
{
    public FakeDevice Device { get; } = new();
    public FakeSettingsStore Store { get; }
    public LaptopService Service { get; }

    /// <summary>The power-profiles port, when <see cref="Power"/> or <see cref="WithProfiles"/> created one.</summary>
    public FakePowerProfiles? Pp { get; private set; }

    /// <param name="settings">The values to start from, as a file would supply them.</param>
    /// <param name="declare">The fake backend's probe: whatever this does to the device happens BEFORE the
    /// service — and so before the settings model — is built. This is where <see cref="FakeDevice.Declare"/>
    /// calls belong.</param>
    public LaptopServiceFixture(Settings? settings = null, Action<FakeDevice>? declare = null)
    {
        declare?.Invoke(Device);
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
                                                    PerformanceProfile? current = null,
                                                    Action<FakeDevice>? declare = null)
    {
        var fixture = new LaptopServiceFixture(settings, declare);
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
