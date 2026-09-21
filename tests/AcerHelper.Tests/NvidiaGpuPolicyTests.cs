using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// THE GPU CLOCK-OFFSET PORT'S POLICY, driven through injected I/O — the Linux port's correctness rests on this
/// file, because the Linux half (<c>NvidiaGpu.Linux.cs</c>) is excluded from this project's TFM and cannot be
/// compiled here at all. What is proved here is everything above the P/Invoke: the availability gate's decision
/// table, the safety caps and the degenerate-range fallback, the clamping, the write/confirm ORDER, and the NVML
/// return-code vocabulary the user ends up reading.
///
/// THE SEQUENCE IS ASSERTED AS A TRACE rather than per-effect, for the reason CurveOptimizerPolicyTests gives: a
/// Set that wrote memory before core, or that confirmed before writing, would still "work" on a healthy driver and
/// would be wrong the day a write is refused. The fake records every call in order and the order is compared
/// literally — including the MHz values, which is what pins the UNITS.
///
/// THE UNITS ARE THE HIGHEST-VALUE ASSERTION HERE and the one this port can actually get wrong. NVML's offsets are
/// MHz on both sides, while the Windows twin's NvAPI delta is kHz and divides by 1000. Set(150, -300) must reach
/// the transport as 150 and -300 and nothing else: 0 or -0 would be a kHz conversion that does not belong, and
/// doubling would be the Afterburner "effective" convention this app deliberately does not use.
///
/// WHAT CANNOT BE PROVED HERE, said plainly because a green suite must not be read as more: nothing below touches
/// the library, the driver or the GPU. That the eleven NVML symbols exist, what the device reports as its ranges,
/// and that the setters answer NVML_ERROR_NO_PERMISSION to an unprivileged process were all MEASURED against this
/// machine's libnvidia-ml 615.71.09 and are recorded in docs/nvidia-gpu-oc-linux.md — not asserted here, because a
/// test run on a machine with no NVIDIA driver would then fail for the wrong reason.
/// </summary>
public class NvidiaGpuPolicyTests
{
    /// <summary>The two ranges THIS machine's driver reported, as measured — core ±1000 MHz and memory
    /// -2000..+6000 MHz (docs/nvidia-gpu-oc-linux.md). Spelled as literals here so that a change inside the port
    /// cannot satisfy a test by moving the number the test is compared against.</summary>
    private const int MeasuredCoreMin = -1000, MeasuredCoreMax = 1000;
    private const int MeasuredMemMin = -2000, MeasuredMemMax = 6000;

    private const int CoreCap = 300, MemCap = 1500;

    // ================= the recording fake =================

    /// <summary>The three call shapes NVML offers, recorded in order. Everything the policy does to the hardware
    /// goes through here, so the trace is the whole story: a call that is not in it did not happen.</summary>
    private sealed class FakeNvml
    {
        private readonly List<string> _trace = [];

        /// <summary>What the range getters answer. Defaults to this machine's measured values.</summary>
        internal NvidiaGpuPolicy.RangeResult CoreRange { get; set; } = new(0, MeasuredCoreMin, MeasuredCoreMax);
        internal NvidiaGpuPolicy.RangeResult MemRange { get; set; } = new(0, MeasuredMemMin, MeasuredMemMax);

        /// <summary>What the offset getters answer, keyed by domain. Defaults to stock (0), as this machine
        /// actually reads.</summary>
        internal Dictionary<NvidiaGpuPolicy.OffsetDomain, NvidiaGpuPolicy.NvmlResult> Offsets { get; } = new()
        {
            [NvidiaGpuPolicy.OffsetDomain.Core] = new(0, 0),
            [NvidiaGpuPolicy.OffsetDomain.Memory] = new(0, 0),
        };

        /// <summary>What the setters answer, keyed by domain. 0 = accepted; 4 = the NO_PERMISSION this machine
        /// actually returns.</summary>
        internal Dictionary<NvidiaGpuPolicy.OffsetDomain, int> WriteResults { get; } = new()
        {
            [NvidiaGpuPolicy.OffsetDomain.Core] = NvidiaGpuPolicy.NvmlSuccess,
            [NvidiaGpuPolicy.OffsetDomain.Memory] = NvidiaGpuPolicy.NvmlSuccess,
        };

        internal IReadOnlyList<string> Trace => _trace;

        /// <summary>Everything that reached the hardware, in order.</summary>
        internal List<string> Calls() => [.. _trace];

