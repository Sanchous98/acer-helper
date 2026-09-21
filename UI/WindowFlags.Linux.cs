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
/// keeps this from depending on Avalonia's internals. A failure of any step is silent — the flags are a
/// preference, and a machine without these atoms (a non-EWMH window manager) must still get its window.
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
            var display = XOpenDisplay(null);
            if (display == IntPtr.Zero) return;
            try
            {
                var wmState = XInternAtom(display, "_NET_WM_STATE", onlyIfExists: false);
                var skipTaskbar = XInternAtom(display, "_NET_WM_STATE_SKIP_TASKBAR", onlyIfExists: false);
                var above = XInternAtom(display, "_NET_WM_STATE_ABOVE", onlyIfExists: false);
                if (wmState == IntPtr.Zero || skipTaskbar == IntPtr.Zero || above == IntPtr.Zero) return;

                var ev = new XClientMessageEvent
                {
                    type = ClientMessage,
                    window = xid,
                    message_type = wmState,
                    format = 32,
                    data0 = StateAdd,
                    data1 = (long)skipTaskbar,
                    data2 = (long)above,
                    data3 = SourceApplication,
                };
                var buf = Marshal.AllocHGlobal(EventBufferSize);
                try
                {
                    Marshal.StructureToPtr(ev, buf, fDeleteOld: false);
                    var root = XDefaultRootWindow(display);
                    if (root != IntPtr.Zero) XSendEvent(display, root, propagate: false, SubstructureRedirectNotify, buf);
                    XFlush(display);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { XCloseDisplay(display); }
        }
        catch { /* a window manager that does not want this must not stop the window from opening */ }
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

    [DllImport("libX11", EntryPoint = "XOpenDisplay")] private static extern IntPtr XOpenDisplay(string? name);
    [DllImport("libX11", EntryPoint = "XCloseDisplay")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11", EntryPoint = "XInternAtom")] private static extern IntPtr XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [DllImport("libX11", EntryPoint = "XDefaultRootWindow")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11", EntryPoint = "XSendEvent")] private static extern int XSendEvent(IntPtr display, IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool propagate, long eventMask, IntPtr ev);
    [DllImport("libX11", EntryPoint = "XFlush")] private static extern int XFlush(IntPtr display);
}
