using System.Drawing;

namespace AcerHelper;

// Platform abstraction. The Windows implementations live in Platform/Windows.
// A future Linux backend (Linuwu-Sense sysfs, evdev, logind, hidraw, X/Wayland gamma)
// implements the same interfaces, so the UI/app code stays platform-agnostic.

/// <summary>Performance profiles, sensors, fans and LCD overdrive (Acer gaming WMI).</summary>
public interface IPerformance : IDisposable
{
    bool Available { get; }
    string? LastError { get; }

    AcerProfile? GetProfile();
    byte GetSupportedMask();
    bool SetProfile(AcerProfile profile);

    SensorSnapshot ReadSensors();
    bool SetFanMode(FanMode mode);
    bool SetCustomSpeeds(byte cpuPercent, byte gpuPercent);

    bool? GetLcdOverdrive();
    bool SetLcdOverdrive(bool on);
}

/// <summary>Battery health: ~80% charge limit and calibration.</summary>
public interface IBattery : IDisposable
{
    bool Available { get; }
    string? LastError { get; }

    bool? GetLimit();
    bool SetLimit(bool on);
    bool? GetCalibration();
    bool SetCalibration(bool on);
}

/// <summary>Misc peripherals: USB charging threshold, keyboard backlight timeout.</summary>
public interface IPeripherals : IDisposable
{
    bool Available { get; }
    string? LastError { get; }

    int? GetUsbChargingLevel();
    bool SetUsbChargingLevel(int level);

    bool? GetBacklightTimeout();
    bool SetBacklightTimeout(bool on);
}

/// <summary>Direct RGB control of the keyboard (4 zones) and lightbar.</summary>
public interface IRgb : IDisposable
{
    bool Available { get; }
    string? LastError { get; }

    bool ApplyKeyboard(byte modeByte, bool isEffect, byte brightness, byte speed, Color color);
    bool ApplyKeyboardZone(int zoneIndex, byte modeByte, bool isEffect, byte brightness, byte speed, Color color);
    bool ApplyLightbar(byte modeByte, bool isEffect, byte brightness, byte speed, Color color);
}

/// <summary>Watches the special keys (Turbo / Nitro) and raises an event.</summary>
public interface IHotkeys : IDisposable
{
    event Action<AcerHotkey> Pressed;
    bool Registered { get; }
}

/// <summary>Keep-awake-on-lid-close management (display + AC aware).</summary>
public interface IClamshell : IDisposable
{
    bool Enabled { get; }
    bool Supported { get; }
    void SetEnabled(bool value);
    void Evaluate();
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
    bool IsEnabled();
    bool SetEnabled(bool enable);
}

/// <summary>Aggregate of all platform services for one OS backend.</summary>
public interface IPlatform : IDisposable
{
    IPerformance Performance { get; }
    IBattery     Battery     { get; }
    IPeripherals Peripherals { get; }
    IRgb         Rgb         { get; }
    IHotkeys     Hotkeys     { get; }
    IClamshell   Clamshell   { get; }
    IDisplayTint DisplayTint { get; }
    IAutostart   Autostart   { get; }
}
