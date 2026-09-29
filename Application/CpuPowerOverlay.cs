namespace AcerHelper.Application;

/// <summary>What applying the CPU power overlay needs from whoever owns the OS power API and the per-mode graph —
/// the contract for one of the things the owner's model applies, declared here and implemented by the layer that
/// owns both (Infrastructure/Composition/LaptopService.Tuning.cs).
///
/// WHAT IT CARRIES. The overlay id is a STRING and stays one: it is the OS's own GUID for a power mode, opaque to
/// the domain and to every layer above the port that hands it over, so there is no domain type to convert it into
/// and inventing one would be a second name for the same value. That is also why this axis's state crosses as a
/// bare <c>string</c> in <see cref="ReapplyOutcome.CpuPower"/>.
///
/// ONE WRITE MEMBER PLUS THE CAPABILITY FLAG, on the same terms as <see cref="IGpuOffsetsTarget"/>: remembering is
/// a graph write under the lock, writing is an OS call outside it, and the flag answers the absent-port question as
/// a VALUE — a machine with no CPU-power port is a capability fact, the same no-op the UI has always read, and the
/// use case must not reach <see cref="Apply"/> for it (never a <see cref="PortWriteFailedException"/>).
///
/// An ABSENT preset on this axis is an absence to preserve, not a value: a mode the user never configured gets no
/// OS power mode forced onto it. That rule belongs to the re-apply, which reads the stored entry before it writes
/// anything (<c>ICpuPowerModeTarget.ApplyCurrentMode</c>, <see cref="ApplyModeCpuPower"/>) — this contract is the
/// edit path, where there is always a value to remember because the user has just picked one.</summary>
public interface ICpuPowerOverlayTarget
{
    /// <summary>Whether this machine has a CPU-power port at all. False is the capability fact the use case
    /// consults before writing, so an absent port stays a VALUE (the app has always kept the choice and reported
    /// the write as failed).</summary>
    bool HasPort { get; }

    /// <summary>Remember <paramref name="id"/> as the CURRENT mode's overlay, under the graph lock, and persist
    /// it.</summary>
    void Store(string id);

    /// <summary>Write it to the OS. A PRESENT port that refuses or fails THROWS
    /// <see cref="PortWriteFailedException"/> carrying its own words when it has any, on the same terms as
    /// <see cref="IGpuOffsetsTarget.Apply"/>; the absent-port case is <see cref="HasPort"/>'s, decided by the use
    /// case without reaching here.</summary>
    void Apply(string id);
}

/// <summary>The CPU power overlay as a use case: record the overlay the user picked for the current mode and apply
/// it now.
///
/// WHAT IT DECIDES: the same order as <see cref="ApplyGpuOffsets"/> and for the same reason — REMEMBERED BEFORE
/// WRITTEN, so a refused write still leaves the choice in the file. On this axis the order has a second
/// consequence that is worth stating rather than leaving to be discovered: the stored entry is what the re-apply
/// reads on the next mode switch, so an overlay that was never remembered is an overlay this app will never put
/// back, while one that was remembered and refused is one it will try again at the next boot or mode change.
///
/// AN ABSENT PORT IS A CAPABILITY FACT AND RETURNED, NOT THROWN: <see cref="ICpuPowerOverlayTarget.HasPort"/> is
/// asked after the store, and false returns the same <c>false</c> the UI has always read for a machine with no
/// CPU-power port — which is why <see cref="Run"/> still returns a bool. A PRESENT port's refusal is a
/// <see cref="PortWriteFailedException"/> and escapes to the caller, caught at its boundary (the UI) or turned
/// into the caller's non-verdict outcome; it is NOT caught here, so only the store has happened when it throws.</summary>
public sealed class ApplyCpuPowerOverlay(ICpuPowerOverlayTarget target)
{
    public bool Run(string id)
    {
        target.Store(id);
        if (!target.HasPort) return false;
        target.Apply(id);
        return true;
    }
}
