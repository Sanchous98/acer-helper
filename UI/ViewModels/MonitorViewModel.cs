using System.Collections.ObjectModel;
using AcerHelper.Features;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>Monitoring section: CPU/GPU temperatures and one row per fan (1–3). Columns and rows the
/// device doesn't report are hidden via the Show* flags.</summary>
public sealed partial class MonitorViewModel : SectionViewModel
{
    public ObservableCollection<FanViewModel> Fans { get; } = [];

    [ObservableProperty] private string _cpuTemp = "—";
    [ObservableProperty] private string _gpuTemp = "—";
    [ObservableProperty] private bool _showCpu;
    [ObservableProperty] private bool _showGpu;
    [ObservableProperty] private bool _showTemps;

    public void Update(SensorSnapshot s)
    {
        CpuTemp = s.CpuTempC < 0 ? "—" : $"{s.CpuTempC} °C";
        GpuTemp = s.GpuTempC < 0 ? "—" : $"{s.GpuTempC} °C";
        ShowCpu = s.CpuTempC >= 0;
        ShowGpu = s.GpuTempC >= 0;
        ShowTemps = ShowCpu || ShowGpu;

        if (Fans.Count != s.Fans.Count)   // fan set is static per machine, so this runs once
        {
            Fans.Clear();
            foreach (var f in s.Fans) Fans.Add(new FanViewModel(f.Label));
        }
        for (var i = 0; i < s.Fans.Count; i++) Fans[i].Update(s.Fans[i]);
    }
}

/// <summary>One fan row: a label, a formatted RPM string, and the raw RPM that drives the spinner.</summary>
public sealed partial class FanViewModel(string label) : ObservableObject
{
    [ObservableProperty] private string _label = label;
    [ObservableProperty] private string _rpm = "—";
    [ObservableProperty] private int _rpmValue;   // -> FanSpinner.Rpm

    public void Update(FanReading r)
    {
        Label = r.Label;
        Rpm = r.Rpm < 0 ? "—" : $"{r.Rpm} rpm";
        RpmValue = Math.Max(r.Rpm, 0);
    }
}
