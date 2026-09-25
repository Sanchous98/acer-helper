using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Acer;

// The Acer MUX switch, behind the SAME shared Domain IGpuMux port as the ASUS/asusd and ASUS/ATK paths
// (docs/gpu-mux.md): queued/next-boot semantics, explicit confirmation, refusal over guessing, single-flight,
// and a state that shows the current AND the requested mode.
//
// THE WIRE — recovered by reverse-engineering AcerQAAgent.exe on an AN18-61 (high confidence), over the SAME
// `AcerGamingFunction` WMI class (root\WMI, GUID {7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56}) this project already
// uses for profiles, fans and sensors. There is no new method: everything rides Get/SetGamingMiscSetting, whose
// selector lives in the LOW byte of `gmInput` and whose values live above it — the encoding the profile path
// already uses at selector 0x0B.
//
//   * READ the current mode: `GetGamingMiscSetting(gmInput = 2)` -> `gmOutput = status | (mode << 8)`, so
//     `status = gmOutput & 0xFF` (0 = ok) and `mode = (gmOutput >> 8) & 0xFF`. Mode 1 = MS Hybrid (Optimus),
//     2 = Discrete Only, 3 = Auto Select/DDS, 255 = "not supported".
//   * WRITE a mode: `SetGamingMiscSetting(gmInput = (mode << 8) | 2)`; the reply's low byte is the status.
//   * CAPABILITY: `GetGamingMiscSetting(gmInput = 9)` -> status plus a raw value whose byte 1 is a
//     presence/bitmask signal (observed 7). It is NOT the mode enum and is never parsed as one; the rule is
//     "status 0 and the byte is neither 0 nor 255 => present", anything else is an unsupported refusal.
//
// This class REUSES the Windows backend's existing `GmGet`/`GmSet` helpers (AcerDevice.Windows.cs) rather than
// opening its own WMI path: the caller hands their reads/writes in as two delegates, which is also the seam the
// tests drive without WMI or a machine. The class itself is OS-agnostic and holds no transport.
//
// QUEUED, NOT IMMEDIATE. The write records the mode; the PANEL does not re-route until the next boot, which is
// why the shared port reports the value as Pending and the UI says "restart required" — this backend NEVER
// claims the routing already moved. The pending is reported ONLY while it differs from the mode the firmware
// reports; a re-read that already reports the request normalizes it away (no restart claim), so the card never
// says "after reboot: X" when the machine already reads X. The port tracks the requested value for the running
// session; a fresh process reads the real mode from the firmware, so the "pending" claim never outlives the
// reboot it describes.
//
// UNVERIFIED ON HARDWARE (the honest caveat): the selector numbers and the packing come from the disassembly,
// not from a call made by this app. In particular the READ is believed from the binary and the CAPABILITY byte
// is a presence signal, while the actual SetGamingMiscSetting(selector 2) EFFECT on a real panel is untested —
// no request has been made on a real machine. Re-verify before trusting a write (docs/gpu-mux.md §4, §6).

/// <summary>The Acer gaming-WMI MUX encoding: pure selectors, the read decode, the write packing and the mode
/// validation. No I/O — the transport is the caller's delegates — so every rule is reachable from a test.</summary>
internal static class AcerGpuMuxProtocol
{
    /// <summary>`gmInput` selector 2: read the current GPU mode.</summary>
    internal const ulong ReadModeSelector = 2;

    /// <summary>`gmInput` selector 9: read the MUX capability/presence signal.</summary>
    internal const ulong CapabilitySelector = 9;

    /// <summary>The mode enum the recovered interface carries (values in `gmOutput` bits 8..15).</summary>
    internal const byte ModeHybrid = 1;      // MS Hybrid (Optimus)
    internal const byte ModeDiscrete = 2;    // Discrete Only
    internal const byte ModeAuto = 3;        // Auto Select / DDS
    internal const byte ModeNotSupported = 255;

    /// <summary>The gaming protocol's status byte: 0 = ok, anything else is an error (`gmOutput & 0xFF`).</summary>
    internal const byte OkStatus = 0;

    /// <summary>Split a `gmOutput` into (status, value): the low byte is the status, the next byte the value.</summary>
    internal static (byte Status, byte Value) Decode(ulong gmOutput)
        => ((byte)(gmOutput & 0xFF), (byte)((gmOutput >> 8) & 0xFF));

    /// <summary>Whether selector 9 reports a switchable MUX: a clean status and a value that is neither 0 nor
    /// 255. The value is a presence/bitmask signal (observed 7), never the mode enum, so nothing here reads a
    /// mode out of it. A failed WMI call is all-ones (status 0xFF), which is not present.</summary>
    internal static bool CapabilityPresent(ulong gmOutput)
    {
        var (status, value) = Decode(gmOutput);
        return status == OkStatus && value != 0 && value != ModeNotSupported;
    }

    /// <summary>Pack the write: the mode above the selector — `(mode << 8) | 2`.</summary>
    internal static ulong PackSet(byte mode) => ((ulong)mode << 8) | ReadModeSelector;

