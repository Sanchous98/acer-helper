using AcerHelper.Application;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Lighting;

/// <summary>
/// The translation layer proper: takes lamp frames a host (Windows Dynamic Lighting, or any LampArray-aware
/// app) writes to our virtual HID device and turns them into this device's zone writes — and arbitrates
/// ownership of the backlight while it does, so the app and the OS don't fight over it.
///
/// Three problems make this more than a memcpy, all properties of the hardware (docs/lamparray.md):
///  1. RATE. A host paints at 30–60 Hz; the ENE controller is on HID-over-I2C and a full keyboard apply is
///     several feature reports (hence EneHidController's pacing and coalescing). Frames are rate-limited HERE
///     to the interval the layout ADVERTISES as MinUpdateInterval, and are last-one-wins, so slowing down
///     drops intermediate frames rather than building a backlog.
///  2. WRITE COUNT. A uniform colour across a zone is ONE all-zones report instead of one per sub-zone;
///     unchanged sub-zones (±ColorEpsilon per channel) are skipped outright.
///  3. OWNERSHIP. While the host drives the surface the app must stop painting it, or every profile switch,
///     resume and re-apply tick would stomp the host's frame. <see cref="HostOwnsLighting"/> is that gate;
///     <see cref="Reassert"/> is its counterpart, repainting the host's last frame after the EC clobbers the
///     surface on a profile switch or across sleep.
///
/// Threading: one long-lived worker thread pumps the transport (a blocking wait) and applies frames; the zone
/// writes it makes are themselves non-blocking (EneHidController queues them onto its own writer). Public
/// members are safe to call from any thread. <see cref="OwnerChanged"/> fires on the worker thread — marshal
/// it if you touch UI state.
///
/// It implements <see cref="IDynamicLighting"/>, the contract Application names (Application/DynamicLighting.cs),
/// so the layer that orchestrates lighting never has to name this type or the transport under it.
/// </summary>
public sealed class LampArrayBridge : IDynamicLighting
{
    /// <summary>Slowest we ever repaint the hardware, and the value advertised to the host as
    /// MinUpdateInterval. 10 Hz is a deliberate compromise: fast enough that breathing/wave effects read as
    /// smooth on a 4-zone surface, slow enough that the I2C bus (and the EC) keep up under contention.</summary>
    public const int MinIntervalMs = 100;

    // Per-channel delta below which a lamp counts as unchanged (see LampColor.IsCloseTo). A host stepping a
    // gradient one unit per frame would otherwise generate a write per frame per zone forever.
    private const int ColorEpsilon = 5;

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

    /// <summary>Repaint the hardware from the host's last frame, ignoring the dedupe — for the events where the
    /// EC or the OS clobbers the surface (a profile switch forces the amber OPMODE flash and wipes the RGB, sleep
    /// drops it, a clamshell lid-open restores from black) and the host is the rightful owner of what should be
    /// showing. See docs/lamparray.md.</summary>
    public void Reassert()
    {
        if (!Enabled || !HostOwnsLighting) return;
        lock (_apply)
        {
            if (_frame is { } f) Paint(_layout, _written, f, force: true);
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
                // The channel failed on its own (driver unloaded, device removed) or a Disable tore it down.
                // Either way the worker just stops — it does NOT re-try. Re-enabling is a user action (or an app
                // restart), because silently reconnecting in a loop would hide a real problem and keep poking a
                // device that isn't there. And a failure we did not ask for is the only one worth reporting.
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
                Paint(_layout, _written, frame.Colors, force: false);
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
    //
    // Static with its two fields taken as parameters (was an instance method reading _layout/_written) so a test
    // can drive it with a hand-built layout, a dedupe mirror and a frame — no transport, no worker thread, no
    // timing, nothing that needs hardware. Visibility and shape ONLY: the guard, the read order and every effect
    // are unchanged, and both callers pass the two fields from the same place — inside the same _apply lock —
    // that this method used to read them. Same precedent as RyzenCurveOptimizer.Encode/CoreArg/GpuMargin.
    internal static void Paint(LampArrayLayout? layout, LampColor[]? written, LampColor[] colors, bool force)
    {
        if (layout is null || written is null) return;

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
    //
    // Widened private -> internal, visibility only, so the selection rule (including the fallback) can be pinned
    // without a transport — the same precedent as the members above.
    internal static RgbModeInfo? StaticEffect(RgbZone zone)
        => zone.Effects.FirstOrDefault(e => e is { HasColor: true, HasSpeed: false })
           ?? zone.Effects.FirstOrDefault(e => e.HasColor);

    public void Dispose()
    {
        Disable();
        try { _transport.Dispose(); } catch { /* best-effort teardown */ }
    }
}
