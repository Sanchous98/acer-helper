using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// THE LINUX TRANSPORT FOR THE CPU UNDERVOLT, and nothing else: this file owns node paths, the byte-level I/O and
// the availability probe. Every number, every encoding and the whole mailbox sequence live in
// CurveOptimizerPolicy.cs (an UN-SUFFIXED file) because this one cannot be compiled by the test project — see that
// file's header. If a rule is worth proving, it does not belong here.

/// <summary>
/// AMD Curve Optimizer on Linux, over the <c>ryzen_smu</c> kernel driver's <c>smn</c> node.
///
/// WHY THE smn NODE AND NOT THE DRIVER'S COMMAND NODES, which are right there and speak MP1 and RSMU by name: those
/// nodes cannot tell the truth about a refusal. Measured on this machine, sending the graphics box's wrong opcode
/// (PSMU 0xB7) leaves the driver's <c>rsmu_cmd</c> reporting <c>0x01</c> — "OK" — while the mailbox's real answer,
/// read over smn, is <c>0xFD</c> ("prerequisites not met"). The driver's poll loop has an unreachable timeout branch,
/// so ANY non-zero answer reads as success there; a port built on those nodes would report an undervolt the SMU
/// refused, which breaks this port's own contract ("true means the SMU accepted the message"). The raw SMN window is
/// where the response register can be read for what it is, and on Linux that window is reachable from userspace
/// through this node — the same access RyzenAdj and ZenStates-Core make, and the reason no Windows-style kernel
/// driver of our own is needed on this side (docs/pawnio.md is the Windows half of that story).
///
/// WHAT A TRANSACTION LOOKS LIKE ON THE WIRE, byte for byte (measured against the driver's own smn_store/smn_show,
/// drv.c): a write of EXACTLY four bytes is an SMN READ — the payload is the address, little-endian, and the node's
/// next read answers the value it got, four little-endian bytes — and a write of EXACTLY eight bytes is an SMN WRITE,
/// address then value, after which the node's next read answers <c>0x01</c> (the SMU call got through) or <c>0xF6</c>
/// (the PCI access failed). Any other length does nothing at all and still reports the count as if it had worked, so
/// the two payloads are built by named functions in the policy file rather than assembled here.
///
/// THE STALE-VALUE TRAP, which is a property of the node rather than of this code: a read of the node returns the
/// driver's LAST result, and a FAILED SMN access leaves that previous value in place, so a stale read is
/// indistinguishable from a fresh one. The port therefore only reads where a stale value cannot be mistaken for an
/// answer: the poll loop's readings (a leftover only means "still in flight" or a repeated "busy") and argument 0,
/// which is read only after the response register has said <c>0x01</c> — and a read that fails there is reported as
/// the failure it is instead of handing back a stale number. The residual hazard is stated rather than papered over:
/// because an SMN WRITE leaves <c>0x01</c> in the node, a SMN read that fails while the response register is being
/// polled can hand back a cached <c>0x01</c>, i.e. report a refusal as accepted. Nothing on this side can detect
/// that — the node exposes no status for the read itself — and it is why the port's contract says a true result means
/// the message was accepted and not that the curve moved.
///
/// AVAILABILITY IS PROBED, NEVER REQUIRED, and the probe is cheap (two small reads and one permission test), because
/// composition runs on the UI thread. It needs all three of: a CPU whose mailbox layout is known (CPUID, family
/// 0x1A, models 0x20/0x24), a driver that agrees about the part it is attached to (<c>codename</c> 22 = Strix Point,
/// <c>mp1_if_version</c> 4, the interface version the addresses were measured on), and write access to the ONE node
/// this port drives. Anything missing returns null, the port stays null and the Tuning drawer hides the section —
/// which is what the app does on every machine that cannot use this.
///
/// THE PRIVILEGE PATH is the app's existing one, not a new mechanism: the driver's sysfs tree is a bare kobject with
/// no uevent of its own, so udev's MODE/GROUP cannot reach it and only the PCI bind event can (see
/// packaging/60-acer-helper.rules), while the installer's single pkexec script chmods the same four nodes directly
/// for the case the rule cannot cover — the driver is ALREADY bound when the user grants access
/// (Infrastructure/HardwareAccess.cs). Four nodes are granted because the driver's writable surface is one thing;
/// this port needs <c>smn</c> alone and gates on that alone, so a machine where one of the driver's command nodes is
/// missing still gets its undervolt instead of hiding it.
///
/// A REAL OFFSET HAS NEVER BEEN WRITTEN ON THIS MACHINE THROUGH THIS PATH. The transport and the sequence were
/// validated by reading, and a margin-0 (stock) write is inert by construction — see docs/curve-optimizer-linux.md,
/// which is where the write path's own validation (the owner's, from the finished UI) is recorded as outstanding.
/// See docs/curve-optimizer-strix-point.md for the mechanism, the measurements and the dead ends.
/// </summary>
internal sealed class RyzenCurveOptimizer : ICurveOptimizer, IDisposable
{
    /// <summary>The driver's sysfs tree. A BARE kobject: it is not a device and emits no uevent, which is why the
    /// packaging rule hooks the PCI device's bind event instead of matching this directory.</summary>
    internal const string DriverDir = "/sys/kernel/ryzen_smu_drv";

    internal const string SmnNode = DriverDir + "/smn";
    internal const string CodenameNode = DriverDir + "/codename";
    internal const string Mp1IfVersionNode = DriverDir + "/mp1_if_version";

    private readonly CurveOptimizerPolicy _policy;
    private readonly string _smn;
    private string? _ioError;

