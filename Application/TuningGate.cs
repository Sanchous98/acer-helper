namespace AcerHelper.Application;

/// <summary>The mutual-exclusion gate between the guided undervolt sweep and every other writer of the SAME SMU
/// (the manual Curve-Optimizer edits and the per-mode re-apply). It is a contract rather than a field on the
/// service because the writers are now use cases: a use case that owns its target through the constructor also
/// needs the gate through the constructor, or it is back to being a static helper the caller has to remember to
/// wrap.
///
/// WHAT IT EXISTS TO PREVENT. The sweep owns the SMU for its whole duration; a manual edit or a re-apply that
/// landed BETWEEN two of its probes would invalidate the probe. So the gate makes "read the busy flag, then
/// write" atomic for every writer — the sweep's own writes take it too (so a write already in flight when the
/// flag goes up finishes first, and the sweep's base snapshot is taken after it). It is held for ONE mailbox
/// transaction at a time, never for the whole sweep, so it can never stall the UI on a minutes-long run.
/// (The rule and its history are <c>Infrastructure/Composition/LaptopService.UndervoltSweep.cs</c>'s.)</summary>
public interface ITuningGate
{
    /// <summary>True while a guided sweep owns the SMU. The UI reads it to lock the sliders; a writer may read it
    /// if it wants to skip work, but <see cref="Guard"/> is the form that cannot be raced.</summary>
    bool SweepActive { get; }

    /// <summary>Run <paramref name="write"/> with the gate held, or refuse it with the busy reason when a sweep
    /// owns the SMU — the same refusal the manual edit has always returned. The delegate is invoked exactly once
    /// and only when the gate is free, so the check and the write it guards cannot be a write apart.</summary>
    (bool ok, string? error) Guard(Func<(bool ok, string? error)> write);
}
