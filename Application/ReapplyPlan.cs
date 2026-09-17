using AcerHelper.Domain;

namespace AcerHelper.Application;

/// <summary>What asks for volatile state to be put back. These are the three moments
/// <c>docs/state-and-events.md</c> calls a re-apply rather than a read — a boot, a mode switch and a wake — and
/// each drives a different set of axes. A trigger is therefore a fact about the SCHEDULE, not about any one axis:
/// "which axes are volatile" is <see cref="ModeAxisTable.IsVolatile"/>, "what puts them back" is
/// <see cref="ReapplyPlan.Schedule"/>.
///
/// It lives beside the plan rather than with the axes because a set of actions is a use case: the axes and
/// their policies are the domain's facts, and which ones a given moment drives is this layer's plan of action.</summary>
public enum ReapplyTrigger
{
    /// <summary>The app has just started: the driver has zeroed the GPU offsets, the SMU holds stock and the OS
    /// power overlay is the firmware's. The EC has NOT forgotten the fan mode, so that axis is not in this
    /// trigger's schedule.</summary>
    Startup,

    /// <summary>The performance mode changed — a pick, the Turbo key, or a power-source restore. Every axis
    /// takes the new mode's preset, absent or not: that is what stops a mode inheriting the previous one's
    /// settings.</summary>
    ModeChange,

    /// <summary>Wake from sleep or hibernation. The same volatile set as a boot (the dGPU power-cycles and comes
    /// back at zero offset) plus the lighting, which the EC drops over suspend.</summary>
    Resume,
}

/// <summary>What a re-apply DOES at each moment: which axes a trigger drives, in what order, on which thread, and
/// whether that moment's caller reflects the result. This is the USE CASE half of the operation, and the reason
/// it is here rather than beside the code that executes it.
///
/// WHY THE SPLIT, and it was forced rather than chosen. Executing the plan means calling the service that owns
/// the hardware and the settings graph, and collecting what the calling site's UI pass must show — and that
/// result is built out of the PERSISTED CONTAINER's types (<c>FanPreset</c>, <c>GpuOcPreset</c>), which belong to
/// Infrastructure. Application may not name them (`ArchitectureMapTests.
/// ApplicationPointsAtDomainNotInfrastructure`), so the executor cannot live here; what CAN live here is
/// everything the plan decides, because it is stated in Domain vocabulary alone (<see cref="ModeAxis"/>,
/// <see cref="ModeAxisTable"/>). The executor is <c>Infrastructure/Composition/HardwareReconciler.cs</c>, which
/// reads this plan and drives the ports.
///
/// WHY IT IS NOT A CONTRACT AGAINST THE SERVICE. Wrapping the five axis operations in an Application-declared
/// interface would not have worked: two of them return the container's types, so Application would have had to
/// re-declare the stored preset's own field layout to receive them — that is, put back into Application exactly
/// the shape the container was moved out for, and change the UI's view-model signatures to match. The
/// measurement is recorded in the commit that did this; the plan is what remained.</summary>
internal static class ReapplyPlan
{
    /// <summary>The axes a trigger drives, in the order it drives them — including the ones a layer above owns,
    /// because WHICH layer drives an axis is <see cref="ModeAxisTable.Owner"/> and stating it twice is how the two
    /// facts drift apart. The executing layer skips what is not its own.
    ///
    /// THE LISTS ARE BUILT FROM THE TABLE, never written out a third time: <see cref="ModeAxisTable.All"/> is the
    /// order a mode switch applies the axes in, and <see cref="ModeAxisTable.IsVolatile"/> is the set a boot or a
    /// wake must re-assert. A hand-written copy of the volatility set is the disease the table exists to cure, and
    /// it had already been written twice (the prose blocks the table replaced, and the sites' own axis lists).
    ///
    /// On a wake the lighting axis comes FIRST and on a mode switch LAST: the wake path repaints before it
    /// re-asserts the hardware, and the mode-switch path repaints after it.</summary>
    internal static IReadOnlyList<ModeAxis> Schedule(ReapplyTrigger trigger) => trigger switch
    {
        ReapplyTrigger.Startup => [.. ModeAxisTable.All.Where(ModeAxisTable.IsVolatile)],
        ReapplyTrigger.ModeChange => ModeAxisTable.All,
        ReapplyTrigger.Resume => [ModeAxis.Lights, .. ModeAxisTable.All.Where(ModeAxisTable.IsVolatile)],
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "a new ReapplyTrigger needs a schedule here"),
    };

    /// <summary>Whether this trigger's site must have the axis driven somewhere other than the thread it fires
    /// on. Each answer is a measured fact about that site, and stated here once instead of at each call site:
    ///
    /// <list type="bullet">
    /// <item><b>Startup and mode change</b> — only the Curve Optimizer, whose SMU mailbox transaction waits on
    /// the machine-wide PCI access lock that HWiNFO/Ryzen Master/RyzenAdj also take, so it can block for
    /// seconds. The rest of those two schedules is a fast NvAPI call and a powrprof transaction, and they stay
    /// on the calling thread. That is load-bearing beyond latency: neither site has a catch around an inline
    /// axis, so a port that throws there must still reach it.</item>
    /// <item><b>Resume</b> — the whole hardware schedule, because the trigger fires on the UI thread and the EC
    /// can stall right after wake. One task for all of them, so the three keep their relative order.</item>
    /// </list></summary>
    internal static bool RunsOffTheCallersThread(ReapplyTrigger trigger, ModeAxis axis) => trigger switch
    {
        ReapplyTrigger.Startup or ReapplyTrigger.ModeChange => axis == ModeAxis.Co,
        ReapplyTrigger.Resume => true,
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "a new ReapplyTrigger needs a dispatch decision here"),
    };

    /// <summary>Whether this trigger's caller hands the outcome to a UI pass. Only the mode-change pass does;
    /// startup and resume discard it. The distinction is not cosmetic — filling the Curve Optimizer's row means
    /// reading its stored domains, and that read takes the current mode key under <c>_state</c>, which is an EC
    /// transaction. Doing it for a caller that throws the answer away would be a new hardware read on the UI
    /// thread at every boot, not a free no-op.</summary>
    internal static bool Reflects(ReapplyTrigger trigger) => trigger == ReapplyTrigger.ModeChange;
}
