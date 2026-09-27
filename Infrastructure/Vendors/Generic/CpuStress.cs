using System.Numerics;
using System.Runtime.Intrinsics;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// THE SELF-CHECKING COMPUTE KERNEL AND ITS VALUE TYPES — the oracle at the heart of the load tool, and
// deliberately pure: no I/O, no OS name, no vendor name, no hardware handle. Everything here is a model the
// load runner reasons over and the tests can drive offline. It lives in Infrastructure/Vendors/Generic, NOT in
// Domain: the owner's ruling (2026-09-27) is that the automatic undervolt is in substance a WRAPPER over the
// manual voltage change (Application/Undervolt.cs) and carries no independent domain logic, so the whole
// feature — kernel included — is UI + Infrastructure. It keeps company here with the per-OS adapters and the
// pure Infrastructure policy the repo already tests offline (CurveOptimizerPolicy, CoAxis).
//
// WHY INTEGER AND NOT FLOATING POINT. The point of the tool is not heat, it is to catch a core returning a WRONG
// result. Integer +, *, shifts and xor are exact and associative/commutative-safe — the JIT is free to reorder,
// vectorise or contract them and the answer cannot legitimately change — so a difference between the checksum
// the core produced and the checksum pinned in the binary is a real miscompute, never a legal JIT
// reassociation. A floating-point self-check was considered and left out on purpose: FMA contraction and
// sum-order reassociation are legal and differ between the scalar and vector paths, so an FP comparison has
// false positives that would train the user to ignore the oracle. See the report for the full argument.

/// <summary>Instruction width the load kernel executes in. The widths all compute the SAME checksum by
/// construction (see <see cref="CpuStressKernel"/>), so the width selects the instruction mix the core is
/// stressed with, not the answer: a lighter width reaches a higher single-core boost (CoreCycler's SSE case),
/// while the wider vector path exercises a different transistor set.</summary>
public enum LoadWidth { Auto, Scalar, Vector128, Vector256 }

/// <summary>How one core's load test ended. A missing/zero iteration result is only meaningful together with the
/// outcome — <see cref="Skipped"/> is "the run stopped before reaching it", not "it passed".</summary>
public enum LoadOutcome
{
    /// <summary>Every block matched its golden checksum.</summary>
    Passed,
    /// <summary>A block's checksum did not match the pinned golden — the core returned a wrong result.</summary>
    ChecksumMismatch,
    /// <summary>The kernel threw (anything other than cancellation).</summary>
    Faulted,
    /// <summary>Not reached: the run stopped earlier (error, thermal abort, cancellation or budget).</summary>
    Skipped,
    /// <summary>The thermal cutoff stopped the core (over limit, or the sensor was unreadable — fail closed).</summary>
    ThermalAbort,
    /// <summary>Cooperative cancellation ended the core mid-block.</summary>
    Cancelled,
}

/// <summary>One logical processor, and the Windows processor group it belongs to. <see cref="Group"/> is always 0
/// on Linux; on Windows it is the group number from the processor-topology entries, so a machine with more than
/// 64 logical processors can address every core rather than only the first group.
///
/// <see cref="EfficiencyClass"/> is the OS's hybrid-core class for the PHYSICAL core this logical processor
/// belongs to: on Windows the <c>PROCESSOR_RELATIONSHIP.EfficiencyClass</c> byte, on Linux the class derived
/// from <c>cpu_capacity</c>. It is OS-NEUTRAL in meaning — on BOTH platforms a HIGHER value is the faster,
/// less power-efficient core — which is what lets one comparison name the performance cluster. It is
/// <see cref="LogicalCore.NoEfficiencyClass"/> where the OS reported no class (a homogeneous CPU, or a topology the
/// platform could not read); a consumer that needs a cluster must then treat the mapping as unavailable rather
/// than guess.</summary>
public readonly record struct LogicalCore(int Processor, int Group, int EfficiencyClass = -1)
{
    /// <summary>"The OS did not give this core an efficiency class." Distinct from any real class so the
    /// cluster-unknown fallback is representable and testable. Negative because both platforms report classes as
    /// non-negative bytes/capacities, so no real value can collide with it. The primary constructor spells the same
    /// value as the literal -1 rather than this name, because a parameter default cannot reference a member of the
    /// type it declares; the two must stay equal, and a test pins that they are.</summary>
    public const int NoEfficiencyClass = -1;
}

/// <summary>How the per-core list was obtained, so a caller knows whether it is testing one thread per physical
/// core (the intended case) or a flat logical list (the documented fallback).</summary>
public enum CoreTopologySource { PhysicalCores, LogicalProcessors }

