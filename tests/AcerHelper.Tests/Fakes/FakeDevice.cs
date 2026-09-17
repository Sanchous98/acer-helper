using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="IDevice"/>. EVERY port is a settable auto-property defaulting to
/// <c>null</c> = "this device does not have that feature", so a test declares exactly the hardware its
/// scenario needs and nothing else:
///
///     var f = new LaptopServiceFixture(declare: d => d.Declare("lcd_override", new FakeFlagPort()));
///     f.Device.PowerProfiles = new FakePowerProfiles(profiles);
///     f.Device.FanControl    = new FakeFanControl();
///
/// Ports are read lazily by <c>LaptopService</c> on each call, so assigning them after the service is
/// constructed is correct. <see cref="Dispose"/> is a no-op that records the call: nothing here owns a
/// handle, and no test may touch real hardware.
///
/// <see cref="Battery"/> is the one slot that is NOT a nullable port, because it is not a port any more: the
/// battery is the domain object that declares its own properties one by one (Domain/Battery.cs), so a test
/// arranges it through that object — <c>Battery.ChargeLimit = new FakeFlagPort().AsBatteryToggle()</c>.
///
/// THE DECLARED SETTINGS ARE THE SAME KIND OF THING, one step further out: <see cref="Declare"/> is this fake's
/// probe — what a real backend's <c>InitVendor</c> does with its own key — and the list it fills goes to the
/// settings MODEL, which holds it and switches it (Domain/Settings.cs), exactly as composition hands a real
/// device's list over (<c>DeviceFactory</c> -> <c>LaptopService</c> -> the model's constructor). <see cref="IDevice"/>
/// has no member for it. An empty device is the canonical "this machine has nothing" device.
///
/// <see cref="Declare"/> IS ORDER-SENSITIVE, unlike the ports above: the model copies the set when it is
/// CONSTRUCTED, so every declaration has to be made before the fixture builds the service (the fixture's
/// <c>declare</c> argument is where they go). A declaration made later changes this list and nothing else.
/// </summary>
public sealed class FakeDevice : IDevice
{
    public string VendorName { get; set; } = "Fake Vendor";
    public string? StatusMessage { get; set; }

    public IPowerProfiles? PowerProfiles { get; set; }
    public IFanControl? FanControl { get; set; }
    public ISensors? Sensors { get; set; }
    public Battery Battery { get; set; } = new();
    public IKeyboardBrightness? KeyboardBrightness { get; set; }
    public IRgbDevice? Lighting { get; set; }
    public IHotkeys? Hotkeys { get; set; }
    public IDisplayTint? DisplayTint { get; set; }
    public IGpuOverclock? GpuOverclock { get; set; }
    public ICpuPower? CpuPower { get; set; }
    public ICurveOptimizer? CurveOptimizer { get; set; }
    public IDriverSetup? DriverSetup { get; set; }
    public IAutostart? Autostart { get; set; }
    public IClamshell? Clamshell { get; set; }

    /// <summary>The settings this fake machine declares, in the order its backend would add them. This is the
    /// fake BACKEND's own list rather than an <see cref="IDevice"/> member — the settings model holds the set, and
    /// the fixture hands this very list over (see <see cref="LaptopServiceFixture"/>), which copies it into the
    /// model at construction.</summary>
    public IReadOnlyList<SettingDeclaration> DeclaredSettings => _declaredSettings;

    private readonly List<SettingDeclaration> _declaredSettings = [];

    /// <summary>Declare an on/off setting, as a backend does: the key is the backend's own name for it and is
    /// what the UI's label table is looked up with, so a test that asserts on a row's label must use the key the
    /// real backend uses (Acer's <c>lcd_override</c>, Dell's <c>FnLock</c>).</summary>
    public FlagSetting Declare(string key, IFlagPort port, bool readbackVerifiesWrite = true)
    {
        var setting = new FlagSetting { Key = key, Port = port, ReadbackVerifiesWrite = readbackVerifiesWrite };
        _declaredSettings.Add(setting);
        return setting;
    }

    /// <summary>Declare a pick-one setting, as a backend does.</summary>
    public ChoiceSetting Declare(string key, IChoicePort port)
    {
        var setting = new ChoiceSetting { Key = key, Port = port };
        _declaredSettings.Add(setting);
        return setting;
    }

    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}
