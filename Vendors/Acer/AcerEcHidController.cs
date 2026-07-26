using AcerHelper.Features;

namespace AcerHelper.Vendors.Acer;

// Cross-platform Acer EC HID controller: the channel that actually carries the performance envelope on recent
// Nitro/Predator models (device VID 0x1025 / PID 0x174B, vendor collection on usage page 0xFF05, 65-byte
// feature reports). The packets are identical on every OS, so this file is the codec and the per-OS partials
// supply the transport hooks — AcerEcHidController.Windows.cs uses HidSharp, .Linux.cs talks to hidraw (this
// controller hangs off HID-over-I2C, which HidSharp's Linux enumeration never lists — same story as
// EneHidController).
//
// WHY THIS EXISTS. On the Nitro AN18-61 the gaming-WMI profile byte (SetGamingMiscSetting index 0x0B) turned
// out to be only an *indicator*: writing it moves the tray state and the lightbar palette but does not touch
// the power envelope. Measured live — NitroSense switching Quiet<->Turbo moved the dGPU's enforced limit
// 71 W <-> 108 W while EVERY gaming-WMI value stayed frozen (index 0x0B stuck on Eco the whole time, and
// GetGamingProfile constant). So the envelope — GPU TGP/CTGP plus the CPU limits — lives in the EC's own
// "system usage mode", reachable only over this HID interface. Without it the dGPU sits at the bare vBIOS
// default (70 W base + whatever Dynamic Boost grants, ~78 W sustained) no matter which profile the app shows.
//
// WIRE FORMAT: A0 00 A0 <featureId:LE16> <cmdId> <params…>, zero-padded to 65. A GetFeature of report 0xA0
// answers with byte[2] = 0xE0 when the EC accepted the FRAME — but that is frame-level only: an out-of-range
// mode is acknowledged the same way and then silently ignored, so there is nothing worth verifying against
// and this controller is write-only. (The EC also exposes read commands — GetOCProfileTable at featureId
// 0x0002 / cmdId 0x02 dumps the four per-profile power rows — but nothing needs them at runtime.)
//
// MODE BYTE -> steady dGPU limit, measured on AN18-61 under sustained load, each mode entered from a
// re-confirmed mode 0 (a descending sweep lies: a mode that is a no-op just leaves the previous level in
// place, which is exactly how byte 5 first looked valid):
//     0 = 108 W (TGP 100 + boost)   1 = 93 W (TGP 85)   2 = 79 W (TGP 70 + boost)   3 = 71 W   4 = 71 W
//     5 and above = acknowledged, then ignored.
// Five valid values, matching the EC's own reported "system usage mode capability: 5". Modes 3 and 4 are the
// same GPU row and differ only in the CPU envelope, so Quiet/Eco map to them in that order.
//
// The EC LATCHES the mode: it survives this app exiting and needs no resident daemon (verified with all nine
// Acer services stopped — the limit stayed at 100 W+ with zero Acer processes alive). It does NOT necessarily
// survive a reboot, which is why LaptopService re-asserts the profile at startup. Acer's own AcerQAAgent, when
// it is running, re-applies its own mode within a minute or two and will fight these writes — that is an
// argument for removing the Acer stack, not for polling here.
//
// Writes go through a background writer thread, never the caller's (UI) thread: WriteFeature is a synchronous
// no-timeout HID write on the same HID-over-I2C bus as the RGB controller, and a contended bus (external USB-C
// display, worst at boot) can block it for a long time. Only the newest mode matters, so the queue is a single
// coalescing slot rather than EneHidController's per-region list.
internal sealed partial class AcerEcHidController : IDisposable
{
    private const int VID = 0x1025, PID = 0x174B, FeatureLen = 65;

    // Frame bytes 0 and 2 are both 0xA0 (report id + command marker); byte 1 is reserved. Then the 16-bit
    // little-endian feature id, the command id, and the parameters. FeatureUsageMode is 0x0001, so its high
    // byte (report[4]) stays 0.
    private const byte Frame = 0xA0, FeatureUsageMode = 0x01, CmdSet = 0x01;

    /// <summary>True when the EC HID interface was found — i.e. this model routes its performance envelope
    /// through the EC. False on models without it, where the caller keeps its previous behaviour.</summary>
    public bool Available { get; }

    /// <summary>Last write error, or null. Written by the writer thread and read by the UI; a reference
    /// assignment is atomic and this is only ever surfaced as a diagnostic string, so it needs no lock.</summary>
    public string? LastError { get; private set; }

    public AcerEcHidController()
    {
        Available = OpenTransport();
        if (!Available) return;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "acer-ec-hid-writer" };
        _worker.Start();
    }

    /// <summary>The EC usage-mode byte for a profile class, or null when the class has no EC equivalent
    /// (<see cref="ProfileKind.Other"/> — an unrecognised vendor profile, where inventing a power envelope
    /// would be worse than leaving the EC alone).</summary>
    public static byte? ModeFor(ProfileKind kind) => kind switch
    {
        ProfileKind.Turbo       => 0,
        ProfileKind.Performance => 1,
        ProfileKind.Balanced    => 2,
        ProfileKind.Quiet       => 3,
        ProfileKind.Eco         => 4,
        _                       => null,
    };

    /// <summary>Queue the EC usage mode matching a profile class. Fire-and-forget: it only enqueues (the write
    /// happens on the writer thread), so the result means "accepted for sending", not "the EC applied it".
    /// False when there is nothing to send — no device, or a profile class with no EC mapping.</summary>
    public bool Apply(ProfileKind kind)
    {
        if (!Available || ModeFor(kind) is not { } mode) return false;
        lock (_gate)
        {
            if (_stopping) return false;
            _pending = mode;        // single slot: a burst of switches collapses to the last one
            Monitor.Pulse(_gate);
        }
        return true;
    }

    // ---- single-slot coalescing writer ----
    // A plain object, NOT System.Threading.Lock: the worker parks on Monitor.Wait/Pulse, which Lock does not
    // support (it would silently fall back to monitor-based locking on a converted reference — CS9216).
    private readonly object _gate = new();
    private byte? _pending;
    private readonly Thread? _worker;
    private bool _stopping;

    private void WorkerLoop()
    {
        while (true)
        {
            byte mode;
            lock (_gate)
            {
                while (_pending == null && !_stopping) Monitor.Wait(_gate);
                if (_pending == null) return;   // stopping and drained
                mode = _pending.Value;
                _pending = null;
            }

            var report = new byte[FeatureLen];
            report[0] = Frame;
            report[2] = Frame;
            report[3] = FeatureUsageMode;
            report[5] = CmdSet;
            report[6] = mode;

            // WriteFeature drops the transport handle on failure so the next write re-opens it — a handle
            // opened in a bad state at boot-with-display would otherwise stay broken until restart.
            try { LastError = WriteFeature(report) ? null : "Acer EC HID write failed"; }
            catch (Exception e) { LastError = e.Message; }   // keep the worker alive
        }
    }

    public void Dispose()
    {
        Thread? t;
        lock (_gate) { _stopping = true; _pending = null; Monitor.Pulse(_gate); t = _worker; }
        // Bounded join: a worker stuck inside a blocked write is a background thread, so it can't keep the
        // process alive — proceed and let CloseTransport unstick it (disposing the handle faults the write).
        t?.Join(TimeSpan.FromSeconds(1));
        CloseTransport();
    }

    // ---- transport, per-OS (found device? / send one feature report / release) ----
    private partial bool OpenTransport();
    private partial bool WriteFeature(byte[] report);
    private partial void CloseTransport();
}