        internal NvidiaGpuPolicy.NvmlResult ReadOffset(NvidiaGpuPolicy.OffsetDomain domain)
        {
            _trace.Add($"read-offset {domain}");
            return Offsets[domain];
        }

        internal NvidiaGpuPolicy.RangeResult ReadRange(NvidiaGpuPolicy.OffsetDomain domain)
        {
            _trace.Add($"read-range {domain}");
            return domain == NvidiaGpuPolicy.OffsetDomain.Core ? CoreRange : MemRange;
        }

        internal int WriteOffset(NvidiaGpuPolicy.OffsetDomain domain, int mhz)
        {
            _trace.Add($"write-offset {domain} {mhz}");
            return WriteResults[domain];
        }

        /// <summary>Build a policy over this fake exactly as the Linux transport does.</summary>
        internal NvidiaGpuPolicy? Create(bool libraryLoaded = true, bool initialized = true,
            int visibleDevices = 1, string? name = "NVIDIA GeForce RTX 5070 Ti Laptop GPU")
            => NvidiaGpuPolicy.TryCreate(libraryLoaded, initialized, visibleDevices, name,
                ReadOffset, ReadRange, WriteOffset);

        /// <summary>The same, for a test that then asserts on a WRITE sequence: the probe's own two range reads
        /// are forgotten, so the trace is only what the action under test did. (Kept separate from
        /// <see cref="Create"/> because the probe's trace is itself the subject of one test.)</summary>
        internal NvidiaGpuPolicy Policy()
        {
            var policy = Create()!;
            _trace.Clear();
            return policy;
        }
    }

    private static FakeNvml Healthy() => new();

    // ================= the availability gate =================

    /// <summary>Every conjunct of the gate, one at a time. Each false term is a different machine: no driver at
    /// all, a library that will not initialise, a dGPU that is hidden or absent, and a driver too old to know one
    /// of the three call shapes.</summary>
    [Theory]
    [InlineData(false, true, 1, true, true, true)]    // the library is not installed (AMD/Intel-only laptop)
    [InlineData(true, false, 1, true, true, true)]    // nvmlInit_v2 refused
    [InlineData(true, true, 0, true, true, true)]     // NO DEVICE VISIBLE — the hidden-dGPU case
    [InlineData(true, true, 1, false, true, true)]    // no offset getter
    [InlineData(true, true, 1, true, false, true)]    // no range getter — no honest slider bounds
    [InlineData(true, true, 1, true, true, false)]    // no offset setter — a read-only axis is not an axis
    public void Gate_Gates_On_Every_Term(bool libraryLoaded, bool initialized, int visibleDevices,
        bool readOffset, bool readRange, bool writeOffset)
    {
        Assert.False(NvidiaGpuPolicy.Available(libraryLoaded, initialized, visibleDevices,
            readOffset, readRange, writeOffset));
    }

    [Fact]
    public void Gate_Opens_When_Everything_Is_There()
    {
        Assert.True(NvidiaGpuPolicy.Available(true, true, 1, true, true, true));
    }

    /// <summary>A HIDDEN DEVICE MUST NOT BE MISTAKEN FOR A MACHINE WITHOUT A GPU, and the port's answer to both is
    /// the same null — which is only correct because nothing is cached: the same call that fails while the device
    /// is hidden succeeds once it is visible, with no state carried across.
    /// </summary>
    [Fact]
    public void Hidden_Device_Is_No_Port_But_Not_A_Verdict()
    {
        var fake = Healthy();
        Assert.Null(fake.Create(visibleDevices: 0));   // the broker has not handed the dGPU out
        Assert.NotNull(fake.Create(visibleDevices: 1)); // the very next probe, once it has
    }

    [Fact]
    public void TryCreate_Returns_Null_When_A_Range_Read_Fails()
    {
        var fake = Healthy();
        fake.CoreRange = NvidiaGpuPolicy.RangeResult.Failure(NvidiaGpuPolicy.NvmlNotSupported);
        Assert.Null(fake.Create());
    }

    /// <summary>THE PROBE MUST NOT WRITE. A probe that set an offset on the way past would apply an overclock
    /// nobody asked for, on the UI thread, every time the machine is composed — so the trace of a successful
    /// TryCreate contains the two range reads and nothing else.</summary>
    [Fact]
    public void TryCreate_Only_Reads_Ranges()
    {
        var fake = Healthy();
        Assert.NotNull(fake.Create());
        Assert.Equal(["read-range Core", "read-range Memory"], fake.Calls());
    }

