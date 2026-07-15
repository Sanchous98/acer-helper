using System.Collections.ObjectModel;
using System.Linq;
using AcerHelper.Features;
using AcerHelper.Localization;
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
    // Anchors + default ramp come from the one owner (the emulated-curve engine), so the graph and the
    // controller that drives the fans can't drift apart.
    private static readonly int[] Anchors      = FanCurveEngine.Anchors;
    private static readonly int[] DefaultCurve = FanCurveEngine.DefaultCurve;

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

    public FansViewModel(FanCapability cap, FanPreset preset,
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
            CpuCurve.Add(new CurvePointViewModel(Anchors[i], CurveVal(preset.CpuCurve, i), () => OnCurveChanged(false)));
            GpuCurve.Add(new CurvePointViewModel(Anchors[i], CurveVal(preset.GpuCurve, i), () => OnCurveChanged(true)));
        }

        _cpu = Math.Clamp(preset.Cpu, 0, 100);
        _gpu = Math.Clamp(preset.Gpu, 0, 100);
        _cpuPct = $"{(int)_cpu}%";
        _gpuPct = $"{(int)_gpu}%";
        _cpuUseCurve = preset.CpuUseCurve;
        _gpuUseCurve = preset.GpuUseCurve;
        var mode = (FanMode)preset.Mode;
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

    // The open dialog (if any) is remembered so Load() can push a mode-switch's use-curve flag into it:
    // the dialog's POINTS are live (it binds the shared collection), but its switch holds a by-value copy.
    private FanCurveDialogViewModel? _cpuDialog, _gpuDialog;

    [RelayCommand]
    private async Task OpenCpuCurve()
    {
        var dlg = new FanCurveDialogViewModel(Loc.T("CPU fan curve"), CpuCurve, CpuUseCurve, u => CpuUseCurve = u);
        _cpuDialog = dlg;
        try { await _showCurve(dlg); } finally { _cpuDialog = null; }
    }

    [RelayCommand]
    private async Task OpenGpuCurve()
    {
        var dlg = new FanCurveDialogViewModel(Loc.T("GPU fan curve"), GpuCurve, GpuUseCurve, u => GpuUseCurve = u);
        _gpuDialog = dlg;
        try { await _showCurve(dlg); } finally { _gpuDialog = null; }
    }

    /// <summary>Reflect a mode's saved fan preset without triggering apply/persist (the service already set
    /// the hardware on the mode switch). The <c>_loading</c> guard neuters the hooks.</summary>
    public void Load(FanPreset preset)
    {
        // A pending debounce belongs to the PREVIOUS mode: letting it tick after this reload would fire
        // the NEW mode's just-loaded values at the hardware and persist them under the new key (silently
        // creating a preset from leftover UI state). The stale edit raced the mode switch — drop it.
        _fixedDebounce.Stop(); _cpuCurveDebounce.Stop(); _gpuCurveDebounce.Stop();
        _loading = true;
        Cpu = Math.Clamp(preset.Cpu, 0, 100);
        Gpu = Math.Clamp(preset.Gpu, 0, 100);
        var m = (FanMode)preset.Mode;
        IsMax    = m == FanMode.Max && HasMax;
        IsCustom = m == FanMode.Custom && HasCustom;
        IsAuto   = !IsMax && !IsCustom;
        CpuUseCurve = preset.CpuUseCurve;
        GpuUseCurve = preset.GpuUseCurve;
        LoadCurve(CpuCurve, preset.CpuCurve);
        LoadCurve(GpuCurve, preset.GpuCurve);
        // An open curve dialog shows the new mode's points already (shared collection) — keep its
        // "Follow curve" switch in step too, or it would show the old mode's flag and look broken.
        _cpuDialog?.SyncUseCurve(preset.CpuUseCurve);
        _gpuDialog?.SyncUseCurve(preset.GpuUseCurve);
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
/// curve" switch. Toggling the switch flows back to the fan row via <paramref name="setUse"/>; a mode
/// switch while the dialog is open flows the other way via <see cref="SyncUseCurve"/>.</summary>
public sealed partial class FanCurveDialogViewModel : ObservableObject
{
    private readonly Action<bool> _setUse;
    private bool _syncing;

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

    partial void OnUseCurveChanged(bool value) { if (!_syncing) _setUse(value); }

    /// <summary>Reflect the row's flag (a mode switch changed it) without echoing it back through
    /// <c>_setUse</c> — the row already holds this value.</summary>
    public void SyncUseCurve(bool value)
    {
        if (UseCurve == value) return;
        // try/finally: the UseCurve setter raises PropertyChanged synchronously (the two-way ToggleSwitch
        // binding runs inside it); a throwing subscriber must not latch _syncing true, which would silence
        // every later user toggle (OnUseCurveChanged would keep skipping _setUse).
        _syncing = true;
        try { UseCurve = value; }
        finally { _syncing = false; }
    }
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
