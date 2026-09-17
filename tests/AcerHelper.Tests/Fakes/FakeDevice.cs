using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="IDevice"/>. EVERY port is a settable auto-property defaulting to
/// <c>null</c> = "this device does not have that feature", so a test declares exactly the hardware its
/// scenario needs and nothing else:
///
///     var f = new LaptopServiceFixture();
///     f.Device.PowerProfiles = new FakePowerProfiles(profiles);
///     f.Device.FanControl    = new FakeFanControl();
///     f.Device.Declare("lcd_override", new FakeFlagPort());       // a declared setting
///
/// Ports are read lazily by <c>LaptopService</c> on each call, so assigning them after the service is
/// constructed is correct. <see cref="Dispose"/> is a no-op that records the call: nothing here owns a
/// handle, and no test may touch real hardware.
///
/// <see cref="Battery"/> and <see cref="DeclaredSettings"/> are the two slots that are NOT a nullable port,
/// because neither is a port any more: the battery is the domain object that declares its own properties one
/// by one (Domain/Battery.cs), and a setting is DECLARED under the backend's own key (Domain/Settings.cs). A
/// test therefore arranges them through those objects — <c>Battery.ChargeLimit = new
/// FakeFlagPort().AsBatteryToggle()</c>, <see cref="Declare"/> — and an empty device is the canonical "this
/// machine has nothing" device.
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

    /// <summary>The settings this fake machine declares, in the order its backend would add them.</summary>
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
