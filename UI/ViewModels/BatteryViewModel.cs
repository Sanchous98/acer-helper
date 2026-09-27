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
                            Action<Action>? post = null, Func<PowerSource>? powerSource = null)
    {
        ShowInfo = hasInfo;
        _powerSource = powerSource;
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

    // The power-source read op, carried from the device when it exposes one (Battery.PowerSource), null
    // otherwise — a machine without the EC channel therefore has no schedule behind this row and never shows it.
    private readonly Func<PowerSource>? _powerSource;

    public ToggleRowViewModel? Limit { get; }
    public ToggleRowViewModel? Calibration { get; }
    public ChoiceRowViewModel? Mode { get; }
    public bool ShowLimit => Limit != null;
    public bool ShowCalibration => Calibration != null;
    public bool ShowMode => Mode != null;

    private bool _calibrationSyncing;   // one out-of-band calibration read in flight; later refreshes wait for it

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

    /// <summary>The power-source row: which source is powering the machine right now — the DC-in barrel/plaque
    /// charger, USB Power Delivery over Type-C, or the battery. Its label is fixed ("Power source") and its value
    /// is the named source. This is a DIFFERENT, costlier transport from the live power rate above (a vendor
    /// transaction, e.g. an Acer EC HID read, not the OS gauge), and it is filled on its own slow schedule,
    /// <c>PowerSourceSchedule</c>, through <see cref="SetPowerSource"/> rather than by <see cref="Update"/>.
    /// The row exists only when the device
    /// exposes the property AND the read resolves to a named source; <see cref="PowerSource.Unknown"/> — a failed
    /// read, or an unmapped code — hides it rather than showing a made-up source.</summary>
    [ObservableProperty] private string _sourceLabel = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private bool _showSource;

    /// <summary>True when this machine exposes the power-source read at all (the device declared the property).
    /// The app uses it to decide whether to build the slow <c>PowerSourceSchedule</c> — a machine without the
    /// EC channel gets no schedule and never shows the row.</summary>
    public bool HasPowerSource => _powerSource != null;

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
            BatteryState.Charging    => Loc.T("bat.charging"),
            BatteryState.Discharging => Loc.T("bat.on_battery"),
            BatteryState.Idle        => Loc.T("bat.plugged_in"),
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
            PowerLabel = Loc.T(w < 0 ? "bat.power_draw" : "bat.charging_power");
            Power = Loc.T("bat.watts", Math.Abs(w));
            ShowPower = true;
        }
        else
        {
            PowerLabel = "";
            Power = "";
            ShowPower = false;
        }

        ReconcileCalibration();
    }

    /// <summary>Apply one power-source reading to the row. Called from the slow <c>PowerSourceSchedule</c>
    /// tick on the UI thread, with the value the op already read off the UI thread. <see cref="PowerSource.Unknown"/>
    /// and a machine with no op both leave the row hidden: a failed read must not invent a source. A mapped source
    /// sets the fixed label and the translated name.</summary>
    public void SetPowerSource(PowerSource source)
    {
        // Gate on the OP, not just the value: a machine without the property must never show the row, even if
        // some caller hands a value in. The app also never builds the schedule for such a machine, so this is the
        // second half of the same "presence is the property" rule.
        if (_powerSource == null || source == PowerSource.Unknown)
        {
            SourceLabel = "";
            Source = "";
            ShowSource = false;
            return;
        }

        SourceLabel = Loc.T("bat.power_source");
        Source = Loc.T(source switch
        {
            PowerSource.Barrel => "bat.source_ac",
            PowerSource.UsbC   => "bat.source_usbc",
            PowerSource.Battery => "bat.source_battery",
            _                  => "",   // unreachable: Unknown returned above
        });
        ShowSource = true;
    }

    /// <summary>Re-read the calibration row from the firmware on the periodic refresh, so the switch tracks
    /// reality when the CYCLE ENDS ON ITS OWN. Calibration is firmware-owned and can run for hours; when it
    /// finishes the firmware clears its own flag and restores the ~80% cap, with no write of ours to fire the
    /// row's usual verified read-back (that read-back is attached to OUR write, see the constructor). This is
    /// the same <see cref="ToggleRowViewModel.Sync"/> read-back the limit row already uses, now also driven by
    /// the existing one-second battery refresh rather than only by a click or a restart.
    ///
    /// ONLY WHILE THE ROW SHOWS ON, which is what keeps the cheap battery poll cheap: the calibration status is
    /// a WMI transaction (the fast path deliberately avoids the WMI/EC gate), and reading it every second
    /// forever would be exactly the sweep that path exists not to be. While a calibration is active the read
    /// costs one WMI call a second and stops the moment the row settles off. And it is skipped while a change
    /// the user just asked for is pending (<see cref="ToggleRowViewModel.IsPending"/>) and coalesced to one read
    /// in flight, so a refresh can never race a click's confirmation/write and fight it.
    ///
    /// A CHANGE ALSO RE-READS THE CHARGE LIMIT, because the firmware restores the cap its start lifted — the
    /// same pairing the write path performs in the constructor. The reverse (a calibration started behind the
    /// app's back) is not chased on this path, for the cost reason above: the row is settled off then, so there
    /// is nothing a per-second WMI read would buy without also being paid while no calibration exists.</summary>
    public void ReconcileCalibration()
    {
        if (_calibrationSyncing) return;
        if (Calibration is not { IsOn: true, IsPending: false }) return;
        _calibrationSyncing = true;
        Calibration.Sync(onChanged: () => Limit?.Sync(), settled: () => _calibrationSyncing = false);
    }
}