    private RyzenCurveOptimizer(string smn, string name, int physicalCores)
    {
        _smn = smn;
        _policy = new CurveOptimizerPolicy(name, physicalCores, SmnRead, SmnWrite, () => _ioError);
    }

    /// <summary>Probe for a tunable CPU on this machine. Returns null — feature hidden — unless the CPU is one whose
    /// mailbox layout is known AND the driver is present, agrees about the part, and leaves the smn node writable.
    /// Never throws: a missing node, an unreadable one and a driver that is not loaded are all "no port here".</summary>
    public static RyzenCurveOptimizer? TryCreate()
    {
        try
        {
            if (!CurveOptimizerPolicy.IsKnownStrixPoint()) return null;
            if (!CurveOptimizerPolicy.TransportReady(ReadInt(CodenameNode), ReadInt(Mp1IfVersionNode), CanWrite(SmnNode)))
                return null;

            // The brand string and the core count come from CPUID rather than from /proc, so the section header and
            // the cluster split are the same arithmetic on both OSes (CurveOptimizerPolicy).
            return new RyzenCurveOptimizer(SmnNode, CurveOptimizerPolicy.ReadCpuName(), CurveOptimizerPolicy.PhysicalCores());
        }
        catch { return null; }
    }

    public string Name => _policy.Name;
    public (int Min, int Max) Range => _policy.Range;
    public double MillivoltsPerCount => _policy.MillivoltsPerCount;
    public IReadOnlyList<VoltageDomain> Domains => _policy.Domains;
    public string? LastError => _policy.LastError;

    /// <summary>Apply an all-core offset. See <see cref="ICurveOptimizer"/>: true means the SMU accepted the message,
    /// not that the curve provably moved.</summary>
    public bool Set(int counts) => _policy.Set(counts);

    /// <summary>Apply one offset per voltage domain (two clusters + the iGPU on this part).</summary>
    public bool SetDomains(IReadOnlyList<int> counts) => _policy.SetDomains(counts);

    /// <summary>NOTHING TO RELEASE, deliberately: the node is opened per operation and closed again, so the app holds
    /// no file descriptor on the SMU while idle — the same stance the Windows port takes on the PCI access mutex, and
    /// the reason this type is disposable at all is that the machine's port slots are (GenericDevice owns it).</summary>
    public void Dispose()
    {
        // Intentionally empty — see the remarks above.
    }

    // ---- the smn node's byte-level I/O ----
    // One operation, one open: the request is written, the file offset is reset, and the node's answer is read back
    // from the same descriptor. The seek before the read is load-bearing rather than tidy — the node's read side
    // answers from offset 0, and the write we just made left the offset at the end of the payload.

    /// <summary>An SMN read of one register: the four-byte read request, then the value. Null when either half failed,
    /// which — mind the trap in the class remarks — includes a FAILED access that handed back a stale value.</summary>
    private uint? SmnRead(uint address)
    {
        try
        {
            using var node = Open();
            node.Write(CurveOptimizerPolicy.SmnReadRequest(address));
            node.Flush();
            node.Seek(0, SeekOrigin.Begin);
            return ReadValue(node);
        }
        catch (Exception e) { Fail(e); return null; }
    }

    /// <summary>An SMN write of one register: the eight-byte request, then the node's verdict on the SMU call itself
    /// (0x01 got through, 0xF6 the PCI access failed). A verdict other than 0x01 is a failed write — the whole point
    /// of this transport is that its answer is the SMU's own.</summary>
    private bool SmnWrite(uint address, uint value)
    {
        try
        {
            using var node = Open();
            node.Write(CurveOptimizerPolicy.SmnWriteRequest(address, value));
            node.Flush();
            node.Seek(0, SeekOrigin.Begin);
            return CurveOptimizerPolicy.SmnAcknowledged(ReadValue(node));
        }
        catch (Exception e) { Fail(e); return false; }
    }

    /// <summary>The node, read-write: an SMN read is a WRITE of the address followed by a read of the value, so both
    /// directions are used on one descriptor. Unbuffered (a buffer of one byte is no buffer in FileStream), because a
    /// buffered four-byte payload would only reach the kernel at the flush — and it is the write() syscall, not the
    /// call to Write, that performs the SMN operation.</summary>
    private FileStream Open()
    {
        var node = new FileStream(_smn, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 1);
        node.Seek(0, SeekOrigin.Begin);
        return node;
    }

    /// <summary>The four little-endian bytes the node answered with. A SHORT read is an error rather than a value:
    /// the node always answers four bytes, so anything else means it did not answer at all.</summary>
    private static uint ReadValue(FileStream node)
    {
        Span<byte> reply = stackalloc byte[sizeof(uint)];
        node.ReadExactly(reply);
        return CurveOptimizerPolicy.SmnValue(reply);
    }

    private void Fail(Exception e) => _ioError = e.Message;

    // ---- the probe's own two reads ----

    /// <summary>One of the driver's read-only identity nodes, as an integer, or null when it is absent or does not
    /// parse (an older driver, a driver built for another part, a machine where the module is not loaded at all).</summary>
    private static int? ReadInt(string path)
    {
        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            return int.TryParse(text, out var value) ? value : null;
        }
        catch { return null; }
    }

    /// <summary>Whether this user can write the node, probed by OPENING it for write and writing nothing — the
    /// technique <c>Hwmon.CanWrite</c> established in this tree (sysfs attributes do not act until a write()
    /// happens, so the probe has no side effect). Kept here rather than borrowed from Hwmon, which is the hwmon class
    /// tree's reader: the SMU window is not an hwmon device, and a general-purpose permission probe living in the
    /// hwmon scanner is how the next unrelated subsystem ends up reaching into it too.</summary>
    private static bool CanWrite(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Write); return true; }
        catch { return false; }
    }
}
