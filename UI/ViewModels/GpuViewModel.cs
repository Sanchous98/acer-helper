using AcerHelper;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>GPU-overclock section: a core-clock and a memory-clock offset slider (MHz), each bounded by the
/// range the driver reports. Applies on change (debounced) and persists PER performance mode — switching mode
/// reloads that mode's offsets (see <see cref="Load"/>), and an unconfigured mode is stock 0/0. Only built
/// when the device exposes an <see cref="Features.IGpuOverclock"/> port (an NVIDIA dGPU that allows tuning).</summary>
public sealed partial class GpuViewModel : SectionViewModel
{
    private readonly Action<int, int> _set;                 // (core MHz, mem MHz) -> apply + persist for the current mode
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _loading;

    public string GpuName { get; }
    public int CoreMin { get; }
    public int CoreMax { get; }
    public int MemMin { get; }
    public int MemMax { get; }

    public GpuViewModel(string name, (int Min, int Max) coreRange, (int Min, int Max) memRange,
                        GpuOcPreset preset, Action<int, int> set)
    {
        _loading = true;
        _set = set;
        GpuName = name;
        CoreMin = coreRange.Min; CoreMax = coreRange.Max;
        MemMin = memRange.Min; MemMax = memRange.Max;
        _debounce.Tick += (_, _) => { _debounce.Stop(); Apply(); };

        _core = Math.Clamp(preset.Core, CoreMin, CoreMax);
        _mem = Math.Clamp(preset.Mem, MemMin, MemMax);
        _coreLabel = Fmt(_core);
        _memLabel = Fmt(_mem);
        _loading = false;
    }

    [ObservableProperty] private double _core;
    [ObservableProperty] private double _mem;
    [ObservableProperty] private string _coreLabel;
    [ObservableProperty] private string _memLabel;

    partial void OnCoreChanged(double value) { CoreLabel = Fmt(value); Debounce(); }
    partial void OnMemChanged(double value)  { MemLabel = Fmt(value); Debounce(); }

    /// <summary>Reset both offsets to stock (0/0). Setting the properties fires the debounced apply, so this
    /// persists + applies just like a drag to zero.</summary>
    [RelayCommand]
    private void Reset()
    {
        Core = 0;
        Mem = 0;
    }

    /// <summary>Reflect a mode's saved offsets without triggering apply/persist (the service already set the
    /// hardware on the mode switch). The <c>_loading</c> guard neuters the change hooks; a pending debounce
    /// from the PREVIOUS mode is dropped so it can't fire the new mode's values and re-persist them.</summary>
    public void Load(GpuOcPreset preset)
    {
        _debounce.Stop();
        _loading = true;
        Core = Math.Clamp(preset.Core, CoreMin, CoreMax);
        Mem = Math.Clamp(preset.Mem, MemMin, MemMax);
        _loading = false;
    }

    private void Debounce()
    {
        if (_loading) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Apply() => _set((int)Core, (int)Mem);

    private static string Fmt(double mhz) => $"{(mhz > 0 ? "+" : "")}{(int)mhz} MHz";
}