/// <summary>The cores the load runner will visit, in the order it will visit them. <see cref="Cores"/> holds ONE
/// logical processor per physical core where the topology was readable, so SMT siblings are never loaded
/// together — loading both would lower the boost and hide exactly the single-core instability this tool exists
/// to find.
///
/// <see cref="PerformanceCluster"/> and <see cref="EfficiencyCluster"/> are the subsets of <see cref="Cores"/>
/// the OS attributed to each CPU cluster, in the runner's visit order, or null when it could not be determined
/// (no core carried an efficiency class, or all cores shared one). They are what lets a caller run a load over
/// ONE cluster — so a failure is attributable to it — rather than over all cores.</summary>
public sealed record CoreTopology(
    IReadOnlyList<LogicalCore> Cores,
    CoreTopologySource Source,
    string? Detail,
    IReadOnlyList<LogicalCore>? PerformanceCluster = null,
    IReadOnlyList<LogicalCore>? EfficiencyCluster = null)
{
    /// <summary>The cores of the cluster <paramref name="kind"/> names, or null when the topology could not
    /// partition the cores into clusters (the caller must then fall back and say so, never pretend the load was
    /// isolated).</summary>
    public IReadOnlyList<LogicalCore>? Cluster(CpuClusterKind kind) => kind switch
    {
        CpuClusterKind.Performance => PerformanceCluster,
        CpuClusterKind.Efficiency => EfficiencyCluster,
        _ => null,
    };
}

/// <summary>One core's result, in the vocabulary a future tuner can consume without knowing how the core was
/// pinned or how the kernel is spelled.</summary>
public sealed record CoreLoadResult(
    LogicalCore Core,
    LoadOutcome Outcome,
    LoadWidth Width,
    long Iterations,
    ulong Checksum,
    ulong Expected,
    double ElapsedMs,
    string? Detail);

/// <summary>
/// The deterministic integer workload and its oracle.
///
/// WHAT IS COMPUTED. Eight independent 32-bit lanes run the same integer recurrence (LCG multiply-add, an xor
/// shift, a multiply, a second xor shift). The lanes are independent because that is what lets the scalar path
/// and a <c>Vector128</c>/<c>Vector256</c> path compute the SAME result: a vector lane and a scalar lane execute
/// the identical arithmetic, so the width changes the instruction mix and nothing else. The lane results are
/// folded with xor into a 64-bit word (position-sensitive, because each lane is shifted by its own byte) and
/// finished with a SplitMix64 avalanche.
///
/// WHY THE ANSWER CANNOT LEGITIMATELY CHANGE. Every operation is exact integer arithmetic that C# defines as
/// wrapping (<c>unchecked</c>) and that is associative and commutative where it needs to be. The JIT may
/// vectorise the scalar loop, or keep the vector loop scalar, and the value is the same; a bit flip, an
/// erroneous multiply or a wrong lane value changes it. The expected value is a LITERAL pinned in this type
/// (and in the tests), never recomputed on the core under test — recomputing it there would let a systematic
/// miscompute agree with itself.
///
/// DEAD-CODE ELIMINATION. Every computation is published through <see cref="Volatile"/> into a static sink before
/// it is returned, so the JIT cannot delete a block whose result nothing observable depends on.
/// </summary>
public static class CpuStressKernel
{
    /// <summary>The number of independent lanes; chosen to fill one <c>Vector256&lt;uint&gt;</c> exactly.</summary>
    public const int Lanes = 8;

    /// <summary>The oracle's block size: the iteration count one checksum is pinned for, and the unit the runner
    /// checks and counts in. Small enough that a miscompute is caught within a few milliseconds of load, large
    /// enough that the per-block overhead is noise.</summary>
    public const int DefaultBlockIterations = 1 << 16;   // 65536

    private const uint LcgMul = 1664525u;
    private const uint LcgAdd = 1013904223u;
    private const uint MixMul = 2654435761u;
    private const int Shift1 = 13;
    private const int Shift2 = 16;
    private const uint SeedBase = 0x9E3779B9u;
    private const uint SeedStep = 0x85EBCA6Bu;

    private static readonly Vector128<uint> Mul128 = Vector128.Create(LcgMul);
    private static readonly Vector128<uint> Add128 = Vector128.Create(LcgAdd);
    private static readonly Vector128<uint> Mix128 = Vector128.Create(MixMul);
    private static readonly Vector256<uint> Mul256 = Vector256.Create(LcgMul);
    private static readonly Vector256<uint> Add256 = Vector256.Create(LcgAdd);
    private static readonly Vector256<uint> Mix256 = Vector256.Create(MixMul);

    /// <summary>An observable sink so no block can be eliminated as unused. Written with a volatile store; its
    /// value is never read by logic.</summary>
    private static ulong _sink;

    /// <summary>The width that will actually execute on this CPU for a request: <see cref="LoadWidth.Auto"/>
    /// picks the widest hardware-accelerated path, and an explicitly requested width that the CPU cannot
    /// accelerate steps down to the next one so the reported width is never a promise the instruction mix did not
    /// keep. Every width computes the same checksum, so this never changes the oracle.</summary>
    public static LoadWidth Resolve(LoadWidth requested) => requested switch
    {
        LoadWidth.Scalar => LoadWidth.Scalar,
        LoadWidth.Vector256 => Vector256.IsHardwareAccelerated ? LoadWidth.Vector256
                              : Vector128.IsHardwareAccelerated ? LoadWidth.Vector128 : LoadWidth.Scalar,
        LoadWidth.Vector128 => Vector128.IsHardwareAccelerated ? LoadWidth.Vector128 : LoadWidth.Scalar,
        _ => Vector256.IsHardwareAccelerated ? LoadWidth.Vector256
           : Vector128.IsHardwareAccelerated ? LoadWidth.Vector128 : LoadWidth.Scalar,
    };

