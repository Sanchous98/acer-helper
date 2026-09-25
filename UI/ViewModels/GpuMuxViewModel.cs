using AcerHelper.Domain;
using AcerHelper.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>
/// The GPU-mode card: the shared MUX switch, for whichever vendor backend implements the shared
/// <see cref="IGpuMux"/> port (ASUS/asusd and ASUS/ATK today; Acer's recovered gaming-WMI port when it answers,
/// otherwise its unsupported refusal).
///
/// ONE STABLE LINE. The card renders exactly one sentence — the mode the firmware is in NOW. When the value that
/// will apply after a reboot DIFFERS from the ORIGINAL value the card first showed, the value is marked with a
/// trailing <c>*</c> on that same line ("GPU mode: Hybrid*"), and a short muted note next to the SELECTOR
/// (<see cref="Note"/>, <see cref="NoteVisible"/>) explains that the marked value is applied after a reboot. The
/// mark is a single character, never a second line; the note shares the selector's row, so showing/hiding it does
/// not add a full-width caption row and the SizeToContent flyout is not resized by a stray blank; see
/// docs/gpu-mux.md.
///
/// THERE IS NO APPLY BUTTON: choosing a different mode in the selector IS the request. A selection change runs the
/// same flow the button used to: it is a no-op (no confirmation, nothing queued) when the chosen mode equals the
/// device-reported current one; otherwise the explicit confirmation carrying the restart + black-screen warning
/// gates it, and a CANCEL (or a refused write) reverts the selector to the mode actually in effect. Programmatic
/// selection changes made by <see cref="Refresh"/> (and by a revert) are suppressed so the card never treats its
/// own update as a user request.
///
/// The ORIGINAL value is captured once, when the card is first populated (<see cref="Refresh"/>, before any
/// queued change this session). The note shows while the requested/pending value differs from it and hides when
/// the user reverts to the original or nothing is queued. A pending value EQUAL to the current one is NOT a
/// queued change (the firmware already reports it, or the read is stale), so it earns no mark; an UNKNOWN current
/// keeps its pending (equality is never guessed).
///
/// OUTCOMES THAT ARE NOT A QUEUED CHANGE (asking for the mode the machine is already in, a refusal) are not
/// rendered in the card at all — they go to the app's existing transient status line, the same one every other
/// control failure uses. Nothing here raises a notification.
///
/// THE READ IS NOT IN THE CONSTRUCTOR: <see cref="Refresh"/> is called when the Tuning drawer opens and after a
/// request, so a vendor read never runs on the UI thread merely because the dashboard was built. The card is
/// only built when the device exposes the port at all.
/// </summary>
public sealed partial class GpuMuxViewModel : ObservableObject
{
    private readonly IGpuMux _port;
    private readonly Func<string, Task<bool>> _confirm;          // (warning key) -> proceed?
    private readonly Func<string, GpuMuxChange> _request;         // (mode id) -> queued change
    private readonly Action<string> _report;                      // transient status line: no-op / error, never a card line

    /// <summary>The device-reported CURRENT mode id from the last <see cref="Refresh"/>, or null when it is
    /// unknown. Used to answer "is this request a no-op?" BEFORE the confirmation, so the reboot/black-screen
    /// warning is only shown when something will actually be queued.</summary>
    private string? _currentModeId;

    /// <summary>The ORIGINAL (baseline) mode id captured the FIRST time the card was populated, before any
    /// queued change this session. The note shows only while the requested/pending value differs from it; it is
    /// never overwritten by later reads (the firmware may report the request back).</summary>
    private string? _originalModeId;
    private bool _originalCaptured;

    /// <summary>The mode id the user most recently asked for (queued, or confirmed as already in effect). Null
    /// until the first request. This is the "requested" side of the note's original-vs-changed comparison, and it
    /// survives a read-back that no longer reports a separate pending value.</summary>
    private string? _requestedModeId;

