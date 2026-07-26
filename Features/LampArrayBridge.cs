namespace AcerHelper.Features;

/// <summary>
/// The translation layer proper: takes lamp frames a host (Windows Dynamic Lighting, or any LampArray-aware
/// app) writes to our virtual HID device and turns them into this device's zone writes — and arbitrates
/// ownership of the backlight while it does, so the app and the OS don't fight over it.
///
/// Three problems make this more than a memcpy, all of them properties of the hardware rather than of
/// LampArray (see docs/lighting-an18-61.md):
///
///  1. RATE. A host paints at 30–60 Hz. The ENE controller hangs off HID-over-I2C and a full keyboard apply
///     is several feature reports; bursts land corrupted on a bus a display is contending (that is what the
///     10 ms pacing and the coalescing in EneHidController exist for). So frames are rate-limited HERE, to
///     the same interval the layout ADVERTISES as MinUpdateInterval — a well-behaved host then throttles
///     itself and we simply enforce it for the rest. Frames are last-one-wins in the transport, so slowing
///     down never builds a backlog; it drops intermediate frames, which is exactly right for lighting.
///  2. WRITE COUNT. Per-sub-zone writes are one report each, but a uniform colour across the whole zone is a
///     single all-zones report. Most host effects (solid colour, breathing, "match my accent colour") are
///     uniform, so collapsing them removes 3 of every 4 reports — the same trick LightViewModel.ApplyNow
///     uses, for the same reason. Unchanged sub-zones are skipped outright.
///  3. OWNERSHIP. While the host drives the surface, the app must stop painting it (G HUB likewise refuses to
///     configure LIGHTSYNC while Dynamic Lighting is on) — otherwise every profile switch, resume and 400 ms
///     re-apply tick would stomp the host's frame. <see cref="HostOwnsLighting"/> is that gate, and
///     <see cref="Reassert"/> is its counterpart: the EC forces the keyboard back to its amber profile-flash
///     on every profile switch and drops RGB across sleep, so after those events SOMEONE has to repaint —
///     and while the host owns the surface, that someone is us, from the last frame it sent.
///
/// Threading: one long-lived worker thread pumps the transport (a blocking wait) and applies frames; the zone
/// writes it makes are themselves non-blocking (EneHidController queues them onto its own writer). Public
/// members are safe to call from any thread. <see cref="OwnerChanged"/> fires on the worker thread — marshal
/// it if you touch UI state.
/// </summary>
public sealed class LampArrayBridge : IDisposable
{
    /// <summary>Slowest we ever repaint the hardware, and the value advertised to the host as
    /// MinUpdateInterval. 10 Hz is a deliberate compromise: fast enough that breathing/wave effects read as
    /// smooth on a 4-zone surface, slow enough that the I2C bus (and the EC) keep up under contention.</summary>
    public const int MinIntervalMs = 100;

    // Per-channel delta below which a lamp counts as unchanged (see LampColor.IsCloseTo). A host stepping a
    // gradient one unit per frame would otherwise generate a write per frame per zone forever.
    private const int ColorEpsilon = 5;

    // How long the worker keeps re-trying after the transport fails (driver unloaded, device removed): it just
    // stops. Re-enabling is a user action (or an app restart) — silently reconnecting in a loop would hide a
    // real problem and keep poking a device that isn't there.
    private readonly IRgbDevice _rgb;
    private readonly ILampArrayTransport _transport;
    private readonly Func<RgbZone, bool>? _include;

    private readonly Lock _gate = new();      // guards enable/disable + the worker handle
    private readonly Lock _apply = new();     // serializes a frame apply (worker) against a Reassert (caller)
    private Thread? _worker;
    private volatile bool _stopping;

    private LampArrayLayout? _layout;
    private LampColor[]? _written;            // last colours actually written to the hardware, for dedupe
    private LampColor[]? _frame;              // last complete frame from the host (for Reassert)

    public LampArrayBridge(IRgbDevice rgb, ILampArrayTransport transport, Func<RgbZone, bool>? include = null)
    {
        _rgb = rgb;
        _transport = transport;
        _include = include;
    }

