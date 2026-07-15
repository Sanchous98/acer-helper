using System.Collections.ObjectModel;
using AcerHelper.Features;
using AcerHelper.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>Serializes one control's hardware writes and runs them off the UI thread, preserving click
/// order (each queued action continues the previous). A control's write + its readback run as one unit, so
/// a rapid burst applies in order and the final readback reflects the last write.</summary>
internal sealed class HwSerial
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    public void Enqueue(Action work)
    {
        lock (_gate)
            _tail = _tail.ContinueWith(_ => { try { work(); } catch { /* surfaced via readback/notify */ } },
                                       TaskScheduler.Default);
    }
}

/// <summary>The write -> read-back -> snap-back dance shared by the option rows and the plain-backlight
/// slider, in one place: serialize each write off the UI thread (order preserved via <see cref="HwSerial"/>),
/// then re-read the hardware and correct the control to the real value — UNLESS a newer request superseded
/// this one (the desired latch) or the UI already shows it. Each caller keeps its own control-specific bits:
/// how it reads the current UI value (<c>current</c>) and how it writes the corrected one (<c>snapBack</c> —
/// e.g. a syncing-guarded property set). <paramref name="post"/> is the UI-thread marshaller (defaults to
/// Dispatcher.UIThread.Post); passing a synchronous poster lets this run under a plain unit test.</summary>
internal sealed class VerifiedHwValue<T>(Action<Action>? post = null)
{
    private static readonly EqualityComparer<T> Eq = EqualityComparer<T>.Default;
    private readonly HwSerial _hw = new();
    private readonly Action<Action> _post = post ?? (a => Dispatcher.UIThread.Post(a));
    private T _desired = default!;

    /// <summary>Record the latest intended value WITHOUT writing it (e.g. an optimistic UI flip awaiting a
    /// confirm dialog), so a still-pending read-back from an earlier write can tell it's been superseded.</summary>
    public void Latch(T desired) => _desired = desired;

    /// <summary>Write <paramref name="desired"/> off-thread, read back, and snap the control to the actual
    /// value via <paramref name="snapBack"/> — unless a newer Apply/Latch superseded it or
    /// <paramref name="current"/> already equals it.</summary>
    public void Apply(T desired, Action<T> write, Func<T> read, Func<T> current, Action<T> snapBack)
    {
        _desired = desired;
        _hw.Enqueue(() =>
        {
            write(desired);
            var actual = read();
            _post(() =>
            {
                if (!Eq.Equals(desired, _desired) || Eq.Equals(actual, current())) return;   // superseded / matches
                snapBack(actual);
            });
        });
    }

    /// <summary>Re-read the hardware (no write) and snap the control to it if it differs — for out-of-band
    /// changes (e.g. an Fn key). Not gated on the latch: a genuine external change should always show.</summary>
    public void Sync(Func<T> read, Func<T> current, Action<T> snapBack)
        => _hw.Enqueue(() =>
        {
            var actual = read();
            _post(() => { if (!Eq.Equals(actual, current())) snapBack(actual); });
        });
}

/// <summary>Options section: hardware toggles/choices plus clamshell, Turbo-key behaviour and
/// autostart — whichever the device exposes. <see cref="TryCreate"/> returns null when there is
/// nothing to show, so the shell can omit the section.</summary>
public sealed class OptionsViewModel : SectionViewModel
{
    public ObservableCollection<ObservableObject> Rows { get; } = [];

    public static OptionsViewModel? TryCreate(IDevice device, OptionsSection o)
    {
        var vm = new OptionsViewModel();

        // Language selector — app-level, so always present (this is why the Options drawer never disappears
        // now). Endonyms stay in their own language; only "System" is translated. Picking one rebuilds the
        // whole UI live in the new language (see AppController.SetLanguage).
        AppLanguage[] langValues = [AppLanguage.System, AppLanguage.English, AppLanguage.Russian];
        string[] langNames = [Loc.T("System"), "English", "Русский"];
        var langIndex = Math.Max(0, Array.IndexOf(langValues, o.Language));
        vm.Rows.Add(new ChoiceRowViewModel(new OptionChoice(Loc.T("Language"), true, langNames, langIndex,
            i => o.SetLanguage(langValues[i]))));

        foreach (var t in o.HwToggles) vm.Rows.Add(new ToggleRowViewModel(t));
        foreach (var c in o.HwChoices) vm.Rows.Add(new ChoiceRowViewModel(c));

        // Enabled state read straight off the port (we have the device) — no separate Func needed.
        if (device.Clamshell is { } clam)
            vm.Rows.Add(new ToggleRowViewModel(Loc.T(clam.Label), clam.Enabled, true, o.SetClamshell));

        if (device.PowerProfiles?.All.Any(p => p.Kind == ProfileKind.Turbo) ?? false)
            vm.Rows.Add(new ToggleRowViewModel(Loc.T("Turbo key toggles Turbo"), o.TurboToggles, true, o.SetTurboToggles,
                tip: Loc.T("Otherwise the Turbo key cycles through profiles.")));

        if (device.Autostart is { } auto)
            vm.Rows.Add(new ToggleRowViewModel(Loc.T(auto.Label), auto.IsEnabled(), true, o.SetAutostart));

        return vm.Rows.Count > 0 ? vm : null;
    }
}

