using System.Diagnostics;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The lifecycle and ownership half of <see cref="LampArrayBridge"/> — everything the frame-painting tests
/// (LampArrayPaintTests.cs) deliberately left out: <c>Enable</c>/<c>Disable</c>, the worker thread, the
/// host-takeover flip, <c>ReleaseOwnership</c> and <c>Reassert</c>.
///
/// These are the rules that decide WHOSE lighting is on screen, and every one of them fails as a wrong picture
/// rather than an exception: a takeover that doesn't fire leaves the app stomping the host's frames on the next
/// profile switch; a release that doesn't fire leaves the keyboard frozen on the host's last frame after the
/// host has gone; and a takeover that doesn't drop the dedupe mirror turns the host's first frame into a silent
/// no-op, so the surface keeps showing the app's colours while the host believes it is painting.
///
/// Everything here is driven through a hand-written <see cref="ILampArrayTransport"/>
/// (<see cref="FakeLampArrayTransport"/>) — no driver, no virtual device, no hardware — which is why the bridge
/// takes its transport injected. The bridge itself is unchanged: no seam was added for these tests.
///
/// THREADING. Each test that enables the bridge owns a real background worker, so every one of them tears it
/// down through <see cref="Fixture.Stop"/>, which both Disables and asserts the worker went away. Waits are on
/// observable state with a deadline, never a fixed sleep; the one unavoidable window is documented at Stop.
/// </summary>
public class LampArrayLifecycleTests
{
    // The effect the bridge looks for: HasColor && !HasSpeed — the same rule the shipped table follows and the
    // frame-painting tests pin. Without a colour-capable effect Build drops the zone and there is no layout.
    private static RgbModeInfo Static() => new("Static", HasColor: true, HasSpeed: false, Handle: 0x02);

    private static readonly LampColor Red = new(255, 0, 0, 100);
    private static readonly LampColor Green = new(0, 255, 0, 100);

    /// <summary>A host frame for this device (four lamps) in one colour.</summary>
    private static LampFrame HostFrame(uint sequence, LampColor color) => new(sequence, false, [color, color, color, color]);

    /// <summary>The host saying "I am done — paint yourself again".</summary>
    private static LampFrame Release(uint sequence) => new(sequence, AutonomousMode: true, []);

    /// <summary>Records every write the bridge makes to the device, so a test can assert not just WHAT was
    /// painted but WHETHER anything was at all — which is the whole question in the ownership tests. Zones are
    /// built with REAL effect lists because <c>LampArrayLayout.Build</c> inspects them and drops an effect-less
    /// zone (<c>FakeRgbDevice.Zone</c> builds exactly that, so it cannot be used here).</summary>
    private sealed class Recorder
    {
        private readonly Lock _gate = new();
        private readonly List<(RgbModeInfo Effect, byte Brightness, byte Speed, byte Direction, AccentColor Color)> _effects = [];
        private readonly List<(int Index, byte Brightness, AccentColor Color)> _subZones = [];

        public IReadOnlyList<(RgbModeInfo Effect, byte Brightness, byte Speed, byte Direction, AccentColor Color)> Effects
        { get { lock (_gate) return _effects.ToArray(); } }

        public IReadOnlyList<(int Index, byte Brightness, AccentColor Color)> SubZones
        { get { lock (_gate) return _subZones.ToArray(); } }

        /// <summary>How many writes reached the device at all, whole-zone plus sub-zone. Read from the test
        /// thread while the worker thread may be appending, hence the lock.</summary>
        public int PaintCount { get { lock (_gate) return _effects.Count + _subZones.Count; } }

        public void Clear() { lock (_gate) { _effects.Clear(); _subZones.Clear(); } }

        public RgbZone Zone(string name, int subZones, params RgbModeInfo[] effects)
        {
            Func<int, byte, AccentColor, bool>? applySubZone = subZones > 1
                ? (i, b, c) => { lock (_gate) _subZones.Add((i, b, c)); return true; }
                : null;
            return new RgbZone(name, subZones, effects,
                (e, b, s, d, c) => { lock (_gate) _effects.Add((e, b, s, d, c)); return true; },
                applySubZone);
        }
    }

