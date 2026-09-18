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
/// TWO MEMBERS, because what this use case asks is two genuinely different things: REMEMBER the offsets (a graph
/// write, under the lock, persisted) and WRITE them (a driver call, outside it, reported). Splitting them is what
/// lets <see cref="ApplyGpuOffsets"/> state the order as a rule rather than as the order two statements happened
/// to be typed in — see there.
///
/// The pair is deliberately NOT the whole of this axis's surface: nothing here reads a stored offset, because
/// nothing that applies one needs to. The read path is the re-apply's (<see cref="ReapplyOutcome.GpuOc"/>) and
/// the UI's accessor, and both are separate questions with separate answers — an absent preset means stock on this
/// axis, and stock is a value to WRITE rather than an absence to preserve.</summary>
public interface IGpuOffsetsTarget
{
    /// <summary>Remember <paramref name="state"/> as the CURRENT mode's offsets, under the graph lock, and persist
    /// it.</summary>
    void Store(GpuAxisState state);

    /// <summary>Write the offsets to the driver and report the write: false with the port's own reason when it
    /// refused, false with NO reason when this machine has no GPU-overclock port at all or when the port threw —
    /// a throw leaves the port's <c>LastError</c> holding an EARLIER call's words, and reporting those is the
    /// failure docs/open-decisions.md §2 exists to remove.</summary>
    (bool ok, string? error) Apply(GpuAxisState state);
}

/// <summary>The GPU offsets as a use case: record the pair the user set for the current mode and write it now.
///
/// WHAT IT DECIDES, and it is one order rather than a sequence: THE OFFSETS ARE REMEMBERED BEFORE THEY ARE
/// WRITTEN. A refused write still leaves the user's setting in the file, so the mode they configured comes back
/// on the next boot (the driver zeroes the offsets anyway — the re-apply is what puts it back), and the app never
/// reports an overclock it has forgotten. The opposite order — write, then remember only what the driver took —
/// is the one this axis deliberately does not have, and it is pinned on the service by
/// <c>LaptopServicePresetTests.AWritWithNoPort_ReturnsFalse_ButStillPersistsThePreset</c>.
///
/// The report is the write's own, passed through untouched, because the caller shows it: no port and a refused
/// write are the same <c>false</c>, and only the refusal carries words.</summary>
public static class ApplyGpuOffsets
{
    public static (bool ok, string? error) Run(GpuAxisState state, IGpuOffsetsTarget target)
    {
        target.Store(state);
        return target.Apply(state);
    }
}
