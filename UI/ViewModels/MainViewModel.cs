using System.Collections.ObjectModel;
using AcerHelper.Features;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>The dashboard root: device name + current-profile chip (header), the capability sections
/// (a collection rendered by DataTemplates) as a single full-width column, and a status line plus
/// the drawer-launch buttons (footer). Options and Lighting aren't in the main column — they live in
/// a slide-out side drawer opened from the footer. Built once from the device's capabilities;
/// <see cref="Refresh"/> pushes live state in.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly MonitorViewModel? _monitor;
    private readonly ProfilesViewModel? _profiles;
    private readonly BatteryViewModel? _battery;
    private readonly OptionsViewModel? _options;
    private readonly LightingViewModel? _lighting;

    public string DeviceName { get; }
    public ObservableCollection<SectionViewModel> Sections { get; } = [];

    public bool ShowOptions => _options != null;
    public bool ShowLighting => _lighting != null;

    // The side-panel contents, each hosted in its own window (so switching doesn't flash stale
    // content). DrawerContent points at whichever is currently open.
    public object? OptionsContent => _options;
    public object? LightingContent => _lighting;

    [ObservableProperty] private bool _hasProfile;
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _status = "";

    // Side drawer: a single host that shows either the Options or the Lighting content.
    [ObservableProperty] private object? _drawerContent;
    [ObservableProperty] private string _drawerTitle = "";
    [ObservableProperty] private bool _isDrawerOpen;

    public MainViewModel(IDevice device, UiActions a, LightingViewModel? lighting)
    {
        DeviceName = device.VendorName;
        _lighting = lighting;

        // Main column (full width, glance order): sensors, performance modes, fans, battery.
        if (device.Sensors != null)
            Sections.Add(_monitor = new MonitorViewModel());
        if (device.PowerProfiles is { } pp)
            Sections.Add(_profiles = new ProfilesViewModel(pp.All, a.ApplyProfile));
        if (device.FanControl is { } fc)
            Sections.Add(new FansViewModel(fc.Capability, a.FanModeInit, a.CpuFanInit, a.GpuFanInit, a.ApplyFan, a.PersistFan));
        if (a.HasBatteryInfo || a.BatteryLimit != null || a.BatteryCalibration != null)
            Sections.Add(_battery = new BatteryViewModel(a.HasBatteryInfo, a.BatteryLimit, a.BatteryCalibration));

        // Options live in the drawer, not the main column.
        _options = OptionsViewModel.TryCreate(device, a);
    }

    [RelayCommand] private void OpenOptions() => OpenDrawer("Options", _options);
    [RelayCommand] private void OpenLighting() => OpenDrawer("Lighting", _lighting);
    [RelayCommand] private void CloseDrawer() => IsDrawerOpen = false;

    private void OpenDrawer(string title, object? content)
    {
        if (content == null) return;
        // Re-clicking the open drawer's button closes it (toggle).
        if (IsDrawerOpen && ReferenceEquals(content, DrawerContent)) { IsDrawerOpen = false; return; }
        DrawerContent = content;
        DrawerTitle = title;
        IsDrawerOpen = true;
    }

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
