using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Spike.WinUI;

/// <summary>
/// The flyout shape the real app needs, on WinUI 3: borderless, acrylic, always-on-top, kept out of the taskbar
/// and the Alt-Tab switcher, positioned at a screen corner, and hidden when it loses focus. This is the part the
/// spike exists for — WinUI 3 gives the controls, but the window behaviour is still Win32 and this proves it is
/// reachable without a full Win32 shell.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_TOOLWINDOW = 0x00000080;

    public MainWindow()
    {
        InitializeComponent();

        // 1. Acrylic. WinUI 3 owns the backdrop; no DWM interop needed for the material itself.
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;

        // 2. Borderless + always-on-top, through the AppWindow presenter (no raw SetWindowLong for this half).
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        // 3. Out of the taskbar and Alt-Tab: still Win32 (WS_EX_TOOLWINDOW), as it would be with any toolkit.
        var hwnd = WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

        // 4. Anchor to the bottom-right corner, the way a tray flyout sits.
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Move(new PointInt32(area.X + area.Width - AppWindow.Size.Width - 16,
                                      area.Y + area.Height - AppWindow.Size.Height - 16));

        // 5. Light-dismiss: hide as soon as focus leaves.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) AppWindow.Hide();
        };
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}
