using System.Runtime.Intrinsics.X86;
using System.Text;
using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>
/// AMD Curve Optimizer (all-core) on Zen 5 mobile — an AVFS voltage-curve offset applied through the SMU's MP1
/// mailbox, the same path G-Helper drives on this silicon. A negative offset shifts the whole voltage/frequency
/// curve down: less voltage at every frequency point, so a fixed workload draws less power and runs cooler, and
/// inside a power-limited envelope the part holds higher clocks. The CPU-side analogue of the GPU clock offset in
/// <see cref="NvidiaGpu"/>, and just as volatile.
///
/// Constraints this class is built around:
///  - Opcode 0x4C is inherited from Phoenix in every codebase that models this mailbox; it is NOT confirmed on this
///    die. Response 0xFE means the firmware has no such command, so the feature latches off for the session.
///  - There is no trustworthy read-back on this family, and the mailbox is known to acknowledge while the platform's
///    power mode suppresses the effect. A successful <see cref="Set"/> therefore means the SMU accepted the message
///    and nothing more; confirming the curve actually moved is a measurement (lower package power at a fixed clock),
///    which is the user's to make.
///  - ALL-CORE ONLY. The per-core opcode (0x4B) is unconfirmed here, reported rejected on the sibling Krackan Point,
///    and would need the core-fuse topology out of a second PawnIO module to know which of the 4 Zen5 + 6 Zen5c
///    slots are populated — Windows does not expose the cluster split.
///
/// The offset is VOLATILE — it lives in SMU state and a power cycle restores stock — so the app is the source of
/// truth and re-applies per performance mode at startup, on resume, and on each mode switch (see LaptopService),
/// exactly like the GPU clock offsets. That volatility is also the recovery path: nothing is written to firmware.
///
/// Gated to AMD family 0x1A models 0x20/0x24 (Strix Point) and to a machine where PawnIO is installed;
/// <see cref="TryCreate"/> returns null otherwise, so the port stays null and the UI hides the section
/// (<see cref="PawnIoInstaller"/> is what offers to install the driver). The probe is deliberately cheap — CPUID
/// plus a registry read — because composition runs on the UI thread; the driver handle is opened on first use
/// instead, off that thread, since loading a module is a kernel-side signature verify.
/// </summary>
internal sealed class RyzenCurveOptimizer : ICurveOptimizer, IDisposable
{
    // ---- SMU MP1 mailbox (AMD family 0x1A / Strix Point) ----
    private const uint Mp1Msg = 0x03B10928;
    private const uint Mp1Rsp = 0x03B10978;
    private const uint Mp1Arg = 0x03B10998;   // arg n at Mp1Arg + 4n
    private const int  ArgCount = 6;          // all six are rewritten every transaction, so no stale arg can leak in

    private const uint MsgSetAllCoreCurveOptimizer = 0x4C;
    private const uint MsgSetPerCoreCurveOptimizer = 0x4B;

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
    // an empty slot and do nothing.
    private const int SlotsPerCcd = 8;
    private const int Zen5Ccd = 0;
    private const int Zen5cCcd = 1;
    private const int Zen5Cores = 4;      // fixed across Strix Point
    private const int Zen5SlotStride = 2;

    // SMU response register. Values per AMD's own driver (smu_cmn.c); AMD notes they are defined per-ASIC, so an
    // unrecognised value is reported raw rather than folded into a generic "failed".
    private const uint RspInProgress = 0x00;   // still executing
    private const uint RspOk         = 0x01;
    private const uint RspBusyOther  = 0xFC;   // busy with another command, retry
    private const uint RspBadPrereq  = 0xFD;   // valid command, prerequisites not met (platform/firmware gate)
    private const uint RspUnknownCmd = 0xFE;   // this firmware has no such command
    private const uint RspFailed     = 0xFF;   // the command ran and its status was failure

    /// <summary>Sentinel for "the transaction never reached a response" (a register access or the interlock failed),
    /// distinct from every value the response register can hold.</summary>
    private const uint NoResponse = uint.MaxValue;

