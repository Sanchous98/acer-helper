using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>
/// The Windows half of <see cref="WindowFlags"/>. On Windows the XAML values DO reach the live window — the
/// tooltip behaviour and the taskbar entry are correct there — so this half only re-states the intent, which is
/// what makes the call sites identical on both OSes: a window that asks not to be in the taskbar and to stay
/// above other windows says so once, in <c>Opened</c>, and each OS reaches that state its own way.
///
/// The re-assertion is a no-op when the XAML value already took effect, and it is the repair if a future toolkit
/// version starts applying these properties only at creation time.
/// </summary>
internal static partial class WindowFlags
{
    private static void ApplyCore(Window window)
    {
        window.ShowInTaskbar = false;   // the flyout is a tray panel: never a taskbar entry, never minimisable
        window.Topmost = true;          // it is a panel over other windows, as it is on Windows
    }
}
