using AcerHelper.Features;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>Monitoring section: CPU and GPU each shown as a column with its temperature and fan speed
/// together. Columns/values the device doesn't report are hidden via the Show* flags.</summary>
public sealed partial class MonitorViewModel : SectionViewModel
{
    [ObservableProperty] private string _cpuTemp = "—";
    [ObservableProperty] private string _gpuTemp = "—";
    [ObservableProperty] private string _cpuFan = "—";
    [ObservableProperty] private string _gpuFan = "—";
    [ObservableProperty] private int _cpuFanRpm;    // drives the CPU FanSpinner
    [ObservableProperty] private int _gpuFanRpm;    // drives the GPU FanSpinner
    [ObservableProperty] private bool _showCpu;
    [ObservableProperty] private bool _showGpu;
    [ObservableProperty] private bool _showCpuFan;
    [ObservableProperty] private bool _showGpuFan;
    [ObservableProperty] private bool _showTemps;

    public void Update(SensorSnapshot s)
    {
        CpuTemp = s.CpuTempC < 0 ? "—" : $"{s.CpuTempC} °C";
        GpuTemp = s.GpuTempC < 0 ? "—" : $"{s.GpuTempC} °C";
        ShowCpu = s.CpuTempC >= 0;
        ShowGpu = s.GpuTempC >= 0;
        ShowTemps = ShowCpu || ShowGpu;

        FanReading? cpu = null, gpu = null;
        foreach (var f in s.Fans)
        {
            if (cpu is null && f.Label?.Contains("CPU") == true) cpu = f;
            if (gpu is null && f.Label?.Contains("GPU") == true) gpu = f;
        }
        // Many EC drivers expose the fans unlabelled (e.g. linuwu_sense) — when labels identify neither,
        // fall back to the near-universal laptop layout: fan 1 cools the CPU, fan 2 the GPU.
        if (cpu is null && gpu is null && s.Fans.Count >= 1)
        {
            cpu = s.Fans[0];
            gpu = s.Fans.Count >= 2 ? s.Fans[1] : null;
        }

        ShowCpuFan = cpu is { Rpm: >= 0 };
        ShowGpuFan = gpu is { Rpm: >= 0 };
        CpuFan = ShowCpuFan ? $"{cpu!.Value.Rpm} rpm" : "—";
        GpuFan = ShowGpuFan ? $"{gpu!.Value.Rpm} rpm" : "—";
        CpuFanRpm = ShowCpuFan ? cpu!.Value.Rpm : 0;
        GpuFanRpm = ShowGpuFan ? gpu!.Value.Rpm : 0;
    }
}
