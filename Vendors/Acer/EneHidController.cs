using AcerHelper.Features;

namespace AcerHelper.Vendors.Acer;

// Cross-platform RGB controller: the ENE HID device (VID 0x0CF2 / PID 0x5130, 11-byte feature report id
// 0xA4) — the same controller OpenRGB drives. The packets are identical on every OS; only the transport
// differs, so this file is the Acer packet codec + zone model, and the per-OS partials supply the three
// transport hooks (OpenTransport/SetFeature/Dispose): EneHidController.Windows.cs uses HidSharp (Win32 HID
// API), EneHidController.Linux.cs talks to hidraw directly — HidSharp's Linux enumeration only sees USB
// HID, and on several models (e.g. Nitro AN18-61) this controller hangs off HID-over-I2C. It exposes its
// physical regions as RgbZone bricks: "Keyboard" (multi sub-zone) and, on models that have it, "Lightbar".
// Keyboard brightness read-back isn't on this HID interface — it's the gaming WMI's job (Windows only) —
// so that reader is injected by AcerDevice (null on Linux). Lazily opens the stream; IDisposable.
internal sealed partial class EneHidController : IRgbController
{
    private const int VID = 0x0CF2, PID = 0x5130, FeatureLen = 11;

    // ENE RGB packet: A4 [TGT] [MODE] [BRI 0..0x64] [SPD] [FLAG] c0 c1 c2 [ZONEMASK] 00
    // Colour byte ORDER is mode-dependent (verified on Nitro AN18-61): the arbitrary-colour writes — keyboard
    // STATIC and the lightbar (A4 65) — render the three bytes as R,G,B (UI red 255,0,0 must go out as FF 00 00,
    // else the lightbar shows blue). The OPMODE profile-flash handler is a *separate* firmware path that instead
    // recognises its per-profile palette in B,G,R and whitelists it (see SetProfileFlash). Send() below emits the
    // R,G,B arbitrary-colour order; SetProfileFlash emits B,G,R itself and does not route through Send().
    // Zone masks: keyboard has 4 zones (0x0F = all); the lightbar has 5 (0x1F = all).
    private const byte ReportId = 0xA4, TgtKeyboard = 0x21, TgtLightbar = 0x65, OpMode = 0x06,
                       FlagStatic = 0x01, FlagEffect = 0x02, KbAllZones = 0x0F, LbAllZones = 0x1F, FullBright = 0x64;

    private readonly List<RgbZone> _zones = [];

    public EneHidController(int keyboardZones, bool hasLightbar, Func<int?>? readKeyboardBrightness)
    {
        if (!OpenTransport()) return;   // no ENE interface -> no zones -> composition skips lighting

        // Feature writes go through a single background worker (see SetFeature), never the caller's (UI) thread:
        // WriteFeature is a synchronous no-timeout HID write, and on HID-over-I2C models (AN18-61) it can block
        // for a long time when that shared bus is saturated — e.g. an external USB-C display, worst at boot.
        // Doing it on the UI thread froze the whole app until the bus freed (monitor unplug); the worker keeps
        // such a stall off the UI thread. Started only on the keep-path (transport opened above).
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "ene-hid-writer" };
        _worker.Start();

        _zones.Add(new RgbZone("Keyboard", keyboardZones,
            RgbEffects.Keyboard.Select(e => e.ToModeInfo()).ToList(),
            ApplyKeyboard, ApplyKeyboardZone, readKeyboardBrightness));

