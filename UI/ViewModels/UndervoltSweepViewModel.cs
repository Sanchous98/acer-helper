using System.Diagnostics;
using AcerHelper.Infrastructure;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>
/// The guided-undervolt card inside the Tuning drawer: an explicit start (behind a one-time confirmation), live
/// progress, Cancel, and — on completion — the proposal PREVIEWED on the manual sliders with Save-to-this-mode /
/// Discard.
///
/// WHAT IT OWNS AND WHAT IT DOES NOT. Every decision with a safety consequence is Infrastructure's
/// (Infrastructure/Vendors/Generic/UndervoltSweep.cs for the decisions, the volatile SMU adapter and the load
/// run for the I/O); this class
/// only drives them and renders their progress. It never writes an offset itself: Start runs the injected sweep,
/// the proposal is shown on the sliders through <c>previewProposal</c> (a UI-only preview, never a write), Save
/// calls the injected save (which goes through the existing per-mode path), and Discard only asks the CoViewModel
/// to restore the sliders — the sweep's own finally has already returned the machine to its pre-sweep state, so
/// "discard" is simply dropping the preview.
///
/// WHILE A SWEEP RUNS the manual Curve-Optimizer sliders are DISABLED by telling the CoViewModel a sweep is
/// running (<c>setSweepRunning</c>), because two writers on one SMU would invalidate every probe; the service's own
/// gate is the second line of defence. The delegate names the SWEEP state, not the slider state, so its polarity
/// matches <c>CoViewModel.SetSweepRunning</c> directly — an "enabled" flag here would be the inverse and is exactly
/// the wiring that once left the sliders locked after a cancelled run. Once the run completes the PREVIEW keeps
/// them disabled (CoViewModel.HasPreview) so the only actions are Save and Discard.
///
/// PROGRESS IS EVENT-DRIVEN, and the elapsed clock is the app's shared <see cref="PeriodicSchedule"/> in UI mode
/// (never a bare DispatcherTimer). The injected <c>uiDispatch</c> is the toolkit half, exactly as
/// <see cref="CoViewModel"/>'s debounce takes it.
/// </summary>
public sealed partial class UndervoltSweepViewModel : ObservableObject
{
    private readonly Func<Task<bool>> _confirm;
    private readonly Func<IProgress<SweepProgress>?, CancellationToken, Task<SweepResult>> _run;
    private readonly Func<SweepResult, Task<(bool ok, IReadOnlyList<int>? counts, string? error)>> _save;
    private readonly Action<bool> _setSweepRunning;
    private readonly Action<IReadOnlyList<int>> _loadCoRows;
    private readonly Action<IReadOnlyList<(int Index, int Value)>> _previewProposal;
    private readonly Action _discardPreview;
    private readonly Action<string> _report;
    private readonly PeriodicSchedule _ui;

    private readonly TimeSpan _eta;
    private CancellationTokenSource? _cts;
    private Stopwatch? _clock;
    private SweepResult? _result;

    /// <summary>True when the LIVE source stopped being AC while the sweep was running and this class cancelled
    /// the run for it. It exists only to render the specific "charger was disconnected" stop instead of the
    /// generic "cancelled", so the user understands why a run they did not cancel ended — the sweep's own
    /// finally has already restored the pre-sweep offsets either way. Cleared at each start.</summary>
    private bool _powerLost;

    /// <summary>The live power source as last pushed by the refresh pass: true on AC, false on battery, null
    /// before any reading. The sweep is REFUSED unless this is exactly true — on battery the platform can accept
    /// a Curve-Optimizer write without applying it (the 0xFD cause), and an unknown source has to fail closed
    /// (see <c>LaptopService.OnAc</c>).</summary>
    private bool? _onAc;

    /// <summary>The caution the panel always carries: this is evidence under one test, and the documented failure
    /// mode (hours later at idle) is exactly what a short sweep cannot prove away.</summary>
    public string Caution { get; } = Loc.T("uv.caution");

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _elapsedText = "";

