using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// THE SELF-CHECKING COMPUTE KERNELS AND THEIR VALUE TYPES — the oracle at the heart of the load tool, and
// deliberately pure: no I/O, no OS name, no vendor name, no hardware handle. Everything here is a model the
// load runner reasons over and the tests can drive offline. It lives in Infrastructure/Vendors/Generic, NOT in
// Domain: the owner's ruling (2026-09-27) is that the automatic undervolt is in substance a WRAPPER over the
// manual voltage change (Application/Undervolt.cs) and carries no independent domain logic, so the whole
// feature — kernel included — is UI + Infrastructure. It keeps company here with the per-OS adapters and the
// pure Infrastructure policy the repo already tests offline (CurveOptimizerPolicy, CoAxis).
//
// WHY THE ORACLE IS EXACT AND NOT TOLERANT. The point of the tool is not heat, it is to catch an execution unit
// returning a WRONG result. Every mix therefore computes over values where the answer is bit-identical no matter
// how the JIT legally reorders, vectorises or contracts it — so a difference between the checksum a core produced
// and the checksum pinned in the binary is a real miscompute, never a legal reassociation. There is NO tolerance
// anywhere: every comparison is `==` on an exact value.
//
// WHY MULTIPLE MIXES. Integer +, *, shifts and xor are exact (C# wrapping arithmetic) and associative/
// commutative-safe. The integer mix is kept, but on its own it is a WEAK oracle: an undervolt tends to surface in
// the FP/FMA/AVX execution units and the cache/memory subsystem, not the integer ALUs, and a small L1-resident
// integer load never reaches the voltage/current-droop corner a heavy vector load reaches. So there are now three
// mixes, run by every probe:
//
//   1. INTEGER — the original 8-lane integer recurrence, scalar through Vector512.
//   2. FPFMA   — a widest-hardware-width double FMA recurrence. This was previously left out because INEXACT
//                floating point is not a safe oracle (FMA contraction and sum-order reassociation differ between
//                the scalar and vector paths, so an FP comparison would false-positive). That reasoning was about
//                inexact FP and is unchanged; this mix is different: it computes on INTEGER-VALUED doubles whose
//                every product, addend, quotient and partial sum is exactly representable, so all legal
//                reassociations and FMA contractions yield a bit-identical result. The exactness argument is
//                spelled out on the FP half below.
//   3. MEMORY  — a strided streaming walk over a working set larger than L2/L3 with an exact integer checksum, so
//                the memory controller / Infinity Fabric rails are stressed rather than only the ALUs.
//
// WHAT WAS EXCLUDED, AND WHY IT STILL IS. Inexact floating point is still deliberately absent: a mix that summed
// 1/n or multiplied arbitrary reals would have legal scalar/vector differences and train the user to ignore the
// oracle. Exact-valued FP is safe; inexact FP is not.

/// <summary>Instruction width the load kernels execute in. The widths all compute the SAME checksum by
/// construction (see <see cref="CpuStressKernel"/>), so the width selects the instruction mix the core is
/// stressed with, not the answer: a lighter width reaches a higher single-core boost (CoreCycler's SSE case),
/// while the wider vector path exercises a different transistor set. <see cref="Vector512"/> is the top rung and
/// is only used where the hardware accelerates 512-bit; every request steps down to the widest accelerated path
/// the CPU actually offers. It is deliberately NOT the only rung: most CPUs lack AVX-512, on Zen 5 mobile the
/// 256-bit path can be equally hot (the datapath may be 256-bit double-pumped), sustained heavy 512-bit can
/// downclock and MASK droop, and the narrower integer/SSE paths and the memory mix catch distinct failure modes
/// (single-core boost vs cache/UMC). The ladder and the multi-mix stay.</summary>
public enum LoadWidth { Auto, Scalar, Vector128, Vector256, Vector512 }

