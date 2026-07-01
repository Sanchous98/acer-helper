using AcerHelper.Features;

namespace AcerHelper.Vendors.Acer;

// Generic, transport-agnostic feature holders. Every simple Acer capability is "read a value / write a
// value via a transport" — the transport + encoding is the ONLY thing that differs by platform, so instead
// of a class per feature these thin holders implement the port interfaces and take the platform operation
// as delegates. The delegates (WMI on Windows, Linuwu sysfs on Linux) are supplied by AcerDevice.InitVendor
// in AcerDevice.{Windows,Linux}.cs — that's where the per-OS encoding lives. Writes return (ok, error) so
// the holder can surface LastError; reads return the value directly (errors degrade to a default).
// (AcerLighting is deliberately NOT reduced to this — it's an effect table + HID/packet protocol, not a
// plain read/write.)

/// <summary>A boolean toggle. The identical-shaped bool ports (charge limit, calibration, LCD overdrive,
/// keyboard-backlight timeout) are all this one class, instantiated with different platform ops.</summary>
public sealed class AcerFlag(Func<bool> read, Func<bool, (bool ok, string? error)> write)
    : IBatteryChargeLimit, IBatteryCalibration, ILcdOverdrive, IKeyboardBacklight
{
    public string? LastError { get; private set; }
    public bool Get() => read();
    public bool Set(bool on) { var (ok, e) = write(on); LastError = e; return ok; }
    public bool GetTimeout() => read();
    public bool SetTimeout(bool on) => Set(on);
}

/// <summary>A discrete integer choice from a fixed set (USB charging threshold).</summary>
public sealed class AcerChoice(IReadOnlyList<int> levels, Func<int> read, Func<int, (bool ok, string? error)> write)
    : IUsbCharging
{
    public string? LastError { get; private set; }
    public IReadOnlyList<int> Levels => levels;
    public int Get() => read();
    public bool Set(int level) { var (ok, e) = write(level); LastError = e; return ok; }
}

/// <summary>Fan behaviour + custom CPU/GPU speeds.</summary>
public sealed class AcerFan(FanCapability capability,
    Func<FanMode, (bool ok, string? error)> setMode,
    Func<byte, byte, (bool ok, string? error)> setSpeeds) : IFanControl
{
    public string? LastError { get; private set; }
    public FanCapability Capability => capability;
    public bool SetMode(FanMode mode) { var (ok, e) = setMode(mode); LastError = e; return ok; }
    public bool SetCustomSpeeds(byte cpu, byte gpu) { var (ok, e) = setSpeeds(cpu, gpu); LastError = e; return ok; }
}

/// <summary>Performance/platform profiles. The profile set/mapping is shared data (<see cref="AcerProfiles"/>);
/// only reading the current/available set and applying one is a transport op.</summary>
public sealed class AcerPowerProfiles(IReadOnlyList<PerformanceProfile> all,
    Func<IReadOnlyList<PerformanceProfile>> selectable,
    Func<PerformanceProfile?> current,
    Func<PerformanceProfile, (bool ok, string? error)> set) : IPowerProfiles
{
    public string? LastError { get; private set; }
    public IReadOnlyList<PerformanceProfile> All => all;
    public IReadOnlyList<PerformanceProfile> Selectable() => selectable();
    public PerformanceProfile? Current() => current();
    public bool Set(PerformanceProfile profile) { var (ok, e) = set(profile); LastError = e; return ok; }
}

/// <summary>Live temperature/RPM telemetry (a single transport read assembled into a snapshot).</summary>
public sealed class AcerSensors(Func<SensorSnapshot> read) : ISensors
{
    public SensorSnapshot Read() => read();
}
