using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// THE WINDOWS TRANSPORT FOR THE CPU UNDERVOLT, and nothing else: this file owns the PawnIO handle, the module blob
// and the cross-process PCI interlock. Every number, every encoding and the whole mailbox sequence live in
// CurveOptimizerPolicy.cs, shared with the Linux port — which is also why the encoders below are one-line forwards
// rather than the arithmetic itself: a second copy of "the CPU's -5 is 0xFFFFB" in a file that the test project
// cannot compile against is exactly the drift SmuOffsetEncodingTests exists to catch.

/// <summary>
/// AMD Curve Optimizer on Windows, over the PawnIO driver's SMU register accessors.
///
/// WHY A KERNEL DRIVER AT ALL HERE: the SMU mailbox is reached through the PCI config index/data pair on 00:00.0,
/// which no user-mode process may poke — on Windows the gateway is PawnIO, whose RyzenSMU module whitelists the
/// register window this port writes. Linux reaches the same window through an already-loaded kernel driver's smn
/// node, which is why the port's policy is shared and only the transport differs (docs/pawnio.md).
///
/// The module blob is EMBEDDED, which is what its author prescribes: modules are LGPL-2.1-or-later and the
/// integration guide says to take a release blob and "include its contents in your software". The blob is fetched
/// from upstream at build time (AcerHelper.csproj -> FetchRyzenSMU, build/fetch-ryzensmu.ps1) rather than committed,
/// verified against the release's published digest and against the two exported names below, and its resolved tag +
/// SHA-256 are recorded in the build log. Note the PawnIO installer ships no modules at all, so there is no shared
/// system location to read one from; an explicit override file is honoured for advanced use. We do NOT use the module's generic
/// send-SMU-command entry point: its internal table resolves Strix Point to the RSMU address triple, so a 0x4C sent
/// that way would go to the wrong mailbox. Hand-rolling the transaction over the raw register accessors is what
/// G-Helper does, and it is legal because the module's own range check admits the whole 0x3B10000-0x3B10FFF window
/// these three registers live in.
///
/// THE DRIVER IS GATED ON, and gating on it keeps the section honest: without the driver the sliders could appear and
/// then refuse every write. The check is the REGISTRY one, not a device open — it must stay cheap (composition runs
/// on the UI thread) and it must not depend on elevation. <see cref="PawnIoInstaller"/> is what offers to install it.
/// The handle is opened on first use instead, off that thread, because loading a PawnIO module makes the kernel
/// verify a signed bytecode blob — exactly the kind of blocking call this app keeps off the UI thread.
/// See docs/pawnio.md and docs/curve-optimizer-strix-point.md.
/// </summary>
internal sealed class RyzenCurveOptimizer : ICurveOptimizer, IDisposable
{
    // ---- the PawnIO module and the two functions we drive ----
    private const string ModuleFile = "RyzenSMU.bin";
    private const string FnReadReg  = "ioctl_read_smu_register";    // 1 arg in, 1 value out
    private const string FnWriteReg = "ioctl_write_smu_register";   // 2 args in, nothing out

    // Cross-process interlock. The mailbox is reached through the PCI config index/data pair on 00:00.0, which
    // HWiNFO, CPU-Z, Ryzen Master and RyzenAdj all poke as well; this is the name that ecosystem agreed on. It is
    // held across the WHOLE transaction, because per-access locking still lets another agent's message execute
    // against our arguments. See docs/curve-optimizer-strix-point.md.
    private const string PciMutexName = @"Global\Access_PCI";
    private const int MutexWaitMs = 5000;

    private const string DriverUnavailable = "the PawnIO driver is not available";

    private readonly byte[] _module;
    private readonly CurveOptimizerPolicy _policy;

    private PawnIo? _io;

    /// <summary>The transport's own last reason, read by the policy when a register access failed. Set inside the two
    /// delegates below, which run under the policy's lock.</summary>
    private string? _ioError;

    public string Name => _policy.Name;
    public (int Min, int Max) Range => _policy.Range;
    public double MillivoltsPerCount => _policy.MillivoltsPerCount;
    public IReadOnlyList<VoltageDomain> Domains => _policy.Domains;
    public string? LastError => _policy.LastError;

    private RyzenCurveOptimizer(byte[] module, string name, int physicalCores)
    {
        _module = module;
        _policy = new CurveOptimizerPolicy(name, physicalCores, Read, Write, () => _ioError, TakeInterlock);
    }

    /// <summary>Probe for a tunable CPU. Returns null — feature hidden — unless this is a CPU whose MP1 mailbox
    /// layout is known AND PawnIO is installed. Never throws. See docs/curve-optimizer-strix-point.md.</summary>
    public static RyzenCurveOptimizer? TryCreate()
    {
        try
        {
            if (!CurveOptimizerPolicy.IsKnownStrixPoint() || !PawnIoInstaller.Installed) return null;
            var module = LoadModule();
            return module == null ? null : new RyzenCurveOptimizer(module, CurveOptimizerPolicy.ReadCpuName(),
                                                                   CurveOptimizerPolicy.PhysicalCores());
        }
        catch { return null; }
    }

