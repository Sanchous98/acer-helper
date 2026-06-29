using System.Collections.ObjectModel;
using AcerHelper.Features;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>Performance section: one segmented button per profile. The active one gets the
/// <c>selected</c> class (filled with the system accent in XAML); profiles not selectable right now
/// are disabled.</summary>
public sealed class ProfilesViewModel : SectionViewModel
{
    private readonly Dictionary<string, ProfileButtonViewModel> _byId = new();

    public ObservableCollection<ProfileButtonViewModel> Profiles { get; } = [];

    public ProfilesViewModel(IReadOnlyList<PerformanceProfile> all, Action<PerformanceProfile> onApply)
    {
        foreach (var p in all)
        {
            var vm = new ProfileButtonViewModel(p, onApply);
            Profiles.Add(vm);
            _byId[p.Id] = vm;
        }
    }

    public void Update(PerformanceProfile? current, IReadOnlyList<PerformanceProfile> selectable)
    {
        foreach (var (id, vm) in _byId)
        {
            vm.IsSelected = current?.Id == id;
            vm.IsEnabled = selectable.Any(p => p.Id == id);
        }
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
        Name = profile.DisplayName;
    }

    public string Name { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isEnabled = true;

    [RelayCommand] private void Apply() => _onApply(_profile);
}