/// <summary>Which self-checking computation a block ran. The runner runs every configured mix per core and names
/// the one that failed in <see cref="CoreLoadResult.FailedMix"/>, because "the integer core was fine but the FMA
/// unit miscomputed" is a materially different signal from a generic failure.</summary>
public enum LoadMix { Integer, FpFma, Memory }

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
/// pinned or how the kernel is spelled. <see cref="FailedMix"/> is non-null only when an oracle error or fault
/// ended the core, and names WHICH mix failed; it is carried in <see cref="Detail"/> as well.</summary>
public sealed record CoreLoadResult(
    LogicalCore Core,
    LoadOutcome Outcome,
    LoadWidth Width,
    long Iterations,
    ulong Checksum,
    ulong Expected,
    double ElapsedMs,
    string? Detail,
    LoadMix? FailedMix = null);

/// <summary>
/// The deterministic self-checking workloads and their oracle.
///
/// ============================ THE INTEGER MIX ============================
///
/// WHAT IS COMPUTED. Eight independent 32-bit lanes run the same integer recurrence (LCG multiply-add, an xor
/// shift, a multiply, a second xor shift). The lanes are independent because that is what lets the scalar path
/// and a <c>Vector128</c>/<c>Vector256</c>/<c>Vector512</c> path compute the SAME result: a vector lane and a
/// scalar lane execute the identical arithmetic, so the width changes the instruction mix and nothing else. The
/// lane results are folded with xor into a 64-bit word (position-sensitive, because each lane is shifted by its
/// own byte) and finished with a SplitMix64 avalanche.
///
/// WHY THE ANSWER CANNOT LEGITIMATELY CHANGE. Every operation is exact integer arithmetic that C# defines as
/// wrapping (<c>unchecked</c>) and that is associative and commutative where it needs to be. The JIT may
/// vectorise the scalar loop, or keep the vector loop scalar, and the value is the same; a bit flip, an
/// erroneous multiply or a wrong lane value changes it.
///
/// ============================ THE FP/FMA MIX ============================
///
/// WHAT IS COMPUTED. Eight independent lanes hold INTEGER-VALUED <see cref="double"/>s in <c>[0, 2^20)</c> and run
/// <c>x = fma(A, x, C); x -= M * floor(x / M); x = fma(B, x, D); x -= M * floor(x / M);</c> with
/// <c>M = 2^20</c> (a power of two), <c>A = 3</c>, <c>B = 5</c> and small integer <c>C</c>, <c>D</c>.
///
/// WHY THIS CANNOT FALSE-POSITIVE — the exactness argument, step by step:
///   * Every lane value is an integer in <c>[0, 2^20)</c>, so every product <c>A*x</c>, <c>B*x</c> is an integer
///     in <c>[0, 5*2^20)</c> and every partial sum is an integer below <c>2^23</c> — FAR inside the range where
///     all integers up to <c>2^53</c> are exactly representable as a double. No operation rounds.
///   * <c>M</c> is a power of two, so <c>x / M</c> and <c>x * (1/M)</c> are EXACT (an exponent adjustment of an
///     exact value, never a mantissa rounding), and <c>floor</c> of an exact value is exact.
///   * <c>M * floor(x / M)</c> is an exact integer product and the subtraction is an exact integer subtraction.
///     The result is again an integer in <c>[0, M)</c>.
///   * FMA contraction vs a separate multiply and add: because the product and the addend are both exact and the
///     sum is exactly representable, the fused (single-rounding) and unfused (two-rounding) forms give the
///     bit-identical result. Likewise every legal reassociation of this per-lane recurrence is exact.
///   Therefore the scalar path and every SIMD path — with FMA and without it — must produce bit-identical lane
///   values, and the exact-equality oracle is sound. A NaN or an infinity, which a genuinely bad undervolt can
///   produce, is still caught because it cannot equal the pinned golden.
///
/// ============================ THE MEMORY MIX ============================
///
/// WHAT IS COMPUTED. A deterministic strided walk across a read-only pool of <see cref="MemoryWorkingSetElements"/>
/// 64-bit words (32 MB, deliberately larger than L2 and L3), XOR-accumulating each word into a rolling 64-bit
/// hash. The stride is a large odd constant so consecutive touches land in different cache lines spread across the
/// whole working set, which is what puts pressure on the cache hierarchy, the memory controller and the Infinity
/// Fabric rather than on the ALUs. The pool is initialised once from a fixed seed and is never written, so the
/// walk is deterministic across calls and machines; the checksum is exact integer arithmetic, so it too cannot
/// false-positive.
///
/// GOLDEN VALUES. The expected value is a LITERAL pinned in this type (and in the tests), never recomputed on the
/// core under test — recomputing it there would let a systematic miscompute agree with itself.
///
/// DEAD-CODE ELIMINATION. Every computation is published through <see cref="Volatile"/> into a static sink before
/// it is returned, so the JIT cannot delete a block whose result nothing observable depends on.
/// </summary>
public static class CpuStressKernel
{
    /// <summary>The number of independent lanes; chosen to fill one <c>Vector256&lt;uint&gt;</c> exactly, and used
    /// unchanged as the lane count for the FP mix (one <c>Vector512&lt;double&gt;</c> exactly). Independent lanes
    /// are what let every width compute the same answer.</summary>
    public const int Lanes = 8;

