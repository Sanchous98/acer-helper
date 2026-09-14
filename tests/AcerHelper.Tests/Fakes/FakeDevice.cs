using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="IDevice"/>. EVERY port is a settable auto-property defaulting to
/// <c>null</c> = "this device does not have that feature" (Domain/Ports.cs:270-297), so a test
/// declares exactly the hardware its scenario needs and nothing else:
///
///     var f = new LaptopServiceFixture();
///     f.Device.PowerProfiles = new FakePowerProfiles(profiles);
///     f.Device.FanControl    = new FakeFanControl();
///
/// Ports are read lazily by <c>LaptopService</c> on each call, so assigning them after the service is
/// constructed is correct. <see cref="Dispose"/> is a no-op that records the call: nothing here owns a
/// handle, and no test may touch real hardware.
/// </summary>
public sealed class FakeDevice : IDevice
{
    public string VendorName { get; set; } = "Fake Vendor";
    public string? StatusMessage { get; set; }

    public IPowerProfiles? PowerProfiles { get; set; }
    public IFanControl? FanControl { get; set; }
    public ISensors? Sensors { get; set; }
    public ILcdOverdrive? LcdOverdrive { get; set; }
    public IBatteryInfo? BatteryInfo { get; set; }
    public IBatteryChargeLimit? BatteryChargeLimit { get; set; }
    public IBatteryCalibration? BatteryCalibration { get; set; }
    public IBatteryChargeMode? BatteryChargeMode { get; set; }
    public IUsbCharging? UsbCharging { get; set; }
    public IKeyboardBacklight? KeyboardBacklight { get; set; }
    public IKeyboardBacklightTimeout? KeyboardBacklightTimeout { get; set; }
    public IKeyboardBrightness? KeyboardBrightness { get; set; }
    public IFnLock? FnLock { get; set; }
    public IRgbDevice? Lighting { get; set; }
    public IHotkeys? Hotkeys { get; set; }
    public IDisplayTint? DisplayTint { get; set; }
    public IGpuOverclock? GpuOverclock { get; set; }
    public ICpuPower? CpuPower { get; set; }
    public ICurveOptimizer? CurveOptimizer { get; set; }
    public IDriverSetup? DriverSetup { get; set; }
    public IAutostart? Autostart { get; set; }
    public IClamshell? Clamshell { get; set; }

    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}