    // The PawnIO module that whitelists the SMU register window, and the functions we drive. We do NOT use the
    // module's generic send-SMU-command entry point: its internal table resolves Strix Point to the RSMU address
    // triple, so a 0x4C sent that way would go to the wrong mailbox. Hand-rolling the transaction over the raw
    // register accessors is what G-Helper does, and it is legal because the module's own range check admits the
    // whole 0x3B10000-0x3B10FFF window these three registers live in.
    // The module blob is EMBEDDED, which is what its author prescribes: modules are LGPL-2.1-or-later, the
    // integration guide says to take a release blob and "include its contents in your software", and the project
    // states outright that module APIs are NOT stable across releases — so the version whose call shapes this file
    // is written against has to travel with it. Note the PawnIO installer ships no modules at all, so there is no
    // shared system location to read one from; an explicit override file is honoured for advanced use.
    private const string ModuleFile = "RyzenSMU.bin";
    private const string FnReadReg  = "ioctl_read_smu_register";    // 1 arg in, 1 value out
    private const string FnWriteReg = "ioctl_write_smu_register";   // 2 args in, nothing out

    // Cross-process interlock. The mailbox is reached through the PCI config index/data pair on 00:00.0, which
    // HWiNFO, CPU-Z, Ryzen Master and RyzenAdj all poke as well; this is the name that ecosystem agreed on. It is
    // held across the WHOLE transaction, because per-access locking still lets another agent's message execute
    // against our arguments.
    private const string PciMutexName = @"Global\Access_PCI";
    private const int MutexWaitMs = 5000;

    // Response poll budget. A mailbox message is normally acknowledged in microseconds; anything beyond this is a
    // wedged SMU, and we must return rather than spin — RyzenAdj's equivalent loop has no timeout at all, which is
    // how a stuck mailbox becomes a hung caller.
    private const int ResponseTimeoutMs = 200;

    // Exposed range. Negative only: a positive offset RAISES voltage, which buys nothing here and is a thermal and
    // stability risk, so it is not offered.
    //
    // The floor is -40 to match what the rest of the ecosystem allows, and because it is a real voltage bound
    // rather than a round number: measured on this part, one count is ~2.5 mV (an all-core -30 moved every per-core
    // voltage word in the SMU's PM table from ~1.03 V to ~0.95 V), so -40 is ~100 mV — a normal undervolt target,
    // while the only published failure datum on a sibling Zen 5 die is a crash at -50.
    //
    // Not lower, for a reason that is not mere caution: an ALL-CORE offset is bounded by the WORST core, which is
    // exactly why per-core Curve Optimizer exists — and per-core is unreachable here (opcode 0x4B is unconfirmed on
    // this die, reported rejected on Krackan Point, and there is no way to learn the core-fuse topology). So past
    // some point the limit is one weak core, not the average, and the good cores cannot cash in the difference.
    // Undervolt failures on Zen 5 also surface hours later at idle as machine-check errors or silent corruption
    // rather than as an obvious crash under load, so the end of the slider should not be a place a single drag
    // lands by accident.
    private const int MinCounts = -40;

    private const string UnsupportedError = "the SMU does not implement this command (0xFE)";

    private readonly byte[] _module;
    private readonly Lock _gate = new();

    private PawnIo? _io;

    /// <summary>Latched once the mailbox has answered 0xFE: this firmware does not implement the command, so every
    /// later attempt is pointless. Latched rather than retried so a mode switch can't spam a dead mailbox.</summary>
    private bool _unsupported;

    public string Name { get; }
    public (int Min, int Max) Range => (MinCounts, 0);

    /// <summary>Measured on this part, not taken from a spec: an all-core -30 moved every per-core voltage word in
    /// the SMU's PM telemetry table from ~1.03 V to ~0.95 V under a constant load, and back on stock — ~76 mV over
    /// 30 counts. (AMD publishes no mV-per-count figure for Zen 5; the community's Zen 3 number was 3-5 mV.) Only a
    /// display aid, since the real delta moves with frequency and temperature.</summary>
    public double MillivoltsPerCount => 2.5;

    /// <summary>The two Zen 5 clusters, which are independent voltage domains — measured at stock under load Zen 5 sits
    /// near 1.17 V and Zen 5c near 1.02 V, and offsetting one moves only that one. This is the finest granularity the
    /// hardware honours; see the constructor for why per-core is not it.</summary>
    public IReadOnlyList<VoltageDomain> Domains { get; }

    /// <summary>The CCD each entry of <see cref="Domains"/> covers, in the same order.</summary>
    private readonly int[] _domainCcd;

