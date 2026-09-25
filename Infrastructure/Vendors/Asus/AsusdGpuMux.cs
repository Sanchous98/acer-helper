using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// The ASUS MUX switch, behind the shared Domain IGpuMux port. It is a THIN adapter over the Phase-3
// AsusdArmouryPort: every safety property the armoury port already has applies here unchanged —
//   * the value is validated against the LIVE device-reported range/possible-values on every request (refuse
//     over guessing);
//   * the write is single-flight and never called from a refresh loop;
//   * the attribute is the `Gpu` kind, so asusd QUEUES it and applies it at shutdown (the next restart) and
//     this app NEVER calls apply_queued_gpu_value.
// The only ASUS-specific knowledge here is the value mapping: asusd's GpuMode::to_mux_attr/from_mux pair says
// gpu_mux_mode 0 is Ultimate (discrete) and 1 is Optimus (hybrid).
//
// RUN TIME UNVERIFIED on ASUS hardware (no machine available): the interface, the mapping and the wire form are
// read from the asusd source and pinned by tests; no request has been made on real hardware.

/// <summary>The ASUS <c>gpu_mux_mode</c> attribute as the shared MUX port.</summary>
internal sealed class AsusdGpuMux : IGpuMux
{
    internal const string AttributeName = "gpu_mux_mode";

    private static readonly ChoiceOption Discrete = new("0", GpuMuxMessages.DiscreteMode);
    private static readonly ChoiceOption Optimus  = new("1", GpuMuxMessages.HybridMode);

    private readonly AsusdArmouryPort _armoury;

    internal AsusdGpuMux(AsusdArmouryPort armoury) => _armoury = armoury;

    public string? LastError => null;

    /// <summary>Supported only when the machine actually exposes the attribute AND asusd reports it usable. A
    /// machine that offers it without a reported range is still "supported" here but every Request refuses at
    /// the armoury port's validation — which is the honest "I cannot safely write that" answer.</summary>
    public bool Supported => Attribute is { Kind: AsusAttributeKind.Gpu, Available: true };

    public IReadOnlyList<ChoiceOption> Modes => [Optimus, Discrete];

    private AsusAttributeDescriptor? Attribute => _armoury.Find(AttributeName);

    public GpuMuxState Read()
    {
        if (Attribute is not { } attr || !Supported) return new(null, null, false);

        var current = _armoury.ReadCurrentValue(attr) is { } c ? ModeFor(c) : null;
        var pending = _armoury.QueuedGpuValue(attr) is { } q ? ModeFor(q) : null;
        // A queued value equal to the live current one is normalized away (no pending, no restart): asusd would
        // apply it at shutdown as a no-op. An unknown current keeps the queued value.
        return GpuMuxState.From(current, pending);
    }

    public GpuMuxChange Request(string modeId)
    {
        if (Attribute is not { } attr || !Supported)
            return new(false, false, GpuMuxMessages.Unsupported);
        if (!int.TryParse(modeId, out var value) || value is not (0 or 1))
            return new(false, false, GpuMuxMessages.UnknownMode);

        // NO-OP when the request equals the LIVE current value: asusd's queue is not touched, so requesting the
        // current mode never fabricates a "restart required" claim. An unreadable current value is UNKNOWN, so
        // the request proceeds through the armoury port's validate-then-queue path — equality is never guessed.
        var current = _armoury.ReadCurrentValue(attr) is { } c ? ModeFor(c) : null;
        if (current is not null && current.Id == modeId)
            return new(true, false, null, NoOp: true);

        var outcome = _armoury.Write(attr, value);
        return new(outcome.Ok, outcome.Queued, outcome.Message);
    }

    /// <summary>asusd's own value mapping (rog-platform GpuMode::from_mux): 0 is Ultimate / discrete-only,
    /// anything else is Optimus / hybrid. A value the enum does not name returns null so the UI says "unknown"
    /// rather than showing a wrong mode.</summary>
    private static ChoiceOption? ModeFor(int value) => value switch
    {
        0 => Discrete,
        1 => Optimus,
        _ => null,
    };
}