    /// <summary>True while the ViewModel itself sets <see cref="SelectedIndex"/> (a <see cref="Refresh"/> fold or
    /// a revert), so the selection-change handler does not mistake a programmatic update for a user request.</summary>
    private bool _suppressSelectionApply;

    /// <summary>Single-flight for a selection-driven apply: a rapid re-selection cannot start a second flow while
    /// the confirmation/queue of the first is still running.</summary>
    private bool _applyInProgress;

    public bool Supported { get; }
    public IReadOnlyList<string> Modes { get; }
    public bool HasModes => Modes.Count > 0;

    [ObservableProperty] private int _selectedIndex;

    /// <summary>The card's ONE line: the current mode, marked with a trailing <c>*</c> when the value that will
    /// apply after a reboot differs from the original, or the unsupported refusal on a machine whose firmware
    /// exposes no switch. It is never cleared after a <see cref="Refresh"/>, so the line does not collapse and
    /// the card's height does not change.</summary>
    [ObservableProperty] private string _currentText = "";

    /// <summary>True while the requested/pending value differs from the ORIGINAL value, so the short note next to
    /// the selector is shown. False when nothing is queued or the user reverted to the original.</summary>
    [ObservableProperty] private bool _noteVisible;

    /// <summary>The short note shown NEXT TO THE SWITCHER (not a bottom caption) explaining the <c>*</c> mark:
    /// the marked value is applied after a reboot. Fixed text; only <see cref="NoteVisible"/> changes.</summary>
    public string Note => Loc.T(GpuMuxMessages.AfterRebootNote);

    public GpuMuxViewModel(IGpuMux port, Func<string, Task<bool>> confirm, Func<string, GpuMuxChange> request,
                           Action<string> report)
    {
        _port = port;
        _confirm = confirm;
        _request = request;
        _report = report;
        Supported = port.Supported;
        Modes = port.Modes.Select(m => Loc.T(m.DisplayName)).ToList();
        if (!Supported) _currentText = Loc.T(GpuMuxMessages.Unsupported);
    }

    /// <summary>Read the live current + queued state and fold both into the one line. Called when the drawer
    /// opens and after a request — never from the periodic refresh loop, because this reaches vendor hardware.</summary>
    public void Refresh()
    {
        if (!Supported)
        {
            _currentModeId = null;
            CurrentText = Loc.T(GpuMuxMessages.Unsupported);
            NoteVisible = false;
            return;
        }

        var state = _port.Read();
        _currentModeId = state.Current?.Id;
        // Capture the ORIGINAL only once, on the first populated read: the value before any queued change this
        // session. Later reads must not move it (the firmware may already report the requested value).
        if (!_originalCaptured)
        {
            _originalModeId = state.Current?.Id;
            _originalCaptured = true;
        }

        var current = state.Current != null
            ? Loc.T("GPU mode: {0}", Loc.T(state.Current.DisplayName))
            : Loc.T("GPU mode: unknown");
        // Only a pending that ACTUALLY DIFFERS from the current mode is a queued change: a pending equal to the
        // current one is effectively already applied (a stale read, or firmware that already reports the
        // request), so it earns no mark. An unknown current keeps the pending — equality may not be guessed.
        var pending = state.Pending is not null && state.Pending.Id != state.Current?.Id ? state.Pending : null;
        // The value that WILL apply is the mode the user requested this session (which survives a read-back that
        // no longer reports a separate pending), else the firmware's own queued value. The note/mark appear only
        // while it differs from the ORIGINAL captured above.
        var effectivePendingId = _requestedModeId ?? pending?.Id;
        var changed = effectivePendingId is not null
                      && (_originalModeId is null || effectivePendingId != _originalModeId);
        CurrentText = changed ? current + "*" : current;
        NoteVisible = changed;
        // Prefer the value the user asked for (it survives a read-back that reports it as current), then the
        // firmware's own queued value, then the current mode.
        SetSelectedIndexSuppressed(IndexOfId(_requestedModeId ?? pending?.Id ?? state.Current?.Id));
    }

