using AcerHelper.Domain;
using AcerHelper.Infrastructure;
using AcerHelper.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>CPU-undervolt section: one Curve-Optimizer offset slider per independently tunable voltage domain
/// (on a hybrid part, "Zen 5" and "Zen 5c" — separate rails measured near 1.17 V and 1.02 V, so one number for
/// both is pinned by whichever gives out first — and on an APU also the iGPU), or a single all-core slider on
/// a CPU with one domain. Per-CORE is deliberately not offered: within a rail the delivered voltage follows
/// the mildest core's request, so all but one core per cluster would be inert. Applies on change (debounced)
/// and persists PER performance mode — switching mode reloads that mode's offsets (see
/// <see cref="Load"/>), and an unconfigured mode is stock. Only built when the device exposes an
/// <see cref="ICurveOptimizer"/> port.
///
/// Shaped like <see cref="GpuViewModel"/>, with one difference that matters: applying is slow here (an SMU
/// mailbox transaction per core slot, waiting on a machine-wide lock), so the <c>apply</c> delegate this
/// receives is expected to hand the work off a thread itself — see AppController.SetCo. Nothing in this class
/// may block. All rows are applied together on one debounce tick rather than per row, so dragging one slider
/// does not re-write the others' cores one transaction at a time.
/// See docs/curve-optimizer-strix-point.md.</summary>
public sealed partial class CoViewModel : SectionViewModel
{
    private readonly Action<int[]> _apply;
    private readonly PeriodicSchedule _debounce;
    private bool _loading;
    private int[]? _previewSnapshot;

    /// <summary>True while the guided sweep owns the SMU. The sliders are disabled (the view binds
    /// <see cref="SlidersEnabled"/>) and a late debounce is refused, so a manual edit cannot land between two of
    /// the sweep's probes. The service's own gate is the second line of defence.</summary>
    [ObservableProperty] private bool _sweepRunning;

    /// <summary>True while a sweep proposal is being PREVIEWED on the sliders. The preview is NOT a commit: the
    /// thumbs read the proposed counts (accented), but nothing is written and nothing is persisted — the only
    /// actions are Save (which commits through <see cref="Load"/>) and Discard (which restores
    /// <see cref="_previewSnapshot"/>). While it shows, the sliders are disabled so a drag cannot commit an
    /// unverified offset.</summary>
    [ObservableProperty] private bool _hasPreview;

    public bool SlidersEnabled => !SweepRunning && !HasPreview;

    partial void OnSweepRunningChanged(bool value) => OnPropertyChanged(nameof(SlidersEnabled));
    partial void OnHasPreviewChanged(bool value) => OnPropertyChanged(nameof(SlidersEnabled));

    /// <summary>Called by the sweep card when a run starts and when it ends (the finally), so the sliders are
    /// never left locked if the run throws.</summary>
    public void SetSweepRunning(bool running) => SweepRunning = running;

    public string CpuName { get; }

    /// <summary>One row per voltage domain, in the port's display order — the same order the offsets are handed back to
    /// the service in, so the two must not be re-sorted independently.</summary>
    public IReadOnlyList<CoRowViewModel> Rows { get; }

    public CoViewModel(string name, (int Min, int Max) range, double millivoltsPerCount,
                       IReadOnlyList<VoltageDomain> domains, IReadOnlyList<int> initial, Action<int[]> apply)
    {
        _loading = true;
        _apply = apply;
        CpuName = name;
        _debounce = new PeriodicSchedule(ApplyDebounced, TimeSpan.FromMilliseconds(400), UiSchedule.Normal);

        // A domain label is an architecture name or a technical abbreviation ("Zen 5c", "iGPU"), neither of which is
        // translated, so it is shown as-is; the single-domain case has nothing to name the row after and uses the
        // localized section word instead.
        //
        // Range and mV come from the DOMAIN where it states them, because the rails genuinely differ: measured on this
        // hardware the graphics rail moves 5 mV per count against the cores' 2.5, and its slider runs deeper. A domain
        // that states no scale (null) shows bare steps instead of a borrowed, wrong voltage.
        var rows = new List<CoRowViewModel>(domains.Count);
        for (var i = 0; i < domains.Count; i++)
            rows.Add(new CoRowViewModel(domains[i].Label, domains[i].Range ?? range, domains[i].MillivoltsPerCount,
                                        i < initial.Count ? initial[i] : 0, Debounce));
        if (rows.Count == 0)
            rows.Add(new CoRowViewModel(Loc.T("uv.title"), range, millivoltsPerCount,
                                        initial.Count > 0 ? initial[0] : 0, Debounce));
        Rows = rows;
        _loading = false;
    }

    /// <summary>Back to stock on every domain. Setting the properties fires the debounced apply, so this persists +
    /// applies just like dragging each slider to zero — and it is the in-app way out of an offset that turned out to
    /// be unstable.</summary>
    [RelayCommand]
    private void Reset()
    {
        foreach (var r in Rows) r.Offset = 0;
    }

