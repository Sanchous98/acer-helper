using System.Runtime.InteropServices;

namespace AcerHelper;

// Windows lid-state hook: RegisterPowerSettingNotification(GUID_LIDSWITCH_STATE_CHANGE) delivers a
// WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE to the registered window whenever the lid opens or closes — and it
// fires even when the lid-close power action is "do nothing" (clamshell keep-awake), which is exactly the case
// we care about. This power-setting change is delivered *targeted* to the registered HWND (not via the legacy
// top-level power broadcast), so it would very likely reach a message-only (HWND_MESSAGE) window too — but
// Windows only documents/guarantees WM_POWERBROADCAST delivery to top-level windows and doesn't document
// targeted delivery to message-only ones, so this defensively creates an ordinary top-level window that is
// never shown (a top-level window receives both broadcast and targeted notifications — a strict superset of a
// message-only window; WS_EX_TOOLWINDOW keeps it out of the taskbar / Alt-Tab). Created on the UI thread so
// Avalonia's Win32 message loop dispatches its messages. See LidWatcher.cs.
internal sealed partial class LidWatcher
{
    private const int  WM_POWERBROADCAST      = 0x0218;
    private const int  PBT_POWERSETTINGCHANGE = 0x8013;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
    private const uint WS_EX_TOOLWINDOW       = 0x00000080;

    // GUID_LIDSWITCH_STATE_CHANGE — its notification Data is a DWORD: 0 = lid closed, 1 = lid open.
    private static Guid LidSwitchStateChange = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize; public uint style; public WndProcDelegate lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    // Classic DllImport (not LibraryImport): WNDCLASSEX carries a delegate + string fields the LibraryImport
    // source generator can't marshal (SYSLIB1051). Runtime/AOT marshalling handles it.
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSetting, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    private WndProcDelegate? _proc;   // keep alive for the lifetime of the window
    private IntPtr _hwnd;
    private IntPtr _notify;

    private partial void Subscribe()
    {
        _proc = WndProc;
        const string cls = "AcerHelperLidWnd";
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _proc,
            hInstance = GetModuleHandleW(null),
            lpszClassName = cls,
        };
        RegisterClassExW(ref wc);   // ignore "already registered"
        _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, cls, "AcerHelperLid", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;

        _notify = RegisterPowerSettingNotification(_hwnd, ref LidSwitchStateChange, DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    private partial void Unsubscribe()
    {
        if (_notify != IntPtr.Zero) { UnregisterPowerSettingNotification(_notify); _notify = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_POWERBROADCAST && (int)wParam == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING: GUID PowerSetting (16 bytes) + DWORD DataLength (4) + UCHAR Data[]. For the
            // lid switch Data is a DWORD at offset 20: 0 = closed, 1 = open.
            var setting = Marshal.PtrToStructure<Guid>(lParam);
            if (setting == LidSwitchStateChange)
                _onLidChanged(Marshal.ReadInt32(lParam, 20) != 0);
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}
