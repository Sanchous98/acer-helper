using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AcerHelper;

/// <summary>Which Acer special key was pressed.</summary>
public enum AcerHotkey { Turbo, Nitro }

/// <summary>
/// Single input source for Acer's special keys via RawInput. It only detects which
/// key was pressed and raises <see cref="Pressed"/>; it does NOT decide what each
/// key does — the application wires actions to the event.
///
/// The Turbo key is an HID report on usagePage 0x0088 / usage 0x01 (report id 0x04,
/// "04 85 FF"). The Nitro key (the one next to NumLock that normally launches
/// NitroSense) is an ordinary keyboard key with the extended scancode E0 75 and no
/// virtual-key (VKey 0xFF) — so it is detected from RawInput keyboard data, not a
/// VK hook. RawInput is shared (RIDEV_INPUTSINK), so this works alongside Acer's
/// service and needs no driver.
/// </summary>
public sealed class AcerHotkeyWatcher : IDisposable
{
    private const int  WM_INPUT        = 0x00FF;
    private const uint RID_INPUT       = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;

    private const int RIM_TYPEKEYBOARD = 1;
    private const int RIM_TYPEHID      = 2;

    // Turbo: HID report on 0x0088 (report id 0x04, marker byte 0x85).
    private const byte TURBO_B0 = 0x04, TURBO_B1 = 0x85;

    // Nitro: keyboard key next to NumLock -> extended scancode E0 75 (key-down).
    private const ushort NITRO_MAKECODE = 0x75;
    private const ushort RI_KEY_BREAK   = 0x01;   // 0 = key down, 1 = key up
    private const ushort RI_KEY_E0      = 0x02;   // extended scancode

    // Usage pages we listen on (page, usage).
    private static readonly (ushort page, ushort usage)[] Listen =
    {
        (0x0001, 0x06),   // Generic Desktop / Keyboard (Nitro key is a scancode here)
        (0x0088, 0x01),   // Acer vendor (Turbo)
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usagePage; public ushort usage; public uint flags; public IntPtr target; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint num, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdrSize);

    private readonly MsgWindow _window;

    /// <summary>Raised (on the UI thread) when a recognised Acer key is pressed.</summary>
    public event Action<AcerHotkey>? Pressed;

    public bool Registered { get; }

    public AcerHotkeyWatcher()
    {
        _window = new MsgWindow(OnRawInput);

        var rid = new RAWINPUTDEVICE[Listen.Length];
        for (int i = 0; i < Listen.Length; i++)
            rid[i] = new RAWINPUTDEVICE { usagePage = Listen[i].page, usage = Listen[i].usage, flags = RIDEV_INPUTSINK, target = _window.Handle };

        Registered = RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
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

            if (type == RIM_TYPEKEYBOARD)        // RAWKEYBOARD: MakeCode, Flags, Reserved, VKey, ...
            {
                ushort make  = (ushort)Marshal.ReadInt16(buf, off + 0);
                ushort flags = (ushort)Marshal.ReadInt16(buf, off + 2);
                if (make == NITRO_MAKECODE && (flags & RI_KEY_E0) != 0 && (flags & RI_KEY_BREAK) == 0)
                    Pressed?.Invoke(AcerHotkey.Nitro);
                return;
            }

            if (type != RIM_TYPEHID) return;

            int sizeHid = Marshal.ReadInt32(buf, off);
            int count   = Marshal.ReadInt32(buf, off + 4);
            if (sizeHid < 1 || count < 1) return;

            int dataOff = off + 8;
            byte b0 = Marshal.ReadByte(buf, dataOff);
            byte b1 = sizeHid > 1 ? Marshal.ReadByte(buf, dataOff + 1) : (byte)0;

            if (b0 == TURBO_B0 && b1 == TURBO_B1) Pressed?.Invoke(AcerHotkey.Turbo);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose() => _window.DestroyHandle();

    /// <summary>Message-only window that forwards WM_INPUT.</summary>
    private sealed class MsgWindow : NativeWindow
    {
        private readonly Action<IntPtr> _onInput;

        public MsgWindow(Action<IntPtr> onInput)
        {
            _onInput = onInput;
            CreateHandle(new CreateParams { Caption = "AcerHelperHotkeys", Parent = new IntPtr(-3) }); // HWND_MESSAGE
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT) _onInput(m.LParam);
            base.WndProc(ref m);
        }
    }
}
