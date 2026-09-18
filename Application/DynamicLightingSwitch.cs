namespace AcerHelper.Application;

/// <summary>What switching the virtual lighting surface needs from whoever owns the OS channel and the persisted
/// choice — the contract for one of the things the owner's model applies, declared here and implemented by the
/// layer that owns both (Infrastructure/Composition/LaptopService.Lighting.cs).
///
/// WHAT THIS IS NOT, and the distinction is the reason the lighting can land at all. This contract is the
/// LampArray SWITCH — publish the surface, or take it down. It is not the per-mode lighting state: that path
/// (<c>LightsForCurrentMode</c> both forms and <c>EnsureLightZone</c>) hands out a LIVE reference into the
/// settings graph, the UI edits it in place, and a mapping across this boundary would break it by construction —
/// the recorded cost of the earlier investigation, and the reason those accessors are untouched. The switch
/// touches none of that: it reads and writes one bool beside a call to the bridge.
///
/// TWO MEMBERS, AND THE FIRST ONE ANSWERS IN TWO DEGREES. Publishing has three outcomes and they are not two: it
/// worked, it did not (the driver is missing, the transport refused, and the bridge's own <c>LastError</c> says
/// why), or THIS MACHINE HAS NO SURFACE AT ALL — no LampArray transport for the OS, or no zones to publish. The
/// null is that third outcome, and it is a fact about the machine rather than a failure, which is why it is not
/// spelled as a boolean: the caller must not report it as one, and it must not be remembered as a refused
/// attempt either.</summary>
public interface ILightingSwitchTarget
{
    /// <summary>Publish (<paramref name="on"/> true) or take down the virtual lighting surface, and report the
    /// attempt — or null when this machine offers no surface at all. Taking it down is idempotent and cannot
    /// fail, so it reports success whatever state the bridge was in; only publishing has an outcome to
    /// report.</summary>
    (bool ok, string? error)? Switch(bool on);

    /// <summary>Remember the choice, under the graph lock, and persist it.</summary>
    void Store(bool on);
}

/// <summary>The virtual lighting surface as a use case: publish or take down, and remember what is TRUE about it.
///
/// WHAT IT DECIDES, and it is the whole reason this is not a pass-through:
/// <list type="number">
/// <item><b>What is remembered is what the hardware is IN, not what the user asked for.</b> The stored value is
/// <c>on &amp;&amp; ok</c>: a publish that did not take is remembered as OFF, so the Options row corrects itself on
/// its next read instead of claiming a device that is not there — which is the visible failure the row's own
/// refresh exists to prevent, and the one thing a stored "true" would make permanent across restarts.</item>
/// <item><b>A machine with no surface is refused WITHOUT remembering anything.</b> The choice is not stored,
/// because there is nothing to be in: storing the user's wish would make the next boot try to publish a device
/// this machine cannot have. The caller reads the same <c>(false, null)</c> it has always read for it.</item>
/// <item><b>A failed publish still remembers</b> — as off — and the failure is reported upward with the bridge's
/// own words, because the row shows them.</item>
/// </list>
///
/// THE ORDER IS THE ONE THIS PATH HAS ALWAYS HAD: publish first, remember what took, and never the reverse. A
/// remembered choice that precedes the attempt would be a setting the hardware never agreed to.</summary>
public static class ApplyDynamicLighting
{
    public static (bool ok, string? error) Run(bool on, ILightingSwitchTarget target)
    {
        if (target.Switch(on) is not { } outcome) return (false, null);
        target.Store(on && outcome.ok);
        return outcome;
    }
}