    public string? LastError { get; private set; }

    private RyzenCurveOptimizer(byte[] module, string name, int physicalCores)
    {
        _module = module;
        Name = name;

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
        // would then follow.
        var zen5c = physicalCores - Zen5Cores;
        if (zen5c is < 1 or > SlotsPerCcd) { Domains = []; _domainCcd = []; return; }

        Domains =
        [
            new VoltageDomain($"Zen 5", $"ccd:{Zen5Ccd}"),
            new VoltageDomain($"Zen 5c", $"ccd:{Zen5cCcd}"),
        ];
        _domainCcd = [Zen5Ccd, Zen5cCcd];
    }

    /// <summary>Probe for a tunable CPU. Returns null — feature hidden — unless this is a CPU whose MP1 mailbox
    /// layout is known AND PawnIO is installed. The driver check is the registry one, not a device open: it must
    /// stay cheap (composition runs on the UI thread) and it must not depend on elevation. Gating on it keeps the
    /// section honest — without the driver the sliders could appear and then refuse every write.
    /// <see cref="PawnIoInstaller"/> is what offers to install it. Never throws.</summary>
    public static RyzenCurveOptimizer? TryCreate()
    {
        try
        {
            if (!IsKnownStrixPoint() || !PawnIoInstaller.Installed) return null;
            var module = LoadModule();
            return module == null ? null : new RyzenCurveOptimizer(module, ReadCpuName(), PhysicalCores());
        }
        catch { return null; }
    }

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

            var io = Io();
            if (io == null) { LastError = "the PawnIO driver is not available"; return false; }

