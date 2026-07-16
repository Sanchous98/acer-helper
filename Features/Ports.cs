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

/// <summary>Shared shape of a boolean hardware toggle (on/off) with an error channel. The concrete on/off
/// feature ports derive from this so they share one definition and one implementation (see FlagPort); the
/// distinct interface types stay so IDevice can expose each capability as its own nullable port.</summary>
public interface IFlagPort
{
    string? LastError { get; }
    bool Get();
    bool Set(bool on);
}

/// <summary>Shared shape of a pick-one-of-N labelled choice with an error channel. The concrete choice
/// feature ports derive from this (see ChoicePort). Ids are the vendor's stable keys; <see cref="Options"/>
/// is the display list.</summary>
public interface IChoicePort
{
    string? LastError { get; }
    IReadOnlyList<ChoiceOption> Options { get; }
    /// <summary>The active option's id, or null if it can't be read.</summary>
    string? Get();
    bool Set(string id);
}

/// <summary>LCD overdrive (response-time boost).</summary>
public interface ILcdOverdrive : IFlagPort { }

/// <summary>Live battery telemetry (charge %, state, health, cycles). Read-only.</summary>
public interface IBatteryInfo
{
    BatteryInfoSnapshot Read();
}

/// <summary>~80% battery charge limit (battery-health mode).</summary>
public interface IBatteryChargeLimit : IFlagPort { }

/// <summary>Battery calibration (full charge/discharge cycle).</summary>
public interface IBatteryCalibration : IFlagPort { }

/// <summary>Vendor battery charging strategy — a named mode, not a bare threshold (e.g. Dell:
/// Adaptive / Express charge / Primarily AC / Standard / Custom). The mode set (<see cref="IChoicePort.Options"/>)
/// is what the firmware actually advertises on this machine.</summary>
public interface IBatteryChargeMode : IChoicePort { }

/// <summary>USB charging while the laptop is powered off. The options are vendor-defined labelled choices
/// (Acer: Off/10%/20%/30% battery threshold; Dell PowerShare: Off/On).</summary>
public interface IUsbCharging : IChoicePort { }

/// <summary>Keyboard backlight auto-off timeout (on/off).</summary>
public interface IKeyboardBacklight : IFlagPort { }

/// <summary>Plain (non-RGB) keyboard-backlight brightness in discrete hardware levels, 0 = off
/// (e.g. Dell: 0..2 = Off/Dim/Bright). RGB keyboards expose brightness via <see cref="IRgbDevice"/> instead.</summary>
public interface IKeyboardBrightness
{
    string? LastError { get; }
    int MaxLevel { get; }
    int Get();
    bool Set(int level);
}

/// <summary>Keyboard-backlight auto-off delay as a duration choice (5s / 30s / 1m / 5m …), for hardware
/// where the timeout is a fixed set of durations rather than a plain on/off (e.g. the Dell LED stop_timeout).
/// Ids are the exact strings the hardware accepts and reports back.</summary>
public interface IKeyboardBacklightTimeout : IChoicePort { }

/// <summary>Fn-key lock: whether the F-row defaults to its secondary (media/hardware) functions.</summary>
public interface IFnLock : IFlagPort { }

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

/// <summary>Discrete-GPU clock overclocking: signed core- and memory-clock offsets (MHz) layered on the GPU's
/// stock boost curve, each within the driver-reported allowed range. Present only when a controllable NVIDIA
/// dGPU is detected — the port is null otherwise, so the UI hides the section. The offsets are NOT persisted
/// by the driver (a reboot/driver-reload zeroes them), so the app is the source of truth and re-applies on
/// startup, on resume, and on every performance-mode switch (see LaptopService).</summary>
public interface IGpuOverclock
{
    string? LastError { get; }
    /// <summary>Name of the GPU being tuned, for the section header (e.g. "NVIDIA GeForce RTX 4060 Laptop GPU").</summary>
    string Name { get; }
    /// <summary>Allowed core-clock offset range in MHz (inclusive; Min ≤ 0 ≤ Max).</summary>
    (int Min, int Max) CoreRange { get; }
    /// <summary>Allowed memory-clock offset range in MHz (inclusive).</summary>
    (int Min, int Max) MemRange { get; }
    /// <summary>Apply a core + memory clock offset in MHz (each clamped to its allowed range). Returns false
    /// and sets <see cref="LastError"/> on failure.</summary>
    bool Set(int coreMhz, int memMhz);
}

/// <summary>CPU power behaviour via the Windows Power-Mode overlay (Best efficiency / Balanced / Best
/// performance) — the one CPU-power knob that works driverless on this class of machine. There is no ring-0
/// undervolt/PPT here and no Acer-native WMI power path (Acer bakes the whole PPT/STAPM envelope into its
/// fixed EC profiles), so — exactly like G-Helper's driverless CPU axis — this maps a chosen OS power mode to
/// each performance profile. Ids are the overlay scheme GUID strings; the mode set is the three fixed OS
/// overlays. Present only where the overlay API responds (probe-and-hide).</summary>
public interface ICpuPower
{
    string? LastError { get; }
    /// <summary>The three OS power-mode overlays, in display order. Ids are overlay GUID strings.</summary>
    IReadOnlyList<ChoiceOption> Modes { get; }
    /// <summary>The effective overlay's id right now, or null if unreadable.</summary>
    string? Current();
    /// <summary>Switch the active OS power-mode overlay. Returns false and sets <see cref="LastError"/> on failure.</summary>
    bool Set(string id);
}

/// <summary>Run-at-logon control.</summary>
public interface IAutostart
{
    string Label { get; }
    bool IsEnabled();
    bool SetEnabled(bool enable);

    /// <summary>Re-register the run-at-logon entry if it exists but is out of date (an older build's launch
    /// command), so an in-place upgrade migrates to the current definition. No-op if autostart isn't enabled or
    /// the entry is already current. Called at startup.</summary>
    void EnsureCurrent() { }
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
    IBatteryChargeMode?  BatteryChargeMode  { get; }
    IUsbCharging?        UsbCharging        { get; }
    IKeyboardBacklight?  KeyboardBacklight  { get; }
    IKeyboardBacklightTimeout? KeyboardBacklightTimeout { get; }
    IKeyboardBrightness? KeyboardBrightness { get; }
    IFnLock?             FnLock             { get; }
    IRgbDevice?          Lighting           { get; }
    IHotkeys?            Hotkeys            { get; }
    IDisplayTint?        DisplayTint        { get; }
    IGpuOverclock?       GpuOverclock       { get; }
    ICpuPower?           CpuPower           { get; }
    IAutostart?          Autostart          { get; }
    IClamshell?          Clamshell          { get; }
}
