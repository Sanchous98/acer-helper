using AcerHelper.Features;
using GenericBattery = AcerHelper.Vendors.Generic.BatteryInfo;

namespace AcerHelper.Vendors.Generic;

/// <summary>
/// The generic laptop device: the capabilities any laptop exposes through standard OS APIs — performance
/// profiles, battery telemetry, autostart, and (where the OS/hardware allow) sensors, clamshell and
/// blue-light. This is the base every vendor backend EXTENDS: <c>AcerDevice : GenericDevice</c> inherits
/// these common ports and overrides/adds only its proprietary ones. Ports are <c>protected set</c> so a
/// subclass can override them; the cross-platform common wiring is in the constructor and the per-OS bits
/// in <see cref="InitPlatform"/> (GenericDevice.Windows.cs / .Linux.cs).
/// </summary>
public partial class GenericDevice : IDevice
{
    public string VendorName { get; protected set; } = "Generic";
    public string? StatusMessage { get; protected set; }

    public IPowerProfiles?      PowerProfiles      { get; protected set; }
    public IFanControl?         FanControl         { get; protected set; }
    public ISensors?            Sensors            { get; protected set; }
    public ILcdOverdrive?       LcdOverdrive       { get; protected set; }
    public IBatteryInfo?        BatteryInfo        { get; protected set; }
    public IBatteryChargeLimit? BatteryChargeLimit { get; protected set; }
    public IBatteryCalibration? BatteryCalibration { get; protected set; }
    public IBatteryChargeMode?  BatteryChargeMode  { get; protected set; }
    public IUsbCharging?        UsbCharging        { get; protected set; }
    public IKeyboardBacklight?  KeyboardBacklight  { get; protected set; }
    public IKeyboardBacklightTimeout? KeyboardBacklightTimeout { get; protected set; }
    public IKeyboardBrightness? KeyboardBrightness { get; protected set; }
    public IFnLock?             FnLock             { get; protected set; }
    public IRgbDevice?          Lighting           { get; protected set; }
    public IHotkeys?            Hotkeys            { get; protected set; }
    public IDisplayTint?        DisplayTint        { get; protected set; }
    public IAutostart?          Autostart          { get; protected set; }
    public IClamshell?          Clamshell          { get; protected set; }

    private readonly List<IDisposable> _owned = [];

    /// <summary>Register a service/transport this device owns; disposed with the device.</summary>
    protected void Own(IDisposable d) => _owned.Add(d);

    public GenericDevice(string? status = null)
    {
        StatusMessage = status;
        BatteryInfo = GenericBattery.TryCreate();   // cross-platform (null on a desktop / no battery)
        Autostart = new Autostart();                // cross-platform (.desktop on Linux, scheduled task on Windows)
        InitPlatform();
    }

    /// <summary>Per-OS common capabilities (profiles, sensors, clamshell, blue-light) — the .Windows/.Linux part.</summary>
    partial void InitPlatform();

    public void Dispose()
    {
        foreach (var d in _owned)
        {
            try { d.Dispose(); } catch { /* best effort */ }
        }
    }
}
