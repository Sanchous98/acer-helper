using AcerHelper.Localization;

namespace AcerHelper.UI;

/// <summary>The words of the confirmation that sits between the "Use the discrete GPU" toggle and the D-Bus call
/// that turns it on (<c>OptionsAssembler</c> → <c>FlyoutCoordinator.ConfirmCardwireGpuAccessAsync</c> →
/// <see cref="Views.ConfirmDialog"/>).
///
/// WHY IT IS NOT PART OF <see cref="HardwareAccessConsent"/>, which asks the same kind of question one drawer
/// away. That prompt is built from the INSTALLER's own table and lists files: it exists because the privileged
/// install writes into /etc and, on an Acer, changes how a kernel driver behaves at load — consequences a
/// "grant access" caption cannot carry. This action has no such table and no such file: nothing is installed,
/// nothing is written anywhere, and the whole of what happens is one method call to a running daemon that grants
/// ONE pid a view it did not have. Forcing it into that prompt would mean inventing a row for something no
/// installer places, and the drift guard that keeps that table honest (HardwareAccessConsentTests) would start
/// checking a permission that is not installed at all. So it is its own prompt, and what it owes the user is the
/// four facts that make this grant different from an ordinary setting:
/// <list type="bullet">
/// <item>who is being asked, and for WHICH process — this one, not the machine and not the user;</item>
/// <item>that the grant is per-process and dies with the app;</item>
/// <item>that it CANNOT BE REVOKED while the app runs (cardwire has no revoke method, which is the one thing a
/// user would otherwise reasonably assume a switch can do);</item>
/// <item>that every other application stays blocked.</item>
/// </list>
///
/// A separate, toolkit-free file for the reason <see cref="HardwareAccessConsent"/> is one: the body is BUILT
/// from localized sentences, and a built message has to be readable by a test —
/// <c>CardwireGpuAccessTests</c> holds every sentence here to <c>Strings.Ru.cs</c> and holds the four facts above
/// to the text, neither of which is possible through an Avalonia window that needs a live flyout to exist.</summary>
public static class CardwireGpuAccessConsent
{
    /// <summary>The dialog's heading. A question, like the driver prompt's and the install prompt's, because the
    /// dialog asks rather than announces and the confirm button is the answer to it.</summary>
    private const string TitleKey = "Let Acer Helper use the discrete GPU?";

    /// <summary>The confirm button's word. NOT "Install" (the other two prompts' word): nothing is installed here,
    /// and reusing it would promise a change to the machine that this prompt has just finished saying does not
    /// happen.</summary>
    private const string ConfirmKey = "Allow";

    /// <summary>WHO IS ASKED, AND FOR WHOM — said first, because it is the part that is easy to get wrong from the
    /// outside: the app is not asking for the machine's GPU or for the user's account, it is asking a daemon to
    /// stop hiding one device from one already-running process. The last clause is the honest contrast with the
    /// install prompt next door: nothing is written, and no file permission moves.</summary>
    private const string AskKey =
        "Acer Helper will ask cardwire — the daemon that hides the discrete GPU from programs it has not allowed "
        + "to use it — to let THIS process see that GPU. Nothing is written to disk and no file permission "
        + "changes: the request is a D-Bus call, made while the app is running.";

    /// <summary>THE LIMITS, and the first two are the reason this prompt exists rather than a tooltip. The grant
    /// belongs to this process and ends with it (so "off" is not what ends it), and cardwire offers no revoke at
    /// all — so the switch turns off the NEXT start, and quitting the app is the only thing that ends the grant
    /// that is in force now. A user who reads neither of those would reasonably believe the toggle takes it back.
    /// </summary>
    private const string LimitsKey =
        "The grant belongs to this one process and disappears the moment Acer Helper exits. cardwire has no way "
        + "to take it back, so it cannot be revoked while the app runs — turning this option off stops the next "
        + "start, not this one, and quitting the app is what ends it.";

    /// <summary>THE OTHERS, said last so it is what the user is left with: the block is per-CLIENT, so nothing
    /// about any other program changes. And once per run is worth stating plainly, because it is the behaviour
    /// that follows from the grant's lifetime (see the second sentence above).</summary>
    private const string AfterwardsKey =
        "Every other application stays blocked. While this option stays on, Acer Helper asks again on each start.";

    /// <summary>The dialog's heading, localized.</summary>
    public static string Title() => Loc.T(TitleKey);

    /// <summary>The confirm button's caption, localized.</summary>
    public static string ConfirmText() => Loc.T(ConfirmKey);

    /// <summary>The prompt's body: the three sentences above, one paragraph each. They are the same on every
    /// machine — there is no entry list to render, because nothing is being placed anywhere — so unlike
    /// <see cref="HardwareAccessConsent.Message"/> this needs no parameter to be testable.</summary>
    public static string Message() => string.Join("\n\n", Loc.T(AskKey), Loc.T(LimitsKey), Loc.T(AfterwardsKey));
}
