using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>The lighting window root: one panel per light (keyboard, lightbar), shown as tabs.
/// Built from whatever the device's <see cref="ILighting"/> advertises.</summary>
public sealed class LightingViewModel
{
    public ObservableCollection<LightViewModel> Panels { get; } = [];

    public LightingViewModel(ILighting lighting)
    {
        if (lighting.KeyboardEffects.Count > 0)
            Panels.Add(new LightViewModel("Keyboard", lighting.KeyboardEffects, lighting.KeyboardZones,
                (e, c, b, s) => lighting.ApplyKeyboard(e, b, s, c),
                (i, b, c) => lighting.ApplyKeyboardZone(i, b, c)));

        if (lighting.LightbarEffects.Count > 0)
            Panels.Add(new LightViewModel("Lightbar", lighting.LightbarEffects, zones: 1,
                (e, c, b, s) => lighting.ApplyLightbar(e, b, s, c), applyZone: null));
    }
}

/// <summary>
/// One light, laid out like popular RGB tools: effect picker, a zone selector (multi-zone static
/// only), a full colour wheel, and brightness/speed sliders. Everything applies LIVE (debounced),
/// no Apply button. The colour wheel edits the selected zone (per-zone static) or the single colour
/// (single-zone / effect). Heuristic for "static" (per-zone capable): honours colour, has no speed.
/// </summary>
public sealed partial class LightViewModel : ObservableObject
{
    private readonly IReadOnlyList<RgbModeInfo> _effects;
    private readonly Action<RgbModeInfo, AccentColor, byte, byte> _applyAll;
    private readonly Action<int, byte, AccentColor>? _applyZone;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _loading = true;
    private bool _suppress;   // guards programmatic EditColor writes (zone switch) from re-applying

    public string Title { get; }
    public IReadOnlyList<string> EffectNames { get; }
    public ObservableCollection<ZoneColorViewModel> Zones { get; } = [];

    [ObservableProperty] private int _selectedEffectIndex;
    [ObservableProperty] private int _selectedZoneIndex;
    [ObservableProperty] private Color _editColor = Colors.Red;
    [ObservableProperty] private bool _hasSpeed;
    [ObservableProperty] private bool _showColor;
    [ObservableProperty] private bool _showZones;
    [ObservableProperty] private double _brightness = 100;
    [ObservableProperty] private double _speed = 5;

    public LightViewModel(string title, IReadOnlyList<RgbModeInfo> effects, int zones,
                          Action<RgbModeInfo, AccentColor, byte, byte> applyAll, Action<int, byte, AccentColor>? applyZone)
    {
        Title = title;
        _effects = effects;
        _applyAll = applyAll;
        _applyZone = applyZone;
        EffectNames = effects.Select(e => e.Name).ToList();

        if (applyZone != null && zones > 1)
        {
            Color[] def = { Colors.Red, Colors.Lime, Colors.Blue, Colors.Magenta };
            for (int i = 0; i < zones; i++)
                Zones.Add(new ZoneColorViewModel($"Z{i + 1}", def[i % def.Length]));
            _editColor = Zones[0].Color;
        }

        _debounce.Tick += (_, _) => { _debounce.Stop(); ApplyNow(); };
        UpdateColorMode();
        _loading = false;
    }

    partial void OnSelectedEffectIndexChanged(int value)
    {
        UpdateColorMode();
        if (ShowZones && Zones.Count > 0) SetEditColor(Zones[Clamp(SelectedZoneIndex)].Color);
        Schedule();
    }

    partial void OnSelectedZoneIndexChanged(int value)
    {
        if (value < 0 || value >= Zones.Count) return;
        SetEditColor(Zones[value].Color);   // jump the wheel to the picked zone (no apply)
    }

    partial void OnEditColorChanged(Color value)
    {
        if (_loading || _suppress) return;
        if (ShowZones && SelectedZoneIndex >= 0 && SelectedZoneIndex < Zones.Count)
            Zones[SelectedZoneIndex].Color = value;   // recolours the picked swatch live
        Schedule();
    }

    partial void OnBrightnessChanged(double value) => Schedule();
    partial void OnSpeedChanged(double value) => Schedule();

    private RgbModeInfo Current => _effects[Clamp(SelectedEffectIndex)];
    private int Clamp(int i) => Math.Clamp(i, 0, _effects.Count - 1);

    private void SetEditColor(Color c) { _suppress = true; EditColor = c; _suppress = false; }

    private void UpdateColorMode()
    {
        var e = Current;
        HasSpeed = e.HasSpeed;
        ShowZones = e.HasColor && !e.HasSpeed && Zones.Count > 1;   // static + multi-zone
        ShowColor = e.HasColor;
    }

    private void Schedule()
    {
        if (_loading) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void ApplyNow()
    {
        byte b = (byte)Brightness, s = (byte)Speed;
        if (ShowZones && _applyZone != null)
            for (int i = 0; i < Zones.Count; i++)
            {
                var c = Zones[i].Color;
                _applyZone(i, b, new AccentColor(c.R, c.G, c.B));
            }
        else
            _applyAll(Current, new AccentColor(EditColor.R, EditColor.G, EditColor.B), b, s);
    }
}

/// <summary>One keyboard zone: its colour and a brush for the preview swatch.</summary>
public sealed partial class ZoneColorViewModel(string label, Color color) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty] private Color _color = color;

    /// <summary>Brush for the zone swatch in the selector (kept in sync with <see cref="Color"/>).</summary>
    public IBrush Fill => new SolidColorBrush(Color);

    partial void OnColorChanged(Color value) => OnPropertyChanged(nameof(Fill));
}
