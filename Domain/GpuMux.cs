namespace AcerHelper.Domain;

// The MUX switch: the hardware multiplexer that routes the built-in panel to the integrated GPU (hybrid /
// Optimus) or straight to the discrete GPU (discrete / Ultimate). ONE port for every vendor, because the
// semantics are the vendor-independent part and they are the dangerous part:
//
//   * A MUX CHANGE IS NEVER IMMEDIATE. The firmware records the choice and applies it on the NEXT RESTART, on
//     every backend we support (asusd queues it and applies at shutdown; NitroSense's switch tells the user to
//     restart). So the port's write is a QUEUE, the UI must say "restart required", and no implementation may
//     apply immediately or from a refresh loop.
//   * A WRONG VALUE CAN LEAVE THE SCREEN BLACK, because until the firmware re-routes the panel there is no
//     display to recover from. Every backend must validate against device-reported state and refuse rather than
//     guess, and require an explicit user action with a black-screen warning.
//
// The ASUS backend drives `xyz.ljones.AsusArmoury.gpu_mux_mode` through the consent-gated, single-flight
// AsusdArmouryPort and the ASUS Windows path the ATK `gpu_mux_mode` device; the Acer backend drives the
// recovered `AcerGamingFunction` `Get/SetGamingMiscSetting` selectors (queued, next boot) and still refuses
// cleanly when that interface is not answerable (see AcerGpuMux.cs).
// This file holds only the vocabulary, so all of them sit behind the same type and the same UI.

/// <summary>The machine's MUX state: what the firmware is in NOW, what is QUEUED for the next restart (null when
/// nothing is queued), and whether a restart is consequently required. A queued value EQUAL to the current one
/// is not a change the next restart would make: <see cref="From"/> normalizes it to no pending and
/// <see cref="RebootRequired"/> false, so the UI can never claim a post-reboot change that would not happen.
/// An UNKNOWN current is kept as pending — equality is never guessed against an unread value.</summary>
public sealed record GpuMuxState(ChoiceOption? Current, ChoiceOption? Pending, bool RebootRequired)
{
    /// <summary>Fold a raw (current, queued) read into a normalized state. A queued value equal to the
    /// firmware-reported current is treated as already applied: no pending and no restart. When the current is
    /// unknown the queued value is KEPT, because equality may never be guessed against a value the firmware did
    /// not report.</summary>
    public static GpuMuxState From(ChoiceOption? current, ChoiceOption? pending)
        => pending is not null && pending.Id != current?.Id
            ? new(current, pending, true)
            : new(current, null, false);
}

/// <summary>The outcome of requesting a MUX change. Exactly one success shape is set:
/// <see cref="Queued"/> means a MUX change was queued (never applied) and the UI must warn that a restart is
/// needed; <see cref="NoOp"/> means the requested mode already equals the device-reported current mode, so
/// NOTHING was written, there is no pending value and no restart follows. <see cref="Error"/> is a message: a
/// localization key for the app's own refusals, or the vendor's own words for a transport refusal.</summary>
public readonly record struct GpuMuxChange(bool Ok, bool Queued, string? Error, bool NoOp = false);

/// <summary>
/// Discrete-GPU routing (MUX). A machine whose firmware exposes no switchable MUX has no such port (a null slot
/// on the device). Implementations MUST queue every change, MUST validate against device-reported state and
/// refuse over guessing, and MUST NOT be reachable from the periodic refresh loop. <see cref="Request"/> is the
/// explicit user action; the confirmation prompt (with the black-screen warning) is the caller's.
/// </summary>
public interface IGpuMux
{
    string? LastError { get; }

    /// <summary>False when this machine's firmware exposes no MUX the app can drive; the UI then shows the
    /// refusal reason instead of controls.</summary>
    bool Supported { get; }

    /// <summary>The modes the machine offers, in display order. An id is the vendor's stable wire value.</summary>
    IReadOnlyList<ChoiceOption> Modes { get; }

    /// <summary>Read the current and queued state — a side-effect-free read, done when the UI opens and after a
    /// request, never on a timer. The result is normalized: a queued value equal to the reported current is
    /// reported as no pending (see <see cref="GpuMuxState.From"/>).</summary>
    GpuMuxState Read();

    /// <summary>Queue a mode change by its id. Takes effect on the next restart. Requesting a mode the device
    /// already reports as current is a NO-OP: nothing is written and the result says <see
    /// cref="GpuMuxChange.NoOp"/>, never <c>Queued</c>. When the current mode cannot be read the request is
    /// treated as a real change (no equality may be guessed), which is the conservative answer.</summary>
    GpuMuxChange Request(string modeId);
}

/// <summary>The shared MUX vocabulary: the mode display names and the sentences the UI and the adapters share.
/// The warning is the SAME sentence as the AsusArmouryMessages.GpuRebootWarning the armoury port uses, so one
/// Russian entry covers both (Localization/Strings.Ru.cs is keyed by the English text).</summary>
public static class GpuMuxMessages
{
    /// <summary>The generic mode labels. Each backend maps its own wire values onto these. The labels are kept
    /// DELIBERATELY SHORT (one word where possible): the card renders the current mode on a SINGLE non-wrapping
    /// line (~400px inside the 468px flyout), so a long label would be clipped. The vendor brand words
    /// ("Optimus"/"Ultimate") are dropped from the label, not from the backend's own comments. <see
    /// cref="AutoMode"/> is the Acer-only third wire value (Auto Select / DDS).</summary>
    public const string HybridMode = "Hybrid";
    public const string DiscreteMode = "Discrete";
    public const string AutoMode = "Auto (DDS)";

    /// <summary>The short note shown NEXT TO THE MUX SWITCHER while the requested/pending value differs from the
    /// original value the card first showed. It explains the trailing <c>*</c> on the current-mode line: the
    /// marked value is applied after a reboot. Kept short so it sits on the switcher's row and does not add a
    /// full-width caption row.</summary>
    public const string AfterRebootNote = "* Will be applied after a reboot.";

    public const string Warning =
        "Changing the GPU mode is queued and only takes effect on the next restart. A wrong value can leave the "
        + "screen black — if that happens, force a shutdown by holding the power button, then start the machine "
        + "and change the mode back from a console or another display. See docs/asus-support.md.";

    public const string Unsupported =
        "This machine's GPU mode cannot be switched from the app: the vendor's MUX/GPU-mode interface is not "
        + "publicly documented, and writing an unknown value can leave the screen black. Use the vendor tool "
        + "(NitroSense/PredatorSense) or the BIOS instead.";

    public const string UnknownMode = "That is not one of the GPU modes this machine offers.";

    /// <summary>The no-op outcome: the requested mode is the one the device already reports, so there is
    /// nothing to queue and no restart. The UI shows this instead of the "restart to apply" sentence.</summary>
    public const string AlreadyCurrent = "This is already the current GPU mode.";
}
