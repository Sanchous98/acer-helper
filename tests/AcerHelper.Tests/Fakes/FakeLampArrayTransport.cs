using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="ILampArrayTransport"/>: no driver, no HID channel, no virtual device — just the two
/// things <see cref="LampArrayBridge"/>'s worker actually needs from a transport, a frame slot of depth one and
/// somewhere to park. Everything the lifecycle half of the bridge does (Enable/Disable, the worker thread, the
/// ownership flip) becomes drivable from a test with this and nothing else, which is exactly why the bridge
/// takes its transport injected.
///
/// The semantics below are the REAL ones from the interface docs, not a convenient simplification:
///
///  * LAST ONE WINS. <see cref="Push"/> overwrites whatever is pending, and a parked worker is released at most
///    once per push edge, so N pushes before the worker wakes collapse into ONE delivery of the newest frame —
///    never a backlog. That collapsing is what makes the bridge's "sleep after the write" rate limit free (see
///    the class comment there); a fake that queued instead would hide a backlog the real transport cannot have,
///    and every timing assertion in the lifecycle tests would be measuring the wrong thing.
///  * ONE PERMIT. The semaphore's capacity is 1 and a permit is released only on the edge from "nothing
///    pending" to "something pending" (plus by <see cref="Stop"/>/<see cref="Dispose"/>), so permits cannot
///    accumulate into a spin — a worker that had a spare permit would burn a core re-entering WaitFrame.
///  * STOP UNPARKS. <see cref="Stop"/> sets the stopped flag, drops any pending frame and releases a permit, so
///    a worker parked in <see cref="WaitFrame"/> wakes and gets <c>false</c> rather than blocking forever. That
///    is precisely what <c>LampArrayBridge.Disable</c> relies on to get its worker to exit (it calls Stop, then
///    Joins), so a fake that merely set a flag would hang every teardown.
///  * IDEMPOTENT. <see cref="Stop"/> and <see cref="Dispose"/> are safe to call twice, and <see cref="Dispose"/>
///    deliberately does NOT call <see cref="Stop"/> — the bridge's own Dispose does both, and a fake that
///    counted one as the other could not tell them apart.
/// </summary>
public sealed class FakeLampArrayTransport : ILampArrayTransport
{
    // Capacity 1: "at most one outstanding permit", which is what collapses a burst of pushes into a single
    // wake-up. Never disposed — see Dispose().
    private readonly SemaphoreSlim _signal = new(0, 1);

    private readonly Lock _gate = new();          // guards _frame/_hasFrame and _waitThreads
    private LampFrame _frame;
    private bool _hasFrame;

    private readonly HashSet<int> _waitThreads = [];

    // Written by the test thread (Stop/Dispose), read by the worker: volatile, or the worker could keep spinning
    // inside the loop after Stop. Same reason the bridge's own _stopping is volatile.
    private volatile bool _stopped;

    private int _waitFrameCalls;

    /// <summary>Every <see cref="Start"/> call, including the refused ones.</summary>
    public int StartCalls { get; private set; }

    /// <summary>The layout handed to the last <see cref="Start"/>, or null if it was never called.</summary>
    public LampArrayLayout? StartedLayout { get; private set; }

    /// <summary>Every <see cref="Stop"/> call. Counted per call, not per transition, so "Stop was called once"
    /// and "Stop was called twice" are distinguishable even though the second is a no-op.</summary>
    public int StopCalls { get; private set; }

    /// <summary>Every <see cref="Dispose"/> call.</summary>
    public int DisposeCalls { get; private set; }

    /// <summary>Incremented on ENTRY to <see cref="WaitFrame"/>, before anything blocks. This is the exact
    /// "the previous frame has been applied" signal: the worker only re-enters WaitFrame after it has painted
    /// and slept, so a test can wait on <c>WaitFrameCalls &gt;= n + 1</c> instead of sleeping a guess.</summary>
    public int WaitFrameCalls => Volatile.Read(ref _waitFrameCalls);

