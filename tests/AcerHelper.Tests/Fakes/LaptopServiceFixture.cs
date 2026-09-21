using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Vendors.Generic;

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
    /// <param name="cardwireGpuAccess">The machine's cardwire capability, when a test needs one that is not this
    /// host's. The service builds the real port from the OS host otherwise (<c>CardwireGpuAccessHost.Create</c>),
    /// and on this build that port's facts say "not Linux" — which is the truth about the Windows TFM the suite
    /// compiles, and useless for a test about what happens when there IS something to ask for. So the port is
    /// handed in, exactly as <c>LampArray</c>'s factory is (see <c>DynamicLightingSeamTests</c>): a test writes
    /// its own four facts and its own busctl recorder, and the row, the call and the refusal are all reachable
    /// without a daemon, a GPU or a filesystem.</param>
    /// <remarks><c>internal</c> rather than public because <paramref name="cardwireGpuAccess"/> is an internal
    /// port type: the capability's plumbing stays off the app's public surface (it is a signpost, like
    /// <c>Settings</c>), and a test fake does not need a wider door than the suite that uses it.</remarks>
    internal LaptopServiceFixture(Settings? settings = null, Action<FakeDevice>? declare = null,
                                  CardwireGpuAccessPort? cardwireGpuAccess = null)
    {
        declare?.Invoke(Device);
        Store = new FakeSettingsStore(settings);
        Service = new LaptopService(Device, Store);
        if (cardwireGpuAccess != null) Service.CardwireGpuAccess = cardwireGpuAccess;
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
