using System.Collections.ObjectModel;
using System.ComponentModel;
using AcerHelper;
using AcerHelper.Features;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>The lighting drawer root: one panel per light (keyboard, lightbar), shown as tabs.
/// Built from whatever the device's <see cref="ILighting"/> advertises. Each panel loads its
/// persisted state (the app is the source of truth), re-applies it to the device on startup, and
/// saves changes back via <paramref name="save"/>.</summary>
public sealed class LightingViewModel
{
    private readonly ILighting _lighting;
    private readonly LightViewModel? _keyboard;

    public ObservableCollection<LightViewModel> Panels { get; } = [];

    public LightingViewModel(ILighting lighting, Settings settings, Action save)
    {
        _lighting = lighting;

        if (lighting.KeyboardEffects.Count > 0)
        {
            // Seed the keyboard brightness from what the firmware currently reports (Fn keys change it
            // out-of-band), so the slider matches hardware instead of the last persisted value.
            _keyboard = new LightViewModel("Keyboard", lighting.KeyboardEffects, lighting.KeyboardZones,
                (e, c, b, s) => lighting.ApplyKeyboard(e, b, s, c),
                (i, b, c) => lighting.ApplyKeyboardZone(i, b, c),
                settings.Keyboard, save, lighting.ReadKeyboardBrightness());
            Panels.Add(_keyboard);
        }

        if (lighting.LightbarEffects.Count > 0)
            Panels.Add(new LightViewModel("Lightbar", lighting.LightbarEffects, zones: 1,
                (e, c, b, s) => lighting.ApplyLightbar(e, b, s, c), applyZone: null,
                settings.Lightbar, save));
    }

    /// <summary>Re-read the live keyboard brightness and reflect it in the slider (no re-apply/save).
    /// Call when the lighting panel is shown so it stays in sync with Fn-key changes.</summary>
    public void Sync()
    {
        if (_keyboard != null && _lighting.ReadKeyboardBrightness() is int b)
            _keyboard.SyncBrightness(b);
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
    private readonly LightSettings _state;
    private readonly Action _save;
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
                          Action<RgbModeInfo, AccentColor, byte, byte> applyAll, Action<int, byte, AccentColor>? applyZone,
                          LightSettings state, Action save, int? liveBrightness = null)
    {
        Title = title;
        _effects = effects;
        _applyAll = applyAll;
        _applyZone = applyZone;
        _state = state;
        _save = save;
        EffectNames = effects.Select(e => e.Name).ToList();

        // Restore the persisted selection (direct field writes -> the OnXxxChanged hooks don't fire).
        _selectedEffectIndex = effects.Count > 0 ? Math.Clamp(state.EffectIndex, 0, effects.Count - 1) : 0;
        _brightness = Math.Clamp(liveBrightness ?? state.Brightness, 0, 100);   // hardware value wins if readable
        _speed = state.Speed;
        _color = FromPacked(state.Color);

        if (applyZone != null && zones > 1)
        {
            Color[] def = { Colors.Red, Colors.Lime, Colors.Blue, Colors.Magenta };
            for (int i = 0; i < zones; i++)
            {
                Color c = i < state.ZoneColors.Length ? FromPacked(state.ZoneColors[i]) : def[i % def.Length];
                var z = new ZoneColorViewModel($"Zone {i + 1}", c);
                z.PropertyChanged += OnZoneChanged;
                Zones.Add(z);
            }
        }

        _debounce.Tick += (_, _) => { _debounce.Stop(); ApplyNow(); SaveState(); };
        UpdateColorMode();
        _loading = false;

        // Re-apply on startup so the device matches the app — but only if the user has set it before,
        // so a fresh install doesn't override whatever the firmware was showing.
        if (state.Configured) ApplyNow();
    }

    /// <summary>Reflect a hardware-reported brightness in the slider without triggering an apply/save
    /// (used to sync to Fn-key changes). The <c>_loading</c> guard makes OnBrightnessChanged a no-op.</summary>
    public void SyncBrightness(int value)
    {
        value = Math.Clamp(value, 0, 100);
        if ((int)Brightness == value) return;
        _loading = true;
        Brightness = value;
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

    private void SaveState()
    {
        _state.Configured = true;
        _state.EffectIndex = SelectedEffectIndex;
        _state.Brightness = (int)Brightness;
        _state.Speed = (int)Speed;
        _state.Color = Pack(Color);
        _state.ZoneColors = Zones.Select(z => Pack(z.Color)).ToArray();
        _save();
    }

    private static Color FromPacked(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    private static int Pack(Color c) => (c.R << 16) | (c.G << 8) | c.B;
}

/// <summary>One keyboard zone's editable colour.</summary>
public sealed partial class ZoneColorViewModel(string label, Color color) : ObservableObject
{
    public string Label { get; } = label;
    [ObservableProperty] private Color _color = color;
}
