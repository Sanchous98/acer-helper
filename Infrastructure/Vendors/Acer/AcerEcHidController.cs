using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Vendors.Acer;

// Cross-platform Acer EC HID controller: the channel that actually carries the performance envelope on recent
// Nitro/Predator models (device VID 0x1025 / PID 0x174B, vendor collection on usage page 0xFF05, 65-byte
// feature reports). The packets are identical on every OS, so this file is the codec and the per-OS partials
// supply the transport hooks.
//
// WHY THIS EXISTS. On the Nitro AN18-61 the gaming-WMI profile byte (SetGamingMiscSetting index 0x0B) is only
// an *indicator*: writing it moves the tray state and the lightbar palette but does not touch the power
// envelope. Measured live — NitroSense switching Quiet<->Turbo moved the dGPU's enforced limit 75 W <-> 115 W
// while EVERY gaming-WMI value stayed frozen. So the envelope (GPU TGP/CTGP plus the CPU limits) lives in the
// EC's own "system usage mode", reachable only over this HID interface.
//
// WIRE FORMAT: A0 00 A0 <featureId:LE16> <cmdId> <params…>, zero-padded to 65. A reply takes TWO calls, in
// this order: SEND the request frame, then read the answer back. A get with no preceding send delivers nothing
// to the device and hands back whatever frame the handle last held — measured. The one read in this class
// (ReadPowerSource, below) keeps that order.
//
// The reply carries byte[2] = 0xE0 when the EC accepted the FRAME — but that is frame-level only: an
// out-of-range mode is acknowledged the same way and then silently ignored, so the MODE stays write-only.
//
// ONE read exists: ReadPowerSource (feature 0x0000, cmd 0x03, reply byte 7 = source TYPE, the GetACStatus field
// measured in docs/power-an18-61.md). It follows the SEND-then-GET order above, guards on the 0xE0 ACK, and
// returns null on anything short of a clean read so the caller can hide the row rather than invent a value. It
// is a synchronous HID-over-I2C transaction and runs on the caller's slow pool schedule (AcerPowerSourceSchedule,
// single-flight), NEVER on the writer thread and NEVER at the battery poll's 1 Hz — the battery gauge is an OS
// syscall, this is a bus transaction, and the two must not share a cadence (Infrastructure/BatteryPollSchedule).
//
// The EC LATCHES the mode: it survives this app exiting and needs no resident daemon. It does NOT necessarily
// survive a reboot, which is why LaptopService re-asserts the profile at startup (EC-only — a full profile
// switch there re-flashes the lightbar and cascades at every boot).
//
// Writes go through a background writer thread, never the caller's (UI) thread: WriteFeature is a synchronous
// no-timeout HID write on the same HID-over-I2C bus as the RGB controller, and a contended bus can block it
// for a long time. Only the newest mode matters, so the queue is a single coalescing slot.
//
// Mode byte -> measured dGPU limit (0 = 115 W, 1 = 100 W, 2 = 85 W, 3 = 75 W, 4 = 75 W, 5+ acknowledged then
// ignored), the wire format, the measurement methodology and every dead end: see docs/power-an18-61.md.
internal sealed partial class AcerEcHidController : IDisposable
{
    /// <summary>The Acer HID device both transports look for: this controller finds it for the power envelope's
    /// feature reports, and <c>AcerHotkeys.Linux.cs</c> opens the SAME node to read the Turbo key's input reports
    /// (<c>AcerEcHidController.IsAcerNode</c> is the shared test). Internal rather than private so the two ends
    /// cannot disagree about which device is ours.</summary>
    internal const int VID = 0x1025, PID = 0x174B;

    private const int FeatureLen = 65;

    // Frame bytes 0 and 2 are both 0xA0 (report id + command marker); byte 1 is reserved. Then the 16-bit
    // little-endian feature id, the command id, and the parameters. FeatureUsageMode is 0x0001, so its high
    // byte (report[4]) stays 0.
    private const byte Frame = 0xA0, FeatureUsageMode = 0x01, CmdSet = 0x01;

    // The power-source read: Acer's own GetACStatus is feature 0x0000, command 0x03, and the source TYPE is
    // reply byte 7 (measured by differential read, see docs/power-an18-61.md §"Power source …"). The reply is
    // trusted only when byte 2 is the accepted marker 0xE0, exactly as a write's ACK is. This is the ONE place
    // the encoding is spelled; the domain enum it maps to is vendor-agnostic.
    internal const ushort FeaturePowerStatus = 0x0000;
    internal const byte CmdPowerStatus = 0x03;
    private const byte ReplyAccepted = 0xE0;
    private const int PowerSourceByte = 7;

    /// <summary>True when the EC HID interface was found — i.e. this model routes its performance envelope
    /// through the EC. False on models without it, where the caller keeps its previous behaviour.</summary>
    public bool Available { get; }

