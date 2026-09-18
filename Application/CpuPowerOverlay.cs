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
/// TWO MEMBERS for the same reason <see cref="IGpuOffsetsTarget"/> has two: remembering is a graph write under the
/// lock, writing is an OS call outside it that reports itself, and what this use case has to say is the ORDER
/// between them plus what an absent port means.
///
/// An ABSENT preset on this axis is an absence to preserve, not a value: a mode the user never configured gets no
/// OS power mode forced onto it. That rule belongs to the re-apply, which reads the stored entry before it writes
/// anything (<c>LaptopService.ApplyModeCpuPower</c>) — this contract is the edit path, where there is always a
/// value to remember because the user has just picked one.</summary>
public interface ICpuPowerOverlayTarget
{
    /// <summary>Remember <paramref name="id"/> as the CURRENT mode's overlay, under the graph lock, and persist
    /// it.</summary>
    void Store(string id);

    /// <summary>Write it to the OS and report the write, on the same terms as
    /// <see cref="IGpuOffsetsTarget.Apply"/>: false with the port's own reason when it refused, false with no
    /// reason when this machine has no CPU-power port or the port threw.</summary>
    (bool ok, string? error) Apply(string id);
}

/// <summary>The CPU power overlay as a use case: record the overlay the user picked for the current mode and apply
/// it now.
///
/// WHAT IT DECIDES: the same order as <see cref="ApplyGpuOffsets"/> and for the same reason — REMEMBERED BEFORE
/// WRITTEN, so a refused write still leaves the choice in the file. On this axis the order has a second
/// consequence that is worth stating rather than leaving to be discovered: the stored entry is what the re-apply
/// reads on the next mode switch, so an overlay that was never remembered is an overlay this app will never put
/// back, while one that was remembered and refused is one it will try again at the next boot or mode change.</summary>
public static class ApplyCpuPowerOverlay
{
    public static (bool ok, string? error) Run(string id, ICpuPowerOverlayTarget target)
    {
        target.Store(id);
        return target.Apply(id);
    }
}