    /// <summary>One bridge over one fake transport and one recording device — the default device being a
    /// four-sub-zone keyboard, i.e. four lamps, all of which take the uniform (whole-zone) path when a host
    /// paints them one colour. <see cref="OwnerChanged"/> is collected on the worker thread, so the list is
    /// lock-guarded. Dispose tears the bridge down and asserts the worker stopped; calling
    /// <see cref="Stop"/> explicitly first is fine (it is idempotent) and is what the tests that assert on the
    /// teardown itself do.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly List<bool> _owners = [];
        private bool _stopped;

        public FakeLampArrayTransport Transport { get; } = new();
        public Recorder Recorder { get; } = new();
        public FakeRgbDevice Rgb { get; }
        public LampArrayBridge Bridge { get; }

        public Fixture(Func<Recorder, IReadOnlyList<RgbZone>> zones)
        {
            Rgb = new FakeRgbDevice { Zones = zones(Recorder) };
            Bridge = new LampArrayBridge(Rgb, Transport);
            Bridge.OwnerChanged += owns => { lock (_gate) _owners.Add(owns); };
        }

        /// <summary>The four-lamp keyboard: the shape every test here but the "nothing to expose" one uses.</summary>
        public Fixture() : this(r => [r.Zone("Keyboard", 4, Static())]) { }

        /// <summary>Every <see cref="LampArrayBridge.OwnerChanged"/> argument, in order. A snapshot, so polling
        /// it cannot race the worker that appends to it.</summary>
        public IReadOnlyList<bool> Owners { get { lock (_gate) return _owners.ToArray(); } }

        /// <summary>Disable the bridge and prove the worker really went away. Idempotent.
        ///
        /// WHY TIMING IS THE PROBE: <c>Disable</c> Joins its worker with a one-second budget and DISCARDS the
        /// result, so a worker that failed to stop is invisible from the outside — there is no public handle on
        /// it and no event when it ends. The one thing a leak does change is the time Disable costs: a worker
        /// that wakes on <c>Stop()</c> leaves within one MinIntervalMs sleep, while one that had to be waited
        /// out costs the full second. The budget here is four times the honest worst case and still well inside
        /// Join's, so this measures the Join timing out rather than the machine being busy.
        ///
        /// Everything else asserted here is exact: Stop was called (which is what unparks the worker at all),
        /// and the transport was disposed exactly once — Dispose runs after Disable, not through it.</summary>
        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;

            var wasEnabled = Bridge.Enabled;
            var sw = Stopwatch.StartNew();
            Bridge.Dispose();                 // Disable, then transport.Dispose
            sw.Stop();

            Assert.Equal(1, Transport.DisposeCalls);
            if (!wasEnabled) return;

