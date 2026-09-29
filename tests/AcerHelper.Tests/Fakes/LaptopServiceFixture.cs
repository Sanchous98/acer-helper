using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// A <see cref="LaptopService"/> wired to the hand-written fakes, with no port assigned until a test asks
/// for one. Ports are read lazily on every call, so a test may assign <c>Device.&lt;Port&gt;</c> after
/// construction, and the service's own constructor does no hardware work.
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

    /// <summary>The moved Curve-Optimizer edit use case over <see cref="Service"/> — the UI's entry point now
    /// that <c>Service.SetCoValues</c> is gone. Tests that exercised that method through the service call this
    /// instead, which is also the point: they exercise the seam, not a service method.</summary>
    public ApplyUndervolt ApplyUndervolt { get; }

    /// <summary>The GPU-offset edit use case over <see cref="Service"/>, standing in for the removed
    /// <c>Service.SetGpuOc</c>. Same rule as <see cref="ApplyUndervolt"/>: the tests exercise the seam.</summary>
    public ApplyGpuOffsets ApplyGpuOffsets { get; }

    /// <summary>The GPU power-level edit use case over <see cref="Service"/>, on the same terms — the tests
    /// exercise the seam rather than a service method.</summary>
    public ApplyGpuPower ApplyGpuPower { get; }

    /// <summary>The CPU power-overlay edit use case over <see cref="Service"/>, standing in for the removed
    /// <c>Service.SetCpuPower</c>.</summary>
    public ApplyCpuPowerOverlay ApplyCpuPower { get; }

    /// <summary>The fan-curve edit use case over <see cref="Service"/>, standing in for the removed
    /// <c>Service.SetFanCurve</c>.</summary>
    public ApplyFanCurve ApplyFanCurve { get; }

    /// <summary>The fan-selection edit use case over <see cref="Service"/>, standing in for the removed
    /// <c>Service.SetFan</c>.</summary>
    public ApplyFanSelection ApplyFanSelection { get; }

    /// <summary>The declared-setting edit use case over <see cref="Service"/>, standing in for the removed
    /// <c>Service.ApplySetting</c>.</summary>
    public ApplyDeclaredSetting ApplyDeclaredSetting { get; }

    // ---- the shell/action family (Application/Preferences.cs, HardwareToggles.cs, ProfilePower.cs, ModeApply.cs).
    // One property per moved action, over <see cref="Service"/>, so tests exercise the seam rather than a service
    // method that no longer exists. The re-apply path uses the service's OWN use case (lazily built over its
    // reconciler); the others build fresh ones over the same service.

    public ApplyStartupState Startup { get; }
    public EvaluateClamshell EvaluateClamshell { get; }
    public SetTurboToggles SetTurboToggles { get; }
    public SetLanguage SetLanguage { get; }
    public SetClamshell SetClamshell { get; }
    public SetAutostart SetAutostart { get; }
    public SetKeyboardBrightness SetKeyboardBrightness { get; }
    public SetBlueLight SetBlueLight { get; }
    public SetBatteryToggle SetBatteryToggle { get; }
    public SetBatteryChoice SetBatteryChoice { get; }
    public SetSourceProfile SetSourceProfile { get; }
    public SyncPowerSource SyncPowerSource { get; }
    public ApplyCustom ApplyCustom { get; }
    public ApplyModeFan ApplyModeFan { get; }
    public ApplyModeGpuOc ApplyModeGpuOc { get; }
    public ApplyModeCpuPower ApplyModeCpuPower { get; }
    public ApplyModeCo ApplyModeCo { get; }

    // ---- the QUERY family (Application/Queries.cs). One property per moved read, over <see cref="Service"/>,
    // so tests exercise the seam rather than a service method. The service methods still EXIST as internal
    // helpers (this class's own paths call them), but a test that reaches them would be testing the helper and
    // not the read the UI makes, which is what these are for.

    public ReadCurrentProfile CurrentProfile { get; }
    public ReadSelectableProfiles SelectableProfiles { get; }
    public ReadBaseProfile BaseProfile { get; }
    public ReadSourceProfile SourceProfile { get; }
    public ReadFanState FanState { get; }
    public ReadGpuOcState GpuOcState { get; }
    public ReadCoDomains CoDomains { get; }
    public ReadCpuPower CpuPower { get; }
    public ReadSensors Sensors { get; }
    public ReadBatteryInfo Battery { get; }

    /// <summary>Every query use case grouped as the app wires them — the set <c>OptionsAssembler</c> and
    /// <c>AppController</c> take. Rebuilt per access.</summary>
    public AppQueries Queries => new(CurrentProfile, SelectableProfiles, BaseProfile, SourceProfile, FanState,
        GpuOcState, CoDomains, CpuPower, Sensors, Battery);

    /// <summary>The Turbo switch, built over the service's CURRENT switch use case — a property rather than a
    /// field so a test that installs its own <c>ProfileAnnouncer</c> after construction still reaches it (the
    /// service rebuilds its switch when the announcer changes).</summary>
    public SetTurbo SetTurbo => new(Service, Service.ProfileSwitch);

    /// <summary>The performance hotkey, on the same terms as <see cref="SetTurbo"/>.</summary>
    public TogglePerformance TogglePerformance => new(Service, SetTurbo, Service.ProfileSwitch);

    /// <summary>Every action use case grouped as the app wires them, for a test that needs to hand the set to
    /// <c>OptionsAssembler</c> the way <c>AppController.BuildUi</c> does. Rebuilt per access so it carries the
    /// current <see cref="SetTurbo"/>.</summary>
    public AppActions Actions => new(Startup, SetTurbo, TogglePerformance, SetTurboToggles, SetClamshell, SetAutostart,
        SetLanguage, SetKeyboardBrightness, EvaluateClamshell, SyncPowerSource, ApplyCustom, SetSourceProfile,
        SetBatteryToggle, SetBatteryChoice, SetBlueLight);

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
    /// handed in so a test writes its own four facts and its own busctl recorder, and the row, the call and the
    /// refusal are all reachable without a daemon, a GPU or a filesystem.</param>
    /// <remarks><c>internal</c> rather than public because <paramref name="cardwireGpuAccess"/> is an internal
    /// port type: the capability's plumbing stays off the app's public surface (it is a signpost, like
    /// <c>Settings</c>), and a test fake does not need a wider door than the suite that uses it.</remarks>
    internal LaptopServiceFixture(Settings? settings = null, Action<FakeDevice>? declare = null,
                                  CardwireGpuAccessPort? cardwireGpuAccess = null)
    {
        declare?.Invoke(Device);
        Store = new FakeSettingsStore(settings);
        Service = new LaptopService(Device, Store);
        ApplyUndervolt = new ApplyUndervolt(Service, Service);
        ApplyGpuOffsets = new ApplyGpuOffsets(Service);
        ApplyGpuPower = new ApplyGpuPower(Service);
        ApplyCpuPower = new ApplyCpuPowerOverlay(Service);
        ApplyFanCurve = new ApplyFanCurve(Service);
        ApplyFanSelection = new ApplyFanSelection(Service);
        ApplyDeclaredSetting = new ApplyDeclaredSetting(Service);
        Startup = new ApplyStartupState(Service, Service);
        EvaluateClamshell = new EvaluateClamshell(Service);
        SetTurboToggles = new SetTurboToggles(Service);
        SetLanguage = new SetLanguage(Service);
        SetClamshell = new SetClamshell(Service, Service);
        SetAutostart = new SetAutostart(Service);
        SetKeyboardBrightness = new SetKeyboardBrightness(Service);
        SetBlueLight = new SetBlueLight(Service);
        SetBatteryToggle = new SetBatteryToggle(Service);
        SetBatteryChoice = new SetBatteryChoice(Service);
        SetSourceProfile = new SetSourceProfile(Service);
        SyncPowerSource = new SyncPowerSource(Service);
        ApplyCustom = new ApplyCustom(Service);
        ApplyModeFan = new ApplyModeFan(Service);
        ApplyModeGpuOc = new ApplyModeGpuOc(Service);
        ApplyModeCpuPower = new ApplyModeCpuPower(Service);
        ApplyModeCo = new ApplyModeCo(Service);
        // The query use cases, each owning its read through the service. ReadBaseProfile composes the live-profile
        // read with the base rule, exactly as composition wires it.
        CurrentProfile = new ReadCurrentProfile(Service);
        SelectableProfiles = new ReadSelectableProfiles(Service);
        BaseProfile = new ReadBaseProfile(Service, Service);
        SourceProfile = new ReadSourceProfile(Service);
        FanState = new ReadFanState(Service);
        GpuOcState = new ReadGpuOcState(Service);
        CoDomains = new ReadCoDomains(Service);
        CpuPower = new ReadCpuPower(Service);
        Sensors = new ReadSensors(Service);
        Battery = new ReadBatteryInfo(Service);
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
/// the mode it means ("balanced") rather than as an opaque backend byte.
///
/// The profiles carry an id and a label and nothing else, since 2026-09-22: the class each one belongs to and
/// the colours it is painted with are <see cref="TestProfiles.TraitsOf"/> below — this fake machine's own
/// reading of its own table, in the same shape a vendor backend answers in (<see cref="IProfileTraits"/>), and
/// deliberately by ID rather than on the record. A test that hands one of these to a port therefore gets the
/// same kind of answer the real thing would.</summary>
public static class TestProfiles
{
    public static PerformanceProfile Quiet       => new("quiet",       "Quiet");
    public static PerformanceProfile Eco         => new("eco",         "Eco");
    public static PerformanceProfile Balanced    => new("balanced",    "Balanced");
    public static PerformanceProfile Performance => new("performance", "Performance");
    public static PerformanceProfile Turbo       => new("turbo",       "Turbo");

    /// <summary>A fresh array each call; records compare by value, so equality assertions still work.</summary>
    public static PerformanceProfile[] All => [Quiet, Eco, Balanced, Performance, Turbo];

    /// <summary>The canonical profile with the given id.</summary>
    public static PerformanceProfile ById(string id) => All.Single(p => p.Id == id);

    /// <summary>The fake machine's table: which class each of its modes belongs to. No colours — this machine
    /// has no per-profile palette to paint (its profiles are the domain's own fixtures, not a vendor's), and an
    /// unclassified id is <see cref="ProfileTraits.Unknown"/> rather than the nearest of the five.</summary>
    public static ProfileTraits TraitsOf(PerformanceProfile profile) => profile.Id switch
    {
        "quiet"       => new ProfileTraits(ProfileKind.Quiet),
        "eco"         => new ProfileTraits(ProfileKind.Eco),
        "balanced"    => new ProfileTraits(ProfileKind.Balanced),
        "performance" => new ProfileTraits(ProfileKind.Performance),
        "turbo"       => new ProfileTraits(ProfileKind.Turbo),
        _             => ProfileTraits.Unknown,
    };
}
