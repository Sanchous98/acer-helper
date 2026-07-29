using AcerHelper;
using AcerHelper.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>CPU-undervolt section: one Curve-Optimizer offset slider per independently tunable voltage domain (on a
/// hybrid part, "Zen 5" and "Zen 5c" — separate rails measured near 1.17 V and 1.02 V, so one number for both is pinned
/// by whichever gives out first), or a single all-core slider on a CPU with one domain. Per-CORE is deliberately not
/// offered: within a rail the delivered voltage follows the mildest core's request, so all but one core per cluster
/// would be inert. Applies on change (debounced) and persists PER performance mode — switching mode reloads that
/// mode's offsets (see
/// <see cref="Load"/>), and an unconfigured mode is stock. Only built when the device exposes an
/// <see cref="Features.ICurveOptimizer"/> port.
///
/// Shaped like <see cref="GpuViewModel"/>, with one difference that matters: applying is slow here (an SMU mailbox
/// transaction per core slot, waiting on a machine-wide lock), so the <c>apply</c> delegate this receives is expected
/// to hand the work off a thread itself — see AppController.SetCo. Nothing in this class may block. All rows are
/// applied together on one debounce tick rather than per row, so dragging one slider does not re-write the others'
/// cores one transaction at a time.</summary>
public sealed partial class CoViewModel : SectionViewModel
{
    private readonly Action<int[]> _apply;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _loading;

    public string CpuName { get; }

    /// <summary>One row per voltage domain, in the port's display order — the same order the offsets are handed back to
    /// the service in, so the two must not be re-sorted independently.</summary>
    public IReadOnlyList<CoRowViewModel> Rows { get; }

    public CoViewModel(string name, (int Min, int Max) range, double millivoltsPerCount,
                       IReadOnlyList<string> domainLabels, IReadOnlyList<int> initial, Action<int[]> apply)
    {
        _loading = true;
        _apply = apply;
        CpuName = name;
        _debounce.Tick += (_, _) => { _debounce.Stop(); Apply(); };

        // A domain label is an AMD architecture name, which is not translated, so it is shown as-is; the single-domain
        // case has nothing to name the row after and uses the localized section word instead.
        var rows = new List<CoRowViewModel>(domainLabels.Count);
        for (var i = 0; i < domainLabels.Count; i++)
            rows.Add(new CoRowViewModel(domainLabels[i], range, millivoltsPerCount,
                                        i < initial.Count ? initial[i] : 0, Debounce));
        if (rows.Count == 0)
            rows.Add(new CoRowViewModel(Loc.T("Undervolt"), range, millivoltsPerCount,
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
    /// mode is dropped so it can't fire the new mode's values and re-persist them.</summary>
    public void Load(IReadOnlyList<int> counts)
    {
        _debounce.Stop();
        _loading = true;
        for (var i = 0; i < Rows.Count; i++) Rows[i].Offset = i < counts.Count ? counts[i] : 0;
        _loading = false;
    }

    private void Debounce()
    {
        if (_loading) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Apply()
    {
        var counts = new int[Rows.Count];
        for (var i = 0; i < Rows.Count; i++) counts[i] = (int)Rows[i].Offset;
        _apply(counts);
    }
}

/// <summary>One voltage domain's slider row. The label is an AMD architecture name ("Zen 5c"), which is not translated;
/// the value reads out as the AVFS step count with the millivolts it works out to in brackets.</summary>
public sealed partial class CoRowViewModel : ObservableObject
{
    private readonly double _mvPerCount;
    private readonly Action _changed;

    public string Label { get; }
    public int OffsetMin { get; }
    public int OffsetMax { get; }

    public CoRowViewModel(string label, (int Min, int Max) range, double millivoltsPerCount, int initial,
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

    partial void OnOffsetChanged(double value) { OffsetLabel = Fmt(value); _changed(); }

    // AVFS step count first — it is what the hardware takes and what every tool and write-up talks in — with the
    // millivolts it works out to in brackets, since that is the unit an undervolt is actually thought about in. The
    // mV figure carries "≈" on purpose: a count is only approximately a fixed voltage, because the offset shifts the
    // whole V/F curve rather than clamping a voltage, so the delivered delta moves with frequency and temperature.
    // Stock reads as a plain 0.
    private string Fmt(double counts)
    {
        var steps = (int)counts;
        return steps == 0 ? "0" : $"{steps} (≈{(int)Math.Round(steps * _mvPerCount)} mV)";
    }
}