    /// <summary>The golden checksum for one default block. Identical for every width on purpose (see the type
    /// remarks). Derived offline from the recurrence, not from running this code, and pinned by
    /// <c>CpuStressTests</c>.</summary>
    public static ulong GoldenChecksum(LoadWidth width) => 0x6117C64471DCD42EUL;

    /// <summary>The oracle: true when a core produced the value the algorithm defines. Exists as a named
    /// function so the comparison itself, and not only the kernel, is testable.</summary>
    public static bool Matches(ulong actual, ulong expected) => actual == expected;

    /// <summary>Compute the checksum for <paramref name="iterations"/> steps of <paramref name="width"/>,
    /// cooperatively cancellable. The expected value for the runtime block is <see cref="GoldenChecksum"/>; this
    /// method is the computation the core under test performs, and the tests pin small counts of it (see
    /// <c>CpuStressTests.TheChecksumIsStableAtPinnedCounts</c>) so the algorithm is fixed independently of any
    /// one machine.</summary>
    public static ulong Compute(LoadWidth width, int iterations, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(iterations);

        var checksum = Resolve(width) switch
        {
            LoadWidth.Vector256 => ComputeVector256(iterations, cancellation),
            LoadWidth.Vector128 => ComputeVector128(iterations, cancellation),
            _ => ComputeScalar(iterations, cancellation),
        };

        Volatile.Write(ref _sink, checksum);   // observable: nothing may delete the work
        return checksum;
    }

    private static ulong ComputeScalar(int iterations, CancellationToken cancellation)
    {
        Span<uint> acc = stackalloc uint[Lanes];
        InitSeeds(acc);

        for (var it = 0; it < iterations; it++)
        {
            if ((it & 0x3FF) == 0) cancellation.ThrowIfCancellationRequested();
            for (var lane = 0; lane < Lanes; lane++) acc[lane] = Step(acc[lane]);
        }

        return Combine(acc);
    }

    private static ulong ComputeVector128(int iterations, CancellationToken cancellation)
    {
        Span<uint> seeds = stackalloc uint[Lanes];
        InitSeeds(seeds);
        var lo = Vector128.Create(seeds[0], seeds[1], seeds[2], seeds[3]);
        var hi = Vector128.Create(seeds[4], seeds[5], seeds[6], seeds[7]);

        for (var it = 0; it < iterations; it++)
        {
            if ((it & 0x3FF) == 0) cancellation.ThrowIfCancellationRequested();
            lo = Step128(lo);
            hi = Step128(hi);
        }

        Span<uint> acc = stackalloc uint[Lanes];
        lo.CopyTo(acc);
        hi.CopyTo(acc[4..]);
        return Combine(acc);
    }

    private static ulong ComputeVector256(int iterations, CancellationToken cancellation)
    {
        Span<uint> seeds = stackalloc uint[Lanes];
        InitSeeds(seeds);
        var v = Vector256.Create(seeds[0], seeds[1], seeds[2], seeds[3], seeds[4], seeds[5], seeds[6], seeds[7]);

        for (var it = 0; it < iterations; it++)
        {
            if ((it & 0x3FF) == 0) cancellation.ThrowIfCancellationRequested();
            v = Step256(v);
        }

        Span<uint> acc = stackalloc uint[Lanes];
        v.CopyTo(acc);
        return Combine(acc);
    }

    private static uint Step(uint x)
    {
        x = unchecked(x * LcgMul + LcgAdd);
        x ^= x >> Shift1;
        x = unchecked(x * MixMul);
        x ^= x >> Shift2;
        return x;
    }

    private static Vector128<uint> Step128(Vector128<uint> v)
    {
        v = v * Mul128 + Add128;
        v ^= Vector128.ShiftRightLogical(v, Shift1);
        v *= Mix128;
        v ^= Vector128.ShiftRightLogical(v, Shift2);
        return v;
    }

    private static Vector256<uint> Step256(Vector256<uint> v)
    {
        v = v * Mul256 + Add256;
        v ^= Vector256.ShiftRightLogical(v, Shift1);
        v *= Mix256;
        v ^= Vector256.ShiftRightLogical(v, Shift2);
        return v;
    }

    private static void InitSeeds(Span<uint> seeds)
    {
        for (var lane = 0; lane < Lanes; lane++)
            seeds[lane] = unchecked(SeedBase + (uint)lane * SeedStep);
    }

    private static ulong Combine(ReadOnlySpan<uint> lanes)
    {
        var h = 0UL;
        for (var lane = 0; lane < Lanes; lane++)
            h ^= (ulong)lanes[lane] << (lane * 8);   // lane-position-sensitive
        h ^= h >> 29;
        h = unchecked(h * 0xBF58476D1CE4E5B9UL);
        h ^= h >> 32;
        return h;
    }
}