        if (hasLightbar)
            _zones.Add(new RgbZone("Lightbar", subZones: 1,
                RgbEffects.Lightbar.Select(e => e.ToModeInfo()).ToList(),
                ApplyLightbar, canFollowProfile: true));
    }

    public IReadOnlyList<RgbZone> Zones => _zones;

    // Acer-owned settings key for the lightbar's "follows performance profile" preference, surfaced through the
    // neutral IRgbController port so the app persists it generically (Settings stays vendor-agnostic). Only
    // present when this device actually has a lightbar (a CanFollowProfile zone).
    public string? ProfileFollowKey => _zones.Any(z => z.CanFollowProfile) ? "acer.lightbarFollowsProfile" : null;

    private bool ApplyKeyboard(RgbModeInfo effect, byte brightness, byte speed, byte direction, AccentColor color)
    {
        var e = (RgbEffect)effect.Handle;
        return Send(TgtKeyboard, e.ModeByte, e.IsEffect, brightness, speed, Dir(e, direction), color, KbAllZones);
    }

    private bool ApplyKeyboardZone(int zoneIndex, byte brightness, AccentColor color)
        => Send(TgtKeyboard, RgbEffects.StaticModeByte, isEffect: false, brightness, speed: 0, FlagStatic, color, (byte)(1 << zoneIndex));

    // The performance-profile "operating mode" flash is a GLOBAL write (keyboard target 0x21, mode 0x06) that
    // paints BOTH the keyboard and the lightbar at once. Unlike the arbitrary-colour paths (which the firmware
    // renders R,G,B), the OPMODE handler recognises its per-profile palette in B,G,R and whitelists it — anything
    // else reverts to amber. So this path is NOT routed through Send() (that applies the R,G,B arbitrary order):
    // it emits the palette colour in B,G,R directly, reproducing byte-for-byte what NitroSense sends. This is how
    // the lightbar gets its per-profile colour when it "follows the profile" — it has no standalone software
    // colour, so we re-send this on each profile switch. See docs/lighting-an18-61.md.
    public bool SetProfileFlash(AccentColor color)
        => SetFeature([ReportId, TgtKeyboard, OpMode, 0x00, 0x00, FlagEffect, color.B, color.G, color.R, KbAllZones, 0x00]);

    // Turn every zone off — blanks the backlight while it's hidden under a shut lid in clamshell (keep-awake)
    // mode; the app restores it by re-applying the current mode's lighting on lid-open. The keyboard honours the
    // brightness byte, so a STATIC write at brightness 0 darkens it; the lightbar ignores that byte (see
    // ApplyLightbar), so it's darkened with a black STATIC colour instead. A follows-profile lightbar (no panel)
    // is included too — its palette is repainted from the profile flash on the restore. Doesn't touch stored state.
    public bool Blank()
    {
        var off = new AccentColor(0, 0, 0);
        var ok = Send(TgtKeyboard, RgbEffects.StaticModeByte, isEffect: false, brightness: 0, speed: 0, FlagStatic, off, KbAllZones);
        if (_zones.Any(z => z.CanFollowProfile))   // model has a lightbar
            ok |= Send(TgtLightbar, RgbEffects.StaticModeByte, isEffect: false, FullBright, speed: 0, FlagStatic, off, LbAllZones);
        return ok;
    }

    private bool ApplyLightbar(RgbModeInfo effect, byte brightness, byte speed, byte direction, AccentColor color)
    {
        var e = (RgbEffect)effect.Handle;
        // The lightbar ignores the HID brightness byte, so emulate brightness by scaling the colour and send
        // full brightness (works for colour modes; self-cycling effects generate their own colours).
        return Send(TgtLightbar, e.ModeByte, e.IsEffect, FullBright, speed, Dir(e, direction), Scale(color, brightness), LbAllZones);
    }

    // Report byte[5] is the effect DIRECTION. For a directional effect (e.g. Wave) it's the user's choice
    // (1/2); otherwise it's the mode default the firmware expects — 0x02 for animated effects, 0x01 for static.
    private static byte Dir(RgbEffect e, byte direction)
        => e.HasDirection ? (direction is 1 or 2 ? direction : FlagStatic)
                          : (e.IsEffect ? FlagEffect : FlagStatic);

    private bool Send(byte target, byte mode, bool isEffect, byte brightness, byte speed, byte direction, AccentColor c, byte zoneMask)
        => SetFeature([ReportId, target, mode, brightness, isEffect ? speed : (byte)0,
                       direction, c.R, c.G, c.B, zoneMask, 0x00]);   // arbitrary colours render R,G,B on the wire

    private static AccentColor Scale(AccentColor c, byte brightness)
    {
        var b = Math.Clamp((int)brightness, 0, 100);
        return new AccentColor((byte)(c.R * b / 100), (byte)(c.G * b / 100), (byte)(c.B * b / 100));
    }

    // ---- serialized background writer ----
    // Every feature report is enqueued here and written by ONE long-lived worker thread — NOT the caller's
    // (UI) thread. WriteFeature (the per-OS transport) is a synchronous, no-timeout HID write that can block
    // hard on a contended HID-over-I2C bus; off the UI thread, that stall freezes only the worker, so the app
    // stays responsive. Fire-and-forget: SetFeature only enqueues. Writes coalesce by region (see SameRegion)
    // so a stalled worker can't accumulate — and won't replay — a flood of stale writes: when the bus frees it
    // applies just the latest state per region. A single in-flight write is also the de-facto circuit breaker
    // (the worker can't issue a second write while one blocks), keeping the app's bus contention minimal.
    private readonly object _gate = new();
    private readonly List<byte[]> _pending = [];   // ordered; a superseded same-region write is moved to the TAIL
    private Thread? _worker;
    private bool _stopping;
    private const int MaxPending = 32;             // backstop only; coalescing keeps this to a handful

    // Inter-write pacing: a small gap between consecutive feature reports on the worker (NOT the UI thread —
    // this only ever stalls the writer). A full keyboard apply is several back-to-back reports (profile-flash +
    // per-zone paints + lightbar); on a HID-over-I2C bus that an externally-booted display is contending, a
    // tight burst tends to land some reports corrupted (amber fallback) and others clean ("half green/half
    // orange"). Spacing the reports decorrelates them so they don't all fall inside one contention window —
    // it can't phase-lock to the display's traffic, only randomise phase, so it *reduces* the odds of a fully
    // corrupt apply rather than guaranteeing a clean one. 5 reports × this delay stays well under the ~120 ms
    // apply debounce, so a normal apply is still visually instant. 0 disables.
    private const int PacingMs = 10;

    // Enqueue one report. Runs on the caller's thread (UI); never blocks, never touches hardware. Returns true
    // = accepted onto the queue (no lighting caller consumes the result — the old "hardware acknowledged"
    // meaning is gone).
    private bool SetFeature(byte[] report)
    {
        lock (_gate)
        {
            if (_stopping) return false;
            for (var i = 0; i < _pending.Count; i++)
                // Coalesce a superseded same-region write by MOVING it to the tail (not replacing in place): the
                // coordinator always enqueues the profile-flash (mode 0x06) BEFORE the keyboard paint (mode 0x02)
                // so the custom colour lands on top of the global flash. Move-to-tail orders each region by its
                // most-recent enqueue, so the latest paint stays AFTER the latest flash in every interleaving —
                // including a multi-second bus stall where a re-emitted flash+paint pair queues behind an
                // in-flight write. In-place replacement would strand an early paint ahead of a later flash and
                // leave the keyboard showing the flash palette instead of the custom colour.
                if (SameRegion(_pending[i], report)) { _pending.RemoveAt(i); _pending.Add(report); Monitor.Pulse(_gate); return true; }
            if (_pending.Count >= MaxPending) _pending.RemoveAt(0);   // defensive FIFO evict; unreachable in practice
            _pending.Add(report);
            Monitor.Pulse(_gate);
        }
        return true;
    }

    // Same region = same target (byte 1), mode (byte 2) and zone mask (byte 9), so a brightness/colour drag
    // collapses to its latest value. The profile-flash (mode 0x06, which no keyboard effect uses) and each
    // keyboard sub-zone (distinct zone masks) are their own regions, so a flash never collapses a paint and
    // vice-versa; their relative order is handled by the move-to-tail coalescing in SetFeature.
    private static bool SameRegion(byte[] a, byte[] b)
        => a.Length > 9 && b.Length > 9 && a[1] == b[1] && a[2] == b[2] && a[9] == b[9];

    private void WorkerLoop()
    {
        // First action on the writer thread (never the UI thread): optionally re-initialise the HID transport
        // by restarting the ENE I2C-HID device node — the software analog of the physical display replug that
        // recovers a boot-with-display corrupted channel. Blocking PnP work, hence here and not in the ctor.
        // Best-effort and bounded; no-op on Linux and when not applicable (see the Windows implementation).
        TryReinitTransport();
        while (true)
        {
            byte[] report;
            lock (_gate)
            {
                while (_pending.Count == 0 && !_stopping) Monitor.Wait(_gate);
                if (_pending.Count == 0) return;   // stopping and drained
                report = _pending[0];
                _pending.RemoveAt(0);
            }
            // WriteFeature drops the transport handle on failure so the next write re-opens (a bad boot-time
            // handle otherwise sticks forever); it swallows its own errors, the catch is belt-and-braces.
            try { WriteFeature(report); } catch { /* keep the worker alive */ }
            // Space the next report (see PacingMs). Outside _gate, so SetFeature can keep enqueuing meanwhile;
            // a trailing sleep with nothing queued only delays the worker's return to Wait — harmless.
            if (PacingMs > 0) Thread.Sleep(PacingMs);
        }
    }

    private void StopWorker()
    {
        Thread? t;
        lock (_gate) { _stopping = true; _pending.Clear(); Monitor.Pulse(_gate); t = _worker; }
        // Bounded: a worker stuck inside a blocked write is a background thread, so a timed-out Join can't keep
        // the process alive — we proceed and let CloseTransport unstick it (disposing the handle faults the write).
        t?.Join(TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        StopWorker();       // stop enqueuing/writing first ...
        CloseTransport();   // ... then release the handle (best-effort; also unsticks a blocked worker write)
    }

    // ---- transport, per-OS (found device? / send one feature report / release) ----
    private partial bool OpenTransport();
    private partial bool WriteFeature(byte[] report);
    private partial void CloseTransport();
    // Optional one-shot transport re-initialisation, run as the writer thread's first action. Windows restarts
    // the ENE I2C-HID device node (see EneHidController.Windows.cs); Linux no-op.
    private partial void TryReinitTransport();
}
