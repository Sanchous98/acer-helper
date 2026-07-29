using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AcerHelper.Vendors.Generic;

/// <summary>
/// Windows transport for <b>PawnIO</b> (pawnio.eu) — the signed, sandboxed ring-0 gateway this app uses to reach
/// the AMD SMU. PawnIO is a kernel driver that executes small verified bytecode <i>modules</i>; a module declares
/// which hardware it may touch, so the driver grants narrow access instead of the blanket "read/write any port,
/// any MMIO" hole that WinRing0 and inpoutx64 are.
///
/// Why not the drivers already on this class of machine: WinRing0 (which RyzenAdj ships) is named in Microsoft's
/// vulnerable-driver blocklist and carries an active Defender signature, so it loads only while the blocklist and
/// HVCI happen to be off — a posture one Windows policy push can revoke. inpoutx64 cannot do the job at all: its
/// author documents <c>DlPortWritePortUlong</c> as not working as expected (and a 32-bit write to the PCI
/// CONFIG_ADDRESS port at 0xCF8 is exactly what an SMN transaction needs — PCI does not treat a byte/word write
/// there as an address-latch update, so it cannot be split), while <c>MapPhysToLin</c>/<c>GetPhysLong</c> are
/// documented as limited to physical addresses under 2 GB, which excludes any plausible MMCFG base.
///
/// Protocol: one device, two IOCTLs, both plain buffered.
///
///   LOAD_BINARY   the module blob; one loaded module per open handle, so a second module needs a second handle
///   EXECUTE_FN    call a function the module exports, by name
///
/// The execute buffer layout is a 32-byte ASCII function name (zero padded) followed by packed little-endian
/// 64-bit arguments; the output is packed 64-bit values. Sizes are validated STRICTLY by the module (PawnIO's
/// DEFINE_IOCTL_SIZED returns STATUS_INVALID_PARAMETER on any mismatch), so the caller must pass exactly the
/// argument and result counts the function declares. Everything is blittable — plain <c>byte[]</c> written with
/// <see cref="BitConverter"/>-free span helpers — so there is no marshalling stub and this stays Native-AOT-safe.
///
/// The driver and its module blobs ship SEPARATELY from the app (same arrangement as the LampArray driver, see
/// <see cref="LampArrayTransport"/>): PawnIO is installed by its own signed installer, and the module is a signed
/// binary only its author can produce. So this class never installs anything — it probes, and every consumer
/// treats "absent" as "feature unavailable" rather than as an error.
/// </summary>
internal sealed class PawnIo : IDisposable
{
    // ---- shared contract with the PawnIO driver (pawnio_um.h) ----
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
    // CTL_CODE(41394, 0x821, METHOD_BUFFERED, FILE_ANY_ACCESS) and (…, 0x841, …). Spelled as literals because the
    // device type is PawnIO's own (0xA1B2), not a Windows one: (0xA1B2 << 16) | (fn << 2). Both codes were verified
    // byte-for-byte against an installed PawnIO 2.2.0.0 — they appear in PawnIOLib.dll and adjacently in PawnIO.sys's
    // dispatch switch — so these are the driver's real numbers, not values copied out of a write-up.
    private const uint IoctlLoadBinary = 0xA1B22084;
    private const uint IoctlExecuteFn  = 0xA1B22104;
    private const int  NameField       = 32;   // fixed-size ASCII function-name field that prefixes the args

    // Serialises this handle's requests. A PawnIO handle carries one loaded module and the modules we use drive a
    // stateful hardware mailbox, so overlapping executes on one handle are never valid.
    private readonly Lock _gate = new();
    private readonly SafeFileHandle _device;

    public string? LastError { get; private set; }

    private PawnIo(SafeFileHandle device) => _device = device;

    /// <summary>Whether the PawnIO driver answers on this machine. Cheap (open + close) and never throws, so it
    /// can gate a feature's visibility. False also when the caller is not elevated — the device requires it.</summary>
    public static bool Available
    {
        get
        {
            var h = TryOpenDevice();
            if (h == null) return false;
            h.Dispose();
            return true;
        }
    }

