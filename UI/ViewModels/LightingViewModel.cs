using System.Collections.ObjectModel;
using System.ComponentModel;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure;
using AcerHelper.Localization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>The lighting drawer root: one panel per RGB zone the device advertises (keyboard, lightbar,
/// or anything a future controller exposes), shown as tabs. Built generically from <see cref="IRgbDevice"/>
/// — no fixed keyboard/lightbar assumptions. Each panel holds its persisted state as a VALUE (a
/// <see cref="LightZoneState"/> for the mode it is bound to) and writes it back through the mode door
/// (<see cref="ILightZoneMode"/>), which is what replaced the live dictionary this section used to hold and
/// edit in place.</summary>
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
    private readonly Action<bool> _saveFollowsProfile;
    private readonly Action<Action>? _post;   // UI-thread marshaller handed to every panel (null -> the real one)
    private readonly Func<DateTime> _clock;   // time source forwarded to every panel (flash-suspicion window)

    // THE MODE THIS SECTION IS BOUND TO, and the values of its zones. The mode is FIXED when the door is taken
    // (LaptopService.LightsForCurrentMode), so every edit here lands in the mode the user is looking at — which
    // is what the old live dictionary did too, since the read handed over one mode's bucket and the UI wrote
    // into that. Reload swaps both when the performance mode changes.
    //
    // THE MODE-SCOPED USE CASES ARE BUILT ALONGSIDE THE MODE, not kept as one instance with a settable door: they
    // own the door through their constructors (Application/LightZone.cs), and rebuilding them HERE, at the moment
    // the door is taken, is what keeps the fixed-mode property — a stale instance could not be handed a mode it
    // was not built for, and a mode change builds fresh ones over the new door with the new values.
    private ReadLightZone _readZone;
    private ApplyLightZone _applyZone;
    private readonly Dictionary<string, LightZoneState> _lights = [];

    /// <summary>True when the device has a follow-capable zone (a lightbar) — the switch is only shown then.</summary>
    public bool ShowFollowsProfile => _followZones.Count > 0;

    /// <summary>When on (default), follow-capable zones (the lightbar) are left to the firmware — their
    /// per-profile palette colour, flash-free — and get no panel. When off they become normal user-controlled
    /// zones (custom colour/effects), at the cost of a brief palette flash on each profile switch.</summary>
    [ObservableProperty] private bool _followsProfile;

    /// <summary><paramref name="mode"/> is the CURRENT performance mode, as the door this section reads and
    /// writes lighting through (<see cref="LaptopService.LightsForCurrentMode()"/>). It replaces the pair the
    /// constructor used to take — the stored zone dictionary and the create-if-missing callback — because the
    /// dictionary WAS the settings graph, held here and written in place, which the owner overruled. On a mode
    /// change the panels are rebound through <see cref="Reload"/>. <paramref name="backlight"/> is a plain
    /// (non-RGB) backlight, if any.
    ///
    /// <paramref name="post"/> is the UI-thread marshaller each panel (and the backlight) is built with; it
    /// defaults to <c>Dispatcher.UIThread.Post</c> and exists so a test can drive the whole section with a
    /// synchronous poster — the same seam, and the same measured reason, as
    /// <c>OptionsViewModel.TryCreate(device, o, post)</c>: the real dispatcher is thread-affine in a bare xUnit
    /// process, so a headless test cannot pump it (see <c>Eventually</c>). Without the seam the wave's rule could
    /// not be tested where the app actually calls it: a read that is not an event must leave state alone, and a
    /// "nothing changed" assertion against a post that never runs proves nothing.</summary>
    /// <param name="clock">The time source the panels' flash-suspicion window is measured against; defaults to
    /// <c>DateTime.UtcNow</c>. A test seam only (see <see cref="LightViewModel"/>); production never passes it.</param>
    public LightingViewModel(IRgbDevice? rgb, ILightZoneMode mode,
                             bool followsProfile, Action<bool> saveFollowsProfile,
                             IKeyboardBrightness? backlight = null, Func<int, bool>? applyBacklight = null,
                             Action<Action>? post = null, Func<DateTime>? clock = null)
    {
        _readZone = new ReadLightZone(mode);
        _applyZone = new ApplyLightZone(mode);
        foreach (var (name, state) in mode.Stored()) _lights[name] = state;
        _saveFollowsProfile = saveFollowsProfile;
        _post = post;
        _clock = clock ?? (static () => DateTime.UtcNow);
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

    // The value this section is holding for a zone, creating it when the mode has none. The creation is the
    // READ's (Application/LightZone.cs ReadLightZone, built over this section's mode), not this section's: what
    // a mode with no entry yet IS — the zone in its defaults, held by the mode from that moment on, and not
    // persisted — is a rule the use case states and a stub pins, and it lived here as `EnsureLightZone` plumbing
    // before.
    private LightZoneState ZoneFor(string zone)
    {
        if (_lights.TryGetValue(zone, out var state)) return state;
        return _lights[zone] = _readZone.Run(zone);
    }

    // Write one zone's value back for the mode this section is bound to, and keep the local copy in step so the
    // next re-apply paints the user's own value rather than a stale one. The mirror happens only when the write
    // was TAKEN: a zone this machine does not advertise is refused by the use case, and a value the graph does
    // not hold must not be what the burst re-applies.
    private void Store(string zone, LightZoneState state)
    {
        if (_applyZone.Run(zone, state)) _lights[zone] = state;
    }

    // Build a panel for one zone, bound to the current mode's per-zone state (created on first sight). A
    // CONFIGURED zone's brightness comes from STORAGE (our settings are the source of truth); only an
    // UNCONFIGURED zone is seeded from what the firmware reports — a fresh install has no stored intent, and the
    // construction applies nothing there anyway. readBrightness is what the event path re-reads it with
    // (AdoptFromInput -> LightViewModel.AdoptFromHardware), the one place a read authors intent.
    private void BuildPanel(RgbZone zone)
    {
        var state = ZoneFor(zone.Name);
        // NOTE the missing `profileFlashPossible` predicate this panel used to take. The flash suspicion is no
        // longer a STATIC property of the device ("this machine's register CAN lie") but an EVENT the coordinator
        // arms when it actually sends a flash (NoteProfileFlash). A static predicate was permanently true on the
        // AN18-61 (its lightbar follows the profile), so it refused every genuine Fn dim to 0 — the owner's
        // "slider stops at 25%". See LightViewModel.NoteProfileFlash and docs/lighting-an18-61.md.
        var panel = new LightViewModel(zone.Name, zone.Effects, zone.SubZones,
            (e, c, b, s, d) => zone.ApplyEffect(e, b, s, d, c),
            zone.HasSubZones ? (i, b, c) => zone.ApplySubZone(i, b, c) : null,
            state, s => Store(zone.Name, s), zone.ReadBrightness, _post, _clock);
        Panels.Add(panel);
    }

    /// <summary>The app just drove a profile flash (<c>LightingCoordinator.Paint</c> is the one caller, at the
    /// instant it sends the palette). Arms the spurious-zero discrimination on every panel: until a hardware read
    /// proves the register live again, a 0 it reports over a lit keyboard is the flash's lie, not the user dimming.
    /// See <see cref="LightViewModel.NoteProfileFlash"/> for why this is an event and not the old static
    /// capability predicate.</summary>
    public void NoteProfileFlash()
    {
        foreach (var panel in Panels) panel.NoteProfileFlash();
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
    /// The panels write STRAIGHT to the zones — this method does not go through <c>LightingCoordinator.Paint</c>.
    ///
    /// The plain backlight is the one control here settled by a READ, and the only device it exists on is one
    /// with no RGB panels at all: it has no stored value to push (its slider is a deferred placeholder whose
    /// write is verified by read-back), so the drawer can do for it exactly what <see cref="Prime"/> does at
    /// startup — and it must, because the level can change with no event the app ever sees (another tool, a BIOS
    /// hotkey raising no raw input, an Fn press while the drawer was shut) and would otherwise sit at its
    /// startup value for the whole session.</summary>
    public void Reapply()
    {
        foreach (var panel in Panels) panel.Reapply();
        Backlight?.SyncFromHardware();
    }

    /// <summary>Rebind every panel to a different mode's lighting and re-apply it (called when the performance
    /// mode changes, so each mode carries its own lighting). The mode arrives as the door for THAT mode —
    /// resolved by the caller against a profile it had already read (LaptopService.LightsForCurrentMode) — and
    /// this section adopts both it and its values, so every later edit lands in the mode the user is looking
    /// at.</summary>
    public void Reload(ILightZoneMode mode)
    {
        _readZone = new ReadLightZone(mode);
        _applyZone = new ApplyLightZone(mode);
        _lights.Clear();
        foreach (var (name, state) in mode.Stored()) _lights[name] = state;
        foreach (var panel in Panels)
            panel.Rebind(ZoneFor(panel.Title));   // created on first sight, through the read use case
    }

    /// <summary>Push the values this section is holding at the device again — the re-apply the coordinator's
    /// burst, the drawer open, the resume and the lid each ask for.
    ///
    /// IT TAKES NO STATE FROM THE GRAPH AND READS NOTHING, but that alone does NOT make the burst safe to run
    /// mid-edit, as an earlier comment here claimed. The values live in <c>_lights</c>, and <c>_lights[zone]</c>
    /// is written ONLY when the panel's debounce tick commits an edit (<see cref="Store"/>) — so a repaint that
    /// lands inside the 120 ms debounce window sees the last COMMITTED value, not the edit in flight, and the
    /// old code here rebound every panel from it: the user's slider jumped back and the pending tick then
    /// applied and PERSISTED the stale value over the edit. That is the owner's «настройки яркости не пишутся»
    /// (the post-switch burst, still running ~3 s after a profile switch, was the tick that landed).
    ///
    /// THE PANEL DECIDES WHAT TO DO WITH IT (see <see cref="LightViewModel.Repaint"/>): with an edit pending it
    /// re-applies the panel's OWN current values and leaves the controls alone, so the edit survives and the
    /// pending tick commits it; with nothing pending it rebinds from the committed value, exactly as before
    /// (which also keeps the "a mode's first sight of a zone becomes configured" rule
    /// <see cref="LightViewModel.Rebind"/> carries). A genuine mode change does NOT come through here — it is
    /// <see cref="Reload"/>, whose panels adopt the new mode's stored values even mid-edit.</summary>
    public void Repaint()
    {
        foreach (var panel in Panels) panel.Repaint(_lights[panel.Title]);
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
    // WHEN the app last drove a profile flash (<see cref="NoteProfileFlash"/>), or null when no flash is in play.
    // This replaces the old static `profileFlashPossible` predicate, which asked the wrong question — "CAN this
    // machine's register lie?" is a permanent YES on any device with a follow-capable zone, so it refused every
    // genuine Fn dim to 0. The OPMODE flash only zeroes the EC register AT the switch, in a short window; gating
    // the refusal on that window (rather than on the capability) is what lets a plain Fn dim to 0 land. A 0 read
    // inside the window while the app believes the keyboard is lit is the flash's lie and is refused; a 0 read
    // once the window has passed is the user's real dim and is adopted. Guarded by _readGate with the read
    // coalescing, since the worker can post an adoption while the coordinator arms the suspicion.
    private DateTime? _flashSuspectedAt;
    private readonly Func<DateTime> _clock;
    // How long a flash keeps a read-back 0 suspicious. The register is zeroed AT the switch; the app's own
    // InputActivity re-reads (and the refresh pass) land within a few seconds, and any non-zero read clears the
    // suspicion early, so the window only has to outlast the immediate post-flash reads. 5 s is the same "short
    // window" the coordinator already uses for PendingTimeoutSeconds / WakeTailSeconds.
    private const double FlashSuspectSeconds = 5;
    // This zone's lighting for the mode the section is bound to, as a VALUE — replaced by Rebind when the mode
    // changes, and by every edit this panel makes. It is deliberately not the stored object: what crosses is
    // Domain's LightZoneState (Domain/LightZoneState.cs), and the write goes back through _store.
    private LightZoneState _state;
    // Write this zone's value back for the bound mode (LightingViewModel.Store -> the mode-scoped ApplyLightZone).
    private readonly Action<LightZoneState> _store;
    private readonly PeriodicSchedule _debounce;
    private bool _loading;
    private readonly object _readGate = new();   // guards the off-thread brightness read coalescing
    private bool _reading, _readPending;

    /// <summary>The zone's stable identity (e.g. "Keyboard"/"Lightbar") — used as the settings key and for
    /// panel matching, so it stays English. Bind the tab header to <see cref="DisplayName"/> instead.</summary>
    public string Title { get; }

    /// <summary>The localized tab header for this zone. The zone NAME is the settings key and for the shipped
    /// Acer zones ("Keyboard"/"Lightbar") maps to a neutral display key; an unknown zone falls back to the raw
    /// name (a key with no entry returns itself, so the tab still renders something honest).</summary>
    public string DisplayName => Loc.T(Title switch
    {
        "Keyboard" => "light.zone_keyboard",
        "Lightbar" => "light.zone_lightbar",
        _          => Title,
    });

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

    /// <param name="state">This zone's stored lighting for the mode being bound, as a value.
    /// <param name="store">Where an edit goes: the section's write for THIS zone, which is the contract call
    /// plus the local copy of the value (see <c>LightingViewModel.Store</c>).</param>
    /// <param name="post">The UI-thread marshaller the out-of-band read posts its adopted value through;
    /// defaults to <c>Dispatcher.UIThread.Post</c>. Injectable for the same reason the option rows' poster is
    /// (see <c>Eventually</c>): the real dispatcher is thread-affine and a bare xUnit process creates it from
    /// whichever thread first touches it, so a headless test cannot pump it. Same seam, same reason.</param>
    /// <remarks>The old <c>profileFlashPossible</c> predicate is GONE from this constructor: the flash suspicion
    /// is no longer passed in as a static capability but armed as an event (<see cref="NoteProfileFlash"/>), because
    /// a capability that is permanently true on the AN18-61 refused every genuine Fn dim to 0. See
    /// <see cref="AdoptBrightness"/>.</remarks>
    /// <param name="clock">The time source the flash-suspicion window is measured against; defaults to
    /// <c>DateTime.UtcNow</c>. A seam for the same reason the poster is: a test must be able to place a read
    /// INSIDE the post-flash window (the spurious 0) and OUTSIDE it (a genuine Fn dim) without sleeping for the
    /// whole window. Production never passes it.</param>
    public LightViewModel(string title, IReadOnlyList<RgbModeInfo> effects, int zones,
                          Action<RgbModeInfo, AccentColor, byte, byte, byte> applyAll, Action<int, byte, AccentColor>? applyZone,
                          LightZoneState state, Action<LightZoneState> store, Func<int?>? readBrightness = null,
                          Action<Action>? post = null, Func<DateTime>? clock = null)
    {
        Title = title;
        _effects = effects;
        _applyAll = applyAll;
        _applyZone = applyZone;
        _readBrightness = readBrightness;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _clock = clock ?? (static () => DateTime.UtcNow);
        _state = state;
        _store = store;
        _debounce = new PeriodicSchedule(ApplyDebounced, TimeSpan.FromMilliseconds(120), UiSchedule.Normal);
        EffectNames = effects.Select(e => Loc.T(e.Name)).ToList();

        // Restore the persisted selection (direct field writes -> the OnXxxChanged hooks don't fire).
        _selectedEffectIndex = effects.Count > 0 ? Math.Clamp(state.EffectIndex, 0, effects.Count - 1) : 0;
        // WAVE 6: the construction-time read that stays SYNCHRONOUS, where it happens at all, deliberately. The
        // reason is not the slider. `Brightness` is ALSO what the startup re-apply below sends to the device — the
        // single value the app pushes at launch — so a placeholder here would change what the HARDWARE is told,
        // not only what the user sees. The price is one EC transaction on the UI thread at BuildUi — and it is
        // paid ONLY for an UNCONFIGURED zone (see below): a zone the user has configured reads nothing.
        //
        // A CONFIGURED ZONE'S STORED VALUE IS AUTHORITATIVE — the read does NOT override it, not even with a
        // non-zero value. docs/state-and-events.md states why: a read answers "what does the wire hold now", and
        // after a profile flash the Acer keyboard-brightness register LIES (the OPMODE flash zeroes it while the
        // keyboard is lit — docs/lighting-an18-61.md), but even a NON-zero read is not the user's intent: a read
        // is not an author of intent except on the one event path (AdoptFromHardware/AdoptBrightness, below).
        // The read used to win here ("hardware value wins if readable"), which is the owner's bug on both
        // reported problems: a zone stored at 100 whose register read 0 came up with the slider at 0 and the
        // keyboard DARK (the construction re-apply below sends the slider), and a later profile switch inherited
        // that wrong look through the unconfigured-inheritance branch — the stored value was never wrong, only
        // what the panel believed. Reading a configured zone here also violated the rule the wave-6 comment
        // above already states, so it is removed rather than narrowed.
        //
        // THE READ SEEDS ONLY AN UNCONFIGURED ZONE — a fresh install, where there is no stored intent to apply
        // and the firmware's own level is the honest thing to show. Such a zone applies nothing (see the
        // Configured guard below), so the seed cannot leak to the device.
        _brightness = state.Configured
            ? Math.Clamp(state.Brightness, 0, 100)                              // our stored intent — the source of truth
            : Math.Clamp(readBrightness?.Invoke() ?? state.Brightness, 0, 100); // fresh install: seed from the firmware
        _speed = state.Speed;
        _reverseDirection = state.Direction == 2;
        _color = FromPacked(state.Color);

        if (applyZone != null && zones > 1)
        {
            Color[] def = [Colors.Red, Colors.Lime, Colors.Blue, Colors.Magenta];
            for (var i = 0; i < zones; i++)
            {
                var c = i < state.ZoneColors.Length ? FromPacked(state.ZoneColors[i]) : def[i % def.Length];
                var z = new ZoneColorViewModel(Loc.T("light.zone", i + 1), c);
                z.PropertyChanged += OnZoneChanged;
                Zones.Add(z);
            }
        }

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

    /// <summary>The app is about to drive a profile flash (the palette write that the Acer OPMODE handler
    /// performs at a switch). From this instant, for <see cref="FlashSuspectSeconds"/>, a 0 this zone reports over
    /// a lit keyboard is the flash's lie and is refused rather than adopted. The section/coordinator arms this at
    /// the flash instant (<see cref="LightingViewModel.NoteProfileFlash"/>), so the suspicion is tied to the EVENT
    /// that can zero the register, not to a permanent device capability. Any non-zero read clears it early (the
    /// register is provably live again), and the window itself expires, so a much later Fn dim to 0 lands.
    /// See docs/lighting-an18-61.md and docs/state-and-events.md.</summary>
    public void NoteProfileFlash()
    {
        lock (_readGate) _flashSuspectedAt = _clock();
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
    /// instead of snapping to a bare default.
    ///
    /// THE INHERITANCE IS A WRITE, so it goes through the same door every edit does: the look we are leaving is
    /// this panel's own current state, captured as a value and handed to the contract — where it used to be
    /// written into the stored object the panel was holding. Nothing else about the rule moved.</summary>
    public void Rebind(LightZoneState state)
    {
        // A mode change SUPERSEDES any edit in flight: stop the panel's debounce so the superseded edit cannot
        // tick later and save its stale capture over the target mode's state we are about to adopt. (A Repaint
        // of the SAME mode deliberately does NOT come here — see Repaint — because there the pending edit is the
        // intent we must keep.)
        _debounce.Stop();
        if (!state.Configured)
        {
            state = Captured(configured: true);
            _store(state);
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

    /// <summary>Re-apply what this panel is showing at the device for the SAME mode — the panel half of
    /// <see cref="LightingViewModel.Repaint"/>, i.e. the post-switch burst, the drawer open, the resume and the
    /// lid. It is NOT a rebind, and the difference is load-bearing.
    ///
    /// THE RULE: while a user edit is pending (<c>_debounce.IsRunning</c>), the controls are NOT overwritten —
    /// the panel just applies its OWN current values. The edit is applied and saved by its pending tick a moment
    /// later, so the burst cannot revert it. <c>state</c> is the section's committed copy (behind the edit), so
    /// rebinding from it here would put the stale value back on the slider and the pending tick would then save
    /// that over the user's choice: the owner's «настройки яркости не пишутся». The read path already keeps the
    /// mirror guard (<c>AdoptBrightness</c>: "the hardware read raced ahead of the apply"); this is the same
    /// guard on the re-apply path.
    ///
    /// WITH NO EDIT PENDING it rebinds from <paramref name="state"/>, exactly as the old shape did: that is what
    /// re-applies an adopted/Fn value the section has committed, and what marks a mode's first sight of a zone
    /// as configured (<see cref="Rebind"/> carries that rule).
    ///
    /// A MODE CHANGE IS <see cref="LightingViewModel.Reload"/> → <see cref="Rebind"/>, NOT this method, and it
    /// still ADOPTS the new mode even with an edit in flight (a mode change is a new intent that supersedes the
    /// edit, and <c>Rebind</c> stops the superseded debounce). "Adopt the target" versus "do not touch the
    /// pending edit" is thus distinguished by the CALL, not by guessing from the values.</summary>
    public void Repaint(LightZoneState state)
    {
        if (_debounce.IsRunning) { ApplyNow(); return; }   // same mode: never clobber an edit in flight
        Rebind(state);
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
    /// on the user-edit path.
    ///
    /// WHY A 0 CAN BE REFUSED, AND WHEN. The Acer OPMODE profile flash zeroes the EC's keyboard-brightness
    /// register at the switch even though the keyboard stays lit by the STATIC re-apply (docs/lighting-an18-61.md),
    /// so a 0 read in that moment is the register lying, not the user dimming. A 0 read at any other time is a
    /// genuine Fn-key dim to off, which MUST reach 0 (the owner's "slider stops at 25%": the old guard keyed on
    /// the static capability — permanently true on this hardware — and refused it forever). The discriminator is
    /// therefore the flash EVENT WINDOW (<see cref="NoteProfileFlash"/> / <see cref="_flashSuspectedAt"/>): refuse
    /// the 0 only while a flash we drove is still fresh, and adopt it otherwise. Any non-zero read proves the
    /// register live and clears the suspicion early, so a flash whose 0 was already refused can never mask a
    /// subsequent real read either.</summary>
    private void AdoptBrightness(int value)
    {
        value = Math.Clamp(value, 0, 100);
        // A user edit is in flight (the debounce is pending): the hardware read raced ahead of the apply and
        // carries a stale value — accepting it would yank the slider back mid-drag, and (worse) the pending
        // debounce tick would then apply and PERSIST the stale value over the user's choice. The user wins;
        // the next input event re-reads after this apply has landed.
        if (_debounce.IsRunning) return;
        // A non-zero read is ground truth: the register is live, so whatever flash was suspected is over. Clear
        // it before deciding, which also keeps a long-ago flash from poisoning a later genuine dim to 0.
        if (value > 0) { lock (_readGate) _flashSuspectedAt = null; }
        // The flash's lie: a 0 reported while a flash we drove is still fresh and the app believes the keyboard
        // is lit (Brightness > 0). Refuse it — the register zeroes at the switch, and the next real read returns
        // the true level. Outside that window (or with Brightness already 0, where a 0 is our own intent) the 0 is
        // the user's dim and is adopted.
        if (value == 0 && Brightness > 0 && FlashSuspected())
            return;
        if ((int)Brightness == value) return;
        _loading = true;
        Brightness = value;
        _loading = false;
        // The read is the new intent, so persist it — and `with` states that nothing else in the state moves,
        // where the assignment this replaced left the same guarantee only as a comment.
        _state = _state with { Brightness = value };
        _store(_state);
    }

    // Whether a flash we drove is still fresh enough that a 0 read is the register lying rather than the user
    // dimming (see AdoptBrightness). Static window measured against the injected clock so a test can place a read
    // on either side of it without sleeping.
    private bool FlashSuspected()
    {
        lock (_readGate)
            return _flashSuspectedAt is { } at && (_clock() - at).TotalSeconds < FlashSuspectSeconds;
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
        _debounce.Restart();
    }

    // The debounce tick: stop the (periodic) schedule first, so a late tick cannot re-enter, then apply once.
    private void ApplyDebounced() { _debounce.Stop(); ApplyNow(); SaveState(); }

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

    /// <summary>This panel's controls as a value — the whole zone at once, which is the shape the contract
    /// takes and the shape the UI has always written (every field it knows, then save). <paramref name="configured"/>
    /// is the one field that is not read off a control: a user edit and the inheritance both mean "the user has
    /// this zone now", which is what the stored flag records (and what decides whether the next launch drives
    /// this zone at all).</summary>
    private LightZoneState Captured(bool configured) => new(
        Configured: configured,
        EffectIndex: SelectedEffectIndex,
        Brightness: (int)Brightness,
        Speed: (int)Speed,
        Direction: ReverseDirection ? 2 : 1,
        Color: Pack(Color),
        ZoneColors: Zones.Select(z => Pack(z.Color)).ToArray());

    private void SaveState()
    {
        _state = Captured(configured: true);
        _store(_state);
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
        1 => [Loc.T("level.off"), Loc.T("level.on")],
        2 => [Loc.T("level.off"), Loc.T("level.dim"), Loc.T("level.bright")],
        3 => [Loc.T("level.off"), Loc.T("level.low"), Loc.T("level.medium"), Loc.T("level.high")],
        _ => [.. Enumerable.Range(0, max + 1).Select(i => i == 0 ? Loc.T("level.off") : i.ToString())],
    };
}
