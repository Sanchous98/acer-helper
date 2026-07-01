using AcerHelper;

namespace AcerHelper.Features;

// Feature ports: one fine-grained interface per laptop capability. Infrastructure implements
// them; the Application/UI depend only on these. A feature a device lacks is represented by a
// null port on IDevice (see below), so the UI shows exactly the features that exist.

/// <summary>Switchable performance/platform profiles.</summary>
public interface IPowerProfiles
{
    string? LastError { get; }
    /// <summary>Full set the device exposes, in display order (for building the UI).</summary>
    IReadOnlyList<PerformanceProfile> All { get; }
    /// <summary>Subset of <see cref="All"/> selectable right now (e.g. Turbo drops out on battery).</summary>
    IReadOnlyList<PerformanceProfile> Selectable();
    PerformanceProfile? Current();
    bool Set(PerformanceProfile profile);
}

/// <summary>Fan behaviour and custom speeds.</summary>
public interface IFanControl
{
    string? LastError { get; }
    FanCapability Capability { get; }
    bool SetMode(FanMode mode);
    bool SetCustomSpeeds(byte cpuPercent, byte gpuPercent);
}

/// <summary>Live temperature/RPM telemetry.</summary>
public interface ISensors
{
    SensorSnapshot Read();
}

/// <summary>LCD overdrive (response-time boost).</summary>
public interface ILcdOverdrive
{
    string? LastError { get; }
    bool Get();
    bool Set(bool on);
}

/// <summary>Live battery telemetry (charge %, state, health, cycles). Read-only.</summary>
public interface IBatteryInfo
{
    BatteryInfoSnapshot Read();
}

/// <summary>~80% battery charge limit (battery-health mode).</summary>
public interface IBatteryChargeLimit
{
    string? LastError { get; }
    bool Get();
    bool Set(bool on);
}

/// <summary>Battery calibration (full charge/discharge cycle).</summary>
public interface IBatteryCalibration
{
    string? LastError { get; }
    bool Get();
    bool Set(bool on);
}

/// <summary>USB charging while the laptop is powered off (threshold level).</summary>
public interface IUsbCharging
{
    string? LastError { get; }
    /// <summary>Selectable thresholds, e.g. 0 (off), 10, 20, 30 (percent).</summary>
    IReadOnlyList<int> Levels { get; }
    int Get();
    bool Set(int level);
}

/// <summary>Keyboard backlight auto-off timeout.</summary>
public interface IKeyboardBacklight
{
    string? LastError { get; }
    bool GetTimeout();
    bool SetTimeout(bool on);
}

// RGB lighting is modelled as a zone-based device (IRgbDevice, in Rgb.cs) rather than a fixed
// keyboard+lightbar port, so the UI adapts to whatever zones the active controllers advertise.

/// <summary>Special keys, mapped to generic actions.</summary>
public interface IHotkeys : IDisposable
{
    event Action<HotkeyAction> Pressed;

    /// <summary>Fires when any special-key / raw input is observed (not just the mapped hotkeys). Lets the
    /// app react to out-of-band hardware changes in real time — e.g. re-read the keyboard backlight
    /// brightness the moment the Fn brightness key is pressed, instead of polling.</summary>
    event Action InputActivity;
}

/// <summary>Display blue-light reduction (gamma based). Level 0 = off.</summary>
public interface IDisplayTint
{
    int Levels { get; }
    bool Apply(int level);
}

/// <summary>Run-at-logon control.</summary>
public interface IAutostart
{
    string Label { get; }
    bool IsEnabled();
    bool SetEnabled(bool enable);
}

/// <summary>Keep-awake-on-lid-close management (display + AC aware).</summary>
public interface IClamshell : IDisposable
{
    string Label { get; }
    bool Enabled { get; }
    void SetEnabled(bool value);
    void Evaluate();
}

/// <summary>
/// The connected laptop. Each feature is a nullable port: <c>null</c> means the device does not
/// support that feature (so the UI hides its section). This <i>is</i> the capability model —
/// the set of non-null ports describes exactly what this vendor × OS combination can do.
/// </summary>
public interface IDevice : IDisposable
{
    string VendorName { get; }
    string? StatusMessage { get; }

    IPowerProfiles?      PowerProfiles      { get; }
    IFanControl?         FanControl         { get; }
    ISensors?            Sensors            { get; }
    ILcdOverdrive?       LcdOverdrive       { get; }
    IBatteryInfo?        BatteryInfo        { get; }
    IBatteryChargeLimit? BatteryChargeLimit { get; }
    IBatteryCalibration? BatteryCalibration { get; }
    IUsbCharging?        UsbCharging        { get; }
    IKeyboardBacklight?  KeyboardBacklight  { get; }
    IRgbDevice?          Lighting           { get; }
    IHotkeys?            Hotkeys            { get; }
    IDisplayTint?        DisplayTint        { get; }
    IAutostart?          Autostart          { get; }
    IClamshell?          Clamshell          { get; }
}
