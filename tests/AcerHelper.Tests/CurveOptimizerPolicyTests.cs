using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// THE CURVE OPTIMIZER'S PORT POLICY, driven through injected I/O — the Linux port's correctness rests on this file,
/// because the Linux half (<c>RyzenCurveOptimizer.Linux.cs</c>) is excluded from this project's TFM and cannot be
/// compiled here at all. What is proved here is everything above the byte layer: the mailbox sequence and its ORDER,
/// the response-code mapping, the per-rail write policy, the read-back asymmetry, the availability gate's decision
/// table and the interlock's three outcomes.
///
/// THE SEQUENCE IS THE HIGHEST-VALUE THING IN THE FILE, and it is asserted as a TRACE rather than per-effect: a
/// transaction that clears the response after publishing the message, or that publishes the message before the
/// arguments, still "works" on a machine where nothing else is using the mailbox — and produces a wrong reading the
/// day something is (the SMU latches on the message write, so a stale response would be read as this message's
/// answer). The fake therefore records every register access in order, and the order is compared literally.
///
/// WHAT CANNOT BE PROVED HERE, said plainly because a green suite must not be read as more: nothing in this project
/// executes the smn node's byte-level I/O — that a four-byte write is a read, that an eight-byte write is a write and
/// that a short or long write is a silent no-op are properties of the driver (measured; see
/// docs/curve-optimizer-linux.md), and no test below touches hardware. What is pinned is the PAYLOAD the Linux side
/// builds and the verdict it takes from the reply.
/// </summary>
public class CurveOptimizerPolicyTests
{
    // The two register triples, spelled here as the literals the reverse-engineering projects agree on — so a change
    // inside the port cannot satisfy these tests by moving the constant it is compared against.
    private const uint Mp1Msg = 0x03B10928, Mp1Rsp = 0x03B10978, Mp1Arg = 0x03B10998;
    private const uint RsmuMsg = 0x03B10A20, RsmuRsp = 0x03B10A80, RsmuArg = 0x03B10A88;

    private const uint RspOk = 0x01, RspBusyOther = 0xFC, RspBadPrereq = 0xFD, RspUnknownCmd = 0xFE, RspFailed = 0xFF;

    private const int SlotsPerCcd = 8;
    private const int ArgCount = 6;

    /// <summary>This machine's core count (4x Zen 5 + 6x Zen 5c), which is what the three-domain split is derived
    /// from.</summary>
    private const int An1861Cores = 10;

    /// <summary>The reads one successful 8-slot cluster apply takes: per transaction an idle read, a poll read and
    /// the argument-0 read.</summary>
    private const int ReadsPerTransaction = 3;

    // ================= the fake transport =================

    /// <summary>A mailbox and its register file, reached exactly the way the two OS transports reach the real one: one
    /// read and one write delegate. Every access is recorded, so a test can assert an ORDER rather than only an
    /// outcome.</summary>
    private sealed class FakeSmu
    {
        private readonly List<string> _trace = [];
        private readonly Queue<uint> _responses = new();
        private int _reads;

        /// <summary>The values the response register answers with, in reading order, with THE LAST ONE REPEATING: a
        /// single entry is a mailbox that gives the same answer to every read, and an empty queue is a mailbox that
        /// never answers at all (0x00 = still executing, forever). The first read is the transaction's idle wait, so
        /// the first entry has to be non-zero or the write is never attempted.</summary>
        internal Queue<uint> Responses => _responses;

        /// <summary>What argument 0 holds when it is read — where a getter's value comes back.</summary>
        internal uint Arg0 { get; set; }

        internal bool FailReads { get; set; }
        internal bool FailWrites { get; set; }

        /// <summary>Start failing reads once this many have succeeded — the only way to reach the middle of a
        /// multi-transaction apply, where a transport can break between the setter and its read-back.</summary>
        internal int? FailReadsAfter { get; set; }

        internal string IoError { get; set; } = "the transport refused the access";

        /// <summary>Queue the next answers the response register gives: <paramref name="times"/> copies of one value,
        /// in order of the calls that plan them. One read per answer, so a test that wants to know WHICH message got
        /// a refusal has to count the reads its own apply makes (two per message plus the argument-0 read).</summary>
        internal void Plan(uint value, int times)
        {
            for (var i = 0; i < times; i++) _responses.Enqueue(value);
        }

        internal IReadOnlyList<string> Trace => _trace;

        /// <summary>The trace entries that are writes, as (address, value), in order.</summary>
        internal List<(uint Address, uint Value)> Writes()
            => _trace.Where(e => e[0] == 'w').Select(e => e.Split(' '))
                     .Select(p => (Convert.ToUInt32(p[1], 16), Convert.ToUInt32(p[2], 16))).ToList();

        internal List<uint> ReadAddresses()
            => _trace.Where(e => e[0] == 'r').Select(e => Convert.ToUInt32(e[2..], 16)).ToList();

        /// <summary>The writes to one register, in order — the shape every order assertion below uses.</summary>
        internal List<uint> WritesTo(uint address) => Writes().Where(w => w.Address == address).Select(w => w.Value).ToList();

        /// <summary>The writes whose value has the given low sixteen bits set — the margin a per-slot argument
        /// carries, which is what "how deep was this rail offset" means inside a CoreArg.</summary>
        internal List<uint> MarginsOf(uint address) => WritesTo(address).Select(v => v & 0xFFFFu).ToList();

