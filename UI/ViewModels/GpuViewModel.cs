using AcerHelper.Domain;
using AcerHelper.Infrastructure;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>GPU-overclock section: a core-clock and a memory-clock offset slider (MHz), each bounded by the
/// range the driver reports, plus a GPU POWER LEVEL selector when the machine exposes the EC envelope channel.
/// The offsets apply on change (debounced) and persist PER performance mode — switching mode reloads that
/// mode's values (see <see cref="Load"/>), and an unconfigured mode is stock 0/0. The power level persists per
/// mode the same way; it is applied on selection (not debounced — a pick is discrete, unlike a slider drag) and
/// its default is <em>follow the profile</em>, which is the old behaviour. Only built when the device exposes an
/// <see cref="IGpuOverclock"/> port (an NVIDIA dGPU that allows tuning).
///
/// THE POWER ROWS ARE THE EC'S FIXED LEVELS, not arbitrary wattage — the machine has no register for a number,
/// and the port that owns the channel declares the rows and their measured watts (see
/// <see cref="GpuPowerOption"/>). When the channel is absent the port offers no levels and the selector is
/// hidden, exactly like every other probe-and-hide control here.</summary>
public sealed partial class GpuViewModel : SectionViewModel
{
    private readonly Action<int, int> _set;                 // (core MHz, mem MHz) -> apply + persist for the current mode
    private readonly Action<GpuPowerLevel?> _setPower;      // level, or null = follow the profile
    private readonly GpuPowerLevel?[] _levels;              // index-aligned with PowerNames[1..]; PowerNames[0] is the default
    private readonly PeriodicSchedule _debounce;
    private bool _loading;

    public string GpuName { get; }
    public int CoreMin { get; }
    public int CoreMax { get; }
    public int MemMin { get; }
    public int MemMax { get; }

    /// <summary>Whether this machine exposes the GPU-power envelope channel at all. False hides the selector:
    /// the rows are the EC's fixed levels, and a machine without the channel cannot enforce any of them.</summary>
    public bool HasPower { get; }

    /// <summary>Localized dropdown labels: index 0 is <em>follow the profile</em> (the default, and the old
    /// behaviour), and each following entry is one EC row named after its profile with its measured steady limit
    /// in watts. Empty when <see cref="HasPower"/> is false.</summary>
    public IReadOnlyList<string> PowerNames { get; }

    public GpuViewModel(string name, (int Min, int Max) coreRange, (int Min, int Max) memRange,
                        IReadOnlyList<GpuPowerOption> powerLevels, GpuAxisState initial,
                        Action<int, int> set, Action<GpuPowerLevel?> setPower)
    {
        _loading = true;
        _set = set;
        _setPower = setPower;
        GpuName = name;
        CoreMin = coreRange.Min; CoreMax = coreRange.Max;
        MemMin = memRange.Min; MemMax = memRange.Max;
        _levels = [.. powerLevels.Select(o => (GpuPowerLevel?)o.Level)];
        HasPower = _levels.Length > 0;
        PowerNames = HasPower
            ? [Loc.T("oc.gpu_power_follow"), .. powerLevels.Select(o => $"{Loc.T(o.LabelKey)} ({o.Watts} W)")]
            : [];
        _debounce = new PeriodicSchedule(ApplyDebounced, TimeSpan.FromMilliseconds(400), UiSchedule.Normal);

        _core = Math.Clamp(initial.Core, CoreMin, CoreMax);
        _mem = Math.Clamp(initial.Mem, MemMin, MemMax);
        _powerIndex = IndexOfLevel(initial.Power);
        _coreText = Fmt(_core);
        _memText = Fmt(_mem);
        _loading = false;
    }

    [ObservableProperty] private double _core;
    [ObservableProperty] private double _mem;

    /// <summary>The exact-entry text beside each slider — a plain signed MHz number ("+275", "-150", "0"), with
    /// the unit shown as a separate label so editing is just editing the number. Typing is the answer to the
    /// slider's coarse resolution: at the driver's full range a 2000 MHz core span sits on ~250 px of track, so a
    /// drag can only land within several MHz — a typed number is exact.
    ///
    /// THE BOX IS A COMMIT-TARGET, NOT A LIVE SOURCE: the parse+clamp happens in <see cref="ApplyCoreText"/> /
    /// <see cref="ApplyMemText"/> (Enter / lost focus), never on every keystroke, so a half-typed "-" or "1" does
    /// not drag the slider around mid-edit. A value out of range is CLAMPED (like the slider itself) rather than
    /// refused, and the box is rewritten to the clamped result so what it shows is what was applied.</summary>
    [ObservableProperty] private string _coreText;
    [ObservableProperty] private string _memText;

