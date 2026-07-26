using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>
/// Windows transport for the LampArray bridge: the channel to <c>AcerHelperLampArray.sys</c> (driver/), the
/// VHF-based virtual HID device that makes this laptop's backlight visible to Windows Dynamic Lighting.
///
/// Why a driver at all: Windows enumerates lighting devices ONLY as HID LampArray collections — there is no
/// user-mode API to register one — so a HID source driver has to exist. It is deliberately dumb: it owns the
/// static HID report descriptor, answers the host's attribute GETs from a layout blob we push down, and hands
/// back completed lamp frames. All the semantics (geometry, rate limiting, ownership) stay in C#, so the
/// signed binary almost never has to change.
///
/// Protocol (kept byte-for-byte in sync with driver/AcerHelperLampArray/public.h — if you change one, change
/// both): three IOCTLs, plain buffered, fixed-size structs written here by hand so there is no struct-layout
/// marshalling to get wrong.
///
///   SET_LAYOUT  app -> driver   the lamp table; also what makes the virtual device appear
///   WAIT_FRAME  driver -> app   blocks until the host completes a frame; last-one-wins (never a backlog)
///   STOP        app -> driver   un-publish the device
///
/// Two handles, on purpose: a synchronous file object serialises its requests, so a STOP issued while
/// WAIT_FRAME is pending would queue BEHIND it and deadlock until the host happened to send a frame. Control
/// and frame traffic therefore use separate handles (the driver tears the device down when the last one
/// closes, so an app crash can't leave a zombie entry in Dynamic Lighting either).
/// </summary>
internal sealed class LampArrayTransport : ILampArrayTransport
{
    // ---- shared contract with driver/AcerHelperLampArray/public.h ----
    private const string DevicePath = @"\\.\AcerHelperLampArray";
    private const string DriverService = "AcerHelperLampArray";
    private const string HardwareId = "AcerHelperLampArray";
    private const int MaxLamps = 64;             // AHLA_MAX_LAMPS
    private const uint LayoutVersion = 1;        // AHLA_LAYOUT_VERSION
    private const int LampSize = 28;             // sizeof(AHLA_LAMP)
    private const int LayoutSize = 28 + MaxLamps * LampSize;   // sizeof(AHLA_LAYOUT)
    private const int FrameSize = 12 + MaxLamps * 4;           // sizeof(AHLA_FRAME)

    private const uint DeviceType = 0xB007;      // AHLA_DEVICE_TYPE (vendor range)
    private const uint FileReadAccess = 1, FileWriteAccess = 2;
    private static uint Ctl(uint function, uint access) => (DeviceType << 16) | (access << 14) | (function << 2);
    private static readonly uint IoctlSetLayout = Ctl(0x800, FileWriteAccess);
    private static readonly uint IoctlWaitFrame = Ctl(0x801, FileReadAccess);
    private static readonly uint IoctlStop      = Ctl(0x802, FileWriteAccess);

    private readonly Lock _gate = new();
    private SafeFileHandle? _control;   // SET_LAYOUT / STOP
    private SafeFileHandle? _frames;    // WAIT_FRAME (blocking; cancelled by Stop)
    private IntPtr _swDevice;           // the software device node WE created (IntPtr.Zero if it pre-existed)
    private int _lampCount;
    private volatile bool _stopped;

    public string? LastError { get; private set; }

    /// <summary>Whether the driver package is staged on this machine. The whole feature (and its Options row) is
    /// hidden until it is: the driver ships separately from the app because it needs a signature Windows will
    /// load without test-signing (see docs/lamparray.md), so most installs won't have it.
    ///
    /// Checked by looking for the package in the driver store — that is what <c>pnputil /add-driver</c> creates,
    /// and it is true before any device node exists (the app creates the node itself, so there is no device to
    /// interrogate yet). The System32\drivers copy is accepted too, for a package built with DIRID 12.</summary>
    public static bool DriverInstalled
    {
        get
        {
            try
            {
                if (File.Exists(Path.Combine(Environment.SystemDirectory, "drivers", DriverService + ".sys")))
                    return true;
                var repo = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
                return Directory.Exists(repo) &&
                       Directory.EnumerateDirectories(repo, DriverService + ".inf_*").Any();
            }
            catch { return false; }   // not elevated / no such path -> treat as absent
        }
    }

    public bool Start(LampArrayLayout layout)
    {
        lock (_gate)
        {
            LastError = null;
            _stopped = false;

            if (layout.LampCount is < 1 or > MaxLamps)
            {
                LastError = $"lamp count {layout.LampCount} out of range (1..{MaxLamps})";
                return false;
            }
            if (_control != null) Close();   // re-Start with a new layout: tear the old device down first

            if (!Open()) return false;

            var blob = Serialize(layout);
            if (!Ioctl(_control!, IoctlSetLayout, blob, null, out var err))
            {
                LastError = $"SET_LAYOUT failed (win32 {err})";
                Close();
                return false;
            }

            _lampCount = layout.LampCount;
            return true;
        }
    }

