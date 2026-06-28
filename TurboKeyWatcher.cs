using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AcerHelper;

/// <summary>
/// Watches the Acer Turbo key via RawInput. On the Acer Nitro the Turbo key is an
/// HID report on usagePage 0x0088 / usage 0x01 (report id 0x04, "04 85 FF").
/// RawInput is shared, so this works alongside Acer's service and needs no driver.
/// </summary>
public sealed class TurboKeyWatcher : IDisposable
{
    private const int    WM_INPUT          = 0x00FF;
    private const uint   RID_INPUT         = 0x10000003;
    private const uint   RIDEV_INPUTSINK   = 0x00000100;
    private const ushort TURBO_USAGE_PAGE  = 0x0088;
    private const ushort TURBO_USAGE       = 0x01;
    private const byte   TURBO_REPORT_ID   = 0x04;
    private const byte   TURBO_KEY_CODE     = 0x85;   // data byte that marks a Turbo press

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usagePage; public ushort usage; public uint flags; public IntPtr target; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint num, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdrSize);

    private readonly Action _onTurbo;
    private readonly MsgWindow _window;

    public bool Registered { get; }

    public TurboKeyWatcher(Action onTurbo)
    {
        _onTurbo = onTurbo;
        _window  = new MsgWindow(OnRawInput);

        var rid = new RAWINPUTDEVICE[]
        {
            new() { usagePage = TURBO_USAGE_PAGE, usage = TURBO_USAGE, flags = RIDEV_INPUTSINK, target = _window.Handle }
        };
        Registered = RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
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
            if (Marshal.ReadInt32(buf, 0) != 2) return;   // RIM_TYPEHID

            int off     = (int)hdr;
            int sizeHid = Marshal.ReadInt32(buf, off);
            int count   = Marshal.ReadInt32(buf, off + 4);
            if (sizeHid < 2 || count < 1) return;

            int dataOff = off + 8;
            byte b0 = Marshal.ReadByte(buf, dataOff);
            byte b1 = Marshal.ReadByte(buf, dataOff + 1);

            if (b0 == TURBO_REPORT_ID && b1 == TURBO_KEY_CODE)
                _onTurbo();   // runs on the UI thread (window created there)
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
            CreateHandle(new CreateParams { Caption = "AcerHelperTurbo", Parent = new IntPtr(-3) }); // HWND_MESSAGE
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT) _onInput(m.LParam);
            base.WndProc(ref m);
        }
    }
}