            Assert.Equal(1, Transport.StopCalls);
            Assert.False(Bridge.Enabled);
            Assert.Equal(0, Bridge.LampCount);
            Assert.True(sw.ElapsedMilliseconds < 400,
                $"Dispose() took {sw.ElapsedMilliseconds} ms — Disable's Join(1s) timed out, so the worker did not stop");
        }

        public void Dispose() => Stop();
    }

    /// <summary>Poll with a deadline instead of sleeping a guess. A worker that is going to do something does it
    /// within a frame interval; a fixed sleep would either be slower than it needs to be or race it.</summary>
    private static void Until(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 5000) Assert.Fail($"timed out waiting for {what}");
            Thread.Sleep(5);
        }
    }

    // ================= Enable: the two ways it refuses =================

    /// <summary>A device with nothing a host could paint is not published at all: <c>Build</c> drops every zone
    /// with no effects and answers null, and the bridge reports that as a plain error rather than handing the
    /// transport a layout with no lamps. The transport must not even be asked — a virtual device that appeared
    /// and then had nothing to show would be worse than one that never appeared.</summary>
    [Fact]
    public void EnableFailsWhenTheDeviceHasNoControllableZone()
    {
        using var fx = new Fixture(_ => [FakeRgbDevice.Zone("Keyboard")]);   // a zone, but with no effects

        Assert.False(fx.Bridge.Enable());
        Assert.Equal("no controllable lighting zones", fx.Bridge.LastError);
        Assert.False(fx.Bridge.Enabled);
        Assert.Equal(0, fx.Bridge.LampCount);
        Assert.Equal(0, fx.Transport.StartCalls);
        Assert.Null(fx.Transport.StartedLayout);
    }

    /// <summary>The transport refusing (the driver is not installed, no permission) is a normal state, not an
    /// exception — and its own message is the useful one, so it wins over the bridge's generic fallback. Nothing
    /// was started, so nothing is stopped either, and the bridge stays disabled with no lamps to report.</summary>
    [Fact]
    public void EnableFailsWhenTheTransportRefuses_AndReportsTheTransportsOwnError()
    {
        using var fx = new Fixture();
        fx.Transport.StartResult = false;
        fx.Transport.LastError = "LampArray driver not installed";

        Assert.False(fx.Bridge.Enable());

        Assert.Equal("LampArray driver not installed", fx.Bridge.LastError);   // the transport's words, not ours
        Assert.False(fx.Bridge.Enabled);
        Assert.Equal(0, fx.Bridge.LampCount);
        Assert.Equal(1, fx.Transport.StartCalls);
        Assert.Equal(0, fx.Transport.StopCalls);
    }

    /// <summary>...and when the transport refuses without saying why, the bridge still has to put something in
    /// front of the user. Pinned because the fallback is the branch a silent transport takes, and "unavailable"
    /// with no reason is what the UI will render.</summary>
    [Fact]
    public void EnableFailsWithTheDefaultMessageWhenTheTransportReportsNothing()
    {
        using var fx = new Fixture();
        fx.Transport.StartResult = false;
        fx.Transport.LastError = null;

        Assert.False(fx.Bridge.Enable());
        Assert.Equal("transport unavailable", fx.Bridge.LastError);
    }

    // ================= Enable: the layout that gets published =================

    /// <summary>What actually reaches the transport. The bridge builds the layout itself from the device, so
    /// what is pinned here is that the published layout is the one <c>Build</c> produces from these zones —
    /// same lamps, same targets, same zone instances — and that the update interval advertised to the host is
    /// the bridge's OWN <see cref="LampArrayBridge.MinIntervalMs"/> rather than Build's default. Those two
    /// happen to agree at 100 ms today, which is exactly why the assertion is against the constant: if the
    /// bridge's throttle and the interval it advertises ever drifted apart, a well-behaved host would be pacing
    /// itself to a number nobody enforces. The worker is verified to be up by waiting for its first park.</summary>
    [Fact]
    public void EnablePublishesTheLayoutBuildProducesFromTheDevice()
    {
        using var fx = new Fixture();

        Assert.True(fx.Bridge.Enable());
        Assert.True(fx.Bridge.Enabled);
        Assert.Null(fx.Bridge.LastError);

        var expected = LampArrayLayout.Build(fx.Rgb)!;
        var started = fx.Transport.StartedLayout;

        Assert.NotNull(started);
        Assert.Equal(expected.LampCount, fx.Bridge.LampCount);
        Assert.Equal(4, fx.Bridge.LampCount);                 // the premise: four sub-zones, four lamps
        Assert.Equal(expected.Targets, started.Targets);
        Assert.Equal(expected.Lamps, started.Lamps);
        Assert.Equal(expected.Kind, started.Kind);
        Assert.Equal(expected.MinUpdateIntervalUs, started.MinUpdateIntervalUs);
        Assert.Equal(LampArrayBridge.MinIntervalMs * 1000, started.MinUpdateIntervalUs);   // µs on the wire
        Assert.Same(expected.Zones[0], started.Zones[0]);     // the very zone instances the device advertises

        // The worker is really running: its first act is to park in WaitFrame.
        Until(() => fx.Transport.WaitFrameCalls >= 1, "the worker to start");
        Assert.Equal(0, fx.Transport.StopCalls);
    }

    /// <summary>Enabling twice is one enable. The second call succeeds (the surface is up, which is what the
    /// caller asked for) but must not re-publish the device or leave a second worker behind — two workers would
    /// both paint every frame, and the second one's mirror would fight the first's. "No second worker" is proved
    /// exactly rather than by waiting: the transport records the thread id of every caller that parks in
    /// WaitFrame, and a leaked second worker would have to park there too.</summary>
    [Fact]
    public void EnableIsIdempotent()
    {
        using var fx = new Fixture();

        Assert.True(fx.Bridge.Enable());
        Assert.True(fx.Bridge.Enable());
        Assert.True(fx.Bridge.Enabled);
        Assert.Equal(1, fx.Transport.StartCalls);

        fx.Transport.Push(HostFrame(1, Red));
        Until(() => fx.Transport.WaitFrameCalls >= 2, "the frame to be applied");

        Assert.Single(fx.Transport.WaitThreads);
    }

    // ================= the ownership flip =================

    /// <summary>A host frame IS a takeover: the app must stop painting the surface, and everyone who cares is
    /// told exactly once. Fired from the worker thread, which is why the fixture collects it under a lock.</summary>
    [Fact]
    public void AHostFrameTakesOwnership()
    {
        using var fx = new Fixture();
        Assert.True(fx.Bridge.Enable());

        fx.Transport.Push(HostFrame(1, Red));
        Until(() => fx.Transport.WaitFrameCalls >= 2, "the frame to be applied");

        Assert.True(fx.Bridge.HostOwnsLighting);
        Assert.Equal([true], fx.Owners);
    }

    /// <summary>...once, not once per frame. <c>OwnerChanged</c> is what makes the app tear down its own
    /// lighting, so firing it on every frame of a 60 Hz host would have the app re-entering its lighting path
    /// continuously for as long as the host paints. The second frame is asserted to have been APPLIED as well as
    /// silent, so this cannot pass by the frame never arriving.</summary>
    [Fact]
    public void ASecondHostFrameDoesNotTakeOwnershipAgain()
    {
        using var fx = new Fixture();
        Assert.True(fx.Bridge.Enable());

        fx.Transport.Push(HostFrame(1, Red));
        Until(() => fx.Transport.WaitFrameCalls >= 2, "the first frame to be applied");
        fx.Transport.Push(HostFrame(2, Green));
        Until(() => fx.Transport.WaitFrameCalls >= 3, "the second frame to be applied");

        Assert.True(fx.Bridge.HostOwnsLighting);
        Assert.Equal([true], fx.Owners);            // exactly once, not twice
        Assert.Equal(2, fx.Recorder.PaintCount);    // and the silent frame really was painted
    }

    /// <summary>The host letting go — it lost its exclusive lock, Dynamic Lighting was switched off, the app
    /// holding it exited — must hand the surface back, or the keyboard stays frozen on the host's last frame
    /// with the app believing it is not allowed to paint. Note the bridge is still ENABLED afterwards: the
    /// virtual device stays published and the worker keeps pumping, it is only ownership that changed.</summary>
    [Fact]
    public void AnAutonomousFrameHandsTheSurfaceBack()
    {
        using var fx = new Fixture();
        Assert.True(fx.Bridge.Enable());

        fx.Transport.Push(HostFrame(1, Red));
        Until(() => fx.Transport.WaitFrameCalls >= 2, "the host frame to be applied");

        fx.Transport.Push(Release(2));
        Until(() => fx.Owners.Count >= 2, "the release");

        Assert.False(fx.Bridge.HostOwnsLighting);
        Assert.Equal([true, false], fx.Owners);
        Assert.True(fx.Bridge.Enabled);
    }

    /// <summary>Disabling after a takeover has to hand the surface back as part of the teardown — otherwise the
    /// app never repaints, and the keyboard is left showing the last host frame with nothing driving it at all.
    /// Exactly one <c>false</c> is expected, not two: the worker's own end-of-loop release is suppressed while
    /// <c>_stopping</c> is set, and <c>ReleaseOwnership</c> returns early on an already-false flag, so the two
    /// possible posters cannot both fire. The last frame is also gone, so <c>LampCount</c> reads 0.</summary>
    [Fact]
    public void DisableAfterATakeoverHandsTheSurfaceBack()
    {
        using var fx = new Fixture();
        Assert.True(fx.Bridge.Enable());

        fx.Transport.Push(HostFrame(1, Red));
        Until(() => fx.Transport.WaitFrameCalls >= 2, "the frame to be applied");
        Assert.True(fx.Bridge.HostOwnsLighting);

        fx.Stop();                                   // Disable + Dispose, and prove the worker went away

        Assert.False(fx.Bridge.HostOwnsLighting);
        Assert.False(fx.Bridge.Enabled);
        Assert.Equal(0, fx.Bridge.LampCount);
        Assert.Equal([true, false], fx.Owners);
    }

    /// <summary>The transport dying ON ITS OWN — the driver unloaded, the device was pulled — has to hand the
    /// surface back too, and this is the path that would otherwise strand the app: nothing calls <c>Disable</c>,
    /// so the worker's own end-of-loop release is the ONLY thing that can clear ownership. Leave it set and the
    /// app believes a host that no longer exists is still painting, so it never repaints its own lighting again
    /// for the rest of the session — a permanently dead keyboard, with no error anywhere.
    ///
    /// The break is driven through <see cref="FakeLampArrayTransport.Break"/>, which is deliberately not a
    /// <c>Stop</c>: the bridge can only tell "torn down" from "died on me" by its own <c>_stopping</c> flag, so
    /// this is the one state where <c>!WaitFrame</c> is not a user Disable. The frame that was painted first is
    /// what makes it a takeover worth releasing. Also pinned: the transport's failure reason is surfaced on the
    /// bridge rather than swallowed, which is what the UI shows.
    ///
    /// Found by mutation — dropping the end-of-loop <c>ReleaseOwnership()</c> reddened nothing until this test
    /// existed, because every other teardown here goes through Disable and sets <c>_stopping</c> first.</summary>
    [Fact]
    public void ATransportThatDiesOnItsOwnReleasesTheSurface()
    {
        using var fx = new Fixture();
        Assert.True(fx.Bridge.Enable());

        fx.Transport.Push(HostFrame(1, Red));
        Until(() => fx.Transport.WaitFrameCalls >= 2, "the host frame to be applied");
        Assert.True(fx.Bridge.HostOwnsLighting);

        fx.Transport.Break("the LampArray driver went away");   // NOT a Stop: the bridge never asked for this

        Until(() => !fx.Bridge.HostOwnsLighting, "the bridge to give the surface back");
        Assert.Equal([true, false], fx.Owners);
        Assert.Equal("the LampArray driver went away", fx.Bridge.LastError);
    }

    // ================= the takeover drops the dedupe mirror =================

    /// <summary>THE load-bearing assertion of this file. A fresh takeover must paint every lamp even when the
    /// colours match what the mirror last recorded, because the mirror is a record of what THIS bridge wrote —
    /// and while the host was away the app painted the surface itself, straight through the zones. So the mirror
    /// is stale the moment ownership is released, and a host that comes back with the colour it left with (the
    /// common case: it is repainting the same effect) would otherwise be deduped away SILENTLY: no exception,
    /// no LastError, the surface simply keeps the app's colours while the host believes it is painting.
    ///
    /// That is what <c>Array.Clear(_written!)</c> on the takeover edge is for. This test is built so it genuinely
    /// fails without it: the second takeover frame is Red, the mirror was left holding Red by the first session,
    /// and the only thing that makes the write happen is the clear. A frame of BLACK would not do — see
    /// <see cref="AFirstTakeoverFrameOfBlackIsDedupedAwayByTheInitiallyZeroMirror"/>.</summary>
    [Fact]
    public void ATakeoverRepaintsEvenWhenTheFrameMatchesTheStaleMirror()
    {
        using var fx = new Fixture();
        Assert.True(fx.Bridge.Enable());

        // A host session that leaves the mirror holding Red...
        fx.Transport.Push(HostFrame(1, Red));
        Until(() => fx.Transport.WaitFrameCalls >= 2, "the first frame to be applied");
        Assert.True(fx.Bridge.HostOwnsLighting);

        // ...the host lets go. Nothing clears the mirror here: it still says Red, while the surface is whatever
        // the app repainted on OwnerChanged(false).
        fx.Transport.Push(Release(2));
        Until(() => fx.Owners.Count >= 2, "the release");
        fx.Recorder.Clear();

        // ...and takes the surface back with the SAME colour.
        fx.Transport.Push(HostFrame(3, Red));
        Until(() => fx.Transport.WaitFrameCalls >= 4, "the second takeover to be applied");

        Assert.Equal([true, false, true], fx.Owners);
        Assert.Equal(1, fx.Recorder.PaintCount);     // without Array.Clear(_written!) this is 0
    }

    /// <summary>OBSERVED CURRENT behaviour, and the reason the test above cannot be written with black.
    ///
    /// The mirror <c>Enable</c> allocates is already all-zero, and <see cref="LampColor"/>'s default is
    /// <c>(0,0,0,0)</c> — a lamp that is off. So a FIRST takeover whose frame is entirely black is within the
    /// epsilon of the mirror and is skipped, even with the Array.Clear in place: the mirror claims the surface
    /// already shows black, but at Enable time the surface was showing whatever the app had painted. The
    /// consequence is the same silent no-op the clear exists to prevent, one step earlier.
    ///
    /// Pinned as observed rather than fixed, so that seeding the mirror with something that cannot collide (or
    /// forcing the first takeover) is a visible change of behaviour and not a silent "cleanup". It is narrow —
    /// it needs a host whose very first frame after Enable is black, e.g. a "lights off" effect — but it is the
    /// same failure mode, and the frame-painting tests pin the mirror's other poisoned case the same way.</summary>
    [Fact]
    public void AFirstTakeoverFrameOfBlackIsDedupedAwayByTheInitiallyZeroMirror()
    {
        using var fx = new Fixture();
        Assert.True(fx.Bridge.Enable());

        fx.Transport.Push(HostFrame(1, new LampColor(0, 0, 0, 0)));
        Until(() => fx.Transport.WaitFrameCalls >= 2, "the frame to be applied");

        Assert.True(fx.Bridge.HostOwnsLighting);     // the takeover happened...
        Assert.Equal(0, fx.Recorder.PaintCount);     // ...but nothing was painted
    }

    // ================= Reassert =================

    /// <summary><c>Reassert</c> is the app's "the EC just clobbered the surface, put the host's frame back"
    /// button, and it is only meaningful while a host owns the surface. The second half of this test is the
    /// state the app is in the moment it repaints its OWN lighting: ownership has been released, but
    /// <c>_frame</c> still holds the host's last frame. So a <c>Reassert</c> arriving there cannot be stopped by
    /// a null frame — only by the ownership guard — and without that guard the app's fresh repaint would be
    /// overwritten by the host's stale one.</summary>
    [Fact]
    public void ReassertDoesNothingWhenNoHostOwnsTheSurface()
    {
        using var fx = new Fixture();
        Assert.True(fx.Bridge.Enable());

        fx.Bridge.Reassert();                        // nothing has ever been painted: _frame is null
        Assert.Equal(0, fx.Recorder.PaintCount);

        fx.Transport.Push(HostFrame(1, Red));
        Until(() => fx.Transport.WaitFrameCalls >= 2, "the host frame to be applied");
        fx.Transport.Push(Release(2));
        Until(() => fx.Owners.Count >= 2, "the release");
        fx.Recorder.Clear();

        fx.Bridge.Reassert();

        Assert.False(fx.Bridge.HostOwnsLighting);
        Assert.Equal(0, fx.Recorder.PaintCount);     // would be 1 with the ownership guard removed
    }

    /// <summary>...and nothing at all once the bridge is disabled — the layout and the host's frame are gone
    /// with it, and the caller may well be a UI event handler racing the teardown. Weaker than the test above by
    /// construction: <c>Disable</c> nulls <c>_frame</c> as well as clearing <c>Enabled</c>, so this pins the
    /// contract (no throw, no write) rather than singling out either half of the guard.</summary>
    [Fact]
    public void ReassertDoesNothingWhenDisabled()
    {
        using var fx = new Fixture();
        Assert.True(fx.Bridge.Enable());

        fx.Transport.Push(HostFrame(1, Red));
        Until(() => fx.Transport.WaitFrameCalls >= 2, "the frame to be applied");
        Assert.True(fx.Bridge.HostOwnsLighting);

        fx.Stop();                                   // Disable: Enabled and _frame both go
        fx.Recorder.Clear();

        fx.Bridge.Reassert();

        Assert.Equal(0, fx.Recorder.PaintCount);
    }

    /// <summary>The other half of <c>Reassert</c>, and the reason it exists: it repaints even though the colours
    /// already match the mirror, because the mirror records what the bridge last WROTE and not what the surface
    /// currently SHOWS — the EC's amber OPMODE flash on a profile switch, sleep, and a clamshell lid-open all
    /// wipe the RGB behind the bridge's back. With the dedupe applied, every lamp would be skipped and the
    /// surface would stay wrong; that is what <c>force: true</c> is for. Caller-thread, so no polling: the write
    /// has happened by the time the call returns.</summary>
    [Fact]
    public void ReassertRepaintsTheHostsLastFrameWithForce()
    {
        using var fx = new Fixture();
        Assert.True(fx.Bridge.Enable());

        fx.Transport.Push(HostFrame(1, Red));
        Until(() => fx.Transport.WaitFrameCalls >= 2, "the frame to be applied");
        fx.Recorder.Clear();                          // the mirror still holds Red, and so does the frame

        fx.Bridge.Reassert();

        var painted = Assert.Single(fx.Recorder.Effects);   // forced through, not deduped away
        Assert.Equal(new AccentColor(255, 0, 0), painted.Color);
        Assert.True(fx.Bridge.HostOwnsLighting);            // ownership is untouched by a repaint
    }
}
