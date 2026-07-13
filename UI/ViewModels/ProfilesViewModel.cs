using System.Collections.ObjectModel;
using System.Linq;
using AcerHelper.Features;
using AcerHelper.Localization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>Performance section: one segmented button per profile. The active one gets the
/// <c>selected</c> class (filled with the system accent in XAML); profiles not selectable right now are
/// disabled. When the "Turbo toggles" option is on, Turbo is NOT one of the segments — it's a separate
/// switch layered over the selected base profile (see <see cref="TurboOn"/>), so the segments show only
/// the base profiles and the highlighted one is the base underneath Turbo.</summary>
public sealed partial class ProfilesViewModel : SectionViewModel
{
    private readonly IReadOnlyList<PerformanceProfile> _all;
    private readonly Action<PerformanceProfile> _onApply;
    private readonly Action<bool> _setTurbo;
    private readonly Dictionary<string, ProfileButtonViewModel> _byId = new();
    private bool _turboAsToggle;
    private bool _updating;

    public ObservableCollection<ProfileButtonViewModel> Profiles { get; } = [];

    public bool HasTurbo { get; }

    [ObservableProperty] private bool _showTurboSwitch;   // Turbo shown as a switch (option on + device has Turbo)
    [ObservableProperty] private bool _turboOn;           // hardware currently in Turbo
    [ObservableProperty] private bool _turboEnabled = true;   // Turbo selectable right now (e.g. off on battery)

    public ProfilesViewModel(IReadOnlyList<PerformanceProfile> all, Action<PerformanceProfile> onApply,
                             bool turboAsToggle, Action<bool> setTurbo)
    {
        _all = all;
        _onApply = onApply;
        _setTurbo = setTurbo;
        _turboAsToggle = turboAsToggle;
        HasTurbo = all.Any(p => p.Kind == ProfileKind.Turbo);
        BuildButtons();
    }

    private void BuildButtons()
    {
        Profiles.Clear();
        _byId.Clear();
        foreach (var p in _all)
        {
            if (_turboAsToggle && p.Kind == ProfileKind.Turbo) continue;   // Turbo lives in the switch instead
            var vm = new ProfileButtonViewModel(p, _onApply);
            Profiles.Add(vm);
            _byId[p.Id] = vm;
        }
    }

    /// <summary>The Turbo switch. Guarded so pushing hardware state in (via <see cref="Update"/>) doesn't
    /// re-trigger an apply.</summary>
    partial void OnTurboOnChanged(bool value) { if (!_updating) _setTurbo(value); }

    public void Update(PerformanceProfile? current, IReadOnlyList<PerformanceProfile> selectable,
                       bool turboAsToggle, PerformanceProfile? baseHighlight)
    {
        if (turboAsToggle != _turboAsToggle) { _turboAsToggle = turboAsToggle; BuildButtons(); }

        _updating = true;
        ShowTurboSwitch = turboAsToggle && HasTurbo;
        bool inTurbo = current?.Kind == ProfileKind.Turbo;
        TurboOn = inTurbo;
        TurboEnabled = selectable.Any(p => p.Kind == ProfileKind.Turbo);

        // While Turbo is active in toggle mode, highlight the base profile it sits over; otherwise the current.
        string? selectedId = turboAsToggle && inTurbo ? baseHighlight?.Id : current?.Id;
        foreach (var (id, vm) in _byId)
        {
            vm.IsSelected = selectedId == id;
            vm.IsEnabled = selectable.Any(p => p.Id == id);
        }
        _updating = false;
    }
}

public sealed partial class ProfileButtonViewModel : ObservableObject
{
    private readonly PerformanceProfile _profile;
    private readonly Action<PerformanceProfile> _onApply;

    public ProfileButtonViewModel(PerformanceProfile profile, Action<PerformanceProfile> onApply)
    {
        _profile = profile;
        _onApply = onApply;
        Name = Loc.T(profile.DisplayName);
        // Each mode's signature colour (from the domain model) fills the segment when it's selected, so the
        // active mode reads at a glance — Eco teal / Quiet blue / Balanced green / Performance orange /
        // Turbo red. Unknown profiles fall back to neutral grey.
        var a = profile.Accent ?? new AccentColor(0x80, 0x80, 0x80);
        var c = Color.FromRgb(a.R, a.G, a.B);
        SelectedBrush = new SolidColorBrush(c);
        // Hover / press tint an UNSELECTED segment with a translucent wash of its own colour, so hovering
        // previews the mode's colour and clearly reads as different from the neutral resting fill.
        HoverBrush = new SolidColorBrush(c, 0.22);
        PressedBrush = new SolidColorBrush(c, 0.34);
    }

    public string Name { get; }

    /// <summary>Fill colour for this segment when selected (the mode's signature colour).</summary>
    public IBrush SelectedBrush { get; }

    /// <summary>Translucent wash of the mode's colour for hover / press on an unselected segment.</summary>
    public IBrush HoverBrush { get; }
    public IBrush PressedBrush { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isEnabled = true;

    [RelayCommand] private void Apply() => _onApply(_profile);
}
