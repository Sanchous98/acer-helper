using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>The "Tuning" drawer: hosts the per-profile performance-tuning controls that used to crowd the
/// main dashboard — a <see cref="GpuViewModel"/> (NVIDIA clock offsets) and a <see cref="CpuViewModel"/>
/// (Windows power mode). Either child may be absent (the device lacks that capability); the drawer is only
/// created when at least one exists (see MainViewModel). Rendered by TuningView, which shows each present
/// child under its own "GPU" / "CPU" header.</summary>
public sealed class TuningViewModel : ObservableObject
{
    public GpuViewModel? Gpu { get; }
    public CpuViewModel? Cpu { get; }

    public bool HasGpu => Gpu != null;
    public bool HasCpu => Cpu != null;

    public TuningViewModel(GpuViewModel? gpu, CpuViewModel? cpu)
    {
        Gpu = gpu;
        Cpu = cpu;
    }
}