    [Fact]
    public void TryCreate_Names_The_Part_And_Falls_Back_When_The_Driver_Will_Not()
    {
        Assert.Equal("NVIDIA GeForce RTX 5070 Ti Laptop GPU", Healthy().Create()!.Name);
        Assert.Equal(NvidiaGpuPolicy.DefaultName, Healthy().Create(name: null)!.Name);
        Assert.Equal(NvidiaGpuPolicy.DefaultName, Healthy().Create(name: "   ")!.Name);
    }

    // ================= the caps =================

    /// <summary>The caps are applied even where the driver offers more, and on this machine the driver offers a
    /// LOT more memory headroom: -2000..+6000 MHz reported against a 1500 MHz cap. The exposed range is the
    /// intersection, so the top of that driver range is unreachable from the slider.</summary>
    [Fact]
    public void Ranges_Are_Intersected_With_The_Caps()
    {
        var ranges = NvidiaGpuPolicy.RangesFor(MeasuredCoreMin, MeasuredCoreMax, MeasuredMemMin, MeasuredMemMax);
        Assert.Equal((-CoreCap, CoreCap), ranges.Core);
        Assert.Equal((-MemCap, MemCap), ranges.Mem);
        Assert.True(ranges.Mem.Max < MeasuredMemMax, "the driver's +6000 MHz must not reach the slider");
    }

    /// <summary>A driver range narrower than the cap is left alone — the cap bounds the slider, it does not widen
    /// it.</summary>
    [Fact]
    public void A_Narrower_Driver_Range_Survives_The_Cap()
    {
        var ranges = NvidiaGpuPolicy.RangesFor(-80, 120, -200, 300);
        Assert.Equal((-80, 120), ranges.Core);
        Assert.Equal((-200, 300), ranges.Mem);
    }

    /// <summary>A degenerate 0..0 read is what a powered-off / D3-cold dGPU answers, and reading it literally would
    /// leave a dead 0..0 slider. The fallback is the full ±cap envelope, which is what the Windows port does too —
    /// one rule, both OSes.</summary>
    [Fact]
    public void A_Degenerate_Zero_Range_Falls_Back_To_The_Envelope()
    {
        Assert.Equal((-CoreCap, CoreCap), NvidiaGpuPolicy.Cap(0, 0, CoreCap));
        Assert.Equal((-MemCap, MemCap), NvidiaGpuPolicy.Cap(0, 0, MemCap));
    }

    /// <summary>A driver range lying entirely above the cap has nothing safe to offer, and the intersection is
    /// genuinely empty. It collapses to stock rather than producing a CROSSED pair — which would reach
    /// <c>Math.Clamp</c>, throw, and take the UI thread down on a slider drag.</summary>
    [Fact]
    public void An_Empty_Intersection_Collapses_To_Stock_Instead_Of_A_Crossed_Range()
    {
        var capped = NvidiaGpuPolicy.Cap(400, 1000, CoreCap);
        Assert.Equal((0, 0), capped);

        // And the consequence that matters: the clamp the write path performs cannot throw on it.
        Assert.Equal(0, NvidiaGpuPolicy.ClampOffset(250, capped));
        Assert.Equal(0, NvidiaGpuPolicy.ClampOffset(-250, capped));
    }

    [Fact]
    public void Every_Cap_Result_Is_A_Usable_Range()
    {
        // Math.Clamp throws when Min > Max, so this is the invariant the write path depends on. Swept rather than
        // spot-checked because the crossing case is a narrow band of driver answers.
        for (var min = -3000; min <= 3000; min += 137)
        {
            for (var max = min; max <= 3000; max += 89)
            {
                var (lo, hi) = NvidiaGpuPolicy.Cap(min, max, CoreCap);
                Assert.True(lo <= hi, $"Cap({min}, {max}) produced a crossed range ({lo}, {hi})");
                Assert.InRange(NvidiaGpuPolicy.ClampOffset(0, (lo, hi)), lo, hi);
            }
        }
    }

    // ================= clamping, and the units =================

    /// <summary>The core, on a healthy driver: both offsets reach the transport as the literal MHz the caller
    /// asked for — no kHz division, no doubling — and are confirmed by a read-back.</summary>
    [Fact]
    public void Set_Writes_Both_Offsets_In_Mhz_Then_Confirms()
    {
        var fake = Healthy();
        var policy = fake.Policy();

        // The fake's getters have to agree, or the confirmation fails — which is the next test's subject.
        fake.Offsets[NvidiaGpuPolicy.OffsetDomain.Core] = new(0, 150);
        fake.Offsets[NvidiaGpuPolicy.OffsetDomain.Memory] = new(0, -300);

        Assert.True(policy.Set(150, -300));
        Assert.Null(policy.LastError);

        Assert.Equal([
            "write-offset Core 150",
            "write-offset Memory -300",
            "read-offset Core",
            "read-offset Memory",
        ], fake.Calls());
    }

