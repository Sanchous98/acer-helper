using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>
/// THE TWO WINDOW FLAGS THIS APP SETS IN XAML DO NOT REACH A LIVE X11 WINDOW ON THEIR OWN.
///
/// Measured on the owner's session (KWin, XWayland) with the flyout OPEN and viewable:
/// <c>xprop _NET_WM_STATE</c> returned <c>_NET_WM_STATE_FOCUSED</c> and nothing else — no
/// <c>_NET_WM_STATE_SKIP_TASKBAR</c> and no <c>_NET_WM_STATE_ABOVE</c>, although MainWindow.axaml sets
/// <c>ShowInTaskbar="False"</c> and <c>Topmost="True"</c>. The machinery is there — Avalonia 12.1.2 has
/// <c>Avalonia.Platform.IWindowImpl.ShowTaskbarIcon</c> and <c>Avalonia.X11.X11Window.ShowTaskbarIcon</c>, and the
/// backend knows the atom — so what is missing is the moment: a styled property set from XAML is applied before
/// the window has a platform handle, and the X11 backend does not re-apply it afterwards.
///
/// WHAT THAT COST, in the owner's words: the flyout appeared in the taskbar, so it could be minimised like an
/// ordinary window ("приложение можно свернуть при нажатии на иконку в панели задач"), and from there it could not
/// be brought back — the app hides the flyout when it loses focus (the tray rule), so the taskbar entry
/// disappeared with it and the only way back was the tray menu's own item. The owner's requirement is the tray
/// behaviour this file restores: "всегда должно сворачиваться в трей".
///
/// WHY RE-ASSERTING IS THE WHOLE FIX: the property is already correct in XAML and in the platform-agnostic
/// <see cref="Window"/>; what it needs is one assignment made while the handle EXISTS, which is what
/// <see cref="Apply"/> is. It is called from each window's <c>Opened</c> — after the handle is created and before
/// the first frame is placed — and it is deliberately harmless on Windows, where the XAML values were already
/// reaching the live window: both setters are no-ops when the value is unchanged.
///
/// REMOVE THIS WHEN: a window opened with <c>ShowInTaskbar="False"</c> and <c>Topmost="True"</c> reports both
/// <c>_NET_WM_STATE_SKIP_TASKBAR</c> and <c>_NET_WM_STATE_ABOVE</c> in <c>xprop</c> without this call — that is
/// the whole check, and it needs no reading of Avalonia's source.
/// </summary>
internal static class WindowFlags
{
    /// <summary>Re-assert the two flags on a window whose handle now exists. Called from <c>Opened</c>.</summary>
    internal static void Apply(Window window)
    {
        window.ShowInTaskbar = false;   // the flyout is a tray panel: never a taskbar entry, never minimisable
        window.Topmost = true;          // it is a panel over other windows, as it is on Windows
    }
}
