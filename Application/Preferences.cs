using AcerHelper.Localization;

namespace AcerHelper.Application;

/// <summary>What persisting the app's own scalar preferences needs from whoever owns the settings graph and the
/// store — the contract for the three shell rows that are a preference and nothing else («поведение клавиши Turbo»,
/// «язык», «Stay awake when lid closed»), declared here and implemented by the layer that owns the graph and the
/// file (Infrastructure/Composition/LaptopService.Toggles.cs).
///
/// ONE CONTRACT FOR THREE SCALARS, and it is not a shortcut: the three are the same act — write one scalar into the
/// guarded graph and hand the file to the store — so three interfaces of one method each would be the contract
/// spelled out three times rather than three rules. What makes them one axis is exactly what the row has: a chosen
/// value, no hardware to ask, and a file to update. The clamshell is the odd one and it is NOT in this contract:
/// its write touches a port first (see <see cref="IClamshellTarget"/>).
///
/// THE LOCK IS THE IMPLEMENTATION'S. Every member here is a graph write under the hardware's own lock followed by
/// the store, and the use case below must not be able to hold that lock itself — which is why the contract has one
/// member per value rather than an "open a transaction" half a caller could get wrong.</summary>
public interface IPreferenceStore
{
    /// <summary>Remember whether the Turbo hotkey behaves as a switch.</summary>
    void TurboToggles(bool on);

    /// <summary>Remember the chosen UI language. Activating it (and rebuilding the UI) is the app layer's job —
    /// this records the preference only.</summary>
    void Language(AppLanguage language);

    /// <summary>Remember whether the lid-closed keep-awake takeover is on. The hardware half is
    /// <see cref="IClamshellTarget"/>'s; this is only the file.</summary>
    void Clamshell(bool on);
}

/// <summary>What the clamshell keep-awake needs from whoever owns the port — implemented by
/// Infrastructure/Composition/LaptopService.Toggles.cs. TWO MEMBERS because the row writes and the refresh pass
/// re-evaluates, and those are different acts: <see cref="SetEnabled"/> is the user's choice,
/// <see cref="Evaluate"/> lets the OS re-decide the current lid/AC state without a new choice.</summary>
public interface IClamshellTarget
{
    /// <summary>Ask the port to enable or disable the takeover. A machine with no port is a no-op.</summary>
    void SetEnabled(bool on);

    /// <summary>Recompute the lid-closed sleep decision from the current topology. QueryDisplayConfig can hitch
    /// during a topology change, so the refresh pass calls this off the UI thread.</summary>
    void Evaluate();
}

/// <summary>The Turbo-key preference as a use case: remember it. There is nothing else — no hardware, no read —
/// and the use case exists so the UI names the action rather than the service.</summary>
public sealed class SetTurboToggles(IPreferenceStore store)
{
    public void Run(bool on) => store.TurboToggles(on);
}

/// <summary>The UI language as a use case: record the preference. The rebuild that makes it live stays in the app
/// layer, because it tears down and rebuilds the UI and this layer may not know a toolkit exists.</summary>
public sealed class SetLanguage(IPreferenceStore store)
{
    public void Run(AppLanguage language) => store.Language(language);
}

/// <summary>The clamshell keep-awake as a use case: ask the port to take the value, then record it.
///
/// WHAT IT DECIDES, and it is the order the row has always had: THE PORT IS TOLD FIRST. The recorded value is what
/// <c>ApplyStartupState</c> re-applies on the next run, so writing it before the port was asked would leave the
/// file claiming a takeover this run never engaged. There is no report: <see cref="IClamshellTarget.SetEnabled"/>
/// returns nothing (the port's own <c>SetEnabled</c> does too), and the app has never shown a failure for this row.
///
/// The hardware call is NOT under the graph lock: the lock must never span a port call
/// (docs/domain-refactoring-plan.md §4), and the two halves are therefore two contract members rather than one.</summary>
public sealed class SetClamshell(IClamshellTarget target, IPreferenceStore store)
{
    public void Run(bool on)
    {
        target.SetEnabled(on);
        store.Clamshell(on);
    }
}

/// <summary>Re-evaluate the clamshell takeover as a use case: one port call, named so the refresh pass depends on
/// the action instead of on the service. No policy lives here — the port decides from the live topology — which is
/// why this is the thinnest member of the family and is stated rather than hidden.</summary>
public sealed class EvaluateClamshell(IClamshellTarget target)
{
    public void Run() => target.Evaluate();
}