    /// <summary>The oracle's block size: the iteration count one checksum is pinned for, and the unit the runner
    /// checks and counts in. Small enough that a miscompute is caught within a few milliseconds of load, large
    /// enough that the per-block overhead is noise.</summary>
    public const int DefaultBlockIterations = 1 << 16;   // 65536

    /// <summary>The mix set every probe runs by default, in the order it runs them. Integer stays first so a
    /// pinned integer golden is still the cheapest signal; the FP and memory mixes follow.</summary>
    public static IReadOnlyList<LoadMix> DefaultMixes { get; } = [LoadMix.Integer, LoadMix.FpFma, LoadMix.Memory];

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
    private static readonly Vector512<uint> Mul512 = Vector512.Create(LcgMul);
    private static readonly Vector512<uint> Add512 = Vector512.Create(LcgAdd);
    private static readonly Vector512<uint> Mix512 = Vector512.Create(MixMul);

    // ---- the FP/FMA mix's constants (see the exactness argument in the type remarks) ----
    private const double FpModulus = 1048576.0;            // 2^20 — a power of two, so / and floor stay exact
    private const double FpInvModulus = 1.0 / 1048576.0;   // exactly 2^-20
    private const double FpMulA = 3.0;
    private const double FpAddC = 12345.0;
    private const double FpMulB = 5.0;
    private const double FpAddD = 6789.0;
    private const double FpSeedBase = 1.0;
    private const double FpSeedStep = 7919.0;

    private static readonly Vector128<double> A128 = Vector128.Create(FpMulA);
    private static readonly Vector128<double> C128 = Vector128.Create(FpAddC);
    private static readonly Vector128<double> B128 = Vector128.Create(FpMulB);
    private static readonly Vector128<double> D128 = Vector128.Create(FpAddD);
    private static readonly Vector128<double> Mod128 = Vector128.Create(FpModulus);
    private static readonly Vector128<double> Inv128 = Vector128.Create(FpInvModulus);
    private static readonly Vector256<double> A256 = Vector256.Create(FpMulA);
    private static readonly Vector256<double> C256 = Vector256.Create(FpAddC);
    private static readonly Vector256<double> B256 = Vector256.Create(FpMulB);
    private static readonly Vector256<double> D256 = Vector256.Create(FpAddD);
    private static readonly Vector256<double> Mod256 = Vector256.Create(FpModulus);
    private static readonly Vector256<double> Inv256 = Vector256.Create(FpInvModulus);
    private static readonly Vector512<double> A512 = Vector512.Create(FpMulA);
    private static readonly Vector512<double> C512 = Vector512.Create(FpAddC);
    private static readonly Vector512<double> B512 = Vector512.Create(FpMulB);
    private static readonly Vector512<double> D512 = Vector512.Create(FpAddD);
    private static readonly Vector512<double> Mod512 = Vector512.Create(FpModulus);
    private static readonly Vector512<double> Inv512 = Vector512.Create(FpInvModulus);

    // ---- the memory mix ----
    /// <summary>Working-set size in 64-bit words: 2^22 words = 32 MB, deliberately larger than the L2 and L3 of
    /// the target part so the walk reaches main memory.</summary>
    public const int MemoryWorkingSetElements = 1 << 22;

    /// <summary>Stride in words between consecutive touches: odd and far larger than a cache line, so each access
    /// lands on a different line across the whole working set and spatial prefetch cannot hide the latency.</summary>
    private const int MemoryStride = 4099;

