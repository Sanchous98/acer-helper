using System.Collections.ObjectModel;
using System.ComponentModel;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Localization;
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
    // Create-if-missing a zone entry in the live per-mode dict, UNDER the service's _state lock — the dict is
    // aliased into LaptopService.Settings and now enumerated by a background Save(), so the structural insert
    // must be serialized with it (see LaptopService.EnsureLightZone).
    private readonly Func<Dictionary<string, LightSettings>, string, LightSettings> _ensureZone;
    private readonly Action<Action>? _post;   // UI-thread marshaller handed to every panel (null -> the real one)
    // The virtual lighting surface the app publishes for a host (Windows Dynamic Lighting / any LampArray app),
    // or null where this machine has none. Read only for its ownership flag — see Reapply.
    private readonly IDynamicLighting? _host;
    private Dictionary<string, LightSettings> _lights;   // current performance mode's per-zone state (swapped by Reload)

    /// <summary>True when the device has a follow-capable zone (a lightbar) — the switch is only shown then.</summary>
    public bool ShowFollowsProfile => _followZones.Count > 0;

    /// <summary>When on (default), follow-capable zones (the lightbar) are left to the firmware — their
    /// per-profile palette colour, flash-free — and get no panel. When off they become normal user-controlled
    /// zones (custom colour/effects), at the cost of a brief palette flash on each profile switch.</summary>
    [ObservableProperty] private bool _followsProfile;

    /// <summary><paramref name="lights"/> is the CURRENT performance mode's per-zone state (from
    /// LaptopService.LightsForCurrentMode). On a mode change the panels are rebound to the new mode's state
    /// via <see cref="Reload"/>. <paramref name="backlight"/> is a plain (non-RGB) backlight, if any.
    ///
    /// <paramref name="post"/> is the UI-thread marshaller each panel (and the backlight) is built with; it
    /// defaults to <c>Dispatcher.UIThread.Post</c> and exists so a test can drive the whole section with a
    /// synchronous poster — the same seam, and the same measured reason, as
    /// <c>OptionsViewModel.TryCreate(device, o, post)</c>: the real dispatcher is thread-affine in a bare xUnit
    /// process, so a headless test cannot pump it (see <c>Eventually</c>). Without the seam the wave's rule could
    /// not be tested where the app actually calls it: a read that is not an event must leave state alone, and a
    /// "nothing changed" assertion against a post that never runs proves nothing.
    ///
    /// <paramref name="host"/> is the virtual lighting surface a host may be holding (AppController passes
    /// <c>LaptopService.LampArray</c>; null where this machine publishes none). This section reads it for its
    /// ownership flag and never drives it: while a host owns the surface the panels must not paint, and the
    /// re-assertion of the host's frame belongs to <c>LightingCoordinator</c>, which is the only caller that
    /// has to hand because something clobbered it.</summary>
    public LightingViewModel(IRgbDevice? rgb, Dictionary<string, LightSettings> lights,
                             Func<Dictionary<string, LightSettings>, string, LightSettings> ensureZone, Action save,
                             bool followsProfile, Action<bool> saveFollowsProfile,
                             IKeyboardBrightness? backlight = null, Func<int, bool>? applyBacklight = null,
                             Action<Action>? post = null, IDynamicLighting? host = null)
    {
        _lights = lights;
        _ensureZone = ensureZone;
        _save = save;
        _saveFollowsProfile = saveFollowsProfile;
        _post = post;
        _host = host;
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
            Backlight = new BacklightViewModel(backlight, applyBacklight, post);
    }

    // Build a panel for one zone, bound to the current mode's per-zone state (created on first sight). Seeds
    // brightness from what the firmware reports (Fn keys change it out-of-band); readBrightness is what the
    // event path re-reads it with (AdoptFromInput -> LightViewModel.AdoptFromHardware).
    private void BuildPanel(RgbZone zone)
    {
        var state = _ensureZone(_lights, zone.Name);   // create-if-missing under the service lock (see _ensureZone)
        Panels.Add(new LightViewModel(zone.Name, zone.Effects, zone.SubZones,
            (e, c, b, s, d) => zone.ApplyEffect(e, b, s, d, c),
            zone.HasSubZones ? (i, b, c) => zone.ApplySubZone(i, b, c) : null,
            state, _save, zone.ReadBrightness, _post));
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

    /// <summary>Settle the deferred placeholders after a (re)build. Only the plain backlight needs this: its
    /// slider is built at 0 and takes its real level here (see <see cref="BacklightViewModel"/>). The RGB panels
    /// are deliberately NOT re-read — their construction value is already a reading and is what the startup
    /// re-apply sends, and re-reading them here is what <see cref="AdoptFromInput"/> exists to do on an event
    /// instead. Call at startup and after a language rebuild, never on a schedule: see docs/state-and-events.md.
    /// Opening the drawer runs the same read through <see cref="Reapply"/> — a moment of doubt is an event, not
    /// a schedule, and the rule above is about running it on a timer.</summary>
    public void Prime() => Backlight?.SyncFromHardware();

    /// <summary>An out-of-band input arrived (a special key): read the hardware and <b>adopt</b> what it says as
    /// the new intent. This is the ONE path on which a read changes state, and it is an event, not a schedule —
    /// the read only delivers the event's value. A read taken on a timer carries whatever the wire last held,
    /// which is how switching profiles used to lose the brightness of the mode being switched to.</summary>
    public void AdoptFromInput()
    {
        foreach (var panel in Panels) panel.AdoptFromHardware();
        Backlight?.SyncFromHardware();
    }

    /// <summary>The doubt moment (the drawer opening, a mode change, a resume): push OUR stored value at the
    /// device rather than asking the device what it holds. Re-applying is idempotent and cannot be wrong; reading
    /// can, because the write we just sent may not have landed yet.
    ///
    /// A HOST OWNS THE SURFACE (Windows Dynamic Lighting / a LampArray app) and the PANELS write nothing. The
    /// panels write STRAIGHT to the zones — this method does not go through <c>LightingCoordinator.Paint</c>,
    /// which is where every other painting path yields to the host — so an ungated re-apply would land the app's
    /// frame on a surface the host owns, and would land it out of band: the bridge's dedupe mirror
    /// (<c>LampArrayBridge._written</c>) is not told about it, so the host's next frame of the SAME colours is
    /// judged unchanged and skipped, and the app's colour stays on the keyboard until the host's frame moves by
    /// more than its colour epsilon. Nothing is lost by yielding: the stored value is what
    /// <c>LightingCoordinator.OnHostOwnerChanged</c> paints the moment the host lets go. The host's own frame is
    /// deliberately NOT re-asserted here — opening a drawer clobbers nothing, and the callers that do need a
    /// re-assertion are the ones <c>Paint</c> already serves.
    ///
    /// THE GATE IS ABOUT THE PANELS ONLY, because it is about the surface the host holds: the LampArray is built
    /// FROM the RGB device (<c>LaptopService.LampArray</c>), so a host owns the zones and nothing else. The plain
    /// backlight (<see cref="IKeyboardBrightness"/>) is separate hardware that no host claims — no LampArray
    /// carries it, and the only devices that expose it have no RGB device at all in today's tree — so its settle
    /// sits OUTSIDE the gate. Gating it would be a flag about one surface deciding another's: on a device
    /// with both (an RGB device whose zones the firmware follows, so no panel is built, beside a plain
    /// backlight — the shape LightingDrawerTests arranges) the level would keep its startup value for the whole
    /// session, which is the very symptom this read exists to close.
    ///
    /// The plain backlight is the one control here settled by a READ, and the only device it exists on is one
    /// with no RGB panels at all: it has no stored value to push (its slider is a deferred placeholder whose
    /// write is verified by read-back), so the drawer can do for it exactly what <see cref="Prime"/> does at
    /// startup — and it must, because the level can change with no event the app ever sees (another tool, a BIOS
    /// hotkey raising no raw input, an Fn press while the drawer was shut) and would otherwise sit at its
    /// startup value for the whole session.</summary>
    public void Reapply()
    {
        if (_host is not { HostOwnsLighting: true })
            foreach (var panel in Panels) panel.Reapply();
        Backlight?.SyncFromHardware();
    }

    /// <summary>Rebind every panel to a different mode's per-zone state and re-apply it (called when the
    /// performance mode changes, so each mode carries its own lighting).</summary>
    public void Reload(Dictionary<string, LightSettings> lights)
    {
        _lights = lights;   // keep for BuildPanel when the follow switch is flipped mid-mode
        foreach (var panel in Panels)
            panel.Rebind(_ensureZone(lights, panel.Title));   // create-if-missing under the service lock
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
    private readonly Action<Action> _post;   // UI-thread marshaller for an adopted read (see the ctor)
    private LightSettings _state;   // swapped by Rebind when the performance mode changes
    private readonly Action _save;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _loading;
    private readonly object _readGate = new();   // guards the off-thread brightness read coalescing
    private bool _reading, _readPending;

    /// <summary>The zone's stable identity (e.g. "Keyboard"/"Lightbar") — used as the settings key and for
    /// panel matching, so it stays English. Bind the tab header to <see cref="DisplayName"/> instead.</summary>
    public string Title { get; }

    /// <summary>The localized tab header for this zone (translation of <see cref="Title"/>).</summary>
    public string DisplayName => Loc.T(Title);

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

    /// <param name="post">The UI-thread marshaller the out-of-band read posts its adopted value through;
    /// defaults to <c>Dispatcher.UIThread.Post</c>. Injectable for the same reason the option rows' poster is
    /// (see <c>Eventually</c>): the real dispatcher is thread-affine and a bare xUnit process creates it from
    /// whichever thread first touches it, so a headless test cannot pump it. Same seam, same reason.</param>
    public LightViewModel(string title, IReadOnlyList<RgbModeInfo> effects, int zones,
                          Action<RgbModeInfo, AccentColor, byte, byte, byte> applyAll, Action<int, byte, AccentColor>? applyZone,
                          LightSettings state, Action save, Func<int?>? readBrightness = null,
                          Action<Action>? post = null)
    {
        Title = title;
        _effects = effects;
        _applyAll = applyAll;
        _applyZone = applyZone;
        _readBrightness = readBrightness;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _state = state;
        _save = save;
        EffectNames = effects.Select(e => Loc.T(e.Name)).ToList();

        // Restore the persisted selection (direct field writes -> the OnXxxChanged hooks don't fire).
        _selectedEffectIndex = effects.Count > 0 ? Math.Clamp(state.EffectIndex, 0, effects.Count - 1) : 0;
        // WAVE 6: THE ONE CONSTRUCTION-TIME READ IN THIS FILE THAT STAYS SYNCHRONOUS, deliberately. The reason is
        // not the slider. `Brightness` is ALSO what the startup re-apply below sends to the device — the single
        // value the app pushes at launch — so a placeholder here would change what the HARDWARE is told, not only
        // what the user sees. And the placeholder this wave uses everywhere else, 0, is the one value that must
        // not go in: the keyboard would come up dark on a configured zone, because the prime only re-reads the
        // slider and never re-applies. Deferring this read therefore means deferring the startup apply with it —
        // a device-visible change that cannot be checked without the machine. The price of leaving it: one EC
        // transaction on the UI thread at BuildUi.
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
                var z = new ZoneColorViewModel(Loc.T("Zone {0}", i + 1), c);
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

    /// <summary>Read this zone's live brightness and ADOPT it as the new intent — slider and storage. Called
    /// only on an out-of-band input event (a special key), because that is the only way a brightness the app did
    /// not author can appear: the Fn key is an author of intent exactly like the slider is, it just delivers its
    /// value through a read instead of through the UI. Never call this on a schedule — a read taken while our own
    /// write is still in flight carries the PREVIOUS value, and adopting it persists the wire's lag as the user's
    /// choice. See docs/state-and-events.md and <see cref="Reapply"/>.</summary>
    public void AdoptFromHardware()
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
                if (b is { } v) _post(() => AdoptBrightness(v));
                lock (_readGate)
                {
                    if (!_readPending) { _reading = false; return; }
                    _readPending = false;   // do one more pass to catch the latest
                }
            }
        });
    }

    /// <summary>The doubt moment at this panel's level: push OUR state at the device and read nothing. Called
    /// when the panel opens (see <see cref="LightingViewModel.Reapply"/>). Reading here is what loses the value:
    /// the write we sent may not have landed, so the register still holds the PREVIOUS profile's brightness (or
    /// the zero the profile flash leaves behind) and believing it undoes the switch. Re-applying is idempotent —
    /// the device is last-write-wins, and an already-correct value is visually silent.</summary>
    public void Reapply() => ApplyNow();

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

    /// <summary>ADOPT a hardware-reported brightness: move the slider and make it the STORED intent. Reached only
    /// from <see cref="AdoptFromHardware"/>, i.e. only from an out-of-band input event — the one path on which a
    /// read is an author of intent (docs/state-and-events.md). Storing is the half that used to be missing: the
    /// slider followed an Fn-key change while <c>_state</c> kept the old value, so the app and the hardware
    /// disagreed until the user happened to touch the slider again.
    ///
    /// <c>_loading</c> keeps this out of <c>Schedule()</c>, and only Brightness is written: <c>Configured</c> and
    /// the rest of the slider are deliberately left alone, so a key that moves a zone the user never configured
    /// (a fresh install) cannot make the app start driving that zone — that snapshot is <c>SaveState()</c>'s job
    /// on the user-edit path.</summary>
    private void AdoptBrightness(int value)
    {
        value = Math.Clamp(value, 0, 100);
        // A user edit is in flight (the debounce is pending): the hardware read raced ahead of the apply and
        // carries a stale value — accepting it would yank the slider back mid-drag, and (worse) the pending
        // debounce tick would then apply and PERSIST the stale value over the user's choice. The user wins;
        // the next input event re-reads after this apply has landed.
        if (_debounce.IsEnabled) return;
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
        _state.Brightness = value;   // the read is the new intent, so persist it — nothing else in _state moves
        _save();
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
        if (ShowZones && _applyZone != null && !AllZonesSameColor())
            // Genuinely-multicolour: one STATIC report per zone (unavoidable).
            for (var i = 0; i < Zones.Count; i++)
            {
                var c = Zones[i].Color;
                _applyZone(i, b, new AccentColor(c.R, c.G, c.B));
            }
        else
            // Single colour (or a non-zone effect): ONE all-zones report. For a uniform multi-zone keyboard this
            // replaces the 4 per-zone writes — which is what "half green / half orange" was (some per-zone reports
            // landing, others corrupting to the amber fallback on a contended HID-over-I2C bus). One report can't
            // be split across zones, so it removes that failure mode for the common uniform case.
            _applyAll(Current, new AccentColor(SingleColor.R, SingleColor.G, SingleColor.B), b, s, d);
    }

    // The colour to drive an all-zones write with: the shared zone colour when the keyboard is in per-zone
    // (static multi-zone) mode and all zones match, else the single-colour swatch.
    private Color SingleColor => ShowZones && Zones.Count > 0 ? Zones[0].Color : Color;

    private bool AllZonesSameColor()
    {
        if (Zones.Count == 0) return true;
        var first = Zones[0].Color;
        foreach (var z in Zones) if (z.Color != first) return false;
        return true;
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
    private readonly VerifiedHwValue<int> _hw;
    private bool _syncing;

    public int MaxLevel { get; }
    public IReadOnlyList<string> Names { get; }

    [ObservableProperty] private double _level;
    [ObservableProperty] private string _levelName = "";

    /// <param name="post">The UI-thread marshaller the serial worker posts corrections through; defaults to
    /// <c>Dispatcher.UIThread.Post</c>. Injectable so a test can observe a prime without a dispatcher — see
    /// <see cref="VerifiedHwValue{T}"/>.</param>
    public BacklightViewModel(IKeyboardBrightness port, Func<int, bool> apply, Action<Action>? post = null)
    {
        _port = port;
        _apply = apply;
        _hw = new VerifiedHwValue<int>(post);
        MaxLevel = port.MaxLevel;
        Names = NamesFor(MaxLevel);
        // WAVE 6: a PLACEHOLDER, not a reading — 0 is what a failed read gives (LightingViewModel.cs, the same
        // rule as the option rows). Unlike an RGB zone's brightness, this one is safe to defer: a backlight's
        // construction writes nothing to the device (the latch below is a field write, and no apply fires until
        // the user moves the slider), so the placeholder cannot leak outward. Its reads are all EVENTS or
        // startup: the prime is SyncFromHardware, called once at startup and again on a language rebuild
        // (LightingViewModel.Prime), wired to the Fn-key path through LightingViewModel.AdoptFromInput, and run
        // when the Lighting drawer opens (LightingViewModel.Reapply) — the three moments the level can be known
        // to have moved with no event of ours to say so.
        _level = 0;
        _hw.Latch((int)_level);
        UpdateName();
    }

    partial void OnLevelChanged(double value)
    {
        UpdateName();
        if (_syncing) return;   // change came from a readback snap-back, not the user
        var lvl = Math.Clamp((int)Math.Round(value), 0, MaxLevel);
        _hw.Apply(lvl, l => _apply(l), _port.Get, () => (int)Level, actual =>
        {
            _syncing = true; Level = actual; _syncing = false; UpdateName();
        });
    }

    /// <summary>Re-read the live level (Fn key changed it) and reflect it without applying.</summary>
    public void SyncFromHardware() => _hw.Sync(_port.Get, () => (int)Level, actual =>
    {
        _syncing = true; Level = actual; _syncing = false; UpdateName();
    });

    private void UpdateName() => LevelName = Names[Math.Clamp((int)Math.Round(Level), 0, Names.Count - 1)];

    private static IReadOnlyList<string> NamesFor(int max) => max switch
    {
        1 => [Loc.T("Off"), Loc.T("On")],
        2 => [Loc.T("Off"), Loc.T("Dim"), Loc.T("Bright")],
        3 => [Loc.T("Off"), Loc.T("Low"), Loc.T("Medium"), Loc.T("High")],
        _ => [.. Enumerable.Range(0, max + 1).Select(i => i == 0 ? Loc.T("Off") : i.ToString())],
    };
}