    public void Stop()
    {
        // Order matters. CancelIoEx is not itself an I/O request, so it does NOT queue behind the pending
        // WAIT_FRAME on the frame handle — it unblocks it. Only then is STOP safe to send.
        _stopped = true;
        var frames = _frames;
        if (frames is { IsInvalid: false }) CancelIoEx(frames, IntPtr.Zero);

        lock (_gate)
        {
            if (_control is { IsInvalid: false } c) Ioctl(c, IoctlStop, null, null, out _);
            Close();
        }
    }

    public bool WaitFrame(out LampFrame frame)
    {
        frame = default;
        var h = _frames;
        if (_stopped || h is null || h.IsInvalid) return false;

        var buf = new byte[FrameSize];
        if (!Ioctl(h, IoctlWaitFrame, null, buf, out var err))
        {
            // ERROR_OPERATION_ABORTED (995) is our own Stop() cancelling the wait — not a failure.
            if (!_stopped && err != 995) LastError = $"WAIT_FRAME failed (win32 {err})";
            return false;
        }

        var seq = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0));
        var autonomous = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4)) != 0;
        var count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8)), (uint)_lampCount);

        var colors = new LampColor[_lampCount];
        for (var i = 0; i < count; i++)
        {
            var o = 12 + i * 4;
            colors[i] = new LampColor(buf[o], buf[o + 1], buf[o + 2], buf[o + 3]);
        }
        frame = new LampFrame(seq, autonomous, colors);
        return true;
    }

    // ---- device node + handles ----

    // Open the driver's control device, creating the software device node first if nothing answers yet.
    // Caller holds _gate.
    private bool Open()
    {
        _control = TryOpenDevice();
        if (_control == null)
        {
            if (!DriverInstalled)
            {
                LastError = "LampArray driver is not installed";
                return false;
            }
            if (!CreateDeviceNode()) return false;

            // PnP has to start the device and the driver has to create its symbolic link — both happen after
            // SwDeviceCreate's callback, so poll briefly rather than assume.
            for (var i = 0; i < 25 && _control == null; i++)
            {
                Thread.Sleep(200);
                _control = TryOpenDevice();
            }
            if (_control == null)
            {
                LastError = "device node created but the driver did not start (check Device Manager)";
                DestroyDeviceNode();
                return false;
            }
        }

        _frames = TryOpenDevice();
        if (_frames != null) return true;

        LastError = "could not open the frame channel";
        Close();
        return false;
    }

    private static SafeFileHandle? TryOpenDevice()
    {
        const uint genericRead = 0x80000000, genericWrite = 0x40000000;
        const uint shareRead = 1, shareWrite = 2, openExisting = 3;
        var h = CreateFile(DevicePath, genericRead | genericWrite, shareRead | shareWrite,
                           IntPtr.Zero, openExisting, 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    // Caller holds _gate.
    private void Close()
    {
        _frames?.Dispose(); _frames = null;
        _control?.Dispose(); _control = null;
        DestroyDeviceNode();
        _lampCount = 0;
    }

    // Create the root-parented software device node the driver binds to. This is what makes the virtual
    // LampArray exist ONLY while the app wants it: the node (and with it the Dynamic Lighting entry) is
    // created here and destroyed in Close(). The alternative — a permanently installed node via devgen/devcon
    // — would leave a dead lighting device listed whenever the app isn't running (see docs/lamparray.md).
    private unsafe bool CreateDeviceNode()
    {
        var instanceId = Marshal.StringToHGlobalUni("AcerHelperLampArray");
        var hardwareIds = MultiSz(HardwareId);
        var description = Marshal.StringToHGlobalUni("Acer Helper keyboard lighting");
        try
        {
            var info = new SwDeviceCreateInfo
            {
                cbSize = (uint)sizeof(SwDeviceCreateInfo),
                pszInstanceId = instanceId,
                pszzHardwareIds = hardwareIds,
                pszzCompatibleIds = IntPtr.Zero,
                pContainerId = IntPtr.Zero,
                // Removable so PnP is happy to see it come and go; SilentInstall + NoDisplayInUI keep it out of
                // the user's face (it is plumbing, not a device they plugged in); DriverRequired tells PnP to
                // actually match our INF instead of leaving a raw devnode.
                CapabilityFlags = SwDeviceCapabilities.Removable | SwDeviceCapabilities.SilentInstall |
                                  SwDeviceCapabilities.NoDisplayInUI | SwDeviceCapabilities.DriverRequired,
                pszDeviceDescription = description,
                pszDeviceLocation = IntPtr.Zero,
                pSecurityDescriptor = IntPtr.Zero,
            };

            _createResult = int.MinValue;   // sentinel: callback not seen yet
            var hr = SwDeviceCreate("AcerHelper", "HTREE\\ROOT\\0", in info, 0, IntPtr.Zero,
                                    &CreateCallback, IntPtr.Zero, out var handle);
            if (hr < 0)
            {
                LastError = $"SwDeviceCreate failed (0x{hr:X8})";
                return false;
            }

            // The create result arrives on the callback; wait briefly for it.
            for (var i = 0; i < 50 && _createResult == int.MinValue; i++) Thread.Sleep(100);
            if (_createResult < 0)
            {
                SwDeviceClose(handle);
                LastError = $"software device creation failed (0x{_createResult:X8})";
                return false;
            }

            _swDevice = handle;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(instanceId);
            Marshal.FreeHGlobal(hardwareIds);
            Marshal.FreeHGlobal(description);
        }
    }

    private void DestroyDeviceNode()
    {
        if (_swDevice == IntPtr.Zero) return;
        try { SwDeviceClose(_swDevice); } catch { /* best-effort */ }
        _swDevice = IntPtr.Zero;
    }

    // ---- the layout blob (mirrors AHLA_LAYOUT) ----

    private static byte[] Serialize(LampArrayLayout layout)
    {
        var b = new byte[LayoutSize];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s[0..], LayoutVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)layout.LampCount);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)layout.WidthUm);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)layout.HeightUm);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)layout.DepthUm);
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], (uint)layout.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)layout.MinUpdateIntervalUs);

        for (var i = 0; i < layout.LampCount; i++)
        {
            var lamp = layout.Lamps[i];
            var o = 28 + i * LampSize;
            BinaryPrimitives.WriteUInt32LittleEndian(s[o..], (uint)lamp.XUm);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 4)..], (uint)lamp.YUm);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 8)..], (uint)lamp.ZUm);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 12)..], (uint)lamp.UpdateLatencyUs);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 16)..], (uint)lamp.Purposes);
            // 8-bit level counts: 255 is the most the HID field can express (LogicalMaximum(255)), and it is
            // what Microsoft's own reference device reports for 8-bit-per-channel LEDs. IntensityLevelCount 1
            // = "no independent gain", which tells the host to bake brightness into RGB (see LampColor.Rgb).
            b[o + 20] = 255; b[o + 21] = 255; b[o + 22] = 255; b[o + 23] = 1;
            b[o + 24] = lamp.IsProgrammable ? (byte)1 : (byte)0;
            b[o + 25] = lamp.InputBinding;
            // b[o + 26..27]: reserved padding, must stay zero.
        }
        return b;
    }

    // ---- win32 ----

    private static bool Ioctl(SafeFileHandle h, uint code, byte[]? input, byte[]? output, out int error)
    {
        var ok = DeviceIoControl(h, code, input, (uint)(input?.Length ?? 0), output, (uint)(output?.Length ?? 0),
                                 out _, IntPtr.Zero);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    // MULTI_SZ (double-NUL-terminated) for a single id, as SwDeviceCreate wants for pszzHardwareIds.
    private static IntPtr MultiSz(string value)
    {
        var chars = value.Length + 2;
        var p = Marshal.AllocHGlobal(chars * 2);
        for (var i = 0; i < value.Length; i++) Marshal.WriteInt16(p, i * 2, value[i]);
        Marshal.WriteInt16(p, value.Length * 2, 0);
        Marshal.WriteInt16(p, (value.Length + 1) * 2, 0);
        return p;
    }

    private static class SwDeviceCapabilities
    {
        public const uint Removable = 0x01, SilentInstall = 0x02, NoDisplayInUI = 0x04, DriverRequired = 0x08;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SwDeviceCreateInfo
    {
        public uint cbSize;
        public IntPtr pszInstanceId;
        public IntPtr pszzHardwareIds;
        public IntPtr pszzCompatibleIds;
        public IntPtr pContainerId;
        public uint CapabilityFlags;
        public IntPtr pszDeviceDescription;
        public IntPtr pszDeviceLocation;
        public IntPtr pSecurityDescriptor;
    }

    // SwDeviceCreate reports the outcome through a callback rather than its return value. There is at most one
    // node per process, so a static slot is enough — and it keeps the callback [UnmanagedCallersOnly], which is
    // what makes it safe under Native AOT (no delegate marshalling stub involved).
    private static volatile int _createResult;

    [UnmanagedCallersOnly]
    private static void CreateCallback(IntPtr swDevice, int createResult, IntPtr context, IntPtr instanceId)
        => _createResult = createResult;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int SwDeviceCreate(string enumeratorName, string parentDeviceInstance,
        in SwDeviceCreateInfo createInfo, uint propertyCount, IntPtr properties,
        delegate* unmanaged<IntPtr, int, IntPtr, IntPtr, void> callback, IntPtr context, out IntPtr swDevice);

    [DllImport("cfgmgr32.dll")]
    private static extern void SwDeviceClose(IntPtr swDevice);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
                                                    uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        byte[]? input, uint inputSize, byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle device, IntPtr overlapped);

    public void Dispose() => Stop();
}

/// <summary>Composition helper: the LampArray transport for this OS, or null where there is none. Windows
/// returns the driver channel — but only once the driver package is actually installed, so the feature (and
/// its Options row) stays invisible otherwise.</summary>
internal static class LampArrayHost
{
    public static ILampArrayTransport? Create()
        => LampArrayTransport.DriverInstalled ? new LampArrayTransport() : null;
}