        internal uint? Read(uint address)
        {
            _trace.Add($"r {address:X8}");
            if (FailReads || (FailReadsAfter is { } limit && _reads >= limit)) return null;
            _reads++;

            // The argument register holds the value the SMU left there; the response register answers from the plan.
            if (address == Mp1Arg || address == RsmuArg) return Arg0;
            if (_responses.Count == 0) return 0x00;              // nothing planned -> still executing, for ever

            var value = _responses.Dequeue();
            if (_responses.Count == 0) _responses.Enqueue(value);   // the last answer repeats
            return value;
        }

        internal bool Write(uint address, uint value)
        {
            _trace.Add($"w {address:X8} {value:X8}");
            return !FailWrites;
        }
    }

    /// <summary>The interlock, in the three shapes the port has to tell apart.</summary>
    private sealed class FakeInterlock
    {
        /// <summary>Whether a handle is handed back at all — false is "the name could not even be created".</summary>
        internal bool Granted { get; set; } = true;

        /// <summary>Null with a reason = someone else holds it; that is what abandons the write. Both unset is the
        /// third case: the lock could not be created, and the transaction proceeds UNLOCKED.</summary>
        internal string? Refusal { get; set; }

        internal int Taken { get; private set; }
        internal bool Released { get; private set; }

        internal IDisposable? Take(out string? error)
        {
            error = null;
            Taken++;
            if (Refusal != null) { error = Refusal; return null; }
            return Granted ? new Handle(this) : null;
        }

        private sealed class Handle(FakeInterlock owner) : IDisposable
        {
            public void Dispose() => owner.Released = true;
        }
    }

    private static CurveOptimizerPolicy Policy(FakeSmu smu, int cores = An1861Cores,
                                              FakeInterlock? interlock = null, int timeoutMs = 50)
        => new("AMD Ryzen AI 9 365 w/ Radeon 880M", cores, smu.Read, smu.Write, () => smu.IoError,
               interlock is null ? null : interlock.Take)
        { TimeoutMs = timeoutMs };

    private static string Read(uint address) => $"r {address:X8}";

    // ================= the byte layouts =================

