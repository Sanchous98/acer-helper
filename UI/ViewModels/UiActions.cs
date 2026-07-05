using AcerHelper.Features;

namespace AcerHelper.UI.ViewModels;

/// <summary>Everything the dashboard view-models need from the application layer, bundled into one
/// value so construction stays tidy. Built by <see cref="AppController"/>.</summary>
public sealed record UiActions(
    Action<PerformanceProfile> ApplyProfile,
    Action<bool> SetTurbo,
    Action<FanMode, byte, byte> SetFan,
    Action<bool, bool, int[]> SetFanCurve,
    Func<FanCurveDialogViewModel, Task> ShowFanCurve,
    IReadOnlyList<OptionToggle> HwToggles,
    IReadOnlyList<OptionChoice> HwChoices,
    Func<bool> ClamshellEnabled, Action<bool> SetClamshell,
    bool TurboToggles, Action<bool> SetTurboToggles,
    Func<bool> AutostartEnabled, Action<bool> SetAutostart,
    int FanModeInit, int CpuFanInit, int GpuFanInit,
    bool CpuUseCurveInit, bool GpuUseCurveInit, int[] CpuCurveInit, int[] GpuCurveInit,
    bool HasBatteryInfo, OptionToggle? BatteryLimit, OptionToggle? BatteryCalibration,
    OptionChoice? BatteryChargeMode);
