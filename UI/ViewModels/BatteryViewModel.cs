using AcerHelper.Features;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>Battery section: live charge %/state + health and cycle count (when the battery reports
/// them), plus the charge-limit and calibration toggles where the device supports them. Info is
/// pushed in by <see cref="Update"/> on each refresh.</summary>
public sealed partial class BatteryViewModel : SectionViewModel
{
    public BatteryViewModel(bool hasInfo, OptionToggle? limit, OptionToggle? calibration)
    {
        ShowInfo = hasInfo;
        if (limit != null) Limit = new ToggleRowViewModel(limit);
        if (calibration != null) Calibration = new ToggleRowViewModel(calibration);
    }

    public bool ShowInfo { get; }

    public ToggleRowViewModel? Limit { get; }
    public ToggleRowViewModel? Calibration { get; }
    public bool ShowLimit => Limit != null;
    public bool ShowCalibration => Calibration != null;

    [ObservableProperty] private string _charge = "—";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _health = "";
    [ObservableProperty] private bool _showHealth;
    [ObservableProperty] private string _cycles = "";
    [ObservableProperty] private bool _showCycles;

    public void Update(BatteryInfoSnapshot s)
    {
        Charge = s.Percent < 0 ? "—" : $"{s.Percent}%";
        State = s.State switch
        {
            BatteryState.Charging    => "Charging",
            BatteryState.Discharging => "On battery",
            BatteryState.Idle        => "Plugged in",
            _                        => "",
        };
        Health = s.HealthPercent < 0 ? "" : $"{s.HealthPercent}%";
        ShowHealth = s.HealthPercent >= 0;
        Cycles = s.CycleCount < 0 ? "" : s.CycleCount.ToString();
        ShowCycles = s.CycleCount >= 0;
    }
}