    // The two computed flags below are read by the view and do not change on their own: raise them whenever the
    // state they depend on moves, or the start button and the cancel button would not swap.
    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStart));
        OnPropertyChanged(nameof(CanCancel));
    }

    partial void OnHasResultChanged(bool value) => OnPropertyChanged(nameof(ShowStart));

    /// <summary>Shown next to the start button before a run: the AC requirement (the reason the button is greyed
    /// on battery) and a realistic (pessimistic) estimate of how long the sweep will take. The procedure itself
    /// — the cluster/stage scope and the transient performance profile — is spelled out in the confirmation, so
    /// it is not repeated here.</summary>
    public string StartHint { get; }

    /// <summary>Whether the Start button is live. Requires a CONFIRMED AC source: on battery the sweep is
    /// refused (the platform can suppress a Curve-Optimizer write while acknowledging it, so a probe would
    /// "pass" against an offset that was never applied), and an unknown source fails closed for the same
    /// reason.</summary>
    public bool CanStart => _onAc == true;

    public bool ShowStart => !IsRunning && !HasResult;
    public bool CanCancel => IsRunning;

    public UndervoltSweepViewModel(
        IReadOnlyList<SweepDomain> domains,
        TimeSpan eta,
        Func<Task<bool>> confirm,
        Func<IProgress<SweepProgress>?, CancellationToken, Task<SweepResult>> run,
        Func<SweepResult, Task<(bool ok, IReadOnlyList<int>? counts, string? error)>> save,
        Action<bool> setSweepRunning,
        Action<IReadOnlyList<int>> loadCoRows,
        Action<IReadOnlyList<(int Index, int Value)>> previewProposal,
        Action discardPreview,
        Action<string> report,
        Action<Action> uiDispatch,
        bool? onAc = null)
    {
        _eta = eta;
        _confirm = confirm;
        _run = run;
        _save = save;
        _setSweepRunning = setSweepRunning;
        _loadCoRows = loadCoRows;
        _previewProposal = previewProposal;
        _discardPreview = discardPreview;
        _report = report;
        _onAc = onAc;
        _ui = new PeriodicSchedule(Tick, TimeSpan.FromSeconds(1), uiDispatch);
        StartHint = Loc.T("uv.requires_ac", Duration(eta));
    }

    /// <summary>Push the live power source in. Called from the refresh pass on the UI thread; raises the start
    /// button's enabled state so a plug/unplug is reflected without waiting for a rebuild.
    ///
    /// IT ALSO STOPS A RUNNING SWEEP WHEN THE CHARGER GOES AWAY. The AC requirement is not only a gate on the
    /// START: the same reason it is refused on battery — the platform can ACKNOWLEDGE a Curve-Optimizer write
    /// while SUPPRESSING its effect, so a probe "passes" against an offset that was never applied — applies the
    /// moment the source drops mid-run. Everything the run has measured since the unplug is suspect, and the
    /// machine is now on a power budget whose throttling the probe never accounted for, so the honest action is
    /// to stop, not to keep producing a verdict from contaminated evidence. The REFRESH PASS is what calls this
    /// (AppController.UiPass pushes <c>LaptopService.OnAc</c> every pass), so a physical unplug reaches here
    /// within one pass without any polling of its own. Cancelling is enough: the cancellation propagates into the
    /// loop and the load run, and the sweep's finally restores the pre-sweep SMU counts, exactly as the Cancel
    /// button does. A source flapping BACK to AC mid-run cannot revive the run — it was stopped.</summary>
    public void SetOnAc(bool? onAc)
    {
        if (_onAc == onAc) return;
        _onAc = onAc;
        OnPropertyChanged(nameof(CanStart));
        if (onAc != true && IsRunning) { _powerLost = true; _cts?.Cancel(); }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        // THE AC GATE, before the confirmation and before anything is written. A refusal is a message, never a
        // run and never a profile change; the service enforces the same rule as its first act, so a source that
        // changes between this check and the run is still refused there.
        if (_onAc != true)
        {
            _report(_onAc == false
                ? Loc.T("uv.connect_ac")
                : Loc.T("uv.waiting_power"));
            return;
        }
        // One-time explicit consent, and it must be BEFORE anything is written: the sweep is long, invasive and
        // can destabilise the machine or force a reboot.
        if (!await _confirm()) return;

        // A new run supersedes any proposal still previewed from the last one: drop the preview (put the sliders
        // back on their committed values and clear the marks) before the run locks them.
        if (HasResult) _discardPreview();
        _result = null;
        HasResult = false;
        _powerLost = false;               // a new run starts with no power-loss stop behind it
        _clock = Stopwatch.StartNew();
        _cts = new CancellationTokenSource();
        IsRunning = true;
        StatusText = Loc.T("uv.phase_first");
        ElapsedText = "";
        _setSweepRunning(true);   // one writer on the SMU at a time (the service gate is the second defence)
        _ui.Start();
        try
        {
            var progress = new Progress<SweepProgress>(OnProgress);
            var result = await _run(progress, _cts.Token);
            _result = result;
            // PREVIEW the proposal ON the manual sliders — a UI-only move of the thumbs, never a write and never
            // persisted. Only the swept CPU clusters carry an index into the port's domain array; the iGPU is
            // excluded from the sweep and so is never in this list (its row is left untouched). A run that
            // produced no proposal (cancelled, refused, SMU-rejected) shows the panel but does NOT lock the
            // sliders behind an empty preview — there is nothing to save and Discard must not be the only way out.
            if (result.HasProposal)
                _previewProposal(result.Domains.Where(d => d.Domain.Index >= 0)
                                               .Select(d => (d.Domain.Index, d.Proposed)).ToList());
            HasResult = true;
            StatusText = OutcomeText(result);
        }
        catch (Exception ex)
        {
            // A run that THREW is not a stability verdict and it must not be dressed as one. The sweep's own
            // failure channel is the stop reason, but an exception never reaches a SweepResult, so the honest
            // report is the app's own words PLUS the exception's — without it the user only ever saw the generic
            // "Guided undervolt failed" and the real cause (a stale gate, an EC/WMI throw, a port that blew up)
            // was swallowed here.
            _report(Loc.T("uv.sweep_failed") + Err(ex));
            StatusText = Loc.T("uv.sweep_failed") + Err(ex);
        }
        finally
        {
            _ui.Stop();
            _clock?.Stop();
            IsRunning = false;
            _setSweepRunning(false);
            _cts?.Dispose();
            _cts = null;
            Tick();
        }
    }

    /// <summary>Stop the sweep. The cancellation propagates into the loop and the load run, and the sweep's
    /// finally restores the pre-sweep state, so cancelling never leaves a probe offset behind.</summary>
    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Persist the proposal as the current mode's offsets, through the existing per-mode path. This is
    /// the ONLY way a swept value reaches settings.json, and it is the user's explicit click.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_result is not { } result || IsRunning) return;
        var (ok, counts, error) = await _save(result);
        if (!ok)
        {
            // Leave the preview up so the user can retry Save or Discard.
            _report(Loc.T("uv.sweep_failed") + (string.IsNullOrEmpty(error) ? "" : $" — {error}"));
            return;
        }
        // Success: drop the preview first (clears the proposed marks / HasPreview), then reload the rows with the
        // committed counts, so the sliders show the saved values with no proposal marking left.
        _discardPreview();
        if (counts is not null) _loadCoRows(counts);
        // NO status-line message on a successful save: the proposal marking clearing and the manual rows reloading
        // are the feedback, and a confirmation line in the footer only adds noise (the user asked to drop it).
        // Failures still report (below) — they are the case with no other visible effect.
        _result = null;
        HasResult = false;
        StatusText = "";
    }

    /// <summary>Drop the proposal. Nothing is undone on the hardware — the machine is already back on its pre-sweep
    /// offsets (the sweep's finally), so this only restores the sliders to their pre-preview committed values and
    /// clears the proposal marking.</summary>
    [RelayCommand]
    private void Discard()
    {
        _discardPreview();
        _result = null;
        HasResult = false;
        StatusText = "";
    }

    private void OnProgress(SweepProgress p)
    {
        // STAGE FIRST, then the cluster being swept, so the user sees which cluster is on the bench — not a bare
        // "core x/10", which read as if one probe loaded all ten physical cores. The ALL-CORE phase loads every
        // physical core regardless (the package current the oracle needs); this flag governs the SINGLE-CORE
        // phase, so when the cluster could not be identified it walked ALL cores and the "all cores" wording says
        // so rather than letting the stage look isolated.
        var stage = Loc.T("uv.stage", p.Stage, p.DomainCount);
        var cluster = p.ClusterIdentified
            ? Loc.T("uv.cluster", p.DomainLabel)
            : Loc.T("uv.all_cores");
        var phase = Loc.T(PhaseKey(p.Phase));
        var offset = p.Offset.ToString();
        var temperature = p.TemperatureC >= 0
            ? Loc.T("uv.temp_c", p.TemperatureC)
            : Loc.T("uv.temp_unreadable");
        StatusText = p.Core is { } core
            ? Loc.T("uv.telemetry_core", stage, cluster, phase, offset, p.CoreIndex + 1, p.CoreCount, temperature)
            : Loc.T("uv.telemetry", stage, cluster, phase, offset, temperature);
    }

    private void Tick()
    {
        if (IsRunning && _clock is not null) ElapsedText = Loc.T("uv.elapsed", Duration(_clock.Elapsed));
    }

    /// <summary>The run's outcome line: the stop reason in the user's words, with the run's own Detail appended
    /// when it carries one. The Detail is where the REAL cause lives — a forced-profile note, the SMU's refusal
    /// text, the back-off explanation, the port's error — and dropping it (as this card used to) turned every
    /// failure into the same generic sentence. It is appended verbatim because it is a diagnostic, not a localized
    /// string; the stop reason above it is the localized half.
    ///
    /// A POWER-LOSS STOP IS ITS OWN SENTENCE. The sweep's Stop is <see cref="SweepStop.Cancelled"/> for a charger
    /// disconnect just as it is for the Cancel button, because the mechanism is the same cancellation — but the
    /// two mean different things to the user, and "Stopped: cancelled" for a run they never cancelled is exactly
    /// the confusing half-truth this flag exists to avoid. <see cref="_powerLost"/> is set only on the
    /// source-loss path, so it can tell them apart.</summary>
    private string OutcomeText(SweepResult result)
    {
        var line = StopText(result);
        return string.IsNullOrWhiteSpace(result.Detail) ? line : line + " — " + result.Detail;
    }

    private string StopText(SweepResult result)
    {
        if (_powerLost && result.Stop == SweepStop.Cancelled) return Loc.T("uv.stop_power_lost");
        return result.Stop switch
        {
            SweepStop.Completed or SweepStop.FirstError => Loc.T("uv.finished"),
            SweepStop.ThermalAbort => Loc.T("uv.stop_thermal"),
            SweepStop.Cancelled => Loc.T("uv.stop_cancelled"),
            SweepStop.ApplyFailed => Loc.T("uv.stop_smu"),
            SweepStop.NotOnAc => Loc.T("uv.refused_ac"),
            _ => Loc.T("uv.stopped"),
        };
    }

    private static string Err(Exception ex) => $": {ex.GetType().Name}: {ex.Message}";

    private static string PhaseKey(SweepPhase phase) => phase switch
    {
        SweepPhase.Applying => "uv.phase_applying",
        SweepPhase.Loading => "uv.phase_testing",
        SweepPhase.SingleCore => "uv.phase_single_core",
        SweepPhase.BackingOff => "uv.phase_backing_off",
        SweepPhase.Confirming => "uv.phase_confirming",
        SweepPhase.Restoring => "uv.phase_restoring",
        _ => "nav.done",
    };

    private static string Duration(TimeSpan span)
        => span.TotalHours >= 1
            ? Loc.T("uv.eta_hours", span.TotalHours)
            : span.TotalMinutes >= 1
                ? Loc.T("uv.eta_minutes", span.TotalMinutes)
                : Loc.T("uv.eta_seconds", span.TotalSeconds);
}
