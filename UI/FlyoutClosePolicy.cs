using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>
/// WHY A CLOSE REQUEST IS REFUSED, AND WHEN IT MUST NOT BE. The flyout is a tray app's window: closing it with
/// the window manager (X, Alt+F4) hides it and leaves the app running in the tray, which is why
/// <see cref="MainWindow"/> cancels that close and hides instead.
///
/// THAT RULE MUST NOT REACH THE SESSION MANAGER. Avalonia's X11 backend is an XSMP client (measured on the
/// owner's machine: the process holds an ICE connection to ksmserver and loads libICE), so KDE's logout asks
/// the app to close its windows and reads a cancelled window close as the application REFUSING to end the
/// session — which is exactly what the owner saw: the desktop reported "session end cancelled by an unnamed
/// application" and the machine could only be switched off after quitting Acer Helper by hand. The same
/// request arrives on Windows as WM_QUERYENDSESSION, and a cancellation there is the classic way to stall a
/// Windows shutdown, so this policy is deliberately cross-platform rather than Linux-only.
///
/// The decision is a pure function of the reason Avalonia reports, so every branch is asserted in
/// <c>FlyoutClosePolicyTests</c> without a window, a platform or a session. The values are Avalonia 12.1.2's
/// <see cref="WindowCloseReason"/>: Undefined=0, WindowClosing=1, OwnerWindowClosing=2, ApplicationShutdown=3,
/// OSShutdown=4 — read out of the installed assembly, not from memory.
/// </summary>
internal static class FlyoutClosePolicy
{
    /// <summary>Hide instead of closing? Only for a close the WINDOW initiated while the app is staying up.
    ///
    /// <paramref name="destroying"/> is the app tearing its own UI down (a live language switch) — that close is
    /// ours and must go through. <see cref="WindowCloseReason.WindowClosing"/> is the window manager's close:
    /// hide, the tray rule. <see cref="WindowCloseReason.Undefined"/> carries no information, and hiding is the
    /// behaviour this window has always had, so an unlabelled close keeps it.
    ///
    /// The other three are not the window's decision: <see cref="WindowCloseReason.ApplicationShutdown"/> is the
    /// app's own lifetime shutting down, <see cref="WindowCloseReason.OSShutdown"/> is the desktop going away
    /// (KDE's logout, Windows' WM_QUERYENDSESSION), and <see cref="WindowCloseReason.OwnerWindowClosing"/> is our
    /// owner going — in all three the app is on its way out, and refusing would hold the session open.
    /// </summary>
    internal static bool HideInsteadOfClose(bool destroying, WindowCloseReason reason)
        => !destroying
           && reason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined;
}
