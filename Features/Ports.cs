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
/// performance) — the one CPU-power knob that works with no driver at all on this class of machine. Acer exposes
/// no WMI power path (it bakes the whole PPT/STAPM envelope into its fixed EC profiles), so — exactly like
/// G-Helper's driverless CPU axis — this maps a chosen OS power mode to each performance profile. Ids are the
/// overlay scheme GUID strings; the mode set is the three fixed OS overlays. Present only where the overlay API
/// responds (probe-and-hide). The voltage-curve axis is separate and needs a driver: see
/// <see cref="ICurveOptimizer"/>.</summary>
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

/// <summary>CPU undervolt via AMD's Curve Optimizer: a signed offset in AVFS "counts" applied to the whole
/// voltage/frequency curve (negative = less voltage at every frequency, 0 = stock). The CPU-side twin of
/// <see cref="IGpuOverclock"/> — same shape, same volatility, same re-apply duty: the offset lives in SMU state,
/// so a power cycle restores stock and the app is the source of truth per performance mode.
///
/// Present only on a CPU whose SMU mailbox layout is known AND where the required ring-0 gateway is installed —
/// null otherwise, so the UI hides the section. Two properties of this port are unusual and deliberate: a
/// successful <see cref="Set"/> means the SMU <i>accepted</i> the message, not that the curve provably moved
/// (this hardware offers no trustworthy read-back), and a too-aggressive offset fails hours later at idle rather
/// than under load — so callers should treat it as opt-in, warn, and default to stock.</summary>
public interface ICurveOptimizer
{
    string? LastError { get; }
    /// <summary>Name of the CPU being tuned, for the section header (e.g. "AMD Ryzen AI 9 365 w/ Radeon 880M").</summary>
    string Name { get; }
    /// <summary>Allowed offset range in AVFS counts (inclusive; Min &lt; 0, Max = 0 — undervolt only).</summary>
    (int Min, int Max) Range { get; }
    /// <summary>Approximate millivolts one count is worth, for displaying the offset in units a user thinks in.
    /// APPROXIMATE by nature: a Curve Optimizer offset shifts the whole voltage/frequency curve rather than clamping
    /// a voltage, so the delivered delta varies with frequency and temperature. Treat it as a label, not a spec.</summary>
    double MillivoltsPerCount { get; }

    /// <summary>The independently tunable voltage domains, in display order — empty when the CPU accepts only one
    /// offset for everything. A hybrid part has more than one because its clusters are separate rails, and that is the
    /// finest granularity worth exposing: within a rail the delivered voltage follows the mildest core's request, so a
    /// per-CORE control would leave all but one core per cluster inert.</summary>
    IReadOnlyList<VoltageDomain> Domains { get; }

    /// <summary>Apply one offset per domain, index-aligned with <see cref="Domains"/> (each clamped to
    /// <see cref="Range"/>). Returns false and sets <see cref="LastError"/> when the SMU refuses. Only meaningful
    /// when <see cref="Domains"/> is non-empty.</summary>
    bool SetDomains(IReadOnlyList<int> counts);

    /// <summary>Apply one offset to every core (clamped to <see cref="Range"/>). Returns false and sets
    /// <see cref="LastError"/> when the SMU refuses.</summary>
    bool Set(int counts);
}

/// <summary>One independently tunable CPU voltage domain — on a hybrid part, a core cluster. <see cref="Label"/> is for
/// display (e.g. "Zen 5c") and is an AMD architecture name, so it is not translated. <see cref="Key"/> is the stable
/// identity used as the settings key: it names the hardware domain rather than a position in a list, so a preset
/// survives a change in how domains are ordered or labelled.</summary>
public sealed record VoltageDomain(string Label, string Key);

/// <summary>A third-party driver some feature needs, which the app can offer to install. Present only when that
/// driver is relevant to THIS machine and this build actually carries its installer — so a machine that can never
/// use it is never asked, and a build without the payload never offers something it cannot do.
///
/// Deliberately an offer, not an action taken on the user's behalf: installing a kernel driver is the user's
/// decision, it is somebody else's software, and it may be shared with other tools on the machine. Nothing here
/// upgrades or removes anything.</summary>
public interface IDriverSetup
{
    /// <summary>What is being installed, for the prompt (e.g. "PawnIO").</summary>
    string Name { get; }
    /// <summary>Where it comes from, so the prompt can say so rather than hiding it.</summary>
    string SourceUrl { get; }
    /// <summary>Which app feature it unlocks, for the prompt.</summary>
    string Purpose { get; }
    /// <summary>Whether it is already installed. Cheap and elevation-independent, so it can gate UI.</summary>
    bool Installed { get; }
    /// <summary>Install it, blocking. Returns null on success, otherwise a message to show the user. Call OFF the
    /// UI thread. Never throws.</summary>
    string? Install();
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
    ICurveOptimizer?     CurveOptimizer     { get; }
    IDriverSetup?        DriverSetup        { get; }
    IAutostart?          Autostart          { get; }
    IClamshell?          Clamshell          { get; }
}
