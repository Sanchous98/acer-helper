namespace AcerHelper.Application;

/// <summary>What applying the Curve-Optimizer offsets needs from whoever owns the SMU and the per-mode graph — the
/// contract for one of the things the owner's model applies («применение даунвольта»), declared here and
/// implemented by the layer that owns both (Infrastructure/Composition/LaptopService.Tuning.cs).
///
/// WHAT IT CARRIES, and why it is an array rather than a domain type. The offsets cross the way the SMU takes
/// them: index-aligned with the port's voltage domains, or a single entry on a CPU that exposes no domain control.
/// That IS the domain vocabulary here — the alignment is the domain rule (Infrastructure/Vendors/Generic/CoAxis.cs's <c>Rows</c> builds
/// the UI's rows from the same alignment) — and the stored form is not it: the container holds a per-rail
/// DICTIONARY keyed by the rail's hardware identity, precisely so a preset survives a reorder or a relabel of the
/// hardware's rails. The list is the caller's, and it is not copied.
///
/// TWO MEMBERS, AND THE FIRST ONE ANSWERS. Remembering is a graph write under the lock and writing is an SMU
/// transaction outside it, on the same terms as the other two per-mode edit contracts — but on THIS axis the
/// remember can also be REFUSED, and the offsets that were taken are not the offsets that were handed over, so
/// <see cref="Store"/> returns the remembered values where its siblings return void. A count of offsets this CPU
/// cannot take is not a value to remember: today the refusal comes before anything is persisted
/// (<c>LaptopService.SetCoDomains</c>'s domain-count guard), and a use case that could not tell "remembered" from
/// "refused" would either persist a preset the hardware can never be given or have to ask the question again.
/// HANDING THE VALUES BACK ALSO KEEPS THE CLAMP IN ONE PLACE: what the graph holds is what the write sends, and
/// the use case never sees an unbounded value it could pass on by mistake.
///
/// THE ABSENT PORT IS THE FIRST MEMBER'S NULL, not an exception. <see cref="Store"/> returns null when this CPU
/// has no Curve-Optimizer port (or takes a different number of offsets), which is the capability fact the use case
/// consults: it then never reaches <see cref="Apply"/>, and the <c>(false, null)</c> the UI has always read for a
/// portless machine is preserved. Only a PRESENT port's refusal is a <see cref="PortWriteFailedException"/>.
///
/// WHAT IS NOT HERE, MEASURED RATHER THAN ASSUMED. The two rules that make this axis interesting stay inside the
/// implementation, and the reason is a layer decision rather than a size one: WHICH of the two writes this CPU
/// takes (one offset per rail, or one for all cores) and WHERE the offsets are clamped are both the domain's, and
/// the type that holds them (<c>CoAxis</c>, with <c>OffsetCounts</c> under it) is Infrastructure by the owner's
/// ruling — the third of the three walls listed in docs/open-decisions.md's note on the retired analysis, and the
/// only one still standing. This contract therefore cannot answer either question, and the use case does not
/// pretend to: it states the empty edit and the order, and nothing about the rails. A wave that moves
/// <c>CoAxis</c> would be the one that could move the rest.</summary>
public interface IUndervoltTarget
{
    /// <summary>Remember the offsets as the CURRENT mode's, under the graph lock, and persist them — brought
    /// inside this CPU's own bounds, because a stored value the hardware would never accept is an undervolt the
    /// app only claims to have applied. Returns WHAT WAS REMEMBERED, index-aligned with the port's domains, so the
    /// write can send exactly the values the graph holds; <c>null</c> means NOTHING WAS REMEMBERED and the caller
    /// must not write — this CPU has no Curve-Optimizer port, or it takes a different number of offsets than were
    /// handed over. This null is the ABSENT-PORT capability answer (plus the count mismatch), a VALUE and never
    /// <see cref="PortWriteFailedException"/>.</summary>
    IReadOnlyList<int>? Store(IReadOnlyList<int> counts);

    /// <summary>Write the offsets to the SMU. A PRESENT SMU that refuses or fails THROWS
    /// <see cref="PortWriteFailedException"/> carrying the transport's own words when it has any; the old
    /// <c>(false, null)</c> for a port that threw is gone with the ambiguity docs/open-decisions.md §2 records.
    /// This is reached only when <see cref="Store"/> returned values, so an absent port never arrives here. The
    /// SMU transaction waits on a machine-wide PCI lock other tuning tools also take and can block for seconds, so
    /// the caller runs this off the thread that owns the UI.</summary>
    void Apply(IReadOnlyList<int> counts);
}

/// <summary>The Curve-Optimizer offsets as a use case: record the counts the user set for the current mode and
/// write them now.
///
/// WHAT IT DECIDES, and each half is load-bearing:
/// <list type="number">
/// <item><b>An empty edit is a refusal, not a write.</b> It is the <c>false</c> with no reason the UI has always
/// received, nothing is remembered and nothing reaches the SMU. It is stated here rather than left to the
/// implementation because it is a rule about the EDIT and not about this CPU: the per-rail path accepts an empty
/// list (it is how a machine with no rails is disarmed), while the edit path refuses one — and the two must not
/// be confused, because they are reached by different callers.</item>
/// <item><b>Remembered before written</b>, on the same terms as the other two per-mode edits: a refused write
/// still leaves the user's setting in the file.</item>
/// <item><b>A refused remember stops the write, and what is written is what was remembered.</b> If the axis
/// cannot take this many offsets there is nothing to write and the same <c>(false, null)</c> comes back — the
/// counts are not clamped-and-sent anyway, because a value the axis would not remember is not one it will be
/// asked to put back. And the write is given the values <see cref="IUndervoltTarget.Store"/> handed back rather
/// than the caller's, so the two cannot be a clamp apart.</item>
/// <item><b>The whole edit runs under the tuning gate</b> (<see cref="ITuningGate"/>): a manual edit must never
/// land between two of a running sweep's probes, and the gate is what makes "is a sweep running" and the write
/// atomic. This is the reason the operation owns the gate through its constructor rather than leaving the caller
/// to remember a lock — the four call sites that used to hold it by hand are gone.</item>
/// </list>
///
/// WHAT THE RETURN STILL CARRIES, now that the write is exceptional: the pair's non-ok path is ONLY the gate's
/// busy precondition — "a guided sweep is running" — and the three value refusals above (empty edit, absent port,
/// count mismatch). A PRESENT SMU's refusal is a <see cref="PortWriteFailedException"/> and escapes the gate and
/// the use case to the caller, which catches it at its boundary (the UI) or turns it into its non-verdict outcome
/// (the boot re-apply); it is deliberately NOT caught here, so only the remember has happened when it throws.
///
/// This is the thinnest of the family and it is worth naming why: the two rules that decide what this axis
/// actually writes are the rails fork and the clamp, and both are <c>CoAxis</c>'s, in Infrastructure
/// (<see cref="IUndervoltTarget"/>'s docstring measures that). What is left on this side of the wall is the edit's
/// own vocabulary — empty, refused, remembered, written — and that is what is stated here.</summary>
public sealed class ApplyUndervolt(IUndervoltTarget target, ITuningGate gate)
{
    public (bool ok, string? error) Run(IReadOnlyList<int> counts)
    {
        if (counts.Count == 0)
        {
            return (false, null);
        }

        // The gate's busy refusal stays a VALUE (its own precondition, not a port refusal); a present port's
        // refusal THROWS out of target.Apply and is not caught here.
        return gate.Guard(() =>
        {
            if (target.Store(counts) is not { } remembered) return (false, (string?)null);
            target.Apply(remembered);
            return (true, (string?)null);
        });
    }
}
