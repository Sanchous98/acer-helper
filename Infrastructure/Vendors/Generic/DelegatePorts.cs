using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// Generic, transport-agnostic feature holders shared by all vendors. Every simple vendor capability is
// "read a value / write a value via a transport" — the transport + encoding is the ONLY thing that differs
// by vendor and platform, so instead of a class per feature these thin holders implement the port
// interfaces and take the platform operation as delegates. Each vendor's InitVendor supplies method-group
// references (Acer: WMI on Windows, and on Linux the mainline acer-wmi sysfs nodes — its hwmon chip and its
// own platform-profile class node — plus hidraw for RGB and the EC envelope; Dell: power_supply/
// firmware-attributes sysfs on Linux, BIOS-attribute WMI on Windows). Writes return (ok, error) so the holder
// can surface LastError; reads return the value directly (errors degrade to a default).

/// <summary>A boolean toggle. Every on/off setting a backend declares is this one class, instantiated with
/// different ops and handed over as a <see cref="FlagSetting"/> alongside the backend's own key for it
/// (LCD overdrive, the keyboard-backlight timeout flag, Fn lock). The battery's charge limit and calibration
/// are the same pair of delegates but not the same class: they are members of the battery object, which takes
/// the ops directly (Domain/Battery.cs).</summary>
public sealed class FlagPort(Func<bool> read, Func<bool, (bool ok, string? error)> write)
    : IFlagPort
{
    public string? LastError { get; private set; }
    public bool Get() => read();
    public bool Set(bool on) { var (ok, e) = write(on); LastError = e; return ok; }
}

/// <summary>A pick-one-of-N labeled choice. Every pick-one setting a backend declares is this one class,
/// instantiated with different ops and handed over as a <see cref="ChoiceSetting"/> alongside the backend's own
/// key for it (USB charging, the backlight-timeout duration); ids are the vendor's stable keys. The battery's
/// charge mode is the same pair of delegates but not this class, for the same reason as the flag above.</summary>
public sealed class ChoicePort(IReadOnlyList<ChoiceOption> options, Func<string?> read, Func<string, (bool ok, string? error)> write)
    : IChoicePort
{
    public string? LastError { get; private set; }
    public IReadOnlyList<ChoiceOption> Options => options;
    public string? Get() => read();
    public bool Set(string id) { var (ok, e) = write(id); LastError = e; return ok; }
}

/// <summary>Fan behaviour + custom CPU/GPU speeds.</summary>
public sealed class FanPort(FanCapability capability,
    Func<FanMode, (bool ok, string? error)> setMode,
    Func<byte, byte, (bool ok, string? error)> setSpeeds) : IFanControl
{
    public string? LastError { get; private set; }
    public FanCapability Capability => capability;
    public bool SetMode(FanMode mode) { var (ok, e) = setMode(mode); LastError = e; return ok; }
    public bool SetCustomSpeeds(byte cpu, byte gpu) { var (ok, e) = setSpeeds(cpu, gpu); LastError = e; return ok; }
}

/// <summary>Performance/platform profiles. The profile set/mapping is vendor data (e.g. <c>AcerProfiles</c>);
/// only reading the current/available set and applying one is a transport op.</summary>
public sealed class ProfilesPort(IReadOnlyList<PerformanceProfile> all,
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

/// <summary>Discrete brightness levels (0 - Max) for a plain, non-RGB keyboard backlight.</summary>
public sealed class LevelPort(int maxLevel, Func<int> read, Func<int, (bool ok, string? error)> write) : IKeyboardBrightness
{
    public string? LastError { get; private set; }
    public int MaxLevel => maxLevel;
    public int Get() => read();
    public bool Set(int level) { var (ok, e) = write(level); LastError = e; return ok; }
}

/// <summary>Live temperature/RPM telemetry (single transport read assembled into a snapshot).</summary>
public sealed class SensorsPort(Func<SensorSnapshot> read) : ISensors
{
    public SensorSnapshot Read() => read();
}
