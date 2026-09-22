using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Composition;

public sealed partial class LaptopService
{
    // ---- the app's own GPU access (cardwire) ----
    //
    // The one capability in this class that is not about a device this laptop's firmware owns: it is a request
    // the app makes OF A THIRD-PARTY DAEMON on the user's behalf (cardwire, which hides the discrete GPU from
    // programs that have not been allowed to use it — see docs/cardwire-gpu-access.md). What that changes is worth
    // stating where the switch is: nothing on disk, nothing about the machine, and nothing about any other
    // program — one already-running pid is added to the daemon's in-kernel allow-map, and it leaves that map when
    // this process exits.
    //
    // The gate is not here. WHEN there is something to ask for is CardwireGpuAccess's rule, so that it is a pure
    // function of four facts and the row, the startup apply and the call itself cannot come to disagree; this file
    // owns only the graph (the remembered choice) and the order (ask first, remember only what happened).

    /// <summary>This machine's cardwire capability: the facts about it and the one call.
    ///
    /// <c>internal</c>, like <see cref="Settings"/>, as a SIGNPOST rather than a fence, and SETTABLE for the same
    /// reason <c>JsonSettingsStore</c>'s path is a constructor argument: the real port is built by the OS host
    /// (<c>CardwireGpuAccessHost.Create</c>) and probes a live machine, so a test that wants a GPU, a daemon or a
    /// refusal cannot arrange one any other way. Production never writes it.</summary>
    internal CardwireGpuAccessPort CardwireGpuAccess { get; set; } = CardwireGpuAccessHost.Create();

    /// <summary>The user's remembered consent to use the discrete GPU. Off on a machine that never chose —
    /// the capability is off by default, and a machine with no cardwire never shows the row that would turn it
    /// on in the first place.</summary>
    public bool CardwireGpuAccessEnabled { get { lock (_state) return Settings.CardwireGpuAccess; } }

    /// <summary>
    /// Turn the app's GPU access on or off, and remember the choice. Called by the Options row's toggle.
    ///
    /// ON ASKS FIRST AND REMEMBERS ONLY WHAT HAPPENED — the same contract as applying a declared setting
    /// (<see cref="AcerHelper.Application.ApplyDeclaredSetting"/>), and for the same reason: a file that says
    /// "this app may use the discrete GPU" while the daemon refused would be a preference the app cannot keep,
    /// and the row would show it as granted. A refusal leaves the file alone and returns the daemon's own words,
    /// which the row's own failure path shows.
    ///
    /// OFF FORGETS THE CHOICE AND CANNOT TAKE THE GRANT BACK. That asymmetry is cardwire's, not this app's: there
    /// is no revoke method, so the grant stays in force until this process exits. The consent prompt says so before
    /// the user agrees, and the doc repeats it — an OFF that pretended to revoke would be the one lie this feature
    /// could tell.
    ///
    /// The call runs on the caller's thread (the Options row's serial worker, off the UI thread) and blocks for as
    /// long as one busctl round trip takes.</summary>
    public (bool ok, string? error) SetCardwireGpuAccess(bool on)
    {
        if (!on)
        {
            lock (_state) { Settings.CardwireGpuAccess = false; Save(); }
            return (true, null);
        }

        var (ok, error) = CardwireGpuAccess.Request();
        if (!ok) return (false, error);

        lock (_state) { Settings.CardwireGpuAccess = true; Save(); }
        return (true, null);
    }

    /// <summary>
    /// Ask for the grant again on a run that starts with the choice already on — the "apply it every run" half of
    /// the consent. Called by <c>AppController</c> at startup (off the UI thread, since it shells out to busctl),
    /// and it reports rather than throws: (false, reason) is what the caller turns into the status line.
    ///
    /// IT ASKS ONLY WHERE THERE IS SOMETHING TO ASK FOR, and the two silent cases are deliberate: a machine whose
    /// GPU is already visible has nothing to request, and one whose daemon is gone is REPORTED (the user's own
    /// choice has stopped being honoured) — see <see cref="CardwireGpuAccess.DutyFor"/> for all four branches.
    ///
    /// THE CHOICE IS NOT CLEARED WHEN THE DAEMON REFUSES. A startup failure is environmental — the daemon is
    /// down, the interface moved under a newer cardwire — while the preference is the user's and still means what
    /// it meant; clearing it here would silently undo a decision nobody revoked. The row reports the state it can
    /// actually keep (it reads the choice AND the request's outcome through its own read-back), and the next start
    /// tries again exactly once.</summary>
    public (bool ok, string? error) ApplyCardwireGpuAccess()
    {
        bool wanted;
        lock (_state) wanted = Settings.CardwireGpuAccess;
        if (!wanted) return (true, null);   // the default: nothing is probed, called or shown

        return CardwireGpuAccess.Duty(enabled: true) == CardwireGpuAccessDuty.None
            ? (true, null)
            : CardwireGpuAccess.Request();
    }
}
