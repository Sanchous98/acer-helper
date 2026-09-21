using AcerHelper.Infrastructure;
using AcerHelper.Localization;

namespace AcerHelper.UI;

/// <summary>The words of the informed-consent prompt that sits between the "Grant hardware access…" banner and
/// the privileged install (<c>AppController.GrantHardwareAccessAsync</c> → <c>FlyoutCoordinator</c> →
/// <see cref="Views.ConfirmDialog"/>). The install is not only a file permission: it also writes a modprobe.d
/// options line that changes how the acer-wmi driver behaves at load, and it makes specific kernel nodes
/// group-writable — so the user is shown WHAT is granted and to WHOM, and agrees before anything is installed.
/// Cancelling installs nothing (the caller returns before the install runs).
///
/// THE ROWS ARE THE INSTALLER'S OWN TABLE, not a list kept here: <see cref="HardwareAccess.ConsentRows"/> renders
/// one localized sentence per entry the install will place on this machine, in the installer's order and under
/// the installer's Acer gate. A future permission — the CPU undervolt's SMU nodes — is one more entry there and
/// appears here with no edit to this file; the only thing this file owns is the frame around the rows (the
/// request for administrator rights, the honest limits, and what to do afterwards).
///
/// A separate, toolkit-free file rather than three literals inside <c>FlyoutCoordinator</c> (where the other two
/// prompts compose theirs) for one reason: the body is BUILT, and a built message has to be readable by a test —
/// <c>HardwareAccessConsentTests</c> pins the rows against the installer's table and the keys against
/// Strings.Ru.cs, and neither is possible through an Avalonia window that needs a live flyout to exist.</summary>
public static class HardwareAccessConsent
{
    /// <summary>The dialog's heading. A question, like the driver prompt's ("Install {0}?"), because the dialog
    /// asks rather than announces and the confirm button is the answer to it.</summary>
    private const string TitleKey = "Install hardware access?";

    /// <summary>The confirm button's word — the same one the driver prompt's button uses, so "agreeing to install
    /// something" reads the same wherever it is asked. The cancel button is the dialog's own (<c>Cancel</c>).</summary>
    private const string ConfirmKey = "Install";

    /// <summary>What the administrator prompt is FOR, said before the rows: the user is about to be asked for
    /// their password by a program whose title bar they have never seen, and this is the only sentence that
    /// explains it.</summary>
    private const string AskKey =
        "Acer Helper asks for administrator rights once (pkexec) to copy the following into /etc:";

    /// <summary>THE HONEST LIMITS, said after the rows and not left implied: the files are the ones this app
    /// already ships (no download, no network), the list above is the WHOLE of what the install adds, and
    /// walking away costs nothing — which is the point of asking rather than telling.</summary>
    private const string LimitsKey =
        "The files come from this app's own copy — nothing is downloaded, no other permission is added, and "
        + "cancelling changes nothing.";

    /// <summary>What to do when it is done. Deliberately does NOT promise an immediate effect: three of the
    /// installed things can need a restart of the app, and the module parameters can need a full reboot when
    /// acer_wmi is in use — the entry that installs them says so in its own row (see
    /// <see cref="HardwareAccess.ConsentRows"/>), which is where the "could not be reloaded" outcome of the
    /// install finally lands.</summary>
    private const string AfterwardsKey = "Restart Acer Helper afterwards to use the new controls.";

    /// <summary>The dialog's heading, localized.</summary>
    public static string Title() => Loc.T(TitleKey);

    /// <summary>The confirm button's caption, localized.</summary>
    public static string ConfirmText() => Loc.T(ConfirmKey);

    /// <summary>The prompt's body for THIS machine: the frame above around one row per file the install will
    /// place. The rows come from the installer itself (see <see cref="HardwareAccess.ConsentRows"/>), under the
    /// installer's own Acer gate, so the user reads exactly the set that is about to be installed.</summary>
    public static string Message() => Body(HardwareAccess.ConsentRows());

    /// <summary>The same body built from an EXPLICIT row set — the installer's, always; the parameter exists so
    /// the test suite can render the prompt for both machine kinds (Acer and not) without faking a DMI read, and
    /// hold the rows it produces against the installer's table entry by entry. Public callers pass
    /// <see cref="Message"/>.</summary>
    internal static string Body(IReadOnlyList<string> rows)
        => Loc.T(AskKey)
           + "\n\n" + string.Join("\n", rows.Select(row => "• " + row))
           + "\n\n" + Loc.T(LimitsKey)
           + "\n\n" + Loc.T(AfterwardsKey);
}
