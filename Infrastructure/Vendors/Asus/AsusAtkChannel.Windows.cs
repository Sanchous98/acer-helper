using System.Runtime.InteropServices;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// THE WINDOWS TRANSPORT: the only thing in the ASUS Windows backend that touches the machine. `\\.\ATKACPI` is
// opened and one `DeviceIoControl` per call is made; the buffers and decoders are pure and live in AsusAtk.cs.
//
// THE PROTOCOL AND ITS SOURCE: G-Helper `app/AsusACPI.cs` — `CreateFile("\\.\ATKACPI")`, IOCTL `0x0022240C`, an
// 8-byte call header ([method u32 LE][args length u32 LE]) followed by the args, and a 16-byte output. The
// device never appears on a machine without the ASUS ATK ACPI driver, so opening it fails there and the whole
// Windows ASUS backend stays absent (the generic Windows ports stand).
//
// UNVERIFIED ON HARDWARE: no ASUS laptop was available, so nothing in this file has been executed.

/// <summary>The open `\\.\ATKACPI` handle. Owned by the device (Device.Own) so it lives as long as the ports
/// built over its control delegate.</summary>
internal sealed class AsusAtkChannel : IDisposable
{
    private const string DevicePath = @"\\.\ATKACPI";

    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint ShareRead = 1, ShareWrite = 2;
    private const uint OpenExisting = 3, AttributeNormal = 0x80;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private IntPtr _handle;

    private AsusAtkChannel(IntPtr handle) => _handle = handle;

    /// <summary>Open the ATK device, or null when this is not an ASUS machine / the driver is absent.</summary>
    internal static AsusAtkChannel? TryOpen()
    {
        var handle = CreateFileW(DevicePath, GenericRead | GenericWrite, ShareRead | ShareWrite,
                                 IntPtr.Zero, OpenExisting, AttributeNormal, IntPtr.Zero);
        return handle == IntPtr.Zero || handle == new IntPtr(-1) ? null : new AsusAtkChannel(handle);
    }

    /// <summary>One `DeviceIoControl`; the 16-byte output is returned whether or not the call succeeded (the
    /// decoders read zeroes / the failure value as "absent"). The delegate shape AsusAtkDevice takes.</summary>
    internal byte[] Control(byte[] input)
    {
        var output = new byte[16];
        DeviceIoControl(_handle, AsusAtk.ControlCode, input, (uint)input.Length,
                        output, (uint)output.Length, out _, IntPtr.Zero);
        return output;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
    }
}