    /// <summary>The wire modes this backend will write, by their shared id. Anything else — 0, 255, a foreign
    /// string — is refused rather than guessed.</summary>
    internal static bool TryMode(string modeId, out byte mode)
    {
        switch (modeId)
        {
            case "1": mode = ModeHybrid;   return true;
            case "2": mode = ModeDiscrete; return true;
            case "3": mode = ModeAuto;     return true;
            default:  mode = 0;            return false;
        }
    }
}

/// <summary>The Acer MUX: probe selector 9, read the current mode at selector 2, and queue a change at
/// `(mode << 8) | 2`. Every operation goes through the two delegates the Windows backend wires to its existing
/// <c>GmGet</c>/<c>GmSet</c> helpers; with no switchable interface (non-Windows, no WMI, capability absent) the
/// shared port is the <see cref="Unsupported"/> refusal.</summary>
internal sealed class AcerGpuMux : IGpuMux
{
    private static readonly ChoiceOption Hybrid   = new("1", GpuMuxMessages.HybridMode);
    private static readonly ChoiceOption Discrete = new("2", GpuMuxMessages.DiscreteMode);
    private static readonly ChoiceOption Auto     = new("3", GpuMuxMessages.AutoMode);

    /// <summary>The machine's port when no switchable interface is answerable — non-Windows, WMI unavailable, or
    /// selector 9 reporting no capability. One instance: it holds nothing and always answers the same way.</summary>
    internal static readonly AcerGpuMux Unsupported = new();

    private readonly Func<ulong, ulong>? _read;
    private readonly Func<ulong, (bool ok, string? error)>? _write;
    private readonly bool _supported;
    private readonly object _gate = new();
    private ChoiceOption? _pending;

    private AcerGpuMux() => _supported = false;

    private AcerGpuMux(Func<ulong, ulong> read, Func<ulong, (bool ok, string? error)> write, bool supported)
    {
        _read = read;
        _write = write;
        _supported = supported;
    }

    /// <summary>Build the port from the gaming-WMI delegates, probing the capability first. It is returned EVEN
    /// WHEN UNSUPPORTED, so the shared card can state the refusal rather than the capability silently vanishing
    /// — the same shape the ASUS Windows path uses.</summary>
    internal static AcerGpuMux Create(Func<ulong, ulong> read, Func<ulong, (bool ok, string? error)> write)
        => new(read, write, AcerGpuMuxProtocol.CapabilityPresent(read(AcerGpuMuxProtocol.CapabilitySelector)));

    public string? LastError => null;

    public bool Supported => _supported;

    public IReadOnlyList<ChoiceOption> Modes => _supported ? [Hybrid, Discrete, Auto] : [];

    public GpuMuxState Read()
    {
        if (!_supported) return new(null, null, false);

        var current = CurrentMode();

        // Like the ASUS Windows path, the firmware value is the REQUEST and the routing only follows on the next
        // boot, so a request made in this session is what "pending / restart required" means. It is reported
        // ONLY while it differs from the mode the firmware reports now: once the firmware reads back the
        // requested value it is normalized away (no pending, no restart), so the card can never claim an
        // after-reboot change that already reads as current. An unreadable current mode keeps the pending —
        // equality is never guessed against unknown. A fresh process has no pending and reports the real mode.
        return GpuMuxState.From(current, _pending);
    }

    public GpuMuxChange Request(string modeId)
    {
        if (!_supported) return new(false, false, GpuMuxMessages.Unsupported);
        if (!AcerGpuMuxProtocol.TryMode(modeId, out var mode))
            return new(false, false, GpuMuxMessages.UnknownMode);

        lock (_gate)   // single-flight: two requests can never overlap on the WMI write
        {
            // NO-OP when the request equals the mode the firmware reports NOW: nothing is written and no pending
            // is recorded, so requesting the current mode never fabricates a "restart required" claim. When the
            // current mode cannot be read it is UNKNOWN, so the request proceeds as a real change — equality is
            // never guessed.
            if (CurrentMode() is { } current && current.Id == modeId)
                return new(true, false, null, NoOp: true);

            var (ok, error) = _write!(AcerGpuMuxProtocol.PackSet(mode));
            if (!ok)
                return new(false, false, error ?? "the machine did not answer the GPU-mode write");

            _pending = ModeFor(mode);
            return new(true, true, null);   // queued: the panel re-routes on the next boot
        }
    }

    /// <summary>Read the firmware's current mode at selector 2, or null when the read fails or reports a value
    /// outside the enum. Never guessed; shared by <see cref="Read"/> and the <see cref="Request"/> no-op test.</summary>
    private ChoiceOption? CurrentMode()
    {
        var (status, mode) = AcerGpuMuxProtocol.Decode(_read!(AcerGpuMuxProtocol.ReadModeSelector));
        return status == AcerGpuMuxProtocol.OkStatus ? ModeFor(mode) : null;
    }

    private static ChoiceOption? ModeFor(byte value) => value switch
    {
        AcerGpuMuxProtocol.ModeHybrid   => Hybrid,
        AcerGpuMuxProtocol.ModeDiscrete => Discrete,
        AcerGpuMuxProtocol.ModeAuto     => Auto,
        _ => null,
    };
}