            return Accept(io, Transact(io, MsgSetAllCoreCurveOptimizer, Encode(counts)), "all cores");
        }
    }

    /// <summary>Turn a mailbox response into the port's bool + <see cref="LastError"/> contract.
    /// <paramref name="where"/> names the write, so a failure part-way through a multi-slot apply says which one.
    /// Caller holds <see cref="_gate"/>.</summary>
    private bool Accept(PawnIo io, uint rsp, string where)
    {
        switch (rsp)
        {
            case RspOk:
                return true;
            case RspUnknownCmd:
                _unsupported = true;
                LastError = UnsupportedError;
                return false;
            case RspBadPrereq:
                // The documented cause on a sibling Zen 5 mobile part is the active platform power mode: the mailbox
                // refuses while the system sits in an energy-saving mode.
                LastError = $"the SMU rejected {where} — prerequisites not met (0xFD), try a performance power mode";
                return false;
            default:
                // Transact has already explained the NoResponse cases (interlock, busy, no answer); only fill in a
                // message when it hasn't, so its specific reason survives.
                if (rsp != NoResponse) LastError = $"the SMU returned 0x{rsp:X2} for {where}";
                else LastError ??= io.LastError ?? "SMU register access failed";
                return false;
        }
    }

    /// <summary>Apply one offset per entry in <see cref="Domains"/>. Each is written to EVERY slot of its CCD — all 8,
    /// populated or not — because the rail follows the mildest request in the cluster, so a core left un-offset would
    /// undo the setting; and because an empty slot answers REP_MSG_OK and changes nothing (measured), which also makes
    /// this correct on a SKU with a different populated set. A partial failure leaves the clusters inconsistent, so the
    /// first refusal aborts and is reported rather than pressed on with.</summary>
    public bool SetDomains(IReadOnlyList<int> counts)
    {
        lock (_gate)
        {
            LastError = null;
            if (Domains.Count == 0) { LastError = "this CPU has no per-cluster control"; return false; }
            if (counts.Count != Domains.Count) { LastError = $"expected {Domains.Count} offsets, got {counts.Count}"; return false; }
            if (_unsupported) { LastError = UnsupportedError; return false; }

            var io = Io();
            if (io == null) { LastError = "the PawnIO driver is not available"; return false; }

            for (var d = 0; d < counts.Count; d++)
            {
                var arg = Encode(Math.Clamp(counts[d], MinCounts, 0));
                for (var slot = 0; slot < SlotsPerCcd; slot++)
                {
                    var rsp = Transact(io, MsgSetPerCoreCurveOptimizer, CoreArg(_domainCcd[d], slot, arg));
                    if (!Accept(io, rsp, $"{Domains[d].Label} slot {slot}")) return false;
                }
            }
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _io?.Dispose();
            _io = null;
        }
    }

    // ---- driver handle ----

    // Opened on first use, never at composition time: loading a PawnIO module makes the kernel verify a signed
    // bytecode blob, which is exactly the kind of blocking call this app keeps off the UI thread (and composition
    // runs there). Cached once open; a failed open is retried on the next call, since the user may install the
    // driver while the app is running. Caller holds _gate.
    private PawnIo? Io()
    {
        if (_io != null) return _io;
        try { return _io = PawnIo.TryLoad(_module); }
        catch { return null; }
    }

    // ---- the mailbox transaction ----

    // Encode an offset as the SMU expects: a 20-bit field, negatives as 0x100000 - |counts|. 0 is sent as a plain 0,
    // NOT as 0x100000 — that would set bit 20, which the per-core form of this message uses as a core selector and
    // is a known source of rejected arguments in other tools. The mask keeps that invariant local rather than
    // depending on the caller's clamp.
    private static uint Encode(int counts)
        => (counts >= 0 ? 0u : 0x100000u - (uint)(-counts)) & 0xFFFFFu;

    // Per-core argument: the CCD/slot selector in the high nibbles, the margin in the low 16 bits. The margin is
    // masked to 16 bits here because the selector occupies the bits an all-core value would otherwise run into.
    private static uint CoreArg(int ccd, int slot, uint encodedMargin)
        => ((uint)ccd << 28) | ((uint)(slot % SlotsPerCcd) << 20) | (encodedMargin & 0xFFFF);

    /// <summary>Run one MP1 message to completion and return the SMU's response byte, or <see cref="NoResponse"/>
    /// when the interlock or a register access failed (with <see cref="LastError"/> already set). Caller holds
    /// <see cref="_gate"/>.</summary>
    private uint Transact(PawnIo io, uint message, uint arg0)
    {
        Mutex? pci = null;
        var held = false;
        try
        {
            pci = TryOpenPciMutex();
            if (pci != null)
            {
                try { held = pci.WaitOne(MutexWaitMs); }
                catch (AbandonedMutexException) { held = true; }   // previous owner died holding it; it is ours now
                if (!held) { LastError = "another tool is holding the PCI access lock"; return NoResponse; }
            }

            // Wait for the mailbox to be idle. A zero response means a message is still in flight — someone else's
            // or a leftover — and writing ours on top of it would race.
            if (!WaitIdle(io)) return NoResponse;

            // Clear the response, then publish the arguments, then the message. Order matters: the SMU latches on
            // the message write, so the arguments must already be in place, and a stale non-zero response would
            // otherwise be mistaken for this message's answer.
            if (!Write(io, Mp1Rsp, 0)) return NoResponse;
            for (var i = 0; i < ArgCount; i++)
                if (!Write(io, Mp1Arg + (uint)(i * 4), i == 0 ? arg0 : 0)) return NoResponse;
            if (!Write(io, Mp1Msg, message)) return NoResponse;

            return PollResponse(io);
        }
        finally
        {
            if (pci != null)
            {
                // Same thread throughout (WaitOne/ReleaseMutex are thread-affine and this whole method is
                // synchronous under _gate), so releasing here is valid.
                if (held) { try { pci.ReleaseMutex(); } catch { /* best effort */ } }
                pci.Dispose();
            }
        }
    }

    private bool WaitIdle(PawnIo io)
    {
        var deadline = Environment.TickCount64 + ResponseTimeoutMs;
        while (true)
        {
            if (!Read(io, Mp1Rsp, out var rsp)) return false;
            if (rsp != RspInProgress) return true;
            if (Environment.TickCount64 >= deadline) { LastError = "the SMU mailbox is busy"; return false; }
            Thread.Sleep(1);
        }
    }

    private uint PollResponse(PawnIo io)
    {
        var deadline = Environment.TickCount64 + ResponseTimeoutMs;
        var spins = 0;
        while (true)
        {
            if (!Read(io, Mp1Rsp, out var rsp)) return NoResponse;
            // 0xFC is "busy with other commands, retry" rather than a verdict on our message, so it is polled
            // through like an in-flight response instead of surfaced.
            if (rsp != RspInProgress && rsp != RspBusyOther) return rsp;
            if (Environment.TickCount64 >= deadline)
            {
                LastError = rsp == RspBusyOther ? "the SMU stayed busy (0xFC)" : "the SMU did not answer";
                return NoResponse;
            }
            if (++spins < 32) Thread.SpinWait(64); else Thread.Sleep(1);
        }
    }

    private bool Read(PawnIo io, uint address, out uint value)
    {
        Span<ulong> outv = stackalloc ulong[1];
        if (!io.Execute(FnReadReg, [address], outv)) { value = 0; LastError = io.LastError; return false; }
        value = (uint)outv[0];
        return true;
    }

    private bool Write(PawnIo io, uint address, uint value)
    {
        if (io.Execute(FnWriteReg, [address, value], [])) return true;
        LastError = io.LastError;
        return false;
    }

    // ---- CPU identification ----

    // Only the CPUs whose MP1 mailbox layout is actually known: AMD family 0x1A (Zen 5), models 0x20 and 0x24 — the
    // Strix Point pair every tool maps to one address set. Krackan Point (0x60) and Strix Halo (0x70) are
    // deliberately NOT accepted: they share the family case group in the reverse-engineering projects, but the
    // published results diverge (the same opcode is reported rejected on one and effective on the other), so
    // claiming support here would be guessing on someone else's hardware.
    /// <summary>Whether this CPU is one the undervolt supports, independent of whether the driver is installed — so
    /// the driver-setup offer knows if it is even relevant here.</summary>
    public static bool SupportedCpu => IsKnownStrixPoint();

    private static bool IsKnownStrixPoint()
    {
        if (!X86Base.IsSupported) return false;

        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        if (ebx != 0x68747541 || edx != 0x69746E65 || ecx != 0x444D4163) return false;   // "AuthenticAMD"

        var (eax, _, _, _) = X86Base.CpuId(1, 0);
        var family = ((eax >> 8) & 0xF) + ((eax >> 20) & 0xFF);
        var model = ((eax >> 4) & 0xF) | (((eax >> 16) & 0xF) << 4);
        return family == 0x1A && model is 0x20 or 0x24;
    }

    // Physical cores, for labelling the groups: CPUID 0x8000001E EBX[15:8] is "threads per compute unit minus 1", so
    // the logical count divided by that gives cores. Falls back to the logical count if the leaf is absent, which
    // only mislabels the group sizes — nothing addresses cores by them.
    private static int PhysicalCores()
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

    // CPUID leaves 0x80000002..0x80000004 hold the 48-byte brand string. Used for the section header, mirroring
    // NvidiaGpu.Name; a CPU that does not advertise it falls back to a generic label rather than an empty header.
    private static string ReadCpuName()
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

    // ---- module + interlock lookup ----

    // Embedded blob by default, with a user file allowed to override it — the same arrangement acer-models.json
    // uses. The override exists because module APIs are explicitly unstable across releases: if a future blob
    // changes a call shape, someone can pin their own without waiting for a build. It is deliberately an explicit
    // path in the app's own config folder rather than a scan of shared locations, so a stray file elsewhere can
    // never silently change which bytes get loaded into the kernel.
    private static byte[]? LoadModule()
    {
        foreach (var path in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                  "AcerHelper", ModuleFile),
                     Path.Combine(AppContext.BaseDirectory, ModuleFile),
                 })
        {
            try { if (File.Exists(path)) return File.ReadAllBytes(path); }
            catch { /* unreadable override — fall through to the embedded copy */ }
        }

        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var res = asm.GetManifestResourceNames()
                         .FirstOrDefault(n => n.EndsWith(ModuleFile, StringComparison.OrdinalIgnoreCase));
            if (res == null) return null;   // dev build without the fetched blob -> feature hidden
            using var stream = asm.GetManifestResourceStream(res);
            if (stream == null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // Opened per transaction rather than held for the process lifetime, so the app never keeps a machine-wide lock
    // while idle. Null (and the transaction proceeds unlocked) if the name cannot be created — better a possible
    // collision with another tuning tool than no CO at all.
    private static Mutex? TryOpenPciMutex()
    {
        try { return new Mutex(false, PciMutexName, out _); }
        catch { return null; }   // no SeCreateGlobalPrivilege / denied by an existing owner's ACL
    }
}