    /// <summary>Open the driver and load <paramref name="module"/> (a signed PawnIO module blob) into this handle.
    /// Returns null when the driver is absent, the process is not elevated, or the module is rejected — the caller
    /// treats that as "feature unavailable". Never throws.</summary>
    public static PawnIo? TryLoad(byte[] module)
    {
        var h = TryOpenDevice();
        if (h == null) return null;

        if (!Ioctl(h, IoctlLoadBinary, module, null, out _))
        {
            h.Dispose();
            return null;   // unsigned/corrupt blob, or a module built for a different driver edition
        }
        return new PawnIo(h);
    }

    /// <summary>Call a module function. <paramref name="input"/> and <paramref name="output"/> must be exactly the
    /// lengths the function declares — PawnIO validates the buffer sizes and fails the call otherwise. Returns
    /// false and sets <see cref="LastError"/> on failure.</summary>
    public bool Execute(string function, ReadOnlySpan<ulong> input, Span<ulong> output)
    {
        lock (_gate)
        {
            LastError = null;

            // The handle can be closed under us — Dispose runs on the app's teardown path while a background
            // re-assert may still be mid-transaction — so it is checked here and the call itself is guarded. Every
            // consumer of this class relies on the never-throws contract the sibling ports keep.
            if (_device.IsClosed || _device.IsInvalid) { LastError = "the PawnIO handle is closed"; return false; }

            var inBuf = new byte[NameField + input.Length * sizeof(ulong)];
            // ASCII, zero padded, and never terminated by us: the field is fixed width and the module compares the
            // whole 32 bytes. A name longer than the field is a programming error, not a runtime condition.
            var written = Encoding.ASCII.GetBytes(function, 0, Math.Min(function.Length, NameField), inBuf, 0);
            if (written != function.Length) { LastError = $"function name '{function}' exceeds {NameField} bytes"; return false; }
            for (var i = 0; i < input.Length; i++)
                WriteUInt64(inBuf, NameField + i * sizeof(ulong), input[i]);

            var outBuf = output.Length > 0 ? new byte[output.Length * sizeof(ulong)] : null;
            bool ok;
            int err;
            try { ok = Ioctl(_device, IoctlExecuteFn, inBuf, outBuf, out err); }
            catch (Exception ex) { LastError = $"{function} failed ({ex.GetType().Name})"; return false; }
            if (!ok) { LastError = $"{function} failed (win32 {err})"; return false; }

            for (var i = 0; i < output.Length; i++)
                output[i] = ReadUInt64(outBuf!, i * sizeof(ulong));
            return true;
        }
    }

    // Under _gate so the handle can never close mid-transaction.
    public void Dispose()
    {
        lock (_gate) _device.Dispose();
    }

    // ---- win32 ----

    private static SafeFileHandle? TryOpenDevice()
    {
        const uint genericRead = 0x80000000, genericWrite = 0x40000000;
        const uint shareRead = 1, shareWrite = 2, openExisting = 3;
        try
        {
            var h = CreateFile(DevicePath, genericRead | genericWrite, shareRead | shareWrite,
                               IntPtr.Zero, openExisting, 0, IntPtr.Zero);
            if (!h.IsInvalid) return h;
            h.Dispose();
            return null;   // ERROR_FILE_NOT_FOUND = not installed; ERROR_ACCESS_DENIED = not elevated
        }
        catch { return null; }
    }

    private static bool Ioctl(SafeFileHandle h, uint code, byte[]? input, byte[]? output, out int error)
    {
        var ok = DeviceIoControl(h, code, input, (uint)(input?.Length ?? 0), output, (uint)(output?.Length ?? 0),
                                 out _, IntPtr.Zero);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    // Little-endian 64-bit accessors over the packed argument/result buffers. Hand-rolled rather than via
    // BinaryPrimitives so the offsets read the same way as the protocol comment above.
    private static void WriteUInt64(byte[] b, int offset, ulong v)
    {
        for (var i = 0; i < 8; i++) b[offset + i] = (byte)(v >> (i * 8));
    }

    private static ulong ReadUInt64(byte[] b, int offset)
    {
        ulong v = 0;
        for (var i = 0; i < 8; i++) v |= (ulong)b[offset + i] << (i * 8);
        return v;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
                                                    uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        byte[]? input, uint inputSize, byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);
}