    private const ulong MemorySeed = 0x9E3779B97F4A7C15UL;
    private const ulong MemoryAccSeed = 0x243F6A8885A308D3UL;
    private const ulong MemoryAccMul = 0x100000001B3UL;   // FNV-1a prime

    private static readonly ulong[] MemoryPool = BuildMemoryPool();

    /// <summary>An observable sink so no block can be eliminated as unused. Written with a volatile store; its
    /// value is never read by logic. It is written ONCE PER BLOCK (in <see cref="Compute"/>), not per iteration,
    /// so across the concurrently-loaded cores it is one store per ~sub-millisecond block — far too rare to be the
    /// cache-line ping-pong that would leave a core idle, and deliberately kept because the alternative (a sink
    /// per iteration) would be a real shared-line bottleneck. The load's cost is in the block, not the sink.</summary>
    private static ulong _sink;

    /// <summary>The width that will actually execute on this CPU for a request: <see cref="LoadWidth.Auto"/>
    /// picks the widest hardware-accelerated path (Vector512 when the CPU accelerates it, else Vector256, else
    /// Vector128, else scalar), and an explicitly requested width that the CPU cannot accelerate steps down to the
    /// next one so the reported width is never a promise the instruction mix did not keep. Every width computes the
    /// same checksum, so this never changes the oracle.</summary>
    public static LoadWidth Resolve(LoadWidth requested) => requested switch
    {
        LoadWidth.Scalar => LoadWidth.Scalar,
        LoadWidth.Vector512 => Vector512.IsHardwareAccelerated ? LoadWidth.Vector512 : ResolveAuto(),
        LoadWidth.Vector256 => Vector256.IsHardwareAccelerated ? LoadWidth.Vector256
                              : Vector128.IsHardwareAccelerated ? LoadWidth.Vector128 : LoadWidth.Scalar,
        LoadWidth.Vector128 => Vector128.IsHardwareAccelerated ? LoadWidth.Vector128 : LoadWidth.Scalar,
        _ => ResolveAuto(),
    };

    private static LoadWidth ResolveAuto()
        => Vector512.IsHardwareAccelerated ? LoadWidth.Vector512
         : Vector256.IsHardwareAccelerated ? LoadWidth.Vector256
         : Vector128.IsHardwareAccelerated ? LoadWidth.Vector128
         : LoadWidth.Scalar;

    /// <summary>The golden checksum for one default block of the INTEGER mix. Identical for every width on
    /// purpose (see the type remarks). Kept as the width-only overload for the callers that only know the
    /// width.</summary>
    public static ulong GoldenChecksum(LoadWidth width) => GoldenChecksum(LoadMix.Integer, width);

    /// <summary>The golden checksum for one default block of <paramref name="mix"/> at <paramref name="width"/>.
    /// Derived offline from the algorithm and pinned here (and in the tests); the width never changes the value for
    /// the Integer and FpFma mixes (the widths differ only in instruction mix) and is ignored by the Memory mix.
    /// </summary>
    public static ulong GoldenChecksum(LoadMix mix, LoadWidth width) => mix switch
    {
        LoadMix.FpFma => FpGolden,
        LoadMix.Memory => MemoryGolden,
        _ => IntegerGolden,
    };

    /// <summary>The oracle: true when a core produced the value the algorithm defines. Exists as a named
    /// function so the comparison itself, and not only the kernel, is testable. EXACT equality; no tolerance.</summary>
    public static bool Matches(ulong actual, ulong expected) => actual == expected;

    /// <summary>Compute the integer mix's checksum for <paramref name="iterations"/> steps of
    /// <paramref name="width"/>, cooperatively cancellable.</summary>
    public static ulong Compute(LoadWidth width, int iterations, CancellationToken cancellation = default)
        => Compute(LoadMix.Integer, width, iterations, cancellation);

    /// <summary>Compute one block of the named mix. The runner calls this once per (mix, core, block); the tests
    /// pin small counts of it so the algorithm is fixed independently of any one machine.</summary>
    public static ulong Compute(LoadMix mix, LoadWidth width, int iterations, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(iterations);

        var checksum = mix switch
        {
            LoadMix.FpFma => ComputeFp(width, iterations, useFma: true, cancellation),
            LoadMix.Memory => ComputeMemory(iterations, cancellation),
            _ => ComputeInteger(width, iterations, cancellation),
        };

        Volatile.Write(ref _sink, checksum);   // observable: nothing may delete the work
        return checksum;
    }

