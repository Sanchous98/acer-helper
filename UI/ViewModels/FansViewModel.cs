using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;

namespace AcerHelper.UI.ViewModels;

/// <summary>Fan-control section: Auto / Max / Custom mode plus CPU/GPU speed sliders (live only in
/// Custom). Applies on change with a short debounce and persists the selection.</summary>
public sealed partial class FansViewModel : SectionViewModel
{
    private readonly Action<FanMode, byte, byte> _apply;
    private readonly Action<int, int, int> _persist;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly bool _loading;

    public bool HasMax { get; }
    public bool HasCustom { get; }
    public bool HasGpu { get; }

    public FansViewModel(FanCapability cap, int modeInit, int cpuInit, int gpuInit,
                         Action<FanMode, byte, byte> apply, Action<int, int, int> persist)
    {
        _loading = true;
        _apply = apply;
        _persist = persist;
        HasMax = cap.HasMax;
        HasCustom = cap.HasCustom;
        HasGpu = cap.HasGpuFan;
        _debounce.Tick += (_, _) => { _debounce.Stop(); ApplyNow(); };

        // Direct field writes -> the generated setters' OnXxxChanged hooks don't fire during restore.
        _cpu = Math.Clamp(cpuInit, 0, 100);
        _gpu = Math.Clamp(gpuInit, 0, 100);
        _cpuPct = $"{(int)_cpu}%";
        _gpuPct = $"{(int)_gpu}%";
        var mode = (FanMode)modeInit;
        _isMax = mode == FanMode.Max && HasMax;
        _isCustom = mode == FanMode.Custom && HasCustom;
        _isAuto = !_isMax && !_isCustom;
        _slidersEnabled = _isCustom;
        _loading = false;
    }

    [ObservableProperty] private bool _isAuto;
    [ObservableProperty] private bool _isMax;
    [ObservableProperty] private bool _isCustom;
    [ObservableProperty] private double _cpu;
    [ObservableProperty] private double _gpu;
    [ObservableProperty] private string _cpuPct;
    [ObservableProperty] private string _gpuPct;
    [ObservableProperty] private bool _slidersEnabled;

    partial void OnIsAutoChanged(bool value) { if (value) ApplyIfLive(); }
    partial void OnIsMaxChanged(bool value) { if (value) ApplyIfLive(); }
    partial void OnIsCustomChanged(bool value) { SlidersEnabled = value; if (value) ApplyIfLive(); }
    partial void OnCpuChanged(double value) { CpuPct = $"{(int)value}%"; Debounce(); }
    partial void OnGpuChanged(double value) { GpuPct = $"{(int)value}%"; Debounce(); }

    private FanMode Mode() => IsMax ? FanMode.Max : IsCustom ? FanMode.Custom : FanMode.Auto;
    private void ApplyIfLive() { if (!_loading) ApplyNow(); }
    private void Debounce() { if (_loading || !IsCustom) return; _debounce.Stop(); _debounce.Start(); }
    private void ApplyNow() { _apply(Mode(), (byte)Cpu, (byte)Gpu); _persist((int)Mode(), (int)Cpu, (int)Gpu); }
}
