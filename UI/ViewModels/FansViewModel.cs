using System.Collections.ObjectModel;
using System.Linq;
using AcerHelper.Features;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>Fan-control section: Auto / Max / Custom. In Custom each fan has a fixed-speed slider plus a
/// "Curve" button that opens a modal drag-graph; while a fan's curve is on, the service drives that fan from
/// the curve (see LaptopService) and the fixed slider is disabled. Applies on change (debounced) and
/// persists per performance mode.</summary>
public sealed partial class FansViewModel : SectionViewModel
{
    private static readonly int[] Anchors      = [50, 60, 70, 80, 90];   // must match LaptopService.FanCurveAnchors
    private static readonly int[] DefaultCurve = [30, 45, 60, 80, 100];

    private readonly Action<FanMode, byte, byte> _setFan;
    private readonly Action<bool, bool, int[]> _setFanCurve;                 // (gpu, use, points)
    private readonly Func<FanCurveDialogViewModel, Task> _showCurve;
    private readonly DispatcherTimer _fixedDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _cpuCurveDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _gpuCurveDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _loading;

    public bool HasMax { get; }
    public bool HasCustom { get; }
    public bool HasGpu { get; }

    public ObservableCollection<CurvePointViewModel> CpuCurve { get; } = [];
    public ObservableCollection<CurvePointViewModel> GpuCurve { get; } = [];

    public FansViewModel(FanCapability cap, int modeInit, int cpuInit, int gpuInit,
                         bool cpuUseCurveInit, bool gpuUseCurveInit, int[] cpuCurveInit, int[] gpuCurveInit,
                         Action<FanMode, byte, byte> setFan, Action<bool, bool, int[]> setFanCurve,
                         Func<FanCurveDialogViewModel, Task> showCurve)
    {
        _loading = true;
        _setFan = setFan;
        _setFanCurve = setFanCurve;
        _showCurve = showCurve;
        HasMax = cap.HasMax;
        HasCustom = cap.HasCustom;
        HasGpu = cap.HasGpuFan;
        _fixedDebounce.Tick    += (_, _) => { _fixedDebounce.Stop(); ApplyFixedNow(); };
        _cpuCurveDebounce.Tick += (_, _) => { _cpuCurveDebounce.Stop(); PersistCurve(false); };
        _gpuCurveDebounce.Tick += (_, _) => { _gpuCurveDebounce.Stop(); PersistCurve(true); };

        for (var i = 0; i < Anchors.Length; i++)
        {
            CpuCurve.Add(new CurvePointViewModel(Anchors[i], CurveVal(cpuCurveInit, i), () => OnCurveChanged(false)));
            GpuCurve.Add(new CurvePointViewModel(Anchors[i], CurveVal(gpuCurveInit, i), () => OnCurveChanged(true)));
        }

        _cpu = Math.Clamp(cpuInit, 0, 100);
        _gpu = Math.Clamp(gpuInit, 0, 100);
        _cpuPct = $"{(int)_cpu}%";
        _gpuPct = $"{(int)_gpu}%";
        _cpuUseCurve = cpuUseCurveInit;
        _gpuUseCurve = gpuUseCurveInit;
        var mode = (FanMode)modeInit;
        _isMax    = mode == FanMode.Max && HasMax;
        _isCustom = mode == FanMode.Custom && HasCustom;
        _isAuto   = !_isMax && !_isCustom;
        _loading = false;
    }