    /// <summary>Whether this CPU is one the undervolt supports, independent of whether the driver is installed — so
    /// the driver-setup offer knows if it is even relevant here. See docs/curve-optimizer-strix-point.md.</summary>
    public static bool SupportedCpu => CurveOptimizerPolicy.IsKnownStrixPoint();

    // ---- the encodings, kept on this type because this is where they have always lived ----
    // Thin forwards to the shared policy: SmuOffsetEncodingTests pins these three names and five shipped files cite
    // them, so the move into CurveOptimizerPolicy must not move the name a reader or a test reaches for.

    internal static uint Encode(int counts) => CurveOptimizerPolicy.Encode(counts);

    internal static uint CoreArg(int ccd, int slot, uint encodedMargin) => CurveOptimizerPolicy.CoreArg(ccd, slot, encodedMargin);

    internal static uint GpuMargin(int counts) => CurveOptimizerPolicy.GpuMargin(counts);

    /// <summary>Apply an all-core Curve Optimizer offset in AVFS counts (negative = undervolt, 0 = stock). Returns
    /// false and sets <see cref="LastError"/> when the SMU refuses. A true result means the mailbox accepted the
    /// message — NOT that the voltage curve measurably moved; see <see cref="CurveOptimizerPolicy"/>'s remarks.</summary>
    public bool Set(int counts) => _policy.Set(counts);

    /// <summary>Apply one offset per voltage domain (the two clusters and the integrated Radeon on this part).</summary>
    public bool SetDomains(IReadOnlyList<int> counts) => _policy.SetDomains(counts);

    /// <summary>Close the driver handle under the port's own transaction lock — a handle closed while a transaction is
    /// in flight is a use-after-free, and the lock that serialises transactions is the policy's.</summary>
    public void Dispose() => _policy.WithLock(() =>
    {
        _io?.Dispose();
        _io = null;
    });

    // ---- the two register delegates the policy drives ----

    // Opened on first use, never at composition time: loading a PawnIO module makes the kernel verify a signed
    // bytecode blob, which is exactly the kind of blocking call this app keeps off the UI thread (and composition
    // runs there). Cached once open; a failed open is retried on the next call, since the user may install the
    // driver while the app is running. Called from inside the delegates, i.e. under the policy's lock.
    private PawnIo? Io()
    {
        if (_io != null) return _io;
        try { return _io = PawnIo.TryLoad(_module); }
        catch { return null; }
    }

    private uint? Read(uint address)
    {
        var io = Io();
        if (io == null) { _ioError = DriverUnavailable; return null; }

        Span<ulong> outv = stackalloc ulong[1];
        if (!io.Execute(FnReadReg, [address], outv)) { _ioError = io.LastError; return null; }

        _ioError = null;
        return (uint)outv[0];
    }

    private bool Write(uint address, uint value)
    {
        var io = Io();
        if (io == null) { _ioError = DriverUnavailable; return false; }

        if (io.Execute(FnWriteReg, [address, value], [])) { _ioError = null; return true; }

        _ioError = io.LastError;
        return false;
    }

    // ---- the interlock ----

    // Opened per transaction rather than held for the process lifetime, so the app never keeps a machine-wide lock
    // while idle. Two behaviours worth knowing (see docs/curve-optimizer-strix-point.md): if the name cannot even be
    // created the transaction proceeds UNLOCKED — better a possible collision with another tuning tool than no Curve
    // Optimizer at all — and a wait that times out abandons the write with the reason, which is the recorded known
    // defect (the offset then silently does not apply; docs/open-decisions.md).
    private IDisposable? TakeInterlock(out string? error)
    {
        error = null;
        var pci = TryOpenPciMutex();
        if (pci == null) return null;                // unlocked, deliberately

        var held = false;
        try { held = pci.WaitOne(MutexWaitMs); }
        catch (AbandonedMutexException) { held = true; }   // previous owner died holding it; it is ours now

        if (held) return new PciLock(pci);

        pci.Dispose();
        error = "another tool is holding the PCI access lock";
        return null;
    }

    /// <summary>The held PCI mutex, released and disposed as one step. ReleaseMutex and Dispose are two different
    /// obligations — the first hands the lock over, the second releases the handle — and a disposable that did only
    /// one of them would either keep a machine-wide lock while the app sits idle or leak a handle per transaction.</summary>
    private sealed class PciLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            // Same thread throughout (WaitOne/ReleaseMutex are thread-affine and the whole transaction is synchronous
            // under the policy's lock), so releasing here is valid.
            try { mutex.ReleaseMutex(); } catch { /* best effort */ }
            mutex.Dispose();
        }
    }

    private static Mutex? TryOpenPciMutex()
    {
        try { return new Mutex(false, PciMutexName, out _); }
        catch { return null; }   // no SeCreateGlobalPrivilege / denied by an existing owner's ACL
    }

    // ---- module lookup ----

    // Embedded blob by default, with a user file allowed to override it — the same arrangement acer-models.json
    // uses. The override exists because module APIs are explicitly unstable across releases: if a future blob
    // changes a call shape, someone can pin their own without waiting for a build. It is deliberately an explicit
    // path in the app's own config folder rather than a scan of shared locations, so a stray file elsewhere can
    // never silently change which bytes get loaded into the kernel. See docs/pawnio.md.
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
}
