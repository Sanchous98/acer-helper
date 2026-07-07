using System.Collections.ObjectModel;
using System.ComponentModel;
using AcerHelper.Features;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>The lighting drawer root: one panel per RGB zone the device advertises (keyboard, lightbar,
/// or anything a future controller exposes), shown as tabs. Built generically from <see cref="IRgbDevice"/>
/// — no fixed keyboard/lightbar assumptions. Each panel loads its persisted state (keyed by zone name; the
/// app is the source of truth), re-applies it to the device on startup, and saves changes via
/// <paramref name="save"/>.</summary>
public sealed partial class LightingViewModel : ObservableObject
{
    public ObservableCollection<LightViewModel> Panels { get; } = [];

    /// <summary>The plain (non-RGB) keyboard-backlight brightness control, shown when the device has a plain
    /// backlight and NO RGB zones — then, per design, brightness is the only lighting setting.</summary>
    public BacklightViewModel? Backlight { get; }

    public bool HasZones => Panels.Count > 0;
    public bool HasBacklight => Backlight != null;

    // Zones the firmware already paints per performance-profile (the Acer lightbar). While FollowsProfile is on
    // we build no panel for them and never send them anything, so the firmware's per-profile palette shows
    // seamlessly (no flash). See docs/lighting-an18-61.md.
    private readonly IReadOnlyList<RgbZone> _followZones;
    private readonly Action _save;
    private readonly Action<bool> _saveFollowsProfile;
    private Dictionary<string, LightSettings> _lights;   // current performance mode's per-zone state (swapped by Reload)

    /// <summary>True when the device has a follow-capable zone (a lightbar) — the switch is only shown then.</summary>
    public bool ShowFollowsProfile => _followZones.Count > 0;

    /// <summary>When on (default), follow-capable zones (the lightbar) are left to the firmware — their
    /// per-profile palette colour, flash-free — and get no panel. When off they become normal user-controlled
    /// zones (custom colour/effects), at the cost of a brief palette flash on each profile switch.</summary>
    [ObservableProperty] private bool _followsProfile;

    /// <summary><paramref name="lights"/> is the CURRENT performance mode's per-zone state (from
    /// LaptopService.LightsForCurrentMode). On a mode change the panels are rebound to the new mode's state
    /// via <see cref="Reload"/>. <paramref name="backlight"/> is a plain (non-RGB) backlight, if any.</summary>
    public LightingViewModel(IRgbDevice? rgb, Dictionary<string, LightSettings> lights, Action save,
                             bool followsProfile, Action<bool> saveFollowsProfile,
                             IKeyboardBrightness? backlight = null, Func<int, bool>? applyBacklight = null)
    {
        _lights = lights;
        _save = save;
        _saveFollowsProfile = saveFollowsProfile;
        _followsProfile = followsProfile;   // field write: don't fire OnFollowsProfileChanged during construction

        var zones = (rgb?.Zones ?? []).Where(z => z.Effects.Count > 0).ToList();
        _followZones = zones.Where(z => z.CanFollowProfile).ToList();

        // Build a panel per zone — but skip follow-capable zones while syncing (they're left to the firmware).
        foreach (var zone in zones)
            if (!(zone.CanFollowProfile && _followsProfile))
                BuildPanel(zone);

        // A non-RGB keyboard backlight: brightness is the ONLY control (an RGB keyboard's brightness is
        // per-zone, so it's shown only when there are no zones).
        if (Panels.Count == 0 && backlight != null && applyBacklight != null)
            Backlight = new BacklightViewModel(backlight, applyBacklight);
    }

    // Build a panel for one zone, bound to the current mode's per-zone state (created on first sight). Seeds
    // brightness from what the firmware reports (Fn keys change it out-of-band); readBrightness also drives Sync.
    private void BuildPanel(RgbZone zone)
    {
        if (!_lights.TryGetValue(zone.Name, out var state))
            _lights[zone.Name] = state = new LightSettings();
        Panels.Add(new LightViewModel(zone.Name, zone.Effects, zone.SubZones,
            (e, c, b, s, d) => zone.ApplyEffect(e, b, s, d, c),
            zone.HasSubZones ? (i, b, c) => zone.ApplySubZone(i, b, c) : null,
            state, _save, zone.ReadBrightness));
    }

    // Flip the switch live: turning it OFF builds the follow-capable panels (each applies the mode's stored
    // colour); turning it ON drops them so we stop driving those zones (the firmware repaints them to the
    // profile palette on the next switch). Persist the choice either way.
    partial void OnFollowsProfileChanged(bool value)
    {
        foreach (var z in _followZones)
        {
            var panel = Panels.FirstOrDefault(p => p.Title == z.Name);
            if (value) { if (panel != null) Panels.Remove(panel); }   // hand the zone back to the firmware palette
            else if (panel == null) BuildPanel(z);                    // take it over as a user-driven zone
        }
        _saveFollowsProfile(value);
    }

    /// <summary>Re-read live brightness (each RGB zone's, and the plain backlight's) and reflect it in the
    /// slider (no re-apply/save). Call when the lighting panel is shown so it stays in sync with Fn-key
    /// changes.</summary>
    public void Sync()
    {
        foreach (var panel in Panels) panel.SyncFromHardware();
        Backlight?.SyncFromHardware();
    }

    /// <summary>Rebind every panel to a different mode's per-zone state and re-apply it (called when the
    /// performance mode changes, so each mode carries its own lighting).</summary>
    public void Reload(Dictionary<string, LightSettings> lights)
    {
        _lights = lights;   // keep for BuildPanel when the follow switch is flipped mid-mode
        foreach (var panel in Panels)
        {
            if (!lights.TryGetValue(panel.Title, out var state))
                lights[panel.Title] = state = new LightSettings();
            panel.Rebind(state);
        }
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
    private readonly Action<RgbModeInfo, AccentColor, byte, byte, byte> _applyAll;   // (effect, colour, brightness, speed, direction)
    private readonly Action<int, byte, AccentColor>? _applyZone;
    private readonly Func<int?>? _readBrightness;
    private LightSettings _state;   // swapped by Rebind when the performance mode changes
    private readonly Action _save;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _loading;
    private readonly object _readGate = new();   // guards the off-thread brightness read coalescing
    private bool _reading, _readPending;

    public string Title { get; }
    public IReadOnlyList<string> EffectNames { get; }
    public ObservableCollection<ZoneColorViewModel> Zones { get; } = [];

    [ObservableProperty] private int _selectedEffectIndex;
    [ObservableProperty] private bool _hasSpeed;
    [ObservableProperty] private bool _hasDirection;
    [ObservableProperty] private bool _reverseDirection;   // false => byte[5]=1, true => byte[5]=2
    [ObservableProperty] private bool _showSingleColor;
    [ObservableProperty] private bool _showZones;
    [ObservableProperty] private Color _color;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _speed;

    public LightViewModel(string title, IReadOnlyList<RgbModeInfo> effects, int zones,
                          Action<RgbModeInfo, AccentColor, byte, byte, byte> applyAll, Action<int, byte, AccentColor>? applyZone,
                          LightSettings state, Action save, Func<int?>? readBrightness = null)
    {
        Title = title;
        _effects = effects;
        _applyAll = applyAll;
        _applyZone = applyZone;
        _readBrightness = readBrightness;
        _state = state;
        _save = save;
        EffectNames = effects.Select(e => e.Name).ToList();

        // Restore the persisted selection (direct field writes -> the OnXxxChanged hooks don't fire).
        _selectedEffectIndex = effects.Count > 0 ? Math.Clamp(state.EffectIndex, 0, effects.Count - 1) : 0;
        _brightness = Math.Clamp(readBrightness?.Invoke() ?? state.Brightness, 0, 100);   // hardware value wins if readable
        _speed = state.Speed;
        _reverseDirection = state.Direction == 2;
        _color = FromPacked(state.Color);

        if (applyZone != null && zones > 1)
        {
            Color[] def = [Colors.Red, Colors.Lime, Colors.Blue, Colors.Magenta];
            for (var i = 0; i < zones; i++)
            {
                var c = i < state.ZoneColors.Length ? FromPacked(state.ZoneColors[i]) : def[i % def.Length];
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

    /// <summary>Re-read this zone's live brightness (if readable) and reflect it in the slider without an
    /// apply/save. Called when the lighting panel is shown, to catch out-of-band Fn-key changes.</summary>
    public void SyncFromHardware()
    {
        var read = _readBrightness;
        if (read == null) return;

        // The read is a WMI/ACPI call (tens of ms, serialized behind the global WMI lock) — never on the UI
        // thread or fast key input stutters. Off-thread + self-coalescing: at most one read in flight, with a
        // single pending re-read, so a burst of presses tracks the latest value without queuing up.
        lock (_readGate)
        {
            if (_reading) { _readPending = true; return; }
            _reading = true;
        }
        Task.Run(() =>
        {
            while (true)
            {
                int? b;
                try { b = read.Invoke(); } catch { b = null; }
                if (b is { } v) Dispatcher.UIThread.Post(() => SyncBrightness(v));
                lock (_readGate)
                {
                    if (!_readPending) { _reading = false; return; }
                    _readPending = false;   // do one more pass to catch the latest
                }
            }
        });
    }

    /// <summary>Point this panel at another mode's persisted state, reflect it in the UI, and re-apply it to
    /// the device. Called when the performance mode changes. Unlike startup, this ALWAYS applies: the app is
    /// already actively driving the lighting, so leaving the previous mode's colours on the device would be
    /// wrong (this is why e.g. the lightbar seemed "stuck" when switching to a mode it wasn't set in). A mode
    /// never configured yet inherits the look we're leaving (and remembers it), so the switch stays coherent
    /// instead of snapping to a bare default.</summary>
    public void Rebind(LightSettings state)
    {
        if (!state.Configured)
        {
            state.EffectIndex = SelectedEffectIndex;
            state.Brightness  = (int)Brightness;
            state.Speed       = (int)Speed;
            state.Direction   = ReverseDirection ? 2 : 1;
            state.Color       = Pack(Color);
            state.ZoneColors  = Zones.Select(z => Pack(z.Color)).ToArray();
            state.Configured  = true;
            _save();
        }

        _state = state;
        _loading = true;
        SelectedEffectIndex = _effects.Count > 0 ? Math.Clamp(state.EffectIndex, 0, _effects.Count - 1) : 0;
        Brightness = Math.Clamp(state.Brightness, 0, 100);
        Speed = state.Speed;
        ReverseDirection = state.Direction == 2;
        Color = FromPacked(state.Color);
        for (var i = 0; i < Zones.Count; i++)
            Zones[i].Color = i < state.ZoneColors.Length ? FromPacked(state.ZoneColors[i]) : Zones[i].Color;
        UpdateColorMode();
        _loading = false;
        ApplyNow();
    }

    /// <summary>Reflect a hardware-reported brightness in the slider without triggering an apply/save
    /// (used to sync to Fn-key changes). The <c>_loading</c> guard makes OnBrightnessChanged a no-op.</summary>
    private void SyncBrightness(int value)
    {
        value = Math.Clamp(value, 0, 100);
        // A read-back of 0 while the app is driving a non-zero brightness is spurious: the OPMODE profile-flash
        // (follows-profile mode) zeroes the EC's keyboard-brightness register even though the keyboard is lit by
        // the STATIC re-apply — and that register stays 0 until the next firmware switch-flash, incl. across a
        // follows-profile ON->OFF flip. Ignore it so the slider isn't snapped to 0 (and left stuck there). The
        // app's stored brightness is authoritative; a genuine Fn-key dim shows up as a non-zero change and syncs.
        if (value == 0 && Brightness > 0) return;
        if ((int)Brightness == value) return;
        _loading = true;
        Brightness = value;
        _loading = false;
    }

    partial void OnSelectedEffectIndexChanged(int value) { UpdateColorMode(); Schedule(); }
    partial void OnColorChanged(Color value) => Schedule();
    partial void OnBrightnessChanged(double value) => Schedule();
    partial void OnSpeedChanged(double value) => Schedule();
    partial void OnReverseDirectionChanged(bool value) => Schedule();

    private void OnZoneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ZoneColorViewModel.Color)) Schedule();
    }

    private RgbModeInfo Current => _effects[Math.Clamp(SelectedEffectIndex, 0, _effects.Count - 1)];

    private void UpdateColorMode()
    {
        var e = Current;
        HasSpeed = e.HasSpeed;
        HasDirection = e.HasDirection;
        ShowZones = e is { HasColor: true, HasSpeed: false } && Zones.Count > 1;   // static + multi-zone -> per-zone swatches
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
        byte b = (byte)Brightness, s = (byte)Speed, d = (byte)(ReverseDirection ? 2 : 1);
        if (ShowZones && _applyZone != null)
            for (var i = 0; i < Zones.Count; i++)
            {
                var c = Zones[i].Color;
                _applyZone(i, b, new AccentColor(c.R, c.G, c.B));
            }
        else
            _applyAll(Current, new AccentColor(Color.R, Color.G, Color.B), b, s, d);
    }

    private void SaveState()
    {
        _state.Configured = true;
        _state.EffectIndex = SelectedEffectIndex;
        _state.Brightness = (int)Brightness;
        _state.Speed = (int)Speed;
        _state.Direction = ReverseDirection ? 2 : 1;
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

/// <summary>A plain (non-RGB) keyboard backlight: discrete brightness levels only. The slider snaps to the
/// hardware's steps (<see cref="MaxLevel"/>; e.g. Dell 0..2 Off/Dim/Bright) — arbitrary values aren't
/// possible, so it exposes tick stops rather than a free 0-100 range. Applies off the UI thread (serialized)
/// and snaps back to the value the hardware actually took.</summary>
public sealed partial class BacklightViewModel : ObservableObject
{
    private readonly IKeyboardBrightness _port;
    private readonly Func<int, bool> _apply;
    private readonly HwSerial _hw = new();
    private int _desired;
    private bool _syncing;

    public int MaxLevel { get; }
    public IReadOnlyList<string> Names { get; }

    [ObservableProperty] private double _level;
    [ObservableProperty] private string _levelName = "";

    public BacklightViewModel(IKeyboardBrightness port, Func<int, bool> apply)
    {
        _port = port;
        _apply = apply;
        MaxLevel = port.MaxLevel;
        Names = NamesFor(MaxLevel);
        _level = Math.Clamp(port.Get(), 0, MaxLevel);
        _desired = (int)_level;
        UpdateName();
    }

    partial void OnLevelChanged(double value)
    {
        UpdateName();
        if (_syncing) return;   // change came from a readback snap-back, not the user
        var lvl = Math.Clamp((int)Math.Round(value), 0, MaxLevel);
        _desired = lvl;
        _hw.Enqueue(() =>
        {
            _apply(lvl);
            var actual = _port.Get();
            Dispatcher.UIThread.Post(() =>
            {
                if (lvl != _desired || actual == (int)Level) return;   // superseded, or already matches
                _syncing = true; Level = actual; _syncing = false; UpdateName();
            });
        });
    }

    /// <summary>Re-read the live level (Fn key changed it) and reflect it without applying.</summary>
    public void SyncFromHardware() => _hw.Enqueue(() =>
    {
        var actual = _port.Get();
        Dispatcher.UIThread.Post(() =>
        {
            if (actual == (int)Level) return;
            _syncing = true; Level = actual; _syncing = false; UpdateName();
        });
    });

    private void UpdateName() => LevelName = Names[Math.Clamp((int)Math.Round(Level), 0, Names.Count - 1)];

    private static IReadOnlyList<string> NamesFor(int max) => max switch
    {
        1 => ["Off", "On"],
        2 => ["Off", "Dim", "Bright"],
        3 => ["Off", "Low", "Medium", "High"],
        _ => [.. Enumerable.Range(0, max + 1).Select(i => i == 0 ? "Off" : i.ToString())],
    };
}
