namespace AcerHelper.Domain;

/// <summary>Why a write to a PRESENT hardware port did not take. One of the things this app can fail at, and — like
/// <see cref="SettingNotAppliedException"/> — it carries INFORMATION rather than a sentence: WHICH operation was
/// being written (a short machine-readable key), and the transport's OWN words when it has any. It deliberately
/// does not compose a user-facing message, because the domain knows no display names and the layer that does (the
/// UI) is the one that turns this into the words the user reads (<c>AppController</c>, <c>OptionsAssembler</c>).
/// <see cref="Exception.Message"/> exists for a log or a stack trace and is never what the UI shows.
///
/// WHY A WRITE REFUSAL IS AN EXCEPTION AND NOT A RETURNED VALUE. A user action that reaches a write port has
/// ALREADY PASSED A UI GATE — the control was enabled, so the app has already promised the machine can take this.
/// A refusal there is therefore a VIOLATED PRECONDITION or a real fault, not an ordinary outcome the caller is
/// expected to branch on. And unlike the <c>(bool ok, string? error)</c> pair this replaces, a throw always carries
/// its OWN reason: a tuple's reason can be absent (the old <c>(false, null)</c>), which is exactly the ambiguity
/// docs/open-decisions.md §2 had to patch on 2026-09-18 ("a throw reports no reason"); an exception object is the
/// failure's own, so it can never wear an earlier call's words. The declared-setting path already throws
/// (<see cref="SettingNotAppliedException"/>) and is the precedent this follows.
///
/// AN ABSENT PORT IS NOT THIS EXCEPTION. A machine with no port at all (a desktop, an install with no EC channel,
/// a feature the probe removed) is a CAPABILITY fact, not a live hardware refusal: there was no port to refuse
/// anything. That stays a VALUE (or a use-case guard), and the contracts that can be reached without a port expose
/// a capability member (e.g. <c>IProfileTarget.CanApply</c>, <c>IUndervoltTarget.Store</c>'s null) so the no-port
/// no-op the app has always shown is preserved WITHOUT calling a port.
///
/// WHO CATCHES IT. A USER action catches it at its own boundary and shows the message (the control that was
/// enabled is the one that failed). A SYSTEM path — the guided sweep's volatile writes, a per-source restore, the
/// boot re-apply — catches it and turns it into its NON-VERDICT outcome; a throw must never escape a
/// background/poll path unobserved.</summary>
public sealed class PortWriteFailedException(string operation, string? reason)
    : Exception($"{operation} was refused" + (reason != null ? $": {reason}" : ""))
{
    /// <summary>The operation being written, as a short machine-readable key (e.g. <c>"profile switch"</c>,
    /// <c>"GPU offsets"</c>) — enough for a log to name what failed, and deliberately not a translated sentence.
    /// It is the domain's word for the ACT; the UI names the control the user was looking at.</summary>
    public string Operation { get; } = operation;

    /// <summary>The transport's own words when it refused the write, or null when it gave none — it never set its
    /// <c>LastError</c> on this call, or it THREW (and then no reason belonging to this call exists to report;
    /// see <c>FlagSetting.Write</c>'s note, Domain/DeclaredSetting.cs). Never another call's words: a throw carries
    /// its own.</summary>
    public string? Reason { get; } = reason;
}
