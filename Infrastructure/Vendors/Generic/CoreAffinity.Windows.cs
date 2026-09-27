using System.Numerics;
using System.Runtime.InteropServices;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// THE WINDOWS AFFINITY TRANSPORT, and nothing else: the two kernel32 calls that pin the thread and the one that
// enumerates physical cores, plus the struct the topology comes in. The ordering, the thermal policy and the
// oracle are in Infrastructure/Vendors/Generic (CpuLoadTest.cs / CpuStress.cs); the Linux twin is
// CoreAffinity.Linux.cs.
//
// PROCESSOR GROUPS. Windows splits more than 64 logical processors into groups of at most 64. SetThreadAffinityMask
// only reaches the calling thread's own group, so a machine with a second group would refuse every core in it — this
// uses SetThreadGroupAffinity (which takes the group number) instead, and reads the group number from the topology.
// Thread.BeginThreadAffinity is taken so the CLR does not migrate the worker between processors after the mask is
// set; Unpin releases it. The 64-bit mask is a property of the group, so the per-processor bit always fits.

internal sealed partial class CoreAffinity
{
    // LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore
    private const int RelationProcessorCore = 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationshipType, nint buffer, ref uint returnedLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadGroupAffinity(nint thread, in GroupAffinity groupAffinity, nint previousGroupAffinity);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public nuint Mask;        // KAFFINITY (ULONG_PTR)
        public ushort Group;
        public ushort Reserved0, Reserved1, Reserved2;
    }

    private partial CoreTopology TopologyCore()
    {
        // The offsets below are the 64-bit ABI (KAFFINITY is pointer-sized and GROUP_AFFINITY is 16 bytes); a
        // 32-bit process would parse the same buffer with different strides, so refuse rather than lie.
        if (IntPtr.Size != 8) return LogicalFallback("processor topology parsing is 64-bit only");

        try
        {
            uint length = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, 0, ref length);
            if (length == 0) return LogicalFallback("the processor topology could not be read");

            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                    return LogicalFallback($"GetLogicalProcessorInformationEx failed (error {Marshal.GetLastPInvokeError()})");

                var cores = new List<LogicalCore>();
                var offset = 0;
                while (offset < length)
                {
                    var relationship = Marshal.ReadInt32(buffer, offset);
                    var size = Marshal.ReadInt32(buffer, offset + 4);
                    if (size <= 0) break;

                    if (relationship == RelationProcessorCore)
                    {
                        // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX is 8 bytes (relationship + size);
                        // PROCESSOR_RELATIONSHIP then has Flags(1) EfficiencyClass(1) Reserved(20) GroupCount(2)
                        // and the first GROUP_AFFINITY at +24. The class is the hybrid-core class the OS
                        // assigned to THIS physical core (0 where it reports none); a higher value is the
                        // faster, less power-efficient core — Windows' own convention, and the one the
                        // cross-platform cluster partition reads (CpuLoadPolicy.PartitionIntoClusters).
                        var efficiencyClass = (int)Marshal.ReadByte(buffer, offset + 8 + 1);
                        var groupCount = (ushort)Marshal.ReadInt16(buffer, offset + 8 + 22);
                        for (var g = 0; g < groupCount; g++)
                        {
                            var groupOffset = offset + 8 + 24 + g * 16;
                            var mask = unchecked((ulong)(nuint)Marshal.ReadIntPtr(buffer, groupOffset));
                            var group = (ushort)Marshal.ReadInt16(buffer, groupOffset + 8);
                            var bit = mask == 0 ? -1 : BitOperations.TrailingZeroCount(mask);
                            // one logical processor per physical core, tagged with the core's cluster class
                            if (bit >= 0) cores.Add(new LogicalCore(bit, group, efficiencyClass));
                        }
                    }

                    offset += size;
                }

                if (cores.Count == 0) return LogicalFallback("the processor topology listed no cores");
                cores.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Processor.CompareTo(b.Processor));
                return TopologyWithClusters(cores, CoreTopologySource.PhysicalCores, null);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return LogicalFallback("the processor topology could not be read"); }
    }

    private partial bool PinCore(LogicalCore core, out string? error)
    {
        error = null;
        if (core.Processor is < 0 or >= 64)
        {
            error = $"logical processor {core.Processor} is outside its processor group's 64-bit mask";
            return false;
        }

        Thread.BeginThreadAffinity();
        var affinity = new GroupAffinity { Mask = (nuint)(1UL << core.Processor), Group = (ushort)core.Group };
        if (SetThreadGroupAffinity(GetCurrentThread(), in affinity, 0)) return true;

        error = $"SetThreadGroupAffinity failed (error {Marshal.GetLastPInvokeError()})";
        return false;
    }

    private partial void UnpinCore() => Thread.EndThreadAffinity();
}
