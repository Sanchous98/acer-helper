using System.Collections.ObjectModel;
using AcerHelper.Features;
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

/// <summary>Options section: hardware toggles/choices plus clamshell, Turbo-key behaviour and
/// autostart — whichever the device exposes. <see cref="TryCreate"/> returns null when there is
/// nothing to show, so the shell can omit the section.</summary>
public sealed class OptionsViewModel : SectionViewModel
{
    public ObservableCollection<ObservableObject> Rows { get; } = [];

    public static OptionsViewModel? TryCreate(IDevice device, UiActions a)
    {
        var vm = new OptionsViewModel();
        foreach (var t in a.HwToggles) vm.Rows.Add(new ToggleRowViewModel(t));
        foreach (var c in a.HwChoices) vm.Rows.Add(new ChoiceRowViewModel(c));

        if (device.Clamshell is { } clam)
            vm.Rows.Add(new ToggleRowViewModel(clam.Label, a.ClamshellEnabled(), true, a.SetClamshell));

        if (device.PowerProfiles?.All.Any(p => p.Kind == ProfileKind.Turbo) ?? false)
            vm.Rows.Add(new ToggleRowViewModel("Turbo key toggles Turbo", a.TurboToggles, true, a.SetTurboToggles,
                tip: "Otherwise the Turbo key cycles through profiles."));

        if (device.Autostart is { } auto)
            vm.Rows.Add(new ToggleRowViewModel(auto.Label, a.AutostartEnabled(), true, a.SetAutostart));

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
    private readonly HwSerial _hw = new();
    private bool _isOn;
    private bool _desired;   // latest requested value; stale readbacks (from superseded clicks) are ignored

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
        _desired = initial;
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
                _desired = true;
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
    /// (<see cref="_desired"/>), so a fast burst never fights the user. Non-readable options apply inline.</summary>
    private void Apply(bool desired)
    {
        _desired = desired;
        if (_read == null) { _onChange(desired); return; }

        _hw.Enqueue(() =>
        {
            _onChange(desired);
            var actual = _read();
            Dispatcher.UIThread.Post(() =>
            {
                if (desired != _desired || actual == _isOn) return;   // superseded, or already matches
                _isOn = actual;                                       // reflect reality (honest snap-back)
                OnPropertyChanged(nameof(IsOn));
            });
        });
    }

    private async Task ConfirmAndApplyAsync()
    {
        if (await _confirmAsync!()) Apply(true);
        else
        {
            _isOn = false;                    // revert without applying (was never confirmed)
            _desired = false;
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
    private readonly HwSerial _hw = new();
    private int _desired;      // latest requested index; stale readbacks are ignored
    private bool _syncing;     // guards the readback snap-back so it doesn't re-fire OnChange

    public ChoiceRowViewModel(OptionChoice c)
    {
        Label = c.Label;
        IsEnabled = c.Supported;
        Options = c.Options;
        _onPick = c.OnChange;
        _read = c.Read;
        _selectedIndex = Math.Clamp(c.InitialIndex, 0, c.Options.Count - 1);   // direct write -> no pick fired
        _desired = _selectedIndex;
    }

    public string Label { get; }
    public bool IsEnabled { get; }
    public IReadOnlyList<string> Options { get; }

    [ObservableProperty] private int _selectedIndex;

    partial void OnSelectedIndexChanged(int value)
    {
        if (_syncing) return;   // change came from a readback snap-back, not the user
        _desired = value;
        if (_read == null) { _onPick(value); return; }

        _hw.Enqueue(() =>
        {
            _onPick(value);
            var actual = _read();
            Dispatcher.UIThread.Post(() =>
            {
                if (value != _desired || actual == SelectedIndex) return;   // superseded, or already matches
                _syncing = true;
                SelectedIndex = actual;   // reflect reality without re-firing the pick
                _syncing = false;
            });
        });
    }
}