    /// <summary>The transaction's argument window is 24 bytes wide and is rewritten IN FULL on every message: six
    /// 4-byte words at arg base, base+4 … base+20, with the caller's value in argument 0 and ZERO in the other five.
    /// The zeros are load-bearing — a stale argument from an earlier transaction would otherwise be executed as part
    /// of this message — so the whole window is asserted, not just the address of the interesting word.</summary>
    [Fact]
    public void TheTransactionPublishesSixFourByteArgumentsAtArgBasePlus4n()
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);

        Assert.True(Policy(smu).Set(-40));

        var arguments = smu.Writes().Where(w => w.Address >= Mp1Arg && w.Address < Mp1Arg + ArgCount * 4).ToList();
        (uint, uint)[] expected =
        [
            (Mp1Arg + 0, CurveOptimizerPolicy.Encode(-40)),
            (Mp1Arg + 4, 0u), (Mp1Arg + 8, 0u), (Mp1Arg + 12, 0u), (Mp1Arg + 16, 0u), (Mp1Arg + 20, 0u),
        ];
        Assert.Equal(expected, arguments);
        Assert.Equal(ArgCount * 4, (int)(arguments[^1].Address - arguments[0].Address) + 4);   // 24 bytes wide
    }

    /// <summary>The message register carries THE OPCODE ALONE — one byte's worth of value in a 32-bit word. A message
    /// word with anything in its upper bytes would be a different command, and the upper bytes are exactly where a
    /// stray encoding (a 20-bit margin, a CCD nibble) would land if one were packed in beside the opcode.</summary>
    [Fact]
    public void TheMessageRegisterCarriesTheOpcodeAlone()
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);
        Assert.True(Policy(smu).Set(-30));
        Assert.Equal([0x4Cu], smu.WritesTo(Mp1Msg));

        var split = new FakeSmu();
        split.Responses.Enqueue(RspOk);
        Assert.True(Policy(split).SetDomains([-1, -2, 0]));

        Assert.All(split.Writes().Where(w => w.Address is Mp1Msg or RsmuMsg), w => Assert.Equal(0u, w.Value & 0xFFFFFF00u));
        Assert.Contains(0x4Bu, split.WritesTo(Mp1Msg));      // per-cluster: one message per CCD slot
        Assert.Contains(0x1Fu, split.WritesTo(RsmuMsg));     // the graphics margin, set
        Assert.Contains(0x20u, split.WritesTo(RsmuMsg));     // ...and read back
    }

    /// <summary>The smn node's OWN byte layout, which is what the Linux side hands to a FileStream: exactly four bytes
    /// for a read request (the address, little-endian) and exactly eight for a write (address then value, in that
    /// order). Any other length is a no-op in the driver — a silent one, since it still reports the count as written
    /// — so the two lengths and the two field positions are pinned here rather than assembled at the I/O site.</summary>
    [Fact]
    public void TheSmnRequestsAreFourBytesForAReadAndEightForAWrite()
    {
        var read = CurveOptimizerPolicy.SmnReadRequest(0x03B10978);
        Assert.Equal(4, read.Length);
        Assert.Equal([0x78, 0x09, 0xB1, 0x03], read);                 // the little-endian address and nothing else

        var write = CurveOptimizerPolicy.SmnWriteRequest(0x03B10998, 0x0000FFFB);
        Assert.Equal(8, write.Length);
        Assert.Equal([0x98, 0x09, 0xB1, 0x03, 0xFB, 0xFF, 0x00, 0x00], write);

        // ...and the reply is read back as one little-endian u32, which is the only thing the node ever answers.
        Assert.Equal(0x03B10998u, CurveOptimizerPolicy.SmnValue(write.AsSpan(0, 4)));
        Assert.Equal(0x0000FFFBu, CurveOptimizerPolicy.SmnValue(write.AsSpan(4, 4)));

        // The two payloads are told apart by LENGTH at the kernel side, so a four-byte write can never carry a value.
        Assert.Equal(4, CurveOptimizerPolicy.SmnReadRequest(0).Length);
        Assert.Equal(8, CurveOptimizerPolicy.SmnWriteRequest(0, 0).Length);
    }

    /// <summary>The node's verdict on an eight-byte write: 0x01 is the SMU call getting through, 0xF6 is a PCI access
    /// failure, and anything unrecognised is NOT accepted — the whole reason this transport was chosen over the
    /// driver's command nodes is that its answer is the SMU's own, so guessing at an unknown code would give that
    /// away.</summary>
    [Fact]
    public void TheSmnWriteIsAcknowledgedOnlyByTheSmuSuccessCode()
    {
        Assert.True(CurveOptimizerPolicy.SmnAcknowledged(0x01));
        Assert.False(CurveOptimizerPolicy.SmnAcknowledged(0xF6));
        Assert.False(CurveOptimizerPolicy.SmnAcknowledged(0x00));
        Assert.False(CurveOptimizerPolicy.SmnAcknowledged(0xFD));
        Assert.False(CurveOptimizerPolicy.SmnAcknowledged(0xFFFFFFFF));
    }

    // ================= the transaction's order =================

    /// <summary>THE ORDER, asserted literally: wait for an idle mailbox, clear the response, publish all six
    /// arguments, write the message, poll, read argument 0. Both inversions are invisible on an idle machine and
    /// wrong on a busy one — publishing the message before the arguments sends the previous message's arguments, and
    /// clearing the response after publishing the message can read a stale response as this message's answer.</summary>
    [Fact]
    public void TheTransactionWaitsClearsPublishesThenSends()
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);
        smu.Arg0 = 0xDEADBEEF;

        Assert.True(Policy(smu).Set(-5));

        string[] expected =
        [
            Read(Mp1Rsp),                          // 1. idle: a zero response would mean a message is in flight
            $"w {Mp1Rsp:X8} 00000000",             // 2. clear the response
            $"w {Mp1Arg + 0:X8} 000FFFFB",         // 3. argument 0: the all-core margin, the 20-bit form of -5
            $"w {Mp1Arg + 4:X8} 00000000",
            $"w {Mp1Arg + 8:X8} 00000000",
            $"w {Mp1Arg + 12:X8} 00000000",
            $"w {Mp1Arg + 16:X8} 00000000",
            $"w {Mp1Arg + 20:X8} 00000000",
            $"w {Mp1Msg:X8} 0000004C",             // 4. the message, whose write the SMU latches on
            Read(Mp1Rsp),                          // 5. poll
            Read(Mp1Arg),                          // 6. argument 0, inside the transaction
        ];
        Assert.Equal(expected, smu.Trace);
    }

    /// <summary>Argument 0 is read only when the response says OK: on a refusal there is no answer to fetch, and
    /// reading it anyway would take whatever the SMU left there as if it were this message's reply.</summary>
    [Theory]
    [InlineData(RspBadPrereq)]
    [InlineData(RspFailed)]
    [InlineData(0x37)]
    public void ArgumentZeroIsReadOnlyOnOk(uint response)
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);
        smu.Responses.Enqueue(response);

        Assert.False(Policy(smu).Set(-5));

        Assert.DoesNotContain(Read(Mp1Arg), smu.Trace);
    }

    /// <summary>A response that is not OK is a false return with the register's value reported RAW — AMD defines these
    /// per ASIC, so an unrecognised code is named rather than folded into a generic "failed".</summary>
    [Theory]
    [InlineData(RspFailed, "the SMU returned 0xFF for all cores")]
    [InlineData(0x37, "the SMU returned 0x37 for all cores")]
    public void AnUnrecognisedResponseIsReportedRaw(uint response, string expected)
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);
        smu.Responses.Enqueue(response);
        var policy = Policy(smu);

        Assert.False(policy.Set(-5));
        Assert.Equal(expected, policy.LastError);
    }

    /// <summary>0xFD is a VALID command whose prerequisites are unmet — the documented cause being the platform's
    /// active power mode, which is why the message names one the user can act on instead of reporting a bare code.</summary>
    [Fact]
    public void ABadPrerequisiteIsReportedWithThePowerModeHint()
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);
        smu.Responses.Enqueue(RspBadPrereq);
        var policy = Policy(smu);

        Assert.False(policy.Set(-5));
        Assert.Equal("the SMU rejected all cores — prerequisites not met (0xFD), try a performance power mode", policy.LastError);
    }

    /// <summary>0xFC means "busy with another command", not a verdict on ours, so it is polled THROUGH and never
    /// surfaced — a single busy read reported as a failure would make the app refuse writes on a machine that is
    /// merely talking to its own SMU.</summary>
    [Fact]
    public void ABusyMailboxIsPolledThroughAndNotSurfaced()
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);          // idle
        smu.Responses.Enqueue(RspBusyOther);
        smu.Responses.Enqueue(RspBusyOther);
        smu.Responses.Enqueue(RspOk);
        var policy = Policy(smu);

        Assert.True(policy.Set(-10));
        Assert.Null(policy.LastError);
        Assert.Equal(4, smu.ReadAddresses().Count(a => a == Mp1Rsp));   // the idle read + three poll readings
    }

    /// <summary>...and a mailbox that STAYS busy is a failure with its own words, distinct from one that never
    /// answers — the two are different hardware states and the user is told which.</summary>
    [Fact]
    public void AMailboxThatStaysBusyIsReportedAsSuch()
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);
        smu.Responses.Enqueue(RspBusyOther);
        var policy = Policy(smu, timeoutMs: 5);

        Assert.False(policy.Set(-10));
        Assert.Equal("the MP1 mailbox stayed busy (0xFC)", policy.LastError);
    }

    /// <summary>The poll budget is finite and short. RyzenAdj's equivalent loop has no timeout at all, which is how a
    /// wedged mailbox becomes a hung caller — the app calls this from a worker thread that also has to answer the UI.
    /// The constant is asserted (200 ms) as well as the behaviour, so a future edit to either is seen.</summary>
    [Fact]
    public void AMailboxThatNeverAnswersGivesUpOnTheBudget()
    {
        Assert.Equal(200, CurveOptimizerPolicy.ResponseTimeoutMs);

        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);
        smu.Responses.Enqueue(0x00);           // still executing, forever
        var policy = Policy(smu, timeoutMs: 5);

        Assert.False(policy.Set(-10));
        Assert.Equal("the MP1 mailbox did not answer", policy.LastError);
    }

    /// <summary>A mailbox that is never even idle is refused BEFORE anything is written: the first write would
    /// otherwise race a message already in flight — someone else's, or a leftover — and could be executed against
    /// that message's arguments.</summary>
    [Fact]
    public void AnIdleWaitThatExpiresWritesNothing()
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(0x00);           // never idle
        var policy = Policy(smu, timeoutMs: 5);

        Assert.False(policy.Set(-10));
        Assert.Equal("the MP1 mailbox is busy", policy.LastError);
        Assert.Empty(smu.Writes());
    }

    /// <summary>A register access that fails is NOT a response: the port reports the transport's own reason (the
    /// Linux side's would be "Permission denied" on the node, Windows' "the PawnIO driver is not available") rather
    /// than a number nobody sent.</summary>
    [Fact]
    public void AFailedAccessCarriesTheTransportsOwnReason()
    {
        var smu = new FakeSmu { FailReads = true, IoError = "/sys/kernel/ryzen_smu_drv/smn: Permission denied" };
        var policy = Policy(smu);

        Assert.False(policy.Set(-10));
        Assert.Equal("/sys/kernel/ryzen_smu_drv/smn: Permission denied", policy.LastError);

        var write = new FakeSmu { FailWrites = true, IoError = "the PCI access failed (0xF6)" };
        write.Responses.Enqueue(RspOk);
        var second = Policy(write);

        Assert.False(second.Set(-10));
        Assert.Equal("the PCI access failed (0xF6)", second.LastError);
    }

    /// <summary>0xFE is LATCHED: this firmware has no such command, so every later attempt is refused without a
    /// transaction at all. Retrying it would put a message on a dead mailbox on every performance-mode switch, which
    /// is what the latch exists to prevent — and it closes both write paths, not just the all-core one.</summary>
    [Fact]
    public void AnUnsupportedCommandIsLatchedAndNeverRetried()
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);
        smu.Responses.Enqueue(RspUnknownCmd);
        var policy = Policy(smu);

        Assert.False(policy.Set(-5));
        Assert.Equal("the SMU does not implement this command (0xFE)", policy.LastError);
        Assert.True(policy.Unsupported);

        var after = smu.Trace.Count;                        // the transaction that got the refusal
        smu.Responses.Enqueue(RspOk);                       // ...a mailbox that would now answer OK

        Assert.False(policy.Set(-5));
        Assert.Equal("the SMU does not implement this command (0xFE)", policy.LastError);
        Assert.Equal(after, smu.Trace.Count);                // ...and NO register was touched the second time

        Assert.False(policy.SetDomains([-1, -1, -1]));
        Assert.Equal("the SMU does not implement this command (0xFE)", policy.LastError);
        Assert.Equal(after, smu.Trace.Count);
    }

    // ================= the domains =================

    /// <summary>The three rails, their stable settings keys, their ranges and — the part that must not be shared —
    /// their millivolt scales. The iGPU's is 5.0 against the cores' 2.5: a domain that inherited the port's figure
    /// would misreport every graphics offset by a factor of two, which is why the per-domain value is read per domain
    /// (Domain/Ports.cs, VoltageDomain.MillivoltsPerCount) rather than assumed.</summary>
    [Fact]
    public void TheThreeDomainsCarryTheirOwnKeysRangesAndScales()
    {
        var policy = Policy(new FakeSmu());

        Assert.Equal(3, policy.Domains.Count);
        Assert.Equal(["ccd:0", "ccd:1", "gfx"], policy.Domains.Select(d => d.Key));

        // Labels are architecture names, shown as-is and deliberately not translated.
        Assert.Equal(["Zen 5", "Zen 5c", "iGPU"], policy.Domains.Select(d => d.Label));

        Assert.Equal((-40, 0), policy.Range);
        Assert.Equal([null, null, (-50, 0)], policy.Domains.Select(d => d.Range).ToArray());

        Assert.Equal(2.5, policy.MillivoltsPerCount);
        Assert.Equal(2.5, policy.Domains[0].MillivoltsPerCount);
        Assert.Equal(2.5, policy.Domains[1].MillivoltsPerCount);
        Assert.Equal(5.0, policy.Domains[2].MillivoltsPerCount);
    }

    /// <summary>The domain list is index-aligned with the CCD each entry covers, and the iGPU is NOT a CCD: the
    /// mapping is what the write path routes on (a GPU domain goes to the other mailbox, a cluster writes its own
    /// CCD's slots), so it is asserted rather than inferred from the labels.</summary>
    [Fact]
    public void TheDomainToCcdMappingIsTheOneTheWritePathRoutesOn()
    {
        var policy = Policy(new FakeSmu());

        Assert.Equal(0, policy.CcdOf(0));       // Zen 5 -> CCD 0
        Assert.Equal(1, policy.CcdOf(1));       // Zen 5c -> CCD 1
        Assert.Equal(-1, policy.CcdOf(2));      // the iGPU, which is not a cluster at all
    }

    /// <summary>Two clusters is a property of THIS part (4x Zen 5 + 6x Zen 5c): the split is derived from the core
    /// count, and a core count the split cannot answer leaves the domain list EMPTY rather than guessing — which is
    /// the branch the port's own remark says must not be papered over, because a single iGPU domain would quietly
    /// turn the CPU slider into a graphics one.</summary>
    [Theory]
    [InlineData(10, 3)]     // this machine: 4 Zen 5 + 6 Zen 5c
    [InlineData(12, 3)]     // Ryzen AI 9 HX 370: 4 + 8
    [InlineData(5, 3)]      // the minimum the split can answer (1 Zen 5c)
    [InlineData(4, 0)]      // no Zen 5c at all: no answer, so no domains
    [InlineData(13, 0)]     // more Zen 5c than a CCD has slots: no answer either
    [InlineData(24, 0)]
    public void TheDomainSplitComesFromTheCoreCountOrNotAtAll(int cores, int expectedDomains)
        => Assert.Equal(expectedDomains, Policy(new FakeSmu(), cores).Domains.Count);

    /// <summary>A CPU with no domain list refuses the domain path with its own words, while the all-core path still
    /// works — they are different messages (0x4C against 0x4B) and neither depends on the other.</summary>
    [Fact]
    public void AOneDomainCpuRefusesTheDomainWriteAndKeepsTheAllCoreOne()
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);
        var policy = Policy(smu, cores: 4);

        Assert.Empty(policy.Domains);
        Assert.False(policy.SetDomains([0]));
        Assert.Equal("this CPU has no per-cluster control", policy.LastError);
        Assert.True(policy.Set(-16));
    }

    /// <summary>A count that does not match the rail list has no correct reading — which rail would a spare number
    /// belong to? — so it is refused rather than truncated or padded.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void ADomainWriteNeedsOneOffsetPerRail(int supplied)
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);
        var policy = Policy(smu);

        Assert.False(policy.SetDomains(new int[supplied]));
        Assert.Equal($"expected 3 offsets, got {supplied}", policy.LastError);
        Assert.Empty(smu.Trace);
    }

    /// <summary>Every CPU rail is written to EVERY slot of its CCD — all eight, populated or not. Not a
    /// simplification: the cluster's rail follows the MILDIEST request among its cores, so a slot left un-offset
    /// would undo the setting, and an unpopulated slot answers OK and changes nothing (measured), which is what makes
    /// the same loop correct on a die with a different populated set.</summary>
    [Fact]
    public void EachClusterIsWrittenToEverySlotOfItsOwnCcd()
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);
        var policy = Policy(smu);

        Assert.True(policy.SetDomains([-10, -20, 0]));

        Assert.Equal(2 * SlotsPerCcd, smu.WritesTo(Mp1Msg).Count);                  // two clusters, eight slots each
        Assert.All(smu.WritesTo(Mp1Msg), m => Assert.Equal(CurveOptimizerPolicy.MsgSetPerCoreCurveOptimizer, m));

        var arguments = smu.WritesTo(Mp1Arg);
        Assert.Equal(2 * SlotsPerCcd, arguments.Count);
        for (var slot = 0; slot < SlotsPerCcd; slot++)
        {
            // The selector is the CCD in the top nibble and the slot in bits 20..23; the margin is the 16-bit form.
            Assert.Equal(CurveOptimizerPolicy.CoreArg(0, slot, CurveOptimizerPolicy.Encode(-10)), arguments[slot]);
            Assert.Equal(CurveOptimizerPolicy.CoreArg(1, slot, CurveOptimizerPolicy.Encode(-20)), arguments[SlotsPerCcd + slot]);
        }

        Assert.Equal(0u, arguments[0] & 0x00F00000u);                          // CCD 0, slot 0
        Assert.Equal(0x10000000u, arguments[SlotsPerCcd] & 0xF0000000u);       // CCD 1
        Assert.Equal(0u, arguments[SlotsPerCcd] & 0x00F00000u);                // ...starting at slot 0
    }

    /// <summary>A refusal aborts the rest of the apply: a partial write leaves the clusters disagreeing about
    /// something the user set as one thing, so the first refusal is reported — naming the rail and the slot it
    /// happened on — and nothing after it is attempted.</summary>
    [Fact]
    public void TheFirstRefusalAbortsTheRemainingSlots()
    {
        var smu = new FakeSmu();
        // One answer per response-register read, two per message (the idle wait and the poll), so seven
        // acknowledgements carry slots 0..2 and the idle wait of slot 3 before the eighth read is the refusal.
        smu.Plan(RspOk, 7);
        smu.Plan(RspFailed, 1);
        var policy = Policy(smu);

        Assert.False(policy.SetDomains([-10, -20, 0]));
        Assert.Equal("the SMU returned 0xFF for Zen 5 slot 3", policy.LastError);

        // Four transactions happened (and not eight), and the SECOND cluster was never reached.
        Assert.Equal(4, smu.WritesTo(Mp1Msg).Count);
        Assert.DoesNotContain(0x10000000u, smu.WritesTo(Mp1Arg).Select(a => a & 0xF0000000u));
    }

    /// <summary>A request is clamped PER RAIL before it reaches the mailbox: the cores' floor is -40 and the graphics
    /// rail's is -50, and the rail's own bound is the one that applies to its number (the two are different silicon).
    /// The clamp is the port's last line — the service and the view-model clamp earlier, deliberately.</summary>
    [Fact]
    public void EachRailsRequestIsClampedToItsOwnRange()
    {
        var smu = new FakeSmu { Arg0 = unchecked((uint)-50) };   // the graphics rail confirms the clamp it was given
        smu.Responses.Enqueue(RspOk);

        Assert.True(Policy(smu).SetDomains([-400, -400, -400]));
        Assert.Contains(CurveOptimizerPolicy.CoreArg(0, 0, CurveOptimizerPolicy.Encode(-40)), smu.WritesTo(Mp1Arg));
        Assert.Contains(CurveOptimizerPolicy.CoreArg(1, 0, CurveOptimizerPolicy.Encode(-40)), smu.WritesTo(Mp1Arg));
        // The iGPU's own floor, not the cores' — 5 mV a count makes -50 the -250 mV its label warns about.
        Assert.Contains(CurveOptimizerPolicy.GpuMargin(-50), smu.WritesTo(RsmuArg));

        // A positive request is collapsed to stock rather than raising voltage, on BOTH rails: the margin each
        // argument carries is zero, on the cores (whose selector nibbles are still set) and on the graphics word.
        var positive = new FakeSmu();
        positive.Responses.Enqueue(RspOk);
        Assert.True(Policy(positive).SetDomains([5, 5, 5]));
        Assert.All(positive.MarginsOf(Mp1Arg), margin => Assert.Equal(0u, margin));
        Assert.All(positive.WritesTo(RsmuArg), value => Assert.Equal(0u, value));
    }

    // ================= the graphics rail and its read-back =================

    /// <summary>The graphics rail is ONE write on the OTHER MAILBOX — the RSMU triple, not eight MP1 slots. The two
    /// mailboxes are not interchangeable (the CPU curve is on MP1, the graphics curve on RSMU), and a graphics margin
    /// sent through the CPU path would land on a register that path's opcode reads as something else.</summary>
    [Fact]
    public void TheGpuIsOneWriteOnTheOtherMailbox()
    {
        var smu = new FakeSmu { Arg0 = unchecked((uint)-5) };     // what the graphics rail answers with
        smu.Responses.Enqueue(RspOk);
        var policy = Policy(smu);

        Assert.True(policy.SetDomains([-10, -20, -5]));

        Assert.Equal([0x4Bu], smu.WritesTo(Mp1Msg).Distinct());
        Assert.Equal([CurveOptimizerPolicy.MsgSetGpuPsmMargin, CurveOptimizerPolicy.MsgGetGpuPsmMargin], smu.WritesTo(RsmuMsg));
        Assert.Contains(CurveOptimizerPolicy.GpuMargin(-5), smu.WritesTo(RsmuArg));
        Assert.Contains(CurveOptimizerPolicy.CoreArg(0, 0, CurveOptimizerPolicy.Encode(-10)), smu.WritesTo(Mp1Arg));

        // The getter takes no argument: the margin it reads is whatever the rail holds.
        Assert.Equal(0u, smu.WritesTo(RsmuArg)[^1]);
        // ...and it is read on the RSMU response register, not on MP1's: two transactions there (the set and the
        // getter), each with an idle read and a poll.
        Assert.Equal(2 * 2, smu.ReadAddresses().Count(a => a == RsmuRsp));
        Assert.Equal(2 * SlotsPerCcd * 2, smu.ReadAddresses().Count(a => a == Mp1Rsp));   // 2 clusters x 8 x (idle+poll)
    }

    /// <summary>The SETTER takes the 16-bit two's-complement margin and the GETTER answers a FULL 32-BIT SIGNED
    /// value — the asymmetry that makes this pairing fail silently when it is got backwards. -5 comes back as
    /// 0xFFFFFFFB, and the reply is taken as the whole signed word: nothing else is that value.</summary>
    [Fact]
    public void TheGpuReadBackTakesTheWholeThirtyTwoBitSignedReply()
    {
        var smu = new FakeSmu { Arg0 = unchecked((uint)-5) };
        smu.Responses.Enqueue(RspOk);
        var policy = Policy(smu);

        Assert.Equal(0xFFFBu, CurveOptimizerPolicy.GpuMargin(-5));                 // the setter's width
        Assert.Equal(0xFFFFFFFBu, unchecked((uint)-5));                            // the getter's
        Assert.True(policy.SetDomains([0, 0, -5]));
        Assert.Null(policy.LastError);
    }

    /// <summary>...and a reply that is NOT that value is reported as a mismatch with BOTH numbers, because "accepted
    /// -5, reads back -65541" and "accepted -5, reads back 65531" are different bugs: the first is a reply
    /// sign-extended at the wrong width, the second a 16-bit word read where a 32-bit signed one was meant.</summary>
    [Theory]
    [InlineData(0x0000FFFBu, "65531")]        // the setter's own 16-bit word, read back as if it were the answer
    [InlineData(0xFFFEFFFBu, "-65541")]       // sign-extended from bit 15 into a wider field
    [InlineData(0xFFFFFFFEu, "-2")]           // a genuinely different margin (a stale one, or another tool's)
    public void AGpuReplyThatIsNotTheOffsetIsAMismatch(uint reply, string shown)
    {
        var smu = new FakeSmu { Arg0 = reply };
        smu.Responses.Enqueue(RspOk);
        var policy = Policy(smu);

        Assert.False(policy.SetDomains([0, 0, -5]));
        Assert.Equal($"the SMU accepted iGPU -5 but reads back {shown}", policy.LastError);
    }

    /// <summary>A missing or refused read-back is NOT a failed write: the write itself was acknowledged, and
    /// reporting an error would send the user chasing a problem that may only exist in the verification path.</summary>
    [Theory]
    [InlineData(RspBadPrereq)]
    [InlineData(RspFailed)]
    public void ARefusedGpuReadBackIsNotAFailedWrite(uint getterResponse)
    {
        var smu = new FakeSmu();
        // Everything acknowledges up to and including the SETTER — 16 cluster messages plus that send, two reads
        // each — and then the getter's two reads get the refusal.
        smu.Plan(RspOk, 2 * SlotsPerCcd * 2 + 2);
        smu.Plan(getterResponse, 1);
        smu.Arg0 = 0x0000FFFBu;                 // a value that would be a mismatch if it were trusted

        var policy = Policy(smu);
        Assert.True(policy.SetDomains([0, 0, -5]));
        Assert.Null(policy.LastError);
    }

    /// <summary>...and a read-back the transport could not READ is the same case: a verification that could not be
    /// performed must not turn an acknowledged write into a failed undervolt. (A WRITE that fails anywhere in the
    /// apply is a different matter — that is asserted with it, and it is a false return.)</summary>
    [Fact]
    public void AGpuReadBackTheTransportCannotReadIsNotAFailedWrite()
    {
        // Every read of the two clusters and of the setter succeeds; the getter's own reads then fail.
        var smu = new FakeSmu { FailReadsAfter = 2 * SlotsPerCcd * ReadsPerTransaction + ReadsPerTransaction };
        smu.Responses.Enqueue(RspOk);
        var policy = Policy(smu);

        Assert.True(policy.SetDomains([0, 0, -5]));
        Assert.Null(policy.LastError);
        // The getter WAS attempted — it read the graphics response register and could not get past that — and it is
        // the only reason its message was never written.
        Assert.Equal([0x1Fu], smu.WritesTo(RsmuMsg));
        Assert.Equal(3, smu.ReadAddresses().Count(a => a == RsmuRsp));

        // With the WRITE failing instead, the same apply is a false return carrying the transport's reason.
        var unwritable = new FakeSmu { FailWrites = true, IoError = "the SMU call failed at the PCI level (0xF6)" };
        unwritable.Responses.Enqueue(RspOk);
        var failing = Policy(unwritable);

        Assert.False(failing.SetDomains([0, 0, -5]));
        Assert.Equal("the SMU call failed at the PCI level (0xF6)", failing.LastError);
    }

    // ================= the availability gate =================

    /// <summary>The family/model gate: AMD family 0x1A and ONLY models 0x20/0x24 (Strix Point). Krackan Point (0x60)
    /// and Strix Halo (0x70) share the family case group in the reverse-engineering projects but their published
    /// results diverge, so claiming support would be guessing on someone else's hardware.</summary>
    [Theory]
    [InlineData(0x1A, 0x20, true)]
    [InlineData(0x1A, 0x24, true)]
    [InlineData(0x1A, 0x60, false)]     // Krackan Point
    [InlineData(0x1A, 0x70, false)]     // Strix Halo
    [InlineData(0x1A, 0x44, false)]     // another Zen 5 mobile part
    [InlineData(0x19, 0x20, false)]     // the previous family
    [InlineData(0x1A, 0x00, false)]
    public void TheFamilyAndModelGateAcceptsStrixPointOnly(int family, int model, bool expected)
        => Assert.Equal(expected, CurveOptimizerPolicy.IsKnownStrixPoint(family, model));

    /// <summary>The transport half of the gate needs all three facts: the driver must agree about the PART it is
    /// attached to (codename 22 = Strix Point), about the INTERFACE it is speaking (mp1_if_version 4 — the version
    /// the addresses were measured on), and the node the port drives must be writable. Probe, never require: any of
    /// the three missing returns null, so the port stays null and the section hides rather than offering writes that
    /// would refuse.</summary>
    [Theory]
    [InlineData(22, 4, true, true)]
    [InlineData(23, 4, true, false)]        // a different part that happens to share the family
    [InlineData(22, 3, true, false)]        // an interface version the register addresses were not measured on
    [InlineData(22, 4, false, false)]       // present but not writable: the installer's grant is missing
    [InlineData(null, 4, true, false)]      // no driver at all
    [InlineData(22, null, true, false)]
    [InlineData(null, null, false, false)]
    public void TheTransportGateNeedsThePartTheInterfaceAndThePermission(int? codename, int? version, bool writable, bool expected)
        => Assert.Equal(expected, CurveOptimizerPolicy.TransportReady(codename, version, writable));

    /// <summary>The gate's expected values are the machine's own: the driver loaded on the AN18-61 reports codename
    /// 22 and mp1_if_version 4 (read here, see docs/curve-optimizer-linux.md). Pinned so that a port that "just
    /// works" cannot be reading some other part's answer — this is the pair the gate compares against, and nothing
    /// else in the tree spells them.</summary>
    [Fact]
    public void TheGatesExpectedIdentityIsTheOneMeasuredOnThisMachine()
    {
        Assert.Equal(22, CurveOptimizerPolicy.StrixPointCodename);
        Assert.Equal(4, CurveOptimizerPolicy.Mp1IfVersion);
    }

    /// <summary>The CPU identity read, on whatever host runs the suite: a machine that is not an AMD Zen 5 mobile
    /// part must not claim the port, and the arithmetic that decides it is CPUID's own (family = extended + base,
    /// model = extended shifted in beside the base). Asserted as an implication rather than an equality, since the
    /// suite also runs on machines that are not this one.</summary>
    [Fact]
    public void TheCpuIdentityIsReadWithoutClaimingAnUnsupportedHost()
    {
        var (family, model) = CurveOptimizerPolicy.CpuFamilyAndModel();
        if (family is null || model is null)
        {
            Assert.False(CurveOptimizerPolicy.IsKnownStrixPoint());
            return;
        }

        Assert.Equal(CurveOptimizerPolicy.IsKnownStrixPoint(family.Value, model.Value), CurveOptimizerPolicy.IsKnownStrixPoint());
    }

    // ================= the interlock =================

    /// <summary>An interlock someone else holds ABANDONS the write with their reason, and no register is touched: the
    /// register window is shared with other tuning tools, and a message executed against arguments another tool is
    /// still publishing is worse than no undervolt.</summary>
    [Fact]
    public void ARefusedInterlockAbandonsTheTransactionBeforeAnyRegisterWrite()
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);
        var interlock = new FakeInterlock { Refusal = "another tool is holding the PCI access lock" };
        var policy = Policy(smu, interlock: interlock);

        Assert.False(policy.Set(-10));
        Assert.Equal("another tool is holding the PCI access lock", policy.LastError);
        Assert.Empty(smu.Trace);
        Assert.Equal(1, interlock.Taken);
    }

    /// <summary>...and an interlock that could not be CREATED (no privilege to create the name, or an ACL) does not
    /// abandon the write: the transaction proceeds unlocked, deliberately — better a possible collision with another
    /// tuning tool than no Curve Optimizer at all.</summary>
    [Fact]
    public void AnInterlockThatCannotBeCreatedLetsTheWriteProceedUnlocked()
    {
        var smu = new FakeSmu();
        smu.Responses.Enqueue(RspOk);
        var interlock = new FakeInterlock { Granted = false };     // no refusal, no handle
        var policy = Policy(smu, interlock: interlock);

        Assert.True(policy.Set(-10));
        Assert.NotEmpty(smu.Trace);
        Assert.Equal(1, interlock.Taken);
    }

    /// <summary>The interlock is taken once per transaction and released even when the transaction fails — a lock
    /// leaked on the failure path would hold a machine-wide name for the process's lifetime, and on Windows the same
    /// shape leaks the mutex handle. It is taken per transaction rather than held for the process lifetime, so the
    /// app never keeps that lock while idle.</summary>
    [Fact]
    public void TheInterlockIsReleasedWhateverTheOutcome()
    {
        var ok = new FakeSmu();
        ok.Responses.Enqueue(RspOk);
        var held = new FakeInterlock();
        Assert.True(Policy(ok, interlock: held).Set(-10));
        Assert.True(held.Released);

        var refused = new FakeSmu();
        refused.Responses.Enqueue(RspOk);
        refused.Responses.Enqueue(RspFailed);
        var failing = new FakeInterlock();
        Assert.False(Policy(refused, interlock: failing).Set(-10));
        Assert.True(failing.Released);

        var dead = new FakeSmu { FailWrites = true };
        dead.Responses.Enqueue(RspOk);
        var unwritable = new FakeInterlock();
        Assert.False(Policy(dead, interlock: unwritable).Set(-10));
        Assert.True(unwritable.Released);

        // Per transaction, not per port: an apply of three rails takes (and releases) it once per message.
        var twice = new FakeSmu();
        twice.Responses.Enqueue(RspOk);
        var counted = new FakeInterlock();
        Assert.True(Policy(twice, interlock: counted).SetDomains([-1, -2, 0]));
        Assert.Equal(2 + 2 * SlotsPerCcd, counted.Taken);          // 2 CPU clusters x 8 slots + set + getter
    }
}