    // ============================ the integer mix ============================

    private static ulong ComputeInteger(LoadWidth width, int iterations, CancellationToken cancellation)
    {
        var checksum = Resolve(width) switch
        {
            LoadWidth.Vector512 => ComputeIntegerVector512(iterations, cancellation),
            LoadWidth.Vector256 => ComputeIntegerVector256(iterations, cancellation),
            LoadWidth.Vector128 => ComputeIntegerVector128(iterations, cancellation),
            _ => ComputeIntegerScalar(iterations, cancellation),
        };
        return checksum;
    }

    private static ulong ComputeIntegerScalar(int iterations, CancellationToken cancellation)
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

    private static ulong ComputeIntegerVector128(int iterations, CancellationToken cancellation)
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

    private static ulong ComputeIntegerVector256(int iterations, CancellationToken cancellation)
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

    private static ulong ComputeIntegerVector512(int iterations, CancellationToken cancellation)
    {
        Span<uint> seeds = stackalloc uint[Lanes];
        InitSeeds(seeds);
        var lanes = Vector256.Create(seeds[0], seeds[1], seeds[2], seeds[3], seeds[4], seeds[5], seeds[6], seeds[7]);
        // The 8 independent lanes occupy the low half; the high half carries a duplicate so a full 512-bit step
        // is executed every iteration. Only the low half is folded, so the answer is identical to every other
        // width — the high half is deterministic busy-work on the same transistor set.
        var v = Vector512.Create(lanes, lanes);

        for (var it = 0; it < iterations; it++)
        {
            if ((it & 0x3FF) == 0) cancellation.ThrowIfCancellationRequested();
            v = Step512(v);
        }

        Span<uint> acc = stackalloc uint[16];
        v.CopyTo(acc);
        return Combine(acc[..Lanes]);
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

    private static Vector512<uint> Step512(Vector512<uint> v)
    {
        v = v * Mul512 + Add512;
        v ^= v >> Shift1;
        v *= Mix512;
        v ^= v >> Shift2;
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
        return Mix64(h);
    }

    // ============================ the FP/FMA mix ============================

    /// <summary>Compute the FP/FMA mix. <paramref name="useFma"/> selects the fused path where the CPU supports
    /// it; the two paths are asserted equal by the tests because every value is exact (see the type remarks).
    /// Exposed as an internal test hook so "FMA vs non-FMA agree" is a fact about this code rather than a claim.</summary>
    internal static ulong ComputeFp(LoadWidth width, int iterations, bool useFma, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(iterations);

        var checksum = Resolve(width) switch
        {
            LoadWidth.Vector512 => ComputeFpVector512(iterations, useFma, cancellation),
            LoadWidth.Vector256 => ComputeFpVector256(iterations, useFma, cancellation),
            LoadWidth.Vector128 => ComputeFpVector128(iterations, useFma, cancellation),
            _ => ComputeFpScalar(iterations, useFma, cancellation),
        };
        Volatile.Write(ref _sink, checksum);
        return checksum;
    }

    private static ulong ComputeFpScalar(int iterations, bool useFma, CancellationToken cancellation)
    {
        Span<double> acc = stackalloc double[Lanes];
        Span<ulong> sum = stackalloc ulong[Lanes];
        InitFpSeeds(acc);
        for (var lane = 0; lane < Lanes; lane++) sum[lane] = 0;   // stackalloc is not zeroed
        for (var it = 0; it < iterations; it++)
        {
            if ((it & 0x3FF) == 0) cancellation.ThrowIfCancellationRequested();
            for (var lane = 0; lane < Lanes; lane++)
            {
                acc[lane] = FpStepScalar(acc[lane], useFma);
                sum[lane] = unchecked(sum[lane] + BitConverter.DoubleToUInt64Bits(acc[lane]));
            }
        }
        return CombineFp(sum, acc);
    }

    private static ulong ComputeFpVector128(int iterations, bool useFma, CancellationToken cancellation)
    {
        Span<double> seeds = stackalloc double[Lanes];
        InitFpSeeds(seeds);
        var v0 = Vector128.Create(seeds[0], seeds[1]);
        var v1 = Vector128.Create(seeds[2], seeds[3]);
        var v2 = Vector128.Create(seeds[4], seeds[5]);
        var v3 = Vector128.Create(seeds[6], seeds[7]);
        var s0 = Vector128<ulong>.Zero; var s1 = Vector128<ulong>.Zero;
        var s2 = Vector128<ulong>.Zero; var s3 = Vector128<ulong>.Zero;

        for (var it = 0; it < iterations; it++)
        {
            if ((it & 0x3FF) == 0) cancellation.ThrowIfCancellationRequested();
            v0 = FpStep128(v0, useFma); s0 += Vector128.AsUInt64(v0);
            v1 = FpStep128(v1, useFma); s1 += Vector128.AsUInt64(v1);
            v2 = FpStep128(v2, useFma); s2 += Vector128.AsUInt64(v2);
            v3 = FpStep128(v3, useFma); s3 += Vector128.AsUInt64(v3);
        }

        Span<double> acc = stackalloc double[Lanes];
        Span<ulong> sum = stackalloc ulong[Lanes];
        v0.CopyTo(acc); s0.CopyTo(sum);
        v1.CopyTo(acc[2..]); s1.CopyTo(sum[2..]);
        v2.CopyTo(acc[4..]); s2.CopyTo(sum[4..]);
        v3.CopyTo(acc[6..]); s3.CopyTo(sum[6..]);
        return CombineFp(sum, acc);
    }

    private static ulong ComputeFpVector256(int iterations, bool useFma, CancellationToken cancellation)
    {
        Span<double> seeds = stackalloc double[Lanes];
        InitFpSeeds(seeds);
        var lo = Vector256.Create(seeds[0], seeds[1], seeds[2], seeds[3]);
        var hi = Vector256.Create(seeds[4], seeds[5], seeds[6], seeds[7]);
        var sLo = Vector256<ulong>.Zero;
        var sHi = Vector256<ulong>.Zero;

        for (var it = 0; it < iterations; it++)
        {
            if ((it & 0x3FF) == 0) cancellation.ThrowIfCancellationRequested();
            lo = FpStep256(lo, useFma); sLo += Vector256.AsUInt64(lo);
            hi = FpStep256(hi, useFma); sHi += Vector256.AsUInt64(hi);
        }

        Span<double> acc = stackalloc double[Lanes];
        Span<ulong> sum = stackalloc ulong[Lanes];
        lo.CopyTo(acc); sLo.CopyTo(sum);
        hi.CopyTo(acc[4..]); sHi.CopyTo(sum[4..]);
        return CombineFp(sum, acc);
    }

    private static ulong ComputeFpVector512(int iterations, bool useFma, CancellationToken cancellation)
    {
        Span<double> seeds = stackalloc double[Lanes];
        InitFpSeeds(seeds);
        var v = Vector512.Create(seeds[0], seeds[1], seeds[2], seeds[3], seeds[4], seeds[5], seeds[6], seeds[7]);
        var s = Vector512<ulong>.Zero;

        for (var it = 0; it < iterations; it++)
        {
            if ((it & 0x3FF) == 0) cancellation.ThrowIfCancellationRequested();
            v = FpStep512(v, useFma); s += Vector512.AsUInt64(v);
        }

        Span<double> acc = stackalloc double[Lanes];
        Span<ulong> sum = stackalloc ulong[Lanes];
        v.CopyTo(acc); s.CopyTo(sum);
        return CombineFp(sum, acc);
    }

    private static double FpStepScalar(double x, bool useFma)
    {
        x = useFma ? Math.FusedMultiplyAdd(x, FpMulA, FpAddC) : x * FpMulA + FpAddC;
        x -= FpModulus * Math.Floor(x * FpInvModulus);
        x = useFma ? Math.FusedMultiplyAdd(x, FpMulB, FpAddD) : x * FpMulB + FpAddD;
        x -= FpModulus * Math.Floor(x * FpInvModulus);
        return x;
    }

    private static Vector128<double> FpStep128(Vector128<double> v, bool useFma)
    {
        v = useFma && Fma.IsSupported ? Fma.MultiplyAdd(v, A128, C128) : v * A128 + C128;
        v -= Mod128 * Vector128.Floor(v * Inv128);
        v = useFma && Fma.IsSupported ? Fma.MultiplyAdd(v, B128, D128) : v * B128 + D128;
        v -= Mod128 * Vector128.Floor(v * Inv128);
        return v;
    }

    private static Vector256<double> FpStep256(Vector256<double> v, bool useFma)
    {
        v = useFma && Fma.IsSupported ? Fma.MultiplyAdd(v, A256, C256) : v * A256 + C256;
        v -= Mod256 * Vector256.Floor(v * Inv256);
        v = useFma && Fma.IsSupported ? Fma.MultiplyAdd(v, B256, D256) : v * B256 + D256;
        v -= Mod256 * Vector256.Floor(v * Inv256);
        return v;
    }

    private static Vector512<double> FpStep512(Vector512<double> v, bool useFma)
    {
        v = useFma && Avx512F.IsSupported ? Avx512F.FusedMultiplyAdd(v, A512, C512) : v * A512 + C512;
        v -= Mod512 * Vector512.Floor(v * Inv512);
        v = useFma && Avx512F.IsSupported ? Avx512F.FusedMultiplyAdd(v, B512, D512) : v * B512 + D512;
        v -= Mod512 * Vector512.Floor(v * Inv512);
        return v;
    }

    private static void InitFpSeeds(Span<double> seeds)
    {
        for (var lane = 0; lane < Lanes; lane++) seeds[lane] = FpSeedBase + lane * FpSeedStep;
    }

    /// <summary>Fold the running per-lane bit-sum and the final per-lane state. BOTH are folded on purpose: the
    /// recurrence's state transition is a short cycle (it returns to the seed after 65536 steps), so folding the
    /// final state alone would let a transient error that lands the lane back on the cycle pass unnoticed. The
    /// running sum makes the checksum sensitive to the whole trajectory, and is itself exact unsigned integer
    /// arithmetic, so it cannot false-positive.</summary>
    private static ulong CombineFp(ReadOnlySpan<ulong> sums, ReadOnlySpan<double> endState)
    {
        var h = 0UL;
        for (var lane = 0; lane < Lanes; lane++)
        {
            h ^= sums[lane];
            h = unchecked(h * 0x100000001B3UL);
            h ^= BitConverter.DoubleToUInt64Bits(endState[lane]);
            h = unchecked(h * 0x100000001B3UL);
            h ^= h >> 32;
        }
        return Mix64(h);
    }

    // ============================ the memory mix ============================

    private static ulong[] BuildMemoryPool()
    {
        var pool = new ulong[MemoryWorkingSetElements];
        var s = MemorySeed;
        for (var i = 0; i < pool.Length; i++)
        {
            s ^= s << 13;
            s ^= s >> 7;
            s ^= s << 17;
            pool[i] = s;
        }
        return pool;
    }

    private static ulong ComputeMemory(int iterations, CancellationToken cancellation)
    {
        var pool = MemoryPool;
        var mask = pool.Length - 1;
        var acc = MemoryAccSeed;
        var index = 0;
        for (var it = 0; it < iterations; it++)
        {
            if ((it & 0x3FF) == 0) cancellation.ThrowIfCancellationRequested();
            acc ^= pool[index & mask];
            acc = unchecked(acc * MemoryAccMul + 1);
            acc ^= acc >> 29;
            index += MemoryStride;
        }
        return Mix64(acc);
    }

    // ============================ the shared fold ============================

    /// <summary>The SplitMix64 finalizer, shared by every mix's fold so a wrong lane value or a wrong memory word
    /// avalanches into a different 64-bit word.</summary>
    private static ulong Mix64(ulong h)
    {
        h ^= h >> 29;
        h = unchecked(h * 0xBF58476D1CE4E5B9UL);
        h ^= h >> 32;
        return h;
    }

    // ---- the pinned goldens (see the type remarks: literals, never recomputed on the core under test) ----
    private const ulong IntegerGolden = 0x6117C64471DCD42EUL;
    private const ulong FpGolden = 0xD2C0E3F4104CC4A7UL;
    private const ulong MemoryGolden = 0x594564883F846141UL;
}
