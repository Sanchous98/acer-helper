using System.Globalization;
using System.Runtime.InteropServices;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// THE LINUX AFFINITY TRANSPORT, and nothing else: one libc call to pin the thread and a read of the kernel's own
// physical-core description (thread_siblings_list). The ordering, the thermal policy and the oracle are in
// Infrastructure/Vendors/Generic (CpuLoadTest.cs / CpuStress.cs); the Windows twin is CoreAffinity.Windows.cs.
//
// WHY sched_setaffinity AND NOT pthread_setaffinity_np. Both are in libc and both are AOT-safe; sched_setaffinity
// takes pid 0 = "the calling thread", so it pins the worker the kernel will actually run, with no pthread_t handle
// to keep, and it needs no thread bookkeeping. The mask is the kernel's own cpu_set_t ABI: CPU_SETSIZE bits, 1024,
// i.e. 128 bytes, which is the size glibc passes and the kernel accepts.
//
// TOPOLOGY. thread_siblings_list is the set of logical processors that share a physical core (SMT siblings); one
// representative per DISTINCT set is one thread per physical core. The kernel's cpuset/cgroup may hide some CPUs
// from the process, in which case sched_setaffinity refuses the pin — reported as an error rather than silently
// running on the wrong core.
//
// HYBRID CLUSTERS. Linux exposes no direct analogue of Windows' EfficiencyClass, so the cluster class is DERIVED
// from /sys/devices/system/cpu/cpu*/cpu_capacity (the capacity-aware scheduler's per-cpu throughput estimate,
// relative to the most capable CPU of the same capacity domain). A physical core's capacity is the MAXIMUM over
// its SMT siblings (its threads share the core, so they share its capacity). When exactly two distinct capacities
// are present the higher one is the performance cluster, which matches Windows' "higher class = faster" rule and
// lets the shared partition name the same cluster on both platforms. WHERE IT IS NOT RELIABLE, stated plainly:
// cpu_capacity is a RELATIVE, scheduler-oriented number rather than a hardware class — it can be equal across
// clusters (an asymmetric-capacity kernel or a fixed-capacity driver tree), absent entirely (an older kernel, or
// a container that hides the file), or absent on SOME cpus only. In every one of those cases the class is left
// UNKNOWN (and the partition, CpuLoadPolicy.PartitionIntoClusters, returns no clusters) so the sweep falls back
// to loading all cores and says so — never a guessed split.

internal sealed partial class CoreAffinity
{
    private const int CpuSetBytes = 128;   // sizeof(cpu_set_t): CPU_SETSIZE (1024) / 8
    private const string CpuRoot = "/sys/devices/system/cpu";

