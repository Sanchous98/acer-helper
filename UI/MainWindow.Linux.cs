using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace AcerHelper.UI;

// Linux corner rounding — the counterpart to the Win32 WinUICompositionBackdropCornerRadius.
//
// The ROUNDED CORNERS themselves are compositor-independent: Avalonia draws a rounded content card (the Root
// border) on a transparent window, so on ANY ARGB compositor (KWin, Mutter, Xfwm, picom, sway…) the corner
// pixels are transparent and the card reads as rounded. The only compositor-specific artifact is an extra
// rectangular BLUR painted behind the whole window — and blur is handled very differently per compositor:
//   • KWin (KDE) is the ONLY one that (a) blurs on the app's request (TransparencyLevelHint) and (b) blurs
//     the full window RECT, leaving square blur behind the rounded card. It's also the only X11 compositor
//     with an app-controllable blur-region standard, so we fix it here: set _KDE_NET_WM_BLUR_BEHIND_REGION
//     to a rounded-rect region matching the card. (Interned onlyIfExists, so on non-KWin this whole thing
//     is a clean no-op.)
//   • Mutter/Xfwm/most WMs: no window blur at all -> nothing to round; the transparent card is already fine.
//   • picom / Hyprland: blur + rounding are the user's compositor config (corner-radius, blur rules), not
//     app-controllable via any property — our atom is simply ignored.
// So this is correct across compositors, not KWin-only tunnel vision: it fixes the one case that needs (and
// allows) an app fix, and degrades to the already-correct transparent-card rounding everywhere else. X11
// only (Wayland handle isn't an XID -> no-op; Avalonia has no Wayland blur anyway). This file compiles on
// Linux only (the *.Linux.cs csproj glob); the shared partial method is elided on Windows.
public partial class MainWindow
{
    private const int CardCornerRadiusDip = 8;   // = OverlayCornerRadius (App.axaml); the card's rounding
    private const nint XA_CARDINAL = 6;
    private const int PropModeReplace = 0;

    // Self-guarding by the handle descriptor (X11 windows are "XID"), so it's a safe no-op on Wayland/macOS
    // and needs no platform attribute — which would otherwise make the all-platforms call site (Open) warn.
    partial void ApplyRoundedBlur()
    {
        if (TryGetPlatformHandle() is not { } h || h.HandleDescriptor != "XID") return;   // not X11 -> no-op
        var xid = (nuint)(nint)h.Handle;

        var scale = RenderScaling;
        int w = (int)(Bounds.Width * scale), h2 = (int)(Bounds.Height * scale);
        int r = (int)(CardCornerRadiusDip * scale);
        if (w <= 0 || h2 <= 0) return;

        var region = RoundedRectRegion(w, h2, r);

        var display = XOpenDisplay(0);
        if (display == 0) return;
        try
        {
            // onlyIfExists=1: interns nothing on a compositor that never registered it (non-KWin) -> None -> skip.
            var atom = XInternAtom(display, "_KDE_NET_WM_BLUR_BEHIND_REGION", 1);
            if (atom == 0) return;
            XChangeProperty(display, xid, atom, XA_CARDINAL, 32, PropModeReplace, region, region.Length);
            XFlush(display);
        }
        finally { XCloseDisplay(display); }
    }

    // A rounded rectangle expressed as X11 blur-region rectangles [x,y,w,h, …]: the two corner bands are one
    // 1px-tall strip per row (inset to the quarter-circle), with a single full-width rectangle in between.
    private static long[] RoundedRectRegion(int w, int h, int r)
    {
        r = Math.Clamp(r, 0, Math.Min(w, h) / 2);
        var rects = new List<long>((8 * r) + 4);

        for (var y = 0; y < r; y++) AddRow(rects, w, y, Inset(r, r - y));      // top corners
        rects.Add(0); rects.Add(r); rects.Add(w); rects.Add(h - 2 * r);        // straight middle
        for (var y = h - r; y < h; y++) AddRow(rects, w, y, Inset(r, y - (h - r) + 1));  // bottom corners

        return [.. rects];

        static int Inset(int radius, int dist)
            => radius - (int)Math.Round(Math.Sqrt(Math.Max(0, radius * (double)radius - dist * (double)dist)));

        static void AddRow(List<long> acc, int width, int y, int inset)
        {
            acc.Add(inset); acc.Add(y); acc.Add(width - 2 * inset); acc.Add(1);
        }
    }

    [LibraryImport("libX11.so.6")]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport("libX11.so.6")]
    private static partial int XChangeProperty(nint display, nuint window, nint property, nint type,
                                               int format, int mode, long[] data, int nelements);

    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(nint display);
}
