using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// The ASUS Windows GPU MUX switch, behind the SAME shared Domain IGpuMux port as the Linux/asusd path and the
// Acer refusal (docs/gpu-mux.md): queued/next-boot semantics, explicit confirmation, refusal over guessing,
// single-flight, and a state that shows the current AND the requested mode.
//
// THE WIRE (device IDs in AsusAtk.cs; source asus-wmi.h `ASUS_WMI_DEVID_GPU_MUX`): `gpu_mux_mode` is 0 = dGPU
// (discrete) and 1 = Optimus (hybrid) — the same value meaning the Linux path reads through asusd. The ROG
// device is 0x00090016 and the VivoBook/Zenbook one 0x00090026; the port probes one then the other.
//
// QUEUED, NOT IMMEDIATE. The write records the mode; the PANEL does not re-route until the next boot, which is
// why the shared port reports the value as Pending and the UI says "restart required" — but only while the
// requested value differs from what the firmware reports. A re-read that already reports the request normalizes
// the pending away (no restart claim), so the card never says "after reboot: X" when the machine already reads
// X. The port tracks the requested value for the running session; a fresh process reads the real mode from the
// firmware, so the "pending" claim never outlives the reboot it describes.
//
// UNVERIFIED ON HARDWARE (see AsusAtk.cs); no request has been made on a real machine.

/// <summary>The ASUS Windows MUX: probe, read current/requested, and queue a change.</summary>
internal sealed class AsusWindowsGpuMux : IGpuMux
{
    private static readonly ChoiceOption Discrete = new("0", GpuMuxMessages.DiscreteMode);
    private static readonly ChoiceOption Optimus  = new("1", GpuMuxMessages.HybridMode);

    private readonly AsusAtkDevice _atk;
    private readonly uint _device;
    private readonly bool _supported;
    private readonly object _gate = new();
    private ChoiceOption? _pending;

    private AsusWindowsGpuMux(AsusAtkDevice atk, uint device, bool supported)
    {
        _atk = atk;
        _device = device;
        _supported = supported;
    }

    /// <summary>Build the port, preferring the ROG device then the VivoBook one (or the reverse on a known Vivo
    /// model). It is returned EVEN WHEN UNSUPPORTED, so the shared card can state the refusal rather than the
    /// capability silently vanishing — the same shape the Acer path uses.</summary>
    internal static AsusWindowsGpuMux Create(AsusAtkDevice atk, bool vivo)
    {
        var first = vivo ? AsusAtk.GpuMuxVivo : AsusAtk.GpuMux;
        var second = vivo ? AsusAtk.GpuMux : AsusAtk.GpuMuxVivo;
        if (atk.Supported(first)) return new AsusWindowsGpuMux(atk, first, supported: true);
        return atk.Supported(second) ? new AsusWindowsGpuMux(atk, second, supported: true)
                                     : new AsusWindowsGpuMux(atk, first, supported: false);
    }

    public string? LastError => null;

    public bool Supported => _supported;

    public IReadOnlyList<ChoiceOption> Modes => _supported ? [Optimus, Discrete] : [];

    public GpuMuxState Read()
    {
        if (!_supported) return new(null, null, false);
        var value = _atk.Get(_device);
        var current = value >= 0 ? ModeFor(value) : null;

        // ON WINDOWS THE FIRMWARE VALUE IS THE REQUEST, NOT THE ROUTING: the ATK write stores the mode at once
        // (so a re-read already shows it), while the panel only re-routes on the next boot. A request made in
        // THIS session is what "pending / restart required" means here — but only while it differs from the
        // value the firmware reports. Once a re-read reports the requested value it is normalized away (no
        // pending, no restart): the card must never say "after reboot: X" when the machine already reads X.
        // A fresh process reads the real mode and reports no pending, which is the state after the reboot.
        return GpuMuxState.From(current, _pending);
    }

    public GpuMuxChange Request(string modeId)
    {
        if (!_supported) return new(false, false, GpuMuxMessages.Unsupported);
        if (!int.TryParse(modeId, out var value) || value is not (0 or 1))
            return new(false, false, GpuMuxMessages.UnknownMode);

        lock (_gate)
        {
            // NO-OP when the request equals the mode the firmware value reports NOW: do not write and do not
            // record a pending value, so a request for the current mode never fabricates a "restart required"
            // claim. An unreadable current value is UNKNOWN, so the request proceeds — equality is never guessed.
            var currentValue = _atk.Get(_device);
            var current = currentValue >= 0 ? ModeFor(currentValue) : null;
            if (current is not null && current.Id == modeId)
                return new(true, false, null, NoOp: true);

            var result = _atk.Set(_device, value);
            if (result != 1)
                return new(false, false, result < 0
                    ? "the machine did not answer the GPU-mode write"
                    : $"the firmware refused the GPU-mode write (status {result})");

            _pending = ModeFor(value);
            return new(true, true, null);   // queued: the panel re-routes on the next boot
        }
    }

    /// <summary>0 is dGPU (discrete), 1 is Optimus (hybrid) — asus-wmi's own meaning for gpu_mux_mode.</summary>
    private static ChoiceOption? ModeFor(int value) => value switch
    {
        0 => Discrete,
        1 => Optimus,
        _ => null,
    };
}