    /// <summary>True while the virtual device is published and the worker is pumping.</summary>
    public bool Enabled { get; private set; }

    /// <summary>True while a host holds the surface (it took the device out of autonomous mode). The app's own
    /// lighting paths must yield while this is set — see LightingCoordinator.</summary>
    public bool HostOwnsLighting { get; private set; }

    /// <summary>Fires when <see cref="HostOwnsLighting"/> flips (on the worker thread). true = a host just took
    /// the surface; false = it released it (or the transport went away) and the app should repaint its own.</summary>
    public event Action<bool>? OwnerChanged;

    public string? LastError { get; private set; }

    /// <summary>How many lamps we currently expose (0 when disabled) — for the UI/diagnostics.</summary>
    public int LampCount => _layout?.LampCount ?? 0;

    /// <summary>Publish the virtual LampArray and start translating. Returns false (with
    /// <see cref="LastError"/> set) when the device has nothing to expose or the transport is unavailable —
    /// e.g. the driver isn't installed yet.</summary>
    public bool Enable()
    {
        lock (_gate)
        {
            if (Enabled) return true;
            LastError = null;

            var layout = LampArrayLayout.Build(_rgb, _include, MinIntervalMs);
            if (layout == null) { LastError = "no controllable lighting zones"; return false; }

            if (!_transport.Start(layout)) { LastError = _transport.LastError ?? "transport unavailable"; return false; }

            _layout = layout;
            _written = new LampColor[layout.LampCount];
            _frame = null;
            _stopping = false;
            Enabled = true;

            // Same shape as EneHidController's writer: one long-lived background thread, so a blocking wait on
            // the driver channel can never touch the UI thread.
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "lamparray-bridge" };
            _worker.Start();
            return true;
        }
    }

    /// <summary>Un-publish the virtual device and stop translating. If a host was holding the surface, this
    /// hands it back to the app (<see cref="OwnerChanged"/> false) so the caller repaints.</summary>
    public void Disable()
    {
        Thread? worker;
        lock (_gate)
        {
            if (!Enabled) return;
            Enabled = false;
            _stopping = true;
            worker = _worker;
            _worker = null;
        }

        // Stop() breaks the worker out of its blocking wait; then join briefly. The worker is a background
        // thread, so even a stuck join can't keep the process alive (same reasoning as EneHidController).
        try { _transport.Stop(); } catch { /* best-effort teardown */ }
        worker?.Join(TimeSpan.FromSeconds(1));

        _layout = null;
        _written = null;
        _frame = null;
        ReleaseOwnership();
    }

    /// <summary>Repaint the hardware from the host's last frame, ignoring the dedupe. Call after anything that
    /// clobbers the EC's RGB behind our back — a performance-profile switch (the EC forces its amber
    /// profile-flash), a resume from sleep, a lid-open restore. No-op unless a host currently owns the surface
    /// and has actually sent a frame. Runs on the caller's thread; the writes it issues are queued, not
    /// blocking, so this is safe from the UI thread.</summary>
    public void Reassert()
    {
        if (!Enabled || !HostOwnsLighting) return;
        lock (_apply)
        {
            if (_frame is { } f) Paint(f, force: true);
        }
    }

    // ---- worker ----

    private void WorkerLoop()
    {
        while (!_stopping)
        {
            // Blocking, last-one-wins: returns the NEWEST frame, or false once the transport is torn down.
            if (!_transport.WaitFrame(out var frame))
            {
                if (!_stopping) LastError = _transport.LastError;
                break;
            }

            if (frame.AutonomousMode)
            {
                // The host let go (it lost its exclusive lock, Dynamic Lighting was switched off, the app that
                // held it exited). Stop painting and tell the app to take its own lighting back — the surface
                // is otherwise frozen on whatever the last host frame was.
                ReleaseOwnership();
                continue;
            }

            if (!HostOwnsLighting)
            {
                HostOwnsLighting = true;
                // A fresh takeover must paint every lamp even if the colours match what we last wrote (the app
                // may have painted something else in between), so drop the dedupe history.
                lock (_apply) Array.Clear(_written!);
                try { OwnerChanged?.Invoke(true); } catch { /* a subscriber's problem is not ours */ }
            }

            lock (_apply)
            {
                _frame = frame.Colors;
                Paint(frame.Colors, force: false);
            }

            // Rate limit (see MinIntervalMs). Sleeping HERE — after the write, before the next wait — is what
            // makes the throttle free: the transport keeps only the newest frame, so whatever the host sent
            // meanwhile collapses into one apply on the next pass instead of queuing up.
            if (!_stopping) Thread.Sleep(MinIntervalMs);
        }

        // Transport gone (not a user Disable): the host can't be holding anything any more.
        if (!_stopping) ReleaseOwnership();
    }

    private void ReleaseOwnership()
    {
        if (!HostOwnsLighting) return;
        HostOwnsLighting = false;
        try { OwnerChanged?.Invoke(false); } catch { /* ditto */ }
    }

    // Apply one frame to the hardware. Caller holds _apply. Walks the layout zone by zone, because the
    // interesting optimisation is per zone: N equal sub-zone colours collapse into ONE all-zones report.
    private void Paint(LampColor[] colors, bool force)
    {
        if (_layout is not { } layout || _written is not { } written) return;

        for (var zi = 0; zi < layout.Zones.Count; zi++)
        {
            var zone = layout.Zones[zi];

            // The lamp indices belonging to this zone, in sub-zone order (Targets is built in that order).
            var first = -1; var count = 0;
            for (var i = 0; i < layout.Targets.Count; i++)
                if (layout.Targets[i].ZoneIndex == zi)
                {
                    if (first < 0) first = i;
                    count++;
                }
            if (first < 0) continue;

            var uniform = true;
            for (var i = first + 1; i < first + count; i++)
                if (!colors[i].IsCloseTo(colors[first], ColorEpsilon)) { uniform = false; break; }

            if (uniform || !zone.HasSubZones)
            {
                // One report for the whole zone. Also the only way to paint a zone we can't sub-address.
                if (!force && Unchanged(written, colors, first, count)) continue;
                if (StaticEffect(zone) is { } effect)
                    // Brightness stays at full: the host's intensity channel is already folded into the RGB
                    // (LampColor.Rgb) and the user's own brightness slider does not apply while the host owns
                    // the surface — it is painting absolute colours.
                    zone.ApplyEffect(effect, brightness: 100, speed: 0, direction: 1, colors[first].Rgb);
                // Record what the hardware now SHOWS (one colour across the zone), not the per-lamp values the
                // host asked for — they were only near-equal, and the next frame's dedupe must compare against
                // reality or a lamp could stay one epsilon off forever.
                for (var i = first; i < first + count; i++) written[i] = colors[first];
            }
            else
            {
                for (var i = first; i < first + count; i++)
                {
                    if (!force && colors[i].IsCloseTo(written[i], ColorEpsilon)) continue;
                    zone.ApplySubZone(layout.Targets[i].SubZone, brightness: 100, colors[i].Rgb);
                    written[i] = colors[i];
                }
            }
        }
    }

    private static bool Unchanged(LampColor[] written, LampColor[] colors, int first, int count)
    {
        for (var i = first; i < first + count; i++)
            if (!colors[i].IsCloseTo(written[i], ColorEpsilon)) return false;
        return true;
    }

    // The zone's "paint an arbitrary colour, don't animate" effect — the same rule the lighting UI uses to
    // decide a zone shows colour swatches (HasColor && !HasSpeed; on Acer that is STATIC). A host frame is by
    // definition a static colour per lamp, so an animated effect would fight it. Null = this zone can't take
    // an arbitrary colour at all (then it simply isn't painted).
    private static RgbModeInfo? StaticEffect(RgbZone zone)
        => zone.Effects.FirstOrDefault(e => e is { HasColor: true, HasSpeed: false })
           ?? zone.Effects.FirstOrDefault(e => e.HasColor);

    public void Dispose()
    {
        Disable();
        try { _transport.Dispose(); } catch { /* best-effort teardown */ }
    }
}
