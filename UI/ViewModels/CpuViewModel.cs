using System.Linq;
using AcerHelper.Features;
using AcerHelper.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>CPU-power section: a single dropdown that maps a Windows power-mode overlay (Best efficiency /
/// Balanced / Best performance) to the current performance profile. Applies on change and persists PER
/// profile — switching profile reloads that profile's choice (see <see cref="Load"/>); an unconfigured profile
/// shows the live effective overlay and is left untouched. Only built when the device exposes an
/// <see cref="ICpuPower"/> port. This is the driverless CPU-power axis (no undervolt/PPT — those need ring-0).</summary>
public sealed partial class CpuViewModel : SectionViewModel
{
    private readonly IReadOnlyList<ChoiceOption> _modes;   // id = overlay GUID string
    private readonly Action<string> _set;
    private bool _loading;

    /// <summary>Localized mode names for the dropdown (index-aligned with <see cref="_modes"/>).</summary>
    public IReadOnlyList<string> ModeNames { get; }

    [ObservableProperty] private int _selectedIndex;

    public CpuViewModel(IReadOnlyList<ChoiceOption> modes, string? initialId, Action<string> set)
    {
        _loading = true;
        _modes = modes;
        _set = set;
        ModeNames = modes.Select(m => Loc.T(m.DisplayName)).ToList();
        _selectedIndex = IndexOf(initialId);
        _loading = false;
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (_loading) return;                                  // came from a mode-switch reload, not the user
        if (value >= 0 && value < _modes.Count) _set(_modes[value].Id);
    }

    /// <summary>Reflect a profile's chosen (or live effective) overlay without applying — the service already
    /// set the hardware on the mode switch. The <c>_loading</c> guard neuters the change hook.</summary>
    public void Load(string? id)
    {
        _loading = true;
        SelectedIndex = IndexOf(id);
        _loading = false;
    }

    private int IndexOf(string? id)
    {
        for (var i = 0; i < _modes.Count; i++) if (_modes[i].Id == id) return i;
        // Unknown/null -> Balanced (the all-zero overlay), else the first entry.
        for (var i = 0; i < _modes.Count; i++) if (_modes[i].Id == Guid.Empty.ToString()) return i;
        return 0;
    }
}
