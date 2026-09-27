using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>The "Tuning" drawer: hosts the per-profile performance-tuning controls that used to crowd the
/// main dashboard — a <see cref="GpuViewModel"/> (NVIDIA clock offsets), a <see cref="GpuMuxViewModel"/> (the
/// shared GPU-mode/MUX switch), a <see cref="CpuViewModel"/> (Windows power mode) and a <see cref="CoViewModel"/>
/// (CPU undervolt). Any child may be absent (the device lacks that capability); the drawer is only created when
/// at least one exists (see MainViewModel). Rendered by TuningView, which puts the GPU children under a "GPU"
/// header and both CPU children under one "CPU" header.</summary>
public sealed class TuningViewModel : ObservableObject
{
    public GpuViewModel? Gpu { get; }
    public CpuViewModel? Cpu { get; }
    public CoViewModel? Co { get; }
    public GpuMuxViewModel? GpuMux { get; }

    /// <summary>The guided-undervolt card, built beside the manual sliders it disables while it runs. Optional so
    /// a machine (or a test) without the sweep keeps the drawer exactly as it was.</summary>
    public UndervoltSweepViewModel? Sweep { get; }

    public bool HasGpu => Gpu != null;
    public bool HasCpu => Cpu != null;
    public bool HasCo => Co != null;
    public bool HasGpuMux => GpuMux != null;
    public bool HasSweep => Sweep != null;

    /// <summary>The one "GPU" card hosts BOTH the clock offsets AND the GPU-mode/MUX switch (TuningView merged the
    /// two GPU cards so the fixed frame fits without a scrollbar), so the card exists while EITHER capability
    /// does. Each half keeps its own <see cref="HasGpu"/>/<see cref="HasGpuMux"/> visibility inside, so a
    /// MUX-only machine still shows the switch.</summary>
    public bool HasGpuOrMux => HasGpu || HasGpuMux;

    /// <summary>Whether the shared "CPU" card should exist at all — the power-mode picker and the undervolt
    /// controls are independent capabilities that share one header.</summary>
    public bool HasCpuCard => HasCpu || HasCo || HasSweep;

    /// <summary>Push the live power source to whichever child cares (only the guided sweep does today): it is
    /// refused unless the source is exactly AC. A no-op through a child that does not exist.</summary>
    public void SetOnAc(bool? onAc) => Sweep?.SetOnAc(onAc);

    public TuningViewModel(GpuViewModel? gpu, CpuViewModel? cpu, CoViewModel? co, GpuMuxViewModel? gpuMux,
                           UndervoltSweepViewModel? sweep = null)
    {
        Gpu = gpu;
        Cpu = cpu;
        Co = co;
        GpuMux = gpuMux;
        Sweep = sweep;
    }
}