    [LibraryImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
    private static unsafe partial int SchedSetAffinity(int pid, nuint cpusetSize, byte* mask);

    private partial CoreTopology TopologyCore()
    {
        try
        {
            var representatives = new List<(int Cpu, string Key)>();
            var physicalCores = new HashSet<string>(StringComparer.Ordinal);

            foreach (var dir in Directory.EnumerateDirectories(CpuRoot))
            {
                var name = Path.GetFileName(dir);
                if (name.Length < 4 || !name.StartsWith("cpu", StringComparison.Ordinal)
                    || !int.TryParse(name.AsSpan(3), out var cpu)) continue;

                var online = Path.Combine(dir, "online");
                if (File.Exists(online) && ReadInt(online) == 0) continue;   // offline CPU: not testable

                var siblingsPath = Path.Combine(dir, "topology", "thread_siblings_list");
                var key = File.Exists(siblingsPath)
                    ? CanonicalSiblings(File.ReadAllText(siblingsPath))
                    : string.Empty;
                if (key.Length == 0) key = cpu.ToString(CultureInfo.InvariantCulture);   // no topology -> each its own

                if (physicalCores.Add(key)) representatives.Add((cpu, key));   // one logical processor per physical core
            }

            if (representatives.Count == 0) return LogicalFallback("the CPU topology could not be read");

            var classOfCore = ClusterClasses(representatives);
            var cores = new List<LogicalCore>(representatives.Count);
            for (var i = 0; i < representatives.Count; i++)
                cores.Add(new LogicalCore(representatives[i].Cpu, 0, classOfCore[i]));

            cores.Sort((a, b) => a.Processor.CompareTo(b.Processor));
            return TopologyWithClusters(cores, CoreTopologySource.PhysicalCores, null);
        }
        catch { return LogicalFallback("the CPU topology could not be read"); }
    }

    /// <summary>The cluster class of each representative physical core, from cpu_capacity. UNKNOWN for every core
    /// unless exactly two distinct capacities are found across ALL of them (see the file header for why anything
    /// less is left unknown rather than guessed). The faster cluster gets the higher class, matching Windows.</summary>
    private static int[] ClusterClasses(IReadOnlyList<(int Cpu, string Key)> representatives)
    {
        var capacities = new long?[representatives.Count];
        var distinct = new HashSet<long>();
        for (var i = 0; i < representatives.Count; i++)
        {
            capacities[i] = CapacityOfPhysicalCore(representatives[i].Key);
            if (capacities[i] is { } c) distinct.Add(c);
        }

        var unknown = Enumerable.Repeat(LogicalCore.NoEfficiencyClass, representatives.Count).ToArray();
        if (distinct.Count != 2) return unknown;   // zero capacities (none readable), or one/too many: not two rails

        var high = distinct.Max();
        var classes = new int[representatives.Count];
        for (var i = 0; i < capacities.Length; i++)
            classes[i] = capacities[i] is { } c ? (c == high ? 1 : 0) : LogicalCore.NoEfficiencyClass;
        return classes;
    }

    /// <summary>The capacity of one physical core: the MAXIMUM cpu_capacity over its sibling logical processors,
    /// or null when the file is missing/unreadable for ANY sibling (so a partial answer is treated as no answer).</summary>
    private static long? CapacityOfPhysicalCore(string siblingList)
    {
        long? max = null;
        foreach (var cpu in ParseCpuList(siblingList))
        {
            var capacity = ReadLong(Path.Combine(CpuRoot, $"cpu{cpu}", "cpu_capacity"));
            if (capacity is null) return null;
            if (max is null || capacity > max) max = capacity;
        }
        return max;
    }

    /// <summary>The logical processors named by a canonical kernel cpu list ("0,4" / "0-3,8").</summary>
    private static IEnumerable<int> ParseCpuList(string list)
    {
        foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(part.AsSpan(0, dash), out var lo) && int.TryParse(part.AsSpan(dash + 1), out var hi))
                    for (var c = lo; c <= hi; c++) yield return c;
            }
            else if (int.TryParse(part, out var single)) yield return single;
        }
    }

    private partial bool PinCore(LogicalCore core, out string? error)
    {
        error = null;
        if (core.Processor < 0 || core.Processor >= CpuSetBytes * 8)
        {
            error = $"logical processor {core.Processor} is outside the cpu_set_t window";
            return false;
        }

        Span<byte> mask = stackalloc byte[CpuSetBytes];
        mask.Clear();
        mask[core.Processor >> 3] = (byte)(1 << (core.Processor & 7));

        int rc;
        unsafe
        {
            fixed (byte* p = mask)
                rc = SchedSetAffinity(0, CpuSetBytes, p);
        }
        if (rc == 0) return true;

        error = $"sched_setaffinity failed (errno {Marshal.GetLastPInvokeError()})";
        return false;
    }

    /// <summary>Nothing to release: the Linux pin is enforced by the kernel for the thread's lifetime, and there
    /// is no per-thread hold like Windows' BeginThreadAffinity.</summary>
    private partial void UnpinCore() { }

    /// <summary>Turn a kernel cpu list ("0-3,8") into a canonical, order-independent key so two siblings' lists
    /// match even if the kernel spells them differently.</summary>
    private static string CanonicalSiblings(string list)
    {
        var cpus = new HashSet<int>();
        foreach (var part in list.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(part.AsSpan(0, dash), out var lo) && int.TryParse(part.AsSpan(dash + 1), out var hi))
                    for (var c = lo; c <= hi; c++) cpus.Add(c);
            }
            else if (int.TryParse(part, out var single)) cpus.Add(single);
        }
        return string.Join(',', cpus.OrderBy(c => c));
    }

    private static int ReadInt(string path)
        => int.TryParse(File.ReadAllText(path).Trim(), out var value) ? value : 0;

    /// <summary>A sysfs file as an integer, or null when it is absent or does not parse — the caller treats null
    /// as "this core has no capacity reading", which is what makes an incomplete tree leave the class unknown.</summary>
    private static long? ReadLong(string path)
    {
        try { return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var value) ? value : null; }
        catch { return null; }
    }
}
