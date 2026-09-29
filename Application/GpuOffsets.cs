using AcerHelper.Domain;

namespace AcerHelper.Application;

/// <summary>What applying the GPU clock offsets needs from whoever owns the driver and the per-mode graph — the
/// contract for one of the things the owner's model applies («применение OC»), declared here and implemented by
/// the layer that owns both (Infrastructure/Composition/LaptopService.Tuning.cs).
///
/// WHAT IT CARRIES. The offsets cross as <see cref="GpuAxisState"/> — the domain's pair of MHz offsets
/// (Domain/AxisState.cs), not the stored <c>GpuOcPreset</c>, because the use case is in Application and may not
/// name the container (<c>ArchitectureMapTests.ApplicationPointsAtDomainNotInfrastructure</c>). The current mode
/// key is NOT a parameter and is not a member either: it is the graph's own business, and every axis files its
/// preset under the mode the machine is in at the moment of the write.
///
/// ONE WRITE MEMBER PLUS THE CAPABILITY FLAG, because what this use case asks is genuinely different things:
/// REMEMBER the offsets (a graph write, under the lock, persisted), WRITE them (a driver call, outside it), and
/// WHETHER this machine has a driver at all. Splitting the write from the memory is what lets
/// <see cref="ApplyGpuOffsets"/> state the order as a rule rather than as the order two statements happened
/// to be typed in — see there. The flag is the ABSENT-PORT answer and is deliberately a value: a machine with no
/// driver is a capability fact (the app has always reported the write as failed and kept the preset), NOT a
/// refusal the driver gave, so it is never a <see cref="PortWriteFailedException"/>.
///
/// The pair is deliberately NOT the whole of this axis's surface: nothing here reads a stored offset, because
/// nothing that applies one needs to. The read path is the re-apply's (<see cref="ReapplyOutcome.GpuOc"/>) and
/// the UI's accessor, and both are separate questions with separate answers — an absent preset means stock on this
/// axis, and stock is a value to WRITE rather than an absence to preserve.</summary>
public interface IGpuOffsetsTarget
{
    /// <summary>Whether this machine has a GPU-overclock port at all. False is the capability fact the use case
    /// consults before writing, so an absent port stays a VALUE (the app has always kept the preset and reported
    /// the write as failed) and is never confused with a present driver's refusal.</summary>
    bool HasPort { get; }

    /// <summary>Remember <paramref name="state"/> as the CURRENT mode's offsets, under the graph lock, and persist
    /// it.</summary>
    void Store(GpuAxisState state);

    /// <summary>Write the offsets to the driver. A PRESENT driver that refuses or fails THROWS
    /// <see cref="PortWriteFailedException"/>, carrying the driver's own words when it has any; the old
    /// <c>(false, null)</c> for a throwing port is gone with the ambiguity docs/open-decisions.md §2 records, and
    /// the exception's reason is never an earlier call's. The absent-port case is <see cref="HasPort"/>'s, decided
    /// by the use case WITHOUT reaching here.</summary>
    void Apply(GpuAxisState state);
}

/// <summary>The GPU offsets as a use case: record the pair the user set for the current mode and write it now.
///
/// WHAT IT DECIDES, and it is one order plus one capability guard: THE OFFSETS ARE REMEMBERED BEFORE THEY ARE
/// WRITTEN. A refused write still leaves the user's setting in the file, so the mode they configured comes back
/// on the next boot (the driver zeroes the offsets anyway — the re-apply is what puts it back), and the app never
/// reports an overclock it has forgotten. The opposite order — write, then remember only what the driver took —
/// is the one this axis deliberately does not have, and it is pinned on the service by
/// <c>LaptopServicePresetTests.AWritWithNoPort_ReturnsFalse_ButStillPersistsThePreset</c>.
///
/// AN ABSENT PORT IS A CAPABILITY FACT AND RETURNED, NOT THROWN: <see cref="IGpuOffsetsTarget.HasPort"/> is asked
/// after the store, and false returns the same <c>false</c> the UI has always read for a machine with no driver —
/// which is why <see cref="Run"/> still returns a bool. A PRESENT driver's refusal is a
/// <see cref="PortWriteFailedException"/> and escapes to the caller, which catches it at its boundary (the UI) or
/// turns it into its non-verdict outcome (the boot re-apply): it is NOT caught here, so the store-before-write
/// order is untouched and only the store has happened when it throws.</summary>
public sealed class ApplyGpuOffsets(IGpuOffsetsTarget target)
{
    public bool Run(GpuAxisState state)
    {
        target.Store(state);
        if (!target.HasPort) return false;
        target.Apply(state);
        return true;
    }
}