    /// <summary>The selector changed. Ignore the ViewModel's own programmatic updates (a <see cref="Refresh"/>
    /// fold or a revert); for a user change, run the apply flow (the setter itself is synchronous).</summary>
    partial void OnSelectedIndexChanged(int value)
    {
        if (_suppressSelectionApply || !Supported || _port.Modes.Count == 0) return;
        _ = ApplySelectedModeAsync(value);
    }

    /// <summary>Apply the mode the user selected. THERE IS NO APPLY BUTTON: choosing a different mode IS the
    /// request.
    ///
    /// SELECTING THE CURRENT MODE IS A NO-OP and is caught BEFORE the confirmation, so the reboot/black-screen
    /// warning only ever appears for a real change; nothing is queued and no mark/note appears. The port re-checks
    /// the same rule independently (its read is authoritative). A CANCEL — or a refused write — REVERTS the
    /// selector to the mode actually in effect, so the card never shows a mode that was not queued.
    ///
    /// A real change reports itself through <see cref="Refresh"/>: the current-mode value gains the trailing
    /// <c>*</c> marker and the note next to the selector explains it, while the requested value differs from the
    /// original. Reverting to the original hides the note (the mark and note are cleared).</summary>
    private async Task ApplySelectedModeAsync(int index)
    {
        if (_applyInProgress) return;   // single-flight: no second flow while a confirmation/queue is running
        _applyInProgress = true;
        try
        {
            var mode = _port.Modes[Math.Clamp(index, 0, _port.Modes.Count - 1)];

            if (_currentModeId is not null && _currentModeId == mode.Id)
            {
                // Already in the selected mode: no confirmation, no queue. Record the intent (this HIDES the note
                // when the selection is the original), re-fold the line, and report the no-op on the status line.
                _requestedModeId = mode.Id;
                Refresh();
                _report(Loc.T(GpuMuxMessages.AlreadyCurrent));
                return;
            }

            // A real change is queued and risky: the explicit confirmation carries the restart + black-screen
            // warning. Cancelling it queues nothing and reverts the selector.
            if (!await _confirm(GpuMuxMessages.Warning))
            {
                RevertSelection();
                return;
            }

            var change = _request(mode.Id);
            if (!change.Ok)
            {
                _report(Loc.T(change.Error ?? "GPU mode change failed."));
                RevertSelection();
                return;
            }
            if (change.NoOp)
            {
                // Nothing was queued. Clear the intent only when the user reverted to the ORIGINAL; otherwise the
                // previous request state still describes the value that will apply after the reboot.
                if (_originalModeId is not null && mode.Id == _originalModeId) _requestedModeId = mode.Id;
                Refresh();
                _report(Loc.T(GpuMuxMessages.AlreadyCurrent));
                return;
            }
            _requestedModeId = mode.Id;   // queued: this is the value that will apply after the reboot
            Refresh();
            if (!change.Queued) _report(Loc.T("GPU mode changed."));
        }
        catch (Exception)
        {
            // The flow runs off a property setter (fire-and-forget), so a throwing vendor/UI delegate must not
            // surface as an unobserved task: report it and put the selector back on the mode in effect.
            _report(Loc.T("GPU mode change failed."));
            RevertSelection();
        }
        finally
        {
            _applyInProgress = false;
        }
    }

    /// <summary>Put the selector back on the mode actually in effect (used on cancel/refusal), without re-running
    /// the apply flow.</summary>
    private void RevertSelection() => SetSelectedIndexSuppressed(IndexOfId(_currentModeId));

    /// <summary>Set <see cref="SelectedIndex"/> without treating it as a user request.</summary>
    private void SetSelectedIndexSuppressed(int index)
    {
        _suppressSelectionApply = true;
        try { SelectedIndex = Math.Max(0, index); }
        finally { _suppressSelectionApply = false; }
    }

    private int IndexOfId(string? id)
        => id == null ? 0 : Math.Max(0, _port.Modes.ToList().FindIndex(m => m.Id == id));
}
