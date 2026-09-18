namespace AcerHelper.Domain;

/// <summary>
/// This machine's battery, and — per property — what this machine can actually do with it. Presence is
/// declared BY THE PROPERTY: a firmware with no charge limiter is a battery whose <see cref="ChargeLimit"/> is
/// null, while the same object on a machine that has one carries a <see cref="BatteryToggle"/> there. The
/// battery's own shape is therefore the answer to "what does this battery have", rather than four separate
/// nullable slots whose absence each had to be interpreted by the caller.
///
/// COMPOSED INCREMENTALLY, which is why the members are settable rather than constructor arguments: the base
/// device has only telemetry to offer (<c>GenericDevice</c>), and every vendor then adds the properties its
/// own probe found — a WMI capability bitmask (Acer/Windows), a Linuwu-Sense node (Acer/Linux), a BIOS
/// attribute (Dell/Windows), parsed <c>charge_types</c> (Dell/Linux). The ops-as-delegates shape is the one
/// <c>DelegatePorts.cs</c> already uses for the same reason: the transport and its encoding are the only thing
/// that differs by vendor, so a property is a read op and a write op rather than a class per vendor.
///
/// ASSIGNING null IS MEANINGFUL, not tidying: Dell/Linux drops the generic end-threshold toggle when it finds
/// the firmware's own charge-mode set, and that is stated here as removing the property.
/// </summary>
public sealed class Battery
{
    /// <summary>Live telemetry — charge, state, health, cycles. Null on a machine whose OS reports no battery
    /// at all (a desktop), which is the question the UI asks before showing any readings; every member can be
    /// null on such a machine, and the object then says the machine has nothing battery-shaped rather than the
    /// caller having to ask four slots.</summary>
    public Func<BatteryInfoSnapshot>? Telemetry { get; internal set; }

    /// <summary>The ~80% charge limit (battery-health mode). Null when the firmware exposes no such control —
    /// which is a different fact from "it is switched off".</summary>
    public BatteryToggle? ChargeLimit { get; internal set; }

    /// <summary>Calibration (a full charge/discharge cycle). Null when the firmware exposes no such control.</summary>
    public BatteryToggle? Calibration { get; internal set; }

    /// <summary>The firmware's own charging strategy as a named mode (Dell: Adaptive/Express/Primarily AC/
    /// Standard/Custom). Null when this machine advertises no such set.</summary>
    public BatteryChoice? ChargeMode { get; internal set; }

    /// <summary>Live telemetry, or the "unknown" snapshot every field of which is -1 — the same answer the
    /// service gave while the telemetry port could be absent (it defaulted to exactly this record). A machine
    /// with no battery therefore still reads, it just has nothing to report.</summary>
    public BatteryInfoSnapshot Read() => Telemetry?.Invoke() ?? new BatteryInfoSnapshot();
}

/// <summary>An on/off property of a battery, with the ops this machine reads and writes it through. A null one
/// of these on <see cref="Battery"/> is how "this machine does not have it" is expressed, so the UI offers the
/// row only when the property exists rather than offering a switch that can only lie.
///
/// The write answers BOTH halves of its outcome — whether it took, and when it did not the transport's own
/// reason — which is the shape <c>DelegatePorts</c>' holders take for the same reason: a returned reason
/// belongs to THIS write, whereas a <c>LastError</c> field read afterwards can hold another call's failure
/// (docs/open-decisions.md §2).</summary>
public sealed record BatteryToggle(Func<bool> Read, Func<bool, (bool ok, string? error)> Write);

/// <summary>A pick-one-of-N property of a battery, carrying the firmware's own labelled set. Ids are the
/// vendor's stable keys, as in <see cref="IChoicePort"/>; a null one of these on <see cref="Battery"/> means
/// this machine advertises no such set.</summary>
public sealed record BatteryChoice(IReadOnlyList<ChoiceOption> Options,
                                  Func<string?> Read,
                                  Func<string, (bool ok, string? error)> Write);