    /// <summary>The selected power row: 0 is <em>follow the profile</em>, and 1..N index <c>_levels</c>. A pick
    /// is applied immediately — no debounce — because it is a discrete change, not a slider drag; the EC write
    /// is enqueue-only anyway, so it lands on the controller's writer thread.</summary>
    [ObservableProperty] private int _powerIndex;

    partial void OnCoreChanged(double value) { CoreText = Fmt(value); Debounce(); }
    partial void OnMemChanged(double value)  { MemText = Fmt(value); Debounce(); }
    partial void OnPowerIndexChanged(int value) { if (!_loading) _setPower(LevelAt(value)); }

    /// <summary>Reset the offsets to stock (0/0). Setting the properties fires the debounced apply, so this
    /// persists + applies just like a drag to zero. The power choice is deliberately NOT reset: it is a separate
    /// control with its own value, and "Reset" is about the clock offsets the button sits beside.</summary>
    [RelayCommand]
    private void Reset()
    {
        Core = 0;
        Mem = 0;
    }

    /// <summary>Reflect a mode's saved offsets and power choice without triggering apply/persist (the service
    /// already set the hardware on the mode switch). The <c>_loading</c> guard neuters the change hooks; a pending
    /// debounce from the PREVIOUS mode is dropped so it can't fire the new mode's values and re-persist them.
    ///
    /// The value is the DOMAIN's <see cref="GpuAxisState"/>, for the same reason as its fan twin: this Load is
    /// reached from the build path and from the re-apply outcome, and the outcome crosses into Application,
    /// which may not name the stored preset.</summary>
    public void Load(GpuAxisState state)
    {
        _debounce.Stop();
        _loading = true;
        Core = Math.Clamp(state.Core, CoreMin, CoreMax);
        Mem = Math.Clamp(state.Mem, MemMin, MemMax);
        PowerIndex = IndexOfLevel(state.Power);
        _loading = false;
    }

    private void Debounce()
    {
        if (_loading) return;
        _debounce.Restart();
    }

    /// <summary>Commit the typed exact core offset (Enter / lost focus): parse, clamp to the slider's range, apply.</summary>
    public void ApplyCoreText()
    {
        if (ParseMhz(CoreText, out var value)) Core = Math.Clamp(value, CoreMin, CoreMax);
        else CoreText = Fmt(Core);   // unparsable -> snap the box back to what is actually applied
    }

    /// <summary>Commit the typed exact memory offset (Enter / lost focus).</summary>
    public void ApplyMemText()
    {
        if (ParseMhz(MemText, out var value)) Mem = Math.Clamp(value, MemMin, MemMax);
        else MemText = Fmt(Mem);
    }

    /// <summary>Parse a MHz entry: an optional sign, digits, and an optional trailing unit/non-digits. Returns
    /// false for anything that carries no number at all (empty, "abc", a lone "-"). Tolerates a leading "+", a
    /// Unicode minus and spaces, and ignores a trailing unit, so a paste of the label ("+250 MHz") still works.</summary>
    internal static bool ParseMhz(string? text, out int mhz)
    {
        mhz = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().Replace('\u2212', '-');   // U+2212 MINUS SIGN -> ASCII
        var end = 0;
        while (end < s.Length && (s[end] == '+' || s[end] == '-' || s[end] == ' ' || char.IsDigit(s[end]))) end++;
        return int.TryParse(s[..end].Replace(" ", ""), out mhz);
    }

    private void Apply() => _set((int)Core, (int)Mem);

    // The debounce tick: stop the (periodic) schedule first, so a late tick cannot re-enter, then apply once.
    private void ApplyDebounced() { _debounce.Stop(); Apply(); }

    /// <summary>The dropdown index a saved choice maps to: 0 for "no override", otherwise one past the level's
    /// position. A level the port does not offer — impossible while the port declares the rows, but a stale
    /// value off a hand-edited file is refused by <c>GpuPowerLevels</c> before it can arrive — reads as the
    /// default rather than as an invented row.</summary>
    private int IndexOfLevel(GpuPowerLevel? level)
    {
        if (level is not { } chosen) return 0;
        var i = Array.IndexOf(_levels, chosen);
        return i >= 0 ? i + 1 : 0;
    }

    /// <summary>The level a dropdown index names, or null for index 0 / out of range (follow the profile).</summary>
    private GpuPowerLevel? LevelAt(int index)
        => index >= 1 && index <= _levels.Length ? _levels[index - 1] : null;

    private static string Fmt(double mhz) => $"{(mhz > 0 ? "+" : "")}{(int)mhz}";
}
