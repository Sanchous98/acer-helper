using System.Collections.ObjectModel;
using AcerHelper;
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
    private readonly FansViewModel? _fans;
    private readonly BatteryViewModel? _battery;
    private readonly OptionsViewModel? _options;
    private readonly LightingViewModel? _lighting;

    public string DeviceName { get; }
    public ObservableCollection<SectionViewModel> Sections { get; } = [];

    public bool ShowOptions => _options != null;
    public bool ShowLighting => _lighting != null;

    [ObservableProperty] private bool _hasProfile;
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _status = "";

    // Update banner (set once by AppController's startup GitHub-Releases check).
    private Action? _openUpdate;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateLabel = "";

    // "Grant hardware access" banner (Linux/AppImage: install the udev rules via pkexec; set at startup).
    private Action? _grantAccess;
    [ObservableProperty] private bool _needsHardwareAccess;

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
            Sections.Add(_profiles = new ProfilesViewModel(pp.All, a.ApplyProfile, a.TurboToggles, a.SetTurbo));
        if (device.FanControl is { } fc)
            Sections.Add(_fans = new FansViewModel(fc.Capability, a.FanModeInit, a.CpuFanInit, a.GpuFanInit,
                a.CpuUseCurveInit, a.GpuUseCurveInit, a.CpuCurveInit, a.GpuCurveInit,
                a.SetFan, a.SetFanCurve, a.ShowFanCurve));
        if (a.HasBatteryInfo || a.BatteryLimit != null || a.BatteryCalibration != null || a.BatteryChargeMode != null)
            Sections.Add(_battery = new BatteryViewModel(a.HasBatteryInfo, a.BatteryLimit, a.BatteryCalibration, a.BatteryChargeMode));

        // Options live in the drawer, not the main column.
        _options = OptionsViewModel.TryCreate(device, a);
    }

    /// <summary>Show the "update available" banner + tray item (called from the startup update check).</summary>
    public void SetUpdate(string version, Action open)
    {
        _openUpdate = open;
        UpdateLabel = $"Update available: v{version}";
        UpdateAvailable = true;
    }

    [RelayCommand] private void OpenUpdate() => _openUpdate?.Invoke();

    /// <summary>Show the "grant hardware access" banner (Linux install of the udev rules via pkexec).</summary>
    public void SetHardwareAccessNeeded(Action grant)
    {
        _grantAccess = grant;
        NeedsHardwareAccess = true;
    }

    [RelayCommand] private void GrantHardwareAccess() => _grantAccess?.Invoke();

    [RelayCommand] private void OpenOptions() => OpenDrawer("Options", _options);

    [RelayCommand]
    private void OpenLighting()
    {
        _lighting?.Sync();   // re-read live keyboard brightness (Fn keys change it out-of-band) before showing
        OpenDrawer("Lighting", _lighting);
    }

    [RelayCommand] private void CloseDrawer() => IsDrawerOpen = false;

    /// <summary>Reflect a mode's fan preset in the fan section (called when the performance mode changes).</summary>
    public void ReloadFans(int mode, int cpu, int gpu, bool cpuUseCurve, bool gpuUseCurve, int[] cpuCurve, int[] gpuCurve)
        => _fans?.Load(mode, cpu, gpu, cpuUseCurve, gpuUseCurve, cpuCurve, gpuCurve);

    /// <summary>Rebind the lighting panels to a mode's per-zone state (called when the mode changes).</summary>
    public void ReloadLighting(Dictionary<string, LightSettings> lights) => _lighting?.Reload(lights);

    /// <summary>True while the Lighting drawer is the one actually on screen. Cheap check the input-driven
    /// brightness sync gates on, so nothing happens on keystrokes when lighting isn't visible.</summary>
    public bool IsLightingVisible => IsDrawerOpen && ReferenceEquals(DrawerContent, _lighting);

    /// <summary>While the Lighting drawer is showing, re-read live keyboard brightness so the slider tracks
    /// Fn-key changes. The read itself runs off the UI thread (see LightViewModel.SyncFromHardware).</summary>
    public void SyncLightingIfVisible()
    {
        if (IsLightingVisible) _lighting?.Sync();
    }

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
                        bool turboToggles, PerformanceProfile? baseProfile,
                        SensorSnapshot s, BatteryInfoSnapshot battery, string? status)
    {
        HasProfile = current != null;
        ProfileName = current?.DisplayName ?? "";
        _profiles?.Update(current, selectable, turboToggles, baseProfile);
        _monitor?.Update(s);
        _battery?.Update(battery);
        if (status != null) Status = status;
    }
}
