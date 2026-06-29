using System.Collections.ObjectModel;
using AcerHelper.Features;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>The dashboard root: device name + current-profile chip (header), the capability sections
/// (the framework — a collection rendered by DataTemplates), status and the Lighting button (footer).
/// Built once from the device's capabilities; <see cref="Refresh"/> pushes live state into it.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly MonitorViewModel? _monitor;
    private readonly ProfilesViewModel? _profiles;
    private readonly BatteryViewModel? _battery;
    private readonly Action _openLighting;

    public string DeviceName { get; }
    public bool ShowLighting { get; }

    /// <summary>Dense single-panel dashboard (G-Helper style): the control-heavy sections that need
    /// the full width (monitor, performance modes, fan sliders) stack on top; the compact
    /// row-of-settings sections (battery, options) are balanced into two side-by-side columns to keep
    /// the panel short. <see cref="ShowColumns"/> is false when there's only one — it then falls back
    /// into the full-width stack so no half-empty column is left.</summary>
    public ObservableCollection<SectionViewModel> PrimarySections { get; } = [];
    public ObservableCollection<SectionViewModel> LeftColumn { get; } = [];
    public ObservableCollection<SectionViewModel> RightColumn { get; } = [];
    public bool ShowColumns => LeftColumn.Count > 0;

    [ObservableProperty] private bool _hasProfile;
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _status = "";

    public MainViewModel(IDevice device, UiActions a)
    {
        _openLighting = a.OpenLighting;
        DeviceName = device.VendorName;
        ShowLighting = device.Lighting != null;

        // Full-width, in glance order: live readouts, then the primary control surfaces.
        if (device.Sensors != null)
            PrimarySections.Add(_monitor = new MonitorViewModel());
        if (device.PowerProfiles is { } pp)
            PrimarySections.Add(_profiles = new ProfilesViewModel(pp.All, a.ApplyProfile));
        if (device.FanControl is { } fc)
            PrimarySections.Add(new FansViewModel(fc.Capability, a.FanModeInit, a.CpuFanInit, a.GpuFanInit, a.ApplyFan, a.PersistFan));

        // Compact settings stacks — paired into two columns when there's more than one.
        var secondary = new List<SectionViewModel>();
        if (a.HasBatteryInfo || a.BatteryLimit != null || a.BatteryCalibration != null)
            secondary.Add(_battery = new BatteryViewModel(a.HasBatteryInfo, a.BatteryLimit, a.BatteryCalibration));
        if (OptionsViewModel.TryCreate(device, a) is { } options)
            secondary.Add(options);

        DistributeSecondary(secondary);
    }

    /// <summary>Greedy two-column balance: heaviest first into whichever column is currently shorter.
    /// With a single section there are no columns — it joins the full-width stack instead.</summary>
    private void DistributeSecondary(List<SectionViewModel> secondary)
    {
        if (secondary.Count < 2)
        {
            foreach (var s in secondary) PrimarySections.Add(s);
            return;
        }

        double left = 0, right = 0;
        foreach (var s in secondary.OrderByDescending(x => x.LayoutWeight))
        {
            if (left <= right) { LeftColumn.Add(s); left += s.LayoutWeight; }
            else { RightColumn.Add(s); right += s.LayoutWeight; }
        }
    }

    [RelayCommand] private void OpenLighting() => _openLighting();

    public void Refresh(PerformanceProfile? current, IReadOnlyList<PerformanceProfile> selectable,
                        SensorSnapshot s, BatteryInfoSnapshot battery, string? status)
    {
        HasProfile = current != null;
        ProfileName = current?.DisplayName ?? "";
        _profiles?.Update(current, selectable);
        _monitor?.Update(s);
        _battery?.Update(battery);
        if (status != null) Status = status;
    }
}
