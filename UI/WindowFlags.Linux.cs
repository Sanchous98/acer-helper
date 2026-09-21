using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>
/// The X11 half of <see cref="WindowFlags"/>: one <c>_NET_WM_STATE</c> client message to the root window asking
/// for <c>SKIP_TASKBAR</c> and <c>ABOVE</c>. That is the protocol path KWin implements, and it is deliberately
/// not Avalonia's <c>ShowInTaskbar</c>/<c>Topmost</c> setters — see <see cref="WindowFlags"/> for the measurement
/// that ruled them out (re-asserting them from <c>Opened</c> left the window unmapped while the app believed it
/// was open).
///
/// The window id comes from Avalonia's platform handle, and the display is opened by this file rather than
/// borrowed from the backend: a client message only needs a connection to the same server, and opening our own
/// keeps this from depending on Avalonia's internals. That connection and the three atoms are established ONCE
/// and kept for the process (see <see cref="Session"/>), because this runs twice per window open. A failure of
/// any step is silent — the flags are a preference, and a machine without these atoms (a non-EWMH window
/// manager) must still get its window.
/// </summary>
internal static partial class WindowFlags
{
    private const int ClientMessage = 33;
    private const long StateAdd = 1;                 // _NET_WM_STATE_ADD
    private const long SourceApplication = 1;        // data.l[3]: the request comes from the application
    /// <summary>SubstructureRedirectMask | SubstructureNotifyMask. EWMH requires exactly this pair for a
    /// <c>_NET_WM_STATE</c> request, and getting it wrong is silent: with <c>0x140000</c> — which is what this
    /// file first carried, adding ResizeRedirectMask instead of SubstructureNotifyMask — KWin ignored every
    /// message and <c>xprop</c> kept showing <c>FOCUSED</c> alone, with no error anywhere.</summary>
    private const long SubstructureRedirectNotify = 0x180000;
    private const int EventBufferSize = 192;         // sizeof(XEvent) on 64-bit; the union is written at offset 0

    private static void ApplyCore(Window window)
    {
        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } xid || xid == IntPtr.Zero) return;
            if (!Session.TryGet(out var x11)) return;

            var ev = new XClientMessageEvent
            {
                type = ClientMessage,
                window = xid,
                message_type = x11.WmState,
                format = 32,
                data0 = StateAdd,
                data1 = (long)x11.SkipTaskbar,
                data2 = (long)x11.Above,
                data3 = SourceApplication,
            };
            var buf = Marshal.AllocHGlobal(EventBufferSize);
            try
            {
                Marshal.StructureToPtr(ev, buf, fDeleteOld: false);
                var root = XDefaultRootWindow(x11.Display);
                if (root != IntPtr.Zero) XSendEvent(x11.Display, root, propagate: false, SubstructureRedirectNotify, buf);
                XFlush(x11.Display);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { /* a window manager that does not want this must not stop the window from opening */ }
    }

    /// <summary>One X connection and the three atoms interned in it, as an immutable value so the hot path takes
    /// them from one snapshot rather than three fields that could disagree.</summary>
    private readonly record struct X11Session(IntPtr Display, IntPtr WmState, IntPtr SkipTaskbar, IntPtr Above);

    /// <summary>
    /// The connection and the atoms, established once for the process — and NOTHING IS RELEASED, deliberately:
    /// the connection lives as long as the app does, which is the lifetime its atoms have anyway. Closing it at
    /// exit would buy nothing (the server tears it down with the process) and would need this helper to learn
    /// about the app's shutdown, which is a lifetime to get wrong for no gain. That is the whole of what is held:
    /// one connection, no buffers, no per-window state.
    ///
    /// WHY CACHED AT ALL. <see cref="WindowFlags.Apply"/> calls <see cref="ApplyCore"/> twice per window open
    /// (immediately, and again on the next dispatcher turn — see its comment), so a flyout or a dialog open used
    /// to cost TWO connections and SIX server round trips, all of it on the UI thread inside <c>Opened</c>. The
    /// observable effect is identical either way: atoms are not per-client state, and a client message only needs
    /// a connection to the same server.
    ///
    /// THE ATOMS ARE ASKED FOR WITH <c>onlyIfExists</c>, which is what makes the guard below reachable: a server
    /// whose atom table does not hold EWMH's names is a window manager this request cannot reach, and the names
    /// are interned by the window manager and the desktop shell on any session that implements EWMH — this app
    /// asking is never what brings them into existence. Interning them into existence (<c>onlyIfExists: false</c>,
    /// which is what this file first carried) had both consequences and neither was wanted: the guard could never
    /// fire, and the three atoms were created on the server permanently by a client that only ever reads them.
    ///
    /// A FAILED LOOKUP IS NOT REMEMBERED, only successful ones are: a display that is not there yet is retried on
    /// the next call, exactly as this file did when it opened one per call, and so is the atom lookup (which costs
    /// three round trips per call on a non-EWMH session, against a cache that would freeze the answer for the
    /// process on the strength of one attempt).
    ///
    /// The lock covers the setup only. The send itself is not locked because Xlib is not thread-safe per
    /// connection and every caller is on the UI thread (<c>Opened</c> and a dispatcher post from it).
    /// </summary>
    private static class Session
    {
        private static readonly Lock Gate = new();
        private static IntPtr _display;                  // the connection: opened once, never closed
        private static X11Session? _established;

        internal static bool TryGet(out X11Session session)
        {
            lock (Gate)
            {
                if (_established is { } cached) { session = cached; return true; }

                if (_display == IntPtr.Zero) _display = XOpenDisplay(null);
                if (_display == IntPtr.Zero) { session = default; return false; }

                // onlyIfExists for all three: see the class docs — a name that is not on the server means no
                // window manager will act on the message, which is the case this guard exists for.
                var wmState = XInternAtom(_display, "_NET_WM_STATE", onlyIfExists: true);
                var skipTaskbar = XInternAtom(_display, "_NET_WM_STATE_SKIP_TASKBAR", onlyIfExists: true);
                var above = XInternAtom(_display, "_NET_WM_STATE_ABOVE", onlyIfExists: true);
                if (wmState == IntPtr.Zero || skipTaskbar == IntPtr.Zero || above == IntPtr.Zero)
                {
                    session = default;
                    return false;
                }

                session = new X11Session(_display, wmState, skipTaskbar, above);
                _established = session;
                return true;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XClientMessageEvent
    {
        public int type;
        public ulong serial;
        public int send_event;
        public IntPtr display;
        public IntPtr window;
        public IntPtr message_type;
        public int format;
        public long data0, data1, data2, data3, data4;
    }

    // No XCloseDisplay: the connection is the process's (see Session), so there is no close to declare. Its
    // absence is the point rather than an omission — a declaration kept "just in case" is one a future edit
    // reaches for, and closing the cached connection would leave every later window with no atoms to send.
    [DllImport("libX11", EntryPoint = "XOpenDisplay")] private static extern IntPtr XOpenDisplay(string? name);
    [DllImport("libX11", EntryPoint = "XInternAtom")] private static extern IntPtr XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [DllImport("libX11", EntryPoint = "XDefaultRootWindow")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11", EntryPoint = "XSendEvent")] private static extern int XSendEvent(IntPtr display, IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool propagate, long eventMask, IntPtr ev);
    [DllImport("libX11", EntryPoint = "XFlush")] private static extern int XFlush(IntPtr display);
}