    /// <summary>The managed ids of every thread that has entered <see cref="WaitFrame"/>. One bridge worker
    /// parks here, so more than one id means a worker was started and leaked — the only way to prove "no second
    /// worker" exactly, without waiting a window for something that should never happen.</summary>
    public IReadOnlyList<int> WaitThreads { get { lock (_gate) return _waitThreads.ToArray(); } }

    /// <summary>Whether <see cref="Start"/> succeeds. False models the driver not being installed, which is the
    /// state the bridge has to report rather than throw on.</summary>
    public bool StartResult { get; set; } = true;

    /// <summary>What the transport last failed with. Settable, so a test can pin both the "the transport has an
    /// opinion" and the "it has nothing to say" branches of the bridge's error reporting.</summary>
    public string? LastError { get; set; }

    public bool Start(LampArrayLayout layout)
    {
        StartCalls++;
        StartedLayout = layout;

        // A transport that is started again after a Stop is live again; without this a second Enable sharing one
        // fake would find WaitFrame already returning false.
        lock (_gate) _hasFrame = false;
        _stopped = false;
        if (StartResult) LastError = null;   // the interface's contract: null after a successful call
        return StartResult;
    }

    /// <summary>Hand the worker a frame the way the driver would: it becomes the newest state, replacing any
    /// frame not yet consumed, and wakes a parked worker only if nothing was pending.</summary>
    public void Push(LampFrame frame)
    {
        bool wake;
        lock (_gate)
        {
            wake = !_hasFrame;
            _frame = frame;
            _hasFrame = true;
        }
        if (wake) Wake();
    }

    public bool WaitFrame(out LampFrame frame)
    {
        Interlocked.Increment(ref _waitFrameCalls);
        lock (_gate) _waitThreads.Add(Environment.CurrentManagedThreadId);

        while (true)
        {
            lock (_gate)
            {
                if (_hasFrame)
                {
                    frame = _frame;
                    _hasFrame = false;
                    return true;
                }
            }

            if (_stopped) { frame = default; return false; }

            // Park. The permit is released by Push (a frame arrived) or Stop/Dispose (give up), and is consumed
            // exactly once because the state above is re-checked after every wake-up.
            _signal.Wait();
        }
    }

    /// <summary>Idempotent: drops any pending frame, marks the transport dead so a parked (or future) waiter
    /// gets <c>false</c>, and unparks whoever is waiting.</summary>
    public void Stop()
    {
        StopCalls++;
        lock (_gate) _hasFrame = false;
        _stopped = true;
        Wake();
    }

    /// <summary>Unparks waiters but does NOT go through <see cref="Stop"/>, so the two counters stay
    /// independent — <c>LampArrayBridge.Dispose</c> calls both and a test must be able to see that it did.</summary>
    public void Dispose()
    {
        DisposeCalls++;
        _stopped = true;
        Wake();
    }

    /// <summary>The channel failing ON ITS OWN — the driver unloaded, the device was removed — as opposed to a
    /// user Disable, which is the distinction the bridge's worker acts on: it can tell the two apart only by its
    /// own <c>_stopping</c> flag, so a <see cref="Stop"/> here would collapse the very branches a test is trying
    /// to separate. Deliberately does NOT count as a <see cref="StopCalls"/> and does NOT clear the pending frame
    /// — nothing did, the pipe just died. <paramref name="error"/> becomes what the bridge surfaces as its own
    /// <c>LastError</c>.</summary>
    public void Break(string? error)
    {
        LastError = error;
        _stopped = true;
        Wake();
    }

    private void Wake()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* a permit is already outstanding; the waiter re-reads the flags anyway */ }
        catch (ObjectDisposedException) { /* never disposed — see below */ }
    }

    // NOT disposing _signal is deliberate: a worker can still be parked on it when the bridge tears down
    // (Dispose runs after Disable's Join, but a Join that timed out leaves a live thread), and disposing a
    // SemaphoreSlim out from under a waiter is a race no test should have to survive. A handful of these objects
    // per test run costs nothing.
}