    [ObservableProperty] private bool _isAuto;
    [ObservableProperty] private bool _isMax;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurveEnabled), nameof(CpuSliderEnabled), nameof(GpuSliderEnabled))]
    private bool _isCustom;

    [ObservableProperty] private double _cpu;
    [ObservableProperty] private double _gpu;
    [ObservableProperty] private string _cpuPct;
    [ObservableProperty] private string _gpuPct;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(CpuSliderEnabled))] private bool _cpuUseCurve;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(GpuSliderEnabled))] private bool _gpuUseCurve;

    public bool CurveEnabled    => IsCustom;
    public bool CpuSliderEnabled => IsCustom && !CpuUseCurve;
    public bool GpuSliderEnabled => IsCustom && !GpuUseCurve;

    partial void OnIsAutoChanged(bool value)   { if (value) ApplyFixedIfLive(); }
    partial void OnIsMaxChanged(bool value)    { if (value) ApplyFixedIfLive(); }
    partial void OnIsCustomChanged(bool value) { if (value) ApplyFixedIfLive(); }
    partial void OnCpuChanged(double value) { CpuPct = $"{(int)value}%"; DebounceFixed(); }
    partial void OnGpuChanged(double value) { GpuPct = $"{(int)value}%"; DebounceFixed(); }
    partial void OnCpuUseCurveChanged(bool value) { if (!_loading) PersistCurve(false); }
    partial void OnGpuUseCurveChanged(bool value) { if (!_loading) PersistCurve(true); }

    [RelayCommand] private async Task OpenCpuCurve()
        => await _showCurve(new FanCurveDialogViewModel("CPU fan curve", CpuCurve, CpuUseCurve, u => CpuUseCurve = u));

    [RelayCommand] private async Task OpenGpuCurve()
        => await _showCurve(new FanCurveDialogViewModel("GPU fan curve", GpuCurve, GpuUseCurve, u => GpuUseCurve = u));

    /// <summary>Reflect a mode's saved fan preset without triggering apply/persist (the service already set
    /// the hardware on the mode switch). The <c>_loading</c> guard neuters the hooks.</summary>
    public void Load(int mode, int cpu, int gpu, bool cpuUseCurve, bool gpuUseCurve, int[] cpuCurve, int[] gpuCurve)
    {
        _loading = true;
        Cpu = Math.Clamp(cpu, 0, 100);
        Gpu = Math.Clamp(gpu, 0, 100);
        var m = (FanMode)mode;
        IsMax    = m == FanMode.Max && HasMax;
        IsCustom = m == FanMode.Custom && HasCustom;
        IsAuto   = !IsMax && !IsCustom;
        CpuUseCurve = cpuUseCurve;
        GpuUseCurve = gpuUseCurve;
        LoadCurve(CpuCurve, cpuCurve);
        LoadCurve(GpuCurve, gpuCurve);
        _loading = false;
    }

    private static void LoadCurve(ObservableCollection<CurvePointViewModel> pts, int[] vals)
    {
        for (var i = 0; i < pts.Count; i++) pts[i].Percent = CurveVal(vals, i);   // guarded by _loading
    }

    private static int CurveVal(int[] vals, int i)
        => vals != null && i < vals.Length ? Math.Clamp(vals[i], 0, 100) : DefaultCurve[i];

    private FanMode Mode() => IsMax ? FanMode.Max : IsCustom ? FanMode.Custom : FanMode.Auto;

    private void ApplyFixedIfLive() { if (!_loading) ApplyFixedNow(); }
    private void ApplyFixedNow() => _setFan(Mode(), (byte)Cpu, (byte)Gpu);

    private void DebounceFixed() { if (_loading || !IsCustom) return; _fixedDebounce.Stop(); _fixedDebounce.Start(); }

    private void OnCurveChanged(bool gpu)
    {
        if (_loading) return;
        var t = gpu ? _gpuCurveDebounce : _cpuCurveDebounce;
        t.Stop(); t.Start();
    }

    private void PersistCurve(bool gpu)
        => _setFanCurve(gpu, gpu ? GpuUseCurve : CpuUseCurve, Duties(gpu ? GpuCurve : CpuCurve));

    private static int[] Duties(ObservableCollection<CurvePointViewModel> pts) => pts.Select(p => (int)p.Percent).ToArray();
}

/// <summary>View-model for the modal fan-curve editor: the fan's points (shared with the row) + a "Follow
/// curve" switch. Toggling the switch flows back to the fan row via <paramref name="setUse"/>.</summary>
public sealed partial class FanCurveDialogViewModel : ObservableObject
{
    private readonly Action<bool> _setUse;

    public FanCurveDialogViewModel(string title, ObservableCollection<CurvePointViewModel> points, bool useCurve, Action<bool> setUse)
    {
        Title = title;
        Points = points;
        _useCurve = useCurve;
        _setUse = setUse;
    }

    public string Title { get; }
    public ObservableCollection<CurvePointViewModel> Points { get; }

    [ObservableProperty] private bool _useCurve;

    partial void OnUseCurveChanged(bool value) => _setUse(value);
}

/// <summary>One editable point of a fan curve: a fixed temperature anchor (label) + its duty%.</summary>
public sealed partial class CurvePointViewModel : ObservableObject
{
    private readonly Action _changed;

    public CurvePointViewModel(int tempAnchor, int percent, Action changed)
    {
        Label = $"{tempAnchor}°";
        _changed = changed;
        _percent = Math.Clamp(percent, 0, 100);
        _pct = $"{(int)_percent}%";
    }

    public string Label { get; }

    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _pct;

    partial void OnPercentChanged(double value) { Pct = $"{(int)value}%"; _changed(); }
}
