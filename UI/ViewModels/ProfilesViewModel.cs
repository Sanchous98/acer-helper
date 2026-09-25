using System.Collections.ObjectModel;
using System.Linq;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Localization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>Performance section: one segmented button per profile. The active one gets the
/// <c>selected</c> class (filled with the system accent in XAML); profiles not selectable right now are
/// disabled. When the "Turbo toggles" option is on, Turbo is NOT one of the segments — it's a separate
/// switch layered over the selected base profile (see <see cref="TurboOn"/>), so the segments show only
/// the base profiles and the highlighted one is the base underneath Turbo.
///
/// <paramref name="traits"/> is the backend's own reading of the profiles it offers — which class each one
/// belongs to and the colour it is painted with (<see cref="ProfileTraits"/>, reached through the service). It
/// is handed IN rather than read off the profile, because a profile stopped carrying either on 2026-09-22: the
/// class is branched on here (is this Turbo? is Turbo selectable?) and the accent fills the segment, and both
/// are the vendor table's data, not the Domain's.
///
/// OPTIMISTIC UI. The section owns the display rather than waiting for the refresh pass to rediscover the
/// profile by polling: a pick or a Turbo flip moves the selection and the switch IMMEDIATELY, then the write
/// runs, and a refused write puts the previous display back (<see cref="Apply"/> /
/// <see cref="OnTurboOnChanged"/>). The pass may be seconds behind on a busy EC, and the firmware's own
/// read-back is only a confirmation, not the source of the change — see <see cref="_pendingId"/> for how a
/// read-back that predates the write is kept from flickering the selection back.</summary>
public sealed partial class ProfilesViewModel : SectionViewModel
{
    private readonly IReadOnlyList<PerformanceProfile> _all;
    private readonly Func<PerformanceProfile, ProfileTraits> _traits;
    private readonly Func<PerformanceProfile, bool> _onApply;
    private readonly Func<bool, bool> _setTurbo;
    private readonly Action<PerformanceProfile?>? _onDisplay;
    private readonly Dictionary<string, ProfileButtonViewModel> _byId = new();
    private bool _turboAsToggle;
    private bool _updating;

    // ---- optimistic display state ----
    // What the section shows right now. It is the hardware's read-back EXCEPT between a user action and the
    // read-back that confirms it, when it is the action's own target. _pendingId is the profile the write is
    // expected to land on; while it is set, an Update whose read-back shows anything else cannot move the
    // selection off the user's pick (it can only be a snapshot taken before the write). The first read-back
    // that matches commits — clearing _pendingId — after which a genuine change from elsewhere wins again.
    // _optimisticBase is the base highlighted while Turbo is on (toggle mode); _optimisticTurboOn the switch.
    private string? _pendingId;
    private PerformanceProfile? _optimisticCurrent;
    private PerformanceProfile? _optimisticBase;
    private bool _optimisticTurboOn;

    public ObservableCollection<ProfileButtonViewModel> Profiles { get; } = [];

    public bool HasTurbo { get; }

    [ObservableProperty] private bool _showTurboSwitch;   // Turbo shown as a switch (option on + device has Turbo)
    [ObservableProperty] private bool _turboOn;           // hardware currently in Turbo
    [ObservableProperty] private bool _turboEnabled = true;   // Turbo selectable right now (e.g. off on battery)

    /// <param name="onDisplay">Told the profile the section is now displaying whenever it changes — the
    /// optimistic pick, the snap-back, or a confirmed read-back. The header chip uses it to follow the section
    /// instead of waiting for the refresh pass, so the name at the top can't trail the highlighted segment.</param>
    public ProfilesViewModel(IReadOnlyList<PerformanceProfile> all, Func<PerformanceProfile, ProfileTraits> traits,
                             Func<PerformanceProfile, bool> onApply, bool turboAsToggle, Func<bool, bool> setTurbo,
                             Action<PerformanceProfile?>? onDisplay = null)
    {
        _all = all;
        _traits = traits;
        _onApply = onApply;
        _setTurbo = setTurbo;
        _turboAsToggle = turboAsToggle;
        _onDisplay = onDisplay;
        HasTurbo = all.Any(p => traits(p).Kind == ProfileKind.Turbo);
        BuildButtons();
    }

    /// <summary>The profile the section is displaying: the optimistic pick while a write is in flight, and the
    /// hardware's read-back otherwise. The header chip reads this so it can't show a stale name the section has
    /// already moved off (the two used to disagree for a whole pass).</summary>
    public PerformanceProfile? DisplayedProfile => _optimisticCurrent;

    private void BuildButtons()
    {
        Profiles.Clear();
        _byId.Clear();
        foreach (var p in _all)
        {
            if (_turboAsToggle && _traits(p).Kind == ProfileKind.Turbo) continue;   // Turbo lives in the switch instead
            var vm = new ProfileButtonViewModel(p, _traits(p), Apply);
            Profiles.Add(vm);
            _byId[p.Id] = vm;
        }
    }

