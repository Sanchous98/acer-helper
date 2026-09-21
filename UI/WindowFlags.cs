using Avalonia.Controls;
using Avalonia.Threading;

namespace AcerHelper.UI;

/// <summary>
/// THE TWO WINDOW FLAGS THIS APP SETS IN XAML DO NOT REACH A LIVE X11 WINDOW ON THEIR OWN, AND SETTING THEM
/// THROUGH AVALONIA AFTERWARDS TAKES THE WINDOW OFF THE SCREEN.
///
/// Measured on the owner's session (KWin, XWayland), flyout open and viewable: <c>xprop _NET_WM_STATE</c>
/// returned <c>_NET_WM_STATE_FOCUSED</c> and nothing else, although MainWindow.axaml declares
/// <c>ShowInTaskbar="False"</c> and <c>Topmost="True"</c>. That is what the owner saw as "приложение можно
/// свернуть при нажатии на иконку в панели задач, а вот развернуть потом будет уже нельзя": the flyout took a
/// taskbar entry, could be minimised there, and the app then hid it on the focus change (the tray rule), so the
/// entry disappeared with it. The requirement is "всегда должно сворачиваться в трей".
///
/// WHY NOT JUST RE-ASSERT <see cref="Window.ShowInTaskbar"/>/<see cref="Window.Topmost"/> ONCE THE HANDLE
/// EXISTS — the first attempt, measured and rejected: with both re-asserted from <c>Opened</c>, the window
/// stopped being mapped AT ALL. The properties were applied (<c>xprop</c> then showed
/// <c>HIDDEN, FOCUSED, ABOVE, STAYS_ON_TOP, SKIP_TASKBAR</c>) but the window was unmapped within the first
/// 200 ms of every open, while the app itself still believed it was open — so every following tray click took
/// the "hide" branch and the flyout could not be brought back at all. The setter is the wrong lever on X11.
///
/// SO THE STATE IS ASKED FOR DIRECTLY, in the way the window manager expects: an EWMH <c>_NET_WM_STATE</c>
/// client message to the root window, which touches no Avalonia state and no window mapping. Each OS half does
/// that its own way (<c>WindowFlags.Linux.cs</c> / <c>WindowFlags.Windows.cs</c>) — the split is by filename, so
/// this file holds the INTENT the two share and the call sites stay identical.
///
/// REMOVE THIS WHEN: a window opened with <c>ShowInTaskbar="False"</c> and <c>Topmost="True"</c> reports both
/// <c>_NET_WM_STATE_SKIP_TASKBAR</c> and <c>_NET_WM_STATE_ABOVE</c> in <c>xprop</c> without this call — that is
/// the whole check, and it needs no reading of Avalonia's source.
/// </summary>
internal static partial class WindowFlags
{
    /// <summary>Called from each window's <c>Opened</c> — after the handle exists, which is when the request can
    /// name a real window. It is sent TWICE on purpose: once here, and once on the next dispatcher turn, because
    /// a request that arrives before the window manager has finished mapping the window is ignored by some
    /// window managers (and re-asking for a state that is already set costs nothing).
    /// </summary>
    internal static void Apply(Window window)
    {
        ApplyCore(window);
        Dispatcher.UIThread.Post(() => ApplyCore(window), DispatcherPriority.Background);
    }
}
