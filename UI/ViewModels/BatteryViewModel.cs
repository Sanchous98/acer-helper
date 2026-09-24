using AcerHelper.Domain;
using AcerHelper.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>Battery section: live charge %/state + health and cycle count (when the battery reports
/// them), plus whichever charging controls the device exposes — the charge-mode dropdown (e.g. Dell
/// Adaptive/Express/Custom) and the charge-limit and calibration toggles. Info is pushed in by
/// <see cref="Update"/> on each refresh.
///
/// The three charging ROWS are built with placeholder values and settled by <see cref="Prime"/> — the same
/// deferral, for the same reason, as the Options drawer's rows (<see cref="OptionsViewModel.TryCreate"/>):
/// each row's value comes from its port, so reading it while the window is being built would block the UI
/// thread on the EC. <paramref name="post"/> is the UI-thread marshaller the rows are built with; it defaults
/// to <c>Dispatcher.UIThread.Post</c> and exists so a test can build the whole section with a synchronous
/// poster and observe the deferred read without a dispatcher — the seam <see cref="VerifiedHwValue{T}"/>
/// already had. (The telemetry fields below are a separate concern and are not deferred: they arrive from
/// <see cref="Update"/>.)</summary>
public sealed partial class BatteryViewModel : SectionViewModel
{
    public BatteryViewModel(bool hasInfo, OptionToggle? limit, OptionToggle? calibration, OptionChoice? chargeMode,
                            Action<Action>? post = null)
    {
        ShowInfo = hasInfo;
        // The limit row is built FIRST so the calibration row can re-read it. Enabling calibration clears the
        // firmware's ~80% cap (calibration wants a full 100% charge), but the two are SEPARATE flags on the SAME
        // WMI control (Domain/Battery.cs): the calibration write changes hardware the limit row is showing, and
        // nothing re-read the limit until the next prime — i.e. the next app start. So the limit switch kept
        // saying "on" for a cap the firmware had already lifted.
        //
        // The wrapper runs the calibration write and THEN has the limit row read the hardware again on its own
        // serial worker (ToggleRowViewModel.Sync — the same verified read-back path the row already uses for an
        // out-of-band change). It runs on the calibration row's worker inside Apply, so it is strictly after the
        // write reached the transport rather than on the optimistic click. Symmetric by construction: turning
        // calibration back off re-reads the limit too, so a restored (or still-lifted) cap shows either way.
        // A DECLINED confirmation never reaches Apply, so it re-reads nothing.
        if (limit != null) Limit = new ToggleRowViewModel(limit, post);
        if (calibration != null)
        {
            var written = calibration;
            var resyncing = written with
            {
                OnChange = v => { written.OnChange(v); Limit?.Sync(); },
            };
            Calibration = new ToggleRowViewModel(resyncing, post);
        }
        if (chargeMode != null) Mode = new ChoiceRowViewModel(chargeMode, post);
    }

    public bool ShowInfo { get; }

    public ToggleRowViewModel? Limit { get; }
    public ToggleRowViewModel? Calibration { get; }
    public ChoiceRowViewModel? Mode { get; }
    public bool ShowLimit => Limit != null;
    public bool ShowCalibration => Calibration != null;
    public bool ShowMode => Mode != null;

    [ObservableProperty] private string _charge = "—";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _health = "";
    [ObservableProperty] private bool _showHealth;
    [ObservableProperty] private string _cycles = "";
    [ObservableProperty] private bool _showCycles;

    /// <summary>The live power row: its label flips with the SIGN of the reading, because the two directions it
    /// can show are the two facts the user asked for — what the machine draws <em>out of</em> the battery
    /// (negative, "Power draw") and how fast the battery is being filled <em>from the wall</em> (positive,
    /// "Charging power"). The magnitude is rendered with a unit; the sign is consumed by the label, not shown.
    /// <see cref="ShowPower"/> is the row's whole visibility, so a battery that reports no rate shows no row
    /// rather than a lying zero. Mirrors the health/cycles pair above.</summary>
    [ObservableProperty] private string _power = "";
    [ObservableProperty] private string _powerLabel = "";
    [ObservableProperty] private bool _showPower;

    /// <summary>Replace the three charging rows' construction-time placeholders with the real hardware values,
    /// once, right after the UI is built — off the UI thread, on each row's own serial worker. Mirrors
    /// <see cref="OptionsViewModel.Prime"/>, and is called from <c>AppController</c> rather than from this
    /// constructor for the same two reasons: the ordering guarantee at the top of <c>AppController</c>
    /// (persisted device state re-applied BEFORE any row reads it) stays explicit and greppable, and a test can
    /// assert that construction alone performs no device read.
    ///
    /// This is a PRIME, not an adoption (docs/state-and-events.md), and the distinction is load-bearing here:
    /// the app stores no value for any of these three rows — nothing in <c>Settings</c> holds a charge limit, a
    /// calibration flag or a charge mode — so there is no intent of ours to re-apply and the hardware is the
    /// only source there is. A read that fails leaves the row on its placeholder, which is what a failed read
    /// showed before the deferral too. Only a UI edit or an out-of-band input is an event.</summary>
    public void Prime()
    {
        Limit?.Prime();
        Calibration?.Prime();
        Mode?.Prime();
    }

    public void Update(BatteryInfoSnapshot s)
    {
        Charge = s.Percent < 0 ? "—" : $"{s.Percent}%";
        State = s.State switch
        {
            BatteryState.Charging    => Loc.T("Charging"),
            BatteryState.Discharging => Loc.T("On battery"),
            BatteryState.Idle        => Loc.T("Plugged in"),
            _                        => "",
        };
        Health = s.HealthPercent < 0 ? "" : $"{s.HealthPercent}%";
        ShowHealth = s.HealthPercent >= 0;
        Cycles = s.CycleCount < 0 ? "" : s.CycleCount.ToString();
        ShowCycles = s.CycleCount >= 0;

        // Power: signed in the domain (Domain/Models.cs), so the row is decided entirely here — a negative
        // reading is the draw on battery, anything positive is the charge rate on AC. Zero and null both mean
        // "nothing to report" and hide the row, which is the one case where the two are the same answer.
        if (s.PowerWatts is { } w && w != 0)
        {
            PowerLabel = Loc.T(w < 0 ? "Power draw" : "Charging power");
            Power = Loc.T("{0:0.0} W", Math.Abs(w));
            ShowPower = true;
        }
        else
        {
            PowerLabel = "";
            Power = "";
            ShowPower = false;
        }
    }
}