    /// <summary>Stock is a real request, not a no-op the port can skip: it is how a mode switch CLEARS whatever
    /// the previous mode applied, since the driver has no notion of the app's modes.</summary>
    [Fact]
    public void Set_To_Stock_Still_Writes()
    {
        var fake = Healthy();
        Assert.True(fake.Policy().Set(0, 0));
        Assert.Equal(["write-offset Core 0", "write-offset Memory 0", "read-offset Core", "read-offset Memory"],
            fake.Calls());
    }

    [Theory]
    [InlineData(1000, 5000, CoreCap, MemCap)]     // far above
    [InlineData(-9999, -9999, -CoreCap, -MemCap)] // far below
    [InlineData(CoreCap + 1, MemCap + 1, CoreCap, MemCap)]
    public void Set_Clamps_To_The_Capped_Range(int askedCore, int askedMem, int expectCore, int expectMem)
    {
        var fake = Healthy();
        var policy = fake.Policy();
        fake.Offsets[NvidiaGpuPolicy.OffsetDomain.Core] = new(0, expectCore);
        fake.Offsets[NvidiaGpuPolicy.OffsetDomain.Memory] = new(0, expectMem);

        Assert.True(policy.Set(askedCore, askedMem));
        Assert.Equal([$"write-offset Core {expectCore}", $"write-offset Memory {expectMem}",
                      "read-offset Core", "read-offset Memory"], fake.Calls());
    }

    /// <summary>THE UNITS ARE PINNED HERE, at the one scale where a mistake is loud: 150 MHz must not arrive as
    /// 0.15 (a kHz conversion belonging to the Windows path) nor as 300 (the Afterburner effective convention).
    /// </summary>
    [Fact]
    public void Set_Never_Rescales_The_Offset()
    {
        var fake = Healthy();
        fake.Offsets[NvidiaGpuPolicy.OffsetDomain.Core] = new(0, 150);
        fake.Offsets[NvidiaGpuPolicy.OffsetDomain.Memory] = new(0, -300);
        Assert.True(fake.Policy().Set(150, -300));

        var written = fake.Calls().Where(c => c.StartsWith("write-offset", StringComparison.Ordinal)).ToList();
        Assert.Contains("write-offset Core 150", written);
        Assert.Contains("write-offset Memory -300", written);
        Assert.DoesNotContain("write-offset Core 0", written);
        Assert.DoesNotContain("write-offset Memory -300000", written);
    }

    // ================= refusals =================

    /// <summary>THE FINDING THIS PORT WAS BUILT AROUND: NVML refuses the write to an unprivileged process with
    /// NVML_ERROR_NO_PERMISSION (4) — measured on this machine as uid 1000, offsets left at stock. The failure must
    /// surface as that code and as words the user can act on, and the memory write must NOT be attempted after the
    /// core write was refused.</summary>
    [Fact]
    public void A_Refused_Core_Write_Fails_Fast_And_Names_The_Permission()
    {
        var fake = Healthy();
        fake.WriteResults[NvidiaGpuPolicy.OffsetDomain.Core] = NvidiaGpuPolicy.NvmlNoPermission;
        var policy = fake.Policy();

        Assert.False(policy.Set(100, 200));
        Assert.NotNull(policy.LastError);
        Assert.Contains("NVML_ERROR_NO_PERMISSION", policy.LastError!);
        Assert.Contains("core", policy.LastError!);

        // Nothing further was attempted, and nothing was read back.
        Assert.Equal(["write-offset Core 100"], fake.Calls());
    }

    [Fact]
    public void A_Refused_Memory_Write_Reports_It_After_The_Core_Was_Accepted()
    {
        var fake = Healthy();
        fake.WriteResults[NvidiaGpuPolicy.OffsetDomain.Memory] = NvidiaGpuPolicy.NvmlInvalidArgument;
        var policy = fake.Policy();

        Assert.False(policy.Set(100, 200));
        Assert.Contains("memory", policy.LastError);
        Assert.Contains("NVML_ERROR_INVALID_ARGUMENT", policy.LastError!);
        Assert.Equal(["write-offset Core 100", "write-offset Memory 200"], fake.Calls());
    }

