namespace AcerHelper.Composition;

/// <summary>A generic <see cref="IDevice"/> assembled from whatever feature ports a backend
/// supports. Unsupported features are left null. Owns and disposes the services/transports
/// the factory hands it.</summary>
public sealed class CompositeDevice : IDevice
{
    public string VendorName { get; init; } = "(unsupported)";
    public string? StatusMessage { get; init; }

    public IPowerProfiles?      PowerProfiles      { get; init; }
    public IFanControl?         FanControl         { get; init; }
    public ISensors?            Sensors            { get; init; }
    public ILcdOverdrive?       LcdOverdrive       { get; init; }
    public IBatteryChargeLimit? BatteryChargeLimit { get; init; }
    public IBatteryCalibration? BatteryCalibration { get; init; }
    public IUsbCharging?        UsbCharging        { get; init; }
    public IKeyboardBacklight?  KeyboardBacklight  { get; init; }
    public ILighting?           Lighting           { get; init; }
    public IHotkeys?            Hotkeys            { get; init; }
    public IDisplayTint?        DisplayTint        { get; init; }
    public IAutostart?          Autostart          { get; init; }
    public IClamshell?          Clamshell          { get; init; }

    /// <summary>Disposable services/transports this device owns.</summary>
    public IReadOnlyList<IDisposable> Owned { get; init; } = [];

    public void Dispose()
    {
        foreach (var d in Owned)
        {
            try { d.Dispose(); } catch { /* best effort */ }
        }
    }
}
