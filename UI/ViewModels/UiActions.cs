using AcerHelper.Features;
using AcerHelper.Localization;

namespace AcerHelper.UI.ViewModels;

/// <summary>Everything the dashboard view-models need from the application layer, grouped into one small
/// record per section so each view-model receives only its own slice (and construction can't silently
/// transpose two same-typed positional args across unrelated sections). Built by <see cref="AppController"/>.</summary>
public sealed record UiActions(
    ProfileActions Profiles,
    FanSection Fans,
    GpuSection Gpu,
    BatterySection Battery,
    OptionsSection Options);

/// <summary>Performance section: apply a profile, and (in "Turbo toggles" mode) flip Turbo over the base.</summary>
public sealed record ProfileActions(
    Action<PerformanceProfile> Apply, bool TurboToggles, Action<bool> SetTurbo);

/// <summary>Fan section: the current mode's preset plus the apply/persist delegates.</summary>
public sealed record FanSection(
    FanPreset Initial,
    Action<FanMode, byte, byte> SetFan,
    Action<bool, bool, int[]> SetFanCurve,
    Func<FanCurveDialogViewModel, Task> ShowCurve);

/// <summary>GPU-overclock section: the current mode's saved core/memory offsets (MHz) plus the apply/persist
/// delegate. The section is only built when the device exposes an <see cref="IGpuOverclock"/> port.</summary>
public sealed record GpuSection(
    GpuOcPreset Initial,
    Action<int, int> SetGpuOc);

/// <summary>Battery section: whether telemetry exists, plus the pre-built option rows the device supports.</summary>
public sealed record BatterySection(
    bool HasInfo, OptionToggle? Limit, OptionToggle? Calibration, OptionChoice? ChargeMode);

/// <summary>Options drawer: the generic hardware toggles/choices plus the app-level rows (Turbo-key
/// behaviour, clamshell, autostart, language). The enabled STATE of clamshell/autostart is read straight off
/// the device port in <see cref="OptionsViewModel.TryCreate"/> — only the mutations live here as delegates.</summary>
public sealed record OptionsSection(
    IReadOnlyList<OptionToggle> HwToggles,
    IReadOnlyList<OptionChoice> HwChoices,
    bool TurboToggles, Action<bool> SetTurboToggles,
    Action<bool> SetClamshell, Action<bool> SetAutostart,
    AppLanguage Language, Action<AppLanguage> SetLanguage);
