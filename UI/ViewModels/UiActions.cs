using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Localization;

namespace AcerHelper.UI.ViewModels;

/// <summary>Everything the dashboard view-models need from the application layer, grouped into one small
/// record per section so each view-model receives only its own slice (and construction can't silently
/// transpose two same-typed positional args across unrelated sections). Built by <see cref="AppController"/>.</summary>
public sealed record UiActions(
    ProfileActions Profiles,
    FanSection Fans,
    GpuSection Gpu,
    CpuSection Cpu,
    CoSection Co,
    BatterySection Battery,
    OptionsSection Options);

/// <summary>Performance section: apply a profile, and (in "Turbo toggles" mode) flip Turbo over the base.</summary>
public sealed record ProfileActions(
    Action<PerformanceProfile> Apply, bool TurboToggles, Action<bool> SetTurbo);

/// <summary>Fan section: the current mode's fan state plus the apply/persist delegates. <c>Initial</c> is the
/// DOMAIN's <see cref="FanAxisState"/> and not the stored preset, because the same value arrives here twice
/// over the life of the section — once at build time from the stored preset, and once per mode switch from the
/// re-apply outcome, which crosses into Application and may not name the container.</summary>
public sealed record FanSection(
    FanAxisState Initial,
    Action<FanMode, byte, byte> SetFan,
    Action<bool, bool, int[]> SetFanCurve,
    Func<FanCurveDialogViewModel, Task> ShowCurve);

/// <summary>GPU-overclock section: the current mode's saved core/memory offsets (MHz) plus the apply/persist
/// delegate. The section is only built when the device exposes an <see cref="IGpuOverclock"/> port.
/// <c>Initial</c> is the domain's <see cref="GpuAxisState"/>, for the same reason as the fan section's.</summary>
public sealed record GpuSection(
    GpuAxisState Initial,
    Action<int, int> SetGpuOc);

/// <summary>CPU-power section: the available power-mode overlays, the current mode's chosen id (or the live
/// effective overlay when unconfigured), and the apply/persist delegate. Built only when the device exposes an
/// <see cref="ICpuPower"/> port. <c>Initial</c> is a PLACEHOLDER (null → Balanced), not a read: this is the one
/// member of <see cref="UiActions"/> that used to cost a hardware read on the UI thread, and wave 6 moved the
/// read into the first background pass — see <see cref="CpuViewModel.Load"/> for where it lands.</summary>
public sealed record CpuSection(
    IReadOnlyList<ChoiceOption> Modes,
    string? Initial,
    Action<string> SetCpuPower);

/// <summary>Undervolt section: the independently tunable voltage domains (empty on a CPU that takes one offset for
/// everything), the current mode's saved offsets index-aligned with them, and the apply/persist delegate. The whole
/// domain records travel rather than just their labels, because a domain carries its own range and mV scale — the
/// graphics rail's differ from the cores'. Built only when the device exposes an <see cref="ICurveOptimizer"/> port.</summary>
public sealed record CoSection(
    IReadOnlyList<VoltageDomain> Domains,
    IReadOnlyList<int> Initial,
    Action<int[]> SetCo);

/// <summary>Battery section: the battery object — which declares for itself whether there is telemetry and
/// which charging controls exist — plus the pre-built option rows for those controls. The object is carried
/// rather than a <c>bool</c> copied out of it, so "the readings are shown exactly when this machine reports a
/// battery" is stated once, here, where a test can reach it (Battery.Telemetry).</summary>
public sealed record BatterySection(
    Battery Battery, OptionToggle? Limit, OptionToggle? Calibration, OptionChoice? ChargeMode)
{
    /// <summary>Whether the live readings (charge %, state, health, cycles) are shown at all.</summary>
    public bool HasInfo => Battery.Telemetry != null;
}

/// <summary>Options drawer: the generic hardware toggles/choices plus the app-level rows (Turbo-key
/// behaviour, clamshell, autostart, language). The enabled STATE of clamshell/autostart is read straight off
/// the device port in <see cref="OptionsViewModel.TryCreate"/> — only the mutations live here as delegates.</summary>
public sealed record OptionsSection(
    IReadOnlyList<OptionToggle> HwToggles,
    IReadOnlyList<OptionChoice> HwChoices,
    IReadOnlyList<OptionChoice> ProfileChoices,
    bool TurboToggles, Action<bool> SetTurboToggles,
    Action<bool> SetClamshell, Action<bool> SetAutostart,
    AppLanguage Language, Action<AppLanguage> SetLanguage);
