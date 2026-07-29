using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>The "Tuning" drawer: hosts the per-profile performance-tuning controls that used to crowd the
/// main dashboard — a <see cref="GpuViewModel"/> (NVIDIA clock offsets), a <see cref="CpuViewModel"/>
/// (Windows power mode) and a <see cref="CoViewModel"/> (CPU undervolt). Any child may be absent (the device
/// lacks that capability); the drawer is only created when at least one exists (see MainViewModel). Rendered by
/// TuningView, which puts the GPU child under a "GPU" header and both CPU children under one "CPU" header.</summary>
public sealed class TuningViewModel : ObservableObject
{
    public GpuViewModel? Gpu { get; }
    public CpuViewModel? Cpu { get; }
    public CoViewModel? Co { get; }

    public bool HasGpu => Gpu != null;
    public bool HasCpu => Cpu != null;
    public bool HasCo => Co != null;

    /// <summary>Whether the shared "CPU" card should exist at all — the power-mode picker and the undervolt slider
    /// are independent capabilities that share one header.</summary>
    public bool HasCpuCard => HasCpu || HasCo;

    public TuningViewModel(GpuViewModel? gpu, CpuViewModel? cpu, CoViewModel? co)
    {
        Gpu = gpu;
        Cpu = cpu;
        Co = co;
    }
}