/// <summary>What choosing the GPU POWER LEVEL needs from whoever owns the EC envelope port and the per-mode
/// graph — the contract for the manual power pick, implemented by
/// Infrastructure/Composition/LaptopService.Tuning.cs. It mirrors <see cref="IGpuOffsetsTarget"/> exactly
/// (remember-then-write, absent port as a value), because the two are the same shape of edit on the same mode's
/// preset; what differs is the port behind them.
///
/// THE VALUE CROSSES AS THE DOMAIN'S <see cref="GpuPowerLevel"/>?, not as an EC byte: the byte is an Acer wire
/// fact and its mapping lives with the controller (<c>AcerEcHidController.ModeForLevel</c>), which Application
/// may not name. A NULL is the deliberate "follow the performance profile" choice — no override — so the field
/// is nullable rather than a sentinel member, exactly as <see cref="GpuAxisState.Power"/> is.
///
/// <see cref="Apply"/> takes the nullable value rather than a non-null level so the target owns the one decision
/// that "no override" implies: it must NOT write an arbitrary row, and on this axis there is nothing to write — a
/// mode that follows its profile gets its envelope from the profile switch itself. Stating that at the target
/// keeps the use case free of the EC's semantics.</summary>
public interface IGpuPowerTarget
{
    /// <summary>Whether this machine has an EC power-envelope port at all. False is the absent-port capability
    /// fact, never confused with a present port that could not take the write.</summary>
    bool HasPort { get; }

    /// <summary>Remember <paramref name="level"/> (or "no override", null) as the CURRENT mode's power choice,
    /// under the graph lock, and persist it.</summary>
    void Store(GpuPowerLevel? level);

    /// <summary>Write an explicit <paramref name="level"/> to the EC envelope now. A PRESENT port that could not
    /// take the write THROWS <see cref="PortWriteFailedException"/> (the enqueue-only channel reports failure as
    /// a bool, which the target converts); the absent-port case is decided by <see cref="HasPort"/> WITHOUT
    /// reaching here. A null level is "follow the profile": there is nothing to write, so the target does
    /// nothing.</summary>
    void Apply(GpuPowerLevel? level);
}

/// <summary>Choosing the current mode's GPU power level as a use case: remember it, then apply it. The same
/// order and the same absent-port posture as <see cref="ApplyGpuOffsets"/>, and for the same reason — the choice
/// survives a failed or impossible write, so the mode the user configured comes back on the next boot.
///
/// WHAT IT DECIDES AND WHAT IT DELIBERATELY DOES NOT. The one rule here is the shared remember-before-write
/// order and the capability guard; "what a null level means at the port" is the target's (see
/// <see cref="IGpuPowerTarget.Apply"/>), because that is a statement about the EC channel rather than about the
/// edit. The use case returns whether the write was attempted-and-took; an absent port still stores and returns
/// false, the same value the UI has always read for a machine without the channel.</summary>
public sealed class ApplyGpuPower(IGpuPowerTarget target)
{
    public bool Run(GpuPowerLevel? level)
    {
        target.Store(level);
        if (!target.HasPort) return false;
        target.Apply(level);
        return true;
    }
}
