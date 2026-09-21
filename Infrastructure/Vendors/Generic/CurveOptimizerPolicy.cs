using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;
using System.Text;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// THE CURVE OPTIMIZER'S POLICY, IN AN UN-SUFFIXED FILE, for the reason AcerFanPort.cs states at length: the test
// project targets net10.0-windows while AcerHelper.csproj's <Compile Remove> keeps **/*.Linux.cs out of it, so
// whatever is left in RyzenCurveOptimizer.Linux.cs cannot be reached by the suite at all. What a Linux port needs
// proved is therefore all here — the encodings, the mailbox transaction's ORDER, the response-code mapping, the
// rail topology, the availability gate's decision, and the write/read-back policy — and what stays in the two OS
// files is I/O: a PawnIo register accessor on Windows, an smn node and a FileStream on Linux.
//
// It is the SHARED policy rather than the Linux one's, and that is a decision rather than a convenience: there
// are two mailboxes, three rails and four opcodes on this package, and a second, OS-local copy of the transaction
// is exactly the thing that would drift from the first while both looked correct. The OS files own one thing each
// (a transport), and both hand it to this type as two delegates.

/// <summary>
/// AMD's Curve Optimizer on Zen 5 mobile — an AVFS voltage-curve offset applied through the SMU. A negative offset
/// shifts the whole voltage/frequency curve down: less voltage at every frequency point, so a fixed workload draws
/// less power and runs cooler, and inside a power-limited envelope the part holds higher clocks. The CPU-side
/// analogue of the GPU clock offset in <c>NvidiaGpu</c>, and just as volatile.
///
/// THREE rails over TWO mailboxes: the two core clusters are offset on MP1 (0x4B per slot, 0x4C all-core), the
/// integrated Radeon on RSMU (0x1F to set, 0x20 to read back). One port, because they are one kind of thing — a
/// curve offset on this package, SMU-resident, re-applied per performance mode — not because they share a path.
///
/// What each rail can and cannot promise:
///  - CORES: no read-back exists, and the mailbox is known to acknowledge while the platform's power mode suppresses
///    the effect. A successful write means the SMU accepted the message and nothing more. That the curve really moves
///    was established here by measurement — an all-core -30 dropped every per-core voltage word in the SMU's PM
///    telemetry table by ~76 mV under constant load — not by trusting the response code.
///  - iGPU: has a getter, so every write is CONFIRMED by reading the margin back and a mismatch is reported as a
///    failure. Its scale was measured separately and is NOT the cores': 5 mV per count against their 2.5, so the two
///    rails cannot share a millivolt figure.
///
/// Granularity is per CLUSTER, not per core, and that is hardware rather than simplification: 0x4B does address a
/// single slot, but a cluster shares a rail whose setpoint is the MAXIMUM of its cores' requests, so the delivered
/// undervolt is the mildest offset in the cluster and per-core sliders would leave 8 of 10 cores inert.
///
/// The offset is VOLATILE — it lives in SMU state and a power cycle restores stock — so the app is the source of
/// truth and re-applies per performance mode at startup, on resume, and on each mode switch (see LaptopService),
/// exactly like the GPU clock offsets. That volatility is also the recovery path: nothing is written to firmware.
///
/// THE I/O IS TWO DELEGATES, and both are the caller's: <c>read(address)</c> answers the raw u32 a register
/// access returned (null when the access itself failed — a permission, a missing node, a driver handle that could
/// not be opened) and <c>write(address, value)</c> performs one register write. Nothing above them knows whether
/// the bytes travel over a PawnIo ioctl or an smn node, which is what makes the sequence below testable in full.
///
/// THE CALLER ALSO OWNS THE INTERLOCK (see <see cref="InterlockTake"/>): the transaction is serialised here by
/// <see cref="WithLock"/> within one process, while ACROSS processes the register window is shared with other
/// tuning tools and needs the mutual exclusion Windows provides through the PCI access mutex. Linux passes none,
/// because there the register window is reached through the driver's own smn node and the kernel serialises it.
///
/// See docs/curve-optimizer-strix-point.md and docs/curve-optimizer-linux.md.
/// </summary>
internal sealed class CurveOptimizerPolicy : ICurveOptimizer
{
    /// <summary>One SMU mailbox: a message / response / argument-base register triple. There are two on this part and
    /// they are NOT interchangeable — the CPU curve is on MP1, the graphics curve on RSMU — so the triple travels with
    /// every transaction instead of being ambient state.</summary>
    internal readonly record struct Mailbox(uint Msg, uint Rsp, uint Arg, string Name);

    // ---- SMU mailboxes (AMD family 0x1A / Strix Point) ----
    // Both triples agree across RyzenAdj (lib/nb_smu_ops.c), ZenStates-Core and G-Helper. Arg n lives at Arg + 4n.
    internal static readonly Mailbox Mp1  = new(0x03B10928, 0x03B10978, 0x03B10998, "MP1");
    internal static readonly Mailbox Rsmu = new(0x03B10A20, 0x03B10A80, 0x03B10A88, "RSMU");

