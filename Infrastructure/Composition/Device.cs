using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Composition;

/// <summary>The machine: what this laptop is and what it has, as composition built it. <see cref="DeviceFactory"/>
/// returns one of these, and it is what took the place of <c>IDevice</c> — a MODEL rather than an inventory
/// interface.
///
/// WHY A CLASS AND NOT AN INTERFACE, and the reason is the arrow rather than taste. The interface was declared in
/// <c>Domain/Ports.cs</c> and implemented here, which meant the domain declared an aggregate whose every member
/// was either a vendor technology (<c>IGpuOverclock</c> — Nvidia, <c>ICurveOptimizer</c> — AMD, <c>ICpuPower</c> —
/// the Windows overlay) or a vendor transport. Its use sites say the same thing from the other end: 25 of them,
/// every one in Infrastructure, UI or the tests, and NOT ONE reader in Domain or Application — the reasoning the
/// old comment on the interface gave for keeping it in the domain was not exercised anywhere. So the slots came
/// here, where they are filled, and the composition root now hands over a built machine instead of an interface a
/// caller could still interpret.
///
/// WHAT IS NOT MODELLED HERE IS THE POINT. The members below are the ports and the battery — the things a
/// backend's probe can find — and nothing is declared for a capability that is absent: a slot left <c>null</c>
/// is a machine that does not have that feature, which is what the UI's probe-and-hide sites read. The settings a
/// backend declares are the same kind of thing one step further out: <see cref="DeclaredSettings"/> is the
/// backend's own list, filled by its probe through <see cref="Declare"/>, and the settings MODEL copies it at
/// construction (Infrastructure/Composition/Settings.cs) so the machine that carries it and the model that holds
/// it cannot disagree about what was declared.
///
/// WHO WRITES THE MEMBERS. The vendor backends do, from their own constructors — <c>GenericDevice</c> fills the
/// cross-platform ones and <c>AcerDevice</c>/<c>DellDevice</c> add or override the proprietary ones — which is why
/// they are settable rather than constructor arguments (the same reason <see cref="Battery"/>'s members are).
/// The tests arrange a machine the same way, through <c>FakeDevice</c>: the fake is a machine, not an
/// implementation of an interface, so a test that attaches a port writes it exactly as a backend does.</summary>
public class Device : IDisposable
{
    /// <summary>What the UI shows as the product name. The backends set it: the generic one leaves "Generic",
    /// an Acer machine reports its detected model.</summary>
    public string VendorName { get; set; } = "Generic";

    /// <summary>A latched diagnostic the backend wants shown once, or null. Localization key, not a sentence —
    /// the UI looks it up (see <c>AppController.BackgroundPass</c>).</summary>
    public string? StatusMessage { get; set; }

    public IPowerProfiles?      PowerProfiles      { get; set; }
    public IFanControl?         FanControl         { get; set; }
    public ISensors?            Sensors            { get; set; }
    public Battery              Battery            { get; set; } = new();
    public IKeyboardBrightness? KeyboardBrightness { get; set; }
    public IRgbDevice?          Lighting           { get; set; }
    public IHotkeys?            Hotkeys            { get; set; }
    public IDisplayTint?        DisplayTint        { get; set; }
    public IGpuOverclock?       GpuOverclock       { get; set; }
    public ICpuPower?           CpuPower           { get; set; }
    public ICurveOptimizer?     CurveOptimizer     { get; set; }
    public IDriverSetup?        DriverSetup        { get; set; }
    public IAutostart?          Autostart          { get; set; }
    public IClamshell?          Clamshell          { get; set; }

    /// <summary>The settings this machine declares — this backend's own list, and the source of the set the
    /// settings MODEL holds (Infrastructure/Composition/Settings.cs). A LIST rather than a slot per capability,
    /// because a declaration carries its own shape and its own key: each vendor's <c>InitVendor</c> adds the
    /// entries its own probe found, in the order the rows should read, and a setting it did not find is simply
    /// not declared.
    ///
    /// The set travels WITH the machine rather than as a separate hand-off, and that is what the tuple
    /// <c>DeviceFactory</c> used to return was for: the interface carried no member for it, so composition had
    /// to carry the list alongside. A machine that declares nothing offers nothing, which is the same sentence
    /// as an empty list.</summary>
    public IReadOnlyList<SettingDeclaration> DeclaredSettings => _declaredSettings;

    private readonly List<SettingDeclaration> _declaredSettings = [];

    /// <summary>Register one setting this machine's probe found, under the backend's own key for it. The list is
    /// finished before the app service is built (probing happens in the device's construction), so nothing
    /// appends to it once the settings model holds it.</summary>
    protected void Declare(SettingDeclaration setting) => _declaredSettings.Add(setting);

    private readonly List<IDisposable> _owned = [];

    /// <summary>Register a service/transport this machine owns; disposed with it.</summary>
    protected void Own(IDisposable d) => _owned.Add(d);

    /// <summary>Release what the backends registered through <see cref="Own"/>. VIRTUAL on purpose: the tests'
    /// fake machine overrides it to record the call instead of performing one, and a hidden (<c>new</c>) member
    /// would make that override unreachable — <c>LaptopService.Dispose</c> calls this through a
    /// <see cref="Device"/>-typed field, so the base's body would run and the fake's flag would silently never
    /// be set.</summary>
    public virtual void Dispose()
    {
        foreach (var d in _owned)
        {
            try { d.Dispose(); } catch { /* best effort */ }
        }
    }
}