    /// <summary>A driver that accepts the write and then reports a DIFFERENT offset is the case a write-only port
    /// would silently swallow: the user would be shown a number the hardware does not hold.</summary>
    [Fact]
    public void A_Read_Back_That_Disagrees_Is_A_Failure()
    {
        var fake = Healthy();
        fake.Offsets[NvidiaGpuPolicy.OffsetDomain.Core] = new(0, 42);   // the driver clamped it itself
        var policy = fake.Policy();

        Assert.False(policy.Set(150, 0));
        Assert.Contains("42", policy.LastError);
        Assert.Contains("150", policy.LastError);

        // The memory half is still confirmed: its write had already happened.
        Assert.Contains("read-offset Memory", fake.Calls());
    }

    /// <summary>Both getters resolved at probe time, so a getter that stops answering means the transport broke —
    /// and reporting success there is the one thing this axis must not do.</summary>
    [Fact]
    public void A_Read_Back_That_Fails_Is_A_Failure_Not_A_Shrug()
    {
        var fake = Healthy();
        fake.Offsets[NvidiaGpuPolicy.OffsetDomain.Memory] = new(NvidiaGpuPolicy.NvmlNotFound, 0);
        var policy = fake.Policy();

        Assert.False(policy.Set(0, 100));
        Assert.Contains("NVML_ERROR_NOT_FOUND", policy.LastError!);
        Assert.Contains("read-back", policy.LastError!);
    }

    [Fact]
    public void Both_Failures_Fit_In_LastError_Which_The_Ui_Reads()
    {
        var fake = Healthy();
        fake.Offsets[NvidiaGpuPolicy.OffsetDomain.Core] = new(0, 7);
        fake.Offsets[NvidiaGpuPolicy.OffsetDomain.Memory] = new(0, 9);
        var policy = fake.Policy();

        Assert.False(policy.Set(1, 2));
        Assert.NotNull(policy.LastError);

        // A second, successful call clears it — the UI shows LastError only for the last attempt.
        fake.Offsets[NvidiaGpuPolicy.OffsetDomain.Core] = new(0, 0);
        fake.Offsets[NvidiaGpuPolicy.OffsetDomain.Memory] = new(0, 0);
        Assert.True(policy.Set(0, 0));
        Assert.Null(policy.LastError);
    }

    // ================= the error vocabulary =================

    /// <summary>Every code the axis can meet gets words, and the permission one names the code AND the remedy,
    /// because it is the single case that decides whether this feature can work from an unprivileged app at all.</summary>
    [Theory]
    [InlineData(NvidiaGpuPolicy.NvmlNoPermission, "NVML_ERROR_NO_PERMISSION")]
    [InlineData(NvidiaGpuPolicy.NvmlNotSupported, "NVML_ERROR_NOT_SUPPORTED")]
    [InlineData(NvidiaGpuPolicy.NvmlNotFound, "NVML_ERROR_NOT_FOUND")]
    [InlineData(NvidiaGpuPolicy.NvmlDriverNotLoaded, "NVML_ERROR_DRIVER_NOT_LOADED")]
    [InlineData(NvidiaGpuPolicy.NvmlUninitialized, "NVML_ERROR_UNINITIALIZED")]
    [InlineData(NvidiaGpuPolicy.NvmlInvalidArgument, "NVML_ERROR_INVALID_ARGUMENT")]
    [InlineData(NvidiaGpuPolicy.NvmlArgumentVersionMismatch, "NVML_ERROR_ARGUMENT_VERSION_MISMATCH")]
    public void Describe_Names_The_Nvml_Code(int code, string expected)
    {
        var text = NvidiaGpuPolicy.Describe(code, "core");
        Assert.Contains(expected, text);
        Assert.Contains(code.ToString(), text);
        Assert.Contains("core", text);
    }

    /// <summary>An unnamed code still names the number rather than reading as success.</summary>
    [Fact]
    public void An_Unknown_Code_Still_Reports_The_Number()
    {
        Assert.Contains("999", NvidiaGpuPolicy.Describe(999, "memory"));
    }

    // ================= the range rule, in one place =================

    [Fact]
    public void Held_Is_Exact_Equality()
    {
        Assert.True(NvidiaGpuPolicy.Held(150, 150));
        Assert.False(NvidiaGpuPolicy.Held(150, 149));
        Assert.True(NvidiaGpuPolicy.Held(0, 0));    // stock round-trips as stock
    }
}
