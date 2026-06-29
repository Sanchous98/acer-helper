using System.Collections.ObjectModel;
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
    private readonly Action _openLighting;

    public string DeviceName { get; }
    public bool ShowLighting { get; }
    public ObservableCollection<SectionViewModel> Sections { get; } = [];

    [ObservableProperty] private bool _hasProfile;
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _status = "";

    public MainViewModel(IDevice device, UiActions a)
    {
        _openLighting = a.OpenLighting;
        DeviceName = device.VendorName;
        ShowLighting = device.Lighting != null;

        if (device.Sensors != null)
            Sections.Add(_monitor = new MonitorViewModel());
        if (device.PowerProfiles is { } pp)
            Sections.Add(_profiles = new ProfilesViewModel(pp.All, a.ApplyProfile));
        if (device.FanControl is { } fc)
            Sections.Add(new FansViewModel(fc.Capability, a.FanModeInit, a.CpuFanInit, a.GpuFanInit, a.ApplyFan, a.PersistFan));
        if (OptionsViewModel.TryCreate(device, a) is { } options)
            Sections.Add(options);
    }

    [RelayCommand] private void OpenLighting() => _openLighting();

    public void Refresh(PerformanceProfile? current, IReadOnlyList<PerformanceProfile> selectable, SensorSnapshot s, string? status)
    {
        HasProfile = current != null;
        ProfileName = current?.DisplayName ?? "";
        _profiles?.Update(current, selectable);
        _monitor?.Update(s);
        if (status != null) Status = status;
    }
}
