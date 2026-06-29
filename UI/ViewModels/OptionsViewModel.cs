using System.Collections.ObjectModel;
using AcerHelper.Features;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

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
/// declines (the setter vetoes and re-notifies, so the switch snaps back).</summary>
public sealed class ToggleRowViewModel : ObservableObject
{
    private readonly Action<bool> _onChange;
    private readonly Func<bool>? _confirm;
    private readonly Func<Task<bool>>? _confirmAsync;
    private bool _isOn;

    public ToggleRowViewModel(OptionToggle t)
        : this(t.Label, t.Initial, t.Supported, t.OnChange, confirm: t.Confirm, confirmAsync: t.ConfirmAsync) { }

    public ToggleRowViewModel(string label, bool initial, bool enabled, Action<bool> onChange,
                              string? tip = null, Func<bool>? confirm = null, Func<Task<bool>>? confirmAsync = null)
    {
        Label = label;
        IsEnabled = enabled;
        Tip = tip;
        _isOn = initial;
        _onChange = onChange;
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
                _ = ConfirmAndApplyAsync();
                return;
            }

            SetProperty(ref _isOn, value);
            _onChange(value);
        }
    }

    private async Task ConfirmAndApplyAsync()
    {
        if (await _confirmAsync!())
        {
            _onChange(true);
        }
        else
        {
            _isOn = false;                    // revert without firing OnChange (was never applied)
            OnPropertyChanged(nameof(IsOn));
        }
    }
}

/// <summary>A pick-one-of-N option row.</summary>
public sealed partial class ChoiceRowViewModel : ObservableObject
{
    private readonly Action<int> _onPick;

    public ChoiceRowViewModel(OptionChoice c)
    {
        Label = c.Label;
        IsEnabled = c.Supported;
        Options = c.Options;
        _onPick = c.OnChange;
        _selectedIndex = Math.Clamp(c.InitialIndex, 0, c.Options.Count - 1);   // direct write -> no pick fired
    }

    public string Label { get; }
    public bool IsEnabled { get; }
    public IReadOnlyList<string> Options { get; }

    [ObservableProperty] private int _selectedIndex;

    partial void OnSelectedIndexChanged(int value) => _onPick(value);
}