    public AcerEcHidController()
    {
        Available = OpenTransport();
        if (!Available) return;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "acer-ec-hid-writer" };
        _worker.Start();
    }

    /// <summary>Build one 65-byte request frame for a feature group: the report id and command marker, the
    /// feature id as a little-endian uint16, the command id, and the first parameter byte (byte 6 — the usage
    /// mode). THE ONE PLACE THE ENVELOPE IS SPELLED, so the usage-mode write below cannot drift from it.</summary>
    internal static byte[] FrameFor(ushort feature, byte cmd, byte param6)
    {
        var report = new byte[FeatureLen];
        report[0] = Frame;
        report[2] = Frame;
        report[3] = (byte)feature;
        report[4] = (byte)(feature >> 8);
        report[5] = cmd;
        report[6] = param6;
        return report;
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

    /// <summary>The EC usage-mode byte for a GPU power LEVEL — the SAME wire byte <see cref="ModeFor"/> produces
    /// for the profile class of the same name, which is stated here rather than left to coincidence: the two
    /// tables are separate surfaces (a profile class vs a chosen envelope row) and a reorder of either must not
    /// silently move the other. Level <see cref="GpuPowerLevel.Turbo"/> is byte 0 (115 W), Quiet is byte 3
    /// (75 W), etc. — the ordering counts up from Turbo, which is why it agrees with <see cref="ModeFor"/> only
    /// because both were named after the same EC rows.
    ///
    /// No null arm and no byte-4 arm: every <see cref="GpuPowerLevel"/> is one of the EC rows this app offers, so
    /// this mapping is total where <see cref="ModeFor"/> is not — a level cannot be "unrecognised" the way a
    /// foreign vendor profile can. The EC's fifth row (Eco, byte 4) is deliberately absent from the level enum
    /// because it enforces the same envelope as Quiet (see <see cref="GpuPowerLevel"/>), so no level maps to byte
    /// 4 here — a cast of 4 reaches the refusal below.</summary>
    public static byte ModeForLevel(GpuPowerLevel level) => level switch
    {
        GpuPowerLevel.Turbo       => 0,
        GpuPowerLevel.Performance => 1,
        GpuPowerLevel.Balanced    => 2,
        GpuPowerLevel.Quiet       => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level,
                 "a new GpuPowerLevel needs an EC usage-mode byte here"),
    };

    /// <summary>The EC's fixed power rows as this app offers them, most power first, each with the steady
    /// dGPU limit MEASURED on the AN18-61 (docs/power-an18-61.md §"Mode byte → measured dGPU power"). These are
    /// the only selectable levels — there is no arbitrary wattage — so this is DATA, not a computation. The label
    /// keys are the app's own profile strings; a machine that speaks a different row vocabulary would expose its
    /// own list through <see cref="IGpuPowerEnvelope.Levels"/>.
    ///
    /// FOUR ROWS, NOT FIVE: the EC also has an Eco usage row (byte 4), but it enforces the SAME envelope as Quiet
    /// (~77 W measured) and differs only in the CPU side, so it is not offered as a GPU power level — see
    /// <see cref="GpuPowerLevel"/>. The wattages are the fresh measured ceiling per row (115/100/85/75), which the
    /// driver's <c>enforced.power.limit</c> reports under load; the EC's own table declares TGP+CTGP+DynBoost, but
    /// CTGP is not applied by the driver (the real limit is TGP+DynBoost, clamped to the 115 W VBIOS ceiling).</summary>
    public static readonly IReadOnlyList<GpuPowerOption> PowerLevels =
    [
        new(GpuPowerLevel.Turbo,       "profile.turbo",       115),
        new(GpuPowerLevel.Performance, "profile.performance", 100),
        new(GpuPowerLevel.Balanced,    "profile.balanced",     85),
        new(GpuPowerLevel.Quiet,       "profile.quiet",        75),
    ];

    /// <summary>Queue one of the EC's power LEVELS — the manual GPU-power pick, decoupled from the profile. The
    /// byte goes through <see cref="ModeForLevel"/> so the level table cannot drift from the profile-class one,
    /// and a level outside the enum is refused before anything is enqueued (the EC ACKs an out-of-range row
    /// exactly like a good one and then ignores it, so sending one would look like success and change nothing).
    /// Fire-and-forget like <see cref="Apply(ProfileKind)"/>: true means "accepted for sending".</summary>
    public bool ApplyLevel(GpuPowerLevel level) => Queue(ModeForLevel(level));

    /// <summary>Queue the EC usage mode matching a profile class. Fire-and-forget: it only enqueues (the write
    /// happens on the writer thread), so the result means "accepted for sending", not "the EC applied it".
    /// False when there is nothing to send — no device, or a profile class with no EC mapping.</summary>
    public bool Apply(ProfileKind kind)
    {
        if (ModeFor(kind) is not { } mode) return false;
        return Queue(mode);
    }

    /// <summary>The single coalescing slot every envelope write goes through, whatever chose the mode (the
    /// profile class or a manual level). One place, so the two entry points cannot disagree about availability
    /// or about the wake-up.</summary>
    private bool Queue(byte mode)
    {
        if (!Available) return false;
        lock (_gate)
        {
            if (_stopping) return false;
            _pending = mode;        // single slot: a burst of switches collapses to the last one
            Monitor.Pulse(_gate);
        }
        return true;
    }

    /// <summary>Read the live power source over the EC HID channel. A read here is SEND-then-GET: the request
    /// frame goes out first (<c>FrameFor(0x0000, 0x03, 0x00)</c>) and only then is the reply fetched — a get
    /// with no preceding send hands back whatever frame the handle last held (measured; see the class header).
    /// Returns null when there is no device or on any transport failure, and when the reply is not the accepted
    /// marker (<c>reply[2] != 0xE0</c>) or too short to hold byte 7. Never a guessed value: null is "could not
    /// measure", which the caller maps to <see cref="PowerSource.Unknown"/> and the UI hides.
    ///
    /// NOT on the writer thread and NOT at 1 Hz: this is a synchronous HID-over-I2C transaction on the bus the
    /// RGB controller shares, so it can block. The caller runs it on a slow pool schedule (single-flight), never
    /// on the UI thread — see <c>AcerPowerSourceSchedule</c> and the cadence note in the class header.</summary>
    public PowerSource? ReadPowerSource()
    {
        if (!Available) return null;
        try
        {
            var send = FrameFor(FeaturePowerStatus, CmdPowerStatus, param6: 0x00);
            var reply = new byte[FeatureLen];
            reply[0] = Frame;   // the report id the GET must name; the rest is overwritten by the reply
            lock (_ioGate) { if (!ReadFeature(send, reply)) return null; }
            return DecodePowerSource(reply);
        }
        catch { return null; }
    }

    /// <summary>The reply decoder, split out pure and static so a test can pin it without opening the device
    /// (constructing this class probes the hardware — the same reason <see cref="ModeFor"/> is static). Returns
    /// null when the reply is too short to hold byte 7 or byte 2 is not the accepted marker <c>0xE0</c>; maps
    /// byte 7 to the source TYPE; an unmapped non-zero code is <see cref="PowerSource.Unknown"/> ("on a source,
    /// not one we can name"), never a guess.</summary>
    internal static PowerSource? DecodePowerSource(byte[] reply)
    {
        if (reply.Length <= PowerSourceByte || reply[2] != ReplyAccepted) return null;
        return reply[PowerSourceByte] switch
        {
            0x00 => PowerSource.Battery,
            0x01 => PowerSource.Barrel,
            0x04 => PowerSource.UsbC,
            _    => PowerSource.Unknown,
        };
    }

    // ---- single-slot coalescing writer ----
    // A plain object, NOT System.Threading.Lock: the worker parks on Monitor.Wait/Pulse, which Lock does not
    // support (it would silently fall back to monitor-based locking on a converted reference — CS9216).
    private readonly object _gate = new();
    private byte? _pending;
    private readonly Thread? _worker;
    private bool _stopping;

    // Serialises the TRANSPORT, not the request: a read runs on a caller's pool thread while the writer thread
    // may be sending a mode. Both touch the same lazily-opened handle and both drop it on failure, so an
    // unguarded read could close the stream under a write or vice-versa. This is a plain mutual-exclusion lock
    // around WriteFeature/ReadFeature only — never held while parked, so it cannot delay a mode write behind a
    // blocked read beyond the one bus transaction that read already owns.
    private readonly object _ioGate = new();

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

            var report = FrameFor(FeatureUsageMode, CmdSet, mode);

            // WriteFeature drops the transport handle on failure so the next write re-opens it — a handle
            // opened in a bad state at boot-with-display would otherwise stay broken until restart.
            //
            // The failure itself goes nowhere, and that is pre-existing, not new: this used to assign a
            // `LastError` property here, which nothing in the tree ever read (verified by git grep) and whose own
            // doc comment wrongly claimed "read by the UI". Deleting the dead field removes the false impression
            // that an EC write failure is surfaced — it is not. Apply() reports "accepted for sending", not "the
            // EC applied it", and its bool is about the queue, not the write. Recorded as an open gap in
            // docs/open-decisions.md §5 rather than papered over with a field nobody reads.
            try { lock (_ioGate) WriteFeature(report); }
            catch { /* keep the worker alive */ }
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

    // ---- transport, per-OS (found device? / send one feature report / send-then-get / release) ----
    private partial bool OpenTransport();
    private partial bool WriteFeature(byte[] report);

    /// <summary>SEND-then-GET one feature report: <paramref name="send"/> goes out, then the reply fills
    /// <paramref name="reply"/> (whose byte 0 names the report id). Called inside <c>_ioGate</c> only. False on
    /// any transport failure; the per-OS half drops the handle exactly as <see cref="WriteFeature"/> does.</summary>
    private partial bool ReadFeature(byte[] send, byte[] reply);

    private partial void CloseTransport();
}
