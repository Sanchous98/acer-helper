using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// THE PER-CORE AFFINITY ADAPTER'S CROSS-PLATFORM HALF, and nothing else: the contract members forward to the
// OS partial (CoreAffinity.Windows.cs / CoreAffinity.Linux.cs), which own every platform call. This is the same
// split Autostart uses — the shared declaration here, the mechanism in the suffixed files, no preprocessor
// directives — and it is what keeps Win32 P/Invoke and libc out of the other TFM entirely.
//
// WHY INFRASTRUCTURE AND NOT A DOMAIN PORT. Thread affinity and processor topology are OS integration by the
// map's own rule ("контракт, который моделирует технологию вендора или ОС … интеграция"), and the runner that
// names the contract is the automatic undervolt's, which the owner placed in Infrastructure (it is a wrapper
// over the manual voltage change). Nothing in Domain needs to name it, so no Domain port is needed and none is
// forced. See Infrastructure/Vendors/Generic/CpuLoadTest.cs.

/// <summary>Per-OS affinity/topology adapter for the CPU load tool. The contract lives in Infrastructure (here,
/// beside this adapter); this is its only implementation, selected by file-name suffix per TFM. Thin by design:
/// every method is an OS call or a sysfs read, so it is deliberately not unit-tested — the runner is (see
/// CpuStressTests).</summary>
internal sealed partial class CoreAffinity : ICoreAffinity
{
    public CoreTopology Topology() => TopologyCore();

    public bool PinCurrentThread(LogicalCore core, out string? error) => PinCore(core, out error);

    public void UnpinCurrentThread() => UnpinCore();

    // ---- the per-OS halves ----

    private partial CoreTopology TopologyCore();

    private partial bool PinCore(LogicalCore core, out string? error);

    private partial void UnpinCore();

    /// <summary>The documented fallback when the OS cannot report physical-core topology: the flat logical list
    /// <c>0..ProcessorCount-1</c> in group 0, with NO cluster partition (the flat list cannot be attributed to a
    /// cluster). Reported through <see cref="CoreTopology.Source"/> so a caller can tell it apart from a real
    /// topology, because on a fallback the run may load SMT siblings together.</summary>
    private static CoreTopology LogicalFallback(string? detail)
    {
        var count = Math.Max(1, Environment.ProcessorCount);
        var cores = new List<LogicalCore>(count);
        for (var i = 0; i < count; i++) cores.Add(new LogicalCore(i, 0));
        return new CoreTopology(cores, CoreTopologySource.LogicalProcessors, detail);
    }

    /// <summary>Build the topology from the cores a platform enumerated, with the cluster partition derived from
    /// their efficiency classes (the pure rule is <see cref="CpuLoadPolicy.PartitionIntoClusters"/>). Used by both
    /// OS halves so the split is decided once.</summary>
    private static CoreTopology TopologyWithClusters(IReadOnlyList<LogicalCore> cores, CoreTopologySource source, string? detail)
    {
        var (performance, efficiency) = CpuLoadPolicy.PartitionIntoClusters(cores);
        return new CoreTopology(cores, source, detail, performance, efficiency);
    }
}