/// <summary>An on/off option row. A toggle that needs confirmation reverts itself if the user
/// declines (the setter vetoes and re-notifies, so the switch snaps back). WMI-backed toggles verify:
/// after the write they re-read the hardware and snap the switch to the real state (see <see cref="Apply"/>).</summary>
public sealed class ToggleRowViewModel : ObservableObject
{
    private readonly Action<bool> _onChange;
    private readonly Func<bool>? _read;
    private readonly Func<bool>? _confirm;
    private readonly Func<Task<bool>>? _confirmAsync;
    private readonly VerifiedHwValue<bool> _hw = new();
    private bool _isOn;

    public ToggleRowViewModel(OptionToggle t)
        : this(t.Label, t.Initial, t.Supported, t.OnChange, read: t.Read, confirm: t.Confirm, confirmAsync: t.ConfirmAsync) { }

    public ToggleRowViewModel(string label, bool initial, bool enabled, Action<bool> onChange,
                              string? tip = null, Func<bool>? read = null,
                              Func<bool>? confirm = null, Func<Task<bool>>? confirmAsync = null)
    {
        Label = label;
        IsEnabled = enabled;
        Tip = tip;
        _isOn = initial;
        _hw.Latch(initial);
        _onChange = onChange;
        _read = read;
        _confirm = confirm;
        _confirmAsync = confirmAsync;
    }

    public string Label { get; }
    public bool IsEnabled { get; }
    public string? Tip { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (value == _isOn) return;

            // Synchronous veto: ask before committing, snap back if declined.
            if (value && _confirm != null && !_confirm())
            {
                OnPropertyChanged();   // rejected -> re-notify so the switch snaps back to off
                return;
            }

            // Async veto (e.g. a modal dialog): show ON now, then confirm and revert if declined.
            if (value && _confirmAsync != null)
            {
                SetProperty(ref _isOn, true);
                _hw.Latch(true);   // so a pending earlier read-back sees this optimistic flip
                _ = ConfirmAndApplyAsync();
                return;
            }

            SetProperty(ref _isOn, value);
            Apply(value);
        }
    }

    /// <summary>Write the value; then, for a readable (WMI) option, re-read the hardware and snap the switch
    /// to the real state — so a write that silently didn't take corrects itself. The write + readback run
    /// serialized off the UI thread; a correction is discarded if a newer click has since changed the intent
    /// (the desired latch inside <see cref="VerifiedHwValue{T}"/>), so a fast burst never fights the user.
    /// Non-readable options apply inline.</summary>
    private void Apply(bool desired)
    {
        if (_read == null) { _hw.Latch(desired); _onChange(desired); return; }   // non-readable: apply inline
        _hw.Apply(desired, _onChange, _read, () => _isOn, actual =>
        {
            _isOn = actual;                   // reflect reality (honest snap-back)
            OnPropertyChanged(nameof(IsOn));
        });
    }

    private async Task ConfirmAndApplyAsync()
    {
        if (await _confirmAsync!()) Apply(true);
        else
        {
            _isOn = false;                    // revert without applying (was never confirmed)
            _hw.Latch(false);
            OnPropertyChanged(nameof(IsOn));
        }
    }
}

/// <summary>A pick-one-of-N option row. A WMI-backed choice verifies like <see cref="ToggleRowViewModel"/>:
/// after the pick it re-reads the hardware index and snaps the dropdown to the real value.</summary>
public sealed partial class ChoiceRowViewModel : ObservableObject
{
    private readonly Action<int> _onPick;
    private readonly Func<int>? _read;
    private readonly VerifiedHwValue<int> _hw = new();
    private bool _syncing;     // guards the readback snap-back so it doesn't re-fire OnChange

    public ChoiceRowViewModel(OptionChoice c)
    {
        Label = c.Label;
        IsEnabled = c.Supported;
        Options = c.Options;
        _onPick = c.OnChange;
        _read = c.Read;
        _selectedIndex = Math.Clamp(c.InitialIndex, 0, c.Options.Count - 1);   // direct write -> no pick fired
        _hw.Latch(_selectedIndex);
    }

    public string Label { get; }
    public bool IsEnabled { get; }
    public IReadOnlyList<string> Options { get; }

    [ObservableProperty] private int _selectedIndex;

    partial void OnSelectedIndexChanged(int value)
    {
        if (_syncing) return;   // change came from a readback snap-back, not the user
        if (_read == null) { _hw.Latch(value); _onPick(value); return; }
        _hw.Apply(value, _onPick, _read, () => SelectedIndex, actual =>
        {
            _syncing = true;
            SelectedIndex = actual;   // reflect reality without re-firing the pick
            _syncing = false;
        });
    }
}
