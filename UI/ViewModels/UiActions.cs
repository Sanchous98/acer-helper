using AcerHelper.Features;

namespace AcerHelper.UI.ViewModels;

/// <summary>Everything the dashboard view-models need from the application layer, bundled into one
/// value so construction stays tidy. Built by <see cref="AppController"/>.</summary>
public sealed record UiActions(
    Action<PerformanceProfile> ApplyProfile,
    Action<FanMode, byte, byte> ApplyFan,
    Action<int, int, int> PersistFan,
    IReadOnlyList<OptionToggle> HwToggles,
    IReadOnlyList<OptionChoice> HwChoices,
    Func<bool> ClamshellEnabled, Action<bool> SetClamshell,
    bool TurboToggles, Action<bool> SetTurboToggles,
    Func<bool> AutostartEnabled, Action<bool> SetAutostart,
    int FanModeInit, int CpuFanInit, int GpuFanInit,
    bool HasBatteryInfo, OptionToggle? BatteryLimit, OptionToggle? BatteryCalibration);