    /// <summary>Reflect a mode's saved offsets without triggering apply/persist (the service already set the hardware
    /// on the mode switch). The <c>_loading</c> guard neuters the change hooks; a pending debounce from the PREVIOUS
    /// mode is dropped so it can't fire the new mode's values and re-persist them. This is also the COMMIT path for a
    /// previewed proposal (Save reaches here with the saved counts), so it clears the preview marks and flag.</summary>
    public void Load(IReadOnlyList<int> counts)
    {
        _debounce.Stop();
        _loading = true;
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].Offset = i < counts.Count ? counts[i] : 0;
            Rows[i].IsProposed = false;
        }
        _loading = false;
        _previewSnapshot = null;
        HasPreview = false;
    }

    /// <summary>Show a sweep's proposal ON the sliders without committing it. The rows' current offsets are
    /// snapshotted first (so <see cref="DiscardPreview"/> can put them back exactly), then only the listed indices
    /// move — to the proposed value clamped into that row's own range — and are marked proposed. The <c>_loading</c>
    /// guard is reused so the moves do NOT arm the debounce: no SMU write, no settings write. The iGPU is never in
    /// <paramref name="proposed"/> (the sweep excludes it), so its row is left untouched.</summary>
    public void Preview(IReadOnlyList<(int Index, int Value)> proposed)
    {
        _debounce.Stop();
        var snapshot = new int[Rows.Count];
        for (var i = 0; i < Rows.Count; i++) snapshot[i] = (int)Rows[i].Offset;
        _previewSnapshot = snapshot;

        _loading = true;
        foreach (var (index, value) in proposed)
        {
            if (index < 0 || index >= Rows.Count) continue;
            Rows[index].Offset = Math.Clamp(value, Rows[index].OffsetMin, Rows[index].OffsetMax);
            Rows[index].IsProposed = true;
        }
        _loading = false;
        HasPreview = true;
    }

    /// <summary>Drop a preview and put the sliders back on the pre-preview committed values. Nothing is written —
    /// the machine is already on them (the sweep's finally restored the pre-sweep state), so this only moves the
    /// thumbs and clears the proposal marks.</summary>
    public void DiscardPreview()
    {
        if (_previewSnapshot is { } snapshot)
        {
            _debounce.Stop();
            _loading = true;
            for (var i = 0; i < Rows.Count; i++)
            {
                if (i < snapshot.Length) Rows[i].Offset = snapshot[i];
                Rows[i].IsProposed = false;
            }
            _loading = false;
        }
        _previewSnapshot = null;
        HasPreview = false;
    }

    private void Debounce()
    {
        // The sweep owns the SMU while it runs, and a proposal must not be committed by a stray drag: refuse both.
        if (_loading || SweepRunning || HasPreview) return;
        _debounce.Restart();
    }

    private void Apply()
    {
        if (SweepRunning || HasPreview) return;  // defence in depth for a debounce armed before the sweep/preview
        var counts = new int[Rows.Count];
        for (var i = 0; i < Rows.Count; i++) counts[i] = (int)Rows[i].Offset;
        _apply(counts);
    }

    // The debounce tick: stop the (periodic) schedule first, so a late tick cannot re-enter, then apply once.
    private void ApplyDebounced() { _debounce.Stop(); Apply(); }
}

/// <summary>One voltage domain's slider row. The label is an architecture name or abbreviation ("Zen 5c", "iGPU"),
/// which is not translated; the value reads out as the AVFS step count, with the millivolts it works out to in
/// brackets ONLY where that rail's scale is known.</summary>
public sealed partial class CoRowViewModel : ObservableObject
{
    private readonly double? _mvPerCount;
    private readonly Action _changed;

    public string Label { get; }
    public int OffsetMin { get; }
    public int OffsetMax { get; }

    public CoRowViewModel(string label, (int Min, int Max) range, double? millivoltsPerCount, int initial,
                          Action changed)
    {
        Label = label;
        OffsetMin = range.Min; OffsetMax = range.Max;
        _mvPerCount = millivoltsPerCount;
        _changed = changed;
        _offset = Math.Clamp(initial, OffsetMin, OffsetMax);
        _offsetLabel = Fmt(_offset);
    }

    [ObservableProperty] private double _offset;
    [ObservableProperty] private string _offsetLabel;

    /// <summary>True while this row shows a previewed sweep PROPOSAL at this value. The view accents the value
    /// with <c>AccentBrush</c> when it is set, so a moved thumb reads as "proposed, not committed". Cleared by
    /// <see cref="CoViewModel.Load"/> (Save) or <see cref="CoViewModel.DiscardPreview"/>.</summary>
    [ObservableProperty] private bool _isProposed;

    partial void OnOffsetChanged(double value) { OffsetLabel = Fmt(value); _changed(); }

    // AVFS step count first — it is what the hardware takes and what every tool and write-up talks in — with the
    // millivolts it works out to in brackets, since that is the unit an undervolt is actually thought about in.
    // The mV figure carries "≈" on purpose: a count is only approximately a fixed voltage, because the offset
    // shifts the whole V/F curve rather than clamping a voltage, so the delivered delta moves with frequency and
    // temperature. Stock reads as a plain 0.
    //
    // Each rail brings its OWN scale — 2.5 mV a count on the cores, 5 on the graphics rail — so the arithmetic is
    // per row rather than per section. A rail that has never been measured passes null and shows the bare count:
    // steps are the hardware's own unit and remain usable, which beats printing a millivolt figure borrowed from a
    // different rail's measurement, since that would look authoritative and be wrong.
    // See docs/curve-optimizer-strix-point.md.
    private string Fmt(double counts)
    {
        var steps = (int)counts;
        if (steps == 0) return "0";
        return _mvPerCount is { } mv ? $"{steps} (≈{(int)Math.Round(steps * mv)} mV)" : $"{steps}";
    }
}