    internal const int ArgCount = 6;           // all six are rewritten every transaction, so no stale arg can leak in

    internal const uint MsgSetAllCoreCurveOptimizer = 0x4C;
    internal const uint MsgSetPerCoreCurveOptimizer = 0x4B;

    // ---- integrated-GPU curve (RSMU) ----
    // The graphics rail has its own Curve Optimizer, and it is NOT the opcode RyzenAdj's set_cogfx uses: that one
    // (PSMU 0xB7, inherited from the Rembrandt..Hawk Point lineage) does not list Strix Point at all, and probing it
    // here answers 0xFD "prerequisites not met" every single time, idle and under GFX load alike — a wrong-opcode
    // false negative that reads exactly like an absent feature.
    //
    // The right numbers come from ZenStates-Core, whose APUSettings1_Strix inherits APUSettings1_Phoenix. What makes
    // that table trustworthy for THIS die rather than one more guess: the same file carries MP1 SetDldoPsmMargin 0x4B
    // and SetAllDldoPsmMargin 0x4C — the CPU opcodes already proven to work here.
    //
    // Verified on this machine (Windows/PawnIO): 0 -> -5 -> 0, every transaction REP_MSG_OK, each step confirmed by
    // reading 0x20 back. See docs/curve-optimizer-strix-point.md.
    internal const uint MsgSetGpuPsmMargin = 0x1F;
    internal const uint MsgGetGpuPsmMargin = 0x20;   // read-back — the CPU side has no equivalent

    /// <summary>Sentinel in <see cref="_domainCcd"/> for "this domain is the iGPU, not a CPU cluster".</summary>
    internal const int GpuDomain = -1;
    internal const string GpuDomainKey = "gfx";
    internal const string GpuDomainLabel = "iGPU";

    // SMU response register. Values per AMD's own driver (smu_cmn.c); AMD notes they are defined per-ASIC, so an
    // unrecognised value is reported raw rather than folded into a generic "failed".
    internal const uint RspInProgress = 0x00;   // still executing
    internal const uint RspOk         = 0x01;
    internal const uint RspBusyOther  = 0xFC;   // busy with another command, retry
    internal const uint RspBadPrereq  = 0xFD;   // valid command, prerequisites not met (platform/firmware gate)
    internal const uint RspUnknownCmd = 0xFE;   // this firmware has no such command
    internal const uint RspFailed     = 0xFF;   // the command ran and its status was failure

    /// <summary>Sentinel for "the transaction never reached a response" (a register access or the interlock failed),
    /// distinct from every value the response register can hold.</summary>
    internal const uint NoResponse = uint.MaxValue;

    // Response poll budget. A mailbox message is normally acknowledged in microseconds; anything beyond this is a
    // wedged SMU, and we must return rather than spin — RyzenAdj's equivalent loop has no timeout at all, which is
    // how a stuck mailbox becomes a hung caller. Also the budget of the wait for the mailbox to fall idle.
    internal const int ResponseTimeoutMs = 200;

    // Exposed range. Negative only: a positive offset RAISES voltage, which buys nothing here and is a thermal and
    // stability risk, so it is not offered.
    //
    // The floor is -40 to match what the rest of the ecosystem allows, and because it is a real voltage bound
    // rather than a round number: measured on this part, one count is ~2.5 mV (an all-core -30 moved every per-core
    // voltage word in the SMU's PM table from ~1.03 V to ~0.95 V), so -40 is ~100 mV — a normal undervolt target,
    // while the only published failure datum on a sibling Zen 5 die is a crash at -50.
    //
    // Not lower, for a reason that is not mere caution: an ALL-CORE offset is bounded by the WORST core, so past
    // some point the limit is one weak core, not the average, and the good cores cannot cash in the difference.
    // Undervolt failures on Zen 5 also surface hours later at idle as machine-check errors or silent corruption
    // rather than as an obvious crash under load, so the end of the slider should not be a place a single drag
    // lands by accident. See docs/curve-optimizer-strix-point.md.
    internal const int MinCounts = -40;

    // The full range ZenStates documents for the PSM margin on Zen 4 and newer, deliberately not narrowed. At 5 mV
    // a count this floor is -250 mV, and this sample already fell over well before it — but the stable limit is a
    // property of the individual die, and clipping every machine to one unlucky one would cost the good parts real
    // headroom. The guard is the LABEL, not the bound: the row reads "-50 (≈-250 mV)", and a figure like that warns
    // far better than a slider that silently stops somewhere.
    internal const int GpuMinCounts = -50;

    /// <summary>Measured on this part, not taken from a spec: an all-core -30 moved every per-core voltage word in
    /// the SMU's PM telemetry table from ~1.03 V to ~0.95 V under a constant load, and back on stock — ~76 mV over
    /// 30 counts. (AMD publishes no mV-per-count figure for Zen 5; the community's Zen 3 number was 3-5 mV.) Only a
    /// display aid, since the real delta moves with frequency and temperature. See docs/curve-optimizer-strix-point.md.</summary>
    internal const double MvPerCount = 2.5;