    /// <summary>A segment was clicked. Show the pick AT ONCE and turn the Turbo switch off (a direct pick clears
    /// Turbo), then write. The write delegate surfaces its own error on the transient status line; all this needs
    /// back is whether it landed, so a refusal can put the previous display back.</summary>
    private void Apply(PerformanceProfile p)
    {
        var previous = (_optimisticCurrent, _optimisticBase, _optimisticTurboOn);
        _optimisticCurrent = p;
        _optimisticBase = p;
        _optimisticTurboOn = false;
        _pendingId = p.Id;
        Render();

        if (_onApply(p)) return;   // landed; the next matching read-back confirms and later changes win again

        (_optimisticCurrent, _optimisticBase, _optimisticTurboOn) = previous;
        _pendingId = null;
        Render();
    }

    /// <summary>The Turbo switch. Guarded so pushing hardware state in (via <see cref="Update"/>) doesn't
    /// re-trigger an apply. A user flip is already showing in the switch (the two-way binding set
    /// <see cref="TurboOn"/>), so this is the optimistic update; the write follows, and a refusal flips it back.
    /// </summary>
    partial void OnTurboOnChanged(bool value)
    {
        if (_updating) return;

        var previous = (_optimisticCurrent, _optimisticBase, _optimisticTurboOn);
        _optimisticTurboOn = value;
        if (value)
        {
            // Capture the base we sit over (so turning Turbo off later returns to it) and show Turbo as current.
            if (_optimisticCurrent is { } c && _traits(c).Kind != ProfileKind.Turbo) _optimisticBase = c;
            _optimisticCurrent = _all.FirstOrDefault(x => _traits(x).Kind == ProfileKind.Turbo);
            _pendingId = _optimisticCurrent?.Id;
        }
        else
        {
            _optimisticCurrent = _optimisticBase;   // the base it sat over becomes the current profile
            _pendingId = _optimisticBase?.Id;
        }
        Render();

        if (_setTurbo(value)) return;

        (_optimisticCurrent, _optimisticBase, _optimisticTurboOn) = previous;
        _pendingId = null;
        Render();
    }

    /// <summary>Repaint the optimistic display — the Turbo switch and the selected segment — without
    /// re-triggering the writes (<see cref="_updating"/>). Availability (enabled/visible) is left to
    /// <see cref="Update"/>, which is the pass's business.</summary>
    private void Render()
    {
        _updating = true;
        TurboOn = _optimisticTurboOn;
        string? selectedId = _turboAsToggle && _optimisticTurboOn ? _optimisticBase?.Id : _optimisticCurrent?.Id;
        foreach (var (id, vm) in _byId)
            vm.IsSelected = selectedId == id;
        _updating = false;
        _onDisplay?.Invoke(_optimisticCurrent);   // the header chip follows the section immediately
    }

    public void Update(PerformanceProfile? current, IReadOnlyList<PerformanceProfile> selectable,
                       bool turboAsToggle, PerformanceProfile? baseHighlight)
    {
        if (turboAsToggle != _turboAsToggle) { _turboAsToggle = turboAsToggle; BuildButtons(); }

        // Availability is independent of the optimistic state: it always tracks the pass, so a profile that
        // becomes selectable (off battery) or a Turbo switch that appears/disappears still updates during a
        // pending write.
        ShowTurboSwitch = turboAsToggle && HasTurbo;
        TurboEnabled = selectable.Any(p => _traits(p).Kind == ProfileKind.Turbo);
        foreach (var (id, vm) in _byId)
            vm.IsEnabled = selectable.Any(p => p.Id == id);

        // While a write is pending, a read-back that does not match it is a snapshot from before the write and
        // may not flicker the selection back. The first read-back that DOES match commits the optimistic state.
        if (_pendingId != null)
        {
            if (current?.Id != _pendingId) return;
            _pendingId = null;
        }

        _updating = true;
        bool inTurbo = current is { } c && _traits(c).Kind == ProfileKind.Turbo;
        _optimisticCurrent = current;
        _optimisticBase = baseHighlight;
        _optimisticTurboOn = inTurbo;
        TurboOn = inTurbo;

        // While Turbo is active in toggle mode, highlight the base profile it sits over; otherwise the current.
        string? selectedId = turboAsToggle && inTurbo ? baseHighlight?.Id : current?.Id;
        foreach (var (id, vm) in _byId)
            vm.IsSelected = selectedId == id;
        _updating = false;
        _onDisplay?.Invoke(_optimisticCurrent);   // the confirmed read-back reaches the header too
    }
}

public sealed partial class ProfileButtonViewModel : ObservableObject
{
    private readonly PerformanceProfile _profile;
    private readonly Action<PerformanceProfile> _onApply;

    /// <param name="traits">This profile's class and colours, as the backend that offers it declares them.
    /// Only the accent is read here — the class is the section's business, and the button is painted, not
    /// classified.</param>
    public ProfileButtonViewModel(PerformanceProfile profile, ProfileTraits traits, Action<PerformanceProfile> onApply)
    {
        _profile = profile;
        _onApply = onApply;
        Name = Loc.T(profile.DisplayName);
        // Each mode's signature colour (from the backend's own table, reached through the service) fills the
        // segment when it's selected, so the active mode reads at a glance — Eco teal / Quiet blue / Balanced
        // green / Performance orange / Turbo red. Unknown profiles fall back to neutral grey — the painter's
        // fallback, which is where it has always been: an unclassified mode reports no colour rather than
        // inventing one.
        var a = traits.Accent ?? new AccentColor(0x80, 0x80, 0x80);
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
