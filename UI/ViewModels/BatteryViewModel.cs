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
        if (limit != null) Limit = new ToggleRowViewModel(limit, post);
        if (calibration != null) Calibration = new ToggleRowViewModel(calibration, post);
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
    }
}
