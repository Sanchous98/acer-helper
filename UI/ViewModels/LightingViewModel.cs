using System.Collections.ObjectModel;
using System.ComponentModel;
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
/// One light. Applies LIVE (debounced, no Apply button). The colour UI adapts to the selected
/// effect: a static colour on a multi-zone keyboard shows one editable swatch per zone; any other
/// colour effect (e.g. Breathing) shows a single colour; effects that cycle their own colours show
/// no colour control. "Static" = honours colour and has no speed (the only such Acer effect).
/// </summary>
public sealed partial class LightViewModel : ObservableObject
{
    private readonly IReadOnlyList<RgbModeInfo> _effects;
    private readonly Action<RgbModeInfo, AccentColor, byte, byte> _applyAll;
    private readonly Action<int, byte, AccentColor>? _applyZone;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _loading = true;

    public string Title { get; }
    public IReadOnlyList<string> EffectNames { get; }
    public ObservableCollection<ZoneColorViewModel> Zones { get; } = [];

    [ObservableProperty] private int _selectedEffectIndex;
    [ObservableProperty] private bool _hasSpeed;
    [ObservableProperty] private bool _showSingleColor;
    [ObservableProperty] private bool _showZones;
    [ObservableProperty] private Color _color = Colors.Red;
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
            {
                var z = new ZoneColorViewModel($"Zone {i + 1}", def[i % def.Length]);
                z.PropertyChanged += OnZoneChanged;
                Zones.Add(z);
            }
        }

        _debounce.Tick += (_, _) => { _debounce.Stop(); ApplyNow(); };
        UpdateColorMode();
        _loading = false;
    }

    partial void OnSelectedEffectIndexChanged(int value) { UpdateColorMode(); Schedule(); }
    partial void OnColorChanged(Color value) => Schedule();
    partial void OnBrightnessChanged(double value) => Schedule();
    partial void OnSpeedChanged(double value) => Schedule();

    private void OnZoneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ZoneColorViewModel.Color)) Schedule();
    }

    private RgbModeInfo Current => _effects[Math.Clamp(SelectedEffectIndex, 0, _effects.Count - 1)];

    private void UpdateColorMode()
    {
        var e = Current;
        HasSpeed = e.HasSpeed;
        ShowZones = e.HasColor && !e.HasSpeed && Zones.Count > 1;   // static + multi-zone -> per-zone swatches
        ShowSingleColor = e.HasColor && !ShowZones;                 // breathing / single-zone static -> one colour
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
            _applyAll(Current, new AccentColor(Color.R, Color.G, Color.B), b, s);
    }
}

/// <summary>One keyboard zone's editable colour.</summary>
public sealed partial class ZoneColorViewModel(string label, Color color) : ObservableObject
{
    public string Label { get; } = label;
    [ObservableProperty] private Color _color = color;
}