    // Measured separately on the same machine, and NOT the cores' figure: the graphics rail moves 5 mV per count,
    // exactly double (5 counts = 25 mV, 10 = 50, 20 = 100 — strictly linear, read off the iGPU core voltage under
    // load). Reusing the CPU figure here would have understated every offset by half.
    internal const double GpuMvPerCount = 5.0;

    // Per-core argument layout: [31:28] = CCD, [23:20] = core within CCD, [15:0] = margin. Confirmed on this part by
    // applying an offset to one slot at a time and watching exactly one per-core voltage word in the SMU's PM
    // telemetry table drop by ~78 mV, then recover.
    //
    // Which slots are POPULATED had to be measured, because the SMU gives no way to ask: an unpopulated slot answers
    // REP_MSG_OK and changes nothing, exactly like a populated one, so cores cannot be enumerated by response code.
    // Measured on this die (4x Zen5 + 6x Zen5c): Zen5 on CCD0 slots 0, 2, 4, 6 — stride two, not consecutive — and
    // Zen5c on CCD1 slots 2..7, i.e. the LAST six of eight. The rule below reproduces that and extrapolates to the
    // other Strix Point configuration (a Ryzen AI 9 HX 370 is 4+8, so its Zen5c would fill slots 0..7). A wrong guess
    // on some future SKU is benign rather than dangerous — a core would simply get no slider, or a slider would drive
    // an empty slot and do nothing. See docs/curve-optimizer-strix-point.md.
    internal const int SlotsPerCcd = 8;
    internal const int Zen5Ccd = 0;
    internal const int Zen5cCcd = 1;
    internal const int Zen5Cores = 4;      // fixed across Strix Point
    internal const int Zen5SlotStride = 2;

    internal const string UnsupportedError = "the SMU does not implement this command (0xFE)";

    // ---- the smn node's byte layout, pure so the suite can hold it ----
    // Measured against the ryzen_smu driver's own smn_store/smn_show (drv.c) on this machine: a write of EXACTLY
    // four bytes is an SMN READ — the address travels as the payload and the value comes back as the node's next
    // read, four little-endian bytes — and a write of EXACTLY eight bytes is an SMN WRITE, address then value, after
    // which the node's next read answers 0x01 (the SMU call succeeded) or 0xF6 (the PCI access failed). ANY OTHER
    // LENGTH DOES NOTHING AT ALL and reports the count as if it had worked, which is why the two lengths are named
    // here rather than assembled at the I/O site: a 5-byte or 12-byte write is a silent no-op on a live mailbox.

    internal const uint SmnOk = 0x01;
    internal const uint SmnPciFailed = 0xF6;      // SMU_Return_PCIFailed

