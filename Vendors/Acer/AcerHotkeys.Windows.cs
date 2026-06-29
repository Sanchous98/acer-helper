using System.Runtime.InteropServices;

namespace AcerHelper.Vendors.Acer;

/// <summary>
/// Acer special keys on Windows via RawInput, mapped to generic <see cref="HotkeyAction"/>s.
/// Both the OS capture (message-only window) and the Acer marker decoding live here — no separate
/// platform layer. Turbo = HID report 04 85 (usage page 0x0088); Nitro = keyboard scancode E0 75.
/// Must be constructed on the UI thread so the Avalonia message loop dispatches WM_INPUT.
/// </summary>
public sealed class AcerHotkeys : IHotkeys
{
    private const int  WM_INPUT        = 0x00FF;
    private const uint RID_INPUT       = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const int  RIM_TYPEKEYBOARD = 1;
    private const int  RIM_TYPEHID      = 2;

    private const byte   TURBO_B0 = 0x04, TURBO_B1 = 0x85;
    private const ushort NITRO_MAKECODE = 0x75;
    private const ushort RI_KEY_BREAK   = 0x01;
    private const ushort RI_KEY_E0      = 0x02;

    private static readonly (ushort page, ushort usage)[] Listen =
    {
        (0x0001, 0x06),   // keyboard (Nitro key scancode)
        (0x0088, 0x01),   // Acer vendor page (Turbo)
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usagePage; public ushort usage; public uint flags; public IntPtr target; }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize; public uint style; public WndProcDelegate lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint num, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdrSize);

    private readonly WndProcDelegate _proc;   // keep alive
    private readonly IntPtr _hwnd;

    public event Action<HotkeyAction>? Pressed;

    public AcerHotkeys()
    {
        _proc = WndProc;
        const string cls = "AcerHelperHotkeyWnd";
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _proc,
            hInstance = GetModuleHandleW(null),
            lpszClassName = cls,
        };
        RegisterClassExW(ref wc);   // ignore "already registered"
        _hwnd = CreateWindowExW(0, cls, "AcerHelperHotkeys", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero); // HWND_MESSAGE
        if (_hwnd == IntPtr.Zero) return;

        var rid = new RAWINPUTDEVICE[Listen.Length];
        for (int i = 0; i < Listen.Length; i++)
            rid[i] = new RAWINPUTDEVICE { usagePage = Listen[i].page, usage = Listen[i].usage, flags = RIDEV_INPUTSINK, target = _hwnd };
        RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT) OnRawInput(lParam);
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnRawInput(IntPtr lParam)
    {
        uint hdr = (uint)(8 + 2 * IntPtr.Size);   // sizeof(RAWINPUTHEADER)
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, hdr);
        if (size == 0) return;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, hdr) == unchecked((uint)-1)) return;
            int type = Marshal.ReadInt32(buf, 0);
            int off  = (int)hdr;

            if (type == RIM_TYPEKEYBOARD)
            {
                ushort make  = (ushort)Marshal.ReadInt16(buf, off + 0);
                ushort flags = (ushort)Marshal.ReadInt16(buf, off + 2);
                if (make == NITRO_MAKECODE && (flags & RI_KEY_E0) != 0 && (flags & RI_KEY_BREAK) == 0)
                    Pressed?.Invoke(HotkeyAction.ToggleWindow);
                return;
            }

            if (type != RIM_TYPEHID) return;

            int sizeHid = Marshal.ReadInt32(buf, off);
            int count   = Marshal.ReadInt32(buf, off + 4);
            if (sizeHid < 1 || count < 1) return;

            int dataOff = off + 8;
            byte b0 = Marshal.ReadByte(buf, dataOff);
            byte b1 = sizeHid > 1 ? Marshal.ReadByte(buf, dataOff + 1) : (byte)0;
            if (b0 == TURBO_B0 && b1 == TURBO_B1) Pressed?.Invoke(HotkeyAction.TogglePerformance);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }
}
