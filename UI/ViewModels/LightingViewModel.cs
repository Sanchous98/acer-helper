using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>The lighting window root: a collection of light panels (keyboard / per-zone / lightbar),
/// rendered by the DataTemplates in LightingWindow.axaml. Built from whatever the device's
/// <see cref="ILighting"/> port advertises, so absent capabilities simply don't appear.</summary>
public sealed class LightingViewModel
{
    public ObservableCollection<ObservableObject> Panels { get; } = [];

    public LightingViewModel(ILighting lighting)
    {
        if (lighting.KeyboardEffects.Count > 0)
            Panels.Add(new RgbDeviceViewModel("KEYBOARD", lighting.KeyboardEffects,
                (e, c, b, s) => lighting.ApplyKeyboard(e, b, s, c)));

        if (lighting.KeyboardZones > 1)
            Panels.Add(new RgbZonesViewModel(lighting.KeyboardZones,
                (i, b, c) => lighting.ApplyKeyboardZone(i, b, c)));

        if (lighting.LightbarEffects.Count > 0)
            Panels.Add(new RgbDeviceViewModel("LIGHTBAR", lighting.LightbarEffects,
                (e, c, b, s) => lighting.ApplyLightbar(e, b, s, c)));
    }
}

/// <summary>One light with a single effect at a time: mode dropdown, colour, brightness, speed.
/// Colour/speed inputs enable only when the selected effect honours them.</summary>
public sealed partial class RgbDeviceViewModel : ObservableObject
{
    private readonly IReadOnlyList<RgbModeInfo> _effects;
    private readonly Action<RgbModeInfo, AccentColor, byte, byte> _apply;

    public string Title { get; }
    public IReadOnlyList<string> EffectNames { get; }

    [ObservableProperty] private int _selectedEffectIndex;
    [ObservableProperty] private bool _hasColor = true;
    [ObservableProperty] private bool _hasSpeed;
    [ObservableProperty] private Color _color = Colors.Red;
    [ObservableProperty] private double _brightness = 100;
    [ObservableProperty] private double _speed = 5;

    public RgbDeviceViewModel(string title, IReadOnlyList<RgbModeInfo> effects, Action<RgbModeInfo, AccentColor, byte, byte> apply)
    {
        Title = title;
        _effects = effects;
        _apply = apply;
        EffectNames = effects.Select(e => e.Name).ToList();
        UpdateCapabilities();
    }

    partial void OnSelectedEffectIndexChanged(int value) => UpdateCapabilities();

    private void UpdateCapabilities()
    {
        var e = _effects[Math.Clamp(SelectedEffectIndex, 0, _effects.Count - 1)];
        HasColor = e.HasColor;
        HasSpeed = e.HasSpeed;
    }

    [RelayCommand]
    private void Apply()
    {
        var e = _effects[Math.Clamp(SelectedEffectIndex, 0, _effects.Count - 1)];
        _apply(e, new AccentColor(Color.R, Color.G, Color.B), (byte)Brightness, (byte)Speed);
    }
}

/// <summary>Per-zone static colours: one colour per keyboard zone + a shared brightness.</summary>
public sealed partial class RgbZonesViewModel : ObservableObject
{
    private static readonly Color[] Defaults = { Colors.Red, Colors.Lime, Colors.Blue, Colors.Magenta };
    private readonly Action<int, byte, AccentColor> _applyZone;

    public ObservableCollection<ZoneColorViewModel> Zones { get; } = [];

    [ObservableProperty] private double _brightness = 100;

    public RgbZonesViewModel(int zones, Action<int, byte, AccentColor> applyZone)
    {
        _applyZone = applyZone;
        for (int i = 0; i < zones; i++)
            Zones.Add(new ZoneColorViewModel($"Zone {i + 1}", Defaults[i % Defaults.Length]));
    }

    [RelayCommand]
    private void Apply()
    {
        byte b = (byte)Brightness;
        for (int i = 0; i < Zones.Count; i++)
        {
            var c = Zones[i].Color;
            _applyZone(i, b, new AccentColor(c.R, c.G, c.B));
        }
    }
}

/// <summary>One keyboard zone's editable colour.</summary>
public sealed partial class ZoneColorViewModel(string label, Color color) : ObservableObject
{
    public string Label { get; } = label;
    [ObservableProperty] private Color _color = color;
}