    /// <summary>The 4-byte payload that asks the <c>smn</c> node for a read: the address alone, little-endian.</summary>
    internal static byte[] SmnReadRequest(uint address)
    {
        var payload = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, address);
        return payload;
    }

    /// <summary>The 8-byte payload that performs a write: the address, then the value, each little-endian. The two
    /// words are positional and identical in width, so swapping them writes a value to an unrelated address rather
    /// than failing — hence one function rather than two array literals at a call site.</summary>
    internal static byte[] SmnWriteRequest(uint address, uint value)
    {
        var payload = new byte[sizeof(uint) * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, address);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(sizeof(uint)), value);
        return payload;
    }

    /// <summary>The u32 the node answered with, from its four little-endian bytes. The count is asserted by the
    /// caller: a short read is a node that answered nothing, which is not a value.</summary>
    internal static uint SmnValue(ReadOnlySpan<byte> reply) => BinaryPrimitives.ReadUInt32LittleEndian(reply);

    /// <summary>Whether the node's reply to an 8-byte write says the SMU call itself got through. 0xF6 is a PCI
    /// access failure and 0x01 the only success — an unrecognised code is NOT accepted, because the whole point of
    /// using this node instead of the driver's command nodes is that its verdict is the SMU's own.</summary>
    internal static bool SmnAcknowledged(uint reply) => reply == SmnOk;

    // ---- availability gate, pure ----
    // The gate is two decisions taken in two different places on purpose (see RyzenCurveOptimizer.TryCreate in each
    // OS file): this CPU is one whose mailbox layout is known, and the transport is present and writable. Both are
    // pure functions here so the decision table can be pinned without a CPU, a driver or a filesystem.

    internal const int StrixPointCodename = 22;   // the ryzen_smu driver's own codename for Strix Point
    internal const int Mp1IfVersion = 4;          // the MP1 interface version this mailbox layout was measured on

    /// <summary>Whether the CPU is one whose MP1 mailbox layout is actually known: AMD family 0x1A (Zen 5), models
    /// 0x20 and 0x24 — the Strix Point pair every tool maps to one address set. Krackan Point (0x60) and Strix Halo
    /// (0x70) are deliberately NOT accepted: they share the family case group in the reverse-engineering projects,
    /// but the published results diverge (the same opcode is reported rejected on one and effective on the other),
    /// so claiming support here would be guessing on someone else's hardware.</summary>
    internal static bool IsKnownStrixPoint(int family, int model) => family == 0x1A && model is 0x20 or 0x24;

    /// <summary>Whether this CPU is one the undervolt supports, independent of whether the transport is installed —
    /// so a driver-setup offer can know if it is even relevant here. CPUID only, and deliberately cheap: composition
    /// runs on the UI thread.</summary>
    internal static bool IsKnownStrixPoint()
    {
        var (family, model) = CpuFamilyAndModel();
        return family is { } f && model is { } m && IsKnownStrixPoint(f, m);
    }

    /// <summary>Whether the transport is ready for this port, from what the driver reports about itself plus the one
    /// permission that matters. <paramref name="codename"/> and <paramref name="mp1IfVersion"/> are the driver's own
    /// cached identity (null when the node is absent), and <paramref name="transportWritable"/> is a write probe of
    /// the single node the port drives — probed, never required, so a machine that cannot use this simply hides the
    /// section instead of offering sliders that would refuse every write.</summary>
    internal static bool TransportReady(int? codename, int? mp1IfVersion, bool transportWritable)
        => codename == StrixPointCodename && mp1IfVersion == Mp1IfVersion && transportWritable;

    /// <summary>The CPU's family and model, from CPUID leaf 1, or nulls on a CPU that does not advertise an AMD
    /// vendor string (or a host where CPUID is not available at all). The arithmetic is AMD's own: family is the
    /// extended family added to the base one, model is the extended model shifted in beside the base one.</summary>
    internal static (int? Family, int? Model) CpuFamilyAndModel()
    {
        if (!X86Base.IsSupported) return (null, null);

        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        if (ebx != 0x68747541 || edx != 0x69746E65 || ecx != 0x444D4163) return (null, null);   // "AuthenticAMD"

        var (eax, _, _, _) = X86Base.CpuId(1, 0);
        return (((eax >> 8) & 0xF) + ((eax >> 20) & 0xFF), ((eax >> 4) & 0xF) | (((eax >> 16) & 0xF) << 4));
    }

    /// <summary>Physical cores, for labelling the groups and for the cluster split: CPUID 0x8000001E EBX[15:8] is
    /// "threads per compute unit minus 1", so the logical count divided by that gives cores. Falls back to the
    /// logical count if the leaf is absent, which only mislabels the group sizes — nothing addresses cores by them
    /// (see <see cref="IsKnownStrixPoint(int, int)"/>'s remark on the benign slot guess).</summary>
    internal static int PhysicalCores()
    {
        try
        {
            var (max, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
            if ((uint)max < 0x8000001E) return Environment.ProcessorCount;
            var (_, ebx, _, _) = X86Base.CpuId(unchecked((int)0x8000001E), 0);
            var threadsPerCore = ((ebx >> 8) & 0xFF) + 1;
            return threadsPerCore > 0 ? Environment.ProcessorCount / threadsPerCore : Environment.ProcessorCount;
        }
        catch { return Environment.ProcessorCount; }
    }

    /// <summary>The CPU's brand string, for the section header (mirroring <c>NvidiaGpu.Name</c>): CPUID leaves
    /// 0x80000002..0x80000004 hold it as 48 bytes of ASCII. A CPU that does not advertise it falls back to a generic
    /// label rather than an empty header.</summary>
    internal static string ReadCpuName()
    {
        try
        {
            var (max, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
            if ((uint)max < 0x80000004) return "AMD Ryzen";

            var sb = new StringBuilder(48);
            for (var leaf = 0x80000002; leaf <= 0x80000004; leaf++)
            {
                var (a, b, c, d) = X86Base.CpuId(unchecked((int)leaf), 0);
                AppendAscii(sb, a); AppendAscii(sb, b); AppendAscii(sb, c); AppendAscii(sb, d);
            }
            var name = sb.ToString().Trim();
            return name.Length == 0 ? "AMD Ryzen" : name;
        }
        catch { return "AMD Ryzen"; }
    }

    private static void AppendAscii(StringBuilder sb, int reg)
    {
        for (var i = 0; i < 4; i++)
        {
            var ch = (char)((reg >> (i * 8)) & 0xFF);
            if (ch != '\0') sb.Append(ch);
        }
    }

    // ---- the encodings ----
    // The single highest-value silent-failure target in the tree is here: two DIFFERENT widths whose words the
    // mailbox accepts without complaint while meaning something else entirely, so they are two functions rather than
    // one with a width parameter, and SmuOffsetEncodingTests pins every literal.

    // Encode an offset as the SMU expects: a 20-bit field, negatives as 0x100000 - |counts|. 0 is sent as a plain 0,
    // NOT as 0x100000 — that would set bit 20, which the per-core form of this message uses as a core selector and
    // is a known source of rejected arguments in other tools. The mask keeps that invariant local rather than
    // depending on the caller's clamp. See docs/curve-optimizer-strix-point.md.
    internal static uint Encode(int counts)
        => (counts >= 0 ? 0u : 0x100000u - (uint)(-counts)) & 0xFFFFFu;

    // Per-core argument: the CCD/slot selector in the high nibbles, the margin in the low 16 bits. The margin is
    // masked to 16 bits here because the selector occupies the bits an all-core value would otherwise run into.
    internal static uint CoreArg(int ccd, int slot, uint encodedMargin)
        => ((uint)ccd << 28) | ((uint)(slot % SlotsPerCcd) << 20) | (encodedMargin & 0xFFFF);

    // The GRAPHICS rail's margin encoding — ZenStates' Utils.MakePsmMarginArg verbatim: 16-bit two's complement.
    // Deliberately a separate function from Encode above rather than a shared one with a width parameter, because the
    // two differ in a way that fails silently: the CPU's 20-bit form of -5 is 0xFFFFB, the GPU's is 0xFFFB, and the
    // mailbox accepts either without complaint while meaning something else entirely.
    //
    // A NON-NEGATIVE INPUT IS COLLAPSED TO 0, exactly as Encode does, and that ceiling is the point of this form
    // rather than a copy of the sibling's. A positive margin RAISES voltage, which buys nothing on this hardware
    // and is a thermal and stability risk (the sentence ICurveOptimizer's range carries), so a request to raise it
    // must not be representable at the mailbox. It is unreachable from today's call paths: Set/SetDomains clamp at
    // the port, and the service and the view-model clamp before that. The guard is what makes "unreachable" a
    // property of the encoding rather than a count of the clamps that happen to exist.
    internal static uint GpuMargin(int counts)
        => (counts >= 0 ? 0u : (uint)(0x100000 + counts)) & 0xFFFF;

    // ---- the response register's meaning, as a pure mapping ----

    /// <summary>What a response code means for the port's bool + <see cref="LastError"/> contract, as a value so the
    /// mapping itself can be pinned. <paramref name="where"/> names the write, so a failure part-way through a
    /// multi-slot apply says which one; <paramref name="ioError"/> is the transport's own reason, used only where the
    /// transaction never reached a response.
    ///
    /// The triple's second member is the LATCH signal: the firmware has no such command, so every later attempt is
    /// pointless and the caller must remember that rather than retry on each mode switch. An unrecognised code is
    /// reported RAW rather than folded into a generic "failed" — AMD defines these per ASIC, so 0x37 may well mean
    /// something and inventing a word for it would hide the one fact we have.</summary>
    internal static (bool Ok, bool Unsupported, string? Error) Explain(uint rsp, string where, string? ioError)
    {
        switch (rsp)
        {
            case RspOk:
                return (true, false, null);
            case RspUnknownCmd:
                return (false, true, UnsupportedError);
            case RspBadPrereq:
                // The documented cause on a sibling Zen 5 mobile part is the active platform power mode: the mailbox
                // refuses while the system sits in an energy-saving mode.
                return (false, false, $"the SMU rejected {where} — prerequisites not met (0xFD), try a performance power mode");
            default:
                // The NoResponse cases (a failed register access, a busy mailbox, no answer) have already explained
                // themselves; only fill in a message when nothing else has, so the specific reason survives.
                if (rsp != NoResponse) return (false, false, $"the SMU returned 0x{rsp:X2} for {where}");
                return (false, false, ioError ?? "SMU register access failed");
        }
    }

    // ---- injected I/O ----

    /// <summary>Take the cross-process interlock for ONE transaction, as the shape the Windows port needs it in.
    /// THREE outcomes, and the third is why this is not a bool: a handle means the lock is held and the transaction
    /// may proceed; <c>null</c> with no error means the interlock could not even be created, and the transaction
    /// proceeds UNLOCKED — better a possible collision with another tuning tool than no Curve Optimizer at all; and
    /// <c>null</c> with an error means someone else holds it, so this write is abandoned with that reason.
    /// The handle is released when the transaction ends, whatever its outcome. Linux passes no such delegate: the
    /// smn node is reached through the driver, which serialises its own SMU traffic.</summary>
    internal delegate IDisposable? InterlockTake(out string? error);

    private readonly Func<uint, uint?> _read;
    private readonly Func<uint, uint, bool> _write;
    private readonly Func<string?>? _ioError;
    private readonly InterlockTake? _interlock;
    private readonly Lock _gate = new();

    /// <summary>Latched once the mailbox has answered 0xFE: this firmware does not implement the command, so every
    /// later attempt is pointless. Latched rather than retried so a mode switch can't spam a dead mailbox.</summary>
    private bool _unsupported;

    public string Name { get; }
    public (int Min, int Max) Range => (MinCounts, 0);

    /// <summary>Measured on this part — see <see cref="MvPerCount"/>. Only a display aid, since the real delta moves
    /// with frequency and temperature.</summary>
    public double MillivoltsPerCount => MvPerCount;

    /// <summary>The two Zen 5 clusters, which are independent voltage domains — measured at stock under load Zen 5 sits
    /// near 1.17 V and Zen 5c near 1.02 V, and offsetting one moves only that one — plus the integrated Radeon as a
    /// third. This is the finest granularity the hardware honours; see the constructor for why per-core is not it.</summary>
    public IReadOnlyList<VoltageDomain> Domains { get; }

    /// <summary>The CCD each entry of <see cref="Domains"/> covers, in the same order.</summary>
    private readonly int[] _domainCcd;

    /// <summary>Which CCD a domain index covers, or <see cref="GpuDomain"/>. The Linux port's write path needs the
    /// same mapping the CPU path uses, and a second copy of "the third domain is the GPU" is what would drift.</summary>
    internal int CcdOf(int domain) => _domainCcd[domain];

    /// <summary>How long one register access may be retried before a mailbox counts as wedged, and how long the wait
    /// for an idle mailbox may take. Settable so a test can drive the timeout paths without waiting them out.</summary>
    internal int TimeoutMs { get; set; } = ResponseTimeoutMs;

    public string? LastError { get; private set; }

    /// <param name="name">The CPU's brand string, for the section header.</param>
    /// <param name="physicalCores">Physical cores, which is what the cluster split is derived from.</param>
    /// <param name="read">One register read: the u32 the register held, or null when the access failed.</param>
    /// <param name="write">One register write; false when the access failed.</param>
    /// <param name="ioError">The transport's last reason, for the cases where the transaction never reached a
    /// response. Null where the transport has nothing more specific to say.</param>
    /// <param name="interlock">The cross-process interlock, or null where the kernel already serialises the
    /// register window (see <see cref="InterlockTake"/>).</param>
    internal CurveOptimizerPolicy(string name, int physicalCores,
        Func<uint, uint?> read, Func<uint, uint, bool> write,
        Func<string?>? ioError = null, InterlockTake? interlock = null)
    {
        Name = name;
        _read = read;
        _write = write;
        _ioError = ioError;
        _interlock = interlock;

        // ONE KNOB PER CLUSTER, not per core — the granularity the hardware actually honours.
        //
        // MP1 0x4B addresses a single slot and provably works (it moves exactly one per-core voltage word in the PM
        // table). But that word is the core's REQUEST, not what it is fed: each cluster shares a rail whose setpoint is
        // the MAXIMUM of its cores' requests, with no per-core LDO drop below it on this die. So within a cluster the
        // delivered undervolt is the SMALLEST offset among its cores — offsetting one core alone changes nothing, and
        // per-core sliders would leave 8 of 10 inert.
        //
        // The clusters, however, are INDEPENDENT domains: measured at stock under load Zen 5 sits near 1.17 V and
        // Zen 5c near 1.02 V, and offsetting one cluster moves only that cluster. Hence exactly two tunable values.
        // Each is written to every slot of its CCD so that no core is left holding a milder request that the rail
        // would then follow. See docs/curve-optimizer-strix-point.md.
        var zen5c = physicalCores - Zen5Cores;
        if (zen5c is < 1 or > SlotsPerCcd) { Domains = []; _domainCcd = []; return; }

        // The iGPU rides along as a third domain. It is the same kind of thing as a cluster — an AVFS curve offset on
        // one rail of this package, volatile, re-applied per performance mode — so it belongs in the same list rather
        // than in a port of its own; only the mailbox and the argument encoding differ.
        //
        // Added unconditionally on a supported Strix Point rather than probed, because probing means opening the
        // transport, and this constructor runs during composition on the UI thread (see TryCreate in the OS files).
        // Every Strix Point ships the integrated Radeon, so the assumption is safe; and if some SKU ever refuses, the
        // write fails loudly through LastError instead of silently doing nothing.
        //
        // Deliberately NOT offered when cluster detection failed above: that path falls back to the single all-core
        // Set(), and a domain list containing only the iGPU would quietly turn the CPU slider into a GPU one.
        // See docs/curve-optimizer-strix-point.md.
        Domains =
        [
            new VoltageDomain($"Zen 5", $"ccd:{Zen5Ccd}", MillivoltsPerCount: MvPerCount),
            new VoltageDomain($"Zen 5c", $"ccd:{Zen5cCcd}", MillivoltsPerCount: MvPerCount),
            // Its own scale, not the cores': 5 mV a count against their 2.5 (see GpuMvPerCount), and its own range,
            // because the floor is a property of the rail.
            new VoltageDomain(GpuDomainLabel, GpuDomainKey, (GpuMinCounts, 0), GpuMvPerCount),
        ];
        _domainCcd = [Zen5Ccd, Zen5cCcd, GpuDomain];
    }

    /// <summary>Run <paramref name="work"/> under the port's transaction lock. Exposed to the OS wrappers because
    /// they own a resource the lock protects (Windows closes a driver handle in Dispose, and a handle closed under a
    /// transaction in flight is a use-after-free), while the lock itself must be the one the transactions use.</summary>
    internal T WithLock<T>(Func<T> work) { lock (_gate) { return work(); } }

    /// <summary>The same, for work with no result.</summary>
    internal void WithLock(Action work) { lock (_gate) { work(); } }

    /// <summary>Apply an all-core Curve Optimizer offset in AVFS counts (negative = undervolt, 0 = stock). Returns
    /// false and sets <see cref="LastError"/> when the SMU refuses. A true result means the mailbox accepted the
    /// message — NOT that the voltage curve measurably moved; see the class remarks.</summary>
    public bool Set(int counts)
    {
        counts = Math.Clamp(counts, MinCounts, 0);

        lock (_gate)
        {
            LastError = null;
            if (_unsupported) { LastError = UnsupportedError; return false; }

            var rsp = Transact(Mp1, MsgSetAllCoreCurveOptimizer, Encode(counts), out _, out var error);
            return Accept(rsp, "all cores", error);
        }
    }

    /// <summary>Turn a mailbox response into the port's bool + <see cref="LastError"/> contract, latching 0xFE.
    /// Caller holds <see cref="_gate"/>.</summary>
    private bool Accept(uint rsp, string where, string? error)
    {
        var (ok, unsupported, message) = Explain(rsp, where, error);
        if (unsupported) _unsupported = true;
        LastError = message;
        return ok;
    }

    /// <summary>Apply one offset per entry in <see cref="Domains"/>. Each is written to EVERY slot of its CCD — all 8,
    /// populated or not — because the rail follows the mildest request in the cluster, so a core left un-offset would
    /// undo the setting; and because an empty slot answers REP_MSG_OK and changes nothing (measured), which also makes
    /// this correct on a SKU with a different populated set. A partial failure leaves the clusters inconsistent, so the
    /// first refusal aborts and is reported rather than pressed on with. See docs/curve-optimizer-strix-point.md.</summary>
    public bool SetDomains(IReadOnlyList<int> counts)
    {
        lock (_gate)
        {
            LastError = null;
            if (Domains.Count == 0) { LastError = "this CPU has no per-cluster control"; return false; }
            if (counts.Count != Domains.Count) { LastError = $"expected {Domains.Count} offsets, got {counts.Count}"; return false; }
            if (_unsupported) { LastError = UnsupportedError; return false; }

            for (var d = 0; d < counts.Count; d++)
            {
                // The graphics rail is one write on the other mailbox, not eight on this one.
                if (_domainCcd[d] == GpuDomain)
                {
                    if (!SetGpu(Math.Clamp(counts[d], GpuMinCounts, 0))) return false;
                    continue;
                }

                var arg = Encode(Math.Clamp(counts[d], MinCounts, 0));
                for (var slot = 0; slot < SlotsPerCcd; slot++)
                {
                    var rsp = Transact(Mp1, MsgSetPerCoreCurveOptimizer, CoreArg(_domainCcd[d], slot, arg), out _, out var error);
                    if (!Accept(rsp, $"{Domains[d].Label} slot {slot}", error)) return false;
                }
            }
            return true;
        }
    }

    /// <summary>Write the integrated GPU's curve offset and CONFIRM it, which is the one thing the CPU side cannot do:
    /// this rail has a getter, so a write is verified by reading the margin straight back rather than trusted because
    /// the mailbox said REP_MSG_OK. A mismatch is reported as a failure — an accepted-but-ignored write is exactly the
    /// failure mode that made the CPU path need statistics to believe.
    ///
    /// Mind the ASYMMETRIC encoding, which is easy to get backwards: the SETTER takes a 16-bit two's-complement margin
    /// (-5 is 0xFFFB), the GETTER answers a FULL 32-BIT SIGNED value (-5 comes back as 0xFFFFFFFB). Sign-extending the
    /// reply from bit 15 — or narrowing it to the setter's width — turns a perfectly good -5 into a different number,
    /// which is why the reply is taken as the whole 32-bit signed word and nothing else. The failure is reported with
    /// both numbers, because "accepted 5, reads back -5" and "accepted -5, reads back -65541" are different bugs.
    /// Caller holds <see cref="_gate"/>. See docs/curve-optimizer-strix-point.md.</summary>
    private bool SetGpu(int counts)
    {
        var rsp = Transact(Rsmu, MsgSetGpuPsmMargin, GpuMargin(counts), out _, out var error);
        if (!Accept(rsp, GpuDomainLabel, error)) return false;

        rsp = Transact(Rsmu, MsgGetGpuPsmMargin, 0, out var reply, out _);
        // A missing or refused read-back is not treated as a failed write: the write was acknowledged, and reporting
        // an error would send the user chasing a problem that may only be in the verification path.
        if (rsp != RspOk) return true;

        var applied = unchecked((int)reply);
        if (applied == counts) return true;

        LastError = $"the SMU accepted {GpuDomainLabel} {counts} but reads back {applied}";
        return false;
    }

    // ---- the mailbox transaction ----
    // The order is the whole of it, and it is not interchangeable:
    //   1. the interlock, for the register window's other users;
    //   2. wait for the mailbox to be idle — a zero response means a message is still in flight, someone else's or a
    //      leftover, and writing ours on top of it would race;
    //   3. clear the response, publish all six arguments, THEN the message — the SMU latches on the message write, so
    //      the arguments must already be in place, and a stale non-zero response would otherwise be mistaken for this
    //      message's answer;
    //   4. poll the response, treating 0xFC as in flight rather than as a verdict;
    //   5. read argument 0 INSIDE the interlock, because that is where a getter's value comes back and reading it
    //      after releasing would race another tuning tool's transaction into our reply.

    /// <summary>Run one message on <paramref name="mb"/> to completion and return the SMU's response byte, or
    /// <see cref="NoResponse"/> when the interlock or a register access failed (with <paramref name="error"/> set).
    /// <paramref name="reply0"/> receives argument 0 as the SMU left it, which is where a getter's value comes back;
    /// it is read only when the response is <see cref="RspOk"/>.</summary>
    internal static uint Transact(Func<uint, uint?> read, Func<uint, uint, bool> write, in Mailbox mb,
                                  uint message, uint arg0, out uint reply0, out string? error,
                                  int timeoutMs = ResponseTimeoutMs, InterlockTake? interlock = null)
    {
        reply0 = 0;
        error = null;
        IDisposable? held = null;
        try
        {
            if (interlock != null)
            {
                held = interlock(out var reason);
                if (held == null && reason != null) { error = reason; return NoResponse; }
            }

            // Wait for the mailbox to be idle. A zero response means a message is still in flight — someone else's
            // or a leftover — and writing ours on top of it would race.
            if (!WaitIdle(read, mb, timeoutMs, out error)) return NoResponse;

            // Clear the response, then publish the arguments, then the message.
            if (!write(mb.Rsp, 0)) return NoResponse;
            for (var i = 0; i < ArgCount; i++)
                if (!write(mb.Arg + (uint)(i * 4), i == 0 ? arg0 : 0)) return NoResponse;
            if (!write(mb.Msg, message)) return NoResponse;

            var rsp = PollResponse(read, mb, timeoutMs, out error);
            if (rsp != RspOk) return rsp;

            // The getter's answer lives in argument 0. A read that fails here is NOT a response of any kind, so it is
            // reported as the failure it is rather than as a stale number: the node's read side is known to hand back
            // the PREVIOUS value after a failed access, and that value is indistinguishable from a fresh one.
            if (read(mb.Arg) is not { } value)
            {
                error = "the SMU's answer could not be read back";
                return NoResponse;
            }
            reply0 = value;
            return rsp;
        }
        finally
        {
            // Whatever happened — including a thrown exception — the interlock is released.
            held?.Dispose();
        }
    }

    /// <summary>The instance's transaction, over the injected delegates. A register access that failed leaves the
    /// error unset on purpose, and it is filled here from the transport's own reason: "Permission denied" or "the
    /// PawnIO driver is not available" is what the user needs, and the policy's generic sentence is only the fallback
    /// for a transport that has nothing more specific to say. Caller holds <see cref="_gate"/>.</summary>
    private uint Transact(in Mailbox mb, uint message, uint arg0, out uint reply0, out string? error)
    {
        var rsp = Transact(_read, _write, in mb, message, arg0, out reply0, out error, TimeoutMs, _interlock);
        if (rsp == NoResponse && error == null) error = _ioError?.Invoke() ?? "SMU register access failed";
        return rsp;
    }

    /// <summary>True once the mailbox has answered 0xFE: the command is not implemented by this firmware, so nothing
    /// downstream should try again. Read by the OS wrappers' own diagnostics and by the suite.</summary>
    internal bool Unsupported => _unsupported;

    private static bool WaitIdle(Func<uint, uint?> read, in Mailbox mb, int timeoutMs, out string? error)
    {
        error = null;
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (read(mb.Rsp) is not { } rsp) return false;   // the access itself failed -> the transport's reason
            if (rsp != RspInProgress) return true;
            if (Environment.TickCount64 >= deadline) { error = $"the {mb.Name} mailbox is busy"; return false; }
            Thread.Sleep(1);
        }
    }

    private static uint PollResponse(Func<uint, uint?> read, in Mailbox mb, int timeoutMs, out string? error)
    {
        error = null;
        var deadline = Environment.TickCount64 + timeoutMs;
        var spins = 0;
        while (true)
        {
            if (read(mb.Rsp) is not { } rsp) return NoResponse;   // the access itself failed -> the transport's reason
            // 0xFC is "busy with other commands, retry" rather than a verdict on our message, so it is polled
            // through like an in-flight response instead of surfaced.
            if (rsp != RspInProgress && rsp != RspBusyOther) return rsp;
            if (Environment.TickCount64 >= deadline)
            {
                error = rsp == RspBusyOther
                    ? $"the {mb.Name} mailbox stayed busy (0xFC)"
                    : $"the {mb.Name} mailbox did not answer";
                return NoResponse;
            }
            if (++spins < 32) Thread.SpinWait(64); else Thread.Sleep(1);
        }
    }
}
